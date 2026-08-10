import SwiftUI

@main
struct CodexQuotaWidgetApp: App {
    @NSApplicationDelegateAdaptor(DesktopAppDelegate.self) private var appDelegate

    var body: some Scene {
        Settings {
            EmptyView()
        }
    }
}
