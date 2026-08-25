import Foundation

/// Four source counters carried by Codex `token_count` events.  The dashboard
/// displays input + output just like the original Windows application, while
/// retaining cache and reasoning counters for history and project detail.
struct TokenTotals: Equatable, Sendable {
    var input: Int64 = 0
    var output: Int64 = 0
    var cached: Int64 = 0
    var reasoning: Int64 = 0

    init(input: Int64 = 0, output: Int64 = 0, cached: Int64 = 0, reasoning: Int64 = 0) {
        self.input = input
        self.output = output
        self.cached = cached
        self.reasoning = reasoning
    }

    var total: Int64 { input &+ output }

    /// Codex cumulative counters can reset when a session/log stream resets.
    /// A reset contributes the new counter value rather than a negative delta.
    func delta(from previous: TokenTotals) -> TokenTotals {
        let reset = input < previous.input || output < previous.output ||
            cached < previous.cached || reasoning < previous.reasoning
        return reset
            ? self
            : TokenTotals(
                input: input - previous.input,
                output: output - previous.output,
                cached: cached - previous.cached,
                reasoning: reasoning - previous.reasoning
            )
    }

    static func + (lhs: TokenTotals, rhs: TokenTotals) -> TokenTotals {
        TokenTotals(
            input: lhs.input &+ rhs.input,
            output: lhs.output &+ rhs.output,
            cached: lhs.cached &+ rhs.cached,
            reasoning: lhs.reasoning &+ rhs.reasoning
        )
    }

    static func - (lhs: TokenTotals, rhs: TokenTotals) -> TokenTotals {
        TokenTotals(
            input: lhs.input &- rhs.input,
            output: lhs.output &- rhs.output,
            cached: lhs.cached &- rhs.cached,
            reasoning: lhs.reasoning &- rhs.reasoning
        )
    }
}

struct QuotaWindow: Identifiable, Equatable, Sendable {
    let windowMinutes: Int
    let usedPercent: Double
    let resetsAt: Date?

    var id: String {
        "\(windowMinutes)-\(resetsAt?.timeIntervalSince1970 ?? 0)"
    }

    var remainingPercent: Double { max(0, min(100, 100 - usedPercent)) }
}

struct QuotaSnapshot: Equatable, Sendable {
    let at: Date
    let windows: [QuotaWindow]
}

struct SessionUsage: Identifiable, Equatable, Sendable {
    let id: String
    let sessionFilePath: String?
    let totals: TokenTotals
    let startedAt: Date?
    let lastActivity: Date?
    let turnCount: Int
    let toolCallCount: Int
    let model: String?
    let effort: String?
    let status: String?

    var totalTokens: Int64 { totals.total }
}

struct ProjectUsage: Identifiable, Equatable, Sendable {
    let projectPath: String
    let displayName: String
    let sessions: [SessionUsage]

    var id: String { projectPath }
    var totalTokens: Int64 { sessions.reduce(0) { $0 &+ $1.totalTokens } }
}

struct UsageSnapshot: Equatable, Sendable {
    let today: TokenTotals
    let week: TokenTotals
    let month: TokenTotals
    let weekSessions: Int
    let quotaAt: Date?
    let quotas: [QuotaWindow]
    let projects: [ProjectUsage]

    static let empty = UsageSnapshot(
        today: TokenTotals(),
        week: TokenTotals(),
        month: TokenTotals(),
        weekSessions: 0,
        quotaAt: nil,
        quotas: [],
        projects: []
    )

    /// Codex currently reports the short rolling 5H window alongside the
    /// longer weekly window.  Keep the selection explicit instead of relying
    /// on source order or assuming the shortest window is the only quota.
    var fiveHourQuota: QuotaWindow? {
        let shortWindows = quotas.filter { $0.windowMinutes > 0 && $0.windowMinutes < 24 * 60 }
        return shortWindows.min {
            abs($0.windowMinutes - 5 * 60) < abs($1.windowMinutes - 5 * 60)
        }
    }

    var weeklyQuota: QuotaWindow? {
        let longWindows = quotas.filter { $0.windowMinutes >= 24 * 60 }
        return longWindows.min {
            abs($0.windowMinutes - 7 * 24 * 60) < abs($1.windowMinutes - 7 * 24 * 60)
        }
    }

    /// Compatibility accessor for callers that only need one visible quota.
    /// New dashboard code should use `fiveHourQuota` and `weeklyQuota`.
    var primaryQuota: QuotaWindow? {
        fiveHourQuota ?? weeklyQuota ?? quotas.min { $0.windowMinutes < $1.windowMinutes }
    }
}

extension Date {
    static func codexDate(from value: String) -> Date? {
        let withFractionalSeconds = ISO8601DateFormatter()
        withFractionalSeconds.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = withFractionalSeconds.date(from: value) { return date }

        let standard = ISO8601DateFormatter()
        standard.formatOptions = [.withInternetDateTime]
        return standard.date(from: value)
    }

    var localDay: Date {
        Calendar.current.startOfDay(for: self)
    }
}

func compactTokens(_ value: Int64) -> String {
    let absolute = abs(Double(value))
    switch absolute {
    case 1_000_000_000...:
        return String(format: "%.2fB", Double(value) / 1_000_000_000)
    case 1_000_000...:
        return String(format: "%.2fM", Double(value) / 1_000_000)
    case 1_000...:
        return String(format: "%.1fK", Double(value) / 1_000)
    default:
        return value.formatted(.number.grouping(.automatic))
    }
}

func wholePercent(_ value: Double) -> String {
    String(format: "%.0f", min(100, max(0, value)))
}

func quotaWindowName(_ minutes: Int) -> String {
    if minutes < 60 { return "\(minutes) 分钟额度" }
    if minutes % 1_440 == 0 { return "\(minutes / 1_440) 天额度" }
    if minutes % 60 == 0 { return "\(minutes / 60) 小时额度" }
    return "\(minutes) 分钟额度"
}
