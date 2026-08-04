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
        public double? RemainingPercent;
        public int WindowMinutes;
        public DateTimeOffset? ResetsAt;
        public double? TokenRatePerMinute;

        public long DeltaTokens { get { return DeltaInput + DeltaOutput; } }

        public HistorySample(DateTimeOffset at, long deltaInput,
            long deltaOutput, long deltaCached, long deltaReasoning,
            bool isBaseline = false, long sourceInput = 0,
            long sourceOutput = 0, long sourceCached = 0,
            long sourceReasoning = 0, double? remainingPercent = null,
            int windowMinutes = 0, DateTimeOffset? resetsAt = null,
            double? tokenRatePerMinute = null)
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
            RemainingPercent = remainingPercent;
            WindowMinutes = windowMinutes;
            ResetsAt = resetsAt;
            TokenRatePerMinute = tokenRatePerMinute;
        }
    }

    internal sealed class PendingHistorySample
    {
        public readonly DateTimeOffset At;
        public readonly TokenTotals Totals;
        public readonly double? RemainingPercent;
        public readonly int WindowMinutes;
        public readonly DateTimeOffset? ResetsAt;

        public PendingHistorySample(DateTimeOffset at, TokenTotals totals,
            double? remainingPercent, int windowMinutes,
            DateTimeOffset? resetsAt)
        {
            At = at;
            Totals = totals;
            RemainingPercent = remainingPercent;
            WindowMinutes = windowMinutes;
            ResetsAt = resetsAt;
        }
    }

    /// <summary>
    /// 每 30 秒缓冲完整数据点，每 5 分钟批量追加；跨进程按时间标签
    /// 去重，不覆盖旧记录。History 读取与常驻写入使用独立文件句柄。
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
        private readonly bool includeCompatibleFiles;
        private readonly List<PendingHistorySample> pending =
            new List<PendingHistorySample>(12);
        private DateTimeOffset? pendingFiveMinuteBucket;
        private DateTime lastCompactionDate;
        private bool disposed;

        public HistoryStore()
            : this(ResolveDefaultStoragePath(), true)
        {
        }

        internal HistoryStore(string path)
            : this(path, false)
        {
        }

        private HistoryStore(string path, bool includeCompatible)
        {
            storagePath = path;
            includeCompatibleFiles = includeCompatible;
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
                    return ReadablePaths().Where(File.Exists)
                        .Sum(path => new FileInfo(path).Length);
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
                var pointAt = FloorToThirtySeconds(capturedAt);
                if (pending.Count > 0 &&
                    pending[pending.Count - 1].At >= pointAt) return;
                var fiveMinuteBucket = FloorToFiveMinutes(pointAt);
                if (pendingFiveMinuteBucket.HasValue &&
                    fiveMinuteBucket != pendingFiveMinuteBucket.Value)
                {
                    if (FlushPendingLocked())
                        pendingFiveMinuteBucket = fiveMinuteBucket;
                }
                if (!pendingFiveMinuteBucket.HasValue)
                    pendingFiveMinuteBucket = fiveMinuteBucket;
                var quota = snapshot.Quotas
                    .OrderBy(value => value.WindowMinutes)
                    .FirstOrDefault();
                pending.Add(new PendingHistorySample(pointAt,
                    snapshot.Today,
                    quota == null ? (double?)null : Math.Max(0d,
                        Math.Min(100d, 100d - quota.UsedPercent)),
                    quota == null ? 0 : quota.WindowMinutes,
                    quota == null ? (DateTimeOffset?)null : quota.ResetsAt));
            }
        }

        internal void FlushPending()
        {
            lock (gate)
            {
                ThrowIfDisposed();
                FlushPendingLocked();
            }
        }

        private bool FlushPendingLocked()
        {
            if (pending.Count == 0) return true;
            using (var mutex = new Mutex(false, mutexName))
            {
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return false;
                    EnsureFile();
                    var previous = ReadLastSample(storagePath);
                    using (var output = new FileStream(storagePath,
                        FileMode.Open, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete, 4096,
                        FileOptions.SequentialScan))
                    {
                        output.Position = output.Length;
                        foreach (var value in pending)
                        {
                            if (previous != null && previous.At >= value.At)
                                continue;
                            var sample = BuildSample(value, previous);
                            var bytes = Encode(sample);
                            output.Write(bytes, 0, bytes.Length);
                            previous = sample;
                        }
                        output.Flush(false);
                    }
                    pending.Clear();
                    if (new FileInfo(storagePath).Length > MaximumFileBytes &&
                        lastCompactionDate != DateTime.Today)
                    {
                        lastCompactionDate = DateTime.Today;
                        Compact(DateTimeOffset.UtcNow);
                    }
                    return true;
                }
                finally
                {
                    if (acquired) mutex.ReleaseMutex();
                }
            }
        }

        private static HistorySample BuildSample(PendingHistorySample value,
            HistorySample previous)
        {
            var baseline = previous == null ||
                previous.At.ToLocalTime().Date != value.At.ToLocalTime().Date ||
                HasCounterRollback(value.Totals, previous);
            var delta = baseline ? new TokenTotals() : new TokenTotals(
                value.Totals.Input - previous.SourceInput,
                value.Totals.Output - previous.SourceOutput,
                value.Totals.Cached - previous.SourceCached,
                value.Totals.Reasoning - previous.SourceReasoning);
            return new HistorySample(value.At,
                Math.Max(0L, delta.Input), Math.Max(0L, delta.Output),
                Math.Max(0L, delta.Cached), Math.Max(0L, delta.Reasoning),
                baseline, value.Totals.Input, value.Totals.Output,
                value.Totals.Cached, value.Totals.Reasoning,
                value.RemainingPercent, value.WindowMinutes, value.ResetsAt,
                baseline || previous == null ? (double?)null :
                    Math.Max(0d, delta.Total / Math.Max(.5d,
                        (value.At - previous.At).TotalMinutes)));
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
            return ReadAllFiles(cancellationToken).Where(sample =>
                sample.At >= fromUtc && sample.At < toUtc).ToList();
        }

        public List<HistorySample> ReadAll()
        {
            ThrowIfDisposed();
            return ReadAllFiles(CancellationToken.None);
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

        private static HistorySample ReadLastSample(string path)
        {
            using (var input = new FileStream(path, FileMode.Open,
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

        private static List<HistorySample> ReadFile(string path,
            CancellationToken cancellationToken)
        {
            var output = new List<HistorySample>();
            if (!File.Exists(path)) return output;
            using (var input = new FileStream(path, FileMode.Open,
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

        private List<HistorySample> ReadAllFiles(
            CancellationToken cancellationToken)
        {
            var merged = new Dictionary<long, HistorySample>();
            foreach (var path in ReadablePaths())
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var sample in ReadFile(path, cancellationToken))
                    merged[sample.At.UtcDateTime.Ticks] = sample;
            }
            return merged.Values.OrderBy(value => value.At).ToList();
        }

        private void Compact(DateTimeOffset nowUtc)
        {
            var all = ReadFile(storagePath, CancellationToken.None);
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
                second.SourceCached, second.SourceReasoning,
                second.RemainingPercent ?? first.RemainingPercent,
                second.RemainingPercent.HasValue
                    ? second.WindowMinutes : first.WindowMinutes,
                second.RemainingPercent.HasValue
                    ? second.ResetsAt : first.ResetsAt,
                second.TokenRatePerMinute ?? first.TokenRatePerMinute);
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
                var hasQuota = sample.RemainingPercent.HasValue;
                var hasRate = sample.TokenRatePerMinute.HasValue;
                writer.Write((sample.IsBaseline ? 1 : 0) |
                    (hasQuota ? 2 : 0) | (hasRate ? 4 : 0));
                writer.Write(30);
                var quotaBasisPoints = hasQuota
                    ? (ushort)Math.Max(0, Math.Min(10000,
                        (int)Math.Round(sample.RemainingPercent.Value * 100d)))
                    : (ushort)0;
                var window = hasQuota
                    ? (ushort)Math.Max(0, Math.Min(ushort.MaxValue,
                        sample.WindowMinutes)) : (ushort)0;
                writer.Write(quotaBasisPoints);
                writer.Write(window);
                writer.Write(hasQuota && sample.ResetsAt.HasValue
                    ? ToUnixSeconds(sample.ResetsAt.Value) : 0u);
                writer.Write(hasRate ? (float)Math.Max(0d,
                    sample.TokenRatePerMinute.Value) : 0f);
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
                    reader.ReadInt32();
                    var quotaBasisPoints = reader.ReadUInt16();
                    var windowMinutes = reader.ReadUInt16();
                    var resetSeconds = reader.ReadUInt32();
                    var tokenRate = reader.ReadSingle();
                    if (ticks < DateTime.MinValue.Ticks ||
                        ticks > DateTime.MaxValue.Ticks) return false;
                    var at = new DateTimeOffset(new DateTime(ticks,
                        DateTimeKind.Utc));
                    sample = new HistorySample(at, deltaInput, deltaOutput,
                        deltaCached, deltaReasoning, (flags & 1) != 0,
                        sourceInput, sourceOutput, sourceCached,
                        sourceReasoning,
                        (flags & 2) != 0
                            ? (double?)quotaBasisPoints / 100d : null,
                        (flags & 2) != 0 ? windowMinutes : 0,
                        (flags & 2) != 0 && resetSeconds != 0
                            ? (DateTimeOffset?)FromUnixSeconds(resetSeconds)
                            : null,
                        (flags & 4) != 0 ? (double?)tokenRate : null);
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

        private static uint ToUnixSeconds(DateTimeOffset value)
        {
            var seconds = (value.ToUniversalTime().UtcDateTime.Ticks -
                new DateTime(1970, 1, 1, 0, 0, 0,
                    DateTimeKind.Utc).Ticks) / TimeSpan.TicksPerSecond;
            return (uint)Math.Max(1L, Math.Min(uint.MaxValue, seconds));
        }

        private static DateTimeOffset FromUnixSeconds(uint value)
        {
            return new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0,
                DateTimeKind.Utc).AddSeconds(value));
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

        private static DateTimeOffset FloorToThirtySeconds(
            DateTimeOffset value)
        {
            var second = value.Second < 30 ? 0 : 30;
            return new DateTimeOffset(value.Year, value.Month, value.Day,
                value.Hour, value.Minute, second, value.Offset)
                .ToUniversalTime();
        }

        private static DateTimeOffset FloorToFiveMinutes(
            DateTimeOffset value)
        {
            var local = value.ToLocalTime();
            var minute = local.Minute - local.Minute % 5;
            return new DateTimeOffset(local.Year, local.Month, local.Day,
                local.Hour, minute, 0, local.Offset).ToUniversalTime();
        }

        private static string ResolveDefaultStoragePath()
        {
            var folder = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "CodexLocalDashboard");
            return ResolveStoragePath(folder);
        }

        internal static string ResolveStoragePath(string folder)
        {
            var prefix = "codex-usage-history-from";
            var suffix = "-v1.5.0.bin";
            var preferred = Path.Combine(folder, prefix +
                DateTime.Today.ToString("yyyyMMdd",
                    CultureInfo.InvariantCulture) + suffix);
            if (!Directory.Exists(folder)) return preferred;
            var candidates = new List<string>();
            try
            {
                foreach (var path in Directory.EnumerateFiles(folder,
                    prefix + "*-v1.5.0*.bin",
                    SearchOption.TopDirectoryOnly))
                {
                    if (IsCompatibleFile(path)) candidates.Add(path);
                }
            }
            catch { }
            if (candidates.Count > 0)
                return candidates.OrderBy(value => value,
                    StringComparer.OrdinalIgnoreCase).First();
            if (!File.Exists(preferred)) return preferred;
            var next = 2;
            var candidate = Path.Combine(folder,
                Path.GetFileNameWithoutExtension(preferred) + "-" + next +
                ".bin");
            while (File.Exists(candidate))
            {
                next++;
                candidate = Path.Combine(folder,
                    Path.GetFileNameWithoutExtension(preferred) + "-" +
                    next + ".bin");
            }
            return candidate;
        }

        private IEnumerable<string> ReadablePaths()
        {
            if (!includeCompatibleFiles) return new[] { storagePath };
            var paths = new List<string>();
            var folder = StorageDirectory;
            if (Directory.Exists(folder))
            {
                try
                {
                    paths.AddRange(Directory.EnumerateFiles(folder,
                        "usage-history-v*.bin",
                        SearchOption.TopDirectoryOnly)
                        .Where(IsCompatibleFile));
                    paths.AddRange(Directory.EnumerateFiles(folder,
                        "codex-usage-history-from*-v1.5.0*.bin",
                        SearchOption.TopDirectoryOnly)
                        .Where(IsCompatibleFile));
                }
                catch { }
            }
            if (!paths.Any(path => string.Equals(path, storagePath,
                StringComparison.OrdinalIgnoreCase))) paths.Add(storagePath);
            return paths.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path.IndexOf("codex-usage-history-from",
                    StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsCompatibleFile(string path)
        {
            try
            {
                using (var input = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return HasValidHeader(input);
            }
            catch { return false; }
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
            lock (gate)
            {
                if (disposed) return;
                try { FlushPendingLocked(); }
                catch { }
                disposed = true;
            }
        }
    }
}
