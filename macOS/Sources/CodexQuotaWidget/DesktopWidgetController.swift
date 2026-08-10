import AppKit
import SwiftUI

@MainActor
final class DesktopAppDelegate: NSObject, NSApplicationDelegate {
    private var widget: DesktopWidgetController!
    private var statusItem: NSStatusItem!
    private let statusMenu = NSMenu()
    private let quotaPopover = NSPopover()

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        widget = DesktopWidgetController()
        configureStatusItem()
        widget.show()
    }

    func applicationWillTerminate(_ notification: Notification) {
        widget?.stop()
    }

    private func configureStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        guard let button = statusItem.button else { return }
        button.image = NSImage(systemSymbolName: "gauge.with.dots.needle.67percent", accessibilityDescription: "Codex 本地额度面板")
        button.target = self
        button.action = #selector(statusItemClicked(_:))
        button.sendAction(on: [.leftMouseUp, .rightMouseUp])

        statusMenu.autoenablesItems = false
        _ = addMenuItem("显示悬浮面板", action: #selector(showWidget(_:)))
        _ = addMenuItem("立即刷新", action: #selector(refresh(_:)))
        statusMenu.addItem(.separator())
        _ = addMenuItem("切换深色／浅色", action: #selector(toggleTheme(_:)))
        _ = addMenuItem("背景透明度：0%", action: #selector(setTransparency(_:)), representedObject: 0)
        _ = addMenuItem("背景透明度：10%", action: #selector(setTransparency(_:)), representedObject: 10)
        _ = addMenuItem("背景透明度：30%", action: #selector(setTransparency(_:)), representedObject: 30)
        _ = addMenuItem("背景透明度：50%", action: #selector(setTransparency(_:)), representedObject: 50)
        let topmost = addMenuItem("窗口置顶", action: #selector(toggleTopMost(_:)))
        topmost.state = widget.model.topMost ? .on : .off
        let launchAtLogin = addMenuItem("开机自动启动", action: #selector(toggleLaunchAtLogin(_:)))
        launchAtLogin.state = widget.model.launchAtLoginEnabled ? .on : .off
        statusMenu.addItem(.separator())
        _ = addMenuItem("隐藏", action: #selector(hideWidget(_:)))
        _ = addMenuItem("退出", action: #selector(quit(_:)))
    }

    private func addMenuItem(_ title: String, action: Selector, representedObject: Any? = nil) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        item.representedObject = representedObject
        statusMenu.addItem(item)
        return item
    }

    @objc private func statusItemClicked(_ sender: Any?) {
        if NSApp.currentEvent?.type == .rightMouseUp, let button = statusItem.button {
            refreshMenuState()
            statusMenu.popUp(positioning: nil, at: NSPoint(x: 0, y: button.bounds.height), in: button)
        } else {
            showQuotaPopover()
        }
    }

    @objc private func showWidget(_ sender: Any?) { widget.show() }
    @objc private func hideWidget(_ sender: Any?) { widget.hide() }
    @objc private func refresh(_ sender: Any?) { widget.model.refresh() }
    @objc private func toggleTheme(_ sender: Any?) {
        widget.model.theme = widget.model.theme == .dark ? .light : .dark
    }
    @objc private func setTransparency(_ sender: NSMenuItem) {
        widget.model.backgroundTransparency = Double(sender.representedObject as? Int ?? 10)
    }
    @objc private func toggleTopMost(_ sender: NSMenuItem) {
        widget.model.topMost.toggle()
        sender.state = widget.model.topMost ? .on : .off
    }
    @objc private func toggleLaunchAtLogin(_ sender: NSMenuItem) {
        let enabled = !widget.model.launchAtLoginEnabled
        let result = widget.model.setLaunchAtLogin(enabled)
        sender.state = widget.model.launchAtLoginEnabled ? .on : .off
        presentActionResult(result, successTitle: enabled ? "已开启开机自动启动" : "已关闭开机自动启动")
    }
    @objc private func quit(_ sender: Any?) { NSApp.terminate(nil) }

    private func refreshMenuState() {
        for item in statusMenu.items where item.title == "窗口置顶" {
            item.state = widget.model.topMost ? .on : .off
        }
        for item in statusMenu.items where item.title == "开机自动启动" {
            item.state = widget.model.launchAtLoginEnabled ? .on : .off
        }
        if let quota = widget.model.primaryQuota {
            statusItem.button?.toolTip = "Codex 剩余额度 \(wholePercent(quota.remainingPercent))%"
        } else {
            statusItem.button?.toolTip = "Codex 本地额度面板"
        }
    }

    private func showQuotaPopover() {
        guard let button = statusItem.button else { return }
        if quotaPopover.isShown {
            quotaPopover.performClose(nil)
            return
        }
        quotaPopover.behavior = .transient
        quotaPopover.contentSize = NSSize(width: 258, height: 170)
        quotaPopover.contentViewController = NSHostingController(
            rootView: StatusQuotaPopoverView(
                quota: widget.model.primaryQuota,
                onOpenProjectPage: { [weak self] in self?.widget.model.openProjectPage() },
                onShowWidget: { [weak self] in
                    self?.quotaPopover.performClose(nil)
                    self?.widget.show()
                }
            )
        )
        quotaPopover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
    }

    private func presentActionResult(_ result: Result<String, Error>, successTitle: String) {
        let alert = NSAlert()
        switch result {
        case .success(let message):
            alert.messageText = successTitle
            alert.informativeText = message
            alert.alertStyle = .informational
        case .failure(let error):
            alert.messageText = "操作未完成"
            alert.informativeText = error.localizedDescription
            alert.alertStyle = .warning
        }
        alert.addButton(withTitle: "好")
        alert.runModal()
    }
}

private struct StatusQuotaPopoverView: View {
    let quota: QuotaWindow?
    let onOpenProjectPage: () -> Void
    let onShowWidget: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack {
                Text("Codex 本地额度面板")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(.secondary)
                Spacer()
                Button("v1.5.5", action: onOpenProjectPage)
                    .buttonStyle(.plain)
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(.secondary)
                    .help("打开原项目页面")
            }
            if let quota {
                Text("剩余 \(wholePercent(quota.remainingPercent))%")
                    .font(.system(size: 27, weight: .bold, design: .rounded))
                HStack {
                    Text(quotaWindowName(quota.windowMinutes))
                    Spacer()
                    Text(resetText(for: quota))
                }
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(.secondary)
            } else {
                Text("暂无本地额度快照")
                    .font(.system(size: 18, weight: .semibold))
                Text("完成一次 Codex 对话后会自动读取缓存额度。")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            Divider()
            Button("显示悬浮面板", action: onShowWidget)
                .frame(maxWidth: .infinity, alignment: .trailing)
        }
        .padding(14)
        .frame(width: 258)
    }

    private func resetText(for quota: QuotaWindow) -> String {
        guard let reset = quota.resetsAt else { return "重置时间未知" }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = "M月d日 HH:mm 重置"
        return formatter.string(from: reset)
    }
}

@MainActor
final class DesktopWidgetController: NSObject, NSWindowDelegate {
    let model = DashboardViewModel()
    private let panel: DesktopWidgetPanel
    private static let frameKey = "CodexQuotaWidget.frame.v2"
    private var isNormalizingPanelFrame = false

    override init() {
        let initialSize = NSSize(width: 420, height: 456)
        panel = DesktopWidgetPanel(
            contentRect: NSRect(origin: .zero, size: initialSize),
            styleMask: [.borderless, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        super.init()

        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.minSize = WidgetWindowGeometry.minimumSize
        panel.maxSize = WidgetWindowGeometry.maximumSize
        panel.contentMinSize = WidgetWindowGeometry.minimumSize
        panel.contentMaxSize = WidgetWindowGeometry.maximumSize
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        // History owns its chart drag gestures.  The live dashboard instead
        // treats left clicks as window drags, except for its top-right actions.
        panel.isMovableByWindowBackground = false
        panel.delegate = self
        panel.shouldTreatLeftClickAsLiveDrag = { [weak self] in
            self?.model.contentMode == .live
        }

        model.onTopMostChanged = { [weak self] topMost in self?.applyTopMost(topMost) }
        model.onHideRequested = { [weak self] in self?.hide() }
        applyTopMost(model.topMost)
        panel.contentView = NSHostingView(rootView: DashboardView(model: model))
        restoreFrameOrPlaceDefault()
    }

    func show() {
        panel.orderFrontRegardless()
        NSApp.activate(ignoringOtherApps: true)
        model.start()
    }

    func hide() { panel.orderOut(nil) }

    func stop() { model.stop() }

    func windowDidMove(_ notification: Notification) { persistFrame() }
    func windowDidResize(_ notification: Notification) {
        normalizePanelFrameIfNeeded()
        persistFrame()
    }

    func windowWillResize(_ sender: NSWindow, to frameSize: NSSize) -> NSSize {
        WidgetWindowGeometry.constrainedSize(frameSize, relativeTo: sender.frame.size)
    }

    private func applyTopMost(_ topMost: Bool) {
        panel.level = topMost ? .floating : .normal
    }

    private func restoreFrameOrPlaceDefault() {
        if let saved = UserDefaults.standard.string(forKey: Self.frameKey) {
            let frame = NSRectFromString(saved)
            let correctedFrame = NSRect(
                origin: frame.origin,
                size: WidgetWindowGeometry.constrainedSize(frame.size)
            )
            if NSScreen.screens.contains(where: { $0.visibleFrame.intersects(correctedFrame) }) {
                panel.setFrame(correctedFrame, display: false)
                return
            }
        }
        let visible = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        panel.setFrameOrigin(NSPoint(x: visible.maxX - panel.frame.width - 36, y: visible.maxY - panel.frame.height - 72))
    }

    private func persistFrame() {
        UserDefaults.standard.set(NSStringFromRect(panel.frame), forKey: Self.frameKey)
    }

    /// NSPanel normally respects `minSize`, but borderless SwiftUI panels can
    /// receive a final frame after a live-resize gesture.  Correct that frame
    /// once more so it can never become too small to recover with the mouse.
    private func normalizePanelFrameIfNeeded() {
        guard !isNormalizingPanelFrame else { return }
        let currentFrame = panel.frame
        let correctedSize = WidgetWindowGeometry.constrainedSize(currentFrame.size)
        guard
            abs(currentFrame.width - correctedSize.width) > 0.5 ||
                abs(currentFrame.height - correctedSize.height) > 0.5
        else { return }

        isNormalizingPanelFrame = true
        let correctedFrame = NSRect(
            x: currentFrame.minX,
            y: currentFrame.maxY - correctedSize.height,
            width: correctedSize.width,
            height: correctedSize.height
        )
        panel.setFrame(correctedFrame, display: true)
        isNormalizingPanelFrame = false
    }
}

private final class DesktopWidgetPanel: NSPanel {
    var shouldTreatLeftClickAsLiveDrag: (() -> Bool)?

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }

    override func sendEvent(_ event: NSEvent) {
        guard
            event.type == .leftMouseDown,
            shouldTreatLeftClickAsLiveDrag?() == true,
            !isResizeEdge(event.locationInWindow),
            !isLiveHeaderAction(event.locationInWindow)
        else {
            super.sendEvent(event)
            return
        }
        performDrag(with: event)
    }

    private func isResizeEdge(_ point: NSPoint) -> Bool {
        let size = contentView?.bounds.size ?? frame.size
        let edgeInset: CGFloat = 9
        return point.x <= edgeInset || point.y <= edgeInset ||
            point.x >= size.width - edgeInset || point.y >= size.height - edgeInset
    }

    private func isLiveHeaderAction(_ point: NSPoint) -> Bool {
        let size = contentView?.bounds.size ?? frame.size
        return point.x >= size.width - 108 && point.y >= size.height - 54
    }
}
