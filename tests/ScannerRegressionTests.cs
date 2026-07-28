using System;
using System.IO;
using System.Text;

namespace CodexLocalDashboard
{
    internal static class ScannerRegressionTests
    {
        private static int failures;

        public static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodexLocalDashboard.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "sessions"));
            Directory.CreateDirectory(Path.Combine(root, "archived_sessions"));
            try
            {
                PartialLineAndNoRepeat(root);
                CounterReset(root);
                InvalidQuotaIsIgnored(root);
                DeleteMoveAndTruncate(root);
                LargeIrrelevantLineIsSkipped(root);
                ProjectAndSessionTotalsAreLocal(root);
                SessionDetailsAreLoadedOnlyOnDemand(root);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine(failures == 0 ? "PASS" : "FAILURES=" + failures);
            return failures == 0 ? 0 : 1;
        }

        private static void PartialLineAndNoRepeat(string root)
        {
            ResetFolders(root);
            var path = Path.Combine(root, "sessions", "partial.jsonl");
            var line = TokenLine(120, 30, 20, 5, 40, true);
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            File.WriteAllBytes(path, Slice(bytes, 0, bytes.Length / 2));
            var scanner = new UsageScanner(root);
            Equal("partial-not-committed", 0, scanner.Scan().Today.Total);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                stream.Write(bytes, bytes.Length / 2, bytes.Length - bytes.Length / 2);
            var first = scanner.Scan();
            Equal("partial-committed-once", 150, first.Today.Total);
            Equal("structured-quota", 1, first.Quotas.Count);
            Equal("repeat-idempotent", 150, scanner.Scan().Today.Total);
        }

        private static void CounterReset(string root)
        {
            ResetFolders(root);
            var path = Path.Combine(root, "sessions", "reset.jsonl");
            File.WriteAllText(path, TokenLine(100, 10, 5, 1, 20, true) + "\n", Encoding.UTF8);
            var scanner = new UsageScanner(root);
            Equal("counter-before-reset", 110, scanner.Scan().Today.Total);
            File.AppendAllText(path, TokenLine(20, 3, 1, 0, 20, true) + "\n", Encoding.UTF8);
            Equal("counter-reset-new-baseline", 133, scanner.Scan().Today.Total);
        }

        private static void InvalidQuotaIsIgnored(string root)
        {
            ResetFolders(root);
            var path = Path.Combine(root, "sessions", "bad-quota.jsonl");
            var line = TokenLine(1, 1, 0, 0, 0, false);
            File.WriteAllText(path, line + "\n{broken json}\n", Encoding.UTF8);
            var snapshot = new UsageScanner(root).Scan();
            Equal("invalid-quota-not-100-percent", 0, snapshot.Quotas.Count);
            Equal("bad-line-isolated", 2, snapshot.Today.Total);
        }

        private static void DeleteMoveAndTruncate(string root)
        {
            ResetFolders(root);
            var source = Path.Combine(root, "sessions", "lifecycle.jsonl");
            File.WriteAllText(source, TokenLine(80, 20, 10, 0, 25, true) + new string(' ', 200) + "\n", Encoding.UTF8);
            var scanner = new UsageScanner(root);
            Equal("lifecycle-initial", 100, scanner.Scan().Today.Total);
            var archived = Path.Combine(root, "archived_sessions", "lifecycle.jsonl");
            File.Move(source, archived);
            Equal("move-no-duplicate", 100, scanner.Scan().Today.Total);
            File.WriteAllText(archived, TokenLine(4, 1, 0, 0, 25, true) + "\n", Encoding.UTF8);
            Equal("truncate-rebuild", 5, scanner.Scan().Today.Total);
            File.Delete(archived);
            Equal("delete-removes-contribution", 0, scanner.Scan().Today.Total);
        }

        private static void LargeIrrelevantLineIsSkipped(string root)
        {
            ResetFolders(root);
            var path = Path.Combine(root, "sessions", "large-irrelevant.jsonl");
            File.WriteAllText(path, "{\"payload\":\"" + new string('x', 5 * 1024 * 1024) + "\"}\n" + TokenLine(8, 2, 1, 0, 25, true) + "\n", Encoding.UTF8);
            var snapshot = new UsageScanner(root).Scan();
            Equal("large-irrelevant-line-does-not-block-following-usage", 10, snapshot.Today.Total);
        }

        private static void ProjectAndSessionTotalsAreLocal(string root)
        {
            ResetFolders(root);
            var projectA = Path.Combine(root, "work", "alpha");
            var projectB = Path.Combine(root, "work", "beta");
            File.WriteAllText(Path.Combine(root, "sessions", "one.jsonl"),
                SessionMetaLine("session-one", projectA) + "\n" +
                TokenLine(100, 20, 0, 0, 25, true) + "\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "sessions", "two.jsonl"),
                SessionMetaLine("session-two", projectA) + "\n" +
                TokenLine(200, 30, 0, 0, 25, true) + "\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "sessions", "three.jsonl"),
                SessionMetaLine("session-three", projectB) + "\n" +
                TokenLine(40, 10, 0, 0, 25, true) + "\n", Encoding.UTF8);

            var snapshot = new UsageScanner(root, true).Scan();
            Equal("project-group-count", 2, snapshot.Projects.Count);
            Equal("project-alpha-total", 350, snapshot.Projects[0].TotalTokens);
            Equal("project-alpha-session-count", 2, snapshot.Projects[0].Sessions.Count);
            Equal("project-alpha-display-name", "alpha", snapshot.Projects[0].DisplayName);
            Equal("project-beta-total", 50, snapshot.Projects[1].TotalTokens);
        }

        private static void SessionDetailsAreLoadedOnlyOnDemand(string root)
        {
            ResetFolders(root);
            var project = Path.Combine(root, "work", "detail");
            var path = Path.Combine(root, "sessions", "detail.jsonl");
            var timestamp = DateTimeOffset.Now.ToUniversalTime().ToString("O");
            File.WriteAllText(path,
                SessionMetaLine("detail-session", project) + "\n" +
                "{ \"timestamp\": \"" + timestamp +
                "\", \"type\": \"event_msg\", \"payload\": { \"type\": \"user_message\" } }\n" +
                "{ \"timestamp\": \"" + timestamp +
                "\", \"type\": \"turn_context\", \"payload\": { \"model\": \"gpt-test\", \"effort\": \"high\" } }\n" +
                "{ \"timestamp\": \"" + timestamp +
                "\", \"type\": \"function_call\", \"payload\": { \"call_id\": \"call-1\" } }\n" +
                "{ \"timestamp\": \"" + timestamp +
                "\", \"type\": \"function_call\", \"payload\": { \"call_id\": \"call-1\" } }\n" +
                "{ \"timestamp\": \"" + timestamp +
                "\", \"type\": \"task_complete\", \"payload\": { } }\n" +
                TokenLine(100, 20, 30, 5, 25, true) + "\n",
                Encoding.UTF8);

            var lightweight = new UsageScanner(root).Scan();
            Equal("lightweight-scan-keeps-no-project-objects", 0,
                lightweight.Projects.Count);

            var detailed = new UsageScanner(root, true).Scan();
            Equal("detail-project-count", 1, detailed.Projects.Count);
            var session = detailed.Projects[0].Sessions[0];
            Equal("detail-turn-count", 1, session.TurnCount);
            Equal("detail-tool-call-deduplicated", 1,
                session.ToolCallCount);
            Equal("detail-model", "gpt-test", session.Model);
            Equal("detail-effort", "high", session.Effort);
            Equal("detail-status", "已完成", session.Status);
            Equal("detail-input", 100, session.InputTokens);
            Equal("detail-output", 20, session.OutputTokens);
            Equal("detail-cache", 30, session.CachedTokens);
        }

        private static string SessionMetaLine(string id, string cwd)
        {
            return "{ \"timestamp\": \"" + DateTimeOffset.Now.ToUniversalTime().ToString("O") +
                "\", \"type\": \"session_meta\", \"payload\": { \"id\": \"" + id +
                "\", \"cwd\": \"" + cwd.Replace("\\", "\\\\") + "\" } }";
        }

        private static string TokenLine(long input, long output, long cached, long reasoning, double used, bool includeUsed)
        {
            var timestamp = DateTimeOffset.Now.ToUniversalTime().ToString("O");
            var usedField = includeUsed ? ", \"used_percent\": " + used.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            return "{ \"payload\": { \"rate_limits\": { \"primary\": { \"window_minutes\": 10080" + usedField + ", \"resets_at\": 1785200760 }, \"secondary\": null }, \"info\": { \"total_token_usage\": { \"output_tokens\": " + output + ", \"reasoning_output_tokens\": " + reasoning + ", \"input_tokens\": " + input + ", \"cached_input_tokens\": " + cached + " } }, \"type\": \"token_count\" }, \"timestamp\": \"" + timestamp + "\", \"type\": \"event_msg\" }";
        }

        private static byte[] Slice(byte[] value, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(value, offset, result, 0, count);
            return result;
        }

        private static void ResetFolders(string root)
        {
            foreach (var folder in new[] { Path.Combine(root, "sessions"), Path.Combine(root, "archived_sessions") })
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                Directory.CreateDirectory(folder);
            }
        }

        private static void Equal(string name, long expected, long actual)
        {
            if (expected == actual) return;
            failures++;
            Console.Error.WriteLine(name + ": expected " + expected + ", actual " + actual);
        }

        private static void Equal(string name, string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal)) return;
            failures++;
            Console.Error.WriteLine(name + ": expected " + expected + ", actual " + actual);
        }
    }
}
