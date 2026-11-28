using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace BalanceWatcher
{
    internal static class WebViewBundle
    {
        private const string CoreName = "Microsoft.Web.WebView2.Core";
        private const string WpfName = "Microsoft.Web.WebView2.Wpf";
        private const string Loader = "WebView2Loader.dll";
        private static readonly Lazy<Assembly> Core = new Lazy<Assembly>(() => Load(CoreName));
        private static readonly Lazy<Assembly> Wpf = new Lazy<Assembly>(() => Load(WpfName));

        public static void Register()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
                switch (new AssemblyName(args.Name).Name)
                {
                    case CoreName: return Core.Value;
                    case WpfName: return Wpf.Value;
                    default: return null;
                }
            };
        }

        private static Assembly Load(string name)
        {
            using (var source = typeof(WebViewBundle).Assembly.GetManifestResourceStream(name + ".dll"))
            using (var buffer = new MemoryStream())
            {
                source.CopyTo(buffer);
                return Assembly.Load(buffer.ToArray());
            }
        }

        // Windows loads the native DLL from disk. Give different loader versions separate paths.
        public static string InstallLoader(string components)
        {
            using (var source = typeof(WebViewBundle).Assembly.GetManifestResourceStream(Loader))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(source);
                var directory = Path.Combine(components, "WebView2-" + BitConverter.ToString(hash).Replace("-", ""));
                Directory.CreateDirectory(directory);
                var target = Path.Combine(directory, Loader);
                if (Matches(target, hash)) return directory;
                var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    source.Position = 0;
                    using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        source.CopyTo(file);
                    if (File.Exists(target)) File.Replace(temp, target, null);
                    else File.Move(temp, target);
                }
                catch (IOException) { if (!Matches(target, hash)) throw; }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                return directory;
            }
        }

        private static bool Matches(string path, byte[] expected)
        {
            if (!File.Exists(path)) return false;
            using (var file = File.OpenRead(path))
            using (var sha = SHA256.Create()) return sha.ComputeHash(file).SequenceEqual(expected);
        }
    }
}
