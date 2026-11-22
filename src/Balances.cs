using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BalanceWatcher
{
    internal sealed class WatchError : Exception
    {
        public readonly bool NeedsAccount;
        public WatchError(string message, bool needsAccount = false) : base(message) { NeedsAccount = needsAccount; }
    }

    internal sealed class Amount
    {
        public string Currency;
        public decimal Total;
        public decimal? Recharge, Grant;

        public string Format(decimal value)
        {
            string prefix = Currency == "CNY" ? "¥" : Currency == "USD" ? "$" : Currency + " ";
            return prefix + value.ToString("#,0.00##########################", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class Reading
    {
        public readonly List<Amount> Amounts = new List<Amount>();
        public bool Available = true;
        public readonly DateTime Updated = DateTime.Now;

        public IEnumerable<Amount> BelowThreshold(Preferences prefs)
        {
            return Amounts.Where(a => (a.Currency == "CNY" && a.Total <= prefs.YuanLimit) ||
                                      (a.Currency == "USD" && a.Total <= prefs.DollarLimit));
        }
    }

    internal static class BalanceJson
    {
        internal static Dictionary<string, object> Object(string raw)
        {
            try
            {
                var obj = new JavaScriptSerializer().DeserializeObject(raw) as Dictionary<string, object>;
                if (obj != null) return obj;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            throw Invalid();
        }

        internal static object Get(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value : null;
        }

        internal static WatchError Invalid() { return new WatchError("平台返回的数据格式异常，请稍后刷新。"); }

        private static decimal Number(object raw)
        {
            decimal result;
            string text = Convert.ToString(raw, CultureInfo.InvariantCulture);
            if (text.Contains(",") && !Regex.IsMatch(text, @"^[+-]?\d{1,3}(,\d{3})+(\.\d+)?$")) throw Invalid();
            if (!decimal.TryParse(text,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out result)) throw Invalid();
            return result;
        }

        private static Amount ParseAmount(Dictionary<string, object> row, string totalKey)
        {
            var currency = Get(row, "currency") as string;
            if (currency == null || currency.Length != 3 || currency.Any(c => c < 'A' || c > 'Z')) throw Invalid();
            return new Amount { Currency = currency, Total = Number(Get(row, totalKey)) };
        }

        public static Reading DeepSeek(string json)
        {
            var root = Object(json);
            var flags = Get(root, "is_available");
            var entries = Get(root, "balance_infos") as object[];
            if (!(flags is bool) || entries == null) throw Invalid();
            var result = new Reading { Available = (bool)flags };
            foreach (var entry in entries)
            {
                var row = entry as Dictionary<string, object>;
                var amount = ParseAmount(row, "total_balance");
                amount.Recharge = Number(Get(row, "topped_up_balance"));
                amount.Grant = Number(Get(row, "granted_balance"));
                result.Amounts.Add(amount);
            }
            if (result.Available && result.Amounts.Count == 0) throw Invalid();
            return result;
        }

        public static Reading Qianwen(string json)
        {
            var result = new Reading();
            result.Amounts.Add(ParseAmount(Object(json), "availableAmount"));
            return result;
        }
    }

    // All state changes run on the UI dispatcher. Replacing a connection invalidates its in-flight result.
    internal sealed class Account
    {
        public readonly string Name;
        public readonly bool IsDeep;
        public bool Enabled, NeedsAccount;
        public string Error;
        public Reading Last;
        private CancellationTokenSource pending;
        private HashSet<string> previousWarnings = new HashSet<string>();
        public bool Busy { get { return pending != null; } }
        public event Action Changed = delegate { };
        public event Action Succeeded = delegate { };

        public Account(string name, bool deep) { Name = name; IsDeep = deep; }

        public void CancelPending()
        {
            var old = pending;
            pending = null;
            if (old != null) old.Cancel();
        }

        public void Reset(bool enabled)
        {
            CancelPending();
            Enabled = enabled;
            Error = null;
            NeedsAccount = false;
            Last = null;
            previousWarnings.Clear();
            Changed();
        }

        public async Task Refresh(Func<CancellationToken, Task<Reading>> fetch, bool manual)
        {
            if (!Enabled || Busy || (NeedsAccount && !manual)) return;
            var current = new CancellationTokenSource();
            pending = current;
            Changed();
            bool success = false;
            try
            {
                var result = await fetch(current.Token);
                if (current != pending) return;
                Last = result;
                Error = null;
                NeedsAccount = false;
                success = true;
            }
            catch (Exception e)
            {
                if (current != pending) return;
                var problem = e as WatchError;
                NeedsAccount = problem != null && problem.NeedsAccount;
                Error = problem == null ? "查询失败，请检查网络后刷新。" : problem.Message;
            }
            finally
            {
                if (current == pending)
                {
                    pending = null;
                    Changed();
                    if (success) Succeeded();
                }
                current.Dispose();
            }
        }

        public string NewWarning(Preferences prefs)
        {
            if (Last == null || Error != null) return null;
            var messages = new List<string>();
            var now = new HashSet<string>();
            if (prefs.Alerts)
            {
                if (!Last.Available)
                {
                    now.Add("unavailable");
                    if (!previousWarnings.Contains("unavailable")) messages.Add("账户已无可用额度");
                }
                foreach (var a in Last.BelowThreshold(prefs))
                {
                    now.Add(a.Currency);
                    if (!previousWarnings.Contains(a.Currency)) messages.Add("可用余额 " + a.Format(a.Total));
                }
            }
            previousWarnings = now;
            return messages.Count == 0 ? null : string.Join("，", messages);
        }
    }
}
