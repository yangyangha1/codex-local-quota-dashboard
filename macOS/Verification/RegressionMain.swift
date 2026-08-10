import Foundation

@main
enum RegressionMain {
    static func main() {
        do {
            try scannerPartialLineCounterResetMoveTruncateAndDeleteAreIdempotent()
            try scannerCounterRollbackBadQuotaLargeLineAndDetailOnDemand()
            try historyThirtySecondBufferAndBinaryCompatibility()
            try historyCompatiblePathAndInterruptedTail()
            try chartSharedSamplingQuotaConsumptionAndZoom()
            try widgetWindowGeometryKeepsTheReadableMinimum()
            print("CodexQuotaWidget regression checks passed.")
        } catch {
            fputs("CodexQuotaWidget regression failure: \(error)\n", stderr)
            exit(1)
        }
    }
}

private func scannerPartialLineCounterResetMoveTruncateAndDeleteAreIdempotent() throws {
    let root = try makeCodexRoot()
    defer { try? FileManager.default.removeItem(at: root) }
    let sessions = root.appendingPathComponent("sessions")
    let archived = root.appendingPathComponent("archived_sessions")
    let session = sessions.appendingPathComponent("one.jsonl")
    let scanner = UsageScanner(rootOverride: root)

    try tokenLine(input: 120, output: 30, cached: 20, reasoning: 5).data(using: .utf8)!.write(to: session)
    try expect(scanner.scan().today.total == 0, "unfinished final line must not commit")

    try append("\n", to: session)
    let first = scanner.scan()
    try expect(first.today.total == 150, "completed token line should count once; got \(first.today.total)")
    try expect(first.quotas.count == 1, "valid quota should be preserved")
    try expect(scanner.scan().today.total == 150, "repeat scan must be idempotent")

    try FileManager.default.moveItem(at: session, to: archived.appendingPathComponent("one.jsonl"))
    let afterMove = scanner.scan().today.total
    try expect(afterMove == 150, "move into archive must not duplicate; got \(afterMove)")

    let archivedSession = archived.appendingPathComponent("one.jsonl")
    try tokenLine(input: 4, output: 1).appending("\n").data(using: .utf8)!.write(to: archivedSession, options: .atomic)
    try expect(scanner.scan().today.total == 5, "truncate must rebuild contribution")

    try FileManager.default.removeItem(at: archivedSession)
    try expect(scanner.scan().today.total == 0, "deleted archive must remove contribution")
}

private func scannerCounterRollbackBadQuotaLargeLineAndDetailOnDemand() throws {
    let root = try makeCodexRoot()
    defer { try? FileManager.default.removeItem(at: root) }
    let sessions = root.appendingPathComponent("sessions")
    let session = sessions.appendingPathComponent("detail.jsonl")
    let timestamp = isoDate(Date())
    let meta = "{\"timestamp\":\"\(timestamp)\",\"type\":\"session_meta\",\"payload\":{\"type\":\"session_meta\",\"cwd\":\"/tmp/alpha\",\"id\":\"session-1\"}}"
    let events = [
        meta,
        "{\"timestamp\":\"\(timestamp)\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\"}}",
        "{\"timestamp\":\"\(timestamp)\",\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_context\",\"model\":\"gpt-test\",\"effort\":\"high\"}}",
        "{\"timestamp\":\"\(timestamp)\",\"type\":\"event_msg\",\"payload\":{\"type\":\"function_call\",\"call_id\":\"call-1\"}}",
        "{\"timestamp\":\"\(timestamp)\",\"type\":\"event_msg\",\"payload\":{\"type\":\"custom_tool_call\",\"call_id\":\"call-1\"}}",
        "{\"timestamp\":\"\(timestamp)\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}",
        tokenLine(input: 100, output: 20, cached: 30, reasoning: 5, usedPercent: nil, timestamp: timestamp),
        tokenLine(input: 20, output: 3, cached: 1, reasoning: 0, usedPercent: 25, timestamp: timestamp)
    ]
    try (events.joined(separator: "\n") + "\n").data(using: .utf8)!.write(to: session)

    let lightweight = UsageScanner(rootOverride: root).scan()
    try expect(lightweight.today.total == 143, "rollback must add new counter as a baseline")
    try expect(lightweight.projects.isEmpty, "lightweight scan must not retain project details")

    let detailed = UsageScanner(rootOverride: root, includeSessionDetails: true).scan()
    try expect(detailed.projects.count == 1, "detail scan should group by cwd")
    let project = try require(detailed.projects.first, "missing project")
    let detail = try require(project.sessions.first, "missing session")
    try expect(project.displayName == "alpha", "project display name mismatch")
    try expect(detail.turnCount == 1 && detail.toolCallCount == 1, "detail counters mismatch")
    try expect(detail.model == "gpt-test" && detail.effort == "high" && detail.status == "已完成", "session metadata mismatch")
    try expect(detail.totals.cached == 31, "detail source totals mismatch")
    try expect(detail.sessionFilePath == session.path, "session path mismatch")

    let invalid = sessions.appendingPathComponent("invalid.jsonl")
    let largePayload = "{\"payload\":\"" + String(repeating: "x", count: 4 * 1024 * 1024 + 32) + "\"}\n"
    try (largePayload + tokenLine(input: 8, output: 2, usedPercent: nil) + "\n").data(using: .utf8)!.write(to: invalid)
    try expect(UsageScanner(rootOverride: root).scan().today.total == 153, "large irrelevant line should be skipped without blocking later tokens")
}

private func historyThirtySecondBufferAndBinaryCompatibility() throws {
    let folder = try temporaryFolder()
    defer { try? FileManager.default.removeItem(at: folder) }
    let path = folder.appendingPathComponent("history.bin")
    let store = HistoryStore(path: path, includeCompatibleFiles: false)
    let base = noonToday()
    for index in 0..<10 {
        store.record(snapshot(input: Int64((index + 1) * 10), output: Int64((index + 1) * 2)), capturedAt: base.addingTimeInterval(Double(index * 30)))
    }
    try expect(try store.readAll().isEmpty, "first five-minute bucket must remain buffered")

    store.record(snapshot(input: 110, output: 22), capturedAt: base.addingTimeInterval(5 * 60))
    var values = try store.readAll()
    try expect(values.count == 10 && values[0].isBaseline, "batched samples missing")
    try expect(values[1].deltaInput == 10 && values[1].deltaOutput == 2, "delta sample mismatch")
    try expect(abs((values[1].tokenRatePerMinute ?? 0) - 24) < 0.01, "rate must be per minute")

    try expect(store.flushPending(), "flush should succeed")
    values = try store.readAll()
    try expect(values.count == 11, "pending sample not appended")
    try expect((try Data(contentsOf: path)).count == 16 + HistoryStore.recordSize * 11, "96-byte record contract broken")
    let quota = try require(values.last, "missing quota sample")
    try expect(abs((quota.remainingPercent ?? 0) - 62.35) < 0.01 && quota.windowMinutes == 10_080, "quota round-trip mismatch")
    try expect(abs((quota.resetsAt?.timeIntervalSince1970 ?? 0) - resetDate.timeIntervalSince1970) < 1, "reset date round-trip mismatch")
}

private func historyCompatiblePathAndInterruptedTail() throws {
    let folder = try temporaryFolder()
    defer { try? FileManager.default.removeItem(at: folder) }
    let preferred = HistoryStore.resolveStoragePath(in: folder, today: Date())
    let first = HistoryStore(path: preferred, includeCompatibleFiles: false)
    first.record(snapshot(input: 10, output: 2), capturedAt: noonToday())
    try expect(first.flushPending(), "first history flush failed")
    let resolved = HistoryStore.resolveStoragePath(in: folder, today: Date())
    try expect(resolved.standardizedFileURL.path == preferred.standardizedFileURL.path, "compatible path was not reused")

    let tail = try FileHandle(forWritingTo: preferred)
    try tail.seekToEnd()
    tail.write(Data(repeating: 0xFF, count: 11))
    try tail.close()
    try expect(try first.readAll().count == 1, "incomplete tail should be ignored")

    let reopened = HistoryStore(path: preferred, includeCompatibleFiles: false)
    reopened.record(snapshot(input: 20, output: 4), capturedAt: noonToday().addingTimeInterval(30))
    try expect(reopened.flushPending(), "reopen flush failed")
    try expect(try reopened.readAll().count == 2, "interrupted file did not recover")
}

private func chartSharedSamplingQuotaConsumptionAndZoom() throws {
    var chart = TokenRateChart()
    let start = noonToday()
    let totals: [Int64] = [0, 100, 250, 450, 700]
    let quotas: [Double] = [83, 83, 79, 79, 79]
    for index in totals.indices {
        chart.capture(capturedAt: start.addingTimeInterval(Double(index * 30)), cumulativeTokens: totals[index], remainingPercent: quotas[index], windowMinutes: 10_080, resetsAt: resetDate)
    }
    let result = chart.renderSnapshot(now: start.addingTimeInterval(120))
    try expect(abs(result.cumulativeIncrease - 700) < 0.01, "cumulative increase mismatch")
    try expect(abs(result.quotaConsumedDuringRuntime - 4) < 0.01 && result.quotaPoints.count == 5, "quota sampling mismatch")
    try expect(chart.displayHours == 2, "default display window mismatch")
    try expect(chart.zoomByWheel(-120, now: 100) && chart.displayHours == 3, "first wheel zoom mismatch")
    try expect(chart.zoomByWheel(-120, now: 101) && chart.displayHours == 6, "second wheel zoom mismatch")
    chart.selectRange(
        from: start,
        to: start.addingTimeInterval(5 * 60)
    )
    let brushed = chart.renderSnapshot(now: start.addingTimeInterval(6 * 60))
    try expect(
        abs(brushed.displayDuration - 5 * 60) < 0.1 &&
            abs(brushed.timelineStart.timeIntervalSince(start)) < 0.1,
        "box selection did not create a zoomed chart range"
    )
    try expect(chart.hasCustomSelection, "box selection state was not retained")
    chart.clearSelection()
    try expect(!chart.hasCustomSelection, "clearing a box selection failed")
}

private func widgetWindowGeometryKeepsTheReadableMinimum() throws {
    let minimum = WidgetWindowGeometry.constrainedSize(CGSize(width: 100, height: 100))
    try expect(
        minimum.width == WidgetWindowGeometry.minimumSize.width &&
            minimum.height == WidgetWindowGeometry.minimumSize.height,
        "window must not shrink below 320×347"
    )

    let heightDriven = WidgetWindowGeometry.constrainedSize(
        CGSize(width: 321, height: 500),
        relativeTo: WidgetWindowGeometry.minimumSize
    )
    try expect(heightDriven.width == 461 && heightDriven.height == 500, "vertical resize must preserve the panel ratio")

    let maximum = WidgetWindowGeometry.constrainedSize(CGSize(width: 900, height: 900))
    try expect(maximum.width == 576 && maximum.height == 625, "window maximum size changed unexpectedly")
}

private enum RegressionFailure: Error, CustomStringConvertible {
    case failed(String)
    var description: String {
        switch self {
        case let .failed(message): return message
        }
    }
}

private func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
    guard try condition() else { throw RegressionFailure.failed(message) }
}

private func require<T>(_ value: T?, _ message: String) throws -> T {
    guard let value else { throw RegressionFailure.failed(message) }
    return value
}

private let resetDate = Date(timeIntervalSince1970: 1_800_000_000)

private func makeCodexRoot() throws -> URL {
    let root = try temporaryFolder()
    try FileManager.default.createDirectory(at: root.appendingPathComponent("sessions"), withIntermediateDirectories: true)
    try FileManager.default.createDirectory(at: root.appendingPathComponent("archived_sessions"), withIntermediateDirectories: true)
    return root
}

private func temporaryFolder() throws -> URL {
    let folder = FileManager.default.temporaryDirectory.appendingPathComponent("CodexQuotaWidgetTests-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    return folder
}

private func append(_ text: String, to url: URL) throws {
    let handle = try FileHandle(forWritingTo: url)
    defer { try? handle.close() }
    try handle.seekToEnd()
    handle.write(Data(text.utf8))
}

private func tokenLine(input: Int64, output: Int64, cached: Int64 = 0, reasoning: Int64 = 0, usedPercent: Double? = 25, timestamp: String = isoDate(Date())) -> String {
    var payload: [String: Any] = [
        "type": "token_count",
        "info": [
            "total_token_usage": [
                "input_tokens": input,
                "output_tokens": output,
                "cached_input_tokens": cached,
                "reasoning_output_tokens": reasoning
            ]
        ]
    ]
    if let usedPercent {
        payload["rate_limits"] = [
            "primary": [
                "window_minutes": 10_080,
                "used_percent": usedPercent,
                "resets_at": 1_800_000_000
            ]
        ]
    } else {
        payload["rate_limits"] = ["primary": ["window_minutes": 10_080]]
    }
    let root: [String: Any] = ["timestamp": timestamp, "type": "event_msg", "payload": payload]
    let data = try! JSONSerialization.data(withJSONObject: root, options: [])
    return String(decoding: data, as: UTF8.self)
}

private func snapshot(input: Int64, output: Int64) -> UsageSnapshot {
    UsageSnapshot(today: TokenTotals(input: input, output: output, cached: input / 5, reasoning: output / 2), week: TokenTotals(), month: TokenTotals(), weekSessions: 0, quotaAt: nil, quotas: [QuotaWindow(windowMinutes: 10_080, usedPercent: 37.65, resetsAt: resetDate)], projects: [])
}

private func noonToday() -> Date {
    var components = Calendar.current.dateComponents([.year, .month, .day, .timeZone], from: Date())
    components.hour = 12
    components.minute = 0
    components.second = 0
    return Calendar.current.date(from: components) ?? Date()
}

private func isoDate(_ date: Date) -> String {
    let formatter = ISO8601DateFormatter()
    formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    return formatter.string(from: date)
}
