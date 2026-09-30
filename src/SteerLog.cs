using System.Diagnostics;
using System.IO;

namespace CodexWhip;

/// <summary>
/// Append-only diagnostic log at %LOCALAPPDATA%\CodexWhip\steer.log.
/// Lines carry codes, stages and timings only, never conversation content.
/// </summary>
internal static class SteerLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    internal static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexWhip",
        "steer.log");

    internal static void Write(string line) => Write(FilePath, line, MaxBytes);

    internal static void Write(string path, string line, long maxBytes)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                File.AppendAllText(
                    path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Steer log unavailable: {exception.Message}");
            }
        }
    }

    // Keeps only labels Codex Whip already knows; any other UI text (for
    // example a conversation title) is reduced to its length.
    internal static string Redact(string? name, IEnumerable<string> knownLabels)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return knownLabels.Any(label => string.Equals(label, trimmed, StringComparison.OrdinalIgnoreCase))
            ? trimmed
            : $"#{trimmed.Length}";
    }

    internal static bool RunSelfTest(out string message)
    {
        var directory = Path.Combine(Path.GetTempPath(), "codexwhip-log-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "steer.log");
        try
        {
            for (var index = 0; index < 40; index++)
            {
                Write(path, new string('x', 100), maxBytes: 1024);
            }

            var redacted = Redact("Planifier le workflow privé", ["Stop", "Steer"]);
            var success = File.Exists(path)
                && File.Exists(path + ".1")
                && new FileInfo(path).Length <= 1024 + 200
                && Redact(" stop ", ["Stop", "Steer"]) == "stop"
                && redacted == "#27"
                && !redacted.Contains("workflow", StringComparison.OrdinalIgnoreCase);
            message = success
                ? "PASS: the steering log is size-capped and redacts unknown UI text."
                : "FAIL: the steering log cap or redaction is inconsistent.";
            return success;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Self-test cleanup skipped: {exception.Message}");
            }
        }
    }
}
