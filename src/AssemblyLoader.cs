using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DCTrayLite
{
    // Makes the single-exe work: WebView2's managed DLLs are embedded as
    // resources and resolved via AssemblyResolve; the native WebView2Loader.dll
    // is extracted to a temp dir and LoadLibrary'd before WebView2 initializes.
    static class AssemblyLoader
    {
        const string ResourcePrefix = "DCTrayLite.NativeLibs.";

        public static void Attach()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
            ExtractAndLoadNativeLoader();
        }

        static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            if (!name.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal))
                return null;
            string res = ResourcePrefix + name + ".dll";
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream(res))
            {
                if (s == null) return null;
                byte[] data = new byte[s.Length];
                int off = 0;
                while (off < data.Length)
                {
                    int n = s.Read(data, off, data.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
                return Assembly.Load(data);
            }
        }

        static void ExtractAndLoadNativeLoader()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PwaTray", "native");
            Directory.CreateDirectory(dir);
            string dllPath = Path.Combine(dir, "WebView2Loader.dll");
            try
            {
                if (!File.Exists(dllPath))
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(ResourcePrefix + "WebView2Loader.dll"))
                    {
                        if (s == null) return;
                        using (var f = new FileStream(dllPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            s.CopyTo(f);
                    }
                }
                LoadLibrary(dllPath);
            }
            catch { /* WebView2 init will surface a clear error if this failed */ }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibrary(string lpFileName);
    }
}
