using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
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
                BuffersThirtySecondPointsAndFlushesEveryFiveMinutes();
                Console.WriteLine("Running range test");
                ReadsInclusiveDateRange();
                Console.WriteLine("Running compatible quota extension test");
                PersistsQuotaWithoutChangingRecordFormat();
                Console.WriteLine("Running compatible file selection test");
                ReusesCompatibleFileAndAvoidsIncompatibleFile();
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
                Console.WriteLine("Running realtime replay test");
                HistoricalReplayUsesRealtimeQuotaCalculation();
                Console.WriteLine("Running sparse history quota test");
                HistoricalReplayCarriesQuotaAcrossSparseGaps();
                Console.WriteLine("Running seven-day status strip test");
                SupportsSevenDayStatusStrip();
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

        private static void BuffersThirtySecondPointsAndFlushesEveryFiveMinutes()
        {
            WithStore(delegate(HistoryStore store, string path)
            {
                var at = new DateTimeOffset(2026, 8, 4, 9, 15, 0,
                    TimeSpan.FromHours(8));
                for (var index = 0; index < 10; index++)
                    store.Record(Snapshot(100 + index * 10,
                        20 + index * 2, 30 + index * 3, 4 + index),
                        at.AddSeconds(index * 30));
                True(!File.Exists(path),
                    "points stay in memory until the five-minute boundary");
                store.Record(Snapshot(200, 40, 60, 14), at.AddMinutes(5));
                var samples = store.ReadAll();
                Equal(10, samples.Count,
                    "five-minute flush writes every thirty-second point");
                True(samples[0].IsBaseline,
                    "first record is marked as a baseline");
                Equal(0L, samples[0].DeltaInput,
                    "baseline is not counted as an increment");
                Equal(10L, samples[1].DeltaInput,
                    "each thirty-second input increment is retained");
                Equal(2L, samples[1].DeltaOutput,
                    "each thirty-second output increment is retained");
                True(samples[1].TokenRatePerMinute.HasValue &&
                    Math.Abs(samples[1].TokenRatePerMinute.Value - 24d) < .01d,
                    "thirty-second token rate is stored");
                store.FlushPending();
                Equal(11, store.ReadAll().Count,
                    "the active bucket is flushed without dropping points");
                True(new FileInfo(path).Length == 16 +
                    HistoryStore.RecordSize * 11,
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
                store.FlushPending();
                var selected = store.ReadRange(new DateTimeOffset(
                    2026, 8, 4, 0, 0, 0, local), new DateTimeOffset(
                    2026, 8, 5, 0, 0, 0, local));
                Equal(1, selected.Count, "date range uses local-day bounds");
                True(selected[0].IsBaseline,
                    "new local day starts with a baseline record");
            });
        }

        private static void PersistsQuotaWithoutChangingRecordFormat()
        {
            WithStore(delegate(HistoryStore store, string path)
            {
                var at = new DateTimeOffset(2026, 8, 4, 10, 0, 0,
                    TimeSpan.FromHours(8));
                var reset = new DateTimeOffset(2026, 8, 10, 18, 0, 0,
                    TimeSpan.FromHours(8));
                store.Record(Snapshot(100, 20, 10, 2, 62.35d,
                    10080, reset), at);
                store.FlushPending();
                var sample = store.ReadAll().Single();
                True(sample.RemainingPercent.HasValue,
                    "quota is stored in compatible reserved bytes");
                True(Math.Abs(sample.RemainingPercent.Value - 62.35d) < .01d,
                    "remaining quota round-trips");
                Equal(10080, sample.WindowMinutes,
                    "quota window round-trips");
                Equal(reset.ToUniversalTime(),
                    sample.ResetsAt.Value.ToUniversalTime(),
                    "quota reset time round-trips");
                Equal(16L + HistoryStore.RecordSize,
                    new FileInfo(path).Length,
                    "quota extension keeps the v3 fixed record size");
            });
        }

        private static void ReusesCompatibleFileAndAvoidsIncompatibleFile()
        {
            var compatibleFolder = Path.Combine(Path.GetTempPath(),
                "CodexHistoryTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(compatibleFolder);
            try
            {
                var compatiblePath = Path.Combine(compatibleFolder,
                    "codex-usage-history-from" +
                    DateTime.Today.ToString("yyyyMMdd") + "-v1.5.0.bin");
                using (var store = new HistoryStore(compatiblePath))
                {
                    store.Record(Snapshot(100, 20, 4, 1),
                        DateTimeOffset.Now);
                    store.FlushPending();
                }
                Equal(compatiblePath,
                    HistoryStore.ResolveStoragePath(compatibleFolder),
                    "compatible v1.5.0 file is reused for incremental appends");
            }
            finally
            {
                try { Directory.Delete(compatibleFolder, true); } catch { }
            }

            var incompatibleFolder = Path.Combine(Path.GetTempPath(),
                "CodexHistoryTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(incompatibleFolder);
            try
            {
                var preferred = "codex-usage-history-from" +
                    DateTime.Today.ToString("yyyyMMdd") + "-v1.5.0.bin";
                var incompatiblePath = Path.Combine(incompatibleFolder,
                    preferred);
                File.WriteAllBytes(incompatiblePath, new byte[16]);
                Equal(Path.Combine(incompatibleFolder,
                        Path.GetFileNameWithoutExtension(preferred) +
                        "-2.bin"),
                    HistoryStore.ResolveStoragePath(incompatibleFolder),
                    "incompatible file is preserved and a new path is chosen");
            }
            finally
            {
                try { Directory.Delete(incompatibleFolder, true); } catch { }
            }
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
                {
                    store.Record(Snapshot(100, 20, 4, 1), DateTimeOffset.Now);
                    store.FlushPending();
                }
                using (var output = new FileStream(path, FileMode.Append,
                    FileAccess.Write, FileShare.None))
                    output.Write(new byte[11], 0, 11);
                using (var reopened = new HistoryStore(path))
                {
                    Equal(1, reopened.ReadAll().Count,
                        "incomplete tail is ignored after an interrupted write");
                    reopened.Record(Snapshot(200, 40, 8, 2),
                        DateTimeOffset.Now.AddMinutes(5));
                    reopened.FlushPending();
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
                {
                    store.Record(Snapshot(123456, 789, 456, 12),
                        DateTimeOffset.Now);
                    store.FlushPending();
                }
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
                    first.FlushPending();
                    second.FlushPending();
                    Equal(2, first.ReadAll().Count,
                        "different process timestamps are both retained");

                    second.Record(Snapshot(160, 35, 18, 5),
                        at.AddMinutes(5));
                    second.FlushPending();
                    var samples = first.ReadAll();
                    Equal(3, samples.Count,
                        "a later timestamp is appended once");
                    Equal(30L, samples[2].DeltaInput,
                        "later writer derives input from persisted source");
                    Equal(10L, samples[2].DeltaOutput,
                        "later writer derives output from persisted source");
                    Equal(6L, samples[2].DeltaCached,
                        "later writer retains cached increment");
                    Equal(2L, samples[2].DeltaReasoning,
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
                store.FlushPending();
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
                store.FlushPending();
                Equal(2, store.ReadAll().Count,
                    "cancelled history read does not affect writing");
            });
        }

        private static void ChartSupportsWheelAndBrushZoom()
        {
            var chart = new HistoryPanelChart();
            var selectedDate = new DateTime(2026, 8, 1);
            chart.SetDate(selectedDate);
            chart.SetAvailableDates(new[] { selectedDate,
                selectedDate.AddDays(-2) });
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
                Equal(HistoryPanelClickResult.None,
                    chart.HandleClick(new PointF(246f, 24f)),
                    "history chart does not reserve a storage action area");
                Equal(HistoryPanelClickResult.OpenStorage,
                    chart.HandleClick(new PointF(160f, 24f)),
                    "history title opens the storage directory");
                var selectedBounds = chart.DayBounds(5);
                Equal(HistoryPanelClickResult.SelectDate,
                    chart.HandleClick(new PointF(selectedBounds.Left + 2f,
                        selectedBounds.Top + 2f)),
                    "blue status square selects its day immediately");
                var unavailableBounds = chart.DayBounds(4);
                Equal(HistoryPanelClickResult.None,
                    chart.HandleClick(new PointF(unavailableBounds.Left + 2f,
                        unavailableBounds.Top + 2f)),
                    "gray status square is disabled");
                var before = chart.ViewDuration;
                var center = new PointF(chart.PlotBounds.Left +
                    chart.PlotBounds.Width / 2f,
                    chart.PlotBounds.Top + chart.PlotBounds.Height / 2f);
                var start = new PointF(chart.PlotBounds.Left +
                    chart.PlotBounds.Width * .2f, center.Y);
                var end = new PointF(chart.PlotBounds.Left +
                    chart.PlotBounds.Width * .7f, center.Y);
                chart.BeginSelection(start);
                chart.UpdateSelection(end);
                chart.EndSelection(end);
                True(chart.ViewDuration < before,
                    "left-button brush selection zooms to a local range");
                chart.ZoomByWheel(120, 1000);
                Equal(12, chart.DisplayHours,
                    "history wheel reuses the realtime zoom levels");
                Equal(TimeSpan.FromHours(12), chart.ViewDuration,
                    "wheel clears the brushed range");
                chart.ZoomByWheel(-120, 1300);
                chart.ZoomByWheel(-120, 1600);
                Equal(48, chart.DisplayHours,
                    "history wheel reaches the realtime 48-hour level");
                Equal(new DateTimeOffset(selectedDate).AddDays(-1),
                    chart.RequiredReadFrom,
                    "48-hour view requests the previous day automatically");
            }
        }

        private static void SupportsSevenDayStatusStrip()
        {
            var chart = new HistoryPanelChart();
            chart.SetDate(new DateTime(2026, 8, 4));
            var before = chart.VisibleWeekStart;
            True(chart.ShiftWeek(-1), "left arrow moves by seven days");
            Equal(before.AddDays(-7), chart.VisibleWeekStart,
                "status strip advances in seven-day units");
            True(chart.ShiftWeek(1), "right arrow returns one week");
            Equal(before, chart.VisibleWeekStart,
                "right arrow returns to the current week");
        }

        private static void HistoricalReplayUsesRealtimeQuotaCalculation()
        {
            var panel = new HistoryPanelChart();
            var day = new DateTime(2026, 8, 4);
            panel.SetDate(day);
            var from = new DateTimeOffset(day);
            var reset = from.AddDays(7);
            var values = new List<HistorySample>();
            for (var index = 0; index <= 120; index++)
                values.Add(new HistorySample(from.AddSeconds(index * 30),
                    index == 0 ? 0 : 10, index == 0 ? 0 : 2, 0, 0,
                    index == 0, 1000 + index * 10L,
                    200 + index * 2L, 0, 0,
                    90d - index / 12d, 10080, reset));
            panel.SetSamples(values, 0);
            var chart = typeof(HistoryPanelChart).GetField("chart",
                BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(panel);
            var method = chart.GetType().GetMethod(
                "BuildRenderSnapshotLocked",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var snapshot = method.Invoke(chart,
                new object[] { from.AddHours(1) });
            var consumed = (double)snapshot.GetType().GetField(
                "QuotaConsumedDuringRuntime").GetValue(snapshot);
            True(consumed > 9.9d,
                "history replays the realtime quota-consumption algorithm");
            var historical = (bool)snapshot.GetType().GetField(
                "Historical").GetValue(snapshot);
            True(historical,
                "history render state can hide the meaningless rate label");
        }

        private static void HistoricalReplayCarriesQuotaAcrossSparseGaps()
        {
            var panel = new HistoryPanelChart();
            var day = new DateTime(2026, 8, 4);
            panel.SetDate(day);
            var from = new DateTimeOffset(day);
            var reset = from.AddDays(7);
            panel.SetSamples(new List<HistorySample>
            {
                new HistorySample(from.AddHours(19), 0, 0, 0, 0, true,
                    1000, 100, 0, 0, 15d, 10080, reset),
                new HistorySample(from.AddHours(19).AddMinutes(5), 0, 0,
                    0, 0, false, 1100, 110, 0, 0, 15d, 10080, reset),
                new HistorySample(from.AddHours(20).AddMinutes(30), 0, 0,
                    0, 0, false, 1200, 120, 0, 0, 2d, 10080, reset)
            }, 0);
            var chart = typeof(HistoryPanelChart).GetField("chart",
                BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(panel);
            var method = chart.GetType().GetMethod(
                "BuildRenderSnapshotLocked",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var snapshot = method.Invoke(chart,
                new object[] { from.AddDays(1) });
            var consumed = (double)snapshot.GetType().GetField(
                "QuotaConsumedDuringRuntime").GetValue(snapshot);
            True(consumed > 12.9d,
                "historical quota consumption crosses sparse write gaps");
        }

        private static UsageSnapshot Snapshot(long input, long output,
            long cached, long reasoning, double? remaining = null,
            int windowMinutes = 0, DateTimeOffset? resetsAt = null)
        {
            var today = new TokenTotals(input, output, cached, reasoning);
            var quotas = new List<QuotaWindow>();
            if (remaining.HasValue)
                quotas.Add(new QuotaWindow(windowMinutes,
                    100d - remaining.Value, resetsAt));
            return new UsageSnapshot(today, today, today, 0,
                DateTimeOffset.Now, quotas);
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
