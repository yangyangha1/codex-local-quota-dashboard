import Foundation

/// Incrementally scans Codex JSONL session logs.  It intentionally never opens
/// network connections and only decodes lines whose event marker is relevant.
/// The scanner keeps offsets and per-file contributions so repeat scans are
/// idempotent, a log truncate rebuilds its contribution, and a moved/deleted
/// session cannot be counted twice.
final class UsageScanner: @unchecked Sendable {
    static let readBufferSize = 64 * 1024
    static let maximumLineBytes = 4 * 1024 * 1024

    private let lock = NSLock()
    private let fileManager = FileManager.default
    private let codexRoot: URL
    private let includeSessionDetails: Bool
    private var states: [String: FileState] = [:]
    private var daily: [Date: TokenTotals] = [:]
    private var nextArchivedDiscovery = Date.distantPast

    private let tokenCountMarker = Data("\"token_count\"".utf8)
    private let detailMarkers = [
        "\"session_meta\"", "\"user_message\"", "\"turn_context\"",
        "\"task_started\"", "\"task_complete\"", "\"turn_aborted\"",
        "\"function_call\"", "\"custom_tool_call\"",
        "\"tool_search_call\"", "\"mcp_tool_call_end\"",
        "\"web_search_end\""
    ].map { Data($0.utf8) }

    init(rootOverride: URL? = nil, includeSessionDetails: Bool = false) {
        codexRoot = rootOverride ?? fileManager.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex", isDirectory: true)
        self.includeSessionDetails = includeSessionDetails
    }

    convenience init(rootPath: String, includeSessionDetails: Bool = false) {
        self.init(rootOverride: URL(fileURLWithPath: rootPath, isDirectory: true),
                  includeSessionDetails: includeSessionDetails)
    }

    func scan() -> UsageSnapshot {
        lock.lock()
        defer { lock.unlock() }

        let now = Date()
        let sessionsFolder = codexRoot.appendingPathComponent("sessions", isDirectory: true)
        let archivedFolder = codexRoot.appendingPathComponent("archived_sessions", isDirectory: true)
        var seen = Set<String>()

        // Enumeration only stats untouched files.  Parsing remains incremental
        // from each FileState offset, so doing it every 30 seconds keeps new
        // sessions prompt without repeatedly reading their full contents.
        let sessionsComplete = discover(folder: sessionsFolder, seen: &seen)
        let scanArchived = now >= nextArchivedDiscovery
        var archivedComplete = true
        if scanArchived {
            archivedComplete = discover(folder: archivedFolder, seen: &seen)
            if archivedComplete { nextArchivedDiscovery = now.addingTimeInterval(5 * 60) }
        } else {
            refreshKnownFiles(in: archivedFolder, seen: &seen)
        }

        let sessionPrefix = normalizedDirectoryPath(sessionsFolder)
        let staleSessionPaths = sessionsComplete
            ? states.keys.filter { $0.hasPrefix(sessionPrefix) && !seen.contains($0) }
            : []

        // A session may have been atomically moved into archived_sessions.
        // Probe archive immediately before treating it as deleted.
        if !scanArchived && !staleSessionPaths.isEmpty {
            archivedComplete = discover(folder: archivedFolder, seen: &seen)
            if archivedComplete { nextArchivedDiscovery = now.addingTimeInterval(5 * 60) }
        }

        if sessionsComplete {
            for path in staleSessionPaths { removeState(at: path) }
        }

        if archivedComplete {
            let archivedPrefix = normalizedDirectoryPath(archivedFolder)
            let staleArchivedPaths = states.keys.filter {
                $0.hasPrefix(archivedPrefix) && !seen.contains($0)
            }
            for path in staleArchivedPaths { removeState(at: path) }
        }

        let oldest = Calendar.current.date(byAdding: .day, value: -35, to: Date().localDay) ?? .distantPast
        daily = daily.filter { $0.key >= oldest }
        for state in states.values {
            state.byDay = state.byDay.filter { $0.key >= oldest }
        }

        return buildSnapshot(now: now)
    }

    private func discover(folder: URL, seen: inout Set<String>) -> Bool {
        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: folder.path, isDirectory: &isDirectory) else { return true }
        guard isDirectory.boolValue else { return true }

        let keys: Set<URLResourceKey> = [.isRegularFileKey, .fileSizeKey]
        guard let enumerator = fileManager.enumerator(
            at: folder,
            includingPropertiesForKeys: Array(keys),
            options: [.skipsHiddenFiles, .skipsPackageDescendants],
            errorHandler: { _, _ in false }
        ) else {
            return false
        }

        for case let url as URL in enumerator {
            guard url.pathExtension.caseInsensitiveCompare("jsonl") == .orderedSame else { continue }
            do {
                let values = try url.resourceValues(forKeys: keys)
                guard values.isRegularFile == true else { continue }
                let path = normalizedPath(url)
                seen.insert(path)
                try processFile(url: url, path: path, length: Int64(values.fileSize ?? 0))
            } catch {
                // A session is often rotated while Codex writes it.  Preserve
                // the last committed contribution and retry next refresh.
                continue
            }
        }
        return true
    }

    private func refreshKnownFiles(in folder: URL, seen: inout Set<String>) {
        let prefix = normalizedDirectoryPath(folder)
        for path in states.keys.filter({ $0.hasPrefix(prefix) }) {
            let url = URL(fileURLWithPath: path)
            guard fileManager.fileExists(atPath: path) else { continue }
            do {
                let values = try url.resourceValues(forKeys: [.fileSizeKey])
                seen.insert(path)
                try processFile(url: url, path: path, length: Int64(values.fileSize ?? 0))
            } catch {
                // Keep the last good state if an archived file is briefly busy.
                if fileManager.fileExists(atPath: path) { seen.insert(path) }
            }
        }
    }

    private func processFile(url: URL, path: String, length: Int64) throws {
        var state = states[path] ?? FileState(path: path, includeSessionDetails: includeSessionDetails)
        if states[path] == nil { states[path] = state }

        if length < state.offset {
            removeContribution(state)
            state = FileState(path: path, includeSessionDetails: includeSessionDetails)
            states[path] = state
        }
        guard length > state.offset else { return }

        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        try handle.seek(toOffset: UInt64(state.offset))

        var pending = Data()
        var discardLine = false
        var completeOffset = state.offset
        var chunkStartOffset = state.offset

        while let chunk = try handle.read(upToCount: Self.readBufferSize), !chunk.isEmpty {
            var segmentStart = 0
            for index in chunk.indices where chunk[index] == 0x0A {
                let segmentLength = index - segmentStart
                if !discardLine {
                    if pending.isEmpty {
                        if segmentLength <= Self.maximumLineBytes {
                            parseUTF8Line(chunk.subdata(in: segmentStart..<index), state: state)
                        }
                    } else if pending.count + segmentLength <= Self.maximumLineBytes {
                        pending.append(chunk.subdata(in: segmentStart..<index))
                        parseUTF8Line(pending, state: state)
                    }
                }

                completeOffset = chunkStartOffset + Int64(index + 1)
                state.offset = completeOffset
                pending.removeAll(keepingCapacity: true)
                discardLine = false
                segmentStart = index + 1
            }

            let trailingLength = chunk.count - segmentStart
            if trailingLength > 0 && !discardLine {
                if pending.count + trailingLength > Self.maximumLineBytes {
                    pending.removeAll(keepingCapacity: false)
                    discardLine = true
                } else {
                    pending.append(chunk.subdata(in: segmentStart..<chunk.count))
                }
            }
            chunkStartOffset += Int64(chunk.count)
        }

        // Do not advance past an unfinished final line; the next scan retries it.
        state.offset = completeOffset
    }

    private func parseUTF8Line(_ rawData: Data, state: FileState) {
        var data = rawData
        if data.last == 0x0D { data.removeLast() }
        guard !data.isEmpty else { return }

        let isTokenEvent = data.range(of: tokenCountMarker) != nil
        guard isTokenEvent || (includeSessionDetails && detailMarkers.contains(where: { data.range(of: $0) != nil })) else {
            return
        }

        if data.first == 0xEF, data.count >= 3, data[1] == 0xBB, data[2] == 0xBF {
            data.removeFirst(3)
        }
        guard let line = String(data: data, encoding: .utf8) else { return }
        do { try parseLine(line, state: state) }
        catch { /* A malformed record is isolated to this JSONL line. */ }
    }

    private func parseLine(_ line: String, state: FileState) throws {
        guard let jsonData = line.data(using: .utf8),
              let root = try JSONSerialization.jsonObject(with: jsonData) as? [String: Any]
        else { return }

        let payload = object(root, "payload")
        let rootType = string(root, "type")
        let eventType = string(payload ?? [:], "type") ?? rootType
        let timestamp = string(root, "timestamp").flatMap(Date.codexDate(from:))
        if let timestamp { state.observeActivity(timestamp) }

        if (rootType == "session_meta" || eventType == "session_meta"), let payload {
            parseSessionMeta(payload, state: state, at: timestamp)
            return
        }

        if includeSessionDetails, let eventType {
            parseSessionDetailEvent(eventType, source: payload ?? root, root: root, state: state)
        }

        guard eventType == "token_count", let timestamp, let payload else { return }
        if let usage = object(object(payload, "info") ?? [:], "total_token_usage"),
           let input = nonNegativeInt64(usage, "input_tokens"),
           let output = nonNegativeInt64(usage, "output_tokens") {
            let cached = nonNegativeInt64(usage, "cached_input_tokens") ?? 0
            let reasoning = nonNegativeInt64(usage, "reasoning_output_tokens") ?? 0
            let current = TokenTotals(input: input, output: output, cached: cached, reasoning: reasoning)
            let delta = current.delta(from: state.lastTotal)
            let day = timestamp.localDay
            daily[day] = (daily[day] ?? TokenTotals()) + delta
            state.byDay[day] = (state.byDay[day] ?? TokenTotals()) + delta
            if includeSessionDetails { state.aggregateTotal = state.aggregateTotal + delta }
            state.lastTotal = current
            state.lastActivity = timestamp
            state.hasUsage = true
        }

        guard let rateLimits = object(payload, "rate_limits") else { return }
        if state.latestQuota == nil || timestamp > state.latestQuota!.at {
            var windows = [QuotaWindow]()
            addQuota(rateLimits, name: "primary", into: &windows)
            addQuota(rateLimits, name: "secondary", into: &windows)
            if !windows.isEmpty { state.latestQuota = QuotaSnapshot(at: timestamp, windows: windows) }
        }
    }

    private func parseSessionMeta(_ payload: [String: Any], state: FileState, at: Date?) {
        if let cwd = string(payload, "cwd")?.trimmingCharacters(in: .whitespacesAndNewlines), !cwd.isEmpty {
            state.projectPath = cwd
        }
        if let id = string(payload, "id")?.trimmingCharacters(in: .whitespacesAndNewlines), !id.isEmpty {
            state.sessionID = id
        }
        if let at { state.observeActivity(at) }
    }

    private func parseSessionDetailEvent(_ eventType: String, source: [String: Any], root: [String: Any], state: FileState) {
        switch eventType {
        case "user_message": state.turnCount += 1
        case "task_started": state.status = "进行中"
        case "task_complete": state.status = "已完成"
        case "turn_aborted": state.status = "已中止"
        case "turn_context":
            if let model = string(source, "model")?.trimmingCharacters(in: .whitespacesAndNewlines), !model.isEmpty {
                state.model = model
            }
            if let effort = string(source, "effort")?.trimmingCharacters(in: .whitespacesAndNewlines), !effort.isEmpty {
                state.effort = effort
            }
        default: break
        }

        let toolEvents: Set<String> = ["function_call", "custom_tool_call", "tool_search_call", "mcp_tool_call_end", "web_search_end"]
        guard toolEvents.contains(eventType) else { return }
        let callID = string(source, "call_id") ?? string(root, "call_id")
        guard let callID, !callID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            state.toolCallsWithoutID += 1
            return
        }
        state.toolCallIDs.insert(callID)
    }

    private func addQuota(_ rateLimits: [String: Any], name: String, into windows: inout [QuotaWindow]) {
        guard let window = object(rateLimits, name),
              let rawMinutes = nonNegativeInt64(window, "window_minutes"),
              rawMinutes > 0, rawMinutes <= 525_600,
              let used = finiteDouble(window, "used_percent") else { return }
        let reset = nonNegativeInt64(window, "resets_at").flatMap { $0 > 0 ? Date(timeIntervalSince1970: TimeInterval($0)) : nil }
        windows.append(QuotaWindow(windowMinutes: Int(rawMinutes), usedPercent: max(0, min(100, used)), resetsAt: reset))
    }

    private func removeState(at path: String) {
        guard let state = states.removeValue(forKey: path) else { return }
        removeContribution(state)
    }

    private func removeContribution(_ state: FileState) {
        for (day, contribution) in state.byDay {
            guard let current = daily[day] else { continue }
            let updated = current - contribution
            if updated.total <= 0 && updated.cached <= 0 && updated.reasoning <= 0 {
                daily.removeValue(forKey: day)
            } else {
                daily[day] = updated
            }
        }
    }

    private func buildSnapshot(now: Date) -> UsageSnapshot {
        let today = now.localDay
        func sum(days: Int) -> TokenTotals {
            let firstDay = Calendar.current.date(byAdding: .day, value: -(days - 1), to: today) ?? today
            return daily.reduce(TokenTotals()) { partial, value in
                value.key >= firstDay && value.key <= today ? partial + value.value : partial
            }
        }

        let weekStart = now.addingTimeInterval(-7 * 24 * 60 * 60)
        let latestQuota = states.values.compactMap(\.latestQuota).max { $0.at < $1.at }
        let projects: [ProjectUsage]
        if includeSessionDetails {
            let groups = Dictionary(grouping: states.values.filter(\.hasUsage)) {
                let path = $0.projectPath?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                return path.isEmpty ? "未识别项目" : path
            }
            projects = groups.map { path, entries in
                let sessions = entries.map { state in
                    SessionUsage(
                        id: state.sessionID ?? URL(fileURLWithPath: state.path).deletingPathExtension().lastPathComponent,
                        sessionFilePath: state.sessionFilePath,
                        totals: state.aggregateTotal,
                        startedAt: state.startedAt,
                        lastActivity: state.lastActivity,
                        turnCount: state.turnCount,
                        toolCallCount: state.toolCallIDs.count + state.toolCallsWithoutID,
                        model: state.model,
                        effort: state.effort,
                        status: state.status
                    )
                }.filter { $0.totalTokens > 0 }
                    .sorted { lhs, rhs in
                        let left = lhs.lastActivity ?? .distantPast
                        let right = rhs.lastActivity ?? .distantPast
                        return left == right ? lhs.id < rhs.id : left > right
                    }
                return ProjectUsage(projectPath: path, displayName: projectDisplayName(path), sessions: sessions)
            }.filter { $0.totalTokens > 0 }
                .sorted { $0.totalTokens > $1.totalTokens }
        } else {
            projects = []
        }

        return UsageSnapshot(
            today: sum(days: 1),
            week: sum(days: 7),
            month: sum(days: 30),
            weekSessions: states.values.filter { $0.hasUsage && ($0.lastActivity ?? .distantPast) >= weekStart }.count,
            quotaAt: latestQuota?.at,
            quotas: latestQuota?.windows ?? [],
            projects: projects
        )
    }

    private func object(_ source: [String: Any], _ name: String) -> [String: Any]? {
        source[name] as? [String: Any]
    }

    private func string(_ source: [String: Any], _ name: String) -> String? {
        source[name] as? String
    }

    private func nonNegativeInt64(_ source: [String: Any], _ name: String) -> Int64? {
        guard let value = source[name] else { return nil }
        let number: Int64?
        switch value {
        case let value as Int: number = Int64(value)
        case let value as Int64: number = value
        case let value as NSNumber: number = value.int64Value
        case let value as String: number = Int64(value)
        default: number = nil
        }
        guard let number, number >= 0 else { return nil }
        return number
    }

    private func finiteDouble(_ source: [String: Any], _ name: String) -> Double? {
        guard let value = source[name] else { return nil }
        let number: Double?
        switch value {
        case let value as Double: number = value
        case let value as Float: number = Double(value)
        case let value as Int: number = Double(value)
        case let value as NSNumber: number = value.doubleValue
        case let value as String: number = Double(value)
        default: number = nil
        }
        guard let number, number.isFinite else { return nil }
        return number
    }

    private func normalizedPath(_ url: URL) -> String {
        url.standardizedFileURL.path
    }

    private func normalizedDirectoryPath(_ url: URL) -> String {
        normalizedPath(url).hasSuffix("/") ? normalizedPath(url) : normalizedPath(url) + "/"
    }

    private func projectDisplayName(_ path: String) -> String {
        if path == "未识别项目" { return path }
        let trimmed = path.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        return URL(fileURLWithPath: trimmed).lastPathComponent.isEmpty
            ? path
            : URL(fileURLWithPath: trimmed).lastPathComponent
    }
}

private final class FileState {
    let path: String
    var offset: Int64 = 0
    var lastTotal = TokenTotals()
    var aggregateTotal = TokenTotals()
    var byDay: [Date: TokenTotals] = [:]
    var startedAt: Date?
    var lastActivity: Date?
    var hasUsage = false
    var latestQuota: QuotaSnapshot?
    var projectPath: String?
    var sessionID: String?
    var sessionFilePath: String?
    var turnCount = 0
    var toolCallsWithoutID = 0
    var toolCallIDs = Set<String>()
    var model: String?
    var effort: String?
    var status: String?

    init(path: String, includeSessionDetails: Bool) {
        self.path = path
        if includeSessionDetails {
            sessionID = URL(fileURLWithPath: path).deletingPathExtension().lastPathComponent
            sessionFilePath = path
        }
    }

    func observeActivity(_ date: Date) {
        if startedAt == nil || date < startedAt! { startedAt = date }
        if lastActivity == nil || date > lastActivity! { lastActivity = date }
    }
}
