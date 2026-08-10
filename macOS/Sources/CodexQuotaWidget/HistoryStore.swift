import Darwin
import Foundation

struct HistorySample: Equatable, Sendable {
    let at: Date
    let deltaInput: Int64
    let deltaOutput: Int64
    let deltaCached: Int64
    let deltaReasoning: Int64
    let sourceInput: Int64
    let sourceOutput: Int64
    let sourceCached: Int64
    let sourceReasoning: Int64
    let isBaseline: Bool
    let remainingPercent: Double?
    let windowMinutes: Int
    let resetsAt: Date?
    let tokenRatePerMinute: Double?

    var deltaTokens: Int64 { deltaInput &+ deltaOutput }
}

private struct PendingHistorySample: Sendable {
    let at: Date
    let totals: TokenTotals
    let remainingPercent: Double?
    let windowMinutes: Int
    let resetsAt: Date?
}

/// Persistent, privacy-minimal history storage.  The on-disk header and
/// 96-byte records are deliberately compatible with the original v1.5.0
/// Windows dashboard.  No prompt text, message text, project path, or session
/// name is ever written here.
final class HistoryStore: @unchecked Sendable {
    static let maximumFileBytes: Int64 = 8 * 1024 * 1024
    static let recordSize = 96
    private static let headerSize = 16
    private static let formatVersion: Int32 = 3
    private static let compactTargetBytes: Int64 = 7 * 1024 * 1024
    private static let magic = Data("CLDHST03".utf8)
    private static let dotNetUnixEpochTicks: Int64 = 621_355_968_000_000_000

    private let lock = NSLock()
    private let fileManager = FileManager.default
    let storagePath: URL
    let includeCompatibleFiles: Bool
    private var pending = [PendingHistorySample]()
    private var pendingFiveMinuteBucket: Date?
    private var lastCompactionDay: Date?
    private var disposed = false

    init(path: URL? = nil, includeCompatibleFiles: Bool = true) {
        if let path {
            storagePath = path
            self.includeCompatibleFiles = includeCompatibleFiles
        } else {
            let folder = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("CodexLocalDashboard", isDirectory: true)
            storagePath = Self.resolveStoragePath(in: folder)
            self.includeCompatibleFiles = true
        }
    }

    var storageDirectory: URL { storagePath.deletingLastPathComponent() }

    var fileSize: Int64 {
        readablePaths().reduce(0) { total, path in
            let size = (try? path.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
            return total &+ Int64(size)
        }
    }

    func record(_ snapshot: UsageSnapshot, capturedAt: Date) {
        lock.lock()
        defer { lock.unlock() }
        guard !disposed else { return }

        let pointAt = Self.floorToThirtySeconds(capturedAt)
        if let previous = pending.last, previous.at >= pointAt { return }
        let fiveMinuteBucket = Self.floorToFiveMinutes(pointAt)
        if let previousBucket = pendingFiveMinuteBucket, fiveMinuteBucket != previousBucket {
            // Preserve the current capture if storage is temporarily busy.  It
            // joins the queued batch and will be retried on a later refresh,
            // matching the original dashboard's no-drop buffering behavior.
            if flushPendingLocked() { pendingFiveMinuteBucket = fiveMinuteBucket }
        }
        if pendingFiveMinuteBucket == nil { pendingFiveMinuteBucket = fiveMinuteBucket }

        let quota = snapshot.primaryQuota
        pending.append(PendingHistorySample(
            at: pointAt,
            totals: snapshot.today,
            remainingPercent: quota?.remainingPercent,
            windowMinutes: quota?.windowMinutes ?? 0,
            resetsAt: quota?.resetsAt
        ))
    }

    @discardableResult
    func flushPending() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard !disposed else { return false }
        return flushPendingLocked()
    }

    func readAll(cancellationCheck: () -> Bool = { false }) throws -> [HistorySample] {
        guard !isDisposed else { throw HistoryStoreError.disposed }
        return try readAllFiles(cancellationCheck: cancellationCheck)
    }

    func readRange(
        fromInclusive: Date,
        toExclusive: Date,
        cancellationCheck: () -> Bool = { false }
    ) throws -> [HistorySample] {
        guard !isDisposed else { throw HistoryStoreError.disposed }
        return try readAllFiles(cancellationCheck: cancellationCheck).filter {
            $0.at >= fromInclusive && $0.at < toExclusive
        }
    }

    func dispose() {
        lock.lock()
        defer { lock.unlock() }
        guard !disposed else { return }
        _ = flushPendingLocked()
        disposed = true
    }

    deinit { dispose() }

    private var isDisposed: Bool {
        lock.lock()
        defer { lock.unlock() }
        return disposed
    }

    @discardableResult
    private func flushPendingLocked() -> Bool {
        guard !pending.isEmpty else { return true }
        do {
            try withExclusiveFileLock {
                try ensureFile()
                var previous = try readLastSample(at: storagePath)
                let output = try FileHandle(forWritingTo: storagePath)
                defer { try? output.close() }
                try output.seekToEnd()

                for value in pending {
                    if let previous, previous.at >= value.at { continue }
                    let sample = Self.buildSample(value, previous: previous)
                    output.write(Self.encode(sample))
                    previous = sample
                }
                try output.synchronize()
                pending.removeAll(keepingCapacity: true)

                let length = (try? storagePath.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
                let today = Date().localDay
                if Int64(length) > Self.maximumFileBytes && lastCompactionDay != today {
                    lastCompactionDay = today
                    try compact(now: Date())
                }
            }
            return true
        } catch {
            // Live data remains usable even if Application Support is briefly
            // unavailable.  Pending samples stay queued for the next flush.
            return false
        }
    }

    private func withExclusiveFileLock<T>(_ body: () throws -> T) throws -> T {
        try fileManager.createDirectory(at: storageDirectory, withIntermediateDirectories: true)
        let lockPath = storagePath.appendingPathExtension("lock").path
        let descriptor = open(lockPath, O_CREAT | O_RDWR, S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else { throw HistoryStoreError.lockUnavailable }
        defer { close(descriptor) }
        guard flock(descriptor, LOCK_EX) == 0 else { throw HistoryStoreError.lockUnavailable }
        defer { flock(descriptor, LOCK_UN) }
        return try body()
    }

    private func ensureFile() throws {
        try fileManager.createDirectory(at: storageDirectory, withIntermediateDirectories: true)
        guard fileManager.fileExists(atPath: storagePath.path) else {
            var header = Self.magic
            header.appendLittleEndian(Self.formatVersion)
            header.appendLittleEndian(Int32(Self.recordSize))
            try header.write(to: storagePath, options: .atomic)
            return
        }

        let handle = try FileHandle(forUpdating: storagePath)
        defer { try? handle.close() }
        let data = try handle.readToEnd() ?? Data()
        guard Self.hasValidHeader(data) else { throw HistoryStoreError.invalidHeader }
        let completeLength = Self.headerSize + max(0, (data.count - Self.headerSize) / Self.recordSize) * Self.recordSize
        if data.count != completeLength {
            try handle.truncate(atOffset: UInt64(completeLength))
            try handle.synchronize()
        }
    }

    private func readLastSample(at path: URL) throws -> HistorySample? {
        let data = try Data(contentsOf: path, options: .mappedIfSafe)
        guard Self.hasValidHeader(data), data.count >= Self.headerSize + Self.recordSize else { return nil }
        let offset = Self.headerSize + ((data.count - Self.headerSize) / Self.recordSize - 1) * Self.recordSize
        return Self.decode(data.subdata(in: offset..<(offset + Self.recordSize)))
    }

    private func readAllFiles(cancellationCheck: () -> Bool) throws -> [HistorySample] {
        var merged = [Int64: HistorySample]()
        for path in readablePaths() {
            if cancellationCheck() { throw CancellationError() }
            for sample in try Self.readFile(path, cancellationCheck: cancellationCheck) {
                merged[Self.ticks(for: sample.at)] = sample
            }
        }
        return merged.values.sorted { $0.at < $1.at }
    }

    private static func readFile(_ path: URL, cancellationCheck: () -> Bool) throws -> [HistorySample] {
        guard FileManager.default.fileExists(atPath: path.path) else { return [] }
        let data = try Data(contentsOf: path, options: .mappedIfSafe)
        guard hasValidHeader(data) else { return [] }
        var output = [HistorySample]()
        var offset = headerSize
        while offset + recordSize <= data.count {
            if cancellationCheck() { throw CancellationError() }
            if let sample = decode(data.subdata(in: offset..<(offset + recordSize))) {
                output.append(sample)
            }
            offset += recordSize
        }
        return output.sorted { $0.at < $1.at }
    }

    private func compact(now: Date) throws {
        let all = try Self.readFile(storagePath, cancellationCheck: { false })
        guard !all.isEmpty else { return }

        let sevenDaysAgo = now.addingTimeInterval(-7 * 24 * 60 * 60)
        let ninetyDaysAgo = now.addingTimeInterval(-90 * 24 * 60 * 60)
        var selected = [HistorySample]()
        var buckets = [Int64: HistorySample]()
        for sample in all {
            if sample.at >= sevenDaysAgo {
                selected.append(sample)
                continue
            }
            let minutes = Int64(sample.at.timeIntervalSince1970 / 60)
            let bucketMinutes: Int64 = sample.at >= ninetyDaysAgo ? 15 : 60
            let key = minutes / bucketMinutes
            buckets[key] = buckets[key].map { Self.merge($0, sample) } ?? sample
        }
        selected.append(contentsOf: buckets.values)
        selected.sort { $0.at < $1.at }
        let maximumRecords = Int((Self.compactTargetBytes - Int64(Self.headerSize)) / Int64(Self.recordSize))
        if selected.count > maximumRecords { selected.removeFirst(selected.count - maximumRecords) }

        let temporary = storagePath.appendingPathExtension("tmp")
        defer { try? fileManager.removeItem(at: temporary) }
        var output = Self.magic
        output.appendLittleEndian(Self.formatVersion)
        output.appendLittleEndian(Int32(Self.recordSize))
        for sample in selected { output.append(Self.encode(sample)) }
        try output.write(to: temporary, options: .atomic)
        _ = try fileManager.replaceItemAt(storagePath, withItemAt: temporary)
    }

    private static func buildSample(_ value: PendingHistorySample, previous: HistorySample?) -> HistorySample {
        let changedDay = previous.map { $0.at.localDay != value.at.localDay } ?? true
        let rollback = previous.map {
            value.totals.input < $0.sourceInput || value.totals.output < $0.sourceOutput ||
                value.totals.cached < $0.sourceCached || value.totals.reasoning < $0.sourceReasoning
        } ?? false
        let baseline = previous == nil || changedDay || rollback
        let delta: TokenTotals
        if let previous, !baseline {
            delta = TokenTotals(
                input: max(0, value.totals.input - previous.sourceInput),
                output: max(0, value.totals.output - previous.sourceOutput),
                cached: max(0, value.totals.cached - previous.sourceCached),
                reasoning: max(0, value.totals.reasoning - previous.sourceReasoning)
            )
        } else {
            delta = TokenTotals()
        }
        let rate: Double?
        if let previous, !baseline {
            let minutes = max(0.5, value.at.timeIntervalSince(previous.at) / 60)
            rate = max(0, Double(delta.total) / minutes)
        } else {
            rate = nil
        }
        return HistorySample(
            at: value.at,
            deltaInput: delta.input,
            deltaOutput: delta.output,
            deltaCached: delta.cached,
            deltaReasoning: delta.reasoning,
            sourceInput: value.totals.input,
            sourceOutput: value.totals.output,
            sourceCached: value.totals.cached,
            sourceReasoning: value.totals.reasoning,
            isBaseline: baseline,
            remainingPercent: value.remainingPercent,
            windowMinutes: value.windowMinutes,
            resetsAt: value.resetsAt,
            tokenRatePerMinute: rate
        )
    }

    private static func merge(_ first: HistorySample, _ second: HistorySample) -> HistorySample {
        let hasQuota = second.remainingPercent != nil
        return HistorySample(
            at: second.at,
            deltaInput: first.deltaInput &+ second.deltaInput,
            deltaOutput: first.deltaOutput &+ second.deltaOutput,
            deltaCached: first.deltaCached &+ second.deltaCached,
            deltaReasoning: first.deltaReasoning &+ second.deltaReasoning,
            sourceInput: second.sourceInput,
            sourceOutput: second.sourceOutput,
            sourceCached: second.sourceCached,
            sourceReasoning: second.sourceReasoning,
            isBaseline: first.isBaseline && second.isBaseline,
            remainingPercent: second.remainingPercent ?? first.remainingPercent,
            windowMinutes: hasQuota ? second.windowMinutes : first.windowMinutes,
            resetsAt: hasQuota ? second.resetsAt : first.resetsAt,
            tokenRatePerMinute: second.tokenRatePerMinute ?? first.tokenRatePerMinute
        )
    }

    private static func encode(_ sample: HistorySample) -> Data {
        var data = Data()
        data.reserveCapacity(recordSize)
        data.appendLittleEndian(ticks(for: sample.at))
        data.appendLittleEndian(sample.deltaInput)
        data.appendLittleEndian(sample.deltaOutput)
        data.appendLittleEndian(sample.deltaCached)
        data.appendLittleEndian(sample.deltaReasoning)
        data.appendLittleEndian(sample.sourceInput)
        data.appendLittleEndian(sample.sourceOutput)
        data.appendLittleEndian(sample.sourceCached)
        data.appendLittleEndian(sample.sourceReasoning)
        var flags: Int32 = sample.isBaseline ? 1 : 0
        if sample.remainingPercent != nil { flags |= 2 }
        if sample.tokenRatePerMinute != nil { flags |= 4 }
        data.appendLittleEndian(flags)
        data.appendLittleEndian(Int32(30))
        let quota = UInt16(max(0, min(10_000, Int(((sample.remainingPercent ?? 0) * 100).rounded()))))
        data.appendLittleEndian(quota)
        data.appendLittleEndian(UInt16(max(0, min(Int(UInt16.max), sample.remainingPercent == nil ? 0 : sample.windowMinutes))))
        data.appendLittleEndian(unixSeconds(for: sample.resetsAt))
        data.appendLittleEndian(Float(sample.tokenRatePerMinute ?? 0).bitPattern)
        data.appendLittleEndian(checksum(data))
        precondition(data.count == recordSize)
        return data
    }

    private static func decode(_ data: Data) -> HistorySample? {
        guard data.count == recordSize,
              let expected: UInt32 = data.readLittleEndian(at: recordSize - 4),
              expected == checksum(data.prefix(recordSize - 4))
        else { return nil }

        var offset = 0
        guard let ticks: Int64 = data.readLittleEndian(at: offset) else { return nil }
        offset += 8
        guard let deltaInput: Int64 = data.readLittleEndian(at: offset) else { return nil }
        offset += 8
        guard let deltaOutput: Int64 = data.readLittleEndian(at: offset) else { return nil }
        offset += 8
        guard let deltaCached: Int64 = data.readLittleEndian(at: offset) else { return nil }
        offset += 8
        guard let deltaReasoning: Int64 = data.readLittleEndian(at: offset) else { return nil }
        offset += 8
        guard let sourceInput: Int64 = data.readLittleEndian(at: offset) else { return nil }
        offset += 8
        guard let sourceOutput: Int64 = data.readLittleEndian(at: offset) else { return nil }
        offset += 8
        guard let sourceCached: Int64 = data.readLittleEndian(at: offset) else { return nil }
        offset += 8
        guard let sourceReasoning: Int64 = data.readLittleEndian(at: offset) else { return nil }
        offset += 8
        guard let flags: Int32 = data.readLittleEndian(at: offset) else { return nil }
        offset += 4
        guard let _: Int32 = data.readLittleEndian(at: offset) else { return nil }
        offset += 4
        guard let basisPoints: UInt16 = data.readLittleEndian(at: offset) else { return nil }
        offset += 2
        guard let window: UInt16 = data.readLittleEndian(at: offset) else { return nil }
        offset += 2
        guard let resetSeconds: UInt32 = data.readLittleEndian(at: offset) else { return nil }
        offset += 4
        guard let rateBits: UInt32 = data.readLittleEndian(at: offset) else { return nil }

        let seconds = Double(ticks - dotNetUnixEpochTicks) / 10_000_000
        guard seconds.isFinite, abs(seconds) < 315_537_897_600 else { return nil }
        let hasQuota = flags & 2 != 0
        let hasRate = flags & 4 != 0
        return HistorySample(
            at: Date(timeIntervalSince1970: seconds),
            deltaInput: deltaInput,
            deltaOutput: deltaOutput,
            deltaCached: deltaCached,
            deltaReasoning: deltaReasoning,
            sourceInput: sourceInput,
            sourceOutput: sourceOutput,
            sourceCached: sourceCached,
            sourceReasoning: sourceReasoning,
            isBaseline: flags & 1 != 0,
            remainingPercent: hasQuota ? Double(basisPoints) / 100 : nil,
            windowMinutes: hasQuota ? Int(window) : 0,
            resetsAt: hasQuota && resetSeconds != 0 ? Date(timeIntervalSince1970: TimeInterval(resetSeconds)) : nil,
            tokenRatePerMinute: hasRate ? Double(Float(bitPattern: rateBits)) : nil
        )
    }

    private static func hasValidHeader(_ data: Data) -> Bool {
        guard data.count >= headerSize, data.prefix(magic.count) == magic else { return false }
        let version: Int32? = data.readLittleEndian(at: 8)
        let size: Int32? = data.readLittleEndian(at: 12)
        return version == formatVersion && size == Int32(recordSize)
    }

    private static func checksum(_ data: some DataProtocol) -> UInt32 {
        var hash: UInt32 = 2_166_136_261
        for byte in data {
            hash ^= UInt32(byte)
            hash &*= 16_777_619
        }
        return hash
    }

    private static func ticks(for date: Date) -> Int64 {
        dotNetUnixEpochTicks &+ Int64((date.timeIntervalSince1970 * 10_000_000).rounded())
    }

    private static func unixSeconds(for date: Date?) -> UInt32 {
        guard let date else { return 0 }
        return UInt32(max(1, min(Double(UInt32.max), date.timeIntervalSince1970.rounded(.towardZero))))
    }

    private static func floorToThirtySeconds(_ date: Date) -> Date {
        var components = Calendar.current.dateComponents([.year, .month, .day, .hour, .minute, .second, .timeZone], from: date)
        components.second = (components.second ?? 0) < 30 ? 0 : 30
        components.nanosecond = 0
        return Calendar.current.date(from: components) ?? date
    }

    private static func floorToFiveMinutes(_ date: Date) -> Date {
        var components = Calendar.current.dateComponents([.year, .month, .day, .hour, .minute, .timeZone], from: date)
        components.minute = (components.minute ?? 0) - (components.minute ?? 0) % 5
        components.second = 0
        components.nanosecond = 0
        return Calendar.current.date(from: components) ?? date
    }

    static func resolveStoragePath(in folder: URL, today: Date = Date()) -> URL {
        let prefix = "codex-usage-history-from"
        let suffix = "-v1.5.0.bin"
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd"
        let preferred = folder.appendingPathComponent("\(prefix)\(formatter.string(from: today))\(suffix)")
        guard FileManager.default.fileExists(atPath: folder.path) else { return preferred }

        let candidates = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
        let compatible = candidates.filter {
            $0.lastPathComponent.hasPrefix(prefix) &&
                $0.lastPathComponent.contains("-v1.5.0") &&
                Self.isCompatibleFile($0)
        }.sorted { $0.path < $1.path }
        if let compatible = compatible.first { return compatible }
        guard FileManager.default.fileExists(atPath: preferred.path) else { return preferred }

        var index = 2
        while true {
            let candidate = folder.appendingPathComponent("\(preferred.deletingPathExtension().lastPathComponent)-\(index).bin")
            if !FileManager.default.fileExists(atPath: candidate.path) { return candidate }
            index += 1
        }
    }

    private func readablePaths() -> [URL] {
        guard includeCompatibleFiles else { return [storagePath] }
        let paths = (try? fileManager.contentsOfDirectory(at: storageDirectory, includingPropertiesForKeys: nil)) ?? []
        var readable = paths.filter { path in
            let name = path.lastPathComponent
            return (name.hasPrefix("usage-history-v") || name.hasPrefix("codex-usage-history-from")) &&
                name.hasSuffix(".bin") && Self.isCompatibleFile(path)
        }
        if !readable.contains(storagePath) { readable.append(storagePath) }
        return Array(Set(readable)).sorted {
            let leftCurrent = $0.lastPathComponent.contains("codex-usage-history-from")
            let rightCurrent = $1.lastPathComponent.contains("codex-usage-history-from")
            if leftCurrent != rightCurrent { return !leftCurrent }
            return $0.path < $1.path
        }
    }

    private static func isCompatibleFile(_ path: URL) -> Bool {
        guard let data = try? Data(contentsOf: path, options: .mappedIfSafe) else { return false }
        return hasValidHeader(data)
    }
}

enum HistoryStoreError: Error {
    case disposed
    case invalidHeader
    case lockUnavailable
}

private extension Data {
    mutating func appendLittleEndian<T: FixedWidthInteger>(_ value: T) {
        var littleEndian = value.littleEndian
        Swift.withUnsafeBytes(of: &littleEndian) { append(contentsOf: $0) }
    }

    func readLittleEndian<T: FixedWidthInteger>(at offset: Int) -> T? {
        let size = MemoryLayout<T>.size
        guard offset >= 0, offset + size <= count else { return nil }
        var value: T = 0
        _ = Swift.withUnsafeMutableBytes(of: &value) { destination in
            copyBytes(to: destination, from: offset..<(offset + size))
        }
        return T(littleEndian: value)
    }
}
