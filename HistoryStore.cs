using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexLocalDashboard
{
    internal sealed class HistorySample
    {
        public DateTimeOffset At;
        public long TodayTokens;
        public long WeekTokens;
        public long MonthTokens;
        public double? RemainingPercent;
        public int WindowMinutes;
        public DateTimeOffset? ResetsAt;
        public int WeekSessions;

        public HistorySample(DateTimeOffset at, long todayTokens,
            long weekTokens, long monthTokens, double? remainingPercent,
            int windowMinutes, DateTimeOffset? resetsAt, int weekSessions)
        {
            At = at;
            TodayTokens = todayTokens;
            WeekTokens = weekTokens;
            MonthTokens = monthTokens;
            RemainingPercent = remainingPercent;
            WindowMinutes = windowMinutes;
            ResetsAt = resetsAt;
            WeekSessions = weekSessions;
        }
    }

    /// <summary>
    /// 单文件、定长记录的本地历史库。复用现有扫描节奏，每分钟最多写入一条；
    /// 不保存提示词、回复、会话名称或文件路径。
    /// </summary>
    internal sealed class HistoryStore : IDisposable
    {
        internal const long MaximumFileBytes = 8L * 1024L * 1024L;
        internal const int RecordSize = 64;
        private const int HeaderSize = 16;
        private const int FormatVersion = 1;
        private const long CompactTargetBytes = 7L * 1024L * 1024L;
        private static readonly byte[] Magic =
            { (byte)'C', (byte)'L', (byte)'D', (byte)'H',
              (byte)'S', (byte)'T', (byte)'0', (byte)'1' };

        private readonly object gate = new object();
        private readonly string storagePath;
        private FileStream stream;
        private DateTimeOffset? lastSavedAt;
        private DateTime lastCompactionDate;
        private bool disposed;

        public HistoryStore()
            : this(Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "CodexLocalDashboard", "usage-history-v1.bin"))
        {
        }

        internal HistoryStore(string path)
        {
            storagePath = path;
            TryReadLastTimestamp();
        }

        public string StoragePath { get { return storagePath; } }
        public string StorageDirectory
        {
            get { return Path.GetDirectoryName(storagePath); }
        }

        public long FileSize
        {
            get
            {
                lock (gate)
                {
                    if (stream != null) return stream.Length;
                    try
                    {
                        return File.Exists(storagePath)
                            ? new FileInfo(storagePath).Length : 0L;
                    }
                    catch { return 0L; }
                }
            }
        }

        public void Record(UsageSnapshot snapshot, DateTimeOffset capturedAt)
        {
            if (snapshot == null) return;
            lock (gate)
            {
                ThrowIfDisposed();
                var minute = new DateTimeOffset(
                    capturedAt.Year, capturedAt.Month, capturedAt.Day,
                    capturedAt.Hour, capturedAt.Minute, 0,
                    capturedAt.Offset).ToUniversalTime();
                if (lastSavedAt.HasValue &&
                    minute <= lastSavedAt.Value.ToUniversalTime())
                    return;

                var quota = snapshot.Quotas
                    .OrderBy(value => value.WindowMinutes)
                    .FirstOrDefault();
                var sample = new HistorySample(minute,
                    snapshot.Today.Total, snapshot.Week.Total,
                    snapshot.Month.Total,
                    quota == null ? (double?)null :
                        Math.Max(0d, 100d - quota.UsedPercent),
                    quota == null ? 0 : quota.WindowMinutes,
                    quota == null ? (DateTimeOffset?)null : quota.ResetsAt,
                    snapshot.WeekSessions);

                EnsureWriter();
                var bytes = Encode(sample);
                stream.Write(bytes, 0, bytes.Length);
                // Flush the managed buffer once per minute. The operating system
                // remains free to batch physical writes, avoiding write-through I/O.
                stream.Flush(false);
                lastSavedAt = minute;

                if (stream.Length > MaximumFileBytes &&
                    lastCompactionDate != DateTime.Today)
                {
                    lastCompactionDate = DateTime.Today;
                    CompactLocked(capturedAt.ToUniversalTime());
                }
            }
        }

        public List<HistorySample> ReadRange(DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (stream != null) stream.Flush(false);
                var all = ReadAllLocked();
                var fromUtc = fromInclusive.ToUniversalTime();
                var toUtc = toExclusive.ToUniversalTime();
                return all.Where(sample => sample.At >= fromUtc &&
                    sample.At < toUtc).ToList();
            }
        }

        public List<HistorySample> ReadAll()
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (stream != null) stream.Flush(false);
                return ReadAllLocked();
            }
        }

        private void EnsureWriter()
        {
            if (stream != null) return;
            var folder = StorageDirectory;
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            var exists = File.Exists(storagePath);
            stream = new FileStream(storagePath,
                exists ? FileMode.Open : FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.Read,
                4096, FileOptions.SequentialScan);
            if (!exists || stream.Length == 0)
                WriteHeader(stream);
            else if (!HasValidHeader(stream))
            {
                stream.Dispose();
                stream = null;
                throw new InvalidDataException("历史数据文件头无效。");
            }
            var completeLength = HeaderSize + Math.Max(0L,
                (stream.Length - HeaderSize) / RecordSize) * RecordSize;
            if (stream.Length != completeLength)
                stream.SetLength(completeLength);
            stream.Position = stream.Length;
        }

        private void TryReadLastTimestamp()
        {
            try
            {
                if (!File.Exists(storagePath)) return;
                using (var input = new FileStream(storagePath,
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (!HasValidHeader(input) ||
                        input.Length < HeaderSize + RecordSize)
                        return;
                    var complete = (input.Length - HeaderSize) / RecordSize;
                    input.Position = HeaderSize + (complete - 1) * RecordSize;
                    var buffer = new byte[RecordSize];
                    if (ReadExactly(input, buffer, 0, buffer.Length))
                    {
                        HistorySample sample;
                        if (TryDecode(buffer, out sample))
                            lastSavedAt = sample.At;
                    }
                }
            }
            catch { }
        }

        private List<HistorySample> ReadAllLocked()
        {
            var output = new List<HistorySample>();
            if (!File.Exists(storagePath)) return output;
            using (var input = new FileStream(storagePath,
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (!HasValidHeader(input)) return output;
                var buffer = new byte[RecordSize];
                while (ReadExactly(input, buffer, 0, buffer.Length))
                {
                    HistorySample sample;
                    if (TryDecode(buffer, out sample)) output.Add(sample);
                }
            }
            output.Sort(delegate(HistorySample left, HistorySample right)
            {
                return left.At.CompareTo(right.At);
            });
            return output;
        }

        private void CompactLocked(DateTimeOffset nowUtc)
        {
            var all = ReadAllLocked();
            if (all.Count == 0) return;
            var sevenDaysAgo = nowUtc.AddDays(-7);
            var ninetyDaysAgo = nowUtc.AddDays(-90);
            var selected = new List<HistorySample>(all.Count);
            var buckets = new Dictionary<long, HistorySample>();
            foreach (var sample in all)
            {
                if (sample.At >= sevenDaysAgo)
                {
                    selected.Add(sample);
                    continue;
                }
                var minutes = sample.At.UtcDateTime.Ticks /
                    TimeSpan.TicksPerMinute;
                var bucketMinutes = sample.At >= ninetyDaysAgo ? 15L : 60L;
                buckets[minutes / bucketMinutes] = sample;
            }
            selected.AddRange(buckets.Values);
            selected.Sort(delegate(HistorySample left, HistorySample right)
            {
                return left.At.CompareTo(right.At);
            });
            var maximumRecords = (int)((CompactTargetBytes - HeaderSize) /
                RecordSize);
            if (selected.Count > maximumRecords)
                selected.RemoveRange(0, selected.Count - maximumRecords);

            var temporaryPath = storagePath + ".tmp";
            if (stream != null)
            {
                stream.Flush(false);
                stream.Dispose();
                stream = null;
            }
            try
            {
                using (var output = new FileStream(temporaryPath,
                    FileMode.Create, FileAccess.Write, FileShare.None,
                    4096, FileOptions.SequentialScan))
                {
                    WriteHeader(output);
                    foreach (var sample in selected)
                    {
                        var bytes = Encode(sample);
                        output.Write(bytes, 0, bytes.Length);
                    }
                    output.Flush(false);
                }
                File.Replace(temporaryPath, storagePath, null, true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch { }
                EnsureWriter();
            }
        }

        private static byte[] Encode(HistorySample sample)
        {
            var buffer = new byte[RecordSize];
            using (var memory = new MemoryStream(buffer))
            using (var writer = new BinaryWriter(memory))
            {
                writer.Write(sample.At.UtcDateTime.Ticks);
                writer.Write(sample.TodayTokens);
                writer.Write(sample.WeekTokens);
                writer.Write(sample.MonthTokens);
                writer.Write(sample.RemainingPercent ?? 0d);
                writer.Write(sample.WindowMinutes);
                writer.Write(sample.ResetsAt.HasValue
                    ? sample.ResetsAt.Value.UtcDateTime.Ticks : 0L);
                writer.Write(sample.WeekSessions);
                writer.Write(sample.RemainingPercent.HasValue ? 1 : 0);
                writer.Flush();
                writer.Write(Checksum(buffer, 0, RecordSize - 4));
            }
            return buffer;
        }

        private static bool TryDecode(byte[] buffer, out HistorySample sample)
        {
            sample = null;
            if (buffer == null || buffer.Length != RecordSize) return false;
            var expected = BitConverter.ToUInt32(buffer, RecordSize - 4);
            if (expected != Checksum(buffer, 0, RecordSize - 4)) return false;
            try
            {
                using (var memory = new MemoryStream(buffer, false))
                using (var reader = new BinaryReader(memory))
                {
                    var ticks = reader.ReadInt64();
                    var today = reader.ReadInt64();
                    var week = reader.ReadInt64();
                    var month = reader.ReadInt64();
                    var remaining = reader.ReadDouble();
                    var window = reader.ReadInt32();
                    var resetTicks = reader.ReadInt64();
                    var sessions = reader.ReadInt32();
                    var flags = reader.ReadInt32();
                    if (ticks < DateTime.MinValue.Ticks ||
                        ticks > DateTime.MaxValue.Ticks) return false;
                    var at = new DateTimeOffset(
                        new DateTime(ticks, DateTimeKind.Utc));
                    DateTimeOffset? reset = resetTicks > 0
                        ? new DateTimeOffset(new DateTime(resetTicks,
                            DateTimeKind.Utc)) : (DateTimeOffset?)null;
                    sample = new HistorySample(at, today, week, month,
                        (flags & 1) != 0 ? remaining : (double?)null,
                        window, reset, sessions);
                    return true;
                }
            }
            catch { return false; }
        }

        private static uint Checksum(byte[] buffer, int offset, int count)
        {
            unchecked
            {
                var hash = 2166136261u;
                for (var i = offset; i < offset + count; i++)
                {
                    hash ^= buffer[i];
                    hash *= 16777619u;
                }
                return hash;
            }
        }

        private static void WriteHeader(Stream output)
        {
            output.Position = 0;
            output.Write(Magic, 0, Magic.Length);
            using (var writer = new BinaryWriter(output,
                System.Text.Encoding.UTF8, true))
            {
                writer.Write(FormatVersion);
                writer.Write(RecordSize);
                writer.Flush();
            }
        }

        private static bool HasValidHeader(Stream input)
        {
            if (input.Length < HeaderSize) return false;
            input.Position = 0;
            var header = new byte[HeaderSize];
            if (!ReadExactly(input, header, 0, header.Length)) return false;
            for (var i = 0; i < Magic.Length; i++)
                if (header[i] != Magic[i]) return false;
            return BitConverter.ToInt32(header, 8) == FormatVersion &&
                BitConverter.ToInt32(header, 12) == RecordSize;
        }

        private static bool ReadExactly(Stream input, byte[] buffer,
            int offset, int count)
        {
            var read = 0;
            while (read < count)
            {
                var current = input.Read(buffer, offset + read, count - read);
                if (current <= 0) return false;
                read += current;
            }
            return true;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("HistoryStore");
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (stream != null)
                {
                    try { stream.Flush(false); }
                    finally { stream.Dispose(); stream = null; }
                }
            }
        }
    }
}
