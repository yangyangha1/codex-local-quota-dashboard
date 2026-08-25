import Foundation

struct ChartPoint: Equatable, Sendable, Identifiable {
    let at: Date
    let value: Double
    let breakBefore: Bool
    let sampleCount: Int

    var id: String { "\(at.timeIntervalSince1970)-\(value)-\(sampleCount)" }
}

struct ChartRenderSnapshot: Equatable, Sendable {
    let tokenPoints: [ChartPoint]
    /// Weekly remaining percentage (the base quota curve).
    let quotaPoints: [ChartPoint]
    /// Independent rolling 5H remaining percentage.
    let fiveHourQuotaPoints: [ChartPoint]
    let cumulativePoints: [ChartPoint]
    let tokenAxisMaximum: Double
    let cumulativeAxisMaximum: Double
    let currentQuota: Double?
    let currentFiveHourQuota: Double?
    let currentTokenRate: Double?
    let peakTokenRate: Double
    let cumulativeIncrease: Double
    let quotaConsumedDuringRuntime: Double
    let timelineStart: Date
    let timelineEnd: Date
    let displayDuration: TimeInterval
    let historical: Bool
}

/// The platform-independent sampling and graph calculation engine.  Drawing is
/// deliberately separate so the same data path drives a SwiftUI Canvas,
/// history replay, and tests just as the original WinForms chart did.
struct TokenRateChart: Sendable {
    static let captureIntervalSeconds = 30
    static let zoomLevels = [1, 2, 3, 6, 12, 24, 48]

    private static let minimumTokenAxisMaximum = 1_000.0
    private static let targetPeakAxisRatio = 0.80
    private static let tokenAxisRoundStep = 100_000.0
    private static let targetWindow: TimeInterval = 60
    private static let minimumWindow: TimeInterval = 25
    private static let maximumWindow: TimeInterval = 90
    private static let rawSampleSlack: TimeInterval = 15
    private static let smoothingTime: TimeInterval = 30
    private static let pointBucketDuration: TimeInterval = 30
    private static let maximumContinuousGap: TimeInterval = 120
    private static let maximumDisplayDuration: TimeInterval = 48 * 60 * 60
    private static let retentionSlack: TimeInterval = 120

    private var rateSamples = [TokenCounterSample]()
    private var counterHistory = [TokenCounterSample]()
    private var tokenPoints = [ChartPoint]()

    private(set) var displayHours = 2
    private var wheelDeltaAccumulator = 0.0
    private var wheelDirection = 0.0
    private var lastWheelStepTime: TimeInterval?
    private var lastCaptureAt: Date?
    private var chartOriginAt: Date?
    private var customViewFrom: Date?
    private var customViewTo: Date?
    private var historicalSource = false

    private var lastTokenRate: Double?
    private var lastRateCalculationAt: Date?
    private var breakBeforeNextTokenPoint = false
    private var hasSourceCounter = false
    private var lastSourceCounter: Int64 = 0
    private var lastSourceDay: Date?
    private var normalizedCounter: Int64 = 0
    private var breakBeforeNextCounterSample = false

    private var weeklyQuota = QuotaSeries()
    private var fiveHourQuota = QuotaSeries()

    mutating func setDisplayHours(_ hours: Int) {
        guard Self.zoomLevels.contains(hours) else { return }
        displayHours = hours
        clearSelection()
    }

    /// Positive wheel deltas shorten the timeline, negative values expand it.
    /// `now` is injectable to retain the original 200 ms debounce semantics in
    /// deterministic tests.
    @discardableResult
    mutating func zoomByWheel(_ delta: Double, now: TimeInterval = Date().timeIntervalSince1970) -> Bool {
        guard delta != 0 else { return false }
        let clearedSelection = customViewFrom != nil || customViewTo != nil
        clearSelection()
        let direction = delta.sign == .minus ? -1.0 : 1.0
        if wheelDirection != direction {
            wheelDirection = direction
            wheelDeltaAccumulator = 0
        }
        wheelDeltaAccumulator += delta
        guard abs(wheelDeltaAccumulator) >= 120 else { return clearedSelection }

        if let lastWheelStepTime, now - lastWheelStepTime < 0.2 {
            wheelDeltaAccumulator = direction * 119
            return clearedSelection
        }
        guard let index = Self.zoomLevels.firstIndex(of: displayHours) else { return clearedSelection }
        let next = direction > 0
            ? max(0, index - 1)
            : min(Self.zoomLevels.count - 1, index + 1)
        wheelDeltaAccumulator = 0
        guard next != index else { return clearedSelection }
        displayHours = Self.zoomLevels[next]
        lastWheelStepTime = now
        return true
    }

    mutating func selectRange(from: Date, to: Date) {
        guard to.timeIntervalSince(from) >= 5 * 60 else { return }
        customViewFrom = from
        customViewTo = to
    }

    mutating func clearSelection() {
        customViewFrom = nil
        customViewTo = nil
    }

    var viewDuration: TimeInterval {
        if let customViewFrom, let customViewTo { return customViewTo.timeIntervalSince(customViewFrom) }
        return Double(displayHours) * 60 * 60
    }

    var hasCustomSelection: Bool {
        customViewFrom != nil && customViewTo != nil
    }

    mutating func loadHistoricalSamples(_ samples: [HistorySample], origin: Date) {
        resetAll(captureAt: nil)
        chartOriginAt = origin
        historicalSource = true
        for sample in samples.sorted(by: { $0.at < $1.at }) {
            let (total, overflow) = sample.sourceInput.addingReportingOverflow(sample.sourceOutput)
            capture(
                capturedAt: sample.at,
                cumulativeTokens: overflow ? Int64.max : max(0, total),
                weeklyRemainingPercent: sample.remainingPercent,
                weeklyWindowMinutes: sample.windowMinutes,
                weeklyResetsAt: sample.resetsAt,
                fiveHourRemainingPercent: sample.fiveHourRemainingPercent,
                fiveHourWindowMinutes: sample.fiveHourWindowMinutes,
                fiveHourResetsAt: sample.fiveHourResetsAt
            )
        }
        historicalSource = true
    }

    mutating func capture(
        capturedAt: Date,
        cumulativeTokens: Int64,
        weeklyRemainingPercent: Double?,
        weeklyWindowMinutes: Int,
        weeklyResetsAt: Date?,
        fiveHourRemainingPercent: Double?,
        fiveHourWindowMinutes: Int,
        fiveHourResetsAt: Date?
    ) {
        prune(now: capturedAt)
        if let lastCaptureAt, capturedAt <= lastCaptureAt {
            if capturedAt == lastCaptureAt { return }
            resetAll(captureAt: capturedAt)
        }
        if chartOriginAt == nil { chartOriginAt = capturedAt }

        if let lastCaptureAt, capturedAt.timeIntervalSince(lastCaptureAt) > Self.maximumContinuousGap {
            rateSamples.removeAll()
            lastTokenRate = nil
            lastRateCalculationAt = nil
            weeklyQuota.markDiscontinuous()
            fiveHourQuota.markDiscontinuous()
            breakBeforeNextTokenPoint = true
            breakBeforeNextCounterSample = true
        }
        lastCaptureAt = capturedAt
        captureToken(at: capturedAt, cumulativeTokens: cumulativeTokens)
        weeklyQuota.capture(
            at: capturedAt,
            remainingPercent: weeklyRemainingPercent,
            windowMinutes: weeklyWindowMinutes,
            resetsAt: weeklyResetsAt
        )
        fiveHourQuota.capture(
            at: capturedAt,
            remainingPercent: fiveHourRemainingPercent,
            windowMinutes: fiveHourWindowMinutes,
            resetsAt: fiveHourResetsAt
        )
    }

    /// Preserve the old single-quota entry point for callers compiled against
    /// the pre-5H chart API.  Those values are treated as the weekly series.
    mutating func capture(
        capturedAt: Date,
        cumulativeTokens: Int64,
        remainingPercent: Double?,
        windowMinutes: Int,
        resetsAt: Date?
    ) {
        capture(
            capturedAt: capturedAt,
            cumulativeTokens: cumulativeTokens,
            weeklyRemainingPercent: remainingPercent,
            weeklyWindowMinutes: windowMinutes,
            weeklyResetsAt: resetsAt,
            fiveHourRemainingPercent: nil,
            fiveHourWindowMinutes: 0,
            fiveHourResetsAt: nil
        )
    }

    mutating func captureFailure(capturedAt: Date) {
        prune(now: capturedAt)
        if let lastCaptureAt, capturedAt <= lastCaptureAt {
            if capturedAt == lastCaptureAt { return }
            resetAll(captureAt: capturedAt)
            return
        }
        if chartOriginAt == nil { chartOriginAt = capturedAt }
        if let lastCaptureAt, capturedAt.timeIntervalSince(lastCaptureAt) > Self.maximumContinuousGap {
            rateSamples.removeAll()
            lastTokenRate = nil
            lastRateCalculationAt = nil
            weeklyQuota.markDiscontinuous()
            fiveHourQuota.markDiscontinuous()
            breakBeforeNextTokenPoint = true
            breakBeforeNextCounterSample = true
            self.lastCaptureAt = capturedAt
            return
        }
        lastCaptureAt = capturedAt
        appendTokenHold(at: capturedAt)
        weeklyQuota.appendHold(at: capturedAt)
        fiveHourQuota.appendHold(at: capturedAt)
        pruneRateSamples(now: capturedAt)
    }

    mutating func clear() { resetAll(captureAt: nil) }

    func renderSnapshot(now: Date = Date()) -> ChartRenderSnapshot {
        let timelineStart = self.timelineStart(now: now)
        let timelineEnd = (customViewFrom != nil && customViewTo != nil) ? customViewTo! : now
        let selectedTokens = Self.select(tokenPoints, from: timelineStart, to: timelineEnd)
        let selectedQuota = Self.select(weeklyQuota.points, from: timelineStart, to: timelineEnd)
        let selectedQuotaSource = Self.selectWithBaseline(weeklyQuota.sourcePoints, from: timelineStart, to: timelineEnd)
        let selectedFiveHourQuota = Self.select(fiveHourQuota.points, from: timelineStart, to: timelineEnd)
        let cumulativePoints = buildCumulativePoints(from: timelineStart, to: timelineEnd)
        let currentQuota = weeklyQuota.currentValue ?? selectedQuota.last?.value
        let currentFiveHourQuota = fiveHourQuota.currentValue ?? selectedFiveHourQuota.last?.value
        let cumulativeIncrease = calculatePeriodIncrease(from: timelineStart, to: timelineEnd)
        let peakRate = selectedTokens.map(\.value).max() ?? 0
        let duration = customViewFrom != nil && customViewTo != nil
            ? timelineEnd.timeIntervalSince(timelineStart)
            : Double(displayHours) * 60 * 60

        return ChartRenderSnapshot(
            tokenPoints: selectedTokens,
            quotaPoints: selectedQuota,
            fiveHourQuotaPoints: selectedFiveHourQuota,
            cumulativePoints: cumulativePoints,
            tokenAxisMaximum: Self.calculateRoundedTokenAxisMaximum(peakRate),
            cumulativeAxisMaximum: Self.calculateCumulativeAxis(cumulativePoints),
            currentQuota: currentQuota,
            currentFiveHourQuota: currentFiveHourQuota,
            currentTokenRate: selectedTokens.last?.value ?? (historicalSource ? nil : lastTokenRate),
            peakTokenRate: peakRate,
            cumulativeIncrease: cumulativeIncrease,
            quotaConsumedDuringRuntime: QuotaSeries.calculateConsumption(selectedQuotaSource, carryAcrossHistoricalGaps: historicalSource),
            timelineStart: timelineStart,
            timelineEnd: timelineStart.addingTimeInterval(duration),
            displayDuration: duration,
            historical: historicalSource
        )
    }

    private mutating func captureToken(at: Date, cumulativeTokens: Int64) {
        guard cumulativeTokens >= 0 else { appendTokenHold(at: at); return }
        let sourceDay = at.localDay
        guard hasSourceCounter else {
            hasSourceCounter = true
            lastSourceCounter = cumulativeTokens
            lastSourceDay = sourceDay
            normalizedCounter = 0
            addCounterSample(at: at, breakBefore: true)
            return
        }
        if sourceDay != lastSourceDay {
            lastSourceDay = sourceDay
            lastSourceCounter = cumulativeTokens
            addCounterSample(at: at, breakBefore: breakBeforeNextCounterSample)
            appendTokenHold(at: at)
            return
        }
        if cumulativeTokens < lastSourceCounter {
            appendTokenHold(at: at)
            return
        }
        let sourceDelta = cumulativeTokens - lastSourceCounter
        lastSourceCounter = cumulativeTokens
        let (updatedCounter, overflow) = normalizedCounter.addingReportingOverflow(sourceDelta)
        if overflow {
            rateSamples.removeAll()
            hasSourceCounter = false
            breakBeforeNextTokenPoint = true
            appendTokenHold(at: at)
            return
        }
        normalizedCounter = updatedCounter
        addCounterSample(at: at, breakBefore: breakBeforeNextCounterSample)
        rateSamples.append(TokenCounterSample(at: at, cumulativeTokens: normalizedCounter, breakBefore: false))
        pruneRateSamples(now: at)
        guard let baseline = tokenBaseline(for: at) else { appendTokenHold(at: at); return }
        let elapsed = at.timeIntervalSince(baseline.at)
        let delta = normalizedCounter - baseline.cumulativeTokens
        guard elapsed > 0, delta >= 0 else { appendTokenHold(at: at); return }
        let rate = Double(delta) * 60 / elapsed
        guard rate.isFinite, rate >= 0 else { appendTokenHold(at: at); return }
        let firstCalculatedRate = lastTokenRate == nil
        let stabilized = stabilizeRate(at: at, rawRate: rate)
        if firstCalculatedRate { appendTokenPoint(at: baseline.at, value: stabilized, forceBreakBefore: false) }
        appendTokenPoint(at: at, value: stabilized, forceBreakBefore: false)
    }

    private mutating func stabilizeRate(at: Date, rawRate: Double) -> Double {
        guard let previous = lastTokenRate, let lastRateCalculationAt, at > lastRateCalculationAt else {
            lastRateCalculationAt = at
            return rawRate
        }
        let elapsed = at.timeIntervalSince(lastRateCalculationAt)
        let alpha = max(0.35, min(0.80, 1 - exp(-elapsed / Self.smoothingTime)))
        self.lastRateCalculationAt = at
        return previous + alpha * (rawRate - previous)
    }

    private mutating func addCounterSample(at: Date, breakBefore: Bool) {
        counterHistory.append(TokenCounterSample(at: at, cumulativeTokens: normalizedCounter, breakBefore: breakBefore))
        breakBeforeNextCounterSample = false
        if rateSamples.isEmpty {
            rateSamples.append(TokenCounterSample(at: at, cumulativeTokens: normalizedCounter, breakBefore: false))
        }
    }

    /// A quota stream is deliberately reusable: live and history replay use
    /// the same state machine for the weekly base curve and the 5H curve.
    private struct QuotaSeries: Sendable {
        private static let jitterTolerance = 0.35
        private static let resetRiseThreshold = 2.0
        private static let consumptionEpsilon = 0.01
        private static let smoothingTime: TimeInterval = 30
        private static let pointBucketDuration: TimeInterval = 30
        private static let maximumContinuousGap: TimeInterval = 120

        fileprivate var points = [ChartPoint]()
        fileprivate var sourcePoints = [ChartPoint]()
        private var lastRemaining: Double?
        private var lastCalculationAt: Date?
        private var breakBeforeNextPoint = false
        private var hasSource = false
        private var lastSource = 0.0
        private var consumptionReferenceRemaining = 0.0
        private var windowMinutes = 0
        private var resetsAt: Date?

        var currentValue: Double? { hasSource ? lastSource : nil }

        mutating func capture(at: Date, remainingPercent: Double?, windowMinutes: Int, resetsAt: Date?) {
            guard let supplied = remainingPercent, windowMinutes > 0,
                  supplied.isFinite, supplied >= -0.01, supplied <= 100.01
            else {
                appendHold(at: at)
                return
            }

            let remaining = max(0, min(100, supplied))
            guard hasSource else {
                startWindow(
                    at: at,
                    remaining: remaining,
                    windowMinutes: windowMinutes,
                    resetsAt: resetsAt,
                    breakBefore: breakBeforeNextPoint
                )
                return
            }

            let identityChanged = windowMinutes != self.windowMinutes || resetsAt != self.resetsAt
            let rise = remaining - lastSource
            if identityChanged || rise > Self.resetRiseThreshold {
                startWindow(
                    at: at,
                    remaining: remaining,
                    windowMinutes: windowMinutes,
                    resetsAt: resetsAt,
                    breakBefore: true
                )
                return
            }

            if rise > Self.jitterTolerance {
                lastSource = remaining
                lastCalculationAt = at
                appendHold(at: at)
                return
            }

            lastSource = remaining
            appendSourcePoint(at: at, value: remaining, breakBefore: false)
            appendRenderedPoint(at: at, value: stabilize(at: at, rawRemaining: remaining), forceBreakBefore: false)
        }

        mutating func appendHold(at: Date) {
            guard let lastRemaining else { return }
            guard !Self.hasLongGap(points, at: at) else {
                breakBeforeNextPoint = true
                return
            }
            appendRenderedPoint(at: at, value: lastRemaining, forceBreakBefore: false)
        }

        mutating func markDiscontinuous() {
            lastRemaining = nil
            lastCalculationAt = nil
            hasSource = false
            breakBeforeNextPoint = true
        }

        mutating func reset() {
            points.removeAll()
            sourcePoints.removeAll()
            lastRemaining = nil
            lastCalculationAt = nil
            breakBeforeNextPoint = false
            hasSource = false
            lastSource = 0
            consumptionReferenceRemaining = 0
            windowMinutes = 0
            resetsAt = nil
        }

        mutating func prune(olderThan oldest: Date) {
            Self.prunePoints(&points, olderThan: oldest)
            Self.prunePoints(&sourcePoints, olderThan: oldest)
        }

        static func calculateConsumption(_ points: [ChartPoint], carryAcrossHistoricalGaps: Bool) -> Double {
            guard points.count >= 2 else { return 0 }
            var total = 0.0
            var reference = points[0].value
            for point in points.dropFirst() {
                if point.breakBefore && !carryAcrossHistoricalGaps {
                    reference = point.value
                    continue
                }
                if point.breakBefore && point.value > reference {
                    reference = point.value
                    continue
                }
                if point.value < reference - consumptionEpsilon {
                    total += reference - point.value
                    reference = point.value
                }
            }
            return total
        }

        private mutating func startWindow(
            at: Date,
            remaining: Double,
            windowMinutes: Int,
            resetsAt: Date?,
            breakBefore: Bool
        ) {
            hasSource = true
            lastSource = remaining
            consumptionReferenceRemaining = remaining
            self.windowMinutes = windowMinutes
            self.resetsAt = resetsAt
            lastCalculationAt = at
            appendSourcePoint(at: at, value: remaining, breakBefore: breakBefore || sourcePoints.isEmpty)
            appendRenderedPoint(at: at, value: remaining, forceBreakBefore: breakBefore || points.isEmpty)
        }

        private mutating func stabilize(at: Date, rawRemaining: Double) -> Double {
            guard let previous = lastRemaining, let lastCalculationAt, at > lastCalculationAt else {
                lastCalculationAt = at
                return rawRemaining
            }
            let elapsed = at.timeIntervalSince(lastCalculationAt)
            let alpha = max(0.35, min(0.80, 1 - exp(-elapsed / Self.smoothingTime)))
            self.lastCalculationAt = at
            return previous + alpha * (rawRemaining - previous)
        }

        private mutating func appendRenderedPoint(at: Date, value: Double, forceBreakBefore: Bool) {
            Self.appendContinuousPoint(
                &points,
                at: at,
                value: value,
                breakBefore: forceBreakBefore || breakBeforeNextPoint
            )
            lastRemaining = value
            breakBeforeNextPoint = false
        }

        private mutating func appendSourcePoint(at: Date, value: Double, breakBefore: Bool) {
            Self.appendPoint(&sourcePoints, at: at, value: value, breakBefore: breakBefore)
        }

        private static func appendContinuousPoint(_ points: inout [ChartPoint], at: Date, value: Double, breakBefore: Bool) {
            if let previous = points.last {
                guard at > previous.at else { return }
                if samePointBucket(previous.at, at) {
                    points[points.count - 1] = ChartPoint(
                        at: at,
                        value: value,
                        breakBefore: breakBefore || previous.breakBefore,
                        sampleCount: 1
                    )
                    return
                }
            }
            points.append(ChartPoint(at: at, value: value, breakBefore: breakBefore || points.isEmpty, sampleCount: 1))
        }

        private static func appendPoint(_ points: inout [ChartPoint], at: Date, value: Double, breakBefore initialBreak: Bool) {
            var breakBefore = initialBreak
            if let previous = points.last {
                guard at > previous.at else { return }
                if at.timeIntervalSince(previous.at) > maximumContinuousGap { breakBefore = true }
                if !breakBefore && !previous.breakBefore && samePointBucket(previous.at, at) {
                    points[points.count - 1] = ChartPoint(at: at, value: value, breakBefore: previous.breakBefore, sampleCount: 1)
                    return
                }
            }
            points.append(ChartPoint(at: at, value: value, breakBefore: breakBefore, sampleCount: 1))
        }

        private static func prunePoints(_ points: inout [ChartPoint], olderThan oldest: Date) {
            let count = points.prefix { $0.at < oldest }.count
            if count >= 64 || count == points.count, count > 0 { points.removeFirst(count) }
        }

        private static func hasLongGap(_ points: [ChartPoint], at: Date) -> Bool {
            guard let last = points.last else { return false }
            return at.timeIntervalSince(last.at) > maximumContinuousGap
        }

        private static func samePointBucket(_ lhs: Date, _ rhs: Date) -> Bool {
            Int64(lhs.timeIntervalSince1970 / pointBucketDuration) == Int64(rhs.timeIntervalSince1970 / pointBucketDuration)
        }
    }

    private mutating func resetAll(captureAt: Date?) {
        rateSamples.removeAll()
        counterHistory.removeAll()
        tokenPoints.removeAll()
        weeklyQuota.reset()
        fiveHourQuota.reset()
        lastCaptureAt = captureAt
        chartOriginAt = captureAt
        clearSelection()
        historicalSource = false
        lastTokenRate = nil
        lastRateCalculationAt = nil
        breakBeforeNextTokenPoint = false
        hasSourceCounter = false
        lastSourceCounter = 0
        lastSourceDay = nil
        normalizedCounter = 0
        breakBeforeNextCounterSample = false
    }

    private mutating func appendTokenHold(at: Date) {
        guard let lastTokenRate else { return }
        if Self.hasLongGap(tokenPoints, at: at) {
            breakBeforeNextTokenPoint = true
            return
        }
        appendTokenPoint(at: at, value: lastTokenRate, forceBreakBefore: false)
    }

    private static func hasLongGap(_ points: [ChartPoint], at: Date) -> Bool {
        guard let last = points.last else { return false }
        return at.timeIntervalSince(last.at) > maximumContinuousGap
    }

    private mutating func appendTokenPoint(at: Date, value: Double, forceBreakBefore: Bool) {
        Self.appendRatePoint(&tokenPoints, at: at, value: value, breakBefore: forceBreakBefore || breakBeforeNextTokenPoint)
        lastTokenRate = value
        breakBeforeNextTokenPoint = false
    }

    private static func appendRatePoint(_ points: inout [ChartPoint], at: Date, value: Double, breakBefore initialBreak: Bool) {
        var breakBefore = initialBreak
        if let previous = points.last {
            guard at > previous.at else { return }
            if at.timeIntervalSince(previous.at) > maximumContinuousGap { breakBefore = true }
            if !breakBefore && !previous.breakBefore && samePointBucket(previous.at, at) {
                let sampleCount = previous.sampleCount + 1
                let average = (previous.value * Double(previous.sampleCount) + value) / Double(sampleCount)
                points[points.count - 1] = ChartPoint(at: at, value: average, breakBefore: previous.breakBefore, sampleCount: sampleCount)
                return
            }
        }
        points.append(ChartPoint(at: at, value: value, breakBefore: breakBefore, sampleCount: 1))
    }

    private static func appendPoint(_ points: inout [ChartPoint], at: Date, value: Double, breakBefore initialBreak: Bool) {
        var breakBefore = initialBreak
        if let previous = points.last {
            guard at > previous.at else { return }
            if at.timeIntervalSince(previous.at) > maximumContinuousGap { breakBefore = true }
            if !breakBefore && !previous.breakBefore && samePointBucket(previous.at, at) {
                points[points.count - 1] = ChartPoint(at: at, value: value, breakBefore: previous.breakBefore, sampleCount: 1)
                return
            }
        }
        points.append(ChartPoint(at: at, value: value, breakBefore: breakBefore, sampleCount: 1))
    }

    private static func samePointBucket(_ lhs: Date, _ rhs: Date) -> Bool {
        Int64(lhs.timeIntervalSince1970 / pointBucketDuration) == Int64(rhs.timeIntervalSince1970 / pointBucketDuration)
    }

    private func tokenBaseline(for current: Date) -> TokenCounterSample? {
        var best: TokenCounterSample?
        var bestDifference = Double.greatestFiniteMagnitude
        for candidate in rateSamples.dropLast() {
            let elapsed = current.timeIntervalSince(candidate.at)
            guard elapsed >= Self.minimumWindow, elapsed <= Self.maximumWindow else { continue }
            let difference = abs(elapsed - Self.targetWindow)
            if difference < bestDifference {
                bestDifference = difference
                best = candidate
            }
        }
        return best
    }

    private mutating func pruneRateSamples(now: Date) {
        let oldest = now.addingTimeInterval(-Self.maximumWindow - Self.rawSampleSlack)
        let count = rateSamples.prefix { $0.at < oldest }.count
        if count > 0 { rateSamples.removeFirst(count) }
    }

    private mutating func prune(now: Date) {
        let oldest = now.addingTimeInterval(-Self.maximumDisplayDuration - Self.retentionSlack)
        Self.prunePoints(&tokenPoints, olderThan: oldest)
        weeklyQuota.prune(olderThan: oldest)
        fiveHourQuota.prune(olderThan: oldest)
        Self.pruneCounterSamples(&counterHistory, olderThan: oldest)
        pruneRateSamples(now: now)
    }

    private static func prunePoints(_ points: inout [ChartPoint], olderThan oldest: Date) {
        let count = points.prefix { $0.at < oldest }.count
        if count >= 64 || count == points.count, count > 0 { points.removeFirst(count) }
    }

    private static func pruneCounterSamples(_ samples: inout [TokenCounterSample], olderThan oldest: Date) {
        var count = 0
        while count + 1 < samples.count && samples[count + 1].at < oldest { count += 1 }
        if count >= 64 || count == samples.count - 1, count > 0 { samples.removeFirst(count) }
    }

    private func timelineStart(now: Date) -> Date {
        if let customViewFrom, customViewTo != nil { return customViewFrom }
        let slidingStart = now.addingTimeInterval(-Double(displayHours) * 60 * 60)
        guard let chartOriginAt else { return slidingStart }
        if chartOriginAt < slidingStart { return slidingStart }
        return chartOriginAt > now ? now : chartOriginAt
    }

    private func buildCumulativePoints(from: Date, to: Date) -> [ChartPoint] {
        guard let baseline = periodBaseline(from: from) else { return [] }
        var output = [ChartPoint]()
        for sample in counterHistory where sample.at >= from && sample.at <= to {
            let value = max(0, Double(sample.cumulativeTokens - baseline.cumulativeTokens))
            if historicalSource {
                output.append(ChartPoint(at: sample.at, value: value, breakBefore: sample.breakBefore, sampleCount: 1))
            } else {
                Self.appendPoint(&output, at: sample.at, value: value, breakBefore: sample.breakBefore)
            }
        }
        return Self.smoothCumulativePoints(output)
    }

    private static func smoothCumulativePoints(_ source: [ChartPoint]) -> [ChartPoint] {
        guard source.count >= 4 else { return source }
        var output = [ChartPoint]()
        var segmentStart = 0
        while segmentStart < source.count {
            var segmentEnd = segmentStart + 1
            while segmentEnd < source.count && !source[segmentEnd].breakBefore { segmentEnd += 1 }
            smoothCumulativeSegment(source, start: segmentStart, end: segmentEnd, into: &output)
            segmentStart = segmentEnd
        }
        return output
    }

    private static func smoothCumulativeSegment(_ source: [ChartPoint], start: Int, end: Int, into output: inout [ChartPoint]) {
        let count = end - start
        guard count >= 4 else {
            output.append(contentsOf: source[start..<end])
            return
        }
        let increments = (0..<(count - 1)).map { max(0, source[start + $0 + 1].value - source[start + $0].value) }
        var smoothed = [Double](repeating: 0, count: increments.count)
        var total = 0.0
        for index in increments.indices {
            var weighted = 0.0
            var weights = 0.0
            for offset in -1...1 {
                let sourceIndex = index + offset
                guard increments.indices.contains(sourceIndex) else { continue }
                let weight = Double(2 - abs(offset))
                weighted += increments[sourceIndex] * weight
                weights += weight
            }
            smoothed[index] = weights > 0 ? weighted / weights : increments[index]
            total += smoothed[index]
        }
        let first = source[start]
        let exactIncrease = max(0, source[end - 1].value - first.value)
        let scale = total > 0 ? exactIncrease / total : 0
        var value = first.value
        output.append(first)
        for index in 1..<count {
            value += smoothed[index - 1] * scale
            if index == count - 1 { value = source[end - 1].value }
            let point = source[start + index]
            output.append(ChartPoint(at: point.at, value: value, breakBefore: point.breakBefore, sampleCount: point.sampleCount))
        }
    }

    private func periodBaseline(from: Date) -> TokenCounterSample? {
        var baseline: TokenCounterSample?
        for sample in counterHistory {
            if sample.at <= from { baseline = sample }
            else {
                if baseline == nil { baseline = sample }
                break
            }
        }
        return baseline
    }

    private func calculatePeriodIncrease(from: Date, to: Date) -> Double {
        guard counterHistory.count >= 2, let baseline = periodBaseline(from: from) else { return 0 }
        let latest = counterHistory.last { $0.at <= to }
        guard let latest, latest.at > baseline.at else { return 0 }
        return max(0, Double(latest.cumulativeTokens - baseline.cumulativeTokens))
    }

    private static func calculateCumulativeAxis(_ points: [ChartPoint]) -> Double {
        calculateRoundedCumulativeAxisMaximum(points.map(\.value).max() ?? 0)
    }

    private static func calculateRoundedCumulativeAxisMaximum(_ peak: Double) -> Double {
        guard peak.isFinite, peak > 0 else { return minimumTokenAxisMaximum }
        let target = max(minimumTokenAxisMaximum, peak / targetPeakAxisRatio)
        let step = niceStep(target / 40)
        return max(minimumTokenAxisMaximum, ceil(target / step) * step)
    }

    private static func niceStep(_ value: Double) -> Double {
        guard value > 0 else { return 1 }
        let exponent = floor(log10(value))
        let scale = pow(10, exponent)
        let normalized = value / scale
        if normalized <= 1 { return scale }
        if normalized <= 2 { return 2 * scale }
        if normalized <= 5 { return 5 * scale }
        return 10 * scale
    }

    static func calculateRoundedTokenAxisMaximum(_ peak: Double) -> Double {
        guard peak.isFinite, peak > 0 else { return minimumTokenAxisMaximum }
        let target = max(minimumTokenAxisMaximum, peak / targetPeakAxisRatio)
        let roundedTarget = (target / tokenAxisRoundStep).rounded() * tokenAxisRoundStep
        let peakCeiling = ceil(peak / tokenAxisRoundStep) * tokenAxisRoundStep
        return max(tokenAxisRoundStep, max(roundedTarget, peakCeiling))
    }

    private static func select(_ points: [ChartPoint], from: Date, to: Date) -> [ChartPoint] {
        points.filter { $0.at >= from && $0.at <= to }.sorted { $0.at < $1.at }
    }

    private static func selectWithBaseline(_ points: [ChartPoint], from: Date, to: Date) -> [ChartPoint] {
        var output = points.filter { $0.at > from && $0.at <= to }.sorted { $0.at < $1.at }
        if let baseline = points.last(where: { $0.at <= from }) { output.insert(baseline, at: 0) }
        return output
    }

}

private struct TokenCounterSample: Sendable {
    let at: Date
    let cumulativeTokens: Int64
    let breakBefore: Bool
}
