using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Reflection;
using System.Windows.Forms;
using System.Collections.Generic;

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
                var now = DateTimeOffset.Now;
                var resetAt = now.AddDays(6);
                var snapshot = new UsageSnapshot(
                    new TokenTotals(21400000, 370000, 18200000),
                    new TokenTotals(88400000, 910000, 70200000),
                    new TokenTotals(194000000, 2100000, 157000000),
                    0,
                    now,
                    new List<QuotaWindow> { new QuotaWindow(10080, 38, resetAt) },
                    new List<ProjectUsage>
                    {
                        new ProjectUsage(@"D:\work\codex-dashboard", "codex-dashboard",
                            new List<SessionUsage>
                            {
                                new SessionUsage("session-dashboard-a", 24300000, now),
                                new SessionUsage("session-dashboard-b", 8600000, now.AddMinutes(-12))
                            }),
                        new ProjectUsage(@"D:\work\materials-audit", "materials-audit",
                            new List<SessionUsage>
                            {
                                new SessionUsage("session-audit", 18600000, now.AddMinutes(-30))
                            }),
                        new ProjectUsage(@"D:\work\notes", "notes",
                            new List<SessionUsage>
                            {
                                new SessionUsage("session-notes", 5100000, now.AddHours(-1))
                            })
                    });
                snapshot.Projects[0].Sessions[0].SessionFilePath =
                    @"C:\Users\test\.codex\sessions\session-dashboard-a.jsonl";
                if (Array.IndexOf(args, "--detail-many") >= 0)
                    for (var index = 0; index < 20; index++)
                        snapshot.Projects[0].Sessions.Add(new SessionUsage(
                            "generated-" + index,
                            7200000L - index * 210000L,
                            now.AddMinutes(-index * 17)));
                var chart = (TokenRateChart)typeof(DashboardForm)
                    .GetField("tokenRateChart", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(form);
                var chartStart = now.AddHours(-2);
                var startingTokens = snapshot.Today.Total - 120L * 138000L;
                for (var minute = 0; minute < 120; minute++)
                {
                    chart.Capture(chartStart.AddMinutes(minute),
                        startingTokens + minute * 138000L,
                        88d - minute * 25d / 119d, 10080, resetAt);
                }
                var previewTheme = Array.IndexOf(args, "--dark") >= 0
                    ? ThemeMode.Dark : ThemeMode.Light;
                form.ApplyTheme(previewTheme);
                form.ApplySnapshot(snapshot);
                if (Array.IndexOf(args, "--rate") >= 0)
                    chart.ToggleMode();
                if (Array.IndexOf(args, "--6h") >= 0)
                {
                    chart.ZoomByWheel(-120, 1000);
                    chart.ZoomByWheel(-120, 1250);
                }
                if (Array.IndexOf(args, "--12h") >= 0)
                {
                    chart.ZoomByWheel(-120, 1000);
                    chart.ZoomByWheel(-120, 1250);
                    chart.ZoomByWheel(-120, 1500);
                }
                if (Array.IndexOf(args, "--detail") >= 0)
                {
                    typeof(DashboardForm).GetField("detailMode",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(form, true);
                    ((ProjectDetailChart)typeof(DashboardForm).GetField(
                        "projectDetailChart", BindingFlags.Instance |
                            BindingFlags.NonPublic).GetValue(form))
                        .SetProjects(snapshot.Projects);
                }
                if (Array.IndexOf(args, "--history") >= 0)
                {
                    typeof(DashboardForm).GetField("historyMode",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(form, true);
                    var history = (HistoryPanelChart)typeof(DashboardForm)
                        .GetField("historyPanelChart",
                            BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(form);
                    history.SetDate(DateTime.Today);
                    var historySamples = new List<HistorySample>();
                    var historyStart = new DateTimeOffset(DateTime.Today);
                    for (var index = 0; index < 180; index++)
                        historySamples.Add(new HistorySample(
                            historyStart.AddMinutes(index * 5),
                            18000 + index * 160L,
                            6200 + index * 45L,
                            5100 + index * 80L,
                            900 + index * 12L, index == 0,
                            0, 0, 0, 0,
                            88d - index * 25d / 179d,
                            10080, resetAt,
                            4840d + index * 41d));
                    history.SetSamples(historySamples, 184320);
                    if (Array.IndexOf(args, "--history-loading") >= 0)
                        history.SetLoading(true);
                }
                if (Array.IndexOf(args, "--dashboard-150") >= 0)
                {
                    form.ClientSize = new Size(384, 417);
                    typeof(DashboardForm).GetMethod("ScaleCanvas",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(form, null);
                }
                if (Array.IndexOf(args, "--detail-many") >= 0)
                {
                    var detailChart = (ProjectDetailChart)
                        typeof(DashboardForm).GetField(
                            "projectDetailChart", BindingFlags.Instance |
                                BindingFlags.NonPublic).GetValue(form);
                    var expanded = (HashSet<string>)
                        typeof(ProjectDetailChart).GetField(
                            "expandedProjects", BindingFlags.Instance |
                                BindingFlags.NonPublic).GetValue(detailChart);
                    expanded.Add(snapshot.Projects[0].ProjectPath);
                    var expandedSessions = (HashSet<string>)
                        typeof(ProjectDetailChart).GetField(
                            "expandedSessions", BindingFlags.Instance |
                                BindingFlags.NonPublic).GetValue(detailChart);
                    var firstSession = snapshot.Projects[0].Sessions[0];
                    expandedSessions.Add(
                        snapshot.Projects[0].ProjectPath + "\n" +
                        firstSession.SessionId + "\n" +
                        firstSession.StartedAt.UtcDateTime.Ticks.ToString(
                            System.Globalization.CultureInfo.InvariantCulture));
                }
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
                        form.ApplyTheme(ThemeMode.Light);
                    }
                    Application.Run(form);
                    return 0;
                }
                if (Array.IndexOf(args, "--strip-2k") >= 0)
                {
                    const float previewDpiScale = 1.5f;
                    using (var highDpiStrip = new Bitmap(1050, 42,
                        PixelFormat.Format32bppPArgb))
                    using (var highDpiPanel = new QuotaStripPanel
                    {
                        ClientSize = new Size(1050, 42),
                        DpiScale = previewDpiScale,
                        Theme = previewTheme,
                        Snapshot = snapshot
                    })
                    using (var stripGraphics =
                        Graphics.FromImage(highDpiStrip))
                    {
                        stripGraphics.Clear(previewTheme == ThemeMode.Light
                            ? Color.FromArgb(230, 244, 244, 242)
                            : Color.FromArgb(230, 20, 20, 20));
                        highDpiPanel.DrawLayered(stripGraphics);
                        highDpiStrip.Save(args[0], ImageFormat.Png);
                    }
                    return 0;
                }
                using (var dashboard = form.CreateLayeredSurfacePreview())
                using (var strip = new Bitmap(700, 28, PixelFormat.Format32bppPArgb))
                using (var stripPanel = new QuotaStripPanel { ClientSize = new Size(700, 28), DpiScale = 1f, Theme = previewTheme, Snapshot = snapshot })
                using (var graphics = Graphics.FromImage(output))
                {
                    if (Array.IndexOf(args, "--dashboard-only") >= 0)
                    {
                        dashboard.Save(args.Length > 0 ? args[0] :
                            "render-preview.png", ImageFormat.Png);
                        return 0;
                    }
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
