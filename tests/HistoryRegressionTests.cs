using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace CodexLocalDashboard
{
    internal static class HistoryRegressionTests
    {
        private static int failures;

        [STAThread]
        public static int Main()
        {
            try
            {
                Console.WriteLine("Running persistence test");
                PersistsOneRecordPerMinute();
                Console.WriteLine("Running range test");
                ReadsInclusiveDateRange();
                Console.WriteLine("Running interrupted-tail test");
                IgnoresIncompleteTail();
                Console.WriteLine("Running privacy test");
                StoresMetricsWithoutTextContent();
                Console.WriteLine("Running chart interaction test");
                ChartSupportsWheelAndBrushZoom();
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine("UNHANDLED: " +
                    ex.GetType().FullName + " · " + ex.Message);
            }
            Console.WriteLine(failures == 0
                ? "History regression tests passed."
                : failures + " history regression test(s) failed.");
            return failures == 0 ? 0 : 1;
        }

        private static void PersistsOneRecordPerMinute()
        {
            WithStore(delegate(HistoryStore store, string path)
            {
                var at = new DateTimeOffset(2026, 8, 4, 9, 15, 5,
                    TimeSpan.FromHours(8));
                store.Record(Snapshot(100, 75), at);
                store.Record(Snapshot(200, 74), at.AddSeconds(30));
                store.Record(Snapshot(300, 73), at.AddMinutes(1));
                var samples = store.ReadAll();
                Equal(2, samples.Count, "same-minute records are coalesced");
                Equal(100L, samples[0].TodayTokens,
                    "first minute value is retained");
                Equal(300L, samples[1].TodayTokens,
                    "next minute value is persisted");
                True(new FileInfo(path).Length == 16 +
                    HistoryStore.RecordSize * 2,
                    "history file uses fixed-size records");
            });
        }

        private static void ReadsInclusiveDateRange()
        {
            WithStore(delegate(HistoryStore store, string path)
            {
                var local = TimeSpan.FromHours(8);
                store.Record(Snapshot(100, 80), new DateTimeOffset(
                    2026, 8, 3, 23, 59, 0, local));
                store.Record(Snapshot(200, 79), new DateTimeOffset(
                    2026, 8, 4, 0, 1, 0, local));
                var selected = store.ReadRange(new DateTimeOffset(
                    2026, 8, 4, 0, 0, 0, local), new DateTimeOffset(
                    2026, 8, 5, 0, 0, 0, local));
                Equal(1, selected.Count, "date range uses local-day bounds");
                Equal(200L, selected[0].TodayTokens,
                    "date range returns the matching day");
            });
        }

        private static void IgnoresIncompleteTail()
        {
            var folder = Path.Combine(Path.GetTempPath(),
                "CodexHistoryTests-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "history.bin");
            Directory.CreateDirectory(folder);
            try
            {
                using (var store = new HistoryStore(path))
                    store.Record(Snapshot(100, 75), DateTimeOffset.Now);
                using (var output = new FileStream(path, FileMode.Append,
                    FileAccess.Write, FileShare.None))
                    output.Write(new byte[11], 0, 11);
                using (var reopened = new HistoryStore(path))
                {
                    Equal(1, reopened.ReadAll().Count,
                        "incomplete tail is ignored after an interrupted write");
                    reopened.Record(Snapshot(200, 74),
                        DateTimeOffset.Now.AddMinutes(1));
                    Equal(2, reopened.ReadAll().Count,
                        "new records remain aligned after tail recovery");
                }
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        private static void StoresMetricsWithoutTextContent()
        {
            var folder = Path.Combine(Path.GetTempPath(),
                "CodexHistoryTests-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "history.bin");
            Directory.CreateDirectory(folder);
            try
            {
                using (var store = new HistoryStore(path))
                    store.Record(Snapshot(123456, 68), DateTimeOffset.Now);
                var bytes = File.ReadAllBytes(path);
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                True(text.IndexOf("prompt", StringComparison.OrdinalIgnoreCase)
                    < 0, "history does not contain prompt text");
                True(text.IndexOf("session", StringComparison.OrdinalIgnoreCase)
                    < 0, "history does not contain session names");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        private static void ChartSupportsWheelAndBrushZoom()
        {
            using (var chart = new HistoryChartControl
            {
                Size = new System.Drawing.Size(700, 300)
            })
            {
                var from = new DateTimeOffset(2026, 8, 1, 0, 0, 0,
                    TimeSpan.FromHours(8));
                var to = from.AddDays(4);
                var samples = new List<HistorySample>();
                for (var index = 0; index <= 96; index++)
                    samples.Add(new HistorySample(from.AddHours(index),
                        index * 1000L, index * 2000L, index * 3000L,
                        90d - index * .25d, 10080, to, 5));
                chart.SetSamples(samples, from, to);
                var before = ViewSpan(chart);
                InvokeMouse(chart, "OnMouseWheel", new MouseEventArgs(
                    MouseButtons.None, 0, 350, 150, 120));
                var afterWheel = ViewSpan(chart);
                True(afterWheel < before,
                    "mouse wheel zooms the history time axis");
                chart.ResetZoom();
                InvokeMouse(chart, "OnMouseDown", new MouseEventArgs(
                    MouseButtons.Left, 1, 160, 140, 0));
                InvokeMouse(chart, "OnMouseMove", new MouseEventArgs(
                    MouseButtons.Left, 0, 430, 140, 0));
                InvokeMouse(chart, "OnMouseUp", new MouseEventArgs(
                    MouseButtons.Left, 1, 430, 140, 0));
                True(ViewSpan(chart) < before,
                    "left-button brush selection zooms to a local range");
            }
        }

        private static TimeSpan ViewSpan(HistoryChartControl chart)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var from = (DateTimeOffset)typeof(HistoryChartControl)
                .GetField("viewFrom", flags).GetValue(chart);
            var to = (DateTimeOffset)typeof(HistoryChartControl)
                .GetField("viewTo", flags).GetValue(chart);
            return to - from;
        }

        private static void InvokeMouse(HistoryChartControl chart,
            string method, MouseEventArgs args)
        {
            typeof(HistoryChartControl).GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(chart, new object[] { args });
        }

        private static UsageSnapshot Snapshot(long today, double remaining)
        {
            return new UsageSnapshot(new TokenTotals(today, 0),
                new TokenTotals(today * 2, 0),
                new TokenTotals(today * 3, 0), 4, DateTimeOffset.Now,
                new List<QuotaWindow>
                {
                    new QuotaWindow(10080, 100d - remaining,
                        DateTimeOffset.Now.AddDays(2))
                });
        }

        private static void WithStore(Action<HistoryStore, string> action)
        {
            var folder = Path.Combine(Path.GetTempPath(),
                "CodexHistoryTests-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "history.bin");
            Directory.CreateDirectory(folder);
            try
            {
                using (var store = new HistoryStore(path)) action(store, path);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        private static void True(bool value, string name)
        {
            if (value) return;
            failures++;
            Console.Error.WriteLine("FAIL: " + name);
        }

        private static void Equal<T>(T expected, T actual, string name)
        {
            True(EqualityComparer<T>.Default.Equals(expected, actual),
                name + " (expected " + expected + ", actual " + actual + ")");
        }
    }
}
