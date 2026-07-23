using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Reflection;
using System.Windows.Forms;

namespace CodexLocalDashboard
{
    internal static class RenderPreview
    {
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset))
            using (var form = new DashboardForm(signal))
            using (var output = new Bitmap(900, 560, PixelFormat.Format32bppArgb))
            {
                var snapshot = new UsageSnapshot(
                    new TokenTotals(21400000, 370000, 18200000),
                    new TokenTotals(88400000, 910000, 70200000),
                    new TokenTotals(194000000, 2100000, 157000000),
                    0,
                    DateTimeOffset.Now,
                    new System.Collections.Generic.List<QuotaWindow> { new QuotaWindow(10080, 38, DateTimeOffset.Now.AddDays(6)) });
                form.ApplyTheme(ThemeMode.Transparent, false);
                form.ApplySnapshot(snapshot);
                if (args.Length > 0 && args[0].StartsWith("--live", StringComparison.Ordinal))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(120, 120);
                    if (args[0] == "--live-strip-light")
                    {
                        typeof(DashboardForm).GetField("stripMode", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, true);
                        var canvas = (Panel)typeof(DashboardForm).GetField("canvas", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                        var stripPanel = (QuotaStripPanel)typeof(DashboardForm).GetField("stripPanel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                        canvas.Visible = false;
                        stripPanel.Visible = true;
                        form.ClientSize = new Size(700, 28);
                        form.ApplyTheme(ThemeMode.Light, false);
                    }
                    Application.Run(form);
                    return 0;
                }
                using (var dashboard = form.CreateLayeredSurfacePreview())
                using (var strip = new Bitmap(700, 28, PixelFormat.Format32bppPArgb))
                using (var stripPanel = new QuotaStripPanel { ClientSize = new Size(700, 28), DpiScale = 1f, Theme = ThemeMode.Transparent, Snapshot = snapshot })
                using (var graphics = Graphics.FromImage(output))
                {
                    graphics.Clear(Color.FromArgb(220, 228, 235));
                    using (var stripGraphics = Graphics.FromImage(strip))
                    {
                        stripGraphics.Clear(Color.Transparent);
                        stripPanel.DrawLayered(stripGraphics);
                        using (var hitLayer = new SolidBrush(Color.FromArgb(1, 255, 255, 255))) stripGraphics.FillRectangle(hitLayer, 0, 0, strip.Width, strip.Height);
                    }
                    graphics.DrawImageUnscaled(strip, 100, 35);
                    graphics.DrawImageUnscaled(dashboard, 40, 100);
                }
                output.Save(args.Length > 0 ? args[0] : "render-preview.png", ImageFormat.Png);
            }
            return 0;
        }
    }
}
