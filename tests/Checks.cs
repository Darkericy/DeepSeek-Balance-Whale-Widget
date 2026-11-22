using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace BalanceWatcher
{
    internal static class Checks
    {
        private const string DeepData = "{\"is_available\":true,\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"28.2501\",\"granted_balance\":\"3.25\",\"topped_up_balance\":\"25.0001\"},{\"currency\":\"USD\",\"total_balance\":\"1.00\",\"granted_balance\":\"0\",\"topped_up_balance\":\"1\"}]}";
        private static readonly string Folder = Path.GetFullPath(Path.Combine(".build", "checks"));
        private static int passed;
        private static void Check(bool result, string name)
        {
            if (!result) throw new Exception(name);
            passed++; Console.WriteLine("OK " + name);
        }
        private static void Reject(Action parse, string name)
        {
            try { parse(); } catch (WatchError) { Check(true, name); return; }
            throw new Exception(name);
        }
        [STAThread]
        public static int Main()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                CheckBalances();
                CheckProviders().GetAwaiter().GetResult();
                CheckAccounts().GetAwaiter().GetResult();
                CheckPreferences();
                CheckViews();
                Console.WriteLine("Passed " + passed + " checks."); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        private static void CheckBalances()
        {
            var reading = BalanceJson.DeepSeek(DeepData);
            Check(reading.Available && reading.Amounts.Count == 2 && reading.Amounts[1].Currency == "USD", "DeepSeek keeps each currency");
            Check(reading.Amounts[0].Format(reading.Amounts[0].Total) == "¥28.2501" && reading.Amounts[0].Recharge == 25.0001m && reading.Amounts[0].Grant == 3.25m, "DeepSeek totals, precision and split");
            Check(!BalanceJson.DeepSeek("{\"is_available\":false,\"balance_infos\":[]}").Available, "unavailable account with no buckets");
            Reject(() => BalanceJson.DeepSeek("{\"balance_infos\":[]}"), "missing availability is not zero");
            var q = BalanceJson.Qianwen("{\"availableAmount\":\"-0.0123\",\"currency\":\"CNY\"}");
            Check(q.Amounts[0].Total == -0.0123m, "Qianwen retains signed decimal");
            Check(BalanceJson.Qianwen("{\"availableAmount\":0,\"currency\":\"CNY\"}").Amounts[0].Total == 0, "reported zero balance");
            Reject(() => BalanceJson.Qianwen("{\"currency\":\"CNY\"}"), "missing Qianwen amount is not zero");
            Reject(() => BalanceJson.Qianwen("{\"availableAmount\":\"abc\",\"currency\":\"CNY\"}"), "reject invalid money");
            Check(BalanceJson.Qianwen("{\"availableAmount\":\"1,234.50\",\"currency\":\"CNY\"}").Amounts[0].Total == 1234.50m, "properly grouped balance");
            Reject(() => BalanceJson.Qianwen("{\"availableAmount\":\"1,23\",\"currency\":\"CNY\"}"), "malformed grouping never inflates the balance");
            Check(LoginStep.Parse("{\"events\":[{\"event\":\"device_code\",\"verification_url\":\"https://platform.qianwenai.com/auth\"}]}").Page.Scheme == "https", "official device flow authorization URL");
            Check(LoginStep.Parse("{\"events\":[{\"event\":\"already_authenticated\",\"authenticated\":true}]}").Done, "existing CLI session");
            Check(LoginStep.Parse("{\"events\":[{\"event\":\"success\",\"authenticated\":true}]}").Done, "successful browser authorization");
            Reject(() => LoginStep.Parse("{\"events\":[{\"event\":\"expired\",\"authenticated\":false}]}"), "expired browser authorization");
            Reject(() => LoginStep.Parse("{\"events\":[{\"event\":\"device_code\",\"verification_url\":\"file:///test\"}]}"), "reject non-HTTPS authorization URLs");
        }

        private sealed class TestHttp : HttpMessageHandler
        {
            public HttpStatusCode Status = HttpStatusCode.OK;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
            {
                if (request.Method != HttpMethod.Get || request.RequestUri.AbsoluteUri != "https://api.deepseek.com/user/balance" || request.Headers.Authorization.ToString() != "Bearer test-only")
                    throw new Exception("Unexpected HTTP request");
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(DeepData) });
            }
        }
        private static async Task CheckProviders()
        {
            var http = new TestHttp();
            var d = new DeepSeekClient(http);
            Check((await d.Read("test-only", CancellationToken.None)).Amounts.Count == 2, "authenticated read-only GET contract");
            http.Status = HttpStatusCode.Unauthorized;
            bool unauthorized = false;
            try { await d.Read("test-only", CancellationToken.None); } catch (WatchError e) { unauthorized = e.NeedsAccount; }
            Check(unauthorized, "DeepSeek 401 requests a new key");
            http.Status = (HttpStatusCode)429;
            bool retryable = false;
            try { await d.Read("test-only", CancellationToken.None); } catch (WatchError e) { retryable = !e.NeedsAccount; }
            Check(retryable, "DeepSeek 429 preserves credentials");
            Environment.SetEnvironmentVariable("QIANWEN_CONFIG_DIR", Path.Combine(Folder, "empty-cli-profile"));
            Environment.SetEnvironmentVariable("QIANWEN_KEYRING", "off");
            var q = new QianwenClient(Path.Combine(Folder, "components"));
            bool asksLogin = false;
            try { await q.Read(CancellationToken.None, true); } catch (WatchError e) { asksLogin = e.NeedsAccount; }
            Check(asksLogin, "embedded official CLI executes and returns its real logged-out error");
            var child = new ProcessStartInfo { UseShellExecute = false };
            const string localProxy = "http://127.0.0.1:10808";
            child.EnvironmentVariables["https_proxy"] = localProxy;
            child.EnvironmentVariables["http_proxy"] = localProxy;
            child.EnvironmentVariables["all_proxy"] = localProxy;
            child.EnvironmentVariables["no_proxy"] = "localhost";
            QianwenClient.ConfigureRoute(child, false);
            Check(child.EnvironmentVariables["HTTPS_PROXY"] == localProxy && child.EnvironmentVariables["NO_PROXY"] == "localhost", "existing proxy settings preserved in default mode");
            QianwenClient.ConfigureRoute(child, true);
            Check(child.EnvironmentVariables["https_proxy"] == null && child.EnvironmentVariables["http_proxy"] == null &&
                child.EnvironmentVariables["all_proxy"] == null && child.EnvironmentVariables["no_proxy"] == "*", "direct CLI ignores stale or case-varied proxy variables");
            Check(QianwenClient.CliError(3, "{}", true).Message != QianwenClient.CliError(3, "{}", false).Message, "connection error identifies the selected route");
        }

        private static async Task CheckAccounts()
        {
            var a = new Account("test", true);
            var normal = BalanceJson.DeepSeek(DeepData);
            a.Reset(true);
            var hold = new TaskCompletionSource<Reading>();
            var old = a.Refresh(t => hold.Task, true);
            int duplicate = 0;
            await a.Refresh(t => { duplicate++; return Task.FromResult(normal); }, true);
            Check(duplicate == 0 && a.Busy, "manual and timer refresh cannot overlap");
            a.Reset(false); hold.SetResult(normal); await old;
            Check(!a.Enabled && a.Last == null && !a.Busy, "late reply cannot restore a disconnected account");
            a.Reset(true); await a.Refresh(t => Task.FromResult(normal), false);
            var prefs = new Preferences();
            Check(a.NewWarning(prefs).Contains("$1.00") && a.NewWarning(prefs) == null, "currency-specific low balance fires once");
            await a.Refresh(t => { throw new WatchError("offline"); }, false);
            Check(a.Last == normal && a.Error == "offline" && a.NewWarning(prefs) == null, "failed fetch keeps last value and timestamp");
            await a.Refresh(t => Task.FromResult(BalanceJson.Qianwen("{\"availableAmount\":15,\"currency\":\"CNY\"}")), false);
            Check(a.Error == null && a.NewWarning(prefs) == null, "recovery clears stale state");
            await a.Refresh(t => Task.FromResult(normal), false);
            Check(a.NewWarning(prefs) != null, "new threshold crossing re-arms notification");
            var pendingRoute = new TaskCompletionSource<Reading>();
            var oldRoute = a.Refresh(t => pendingRoute.Task, true);
            a.CancelPending();
            Check(a.Last == normal, "switching routes preserves the last successful balance");
            await a.Refresh(t => Task.FromResult(BalanceJson.Qianwen("{\"availableAmount\":8,\"currency\":\"CNY\"}")), true);
            pendingRoute.SetException(new WatchError("old route failed"));
            await oldRoute;
            Check(a.Error == null && a.Last.Amounts[0].Total == 8, "late failure on old route cannot replace fresh data");
        }

        private static void CheckPreferences()
        {
            var dir = Path.Combine(Folder, "preferences");
            var store = new SettingsStore(dir);
            store.Update(p => { p.ProtectedKey = SettingsStore.Protect("test-only-key"); p.Minutes = 3; p.Left = -800; });
            var saved = new SettingsStore(dir);
            Check(saved.Value.Minutes == 3 && saved.Value.Left == -800 && saved.ReadKey() == "test-only-key", "preferences and per-user DPAPI round trip");
            Check(!File.ReadAllText(Path.Combine(dir, "settings.json")).Contains("test-only-key"), "no plaintext API key on disk");
            saved.Update(p => p.QianwenDirect = true);
            var afterRouteChange = new SettingsStore(dir);
            Check(afterRouteChange.Value.QianwenDirect && afterRouteChange.ReadKey() == "test-only-key" && afterRouteChange.Value.Minutes == 3, "saving route preserves other account settings");
            saved.Update(p => p.ProtectedKey = null);
            Check(string.IsNullOrEmpty(new SettingsStore(dir).Value.ProtectedKey), "disconnect removes stored DeepSeek secret");
        }

        private static void CheckViews()
        {
            var app = new Application();
            app.Resources.MergedDictionaries.Add(Ui.Load<ResourceDictionary>("Theme"));
            var view = Ui.LoadWidget();
            var deep = new Account("DeepSeek", true);
            var q = new Account("千问 AI", false);
            var card = new CardView(deep, delegate { });
            var qcard = new CardView(q, delegate { });
            var stack = view.Part<StackPanel>("Accounts");
            stack.Children.Add(card.Root); stack.Children.Add(qcard.Root);
            var prefs = new Preferences();
            Check(view.Part<Image>("MascotImage").Source is BitmapSource, "standalone mascot loads from the executable");
            Ui.FaceInward(view, true);
            var portraitToBoard = view.Part<Image>("MascotImage").TransformToAncestor((Visual)view.Content);
            Check(portraitToBoard.Transform(new Point(1, 0)).X > portraitToBoard.Transform(new Point(0, 0)).X,
                "left docking preserves the side of the dragon's knot ornament");
            Check(DockPanel.GetDock(view.Part<Grid>("MascotHit")) == Dock.Left &&
                DockPanel.GetDock(view.Part<Border>("BookSpine")) == Dock.Right &&
                view.Part<System.Windows.Shapes.Path>("Tail").HorizontalAlignment == HorizontalAlignment.Left, "bubble and mascot follow left docking");
            Ui.FaceInward(view, false);
            Check(DockPanel.GetDock(view.Part<Grid>("MascotHit")) == Dock.Right &&
                DockPanel.GetDock(view.Part<Border>("BookSpine")) == Dock.Left &&
                view.Part<System.Windows.Shapes.Path>("Tail").HorizontalAlignment == HorizontalAlignment.Right, "right docking restores the bubble layout");
            card.Render(prefs); qcard.Render(prefs);
            view.Part<TextBlock>("Cadence").Text = "每 1 分钟刷新";
            view.Part<Button>("Refresh").IsEnabled = false;
            Check(card.Root.Part<Button>("Connect").Visibility == Visibility.Visible, "setup offers an account connection");
            var content = (FrameworkElement)view.Content;
            view.Content = null;
            TextElement.SetFontFamily(content, view.FontFamily);
            double setupHeight = Render(content, view.Width, "widget-setup.png");
            deep.Reset(true); deep.Last = BalanceJson.DeepSeek(DeepData);
            q.Reset(true); q.Last = BalanceJson.Qianwen("{\"availableAmount\":\"30.58\",\"currency\":\"CNY\"}");
            card.Render(prefs); qcard.Render(prefs);
            Check(card.Root.Part<TextBlock>("Status").Text == "余额偏低" && card.Root.Part<StackPanel>("Amounts").Children.Count == 2 &&
                card.Root.Part<StackPanel>("Breakdown").Children.Count == 0, "compact bubble keeps both currencies and low-balance state visible");
            view.Part<Button>("Refresh").IsEnabled = true;
            Check(Render(content, view.Width, "widget-balances.png") > setupHeight, "widget grows to fit multi-currency results");
            card.Render(prefs, true); qcard.Render(prefs, true);
            Check(card.Root.Part<StackPanel>("Breakdown").Children.Count == 2 &&
                qcard.Root.Part<TextBlock>("Stamp").Visibility == Visibility.Visible, "expanded details include each split and last update");
            deep.Error = "查询超时，稍后会自动重试。"; card.Render(prefs);
            Check(card.Root.Part<TextBlock>("Stamp").Text.StartsWith("上次成功") && card.Root.Part<TextBlock>("Stamp").Visibility == Visibility.Visible &&
                card.Root.Part<TextBlock>("Status").Text == "更新失败", "compact stale UI identifies last successful reading");
            deep.Error = null;
            deep.Last = BalanceJson.DeepSeek("{\"is_available\":true,\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"128.46\",\"topped_up_balance\":\"120\",\"granted_balance\":\"8.46\"}]}");
            card.Render(prefs); qcard.Render(prefs);
            view.Part<Button>("PinButton").Background = (Brush)app.FindResource("PinnedFill");
            Render(content, view.Width, "widget-example.png", 2);
            var opts = Ui.Load<Window>("Settings");
            Check(opts.Part<PasswordBox>("ApiKey") != null && Ui.Load<Window>("Login").Part<Button>("Reopen") != null, "settings and authorization dialogs load");
            var save = opts.Part<Button>("Save");
            var settingsContent = (FrameworkElement)opts.Content;
            opts.Content = null;
            TextElement.SetFontFamily(settingsContent, opts.FontFamily);
            TextElement.SetFontSize(settingsContent, opts.FontSize);
            Render(settingsContent, opts.Width, "settings-layout.png");
            Check(Descendants(save).Any(x => (x is TextBlock || x is AccessText) &&
                ((SolidColorBrush)x.GetValue(TextElement.ForegroundProperty)).Color == Colors.White), "save button label contrasts with the dark background");
            CheckWidgetWindow();
        }

        private static void CheckWidgetWindow()
        {
            var store = new SettingsStore(Path.Combine(Folder, "widget-session"));
            using (var board = new Board(store))
            {
                var view = board.Window;
                view.Opacity = 0;
                view.ShowActivated = false;
                view.ShowInTaskbar = false;
                view.Show();
                var rect = Ui.BoundsOf(view);
                var area = Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(view).Handle).WorkingArea;
                Ui.Position(view, area.Left + 3, area.Top + 3, false);
                Ui.DockWidget(view, true);
                var left = Ui.BoundsOf(view);
                Check(left.Left == area.Left && left.Top == area.Top && DockPanel.GetDock(view.Part<Grid>("MascotHit")) == Dock.Left,
                    "real window snaps at top left and keeps text inward");
                Ui.Position(view, area.Right - (rect.Right - rect.Left) - 3, area.Bottom - (rect.Bottom - rect.Top) - 3, false);
                Ui.DockWidget(view, true);
                var right = Ui.BoundsOf(view);
                Check(right.Right == area.Right && right.Bottom == area.Bottom && DockPanel.GetDock(view.Part<Grid>("MascotHit")) == Dock.Right,
                    "real window snaps at bottom right above the taskbar");
                var details = view.Part<Button>("Details");
                details.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check((string)details.Content == "收起明细", "details button expands the live widget");
                details.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                view.Part<Button>("ToTray").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!view.IsVisible, "tray button hides the live widget");
                board.Show();
                Check(view.IsVisible && (string)details.Content == "明细", "show restores the compact widget");
                view.Hide();
            }
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
        {
            for (int n = 0; n < VisualTreeHelper.GetChildrenCount(element); n++)
            {
                var child = VisualTreeHelper.GetChild(element, n);
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        private static double Render(FrameworkElement element, double width, string file, int scale = 1)
        {
            element.UpdateLayout();
            element.InvalidateMeasure();
            element.Measure(new Size(width, double.PositiveInfinity));
            var size = new Size(width, Math.Ceiling(element.DesiredSize.Height));
            element.Arrange(new Rect(size)); element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)width * scale, (int)size.Height * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(Folder, file))) encoder.Save(output);
            return size.Height;
        }
    }
}
