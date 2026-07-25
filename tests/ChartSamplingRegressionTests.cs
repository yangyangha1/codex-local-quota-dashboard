using System;
using System.Collections;
using System.Reflection;

namespace CodexLocalDashboard
{
    internal static class ChartSamplingRegressionTests
    {
        private static int failures;

        public static int Main()
        {
            FixedBucketsReplaceDenseSamples();
            SameDayRollbackIsHeld();
            MidnightRollbackStartsNewSegment();
            Console.WriteLine(failures == 0 ? "PASS" : "FAILURES=" + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void FixedBucketsReplaceDenseSamples()
        {
            var chart = new TokenRateChart();
            var start = new DateTimeOffset(2026, 7, 25, 10, 0, 5,
                TimeSpan.FromHours(8));
            chart.Capture(start, 100, null, 0, null);
            chart.Capture(start.AddSeconds(10), 120, null, 0, null);
            Equal("same-minute-replaces-point", 1, TokenPoints(chart).Count);
            Equal("same-minute-keeps-latest", 120d,
                PointValue(TokenPoints(chart)[0]));

            chart.Capture(start.AddSeconds(55), 130, null, 0, null);
            Equal("next-minute-adds-point", 2, TokenPoints(chart).Count);
        }

        private static void SameDayRollbackIsHeld()
        {
            var chart = new TokenRateChart();
            var start = new DateTimeOffset(2026, 7, 25, 10, 0, 5,
                TimeSpan.FromHours(8));
            chart.Capture(start, 1000, null, 0, null);
            chart.Capture(start.AddMinutes(1), 1200, null, 0, null);
            chart.Capture(start.AddMinutes(2), 20, null, 0, null);

            var points = TokenPoints(chart);
            Equal("rollback-does-not-drop", 1200d,
                PointValue(points[points.Count - 1]));
            Equal("rollback-does-not-break", false,
                PointBreakBefore(points[points.Count - 1]));
        }

        private static void MidnightRollbackStartsNewSegment()
        {
            var chart = new TokenRateChart();
            var start = new DateTimeOffset(2026, 7, 25, 23, 59, 30,
                TimeSpan.FromHours(8));
            chart.Capture(start, 9000, null, 0, null);
            chart.Capture(start.AddMinutes(1), 25, null, 0, null);

            var points = TokenPoints(chart);
            Equal("midnight-adds-point", 2, points.Count);
            Equal("midnight-accepts-reset", 25d,
                PointValue(points[points.Count - 1]));
            Equal("midnight-breaks-line", true,
                PointBreakBefore(points[points.Count - 1]));
        }

        private static IList TokenPoints(TokenRateChart chart)
        {
            return (IList)typeof(TokenRateChart).GetField("tokenPoints",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(chart);
        }

        private static double PointValue(object point)
        {
            return (double)point.GetType().GetField("Value").GetValue(point);
        }

        private static bool PointBreakBefore(object point)
        {
            return (bool)point.GetType().GetField("BreakBefore").GetValue(point);
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
