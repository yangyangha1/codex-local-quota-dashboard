using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Win32;

namespace CodexLocalDashboard
{
    internal static class WebQuotaPreferences
    {
        private const string RegistryPath = @"Software\yangyangha1\CodexLocalQuotaDashboard";
        private const string EnabledValue = "WebQuotaEnabled";

        public static bool Load()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    var value = key == null ? null : key.GetValue(EnabledValue, 1);
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
                }
            }
            catch { return true; }
        }

        public static void Save(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                    key.SetValue(EnabledValue, enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }
    }

    internal static class WebQuotaCache
    {
        private const string FileName = "web-quota-snapshot-v1.json";

        public static QuotaSnapshot Load()
        {
            try
            {
                var path = CachePath();
                if (!File.Exists(path)) return null;
                var value = Deserialize<WebQuotaCacheDocument>(
                    File.ReadAllText(path));
                if (value == null || value.AtUnixSeconds <= 0 ||
                    value.Windows == null || value.Windows.Count == 0)
                    return null;
                var windows = value.Windows.Where(window => window != null &&
                    window.WindowMinutes > 0).Select(window => new QuotaWindow(
                        window.WindowMinutes,
                        Math.Max(0d, Math.Min(100d, window.UsedPercent)),
                        window.ResetUnixSeconds > 0
                            ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(
                                window.ResetUnixSeconds)
                            : null)).ToList();
                return windows.Count == 0 ? null : new QuotaSnapshot(
                    DateTimeOffset.FromUnixTimeSeconds(value.AtUnixSeconds), windows);
            }
            catch { return null; }
        }

        public static void Save(QuotaSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Windows == null ||
                snapshot.Windows.Count == 0) return;
            try
            {
                var folder = Path.GetDirectoryName(CachePath());
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                var value = new WebQuotaCacheDocument
                {
                    AtUnixSeconds = snapshot.At.ToUnixTimeSeconds(),
                    Windows = snapshot.Windows.Where(window => window != null)
                        .Select(window => new WebQuotaCacheWindow
                        {
                            WindowMinutes = window.WindowMinutes,
                            UsedPercent = window.UsedPercent,
                            ResetUnixSeconds = window.ResetsAt.HasValue
                                ? window.ResetsAt.Value.ToUnixTimeSeconds() : 0
                        }).ToList()
                };
                var path = CachePath();
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, Serialize(value));
                File.Copy(temporary, path, true);
                File.Delete(temporary);
            }
            catch { }
        }

        private static string CachePath()
        {
            return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "CodexLocalDashboard", FileName);
        }

        private static T Deserialize<T>(string json) where T : class
        {
            using (var bytes = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(bytes);
        }

        private static string Serialize<T>(T value)
        {
            using (var bytes = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(bytes, value);
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
        }
    }

    internal static class WebQuotaClient
    {
        private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";

        public static bool TryRead(out QuotaSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                // Some Windows .NET Framework installations still default to
                // legacy TLS for HttpWebRequest. The quota endpoint requires
                // a modern TLS connection even though the rest of the app is
                // entirely local.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var authPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex", "auth.json");
                if (!File.Exists(authPath)) return false;

                var auth = Deserialize<AuthDocument>(File.ReadAllText(authPath));
                var tokens = auth == null ? null : auth.Tokens;
                var accessToken = tokens == null ? null : tokens.AccessToken;
                if (string.IsNullOrWhiteSpace(accessToken)) return false;

                var request = (HttpWebRequest)WebRequest.Create(UsageEndpoint);
                request.Method = "GET";
                request.Timeout = 8000;
                request.ReadWriteTimeout = 8000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.UserAgent = "CodexLocalQuotaDashboard/1.7.0";
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + accessToken;
                var accountId = tokens == null ? null : tokens.AccountId;
                if (!string.IsNullOrWhiteSpace(accountId))
                    request.Headers["ChatGPT-Account-Id"] = accountId;

                WebUsageDocument root;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                    root = Deserialize<WebUsageDocument>(reader.ReadToEnd());

                var limits = root == null ? null : root.RateLimit;
                var windows = new List<QuotaWindow>();
                AddWindow(limits == null ? null : limits.PrimaryWindow, windows);
                AddWindow(limits == null ? null : limits.SecondaryWindow, windows);
                // A one-window response is not a usable quota baseline.  It
                // must not displace a complete local or prior web snapshot.
                if (!HasExpectedQuotaWindows(windows)) return false;
                snapshot = new QuotaSnapshot(DateTimeOffset.Now, windows);
                return true;
            }
            catch
            {
                // The dashboard stays quiet and keeps its prior web snapshot.
                return false;
            }
        }

        private static void AddWindow(WebQuotaWindow source, List<QuotaWindow> windows)
        {
            if (source == null) return;
            if (source.LimitWindowSeconds <= 0)
                return;
            DateTimeOffset? reset = source.ResetAt > 0
                ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(source.ResetAt)
                : null;
            windows.Add(new QuotaWindow(
                (int)Math.Min(int.MaxValue, Math.Max(1, source.LimitWindowSeconds / 60)),
                Math.Max(0d, Math.Min(100d, source.UsedPercent)), reset));
        }

        private static bool HasExpectedQuotaWindows(List<QuotaWindow> windows)
        {
            return windows != null && windows.Any(window => window != null &&
                Math.Abs(window.WindowMinutes - 300) <= 15 &&
                window.ResetsAt != null) && windows.Any(window =>
                window != null && Math.Abs(window.WindowMinutes - 10080) <=
                120 && window.ResetsAt != null);
        }

        private static T Deserialize<T>(string json) where T : class
        {
            using (var bytes = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(bytes);
        }
    }

    [DataContract]
    internal sealed class AuthDocument
    {
        [DataMember(Name = "tokens")] public AuthTokens Tokens { get; set; }
    }

    [DataContract]
    internal sealed class AuthTokens
    {
        [DataMember(Name = "access_token")] public string AccessToken { get; set; }
        [DataMember(Name = "account_id")] public string AccountId { get; set; }
    }

    [DataContract]
    internal sealed class WebUsageDocument
    {
        [DataMember(Name = "rate_limit")] public WebRateLimit RateLimit { get; set; }
    }

    [DataContract]
    internal sealed class WebRateLimit
    {
        [DataMember(Name = "primary_window")] public WebQuotaWindow PrimaryWindow { get; set; }
        [DataMember(Name = "secondary_window")] public WebQuotaWindow SecondaryWindow { get; set; }
    }

    [DataContract]
    internal sealed class WebQuotaWindow
    {
        [DataMember(Name = "used_percent")] public double UsedPercent { get; set; }
        [DataMember(Name = "limit_window_seconds")] public long LimitWindowSeconds { get; set; }
        [DataMember(Name = "reset_at")] public long ResetAt { get; set; }
    }

    [DataContract]
    internal sealed class WebQuotaCacheDocument
    {
        [DataMember] public long AtUnixSeconds { get; set; }
        [DataMember] public List<WebQuotaCacheWindow> Windows { get; set; }
    }

    [DataContract]
    internal sealed class WebQuotaCacheWindow
    {
        [DataMember] public int WindowMinutes { get; set; }
        [DataMember] public double UsedPercent { get; set; }
        [DataMember] public long ResetUnixSeconds { get; set; }
    }
}
