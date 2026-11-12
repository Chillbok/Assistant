using System.Text.RegularExpressions;

namespace Assistant.Bot;

internal static class Log
{
    private static readonly Regex Secrets = new(
        @"\d{8,10}:[A-Za-z0-9_-]{35}|sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|Bearer\s+\S+",
        RegexOptions.Compiled);

    public static string Redact(string text) => Secrets.Replace(text, "[redacted]");

    public static void Info(string message) => Console.WriteLine($"[info] {Redact(message)}");

    public static void Warn(string message) => Console.Error.WriteLine($"[warn] {Redact(message)}");

    public static void Error(string message) => Console.Error.WriteLine($"[error] {Redact(message)}");

    public static void Fatal(string message) => Console.Error.WriteLine($"[fatal] {Redact(message)}");

    public static void Exception(string context, Exception ex) =>
        Console.Error.WriteLine($"[error] {Redact(context)}: {ex.GetType().Name}: {Redact(ex.Message)}");
}
