using System;
using System.Collections;
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
            UnitsScaleAutomatically();
            CompactDualChartDraws();
            Console.WriteLine(failures == 0 ? "PASS" : "FAILURES=" + failures);
            return failures == 0 ? 0 : 1;
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
