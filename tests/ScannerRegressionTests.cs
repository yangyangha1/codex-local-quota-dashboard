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
    }
}
