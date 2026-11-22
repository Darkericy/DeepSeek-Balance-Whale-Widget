using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace BalanceWatcher
{
    internal static class Ui
    {
        public static T Load<T>(string name) where T : class
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name + ".xaml"))
                return (T)XamlReader.Load(stream);
        }
        public static T Part<T>(this FrameworkElement view, string name) where T : FrameworkElement { return (T)view.FindName(name); }
        public static Window LoadWidget()
        {
            var window = Load<Window>("Widget");
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("tide.png"))
            {
                var mascot = BitmapFrame.Create(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                mascot.Freeze();
                window.Part<Image>("MascotImage").Source = mascot;
            }
            return window;
        }
        public static SolidColorBrush Color(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }
        public static Visibility Visible(bool value) { return value ? Visibility.Visible : Visibility.Collapsed; }
        public static void Open(Uri uri) { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        public static void Explain(Window owner, string message) { MessageBox.Show(owner, message, "余额看板", MessageBoxButton.OK, MessageBoxImage.Information); }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Bounds { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Bounds rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public static Bounds BoundsOf(Window window)
        {
            Bounds rect;
            GetWindowRect(new WindowInteropHelper(window).Handle, out rect);
            return rect;
        }
        // Use physical screen coordinates for both storage and clamping, including mixed-DPI monitors.
        public static void Position(Window window, double? left, double? top, bool topRight)
        {
            var rect = BoundsOf(window);
            int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
            bool saved = left.HasValue && top.HasValue && left >= int.MinValue && left <= int.MaxValue && top >= int.MinValue && top <= int.MaxValue;
            var area = saved ? Forms.Screen.FromPoint(new System.Drawing.Point((int)left.Value, (int)top.Value)).WorkingArea :
                topRight ? Forms.Screen.PrimaryScreen.WorkingArea : Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).WorkingArea;
            int x = saved ? (int)left.Value : topRight ? area.Right - w - 14 : rect.Left;
            int y = saved ? (int)top.Value : topRight ? area.Top + 14 : rect.Top;
            x = Math.Max(area.Left, Math.Min(x, area.Right - w));
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - h));
            SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, x, y, 0, 0, 0x15);
        }

        public static void DockWidget(Window window, bool snap)
        {
            var handle = new WindowInteropHelper(window).Handle;
            var area = Forms.Screen.FromHandle(handle).WorkingArea;
            var rect = BoundsOf(window);
            var source = PresentationSource.FromVisual(window);
            double scale = source.CompositionTarget.TransformToDevice.M11;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            bool onLeft = (rect.Left - area.Left) + width / 2 < area.Width / 2;
            if (snap)
            {
                int x = Math.Max(area.Left, Math.Min(rect.Left, area.Right - width));
                int y = Math.Max(area.Top, Math.Min(rect.Top, area.Bottom - height));
                // Use the current screen's work area so the mascot stays clear of the taskbar.
                int range = (int)Math.Round(24 * scale);
                if (x - area.Left <= range) x = area.Left;
                else if (area.Right - x - width <= range) x = area.Right - width;
                if (y - area.Top <= range) y = area.Top;
                else if (area.Bottom - y - height <= range) y = area.Bottom - height;
                SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x15);
            }
            FaceInward(window, onLeft);
        }

        public static void FaceInward(Window window, bool onLeft)
        {
            DockPanel.SetDock(window.Part<Grid>("MascotHit"), onLeft ? Dock.Left : Dock.Right);
            window.Part<Border>("Bubble").Margin = onLeft ? new Thickness(12, 0, 0, 0) : new Thickness(0, 0, 12, 0);
            var tail = window.Part<System.Windows.Shapes.Path>("Tail");
            tail.HorizontalAlignment = onLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            tail.RenderTransform = new ScaleTransform(onLeft ? -1 : 1, 1);
            window.Part<Grid>("MascotFace").RenderTransform = new ScaleTransform(onLeft ? -1 : 1, 1);
        }
    }

    internal sealed class CardView
    {
        public readonly Border Root = Ui.Load<Border>("AccountCard");
        private readonly Account account;
        public CardView(Account account, Action connect)
        {
            this.account = account;
            Root.Part<TextBlock>("Provider").Text = account.Name;
            var mark = Root.Part<System.Windows.Shapes.Path>("Mark");
            mark.Data = (Geometry)Application.Current.FindResource(account.IsDeep ? "DeepMark" : "QMark");
            mark.Stroke = Ui.Color(account.IsDeep ? "#326F8A" : "#71519A");
            Root.Part<Border>("Badge").Background = Ui.Color(account.IsDeep ? "#DFF1F5" : "#EEE6FB");
            Root.Part<Button>("Connect").Click += (s, e) => connect();
        }

        public void Render(Preferences prefs, bool details = false)
        {
            var a = account;
            bool stale = a.Error != null && a.Last != null;
            bool low = a.Last != null && prefs.Alerts && a.Last.BelowThreshold(prefs).Any();
            string status = !a.Enabled ? "未连接" : a.Busy ? "更新中" : a.NeedsAccount ? "需重新连接" :
                a.Error != null ? "更新失败" : a.Last == null ? "等待更新" : !a.Last.Available ? "账户不可用" : low ? "余额偏低" : "已更新";
            var color = Ui.Color(!a.Enabled ? "#737080" : a.Busy ? "#366C92" : a.Error != null ? "#A14332" :
                (low || (a.Last != null && !a.Last.Available)) ? "#935716" : "#31746A");
            Root.Part<Ellipse>("Dot").Fill = color;
            Root.Part<TextBlock>("Status").Text = status;
            Root.Part<TextBlock>("Status").Foreground = color;

            var amounts = Root.Part<StackPanel>("Amounts");
            var breakdown = Root.Part<StackPanel>("Breakdown");
            amounts.Children.Clear();
            breakdown.Children.Clear();
            if (a.Last != null) foreach (var item in a.Last.Amounts)
            {
                string value = item.Format(item.Total);
                var figure = new TextBlock { Text = value, FontFamily = new FontFamily("Segoe UI"), FontSize = 22,
                    FontWeight = FontWeights.SemiBold, Foreground = Ui.Color(stale ? "#777280" : "#312D50") };
                Typography.SetNumeralAlignment(figure, FontNumeralAlignment.Tabular);
                amounts.Children.Add(new Viewbox { Child = figure, StretchDirection = StretchDirection.DownOnly,
                    HorizontalAlignment = HorizontalAlignment.Right, ToolTip = item.Currency + "  " + value });
                if (details && item.Recharge.HasValue && item.Grant.HasValue)
                    breakdown.Children.Add(new TextBlock { Text = "充值 " + item.Format(item.Recharge.Value) + "    赠金 " + item.Format(item.Grant.Value),
                        Style = (Style)Application.Current.FindResource("Hint"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });
            }
            if (amounts.Children.Count == 0 && a.Enabled && !a.NeedsAccount)
                amounts.Children.Add(new TextBlock { Text = "—", FontSize = 19, HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = Ui.Color("#898594") });
            var button = Root.Part<Button>("Connect");
            button.Visibility = Ui.Visible(!a.Enabled || a.NeedsAccount);
            button.Content = a.IsDeep ? (a.NeedsAccount ? "更换密钥" : "填写密钥") : (a.NeedsAccount ? "重新授权" : "网页登录");
            var message = Root.Part<TextBlock>("Message");
            message.Text = a.Error ?? (a.Last != null && a.Last.Amounts.Count == 0 ? "平台未返回分币种金额" : "");
            message.Foreground = Ui.Color(a.Error != null ? "#994333" : "#69677A");
            message.Visibility = Ui.Visible(message.Text.Length > 0);
            var stamp = Root.Part<TextBlock>("Stamp");
            stamp.Visibility = Ui.Visible(a.Last != null && (details || stale));
            if (a.Last != null)
            {
                stamp.Text = (stale ? "上次成功  " : "更新于  ") + a.Last.Updated.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                stamp.ToolTip = a.Last.Updated.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }
    }

    internal static class LoginDialog
    {
        public static bool Open(Window owner, QianwenClient client, bool direct)
        {
            Window view = Ui.Load<Window>("Login");
            view.Owner = owner;
            var label = view.Part<TextBlock>("Progress");
            var reopen = view.Part<Button>("Reopen");
            var dismiss = view.Part<Button>("Dismiss");
            var cancel = new CancellationTokenSource();
            Uri page = null;
            Action open = () =>
            {
                try { Ui.Open(page); label.Text = "请在浏览器中登录并同意授权。完成后，看板会自动读取余额。"; }
                catch (Exception) { label.Text = "无法启动默认浏览器。请检查 Windows 的默认浏览器设置，再点“打开授权页”。"; }
            };
            reopen.Click += (s, e) => open();
            EventHandler closed = (s, e) => cancel.Cancel();
            view.Closed += closed;
            view.ContentRendered += async (s, e) =>
            {
                Ui.Position(view, null, null, false);
                label.Text = "正在连接千问，准备官方授权页面…";
                try
                {
                    var step = await client.Login(false, direct, cancel.Token);
                    if (cancel.IsCancellationRequested) return;
                    if (!step.Done)
                    {
                        page = step.Page;
                        reopen.Visibility = Visibility.Visible;
                        open();
                        await client.Login(true, direct, cancel.Token);
                    }
                    if (!cancel.IsCancellationRequested) view.DialogResult = true;
                }
                catch (Exception issue)
                {
                    if (cancel.IsCancellationRequested) return;
                    label.Text = issue is WatchError ? issue.Message : "无法连接千问，请检查网络和本地组件后重试。";
                    reopen.Visibility = Visibility.Collapsed;
                    dismiss.Content = "关闭";
                }
                finally { view.Closed -= closed; cancel.Dispose(); }
            };
            return view.ShowDialog() == true;
        }
    }

    internal sealed class OptionsDialog
    {
        private readonly Board board;
        private readonly Window view = Ui.Load<Window>("Settings");
        private bool hadStartup;
        public OptionsDialog(Board board) { this.board = board; }
        public void Show(Window owner, bool focusKey)
        {
            var prefs = board.Store.Value;
            view.Owner = owner;
            view.MaxHeight = Math.Max(280, SystemParameters.WorkArea.Height - 20);
            var keyField = view.Part<PasswordBox>("ApiKey");
            var remove = view.Part<CheckBox>("RemoveKey");
            bool hasKey = !string.IsNullOrEmpty(prefs.ProtectedKey);
            view.Part<TextBlock>("KeyHint").Text = hasKey ? "密钥已加密保存。要更换，请输入新密钥。" : "输入平台的 API Key，由当前 Windows 用户加密保存。";
            remove.Visibility = Ui.Visible(hasKey);
            remove.Checked += (s, e) => { keyField.Clear(); keyField.IsEnabled = false; };
            remove.Unchecked += (s, e) => keyField.IsEnabled = true;
            view.Part<Button>("GetKey").Click += (s, e) => {
                try { Ui.Open(new Uri("https://platform.deepseek.com/api_keys")); }
                catch (Exception) { Feedback("无法打开默认浏览器。"); }
            };
            UpdateQ();
            var direct = view.Part<CheckBox>("QianwenDirect");
            direct.IsChecked = prefs.QianwenDirect;
            view.Part<Button>("Login").Click += (s, e) => { if (board.Login(view, direct.IsChecked == true)) UpdateQ(); };
            var interval = view.Part<ComboBox>("Interval");
            foreach (int minutes in Preferences.Intervals) interval.Items.Add(new ComboBoxItem { Content = minutes + " 分钟", Tag = minutes });
            interval.SelectedIndex = Array.IndexOf(Preferences.Intervals, prefs.Minutes);
            var autostart = view.Part<CheckBox>("AutoStart");
            try { hadStartup = Startup.IsEnabled(); autostart.IsChecked = hadStartup; }
            catch (Exception) { autostart.IsEnabled = false; autostart.ToolTip = "Windows 未允许读取启动设置"; }
            var alerts = view.Part<CheckBox>("Alerts");
            var limits = view.Part<FrameworkElement>("Limits");
            alerts.IsChecked = prefs.Alerts;
            limits.IsEnabled = prefs.Alerts;
            alerts.Checked += (s, e) => limits.IsEnabled = true;
            alerts.Unchecked += (s, e) => limits.IsEnabled = false;
            view.Part<TextBox>("Yuan").Text = prefs.YuanLimit.ToString(CultureInfo.InvariantCulture);
            view.Part<TextBox>("Dollar").Text = prefs.DollarLimit.ToString(CultureInfo.InvariantCulture);
            view.Part<Button>("Save").Click += (s, e) => Save();
            view.ContentRendered += (s, e) => { Ui.Position(view, null, null, false); if (focusKey) keyField.Focus(); };
            view.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) view.Close(); };
            view.ShowDialog();
            keyField.Clear();
        }
        private void UpdateQ()
        {
            bool enabled = board.Qianwen.Enabled;
            view.Part<Button>("Login").Content = enabled ? "验证授权" : "网页登录";
            view.Part<TextBlock>("QHint").Text = enabled ? (board.Qianwen.NeedsAccount ? "登录已失效，请重新授权。" : "已启用账户余额监控。") : "通过千问官网授权读取账单余额。";
            view.Part<CheckBox>("DisableQ").Visibility = Ui.Visible(enabled);
            view.Part<CheckBox>("DisableQ").IsChecked = false;
        }
        private void Feedback(string message) { view.Part<TextBlock>("Feedback").Text = message; }

        private void Save()
        {
            var original = board.Store.Value;
            decimal yuan = original.YuanLimit, dollar = original.DollarLimit;
            bool alerts = view.Part<CheckBox>("Alerts").IsChecked == true;
            if (alerts && (!Limit("Yuan", out yuan) || !Limit("Dollar", out dollar))) { Feedback("提醒线请输入大于或等于零的金额。"); return; }
            var raw = view.Part<PasswordBox>("ApiKey").Password.Trim();
            if (raw.Any(c => c < 33 || c > 126)) { Feedback("请填写完整的 API Key，检查空格和换行。"); return; }
            var autoStart = view.Part<CheckBox>("AutoStart");
            bool start = autoStart.IsChecked == true;
            bool changeStart = autoStart.IsEnabled && start != hadStartup;
            bool changedStart = false;
            try
            {
                var key = view.Part<CheckBox>("RemoveKey").IsChecked == true ? null : raw.Length > 0 ? SettingsStore.Protect(raw) : original.ProtectedKey;
                if (changeStart) { Startup.Set(start); changedStart = true; }
                board.Store.Update(p => {
                    p.ProtectedKey = key;
                    p.QianwenDirect = view.Part<CheckBox>("QianwenDirect").IsChecked == true;
                    if (view.Part<CheckBox>("DisableQ").IsChecked == true) p.QianwenEnabled = false;
                    p.Minutes = (int)((ComboBoxItem)view.Part<ComboBox>("Interval").SelectedItem).Tag;
                    p.Alerts = alerts; p.YuanLimit = yuan; p.DollarLimit = dollar;
                });
            }
            catch (Exception)
            {
                if (changedStart) { try { Startup.Set(hadStartup); } catch (Exception) { hadStartup = start; } }
                Feedback("保存失败，请检查当前用户的设置目录或启动项权限。");
                return;
            }
            board.Apply(original);
            view.DialogResult = true;
        }
        private bool Limit(string name, out decimal value)
        {
            return decimal.TryParse(view.Part<TextBox>(name).Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value >= 0;
        }
    }
}
