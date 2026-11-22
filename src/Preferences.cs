using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace BalanceWatcher
{
    internal sealed class Preferences
    {
        public bool Pinned { get; set; }
        public bool QianwenEnabled { get; set; }
        public bool QianwenDirect { get; set; }
        public bool Alerts { get; set; }
        public int Minutes { get; set; }
        public decimal YuanLimit { get; set; }
        public decimal DollarLimit { get; set; }
        public double? Left { get; set; }
        public double? Top { get; set; }
        public string ProtectedKey { get; set; }

        public static readonly int[] Intervals = { 1, 3, 5, 15 };
        public Preferences() { Pinned = true; Alerts = true; Minutes = 1; YuanLimit = 10; DollarLimit = 2; }
        public Preferences Copy() { return (Preferences)MemberwiseClone(); }
    }

    internal sealed class SettingsStore
    {
        public static readonly string AppFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BalanceWatcher");
        private readonly string path;
        public Preferences Value { get; private set; }
        public string LoadMessage { get; private set; }

        public SettingsStore(string directory)
        {
            path = Path.Combine(directory, "settings.json");
            Value = new Preferences();
            try
            {
                if (!File.Exists(path)) return;
                var saved = new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(path));
                if (saved == null) throw new InvalidDataException();
                if (!Preferences.Intervals.Contains(saved.Minutes)) saved.Minutes = 1;
                if (saved.YuanLimit < 0) saved.YuanLimit = 10;
                if (saved.DollarLimit < 0) saved.DollarLimit = 2;
                Value = saved;
            }
            catch (Exception)
            {
                LoadMessage = "无法读取上次的设置。请在设置中重新连接账户。";
            }
        }

        public void Update(Action<Preferences> change)
        {
            var next = Value.Copy();
            change(next);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(next), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                Value = next;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal static string Protect(string key)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        public string ReadKey()
        {
            try
            {
                byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(Value.ProtectedKey), null, DataProtectionScope.CurrentUser);
                try { return Encoding.UTF8.GetString(bytes); }
                finally { Array.Clear(bytes, 0, bytes.Length); }
            }
            catch (Exception) { throw new WatchError("无法解密此 Windows 用户的密钥，请重新填写。", true); }
        }
    }

    internal static class Startup
    {
        private const string PathName = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "LLMBalanceWatcher";
        private static string Command { get { return "\"" + Assembly.GetExecutingAssembly().Location + "\""; } }
        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(PathName))
                return key != null && string.Equals(key.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
        public static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(PathName))
            {
                if (enabled) key.SetValue(Name, Command);
                else key.DeleteValue(Name, false);
            }
        }
    }
}
