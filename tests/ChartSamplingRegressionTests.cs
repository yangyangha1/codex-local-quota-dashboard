using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;

namespace CodexLocalDashboard
{
    internal static class ChartSamplingRegressionTests
    {
        private static int failures;

        public static int Main()
        {
            HiddenCaptureBuildsBothSeries();
            SameDayRollbackDoesNotCreateRateSpike();
            QuotaSmoothingAvoidsVisualBreaks();
            QuotaConnectsAcrossMissingSamples();
            QuotaConsumptionCountsRuntimeDrops();
            TokenAxisKeepsPeakNearEightyPercent();
            CumulativeSeriesIsSmoothAndExact();
            CumulativeAxisKeepsPeakNearEightyPercent();
            UnitsScaleAutomatically();
            WheelZoomUsesSharedInMemoryHistory();
            DashboardSizingKeepsFixedAspectRatio();
            EmbeddedDetailInteractionWorks();
            CompactDualChartDraws();
            Console.WriteLine(failures == 0 ? "PASS" : "FAILURES=" + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void DashboardSizingKeepsFixedAspectRatio()
        {
            var current = new Rectangle(100, 100, 320, 347);
            var minimum = new Size(256, 278);
            var maximum = new Size(576, 625);

            var horizontal = DashboardForm.ConstrainAspectRatio(
                new Rectangle(100, 100, 480, 347), current, 2,
                minimum, maximum);
            Equal("horizontal-resize-width", 480, horizontal.Width);
            Equal("horizontal-resize-fixed-height", 521,
                horizontal.Height);

            var vertical = DashboardForm.ConstrainAspectRatio(
                new Rectangle(100, 100, 320, 500), current, 6,
                minimum, maximum);
            Equal("vertical-resize-height", 500, vertical.Height);
            Equal("vertical-resize-fixed-width", 461, vertical.Width);

            var clamped = DashboardForm.ConstrainAspectRatio(
                new Rectangle(100, 100, 900, 900), current, 8,
                minimum, maximum);
            Equal("fixed-resize-max-width", 576, clamped.Width);
            Equal("fixed-resize-max-height", 625, clamped.Height);
        }

        private static void HiddenCaptureBuildsBothSeries()
        {
            var chart = new TokenRateChart();
            chart.DisplayMode = UsageChartMode.TokenRate;
            var start = new DateTimeOffset(2026, 7, 26, 10, 0, 5,
                TimeSpan.FromHours(8));
            var reset = start.AddHours(6);

            // Capture runs without calling Draw while the combined chart is
            // hidden. Both modes must still receive every background sample.
            chart.Capture(start, 1000, 80, 360, reset);
            chart.Capture(start.AddSeconds(30), 1100, 79, 360, reset);
            chart.Capture(start.AddSeconds(60), 1300, 78, 360, reset);
            chart.Capture(start.AddSeconds(90), 1600, 77, 360, reset);
            chart.Capture(start.AddSeconds(120), 1700, 76, 360, reset);

            var snapshot = Snapshot(chart, start.AddSeconds(120));
            var tokens = Points(snapshot, "TokenPoints");
            var quotas = Points(snapshot, "QuotaPoints");
            var cumulative = Points(snapshot, "CumulativePoints");
            Equal("hidden-token-history", 5, tokens.Count);
            Equal("hidden-quota-history", 5, quotas.Count);
            Equal("hidden-cumulative-history", 5, cumulative.Count);
            Equal("token-plot-frequency-is-30s", 30d,
                PointSpacingSeconds(tokens[0], tokens[1]));
            Equal("quota-plot-frequency-is-30s", 30d,
                PointSpacingSeconds(quotas[0], quotas[1]));
            Equal("cumulative-plot-frequency-is-30s", 30d,
                PointSpacingSeconds(cumulative[0], cumulative[1]));
            Equal("cumulative-starts-at-zero", 0d,
                PointValue(cumulative[0]));
            Equal("cumulative-time-moves-left-to-right", true,
                PointAt(cumulative[cumulative.Count - 1]) >
                PointAt(cumulative[0]));
            Equal("period-increase", 700d,
                Field<double>(snapshot, "CumulativeIncrease"));
            Equal("runtime-quota-consumption", 4d,
                Field<double>(snapshot,
                    "QuotaConsumedDuringRuntime"));
            Equal("timeline-starts-at-first-capture", start,
                Field<DateTimeOffset>(snapshot, "TimelineStart"));
            var latestRate = PointValue(tokens[tokens.Count - 1]);
            Equal("smoothed-rate-lower-bound", true, latestRate >= 300d);
            Equal("smoothed-rate-upper-bound", true, latestRate <= 430d);
            Equal("one-background-sample-per-plotted-point", 1,
                PointSampleCount(tokens[0]));
            Equal("rate-mode-during-background-capture",
                UsageChartMode.TokenRate, chart.DisplayMode);
            chart.ToggleMode();
            Equal("switch-shows-precollected-combined",
                UsageChartMode.CumulativeAndQuota, chart.DisplayMode);
            var combinedSnapshot = Snapshot(chart, start.AddSeconds(120));
            Equal("combined-history-survives-switch", cumulative.Count,
                Points(combinedSnapshot, "CumulativePoints").Count);
            Equal("rate-history-also-survives-switch", tokens.Count,
                Points(combinedSnapshot, "TokenPoints").Count);
        }

        private static void SameDayRollbackDoesNotCreateRateSpike()
        {
            var chart = new TokenRateChart();
            var start = new DateTimeOffset(2026, 7, 26, 11, 0, 5,
                TimeSpan.FromHours(8));
            chart.Capture(start, 1000, 70, 360, start.AddHours(6));
            chart.Capture(start.AddSeconds(30), 1100, 69, 360,
                start.AddHours(6));
            chart.Capture(start.AddSeconds(60), 1200, 68, 360,
                start.AddHours(6));
            chart.Capture(start.AddSeconds(90), 20, 67, 360,
                start.AddHours(6));
            chart.Capture(start.AddSeconds(120), 1300, 66, 360,
                start.AddHours(6));

            var snapshot = Snapshot(chart, start.AddSeconds(120));
            var tokens = Points(snapshot, "TokenPoints");
            Equal("rollback-rate-not-spike", true,
                PointValue(tokens[tokens.Count - 1]) < 250d);
            Equal("rollback-period-increase", 300d,
                Field<double>(snapshot, "CumulativeIncrease"));
        }

        private static void QuotaSmoothingAvoidsVisualBreaks()
        {
            var chart = new TokenRateChart();
            var start = new DateTimeOffset(2026, 7, 26, 11, 30, 5,
                TimeSpan.FromHours(8));
            var reset = start.AddHours(6);
            chart.Capture(start, 1000, 83, 360, reset);
            chart.Capture(start.AddSeconds(30), 1100, 83, 360, reset);
            chart.Capture(start.AddSeconds(60), 1200, 79, 360, reset);
            chart.Capture(start.AddSeconds(90), 1300, 79, 360, reset);

            var snapshot = Snapshot(chart, start.AddSeconds(90));
            var quotas = Points(snapshot, "QuotaPoints");
            Equal("quota-keeps-30s-points", 4, quotas.Count);
            Equal("quota-normal-samples-stay-connected", false,
                PointBreakBefore(quotas[1]));
            Equal("quota-drop-is-smoothed", true,
                PointValue(quotas[2]) > 79d &&
                PointValue(quotas[2]) < 83d);
            Equal("quota-smoothing-converges", true,
                PointValue(quotas[3]) < PointValue(quotas[2]) &&
                PointValue(quotas[3]) > 79d);
            Equal("quota-header-keeps-raw-value", 79d,
                Field<double?>(snapshot, "CurrentQuota").Value);
            Equal("quota-runtime-drop-is-counted", 4d,
                Field<double>(snapshot,
                    "QuotaConsumedDuringRuntime"));
        }

        private static void QuotaConsumptionCountsRuntimeDrops()
        {
            var chart = new TokenRateChart();
            var start = new DateTimeOffset(2026, 7, 26, 11, 45, 5,
                TimeSpan.FromHours(8));
            var reset = start.AddHours(6);
            chart.Capture(start, 1000, 80, 360, reset);
            chart.Capture(start.AddSeconds(30), 1100, 78, 360, reset);
            chart.Capture(start.AddSeconds(60), 1200, 79, 360, reset);
            chart.Capture(start.AddSeconds(90), 1300, 77, 360, reset);
            chart.Capture(start.AddSeconds(120), 1400, 100, 360,
                reset.AddHours(6));
            chart.Capture(start.AddSeconds(150), 1500, 98, 360,
                reset.AddHours(6));

            var snapshot = Snapshot(chart, start.AddSeconds(150));
            Equal("quota-bounce-is-not-double-counted", 5d,
                Field<double>(snapshot,
                    "QuotaConsumedDuringRuntime"));
        }

        private static void QuotaConnectsAcrossMissingSamples()
        {
            var chart = new TokenRateChart();
            var start = new DateTimeOffset(2026, 7, 26, 11, 50, 5,
                TimeSpan.FromHours(8));
            chart.Capture(start, 1000, 83, 360, start.AddHours(6));
            chart.Capture(start.AddSeconds(30), 1100, 82, 360,
                start.AddHours(6));
            chart.CaptureFailure(start.AddMinutes(3));
            chart.Capture(start.AddMinutes(4), 1200, 80, 360,
                start.AddHours(6));

            var points = Points(Snapshot(chart, start.AddMinutes(4)),
                "QuotaPoints");
            Equal("quota-gap-retains-valid-points", 3, points.Count);
            for (var i = 1; i < points.Count; i++)
                Equal("quota-gap-remains-connected-" + i, false,
                    PointBreakBefore(points[i]));
        }

        private static void TokenAxisKeepsPeakNearEightyPercent()
        {
            var method = typeof(TokenRateChart).GetMethod(
                "CalculateRoundedTokenAxisMaximum",
                BindingFlags.Static | BindingFlags.NonPublic);
            var axis = (double)method.Invoke(null,
                new object[] { 785000d });
            Equal("token-axis-rounded", 1000000d, axis);
            var ratio = 785000d / axis;
            Equal("token-peak-near-80-percent", true,
                ratio >= 0.78d && ratio <= 0.81d);

            var compactAxis = (double)method.Invoke(null,
                new object[] { 138000d });
            Equal("token-axis-uses-100k-step", 200000d,
                compactAxis);

            var largerAxis = (double)method.Invoke(null,
                new object[] { 2100000d });
            Equal("larger-token-axis-rounded", 2600000d,
                largerAxis);
            Equal("larger-token-peak-near-80-percent", true,
                2100000d / largerAxis >= 0.80d &&
                2100000d / largerAxis <= 0.82d);
        }

        private static void CumulativeSeriesIsSmoothAndExact()
        {
            var chart = new TokenRateChart();
            var start = new DateTimeOffset(2026, 7, 26, 10, 0, 5,
                TimeSpan.FromHours(8));
            var totals = new long[] { 1000, 1000, 1000, 2000, 2000, 2000 };
            for (var i = 0; i < totals.Length; i++)
                chart.Capture(start.AddSeconds(i * 30), totals[i], 80,
                    360, start.AddHours(6));

            var snapshot = Snapshot(chart, start.AddSeconds(150));
            var points = Points(snapshot, "CumulativePoints");
            Equal("cumulative-smoothing-keeps-points", 6, points.Count);
            Equal("cumulative-smoothing-keeps-start", 0d,
                PointValue(points[0]));
            Equal("cumulative-smoothing-keeps-exact-end", 1000d,
                PointValue(points[points.Count - 1]));
            Equal("cumulative-spike-is-distributed", true,
                PointValue(points[2]) > 0d &&
                PointValue(points[2]) < 1000d);
            for (var i = 1; i < points.Count; i++)
                Equal("cumulative-remains-monotone-" + i, true,
                    PointValue(points[i]) >= PointValue(points[i - 1]));
        }

        private static void CumulativeAxisKeepsPeakNearEightyPercent()
        {
            var method = typeof(TokenRateChart).GetMethod(
                "CalculateRoundedCumulativeAxisMaximum",
                BindingFlags.Static | BindingFlags.NonPublic);
            var axis = (double)method.Invoke(null,
                new object[] { 20410000d });
            Equal("cumulative-axis-rounded", 26000000d, axis);
            Equal("cumulative-peak-near-80-percent", true,
                20410000d / axis >= 0.78d &&
                20410000d / axis <= 0.80d);
        }

        private static void UnitsScaleAutomatically()
        {
            var method = typeof(TokenRateChart).GetMethod("FormatTokenCount",
                BindingFlags.Static | BindingFlags.NonPublic);
            Equal("plain-unit", "999", method.Invoke(null,
                new object[] { 999d }));
            Equal("k-unit", "1.3K", method.Invoke(null,
                new object[] { 1250d }));
            Equal("m-unit", "2.5M", method.Invoke(null,
                new object[] { 2500000d }));
        }

        private static void WheelZoomUsesSharedInMemoryHistory()
        {
            var chart = new TokenRateChart();
            var start = new DateTimeOffset(2026, 7, 26, 0, 0, 5,
                TimeSpan.FromHours(8));
            for (var sample = 0; sample <= 1440; sample++)
                chart.Capture(start.AddSeconds(sample * 30),
                    1000 + sample * 100,
                    90 - sample / 120d, 720, start.AddHours(12));

            Equal("zoom-default-is-2h", 2, chart.DisplayHours);
            var twoHourSnapshot = Snapshot(chart, start.AddHours(12));
            Equal("zoom-snapshot-is-2h", TimeSpan.FromHours(2),
                Field<TimeSpan>(twoHourSnapshot, "DisplayDuration"));

            chart.ToggleMode();
            Equal("zoom-is-shared-across-modes", 2, chart.DisplayHours);
            Equal("partial-wheel-one", false,
                chart.ZoomByWheel(-40, 1000));
            Equal("partial-wheel-two", false,
                chart.ZoomByWheel(-40, 1010));
            Equal("full-wheel-step-to-3h", true,
                chart.ZoomByWheel(-40, 1020));
            Equal("zoom-now-3h", 3, chart.DisplayHours);
            Equal("debounce-prevents-skipping-3h", false,
                chart.ZoomByWheel(-120, 1100));
            Equal("debounce-keeps-3h", 3, chart.DisplayHours);
            var tick = 1250;
            var expected = new[] { 6, 12, 24, 48 };
            for (var i = 0; i < expected.Length; i++)
            {
                Equal("zoom-wheel-down-" + expected[i], true,
                    chart.ZoomByWheel(-120, tick));
                Equal("zoom-now-" + expected[i], expected[i],
                    chart.DisplayHours);
                tick += 250;
            }

            var fortyEightHourSnapshot = Snapshot(chart,
                start.AddHours(12));
            Equal("long-history-retained", true,
                Points(fortyEightHourSnapshot, "TokenPoints").Count >= 1400);
            Equal("zoom-snapshot-is-48h", TimeSpan.FromHours(48),
                Field<TimeSpan>(fortyEightHourSnapshot, "DisplayDuration"));
            Equal("zoom-at-limit-does-not-change", false,
                chart.ZoomByWheel(-120, tick));
        }

        private static void EmbeddedDetailInteractionWorks()
        {
            var sessions = new List<SessionUsage>();
            for (var i = 0; i < 14; i++)
                sessions.Add(new SessionUsage("unreadable-" + i,
                    1000000 - i * 10000,
                    DateTimeOffset.Now.AddMinutes(-i)));
            sessions[0].SessionFilePath =
                @"C:\Users\test\.codex\sessions\session.jsonl";
            var detail = new ProjectDetailChart();
            detail.SetProjects(new List<ProjectUsage>
            {
                new ProjectUsage(@"D:\work\project", "project", sessions)
            });
            var chronological = ProjectDetailChart.SortSessionsByActivity(
                new[]
                {
                    new SessionUsage("high-old", 9000000,
                        DateTimeOffset.Now.AddDays(-2)),
                    new SessionUsage("low-new", 1000,
                        DateTimeOffset.Now)
                });
            Equal("detail-sorts-by-date-not-token", "low-new",
                chronological[0].SessionId);

            using (var bitmap = new Bitmap(292, 183))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                detail.Draw(graphics, new RectangleF(0, 0, 292, 183),
                    ThemeMode.Light, 1f);
                Equal("detail-project-expands",
                    ProjectDetailClickResult.Redraw,
                    detail.HandleClick(new PointF(3, 42)));
                Equal("detail-project-has-no-pointer-hint",
                    ProjectDetailPointerHint.None,
                    detail.PointerHint(new PointF(3, 42)));
                Equal("detail-progress-area-collapses",
                    ProjectDetailClickResult.Redraw,
                    detail.HandleClick(new PointF(120, 55)));
                Equal("detail-name-area-expands",
                    ProjectDetailClickResult.Redraw,
                    detail.HandleClick(new PointF(55, 42)));
                detail.Draw(graphics, new RectangleF(0, 0, 292, 183),
                    ThemeMode.Light, 1f);
                Equal("detail-session-has-no-pointer-hint",
                    ProjectDetailPointerHint.None,
                    detail.PointerHint(new PointF(100, 76)));
                Equal("detail-folder-keeps-pointer-hint",
                    ProjectDetailPointerHint.OpenProjectLocation,
                    detail.PointerHint(new PointF(260, 60)));
                Equal("detail-session-expands",
                    ProjectDetailClickResult.Redraw,
                    detail.HandleClick(new PointF(100, 76)));
                detail.Draw(graphics, new RectangleF(0, 0, 292, 183),
                    ThemeMode.Light, 1f);
                Equal("detail-session-location-keeps-pointer-hint",
                    ProjectDetailPointerHint.OpenSessionLocation,
                    detail.PointerHint(new PointF(250, 125)));
                Equal("detail-close-keeps-pointer-hint",
                    ProjectDetailPointerHint.Close,
                    detail.PointerHint(new PointF(285, 7)));
                Equal("detail-thin-scroll-view-scrolls", true,
                    detail.Scroll(-120));
                Equal("detail-close-hit",
                    ProjectDetailClickResult.Close,
                    detail.HandleClick(new PointF(285, 7)));
            }
        }

        private static void CompactDualChartDraws()
        {
            var chart = new TokenRateChart();
            var start = new DateTimeOffset(2026, 7, 26, 12, 0, 5,
                TimeSpan.FromHours(8));
            for (var i = 0; i <= 12; i++)
                chart.Capture(start.AddSeconds(i * 30), 1000 + i * 500,
                    90 - i, 360, start.AddHours(6));

            using (var bitmap = new Bitmap(234, 177))
            using (var graphics = Graphics.FromImage(bitmap))
                chart.Draw(graphics, new RectangleF(0, 0, 234, 177),
                    ThemeMode.Light, start.AddMinutes(6), 0.8f);
        }

        private static object Snapshot(TokenRateChart chart,
            DateTimeOffset now)
        {
            return typeof(TokenRateChart).GetMethod(
                "BuildRenderSnapshotLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(chart, new object[] { now });
        }

        private static IList Points(object snapshot, string name)
        {
            return (IList)snapshot.GetType().GetField(name).GetValue(snapshot);
        }

        private static double PointValue(object point)
        {
            return (double)point.GetType().GetField("Value").GetValue(point);
        }

        private static DateTimeOffset PointAt(object point)
        {
            return (DateTimeOffset)point.GetType().GetField("At")
                .GetValue(point);
        }

        private static int PointSampleCount(object point)
        {
            return (int)point.GetType().GetField("SampleCount")
                .GetValue(point);
        }

        private static bool PointBreakBefore(object point)
        {
            return (bool)point.GetType().GetField("BreakBefore")
                .GetValue(point);
        }

        private static double PointSpacingSeconds(object left, object right)
        {
            return (PointAt(right) - PointAt(left)).TotalSeconds;
        }

        private static T Field<T>(object source, string name)
        {
            return (T)source.GetType().GetField(name).GetValue(source);
        }

        private static void Equal(string name, object expected, object actual)
        {
            if (Equals(expected, actual)) return;
            failures++;
            Console.WriteLine(name + ": expected " + expected + ", got " +
                actual);
        }
    }
}
