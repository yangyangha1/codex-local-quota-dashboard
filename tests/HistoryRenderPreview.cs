using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace CodexLocalDashboard
{
    internal static class HistoryRenderPreview
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var outputPath = args.Length > 0 ? args[0] :
                    "history-preview.png";
                var temporary = Path.Combine(Path.GetTempPath(),
                    "history-preview-" + Guid.NewGuid().ToString("N"),
                    "history.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(temporary));
                try
                {
                    using (var store = new HistoryStore(temporary))
                    using (var form = new HistoryDashboardForm(store,
                        Array.IndexOf(args, "--light") >= 0
                            ? ThemeMode.Light : ThemeMode.Dark))
                    {
                        var samples = BuildSamples();
                        var from = new DateTimeOffset(
                            DateTime.Today.AddDays(-6));
                        var to = new DateTimeOffset(
                            DateTime.Today.AddDays(1));
                        form.ApplySamplesForPreview(samples, from, to);
                        form.StartPosition = FormStartPosition.Manual;
                        form.Location = new Point(-32000, -32000);
                        form.Show();
                        Application.DoEvents();
                        using (var bitmap = new Bitmap(form.Width,
                            form.Height, PixelFormat.Format32bppArgb))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(0, 0,
                                bitmap.Width, bitmap.Height));
                            bitmap.Save(outputPath, ImageFormat.Png);
                        }
                        form.Hide();
                    }
                }
                finally
                {
                    try
                    {
                        Directory.Delete(Path.GetDirectoryName(temporary),
                            true);
                    }
                    catch { }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().FullName + " · " +
                    ex.Message);
                return 1;
            }
        }

        private static List<HistorySample> BuildSamples()
        {
            var output = new List<HistorySample>();
            var start = DateTimeOffset.Now.AddDays(-6);
            long today = 400000;
            for (var index = 0; index <= 6 * 24 * 6; index++)
            {
                var at = start.AddMinutes(index * 10);
                if (index > 0 && at.Date != output[output.Count - 1].At.Date)
                    today = 120000;
                today += 22000 + (index % 17) * 1800;
                var remaining = Math.Max(7d, 83d - index * 0.075d +
                    Math.Sin(index / 18d) * 2.2d);
                output.Add(new HistorySample(at, today,
                    68400000 + index * 26000L,
                    188000000 + index * 26000L, remaining, 10080,
                    DateTimeOffset.Now.AddDays(2), 18));
            }
            return output;
        }
    }
}
