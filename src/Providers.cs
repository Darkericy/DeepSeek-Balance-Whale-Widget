using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BalanceWatcher
{
    internal sealed class DeepSeekClient
    {
        private readonly HttpClient client;
        public DeepSeekClient(HttpMessageHandler handler)
        {
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        }

        public async Task<Reading> Read(string key, CancellationToken cancel)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                try
                {
                    using (var response = await client.SendAsync(request, cancel).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        if (status == 401) throw new WatchError("密钥未通过验证，请重新填写 API Key。", true);
                        if (status == 403) throw new WatchError("此密钥没有查询余额的权限。", true);
                        if (status == 429) throw new WatchError("查询过于频繁，稍后会自动重试。");
                        if (status == 402) throw new WatchError("平台提示余额不足，未返回具体金额。");
                        if (!response.IsSuccessStatusCode) throw new WatchError("平台暂时无法返回余额（HTTP " + status + "）。");
                        return BalanceJson.DeepSeek(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    }
                }
                catch (OperationCanceledException) { cancel.ThrowIfCancellationRequested(); throw new WatchError("查询超时，稍后会自动重试。"); }
                catch (HttpRequestException) { throw new WatchError("无法连接 DeepSeek，请检查网络或系统代理。"); }
            }
        }
    }

    internal sealed class LoginStep
    {
        public bool Done;
        public Uri Page;
        public static LoginStep Parse(string json)
        {
            var events = BalanceJson.Get(BalanceJson.Object(json), "events") as object[];
            if (events == null) throw BalanceJson.Invalid();
            foreach (var e in events.OfType<Dictionary<string, object>>().Reverse())
            {
                var kind = BalanceJson.Get(e, "event") as string;
                if ((kind == "success" || kind == "already_authenticated") && Equals(BalanceJson.Get(e, "authenticated"), true))
                    return new LoginStep { Done = true };
                Uri uri;
                if (kind == "device_code" && Uri.TryCreate(BalanceJson.Get(e, "verification_url") as string, UriKind.Absolute, out uri)
                    && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)) return new LoginStep { Page = uri };
                if (kind == "expired" || kind == "pending") throw new WatchError("授权已超时，请重新登录千问。");
                if (kind == "error") throw new WatchError("授权未完成，请重新登录千问。");
            }
            throw BalanceJson.Invalid();
        }
    }

    internal sealed class QianwenClient
    {
        private const string BinaryHash = "FEBA174925CD85ED7EEBFF8C2B8CF91BDE22C7DD2E353A62D90461B83A85DBA3";
        private readonly object installLock = new object();
        private string verifiedPath;
        private readonly string toolsFolder;
        public QianwenClient(string folder) { toolsFolder = folder; }

        private static bool Verified(string path)
        {
            if (!File.Exists(path)) return false;
            using (var file = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "") == BinaryHash;
        }

        internal string GetBinary()
        {
            lock (installLock)
            {
                if (verifiedPath != null && File.Exists(verifiedPath)) return verifiedPath;
                Directory.CreateDirectory(toolsFolder);
                var target = Path.Combine(toolsFolder, "qianwen-1.12.0.exe");
                if (!Verified(target))
                {
                    string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("qianwen.zip"))
                        using (var archive = new ZipArchive(resource, ZipArchiveMode.Read))
                        using (var source = archive.GetEntry("qianwen.exe").Open())
                        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
                        if (!Verified(temp)) throw new WatchError("千问组件校验失败，请重新获取余额看板。");
                        if (File.Exists(target)) File.Replace(temp, target, null);
                        else File.Move(temp, target);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                return verifiedPath = target;
            }
        }

        private sealed class Reply { public int Code; public string Output, Error; }

        internal static void ConfigureRoute(ProcessStartInfo start, bool direct)
        {
            if (!direct) return;
            // Windows makes these names case-insensitive. Only the official CLI child is affected.
            start.EnvironmentVariables.Remove("HTTPS_PROXY");
            start.EnvironmentVariables.Remove("HTTP_PROXY");
            start.EnvironmentVariables.Remove("ALL_PROXY");
            start.EnvironmentVariables["NO_PROXY"] = "*";
        }

        private Task<Reply> Run(string args, int seconds, bool direct, CancellationToken cancel)
        {
            return Task.Run(() =>
            {
                var path = GetBinary();
                cancel.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo(path, args) {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = toolsFolder,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                ConfigureRoute(start, direct);
                using (var process = new Process { StartInfo = start })
                {
                    try { process.Start(); }
                    catch (Win32Exception) { throw new WatchError("千问组件未能启动，请检查 Windows 是否允许其运行。"); }
                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    Action kill = () => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { } };
                    using (cancel.Register(kill))
                    {
                        if (!process.WaitForExit(seconds * 1000)) { kill(); throw new WatchError("千问响应超时，请稍后重试。"); }
                        cancel.ThrowIfCancellationRequested();
                        return new Reply { Code = process.ExitCode, Output = output.GetAwaiter().GetResult(), Error = error.GetAwaiter().GetResult() };
                    }
                }
            }, cancel);
        }

        internal static WatchError CliError(int code, string stderr, bool direct)
        {
            string reason = null;
            try
            {
                var error = BalanceJson.Get(BalanceJson.Object(stderr), "error") as Dictionary<string, object>;
                reason = BalanceJson.Get(error, "code") as string;
            }
            catch (WatchError) { }
            if (code == 2 || reason == "TOKEN_EXPIRED" || reason == "AUTH_REQUIRED" || reason == "CS_DATA_AUTH_REQUIRED")
                return new WatchError("千问登录已失效，请重新授权。", true);
            if (code == 5 || reason == "RATE_LIMITED") return new WatchError("千问查询过于频繁，稍后会自动重试。");
            if (code == 6 || reason == "SERVER_ERROR") return new WatchError("千问服务暂时不可用，稍后会自动重试。");
            if (code == 3) return new WatchError(direct ? "直连千问失败，请检查网络，或在设置中关闭“直连千问”后重试。" :
                "千问连接失败。若本机代理已关闭或换了端口，可在设置中开启“直连千问”。");
            return new WatchError("千问未能完成查询" + (reason != null && reason.Length <= 55 && reason.All(c => char.IsLetterOrDigit(c) || c == '_') ? "（" + reason + "）" : "") + "，请稍后重试。");
        }

        public async Task<Reading> Read(CancellationToken cancel, bool direct)
        {
            var result = await Run("billing balance summary --format json", 35, direct, cancel).ConfigureAwait(false);
            if (result.Code != 0) throw CliError(result.Code, result.Error, direct);
            return BalanceJson.Qianwen(result.Output);
        }

        public async Task<LoginStep> Login(bool complete, bool direct, CancellationToken cancel)
        {
            var result = await Run(complete ? "auth login --complete --timeout 240 --format json" : "auth login --init-only --format json",
                complete ? 260 : 40, direct, cancel).ConfigureAwait(false);
            // The CLI reports an expired or incomplete device flow in stdout, even on exit 1.
            if (result.Code != 0 && (!complete || string.IsNullOrWhiteSpace(result.Output))) throw CliError(result.Code, result.Error, direct);
            var step = LoginStep.Parse(result.Output);
            if (result.Code != 0 || (complete && !step.Done)) throw new WatchError("授权未完成，请重新登录千问。");
            return step;
        }
    }
}
