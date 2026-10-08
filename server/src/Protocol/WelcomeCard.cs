using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security;

namespace EpidemicServer.Protocol
{
    /// <summary>
    /// The hub's welcome popup (WelcomeDataManager) loads a small XML list from LoginDataMessage.WelcomeDataURL:
    /// a root whose baseURL is prefixed to each entry's image unless it starts with http(s)://, Language
    /// groups (id "all" for everyone), and one element per page with image, time (seconds), action,
    /// tooltip (WelcomeDataManager.ParseEntry). We serve one page: a title card drawn here with plain text
    /// and shapes (no game art), both files written next to the server, outside the repository.
    /// </summary>
    public static class WelcomeCard
    {
        public const string Title = "Dead Island: Epidemic";
        public const string Subtitle = "Private preservation server";
        public const string Body =
            "The official servers closed on 15 October 2015; this copy runs against a local server " +
            "written from scratch for one owner's personal use. Not affiliated with Deep Silver or Stunlock Studios.";

        /// <summary>Writes welcome.png and welcome.xml into the folder and returns the XML's file URL.</summary>
        public static string Write(string folder)
        {
            // The hub caches a page's image on disk keyed by a hash of its URL, so a redrawn card gets a new
            // name (the date) and the hub fetches it again.
            string name = "welcome-" + DateTime.Now.ToString("yyyyMMdd") + ".png";
            foreach (string old in Directory.GetFiles(folder, "welcome*.png")) try { File.Delete(old); } catch (IOException) { }
            DrawCard(Path.Combine(folder, name));
            string baseUrl = new Uri(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).AbsoluteUri;
            string xml = "<WelcomeData baseURL=\"" + SecurityElement.Escape(baseUrl) + "\">" +
                         "<Language id=\"all\">" +
                         "<Entry image=\"" + name + "\" time=\"30\" action=\"None\" tooltip=\"" + SecurityElement.Escape(Subtitle) + "\" />" +
                         "</Language></WelcomeData>";
            string path = Path.Combine(folder, "welcome.xml");
            File.WriteAllText(path, xml);   // no XML declaration: the hub reads the document's first child as the root
            return new Uri(path).AbsoluteUri;
        }

        private static void DrawCard(string path)
        {
            // The hub keeps a downloaded page only if it is exactly 810 x 477 (WelcomeDataManager.LoadTexture);
            // anything else goes to its TGA fallback, which can't read a PNG.
            const int width = 810, height = 477;
            using (Bitmap bmp = new Bitmap(width, height))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(Color.FromArgb(16, 30, 36));
                using (Brush red = new SolidBrush(Color.FromArgb(170, 28, 28)))
                {
                    g.FillRectangle(red, 0, 0, width, 12);
                    g.FillRectangle(red, 0, height - 12, width, 12);
                    g.FillRectangle(red, 46, 165, 100, 5);
                }
                using (Font title = new Font(FontFamily.GenericSansSerif, 42, FontStyle.Bold))
                using (Font sub = new Font(FontFamily.GenericSansSerif, 24, FontStyle.Regular))
                using (Font body = new Font(FontFamily.GenericSansSerif, 18, FontStyle.Regular))
                using (Font foot = new Font(FontFamily.GenericSansSerif, 13, FontStyle.Italic))
                using (Brush light = new SolidBrush(Color.FromArgb(235, 235, 225)))
                using (Brush dim = new SolidBrush(Color.FromArgb(150, 170, 170)))
                {
                    g.DrawString(Title, title, light, 42, 52);
                    g.DrawString(Subtitle, sub, dim, 46, 116);
                    g.DrawString(Body, body, light, new RectangleF(46, 192, width - 92, 200));
                    g.DrawString("Local server, " + DateTime.Now.ToString("d MMMM yyyy"), foot, dim, 46, height - 56);
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
