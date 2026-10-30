using System.IO;
using System.Text;

namespace SFSVizzyMod
{
    // 读取内嵌的 web/index.html（DLL 单文件分发，不依赖外部页面文件）。
    public static class WebAssets
    {
        private const string ResourceName = "SFSVizzyMod.web.index.html";
        private static string cached;

        public static string IndexHtml()
        {
            if (cached != null) return cached;
            try
            {
                using (Stream stream = typeof(WebAssets).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        cached = "<h1>SFS Vizzy</h1><p>Embedded web/index.html not found.</p>";
                        return cached;
                    }
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        cached = reader.ReadToEnd();
                }
            }
            catch (System.Exception e)
            {
                cached = "<h1>SFS Vizzy</h1><p>Failed to load UI: " + e.Message + "</p>";
            }
            return cached;
        }
    }
}
