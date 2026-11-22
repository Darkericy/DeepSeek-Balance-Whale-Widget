using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace BalanceWatcher
{
    internal sealed class Board : IDisposable
    {
        public readonly Window Window = Ui.LoadWidget();
        public readonly SettingsStore Store;
        public readonly Account Deep = new Account("DeepSeek", true);
        public readonly Account Qianwen = new Account("千问 AI", false);
        private readonly DeepSeekClient deepClient = new DeepSeekClient(new HttpClientHandler { AllowAutoRedirect = false });
        private readonly QianwenClient qClient;
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly Forms.NotifyIcon tray = new Forms.NotifyIcon();
        private readonly CardView deepCard, qCard;
        private bool details;
        private Point? mascotPress;
        private readonly ScaleTransform squash = new ScaleTransform();
        private bool quitting;

        public Board(SettingsStore store)
        {
            Store = store;
            qClient = new QianwenClient(Path.Combine(SettingsStore.AppFolder, "components"));
            Deep.Reset(!string.IsNullOrEmpty(Store.Value.ProtectedKey));
            Qianwen.Reset(Store.Value.QianwenEnabled);
            deepCard = new CardView(Deep, () => Settings(true));
            qCard = new CardView(Qianwen, () => Login(Window, Store.Value.QianwenDirect));
            var stack = Window.Part<StackPanel>("Accounts");
            stack.Children.Add(deepCard.Root);
            stack.Children.Add(qCard.Root);
            Deep.Changed += Paint;
            Qianwen.Changed += Paint;
            Deep.Succeeded += () => Notify(Deep);
            Qianwen.Succeeded += () => Notify(Qianwen);
            timer.Tick += async (s, e) => await Refresh(false);
            Window.Part<Button>("Refresh").Click += async (s, e) => await Refresh(true);
            Window.Part<Button>("Details").Click += (s, e) => { details = !details; Paint(); };
            Window.Part<Button>("Options").Click += (s, e) => Settings(false);
            Window.Part<Button>("ToTray").Click += (s, e) => Window.Hide();
            Window.Part<Button>("PinButton").Click += (s, e) => {
                try { Store.Update(p => p.Pinned = !p.Pinned); ApplyDisplay(); }
                catch (Exception) { Ui.Explain(Window, "无法保存置顶设置，请检查当前用户的设置目录。"); }
            };
            Window.Part<FrameworkElement>("DragZone").MouseLeftButtonDown += (s, e) => {
                if (e.LeftButton == MouseButtonState.Pressed) MoveWidget();
            };
            WireMascot();
            Window.Closing += (s, e) => { if (!quitting) { e.Cancel = true; Window.Hide(); } };
            Window.Loaded += async (s, e) => {
                Ui.Position(Window, Store.Value.Left, Store.Value.Top, true);
                Ui.DockWidget(Window, false);
                timer.Start();
                if (Store.LoadMessage != null) Ui.Explain(Window, Store.LoadMessage);
                await Refresh(false);
            };
            Window.SizeChanged += (s, e) => {
                if (!Window.IsLoaded || !Window.IsVisible || !e.HeightChanged) return;
                Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() => {
                    if (quitting || !Window.IsVisible) return;
                    var rect = Ui.BoundsOf(Window);
                    Ui.Position(Window, rect.Left, rect.Top, false);
                }));
            };
            CreateTray();
            ApplyDisplay();
        }

        private void MoveWidget()
        {
            Window.DragMove();
            Ui.DockWidget(Window, true);
            var rect = Ui.BoundsOf(Window);
            if (Store.Value.Left == rect.Left && Store.Value.Top == rect.Top) return;
            try { Store.Update(p => { p.Left = rect.Left; p.Top = rect.Top; }); }
            catch (Exception) { Ui.Explain(Window, "无法记住窗口位置，请检查当前用户的设置目录。"); }
        }

        private void WireMascot()
        {
            var hit = Window.Part<Grid>("MascotHit");
            Window.Part<Image>("MascotImage").RenderTransform = squash;
            hit.MouseLeftButtonDown += (s, e) => {
                if (!hit.CaptureMouse()) return;
                mascotPress = e.GetPosition(Window);
                // Match the upstream whale's press proportions (assets/whale-widget.js, SQUISH).
                if (SystemParameters.ClientAreaAnimation)
                {
                    squash.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    squash.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    squash.ScaleX = 1.05;
                    squash.ScaleY = 0.88;
                }
                e.Handled = true;
            };
            hit.MouseMove += (s, e) => {
                if (!mascotPress.HasValue || e.LeftButton != MouseButtonState.Pressed) return;
                var delta = e.GetPosition(Window) - mascotPress.Value;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                hit.ReleaseMouseCapture();
                MoveWidget();
            };
            hit.MouseLeftButtonUp += async (s, e) => {
                if (!mascotPress.HasValue) return;
                bool clicked = hit.IsMouseOver;
                hit.ReleaseMouseCapture();
                e.Handled = true;
                if (clicked) await Refresh(true);
            };
            hit.LostMouseCapture += (s, e) => {
                mascotPress = null;
                double x = squash.ScaleX, y = squash.ScaleY;
                squash.ScaleX = squash.ScaleY = 1;
                if (!SystemParameters.ClientAreaAnimation) return;
                var ease = new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut };
                var duration = new Duration(TimeSpan.FromMilliseconds(250));
                squash.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(x, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
                squash.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(y, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
            };
        }

        private void CreateTray()
        {
            tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示余额看板", null, (s, e) => Show());
            menu.Items.Add("立即刷新", null, async (s, e) => await Refresh(true));
            menu.Items.Add("账户与设置", null, (s, e) => Settings(false));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => Quit());
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) Show(); };
            tray.BalloonTipClicked += (s, e) => Show();
            tray.Text = "余额看板";
            tray.Visible = true;
        }

        public void Show()
        {
            Window.Show();
            Window active = Window;
            while (active.OwnedWindows.OfType<Window>().Any(w => w.IsVisible))
                active = active.OwnedWindows.OfType<Window>().First(w => w.IsVisible);
            active.Activate();
        }

        private void Settings(bool focusKey)
        {
            Show();
            if (Window.OwnedWindows.OfType<Window>().Any(w => w.IsVisible)) return;
            new OptionsDialog(this).Show(Window, focusKey);
        }

        public bool Login(Window parent, bool direct)
        {
            if (!LoginDialog.Open(parent, qClient, direct)) return false;
            try { Store.Update(p => { p.QianwenEnabled = true; p.QianwenDirect = direct; }); }
            catch (Exception) { Ui.Explain(parent, "已完成授权，但无法保存监控设置。请检查当前用户的设置目录。"); return false; }
            Qianwen.Reset(true);
            RefreshQianwen();
            return true;
        }
        private Task<Reading> ReadQianwen(CancellationToken token) { return qClient.Read(token, Store.Value.QianwenDirect); }
        private async void RefreshQianwen() { await Qianwen.Refresh(ReadQianwen, true); }

        private Task Refresh(bool manual)
        {
            return Task.WhenAll(Deep.Refresh(t => deepClient.Read(Store.ReadKey(), t), manual), Qianwen.Refresh(ReadQianwen, manual));
        }
        public void Apply(Preferences previous)
        {
            bool differentKey = previous.ProtectedKey != Store.Value.ProtectedKey;
            bool differentRoute = previous.QianwenDirect != Store.Value.QianwenDirect;
            if (differentKey) Deep.Reset(!string.IsNullOrEmpty(Store.Value.ProtectedKey));
            if (previous.QianwenEnabled != Store.Value.QianwenEnabled) Qianwen.Reset(Store.Value.QianwenEnabled);
            else if (differentRoute) Qianwen.CancelPending();
            ApplyDisplay();
            Notify(Deep);
            Notify(Qianwen);
            if (differentKey && Deep.Enabled) RefreshDeep();
            if (Qianwen.Enabled && (differentRoute || !previous.QianwenEnabled)) RefreshQianwen();
        }
        private async void RefreshDeep() { await Deep.Refresh(t => deepClient.Read(Store.ReadKey(), t), true); }

        private void ApplyDisplay()
        {
            Window.Topmost = Store.Value.Pinned;
            var pin = Window.Part<Button>("PinButton");
            pin.ToolTip = Store.Value.Pinned ? "已置顶，点击取消" : "置顶看板";
            pin.Background = Ui.Color(Store.Value.Pinned ? "#EAE5F9" : "Transparent");
            timer.Interval = TimeSpan.FromMinutes(Store.Value.Minutes);
            Paint();
        }

        private void Paint()
        {
            if (quitting) return;
            deepCard.Render(Store.Value, details);
            qCard.Render(Store.Value, details);
            var accounts = new[] { Deep, Qianwen };
            Window.Part<TextBlock>("Cadence").Text = "每 " + Store.Value.Minutes + " 分钟刷新";
            Window.Part<Button>("Details").Content = details ? "收起明细" : "明细";
            Window.Part<Button>("Refresh").IsEnabled = accounts.Any(a => a.Enabled && !a.Busy);
            var lines = accounts.Select(a => a.Name + "  " + (a.Last != null && a.Last.Amounts.Count > 0 ?
                string.Join(" / ", a.Last.Amounts.Select(b => b.Format(b.Total))) + (a.Error != null ? "（上次）" : "") : a.Enabled ? "待更新" : "未连接"));
            string tip = string.Join("\n", lines);
            tray.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
        }
        private void Notify(Account account)
        {
            string text = account.NewWarning(Store.Value);
            if (text != null && !quitting) tray.ShowBalloonTip(6000, account.Name + " 余额提醒", text, Forms.ToolTipIcon.Warning);
        }
        public void Quit()
        {
            quitting = true;
            Application.Current.Shutdown();
        }
        public void Dispose()
        {
            quitting = true;
            timer.Stop();
            Deep.Reset(false);
            Qianwen.Reset(false);
            tray.Visible = false;
            if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose();
            if (tray.Icon != null) tray.Icon.Dispose();
            tray.Dispose();
        }
    }
}
