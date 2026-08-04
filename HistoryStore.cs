using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CodexLocalDashboard
{
    internal sealed class HistorySample
    {
        public DateTimeOffset At;
        public long DeltaInput;
        public long DeltaOutput;
        public long DeltaCached;
        public long DeltaReasoning;
        public long SourceInput;
        public long SourceOutput;
        public long SourceCached;
        public long SourceReasoning;
        public bool IsBaseline;

        public long DeltaTokens { get { return DeltaInput + DeltaOutput; } }

        public HistorySample(DateTimeOffset at, long deltaInput,
            long deltaOutput, long deltaCached, long deltaReasoning,
            bool isBaseline = false, long sourceInput = 0,
            long sourceOutput = 0, long sourceCached = 0,
            long sourceReasoning = 0)
        {
            At = at;
            DeltaInput = deltaInput;
            DeltaOutput = deltaOutput;
            DeltaCached = deltaCached;
            DeltaReasoning = deltaReasoning;
            IsBaseline = isBaseline;
            SourceInput = sourceInput;
            SourceOutput = sourceOutput;
            SourceCached = sourceCached;
            SourceReasoning = sourceReasoning;
        }
    }

    /// <summary>
    /// 多进程安全的单文件增量历史库。每个五分钟时间标签只追加一次，
    /// 不覆盖旧记录；History 全量读取与常驻写入使用独立文件句柄。
    /// </summary>
    internal sealed class HistoryStore : IDisposable
    {
        internal const long MaximumFileBytes = 8L * 1024L * 1024L;
        internal const int RecordSize = 96;
        private const int HeaderSize = 16;
        private const int FormatVersion = 3;
        private const long CompactTargetBytes = 7L * 1024L * 1024L;
        private static readonly byte[] Magic =
            { (byte)'C', (byte)'L', (byte)'D', (byte)'H',
              (byte)'S', (byte)'T', (byte)'0', (byte)'3' };

        private readonly object gate = new object();
        private readonly string storagePath;
        private readonly string mutexName;
        private DateTimeOffset? lastHandledBucket;
        private DateTime lastCompactionDate;
        private bool disposed;

        public HistoryStore()
            : this(Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "CodexLocalDashboard", "usage-history-v3.bin"))
        {
        }

        internal HistoryStore(string path)
        {
            storagePath = path;
            mutexName = "Local\\CodexLocalDashboard-History-" +
                PathHash(Path.GetFullPath(path));
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
                try
                {
                    return File.Exists(storagePath)
                        ? new FileInfo(storagePath).Length : 0L;
                }
                catch { return 0L; }
            }
        }

        public void Record(UsageSnapshot snapshot, DateTimeOffset capturedAt)
        {
            if (snapshot == null) return;
            lock (gate)
            {
                ThrowIfDisposed();
                var bucketMinute = capturedAt.Minute - capturedAt.Minute % 5;
                var bucket = new DateTimeOffset(capturedAt.Year,
                    capturedAt.Month, capturedAt.Day, capturedAt.Hour,
                    bucketMinute, 0, capturedAt.Offset).ToUniversalTime();
                // 扫描器可以高频刷新，但正常情况下每个实例在一个
                // 五分钟槽位内只触碰历史文件一次。
                if (lastHandledBucket.HasValue &&
                    bucket <= lastHandledBucket.Value) return;
                using (var mutex = new Mutex(false, mutexName))
                {
                    var acquired = false;
                    try
                    {
                        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) return;
                        EnsureFile();
                        var previous = ReadLastSample();
                        // 时间标签是唯一键。多实例遇到已经落盘的槽位时
                        // 直接跳过，绝不覆盖或重复修改旧记录。
                        if (previous != null && previous.At >= bucket)
                        {
                            lastHandledBucket = bucket;
                            return;
                        }

                        var baseline = previous == null ||
                            previous.At.ToLocalTime().Date !=
                                capturedAt.LocalDateTime.Date ||
                            HasCounterRollback(snapshot.Today, previous);
                        var delta = baseline ? new TokenTotals() :
                            new TokenTotals(
                                snapshot.Today.Input - previous.SourceInput,
                                snapshot.Today.Output - previous.SourceOutput,
                                snapshot.Today.Cached - previous.SourceCached,
                                snapshot.Today.Reasoning -
                                    previous.SourceReasoning);
                        var sample = new HistorySample(bucket,
                            Math.Max(0L, delta.Input),
                            Math.Max(0L, delta.Output),
                            Math.Max(0L, delta.Cached),
                            Math.Max(0L, delta.Reasoning), baseline,
                            snapshot.Today.Input, snapshot.Today.Output,
                            snapshot.Today.Cached, snapshot.Today.Reasoning);
                        Append(sample);
                        lastHandledBucket = bucket;
                        if (FileSize > MaximumFileBytes &&
                            lastCompactionDate != DateTime.Today)
                        {
                            lastCompactionDate = DateTime.Today;
                            Compact(capturedAt.ToUniversalTime());
                        }
                    }
                    finally
                    {
                        if (acquired) mutex.ReleaseMutex();
                    }
                }
            }
        }

        public List<HistorySample> ReadRange(DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive)
        {
            return ReadRange(fromInclusive, toExclusive,
                CancellationToken.None);
        }

        public List<HistorySample> ReadRange(DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var fromUtc = fromInclusive.ToUniversalTime();
            var toUtc = toExclusive.ToUniversalTime();
            return ReadAllFile(cancellationToken).Where(sample =>
                sample.At >= fromUtc && sample.At < toUtc).ToList();
        }

        public List<HistorySample> ReadAll()
        {
            ThrowIfDisposed();
            return ReadAllFile(CancellationToken.None);
        }

        private void EnsureFile()
        {
            var folder = StorageDirectory;
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            if (!File.Exists(storagePath))
            {
                using (var output = new FileStream(storagePath,
                    FileMode.CreateNew, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete))
                    WriteHeader(output);
                return;
            }
            using (var file = new FileStream(storagePath, FileMode.Open,
                FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                if (!HasValidHeader(file))
                    throw new InvalidDataException("历史数据文件头无效。");
                var completeLength = HeaderSize + Math.Max(0L,
                    (file.Length - HeaderSize) / RecordSize) * RecordSize;
                if (file.Length != completeLength)
                    file.SetLength(completeLength);
            }
        }

        private HistorySample ReadLastSample()
        {
            using (var input = new FileStream(storagePath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (!HasValidHeader(input) ||
                    input.Length < HeaderSize + RecordSize) return null;
                var count = (input.Length - HeaderSize) / RecordSize;
                input.Position = HeaderSize + (count - 1) * RecordSize;
                var buffer = new byte[RecordSize];
                if (!ReadExactly(input, buffer, 0, buffer.Length)) return null;
                HistorySample sample;
                return TryDecode(buffer, out sample) ? sample : null;
            }
        }

        private void Append(HistorySample sample)
        {
            using (var output = new FileStream(storagePath, FileMode.Open,
                FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.SequentialScan))
            {
                output.Position = output.Length;
                var bytes = Encode(sample);
                output.Write(bytes, 0, bytes.Length);
                // 只刷新托管缓冲，不使用 WriteThrough 或强制磁盘同步。
                output.Flush(false);
            }
        }

        private List<HistorySample> ReadAllFile(
            CancellationToken cancellationToken)
        {
            var output = new List<HistorySample>();
            if (!File.Exists(storagePath)) return output;
            using (var input = new FileStream(storagePath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (!HasValidHeader(input)) return output;
                var buffer = new byte[RecordSize];
                while (ReadExactly(input, buffer, 0, buffer.Length))
                {
                    cancellationToken.ThrowIfCancellationRequested();
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

        private void Compact(DateTimeOffset nowUtc)
        {
            var all = ReadAllFile(CancellationToken.None);
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
                var key = minutes / bucketMinutes;
                HistorySample existing;
                buckets[key] = buckets.TryGetValue(key, out existing)
                    ? Merge(existing, sample) : sample;
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
            try
            {
                using (var output = new FileStream(temporaryPath,
                    FileMode.Create, FileAccess.Write, FileShare.Read,
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
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch { }
            }
        }

        private static HistorySample Merge(HistorySample first,
            HistorySample second)
        {
            return new HistorySample(second.At,
                first.DeltaInput + second.DeltaInput,
                first.DeltaOutput + second.DeltaOutput,
                first.DeltaCached + second.DeltaCached,
                first.DeltaReasoning + second.DeltaReasoning,
                first.IsBaseline && second.IsBaseline,
                second.SourceInput, second.SourceOutput,
                second.SourceCached, second.SourceReasoning);
        }

        private static bool HasCounterRollback(TokenTotals current,
            HistorySample previous)
        {
            return current.Input < previous.SourceInput ||
                current.Output < previous.SourceOutput ||
                current.Cached < previous.SourceCached ||
                current.Reasoning < previous.SourceReasoning;
        }

        private static byte[] Encode(HistorySample sample)
        {
            var buffer = new byte[RecordSize];
            using (var memory = new MemoryStream(buffer))
            using (var writer = new BinaryWriter(memory))
            {
                writer.Write(sample.At.UtcDateTime.Ticks);
                writer.Write(sample.DeltaInput);
                writer.Write(sample.DeltaOutput);
                writer.Write(sample.DeltaCached);
                writer.Write(sample.DeltaReasoning);
                writer.Write(sample.SourceInput);
                writer.Write(sample.SourceOutput);
                writer.Write(sample.SourceCached);
                writer.Write(sample.SourceReasoning);
                writer.Write(sample.IsBaseline ? 1 : 0);
                writer.Write(5);
                writer.Write(0);
                writer.Flush();
                memory.Position = RecordSize - 4;
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
                    var deltaInput = reader.ReadInt64();
                    var deltaOutput = reader.ReadInt64();
                    var deltaCached = reader.ReadInt64();
                    var deltaReasoning = reader.ReadInt64();
                    var sourceInput = reader.ReadInt64();
                    var sourceOutput = reader.ReadInt64();
                    var sourceCached = reader.ReadInt64();
                    var sourceReasoning = reader.ReadInt64();
                    var flags = reader.ReadInt32();
                    if (ticks < DateTime.MinValue.Ticks ||
                        ticks > DateTime.MaxValue.Ticks) return false;
                    var at = new DateTimeOffset(new DateTime(ticks,
                        DateTimeKind.Utc));
                    sample = new HistorySample(at, deltaInput, deltaOutput,
                        deltaCached, deltaReasoning, (flags & 1) != 0,
                        sourceInput, sourceOutput, sourceCached,
                        sourceReasoning);
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
                for (var index = offset; index < offset + count; index++)
                {
                    hash ^= buffer[index];
                    hash *= 16777619u;
                }
                return hash;
            }
        }

        private static void WriteHeader(Stream output)
        {
            output.Position = 0;
            output.Write(Magic, 0, Magic.Length);
            using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
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
            for (var index = 0; index < Magic.Length; index++)
                if (header[index] != Magic[index]) return false;
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

        private static string PathHash(string path)
        {
            var bytes = Encoding.UTF8.GetBytes(path.ToUpperInvariant());
            return Checksum(bytes, 0, bytes.Length).ToString("X8",
                CultureInfo.InvariantCulture);
        }

        private void ThrowIfDisposed()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("HistoryStore");
            }
        }

        public void Dispose()
        {
            lock (gate) disposed = true;
        }
    }
}
