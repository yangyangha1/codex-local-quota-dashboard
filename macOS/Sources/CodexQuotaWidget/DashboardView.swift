import AppKit
import SwiftUI

struct DashboardView: View {
    @ObservedObject var model: DashboardViewModel
    @State private var chartBrush: ChartBrush?

    private var isLight: Bool { model.theme == .light }
    private var backgroundColor: Color { isLight ? Color(red: 0.925, green: 0.96, blue: 0.98) : Color(red: 0.10, green: 0.13, blue: 0.15) }
    private var primaryColor: Color { isLight ? .black : Color(red: 0.95, green: 0.97, blue: 0.98) }
    private var mutedColor: Color { isLight ? Color(red: 0.35, green: 0.40, blue: 0.46) : Color(red: 0.56, green: 0.60, blue: 0.66) }
    private let modeStripHeight: CGFloat = 42
    private var quotaHeadlineFont: Font {
        Font(NSFont(name: "PingFangSC-Semibold", size: 22) ?? NSFont.systemFont(ofSize: 22, weight: .semibold))
    }

    var body: some View {
        GeometryReader { geometry in
            ZStack {
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .fill(backgroundColor.opacity(max(0.01, (100 - model.backgroundTransparency) / 100)))

                VStack(spacing: 8) {
                    header
                    if model.contentMode != .detail {
                        modeStrip
                    }
                    Divider().overlay(mutedColor.opacity(0.32))
                    content
                }
                .padding(.horizontal, 14)
                .padding(.top, 7)
                .padding(.bottom, 12)
            }
            .frame(width: geometry.size.width, height: geometry.size.height)
            .contentShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
            .contextMenu { contextMenu }
        }
        .preferredColorScheme(isLight ? .light : .dark)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack(alignment: .center, spacing: 5) {
                Text(model.primaryQuota.map { "GPT·剩余\(wholePercent($0.remainingPercent))%" } ?? "GPT·暂无缓存")
                    .font(quotaHeadlineFont)
                    .foregroundStyle(primaryColor)
                    .lineLimit(1)
                    .minimumScaleFactor(0.82)
                    .layoutPriority(1)
                    .help(model.snapshot.quotas.isEmpty ? "等待 Codex 写入本地限额信息" : model.snapshot.quotas.map { "\(quotaWindowName($0.windowMinutes))：已用 \(wholePercent($0.usedPercent))%" }.joined(separator: "\n"))
                WindowDragHandle()
                    .frame(minWidth: 24, maxWidth: .infinity, minHeight: 28, maxHeight: 28)
                    .help("按住此区域拖动窗口")
                HStack(spacing: 5) {
                    Button("历史") { model.showHistory() }
                        .buttonStyle(WidgetButtonStyle(active: model.contentMode == .history, light: isLight))
                        .fixedSize(horizontal: true, vertical: false)
                        .help(model.contentMode == .history ? "关闭历史数据" : "查看历史数据")
                    Button("明细") { model.showDetail() }
                        .buttonStyle(WidgetButtonStyle(active: model.contentMode == .detail, light: isLight))
                        .fixedSize(horizontal: true, vertical: false)
                        .help(model.contentMode == .detail ? "关闭项目明细" : "查看项目明细")
                }
                .fixedSize(horizontal: true, vertical: false)
            }

            QuotaProgressBar(value: model.primaryQuota?.remainingPercent ?? 0, track: isLight ? Color(red: 0.83, green: 0.85, blue: 0.89) : Color.white.opacity(0.16))
                .frame(height: 9)

            Text(quotaSubtitle)
                .font(.system(size: 10, weight: .medium))
                .foregroundStyle(mutedColor)
                .lineLimit(1)
        }
    }

    private var quotaSubtitle: String {
        guard let quota = model.primaryQuota else {
            return model.isRefreshing ? "正在扫描本地日志" : "等待 Codex 写入限额信息"
        }
        let reset = quota.resetsAt.map { date in
            let formatter = DateFormatter()
            formatter.dateFormat = "M月d日 HH:mm"
            return formatter.string(from: date)
        } ?? "未知"
        return "已用 \(wholePercent(quota.usedPercent))% · 重置 \(reset)"
    }

    private var metrics: some View {
        HStack(spacing: 0) {
            MetricView(title: "今日", value: compactTokens(model.snapshot.today.total), primary: primaryColor, muted: mutedColor)
            MetricView(title: "近 7 天", value: compactTokens(model.snapshot.week.total), primary: primaryColor, muted: mutedColor)
            MetricView(title: "近 30 天", value: compactTokens(model.snapshot.month.total), primary: primaryColor, muted: mutedColor)
        }
        .frame(maxWidth: .infinity, minHeight: modeStripHeight, maxHeight: modeStripHeight, alignment: .center)
    }

    @ViewBuilder
    private var modeStrip: some View {
        switch model.contentMode {
        case .history:
            historyControls
        case .live, .detail:
            metrics
        }
    }

    private var detailMaximumTokens: Int64 {
        max(1, model.displayedProjects.map(\.totalTokens).max() ?? 1)
    }

    @ViewBuilder
    private var content: some View {
        switch model.contentMode {
        case .live:
            chartPanel(snapshot: model.liveChartSnapshot, allowsHistoryInteraction: false)
        case .detail:
            detailPanel
        case .history:
            chartPanel(snapshot: model.historyChartSnapshot, allowsHistoryInteraction: true)
        }
    }

    private func chartPanel(snapshot: ChartRenderSnapshot, allowsHistoryInteraction: Bool) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            ChartInfoLine(snapshot: snapshot)
            ZStack {
                UsageChartCanvas(snapshot: snapshot, isLight: isLight)
                if allowsHistoryInteraction {
                    ChartSelectionOverlay(selection: chartBrush)
                    ChartInteractionOverlay(
                        onWheel: {
                            chartBrush = nil
                            model.zoomChart(delta: $0)
                        },
                        onSelectionPreview: { start, end in
                            if let start, let end {
                                chartBrush = ChartBrush(start: start, end: end)
                            } else {
                                chartBrush = nil
                            }
                        },
                        onSelection: {
                            chartBrush = nil
                            model.selectChartRange(startFraction: $0, endFraction: $1)
                        }
                    )
                } else {
                    // Keep the live panel's left-clicks free for window
                    // dragging, while still retaining the original wheel-only
                    // timeline changes (1h ... 48h).
                    ChartWheelOverlay { delta in
                        model.zoomChart(delta: delta)
                    }
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var detailPanel: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Text("项目明细 · \(compactTokens(model.displayedProjects.reduce(0) { $0 &+ $1.totalTokens }))")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(primaryColor)
                Spacer()
                Button(model.detailShowsAll ? "仅今天" : "显示全部") {
                    model.detailShowsAll.toggle()
                }
                .buttonStyle(WidgetButtonStyle(active: model.detailShowsAll, light: isLight))
                .help(model.detailShowsAll ? "只显示今天有活动的会话" : "显示所有日期的会话")
                Button("×") { model.showLive() }
                    .buttonStyle(.plain)
                    .foregroundStyle(mutedColor)
                    .help("关闭明细")
            }

            if model.isDetailLoading {
                Spacer()
                ProgressView("正在读取本地项目与会话明细…")
                    .font(.system(size: 12))
                    .frame(maxWidth: .infinity)
                Spacer()
            } else if model.displayedProjects.isEmpty {
                Spacer()
                Text(model.detailShowsAll ? "暂无可用项目明细" : "今天暂无可用项目明细")
                    .font(.system(size: 12))
                    .foregroundStyle(mutedColor)
                    .frame(maxWidth: .infinity)
                Spacer()
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 4) {
                        ForEach(model.displayedProjects) { project in
                            ProjectDetailRow(
                                project: project,
                                maximumProjectTokens: detailMaximumTokens,
                                primary: primaryColor,
                                muted: mutedColor,
                                light: isLight,
                                onOpenProject: { model.openProject(project) },
                                onRevealSession: model.revealSession
                            )
                        }
                    }
                    .padding(.vertical, 0)
                }
            }
        }
    }

    private var historyControls: some View {
        VStack(alignment: .leading, spacing: 1) {
            ZStack {
                Button("历史数据") { model.openHistoryFolder() }
                    .buttonStyle(.plain)
                    .font(.system(size: 12, weight: .bold))
                    .foregroundStyle(primaryColor)
                    .help("打开历史数据目录")
                HStack {
                    Button("‹") { model.shiftHistoryWeek(by: -1) }
                        .buttonStyle(.plain)
                        .foregroundStyle(mutedColor)
                    Spacer()
                    HStack(spacing: 6) {
                        if model.historyHasChartSelection {
                            Button("恢复") { model.clearHistoryChartSelection() }
                                .buttonStyle(.plain)
                                .font(.system(size: 9, weight: .semibold))
                                .foregroundStyle(Color.accentColor)
                                .help("取消框选，恢复历史时间范围")
                        }
                        if model.isHistoryLoading {
                            ProgressView().controlSize(.mini)
                        }
                        Button("›") { model.shiftHistoryWeek(by: 1) }
                            .buttonStyle(.plain)
                            .foregroundStyle(mutedColor)
                            .disabled(model.visibleHistoryWeekStart >= weekStart(for: Date()))
                        Button("×") { model.showLive() }
                            .buttonStyle(.plain)
                            .foregroundStyle(mutedColor)
                            .help("关闭历史")
                    }
                }
            }
            HistoryWeekStrip(model: model, primary: primaryColor, muted: mutedColor, light: isLight)
        }
        .frame(maxWidth: .infinity, minHeight: modeStripHeight, maxHeight: modeStripHeight, alignment: .top)
    }

    @ViewBuilder
    private var contextMenu: some View {
        Button("立即刷新") { model.refresh() }
        Button(model.theme == .dark ? "切换为浅色" : "切换为深色") {
            model.theme = model.theme == .dark ? .light : .dark
        }
        Divider()
        Menu("背景透明度") {
            ForEach([0, 10, 30, 50, 70, 90, 100], id: \.self) { value in
                Button("\(value)%") { model.backgroundTransparency = Double(value) }
            }
        }
        Toggle("窗口置顶", isOn: $model.topMost)
        Toggle(
            "开机自动启动",
            isOn: Binding(
                get: { model.launchAtLoginEnabled },
                set: { _ = model.setLaunchAtLogin($0) }
            )
        )
        Divider()
        Button("隐藏") { model.onHideRequested?() }
        Button("退出") { NSApp.terminate(nil) }
    }

    private func weekStart(for date: Date) -> Date {
        var calendar = Calendar.current
        calendar.firstWeekday = 2
        return calendar.dateInterval(of: .weekOfYear, for: date)?.start ?? date.localDay
    }

}

private struct MetricView: View {
    let title: String
    let value: String
    let primary: Color
    let muted: Color

    var body: some View {
        VStack(alignment: .center, spacing: 1) {
            Text(title).font(.system(size: 10, weight: .semibold)).foregroundStyle(muted)
            Text(value).font(.system(size: 16, weight: .bold, design: .rounded)).foregroundStyle(primary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
    }
}

private struct QuotaProgressBar: View {
    let value: Double
    let track: Color

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                Capsule().fill(track)
                Capsule()
                    .fill(quotaColor(value))
                    .frame(width: max(0, geometry.size.width * min(1, max(0, value / 100))))
            }
        }
    }

    private func quotaColor(_ remaining: Double) -> Color {
        // Exact continuous upstream stops: red at 0%, moving
        // through orange/yellow to green at 100%, rather than discrete bands.
        let value = min(100, max(0, remaining))
        let stops: [Double] = [0, 10, 30, 35, 50, 65, 80, 100]
        let colors: [(Double, Double, Double)] = [
            (211, 61, 61),
            (224, 75, 68),
            (229, 103, 58),
            (232, 145, 53),
            (224, 174, 57),
            (164, 197, 72),
            (91, 201, 117),
            (73, 205, 143)
        ]
        for index in 0..<(stops.count - 1) where value <= stops[index + 1] {
            let amount = (value - stops[index]) / (stops[index + 1] - stops[index])
            let from = colors[index]
            let to = colors[index + 1]
            return Color(
                red: (from.0 + (to.0 - from.0) * amount) / 255,
                green: (from.1 + (to.1 - from.1) * amount) / 255,
                blue: (from.2 + (to.2 - from.2) * amount) / 255
            )
        }
        let end = colors[colors.count - 1]
        return Color(red: end.0 / 255, green: end.1 / 255, blue: end.2 / 255)
    }
}

private struct WidgetButtonStyle: ButtonStyle {
    let active: Bool
    let light: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 10, weight: .semibold))
            .foregroundStyle(active ? Color.white : (light ? Color.black : Color.white))
            .padding(.horizontal, 9)
            .padding(.vertical, 5)
            .background(
                RoundedRectangle(cornerRadius: 5, style: .continuous)
                    .fill(active ? Color.accentColor : (light ? Color.black.opacity(0.08) : Color.white.opacity(0.10)))
            )
            .opacity(configuration.isPressed ? 0.65 : 1)
    }
}

private let quotaSeriesColor = Color(red: 0.20, green: 0.72, blue: 0.47)
private let rateSeriesColor = Color.gray
private let cumulativeSeriesColor = Color(red: 0.27, green: 0.62, blue: 0.96)
private let rateAreaColor = Color(red: 0.63, green: 0.48, blue: 0.84)

private struct ChartInfoLine: View {
    let snapshot: ChartRenderSnapshot

    var body: some View {
        HStack(spacing: 0) {
            Text("消耗额度 \(wholePercent(snapshot.quotaConsumedDuringRuntime))% · \(durationText)")
                .foregroundStyle(quotaSeriesColor)
            if showsRate {
                Spacer(minLength: 8)
                Text("速率 \(rateText)")
                    .foregroundStyle(rateSeriesColor)
            }
            Spacer(minLength: 8)
            Text("累计 Token \(compactRate(snapshot.cumulativeIncrease))")
                .foregroundStyle(cumulativeSeriesColor)
        }
        .font(.system(size: 10, weight: .semibold))
        .monospacedDigit()
        .lineLimit(1)
        .minimumScaleFactor(0.78)
    }

    private var durationText: String {
        let hours = snapshot.displayDuration / 3_600
        if abs(hours.rounded() - hours) < 0.05 {
            return "\(Int(hours.rounded()))H"
        }
        return String(format: "%.1fH", hours)
    }

    private var rateText: String {
        snapshot.currentTokenRate.map { "\(compactRate($0))/分" } ?? "—"
    }

    private var showsRate: Bool {
        !snapshot.historical && (snapshot.currentTokenRate ?? 0) > 0
    }
}

private struct UsageChartCanvas: View {
    let snapshot: ChartRenderSnapshot
    let isLight: Bool

    var body: some View {
        Canvas { context, size in
            let canvasRect = CGRect(origin: .zero, size: size).insetBy(dx: 1, dy: 1)
            guard canvasRect.width >= 20, canvasRect.height >= 20 else { return }
            let timeLabelHeight = min(17, max(13, canvasRect.height * 0.16))
            let plot = CGRect(
                x: canvasRect.minX,
                y: canvasRect.minY,
                width: canvasRect.width,
                height: max(18, canvasRect.height - timeLabelHeight)
            )
            let grid = isLight ? Color.black.opacity(0.10) : Color.white.opacity(0.13)
            let muted = isLight ? Color.black.opacity(0.46) : Color.white.opacity(0.58)
            let xAxisGridCells = 6
            let yAxisGridCells = 5
            for index in 0...xAxisGridCells {
                let x = plot.minX + plot.width * CGFloat(index) / CGFloat(xAxisGridCells)
                var vertical = Path()
                vertical.move(to: CGPoint(x: x, y: plot.minY))
                vertical.addLine(to: CGPoint(x: x, y: plot.maxY))
                context.stroke(vertical, with: .color(grid), lineWidth: 0.5)
            }
            for index in 0...yAxisGridCells {
                let y = plot.minY + plot.height * CGFloat(index) / CGFloat(yAxisGridCells)
                var horizontal = Path()
                horizontal.move(to: CGPoint(x: plot.minX, y: y))
                horizontal.addLine(to: CGPoint(x: plot.maxX, y: y))
                context.stroke(horizontal, with: .color(grid), lineWidth: 0.5)
            }

            // The dashboard now has one permanent graph: a gray rate line with a
            // restrained purple area, plus the quota and cumulative curves.
            let rateLine = rateSeriesColor.opacity(isLight ? 0.88 : 0.76)
            drawSeries(
                snapshot.tokenPoints,
                maximum: snapshot.tokenAxisMaximum,
                lineColor: rateLine,
                fillColor: rateAreaColor.opacity(isLight ? 0.16 : 0.12),
                in: plot,
                context: &context
            )
            drawSeries(snapshot.quotaPoints, maximum: 100, lineColor: quotaSeriesColor, in: plot, context: &context)
            drawSeries(snapshot.cumulativePoints, maximum: snapshot.cumulativeAxisMaximum, lineColor: cumulativeSeriesColor, in: plot, context: &context)
            drawScaleHints(in: plot, color: muted, context: &context)

            if snapshot.tokenPoints.isEmpty && snapshot.quotaPoints.isEmpty && snapshot.cumulativePoints.isEmpty {
                context.draw(
                    Text("等待连续数据")
                        .font(.system(size: 10, weight: .medium))
                        .foregroundColor(muted),
                    at: CGPoint(x: plot.midX, y: plot.midY)
                )
            }
            drawTimeLabels(in: canvasRect, plot: plot, color: muted, context: &context)
        }
        .background((isLight ? Color.black.opacity(0.025) : Color.white.opacity(0.035)))
    }

    private func drawScaleHints(in plot: CGRect, color: Color, context: inout GraphicsContext) {
        guard plot.width >= 210 else { return }
        guard snapshot.peakTokenRate > 0 || snapshot.cumulativeIncrease > 0 else { return }
        let rateHint = "峰值 \(compactRate(snapshot.peakTokenRate)) · 速率上限 \(compactRate(snapshot.tokenAxisMaximum))"
        let cumulativeHint = "累计上限 \(compactRate(snapshot.cumulativeAxisMaximum))"
        context.draw(
            Text(rateHint).font(.system(size: 8, weight: .regular)).foregroundColor(color),
            at: CGPoint(x: plot.minX + 4, y: plot.minY + 3),
            anchor: .topLeading
        )
        context.draw(
            Text(cumulativeHint).font(.system(size: 8, weight: .regular)).foregroundColor(color),
            at: CGPoint(x: plot.maxX - 4, y: plot.minY + 3),
            anchor: .topTrailing
        )
    }

    private func drawTimeLabels(in canvasRect: CGRect, plot: CGRect, color: Color, context: inout GraphicsContext) {
        guard snapshot.displayDuration > 0 else { return }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = snapshot.displayDuration > 24 * 60 * 60 ? "MM-dd" : "HH:mm"
        for index in 0...6 {
            let fraction = Double(index) / 6
            let at = snapshot.timelineStart.addingTimeInterval(snapshot.displayDuration * fraction)
            let x = plot.minX + plot.width * CGFloat(fraction)
            let anchor: UnitPoint = index == 0 ? .bottomLeading : index == 6 ? .bottomTrailing : .bottom
            context.draw(
                Text(formatter.string(from: at))
                    .font(.system(size: 9, weight: .medium))
                    .foregroundColor(color),
                at: CGPoint(x: x, y: canvasRect.maxY),
                anchor: anchor
            )
        }
    }

    private func drawSeries(
        _ points: [ChartPoint],
        maximum: Double,
        lineColor: Color,
        fillColor: Color? = nil,
        in rect: CGRect,
        context: inout GraphicsContext
    ) {
        guard maximum > 0, snapshot.displayDuration > 0 else { return }
        var segments = [[CGPoint]]()
        var segment = [CGPoint]()
        func point(_ item: ChartPoint) -> CGPoint {
            let xRatio = max(0, min(1, item.at.timeIntervalSince(snapshot.timelineStart) / snapshot.displayDuration))
            let yRatio = max(0, min(1, item.value / maximum))
            return CGPoint(x: rect.minX + rect.width * xRatio, y: rect.maxY - rect.height * yRatio)
        }
        for item in points {
            let location = point(item)
            if item.breakBefore, !segment.isEmpty {
                segments.append(segment)
                segment.removeAll(keepingCapacity: true)
            }
            segment.append(location)
        }
        if !segment.isEmpty { segments.append(segment) }
        for segment in segments where !segment.isEmpty {
            if let fillColor, segment.count > 1 {
                var fill = Path()
                fill.move(to: CGPoint(x: segment[0].x, y: rect.maxY))
                for location in segment { fill.addLine(to: location) }
                fill.addLine(to: CGPoint(x: segment[segment.count - 1].x, y: rect.maxY))
                fill.closeSubpath()
                context.fill(fill, with: .color(fillColor))
            }
            guard segment.count > 1 else { continue }
            var line = Path()
            line.move(to: segment[0])
            for location in segment.dropFirst() { line.addLine(to: location) }
            context.stroke(line, with: .color(lineColor), lineWidth: 1.75)
        }
    }
}

private struct HistoryWeekStrip: View {
    @ObservedObject var model: DashboardViewModel
    let primary: Color
    let muted: Color
    let light: Bool

    var body: some View {
        HStack(spacing: 3) {
            ForEach(days, id: \.self) { day in
                let available = model.availableHistoryDays.contains(day)
                Button {
                    model.selectHistoryDate(day)
                } label: {
                    VStack(spacing: 0) {
                        Text(weekday(day)).font(.system(size: 8, weight: .medium))
                        Text(day.formatted(.dateTime.day())).font(.system(size: 11, weight: available ? .bold : .regular))
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 1)
                    .foregroundStyle(available ? Color.accentColor : muted.opacity(0.65))
                    .background(
                        RoundedRectangle(cornerRadius: 4)
                            .stroke(day == model.historySelectedDate ? Color.accentColor : .clear, lineWidth: 1)
                    )
                }
                .buttonStyle(.plain)
                .disabled(!available)
            }
        }
    }

    private var days: [Date] {
        (0..<7).compactMap { Calendar.current.date(byAdding: .day, value: $0, to: model.visibleHistoryWeekStart) }
    }

    private func weekday(_ day: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = "EE"
        return formatter.string(from: day)
    }
}

private struct ProjectDetailRow: View {
    let project: ProjectUsage
    let maximumProjectTokens: Int64
    let primary: Color
    let muted: Color
    let light: Bool
    let onOpenProject: () -> Void
    let onRevealSession: (SessionUsage) -> Void
    @State private var expanded = false

    private var maximumSessionTokens: Int64 {
        max(1, project.sessions.map(\.totalTokens).max() ?? 1)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Button { expanded.toggle() } label: {
                    HStack(spacing: 6) {
                        Image(systemName: expanded ? "chevron.down.circle.fill" : "chevron.right.circle.fill")
                            .foregroundStyle(cumulativeSeriesColor)
                        Text(project.displayName)
                            .font(.system(size: 12, weight: .semibold))
                            .lineLimit(1)
                        Spacer(minLength: 4)
                        Text(compactTokens(project.totalTokens))
                            .font(.system(size: 11, weight: .bold, design: .rounded))
                            .foregroundStyle(muted)
                    }
                    .frame(maxWidth: .infinity, minHeight: 30, alignment: .leading)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(expanded ? "收起项目会话" : "展开项目会话")

                Button(action: onOpenProject) {
                    Label("项目位置", systemImage: "folder")
                }
                .buttonStyle(DetailActionButtonStyle(light: light))
                .help("在访达中打开项目位置")
            }

            DetailProgressBar(
                value: min(1, Double(project.totalTokens) / Double(maximumProjectTokens)),
                tint: cumulativeSeriesColor,
                track: light ? Color.black.opacity(0.10) : Color.white.opacity(0.14)
            )

            if expanded {
                VStack(alignment: .leading, spacing: 4) {
                    Text("\(project.sessions.count) 个会话 · 点击整行展开详情")
                        .font(.system(size: 9, weight: .medium))
                        .foregroundStyle(muted)
                    ForEach(Array(project.sessions.enumerated()), id: \.element.id) { item in
                        SessionDetailRow(
                            sequence: item.offset + 1,
                            session: item.element,
                            maximumSessionTokens: maximumSessionTokens,
                            primary: primary,
                            muted: muted,
                            light: light,
                            onReveal: { onRevealSession(item.element) }
                        )
                    }
                }
                .padding(.top, 0)
            }
        }
        .padding(7)
        .background(light ? Color.black.opacity(0.035) : Color.white.opacity(0.055))
        .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
    }
}

private struct SessionDetailRow: View {
    let sequence: Int
    let session: SessionUsage
    let maximumSessionTokens: Int64
    let primary: Color
    let muted: Color
    let light: Bool
    let onReveal: () -> Void
    @State private var expanded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Button { expanded.toggle() } label: {
                HStack(spacing: 6) {
                    Image(systemName: expanded ? "chevron.down" : "chevron.right")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundStyle(muted)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("会话 \(sequence)")
                            .font(.system(size: 11, weight: .semibold))
                            .lineLimit(1)
                        Text("最新 \(latestActivityText)\(statusSuffix)")
                            .font(.system(size: 9, weight: .medium))
                            .foregroundStyle(muted)
                            .lineLimit(1)
                    }
                    Spacer(minLength: 4)
                    Text(compactTokens(session.totalTokens))
                        .font(.system(size: 11, weight: .bold, design: .rounded))
                        .foregroundStyle(primary)
                }
                .frame(maxWidth: .infinity, minHeight: 28, alignment: .leading)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(expanded ? "收起会话详情" : "展开会话详情")

            DetailProgressBar(
                value: min(1, Double(session.totalTokens) / Double(maximumSessionTokens)),
                tint: cumulativeSeriesColor.opacity(0.8),
                track: light ? Color.black.opacity(0.10) : Color.white.opacity(0.14)
            )

            if expanded {
                LazyVGrid(
                    columns: [GridItem(.flexible(), spacing: 5), GridItem(.flexible(), spacing: 5)],
                    spacing: 4
                ) {
                    SessionMetric(title: "输入", value: compactTokens(session.totals.input), muted: muted)
                    SessionMetric(title: "输出", value: compactTokens(session.totals.output), muted: muted)
                    SessionMetric(title: "缓存", value: compactTokens(session.totals.cached), muted: muted)
                    SessionMetric(title: "轮次／工具", value: "\(session.turnCount)／\(session.toolCallCount)", muted: muted)
                }
                HStack(spacing: 6) {
                    if let model = session.model {
                        Label("模型：\(model)\(session.effort.map { " · \($0)" } ?? "")", systemImage: "cpu")
                            .font(.system(size: 10, weight: .medium))
                            .foregroundStyle(muted)
                            .lineLimit(1)
                    }
                    Spacer(minLength: 4)
                    if session.sessionFilePath != nil {
                        Button(action: onReveal) {
                            Label("会话位置", systemImage: "doc.text.magnifyingglass")
                        }
                        .buttonStyle(DetailActionButtonStyle(light: light))
                        .help("在访达中选中这个会话文件")
                    }
                }
            }
        }
        .padding(6)
        .background(light ? Color.black.opacity(0.045) : Color.black.opacity(0.16))
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
    }

    private var latestActivityText: String {
        guard let date = session.lastActivity ?? session.startedAt else { return "暂无记录" }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = "M月d日 HH:mm"
        return formatter.string(from: date)
    }

    private var statusSuffix: String {
        session.status.map { " · \($0)" } ?? ""
    }
}

private struct SessionMetric: View {
    let title: String
    let value: String
    let muted: Color

    var body: some View {
        HStack {
            Text(title)
                .font(.system(size: 9, weight: .medium))
                .foregroundStyle(muted)
            Spacer(minLength: 3)
            Text(value)
                .font(.system(size: 10, weight: .semibold, design: .rounded))
        }
        .padding(.horizontal, 5)
        .padding(.vertical, 3)
        .background(muted.opacity(0.10))
        .clipShape(RoundedRectangle(cornerRadius: 5, style: .continuous))
    }
}

private struct DetailActionButtonStyle: ButtonStyle {
    let light: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 10, weight: .semibold))
            .foregroundStyle(light ? Color(red: 0.08, green: 0.33, blue: 0.55) : Color(red: 0.58, green: 0.79, blue: 0.98))
            .padding(.horizontal, 7)
            .padding(.vertical, 4)
            .background(
                RoundedRectangle(cornerRadius: 5, style: .continuous)
                    .fill(light ? Color.accentColor.opacity(0.10) : Color.accentColor.opacity(0.18))
            )
            .opacity(configuration.isPressed ? 0.60 : 1)
    }
}

private struct ChartBrush: Equatable {
    let start: CGFloat
    let end: CGFloat
}

/// A live-dashboard-only chart layer. It receives scroll events but deliberately
/// has no mouse-down or drag handling, so DesktopWidgetPanel can turn every
/// live-page left click into a panel drag.
private struct ChartWheelOverlay: NSViewRepresentable {
    let onWheel: (Double) -> Void

    func makeNSView(context: Context) -> WheelView {
        let view = WheelView()
        view.onWheel = onWheel
        return view
    }

    func updateNSView(_ nsView: WheelView, context: Context) {
        nsView.onWheel = onWheel
    }

    final class WheelView: NSView {
        var onWheel: ((Double) -> Void)?

        override var isOpaque: Bool { false }
        override func hitTest(_ point: NSPoint) -> NSView? {
            bounds.contains(point) ? self : nil
        }

        override func scrollWheel(with event: NSEvent) {
            let delta = event.hasPreciseScrollingDeltas
                ? event.scrollingDeltaY * 12
                : event.deltaY * 120
            onWheel?(delta)
        }
    }
}

private struct ChartSelectionOverlay: View {
    let selection: ChartBrush?

    var body: some View {
        GeometryReader { geometry in
            if let selection {
                let lower = min(1, max(0, min(selection.start, selection.end)))
                let upper = min(1, max(0, max(selection.start, selection.end)))
                let width = geometry.size.width * (upper - lower)
                RoundedRectangle(cornerRadius: 2, style: .continuous)
                    .fill(Color.accentColor.opacity(0.16))
                    .overlay(
                        RoundedRectangle(cornerRadius: 2, style: .continuous)
                            .stroke(Color.accentColor.opacity(0.85), lineWidth: 1)
                    )
                    .frame(width: width, height: geometry.size.height)
                    .position(
                        x: geometry.size.width * (lower + upper) / 2,
                        y: geometry.size.height / 2
                    )
            }
        }
        .allowsHitTesting(false)
    }
}

private struct ChartInteractionOverlay: NSViewRepresentable {
    let onWheel: (Double) -> Void
    let onSelectionPreview: (CGFloat?, CGFloat?) -> Void
    let onSelection: (Double, Double) -> Void

    func makeNSView(context: Context) -> InteractionView {
        let view = InteractionView()
        view.onWheel = onWheel
        view.onSelectionPreview = onSelectionPreview
        view.onSelection = onSelection
        return view
    }

    func updateNSView(_ nsView: InteractionView, context: Context) {
        nsView.onWheel = onWheel
        nsView.onSelectionPreview = onSelectionPreview
        nsView.onSelection = onSelection
    }

    final class InteractionView: NSView {
        var onWheel: ((Double) -> Void)?
        var onSelectionPreview: ((CGFloat?, CGFloat?) -> Void)?
        var onSelection: ((Double, Double) -> Void)?
        private var selectionStart: CGFloat?

        override var isOpaque: Bool { false }
        override var acceptsFirstResponder: Bool { true }
        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
        override func hitTest(_ point: NSPoint) -> NSView? {
            bounds.contains(point) ? self : nil
        }
        override func mouseDown(with event: NSEvent) {
            guard bounds.width > 0 else { return }
            window?.makeFirstResponder(self)
            let location = min(bounds.width, max(0, convert(event.locationInWindow, from: nil).x))
            selectionStart = location
            onSelectionPreview?(location / bounds.width, location / bounds.width)
        }
        override func mouseDragged(with event: NSEvent) {
            guard let start = selectionStart, bounds.width > 0 else { return }
            let end = min(bounds.width, max(0, convert(event.locationInWindow, from: nil).x))
            onSelectionPreview?(start / bounds.width, end / bounds.width)
        }
        override func mouseUp(with event: NSEvent) {
            guard let start = selectionStart, bounds.width > 0 else {
                onSelectionPreview?(nil, nil)
                return
            }
            selectionStart = nil
            let end = min(bounds.width, max(0, convert(event.locationInWindow, from: nil).x))
            onSelectionPreview?(nil, nil)
            onSelection?(Double(start / bounds.width), Double(end / bounds.width))
        }
        override func scrollWheel(with event: NSEvent) {
            onSelectionPreview?(nil, nil)
            let delta = event.hasPreciseScrollingDeltas ? event.scrollingDeltaY * 12 : event.deltaY * 120
            onWheel?(delta)
        }
    }
}

private struct WindowDragHandle: NSViewRepresentable {
    func makeNSView(context: Context) -> DragHandleView { DragHandleView() }
    func updateNSView(_ nsView: DragHandleView, context: Context) {}

    final class DragHandleView: NSView {
        override var isOpaque: Bool { false }
        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
        override func hitTest(_ point: NSPoint) -> NSView? {
            bounds.contains(point) ? self : nil
        }

        override func mouseDown(with event: NSEvent) {
            window?.performDrag(with: event)
        }
    }
}

private func compactRate(_ value: Double) -> String {
    if value >= 1_000_000_000 { return String(format: "%.2fB", value / 1_000_000_000) }
    if value >= 1_000_000 { return String(format: "%.2fM", value / 1_000_000) }
    if value >= 1_000 { return String(format: "%.1fK", value / 1_000) }
    return String(format: "%.0f", value)
}
