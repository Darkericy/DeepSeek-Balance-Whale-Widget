using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows;

[assembly: AssemblyTitle("余额看板")]
[assembly: AssemblyDescription("龙掌柜的账本 · DeepSeek 与千问 AI 桌面余额挂件")]
[assembly: AssemblyProduct("余额看板")]
[assembly: AssemblyVersion("1.2.0.0")]

namespace BalanceWatcher
{
    internal static class Program
    {
        [STAThread]
        public static void Main()
        {
            bool first;
            using (var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LLMBalanceWatcher.Show.v1"))
            using (var single = new Mutex(true, @"Local\LLMBalanceWatcher.v1", out first))
            {
                if (!first) { wake.Set(); return; }
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(Ui.Load<ResourceDictionary>("Theme"));
                using (var board = new Board(new SettingsStore(SettingsStore.AppFolder)))
                {
                    var listener = ThreadPool.RegisterWaitForSingleObject(wake, (s, timed) => {
                        if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke((Action)board.Show);
                    }, null, Timeout.Infinite, false);
                    try { app.Run(board.Window); }
                    finally { listener.Unregister(null); }
                }
            }
        }
    }
}
