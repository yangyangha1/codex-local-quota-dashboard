using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;

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
                PersistsCompleteIncrementEveryFiveMinutes();
                Console.WriteLine("Running range test");
                ReadsInclusiveDateRange();
                Console.WriteLine("Running interrupted-tail test");
                IgnoresIncompleteTail();
                Console.WriteLine("Running privacy test");
                StoresMetricsWithoutTextContent();
                Console.WriteLine("Running shared-writer test");
                CoordinatesMultipleWritersByTimestamp();
                Console.WriteLine("Running cancellation isolation test");
                CancellingHistoryReadDoesNotBlockWriter();
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

        private static void PersistsCompleteIncrementEveryFiveMinutes()
        {
            WithStore(delegate(HistoryStore store, string path)
            {
                var at = new DateTimeOffset(2026, 8, 4, 9, 15, 5,
                    TimeSpan.FromHours(8));
                store.Record(Snapshot(100, 20, 30, 4), at);
                store.Record(Snapshot(130, 25, 40, 5), at.AddMinutes(1));
                store.Record(Snapshot(160, 35, 50, 6), at.AddMinutes(5));
                var samples = store.ReadAll();
                Equal(2, samples.Count,
                    "same five-minute bucket is coalesced");
                True(samples[0].IsBaseline,
                    "first record is marked as a baseline");
                Equal(0L, samples[0].DeltaInput,
                    "baseline is not counted as a five-minute increment");
                Equal(60L, samples[1].DeltaInput,
                    "next record contains five-minute input increment");
                Equal(15L, samples[1].DeltaOutput,
                    "next record contains five-minute output increment");
                Equal(20L, samples[1].DeltaCached,
                    "cached increment is retained independently");
                Equal(2L, samples[1].DeltaReasoning,
                    "reasoning increment is retained independently");
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
                store.Record(Snapshot(100, 10, 5, 2), new DateTimeOffset(
                    2026, 8, 3, 23, 55, 0, local));
                store.Record(Snapshot(200, 20, 8, 3), new DateTimeOffset(
                    2026, 8, 4, 0, 0, 0, local));
                var selected = store.ReadRange(new DateTimeOffset(
                    2026, 8, 4, 0, 0, 0, local), new DateTimeOffset(
                    2026, 8, 5, 0, 0, 0, local));
                Equal(1, selected.Count, "date range uses local-day bounds");
                True(selected[0].IsBaseline,
                    "new local day starts with a baseline record");
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
                    store.Record(Snapshot(100, 20, 4, 1), DateTimeOffset.Now);
                using (var output = new FileStream(path, FileMode.Append,
                    FileAccess.Write, FileShare.None))
                    output.Write(new byte[11], 0, 11);
                using (var reopened = new HistoryStore(path))
                {
                    Equal(1, reopened.ReadAll().Count,
                        "incomplete tail is ignored after an interrupted write");
                    reopened.Record(Snapshot(200, 40, 8, 2),
                        DateTimeOffset.Now.AddMinutes(5));
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
                    store.Record(Snapshot(123456, 789, 456, 12),
                        DateTimeOffset.Now);
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

        private static void CoordinatesMultipleWritersByTimestamp()
        {
            var folder = Path.Combine(Path.GetTempPath(),
                "CodexHistoryTests-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "history.bin");
            Directory.CreateDirectory(folder);
            try
            {
                var at = new DateTimeOffset(2026, 8, 4, 14, 0, 0,
                    TimeSpan.FromHours(8));
                using (var first = new HistoryStore(path))
                using (var second = new HistoryStore(path))
                {
                    first.Record(Snapshot(100, 20, 10, 2), at);
                    second.Record(Snapshot(130, 25, 12, 3),
                        at.AddMinutes(2));
                    Equal(1, first.ReadAll().Count,
                        "second process skips an existing timestamp");

                    second.Record(Snapshot(160, 35, 18, 5),
                        at.AddMinutes(5));
                    var samples = first.ReadAll();
                    Equal(2, samples.Count,
                        "a later timestamp is appended once");
                    Equal(60L, samples[1].DeltaInput,
                        "later writer derives input from persisted source");
                    Equal(15L, samples[1].DeltaOutput,
                        "later writer derives output from persisted source");
                    Equal(8L, samples[1].DeltaCached,
                        "later writer retains cached increment");
                    Equal(3L, samples[1].DeltaReasoning,
                        "later writer retains reasoning increment");
                }
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        private static void CancellingHistoryReadDoesNotBlockWriter()
        {
            WithStore(delegate(HistoryStore store, string path)
            {
                var at = new DateTimeOffset(2026, 8, 4, 16, 0, 0,
                    TimeSpan.FromHours(8));
                store.Record(Snapshot(100, 20, 10, 2), at);
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    var cancelled = false;
                    try
                    {
                        store.ReadRange(at.Date, at.Date.AddDays(1),
                            cancellation.Token);
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    True(cancelled, "history read observes cancellation");
                }
                store.Record(Snapshot(150, 30, 15, 4), at.AddMinutes(5));
                Equal(2, store.ReadAll().Count,
                    "cancelled history read does not affect writing");
            });
        }

        private static void ChartSupportsWheelAndBrushZoom()
        {
            var chart = new HistoryPanelChart();
            var selectedDate = new DateTime(2026, 8, 1);
            chart.SetDate(selectedDate);
            var from = new DateTimeOffset(selectedDate);
            var samples = new List<HistorySample>();
            for (var index = 0; index < 288; index++)
                samples.Add(new HistorySample(from.AddMinutes(index * 5),
                    1000 + index, 300 + index, 200 + index,
                    30 + index));
            chart.SetSamples(samples, 4096);
            using (var bitmap = new Bitmap(340, 280))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                chart.Draw(graphics, new RectangleF(14, 14, 292, 239),
                    ThemeMode.Dark, 1f);
                var before = chart.ViewDuration;
                var center = new PointF(chart.PlotBounds.Left +
                    chart.PlotBounds.Width / 2f,
                    chart.PlotBounds.Top + chart.PlotBounds.Height / 2f);
                chart.Zoom(120, center);
                var afterWheel = chart.ViewDuration;
                True(afterWheel < before,
                    "mouse wheel zooms the history time axis");
                chart.ResetZoom();
                var start = new PointF(chart.PlotBounds.Left +
                    chart.PlotBounds.Width * .2f, center.Y);
                var end = new PointF(chart.PlotBounds.Left +
                    chart.PlotBounds.Width * .7f, center.Y);
                chart.BeginSelection(start);
                chart.UpdateSelection(end);
                chart.EndSelection(end);
                True(chart.ViewDuration < before,
                    "left-button brush selection zooms to a local range");
            }
        }

        private static UsageSnapshot Snapshot(long input, long output,
            long cached, long reasoning)
        {
            var today = new TokenTotals(input, output, cached, reasoning);
            return new UsageSnapshot(today, today, today, 0,
                DateTimeOffset.Now, new List<QuotaWindow>());
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
