using System.Globalization;
using System.Text.RegularExpressions;

namespace Assistant.Bot;

internal static class MessageFormat
{
    private const int MaxTotal = 4000;
    private const int SuccessBody = 3000;
    private const int FailureStderrBody = 1200;
    private const int FailureStdoutBody = 1800;
    private const int TimeoutBody = 3000;
    private const int MaxFirstLineFragment = 200;

    private const string TimeoutNote =
        "이미 쓰인 파일은 남고, 쓰는 중이던 파일은 일부만 반영됐을 수 있습니다";

    private static readonly Regex Ansi = new(
        @"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)",
        RegexOptions.Compiled);

    private static readonly Regex LongGap = new(@"\n{3,}", RegexOptions.Compiled);

    public static string StatusLabel(RunResult result) => result.Kind switch
    {
        ExitKind.Success => "[완료]",
        ExitKind.Timeout => "[시간 초과]",
        ExitKind.Cancelled => "[중단됨]",
        _ => "[실패]",
    };

    public static string Interrupted() => "[중단됨] 봇이 재시작되어 작업이 중단되었습니다";

    public static string Compose(RunResult result, int limitSeconds)
    {
        var timeout = result.Kind == ExitKind.Timeout;
        var failure = result.Kind is ExitKind.Application or ExitKind.ExternalSignal or ExitKind.RunnerError;

        var stdoutFull = Normalize(result.StdoutTruncated ? result.StdoutTail : result.Stdout);
        var stderrFull = Normalize(StripAnsi(result.StderrTruncated ? result.StderrTail : result.Stderr));

        var hasOut = stdoutFull.Length > 0;
        var hasErr = stderrFull.Length > 0;

        var parts = new List<string> { Header(result, limitSeconds) };
        if (timeout)
        {
            parts.Add(TimeoutNote);
        }

        var footers = new List<string>();

        if (failure)
        {
            if (hasErr)
            {
                AppendStream(parts, footers, "stderr", stderrFull, result.StderrTruncated, FailureStderrBody);
            }

            if (hasOut)
            {
                AppendStream(parts, footers, "stdout", stdoutFull, result.StdoutTruncated, FailureStdoutBody);
            }
        }
        else if (hasOut)
        {
            AppendStream(parts, footers, null, stdoutFull, result.StdoutTruncated,
                timeout ? TimeoutBody : SuccessBody);
        }
        else if (hasErr)
        {
            // exit 0이어도 stderr만 있으면 정보를 버리지 않는다.
            AppendStream(parts, footers, "stderr", stderrFull, result.StderrTruncated, FailureStderrBody);
        }

        if (!hasOut && !hasErr)
        {
            parts.Add(timeout ? "(출력 없음)" : $"(출력 없음, exit {result.ExitCode})");
        }

        parts.AddRange(footers);

        var text = string.Join('\n', parts.Where(static part => part.Length > 0));
        if (text.Length > MaxTotal)
        {
            // 예산 계산이 맞으면 도달하지 않는다. 최후 방어로만 자른다.
            text = CutEnd(text, MaxTotal);
        }

        return text.Length == 0 ? "(출력 없음)" : text;
    }

    internal static int CountCodepoints(string text)
    {
        var count = 0;
        foreach (var value in text)
        {
            if (!char.IsLowSurrogate(value))
            {
                count++;
            }
        }

        return count;
    }

    private static string Header(RunResult result, int limitSeconds)
    {
        var elapsed = result.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        if (result.Kind == ExitKind.Timeout)
        {
            return $"[시간 초과] {elapsed}초에 강제 종료 (제한 {limitSeconds}초)";
        }

        if (result.Kind == ExitKind.Cancelled)
        {
            return "[중단됨]";
        }

        if (result.Kind == ExitKind.RunnerError)
        {
            return $"[실패] {elapsed}초, 실행기 오류";
        }

        var exit = result.ExitCode >= 0 ? $", exit {result.ExitCode}" : string.Empty;

        return result.Kind switch
        {
            ExitKind.Success => $"[완료] {elapsed}초{exit}",
            ExitKind.ExternalSignal => $"[실패] {elapsed}초{exit} (외부 신호)",
            _ => $"[실패] {elapsed}초{exit}",
        };
    }

    private static void AppendStream(
        List<string> parts,
        List<string> footers,
        string? label,
        string normalized,
        bool truncated,
        int budget)
    {
        var fullCount = CountCodepoints(normalized);
        var body = Tail(normalized, budget, out var shownCount);
        if (body.Length == 0)
        {
            return;
        }

        parts.Add(label is null
            ? body
            : $"[{label}, 마지막 {shownCount}자]\n{body}");

        if (truncated)
        {
            // 1MB를 넘으면 전체 문자 수를 알 수 없다. N을 쓰지 않는다.
            footers.Add($"(출력 1MB 초과, 마지막 {shownCount}자만 표시)");
        }
        else if (fullCount > shownCount)
        {
            footers.Add($"(앞부분 생략, 전체 {fullCount}자 중 마지막 {shownCount}자)");
        }
    }

    private static string Tail(string text, int maxUnits, out int shownCount)
    {
        if (text.Length > maxUnits)
        {
            // UTF-16 단위로 자르되 문자 중간을 넘지 않는다.
            var start = text.Length - maxUnits;
            while (start < text.Length && char.IsLowSurrogate(text[start]))
            {
                start++;
            }

            text = text[start..];

            // 잘린 첫 줄이 짧은 파편이면 통째로 버린다.
            var newline = text.IndexOf('\n');
            if (newline > 0 && newline <= MaxFirstLineFragment)
            {
                text = text[(newline + 1)..];
            }
        }

        text = text.Trim();
        shownCount = CountCodepoints(text);
        return text;
    }

    private static string Normalize(string text)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = LongGap.Replace(text, "\n\n");

        // SIGKILL로 마지막 문자가 잘린 경우 디코더가 붙인 U+FFFD를 제거한다.
        var removed = 0;
        while (removed < 3 && text.EndsWith('\uFFFD'))
        {
            text = text[..^1];
            removed++;
        }

        return text.Trim();
    }

    private static string StripAnsi(string text) =>
        text.Length == 0 ? text : Ansi.Replace(text, string.Empty);

    private static string CutEnd(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut];
    }
}
