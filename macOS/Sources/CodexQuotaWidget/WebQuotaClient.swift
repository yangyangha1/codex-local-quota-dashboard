import Foundation

enum WebQuotaClient {
    private static let endpoint = URL(string: "https://chatgpt.com/backend-api/wham/usage")!

    static func fetch() async -> QuotaSnapshot? {
        do {
            let authURL = FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent(".codex/auth.json")
            let authData = try Data(contentsOf: authURL)
            guard
                let auth = try JSONSerialization.jsonObject(with: authData) as? [String: Any],
                let tokens = auth["tokens"] as? [String: Any],
                let accessToken = tokens["access_token"] as? String,
                !accessToken.isEmpty
            else { return nil }

            var request = URLRequest(url: endpoint)
            request.httpMethod = "GET"
            request.timeoutInterval = 8
            request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
            if let accountID = tokens["account_id"] as? String, !accountID.isEmpty {
                request.setValue(accountID, forHTTPHeaderField: "ChatGPT-Account-Id")
            }
            request.setValue("CodexLocalQuotaDashboard/1.6.3", forHTTPHeaderField: "User-Agent")

            let (data, response) = try await URLSession.shared.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200,
                  let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let rateLimit = root["rate_limit"] as? [String: Any]
            else { return nil }

            let windows = ["primary_window", "secondary_window"].compactMap {
                quotaWindow(from: rateLimit[$0] as? [String: Any])
            }
            return windows.isEmpty ? nil : QuotaSnapshot(at: Date(), windows: windows)
        } catch {
            // Intentionally silent: retain the last successful web snapshot.
            return nil
        }
    }

    private static func quotaWindow(from value: [String: Any]?) -> QuotaWindow? {
        guard
            let value,
            let used = number(value["used_percent"]),
            let seconds = number(value["limit_window_seconds"]), seconds > 0
        else { return nil }
        let resetAt = number(value["reset_at"]).flatMap {
            $0 > 0 ? Date(timeIntervalSince1970: $0) : nil
        }
        return QuotaWindow(
            windowMinutes: max(1, Int(seconds / 60)),
            usedPercent: min(100, max(0, used)),
            resetsAt: resetAt)
    }

    private static func number(_ value: Any?) -> Double? {
        if let number = value as? NSNumber { return number.doubleValue }
        if let string = value as? String { return Double(string) }
        return nil
    }
}
