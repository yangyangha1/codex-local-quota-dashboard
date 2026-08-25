import AppKit
import Combine
import Foundation

enum DashboardContentMode: Equatable {
    case live
    case detail
    case history
}

enum WidgetTheme: String, CaseIterable {
    case dark
    case light
}

@MainActor
final class DashboardViewModel: ObservableObject {
    @Published private(set) var snapshot = UsageSnapshot.empty
    @Published private(set) var liveChartSnapshot: ChartRenderSnapshot
    @Published private(set) var historyChartSnapshot: ChartRenderSnapshot
    @Published private(set) var detailProjects = [ProjectUsage]()
    @Published private(set) var historySamples = [HistorySample]()
    @Published private(set) var availableHistoryDays = Set<Date>()
    @Published private(set) var isRefreshing = false
    @Published private(set) var isDetailLoading = false
    @Published private(set) var isHistoryLoading = false
    @Published private(set) var refreshError: String?
    @Published var contentMode: DashboardContentMode = .live
    @Published var historySelectedDate = Date().localDay
    @Published var visibleHistoryWeekStart = DashboardViewModel.startOfWeek(Date())
    @Published var detailShowsAll = false
    @Published var theme: WidgetTheme {
        didSet { UserDefaults.standard.set(theme.rawValue, forKey: Self.themeKey) }
    }
    @Published var backgroundTransparency: Double {
        didSet { UserDefaults.standard.set(backgroundTransparency, forKey: Self.transparencyKey) }
    }
    @Published var topMost: Bool {
        didSet {
            UserDefaults.standard.set(topMost, forKey: Self.topMostKey)
            onTopMostChanged?(topMost)
        }
    }
    @Published private(set) var launchAtLoginEnabled = LaunchAtLoginController.isEnabled()
    @Published private(set) var secondsRemaining = TokenRateChart.captureIntervalSeconds

    var onTopMostChanged: ((Bool) -> Void)?
    var onHideRequested: (() -> Void)?

    private let scanner = UsageScanner()
    private let historyStore = HistoryStore()
    private var liveChart = TokenRateChart()
    private var historyChart: TokenRateChart
    private var countdownTimer: Timer?
    private var historyLoadID: UUID?
    private var detailLoadID: UUID?
    private var historyLoadTask: Task<Void, Never>?
    private var detailLoadTask: Task<Void, Never>?

    private static let themeKey = "CodexQuotaWidget.theme"
    private static let transparencyKey = "CodexQuotaWidget.backgroundTransparency"
    private static let topMostKey = "CodexQuotaWidget.topMost"
    private static let topMostDefaultMigrationKey = "CodexQuotaWidget.topMost.default.v2"

    init() {
        let defaults = UserDefaults.standard
        theme = WidgetTheme(rawValue: defaults.string(forKey: Self.themeKey) ?? "") ?? .dark
        backgroundTransparency = min(100, max(0, defaults.object(forKey: Self.transparencyKey) as? Double ?? 10))
        if !defaults.bool(forKey: Self.topMostDefaultMigrationKey) {
            defaults.set(false, forKey: Self.topMostKey)
            defaults.set(true, forKey: Self.topMostDefaultMigrationKey)
        }
        topMost = defaults.object(forKey: Self.topMostKey) as? Bool ?? false
        historyChart = TokenRateChart()
        historyChart.setDisplayHours(24)
        liveChartSnapshot = liveChart.renderSnapshot()
        historyChartSnapshot = historyChart.renderSnapshot()
    }

    func start() {
        guard countdownTimer == nil else { return }
        refresh()
        countdownTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
    }

    func stop() {
        countdownTimer?.invalidate()
        countdownTimer = nil
        historyLoadTask?.cancel()
        detailLoadTask?.cancel()
        historyLoadID = nil
        detailLoadID = nil
        historyStore.dispose()
    }

    func refresh() {
        guard !isRefreshing else { return }
        isRefreshing = true
        refreshError = nil
        let scanner = scanner
        let historyStore = historyStore
        Task.detached(priority: .utility) { [weak self, scanner, historyStore] in
            let capturedAt = Date()
            let result = scanner.scan()
            historyStore.record(result, capturedAt: capturedAt)
            await MainActor.run {
                self?.apply(snapshot: result, capturedAt: capturedAt)
            }
        }
    }

    func showLive() {
        contentMode = .live
        historyLoadTask?.cancel()
        detailLoadTask?.cancel()
        historyLoadID = nil
        detailLoadID = nil
        detailProjects.removeAll()
        historySamples.removeAll()
        liveChartSnapshot = liveChart.renderSnapshot()
    }

    func showDetail() {
        guard contentMode != .detail else { showLive(); return }
        contentMode = .detail
        historyLoadTask?.cancel()
        detailLoadTask?.cancel()
        historyLoadID = nil
        historySamples.removeAll()
        detailProjects.removeAll()
        isDetailLoading = true
        let loadID = UUID()
        detailLoadID = loadID
        detailLoadTask = Task { [weak self] in
            let result = await Task.detached(priority: .utility) {
                UsageScanner(includeSessionDetails: true).scan()
            }.value
            guard !Task.isCancelled,
                  let self,
                  self.contentMode == .detail,
                  self.detailLoadID == loadID
            else { return }
            self.detailProjects = result.projects
            self.isDetailLoading = false
        }
    }

    func showHistory() {
        guard contentMode != .history else { showLive(); return }
        contentMode = .history
        detailLoadTask?.cancel()
        detailLoadID = nil
        detailProjects.removeAll()
        historySelectedDate = min(historySelectedDate.localDay, Date().localDay)
        visibleHistoryWeekStart = Self.startOfWeek(historySelectedDate)
        beginHistoryLoad()
    }

    func selectHistoryDate(_ date: Date) {
        guard date.localDay <= Date().localDay else { return }
        historySelectedDate = date.localDay
        visibleHistoryWeekStart = Self.startOfWeek(historySelectedDate)
        beginHistoryLoad()
    }

    func shiftHistoryWeek(by weeks: Int) {
        let candidate = Calendar.current.date(byAdding: .day, value: weeks * 7, to: visibleHistoryWeekStart) ?? visibleHistoryWeekStart
        guard candidate <= Self.startOfWeek(Date()) else { return }
        visibleHistoryWeekStart = candidate
        beginHistoryLoad()
    }

    func zoomChart(delta: Double) {
        switch contentMode {
        case .live:
            if liveChart.zoomByWheel(delta) { liveChartSnapshot = liveChart.renderSnapshot() }
        case .history:
            let before = historyChart.displayHours
            if historyChart.zoomByWheel(delta) {
                historyChartSnapshot = historyChart.renderSnapshot(now: chartEnd(for: historySelectedDate))
                if before != historyChart.displayHours && historyChart.displayHours == 48 { beginHistoryLoad() }
            }
        case .detail: break
        }
    }

    func setChartHours(_ hours: Int) {
        switch contentMode {
        case .live:
            liveChart.setDisplayHours(hours)
            liveChartSnapshot = liveChart.renderSnapshot()
        case .history:
            historyChart.setDisplayHours(hours)
            historyChartSnapshot = historyChart.renderSnapshot(now: chartEnd(for: historySelectedDate))
            if hours == 48 { beginHistoryLoad() }
        case .detail: break
        }
    }

    func selectChartRange(startFraction: Double, endFraction: Double) {
        let bounds = activeChartSnapshot
        let lower = max(0, min(1, min(startFraction, endFraction)))
        let upper = max(0, min(1, max(startFraction, endFraction)))
        guard upper - lower >= 0.025 else { return }
        let start = bounds.timelineStart.addingTimeInterval(bounds.displayDuration * lower)
        let end = bounds.timelineStart.addingTimeInterval(bounds.displayDuration * upper)
        guard end.timeIntervalSince(start) >= 5 * 60 else { return }
        switch contentMode {
        case .live:
            liveChart.selectRange(from: start, to: end)
            liveChartSnapshot = liveChart.renderSnapshot()
        case .history:
            historyChart.selectRange(from: start, to: end)
            historyChartSnapshot = historyChart.renderSnapshot(now: chartEnd(for: historySelectedDate))
        case .detail: break
        }
    }

    var activeChartSnapshot: ChartRenderSnapshot {
        contentMode == .history ? historyChartSnapshot : liveChartSnapshot
    }

    var historyHasChartSelection: Bool { historyChart.hasCustomSelection }

    func clearHistoryChartSelection() {
        guard contentMode == .history else { return }
        historyChart.clearSelection()
        historyChartSnapshot = historyChart.renderSnapshot(now: chartEnd(for: historySelectedDate))
    }

    var fiveHourQuota: QuotaWindow? { snapshot.fiveHourQuota }
    var weeklyQuota: QuotaWindow? { snapshot.weeklyQuota }

    /// Retained for the menu-bar compatibility surface.  Dashboard content
    /// must use the two explicit quota accessors above.
    var primaryQuota: QuotaWindow? { snapshot.primaryQuota }

    var displayedProjects: [ProjectUsage] {
        guard !detailShowsAll else { return detailProjects }
        let today = Date().localDay
        return detailProjects.compactMap { project in
            let todaySessions = project.sessions.filter { session in
                let active = session.lastActivity ?? session.startedAt
                return active?.localDay == today
            }
            return todaySessions.isEmpty
                ? nil
                : ProjectUsage(projectPath: project.projectPath, displayName: project.displayName, sessions: todaySessions)
        }
    }

    func openHistoryFolder() {
        NSWorkspace.shared.open(historyStore.storageDirectory)
    }

    func openProject(_ project: ProjectUsage) {
        let url = URL(fileURLWithPath: project.projectPath)
        if FileManager.default.fileExists(atPath: url.path) { NSWorkspace.shared.open(url) }
    }

    func revealSession(_ session: SessionUsage) {
        guard let path = session.sessionFilePath else { return }
        let url = URL(fileURLWithPath: path)
        if FileManager.default.fileExists(atPath: url.path) {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        }
    }

    func openProjectPage() {
        guard let url = URL(string: "https://github.com/yangyangha1/codex-local-quota-dashboard") else { return }
        NSWorkspace.shared.open(url)
    }

    @discardableResult
    func setLaunchAtLogin(_ enabled: Bool) -> Result<String, Error> {
        do {
            let executableURL = try LaunchAtLoginController.setEnabled(enabled)
            launchAtLoginEnabled = LaunchAtLoginController.isEnabled()
            if enabled {
                return .success("已开启开机自动启动：\(executableURL?.path ?? "")")
            }
            return .success("已关闭开机自动启动")
        } catch {
            launchAtLoginEnabled = LaunchAtLoginController.isEnabled()
            return .failure(error)
        }
    }

    private func tick() {
        guard !isRefreshing else { return }
        secondsRemaining -= 1
        if secondsRemaining <= 0 { refresh() }
    }

    private func apply(snapshot: UsageSnapshot, capturedAt: Date) {
        self.snapshot = snapshot
        liveChart.capture(
            capturedAt: capturedAt,
            cumulativeTokens: snapshot.today.total,
            weeklyRemainingPercent: snapshot.weeklyQuota?.remainingPercent,
            weeklyWindowMinutes: snapshot.weeklyQuota?.windowMinutes ?? 0,
            weeklyResetsAt: snapshot.weeklyQuota?.resetsAt,
            fiveHourRemainingPercent: snapshot.fiveHourQuota?.remainingPercent,
            fiveHourWindowMinutes: snapshot.fiveHourQuota?.windowMinutes ?? 0,
            fiveHourResetsAt: snapshot.fiveHourQuota?.resetsAt
        )
        liveChartSnapshot = liveChart.renderSnapshot(now: capturedAt)
        secondsRemaining = TokenRateChart.captureIntervalSeconds
        isRefreshing = false
    }

    private func beginHistoryLoad() {
        guard contentMode == .history else { return }
        let loadID = UUID()
        historyLoadID = loadID
        isHistoryLoading = true
        let selectedDate = historySelectedDate
        let visibleWeek = visibleHistoryWeekStart
        let chartHours = historyChart.displayHours
        let requiredStart = historyReadStart(for: selectedDate, displayHours: chartHours)
        let requiredEnd = Self.dayAfter(selectedDate)
        let statusStart = visibleWeek
        let statusEnd = Calendar.current.date(byAdding: .day, value: 7, to: visibleWeek) ?? visibleWeek
        let readStart = min(requiredStart, statusStart)
        let readEnd = max(requiredEnd, statusEnd)
        let store = historyStore

        historyLoadTask?.cancel()
        historyLoadTask = Task { [weak self, store] in
            do {
                let samples = try await Task.detached(priority: .utility) {
                    try store.readRange(
                        fromInclusive: readStart,
                        toExclusive: readEnd,
                        cancellationCheck: { Task.isCancelled }
                    )
                }.value
                guard !Task.isCancelled,
                      let self,
                      self.contentMode == .history,
                      self.historyLoadID == loadID,
                      self.historySelectedDate == selectedDate,
                      self.visibleHistoryWeekStart == visibleWeek
                else { return }
                self.availableHistoryDays = Set(samples.map { $0.at.localDay })
                self.historySamples = samples.filter { $0.at >= requiredStart && $0.at < requiredEnd }
                self.historyChart.loadHistoricalSamples(self.historySamples, origin: requiredStart)
                self.historyChartSnapshot = self.historyChart.renderSnapshot(now: self.chartEnd(for: selectedDate))
                self.isHistoryLoading = false
            } catch is CancellationError {
                // A mode/date change superseded this detached read.
            } catch {
                guard !Task.isCancelled, let self, self.historyLoadID == loadID else { return }
                self.isHistoryLoading = false
                self.refreshError = "历史数据暂时无法读取"
            }
        }
    }

    private func historyReadStart(for selectedDate: Date, displayHours: Int) -> Date {
        displayHours == 48
            ? Calendar.current.date(byAdding: .day, value: -1, to: selectedDate) ?? selectedDate
            : selectedDate
    }

    private func chartEnd(for selectedDate: Date) -> Date {
        selectedDate == Date().localDay ? Date() : Self.dayAfter(selectedDate)
    }

    private static func startOfWeek(_ date: Date) -> Date {
        var calendar = Calendar.current
        calendar.firstWeekday = 2
        return calendar.dateInterval(of: .weekOfYear, for: date)?.start ?? date.localDay
    }

    private static func dayAfter(_ date: Date) -> Date {
        Calendar.current.date(byAdding: .day, value: 1, to: date) ?? date
    }
}
