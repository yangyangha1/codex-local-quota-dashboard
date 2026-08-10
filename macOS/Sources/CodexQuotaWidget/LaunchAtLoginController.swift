import Foundation

enum LaunchAtLoginError: LocalizedError {
    case missingExecutable

    var errorDescription: String? {
        switch self {
        case .missingExecutable:
            return "无法确定当前应用的可执行文件位置。"
        }
    }
}

/// Manages only this app's optional login LaunchAgent.  It deliberately has no
/// dependency on system-extension infrastructure.
enum LaunchAtLoginController {
    static let label = "com.yangyangha1.codex-local-quota-widget"

    static var launchAgentURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents", isDirectory: true)
            .appendingPathComponent("\(label).plist")
    }

    static func isEnabled() -> Bool {
        guard
            let executablePath = Bundle.main.executableURL?.path,
            let data = try? Data(contentsOf: launchAgentURL),
            let propertyList = try? PropertyListSerialization.propertyList(
                from: data,
                options: [],
                format: nil
            ) as? [String: Any],
            propertyList["Label"] as? String == label,
            let arguments = propertyList["ProgramArguments"] as? [String],
            arguments.first == executablePath
        else {
            return false
        }
        return true
    }

    @discardableResult
    static func setEnabled(_ enabled: Bool) throws -> URL? {
        let fileManager = FileManager.default
        guard enabled else {
            if fileManager.fileExists(atPath: launchAgentURL.path) {
                try fileManager.removeItem(at: launchAgentURL)
            }
            return nil
        }

        guard let executableURL = Bundle.main.executableURL else {
            throw LaunchAtLoginError.missingExecutable
        }
        try fileManager.createDirectory(
            at: launchAgentURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let launchAgent: [String: Any] = [
            "Label": label,
            "ProgramArguments": [executableURL.path],
            "RunAtLoad": true,
            "ProcessType": "Interactive",
            "ThrottleInterval": 10
        ]
        let data = try PropertyListSerialization.data(
            fromPropertyList: launchAgent,
            format: .xml,
            options: 0
        )
        try data.write(to: launchAgentURL, options: .atomic)
        return executableURL
    }
}
