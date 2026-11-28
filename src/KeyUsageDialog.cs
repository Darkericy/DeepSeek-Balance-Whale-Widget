using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace BalanceWatcher
{
    internal sealed class KeyUsageDialog
    {
        internal static readonly Uri UsagePage = new Uri("https://platform.deepseek.com/usage");
        private static bool loaderReady;
        private readonly Window view = Ui.Load<Window>("KeyUsage");
        private WebView2 browser;
        private bool closed;

        public void Show(Window owner)
        {
            view.Owner = owner;
            view.Width = Math.Min(view.Width, SystemParameters.WorkArea.Width);
            view.Height = Math.Min(view.Height, SystemParameters.WorkArea.Height);
            view.Part<TextBlock>("Origin").Text = UsagePage.GetLeftPart(UriPartial.Authority);
            view.Part<Button>("Reload").Click += async (s, e) => {
                try
                {
                    if (browser == null) await Start();
                    else if (IsUsagePage(browser.Source)) browser.Reload();
                    else browser.CoreWebView2.Navigate(UsagePage.AbsoluteUri);
                }
                catch (InvalidOperationException) { Failed("用量页已中断，请重试。", false); }
            };
            view.Part<Button>("RuntimeDownload").Click += (s, e) => {
                try { Ui.Open(new Uri("https://developer.microsoft.com/microsoft-edge/webview2/#download")); }
                catch (Exception) { Notice("请在浏览器中打开 Microsoft Edge WebView2 下载页，安装 Evergreen 运行时。", true); }
            };
            view.ContentRendered += FirstRender;
            view.Closed += (s, e) => { closed = true; ReleaseBrowser(); };
            view.ShowDialog();
        }

        private async void FirstRender(object sender, EventArgs e)
        {
            view.ContentRendered -= FirstRender;
            Ui.Position(view, null, null, false);
            await Start();
        }

        private async Task Start()
        {
            var reload = view.Part<Button>("Reload");
            reload.IsEnabled = false;
            Notice("正在打开 DeepSeek 官方用量页…");
            try
            {
                if (!loaderReady)
                {
                    CoreWebView2Environment.SetLoaderDllFolderPath(WebViewBundle.InstallLoader(Path.Combine(SettingsStore.AppFolder, "components")));
                    loaderReady = true;
                }
                var profile = Path.Combine(SettingsStore.AppFolder, "DeepSeekPlatform");
                var environment = await CoreWebView2Environment.CreateAsync(null, profile);
                if (closed) return;
                browser = new WebView2();
                view.Part<Border>("BrowserHost").Child = browser;
                await browser.EnsureCoreWebView2Async(environment);
                if (closed) return;
                OpenReport(browser);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                if (!closed) Failed("请安装 Microsoft Edge WebView2 Evergreen 运行时，然后重试。", true);
            }
            catch (Exception)
            {
                if (!closed) Failed("无法打开用量页，请检查 WebView2 运行时和当前用户的数据目录后重试。", false);
            }
            finally { if (!closed) reload.IsEnabled = true; }
        }

        private void OpenReport(WebView2 active)
        {
            var web = active.CoreWebView2;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.IsWebMessageEnabled = false;
            web.NavigationStarting += (s, e) => {
                Uri next;
                if (TryWebAddress(e.Uri, out next)) return;
                e.Cancel = true;
                Notice("此窗口仅支持 HTTPS 网页。");
            };
            web.SourceChanged += (s, e) => {
                Uri current;
                if (active != browser || !TryWebAddress(web.Source, out current)) return;
                view.Part<TextBlock>("Origin").Text = current.GetLeftPart(UriPartial.Authority);
                view.Part<Button>("Reload").Content = IsUsagePage(current) ? "刷新用量" : "返回用量页";
            };
            web.NavigationCompleted += (s, e) => {
                if (active != browser || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
                Notice(e.IsSuccess ? null : "网页加载失败，请检查网络后重试。");
            };
            web.ProcessFailed += (s, e) => {
                if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited &&
                    e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited) return;
                view.Dispatcher.BeginInvoke((Action)(() => {
                    if (!closed && browser == active) Failed("用量页已中断，请重试。", false);
                }));
            };
            web.Navigate(UsagePage.AbsoluteUri);
        }

        internal static bool TryWebAddress(string address, out Uri uri)
        {
            return Uri.TryCreate(address, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;
        }

        internal static bool IsUsagePage(Uri uri)
        {
            return uri != null && uri.IsAbsoluteUri && uri.Scheme == UsagePage.Scheme && uri.UserInfo.Length == 0 &&
                uri.Authority == UsagePage.Authority && uri.AbsolutePath.TrimEnd('/') == UsagePage.AbsolutePath;
        }

        private void Failed(string message, bool runtimeMissing)
        {
            ReleaseBrowser();
            view.Part<Button>("Reload").Content = "重试";
            Notice(message, runtimeMissing);
        }

        private void Notice(string message, bool runtimeMissing = false)
        {
            view.Part<StackPanel>("Notice").Visibility = Ui.Visible(message != null);
            view.Part<TextBlock>("Status").Text = message ?? "";
            view.Part<Button>("RuntimeDownload").Visibility = Ui.Visible(runtimeMissing);
        }

        private void ReleaseBrowser()
        {
            if (browser == null) return;
            var active = browser;
            browser = null;
            view.Part<Border>("BrowserHost").Child = null;
            active.Dispose();
        }
    }
}
