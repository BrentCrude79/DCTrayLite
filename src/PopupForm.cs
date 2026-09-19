using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DCTrayLite
{
    // Small in-app popup for same-site windows (OAuth flows, etc.).
    // Shares the main WebView2 environment, so cookies/session carry over.
    sealed class PopupForm : Form
    {
        readonly WebView2 _web = new WebView2 { Dock = DockStyle.Fill };

        public PopupForm(CoreWebView2Environment env, string url, string appName)
        {
            Text = appName;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(640, 720);
            Controls.Add(_web);
            Shown += async (s, e) =>
            {
                await _web.EnsureCoreWebView2Async(env);
                _web.CoreWebView2.NewWindowRequested += (ss, ee) =>
                {
                    // Popups of popups go to the system browser.
                    ee.Handled = true;
                    try { System.Diagnostics.Process.Start(ee.Uri); } catch { }
                };
                _web.Source = new Uri(url);
            };
        }
    }
}
