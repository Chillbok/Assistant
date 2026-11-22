using System.Text;
using Assistant.Bot;
using Xunit;

namespace Assistant.Bot.Tests;

public class MessageFormatTests
{
    private const int Limit = 300;

    [Fact]
    public void Success_EmptyOutput_UsesFallback()
    {
        var text = MessageFormat.Compose(Result(), Limit);

        Assert.StartsWith("[완료]", text);
        Assert.Contains("(출력 없음, exit 0)", text);
    }

    [Fact]
    public void Success_LongOutput_KeepsTailAndFooter()
    {
        var stdout = new string('a', 3001);

        var text = MessageFormat.Compose(Result(stdout: stdout), Limit);

        Assert.Contains("(앞부분 생략, 전체 3001자 중 마지막 3000자)", text);
        Assert.True(text.Length <= 4000);
    }

    [Fact]
    public void Success_EmojiTruncation_DoesNotSplitSurrogatePairs()
    {
        var stdout = string.Concat(Enumerable.Repeat("😀", 3001));

        var text = MessageFormat.Compose(Result(stdout: stdout), Limit);

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]));
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(text[i]));
            }
        }

        Assert.DoesNotContain('\uFFFD', text);
        Assert.True(text.Length <= 4000);
    }

    [Fact]
    public void Failure_ShowsBothStreamLabels()
    {
        var text = MessageFormat.Compose(
            Result(ExitKind.Application, stdout: "out", stderr: "err", exitCode: 1),
            Limit);

        Assert.StartsWith("[실패]", text);
        Assert.Contains("[stderr, 마지막 3자]", text);
        Assert.Contains("[stdout, 마지막 3자]", text);
        Assert.Contains(", exit 1", text);
    }

    [Fact]
    public void Failure_StderrOnly_OmitsStdoutLabel()
    {
        var text = MessageFormat.Compose(
            Result(ExitKind.Application, stdout: "", stderr: "boom", exitCode: 2),
            Limit);

        Assert.Contains("[stderr, 마지막 4자]", text);
        Assert.DoesNotContain("[stdout", text);
        Assert.DoesNotContain("출력 없음", text);
    }

    [Fact]
    public void Timeout_EmptyOutput_ShowsNoteAndFallback()
    {
        var text = MessageFormat.Compose(Result(ExitKind.Timeout, exitCode: 124), 20);

        Assert.StartsWith("[시간 초과]", text);
        Assert.Contains("(제한 20초)", text);
        Assert.Contains("일부만 반영됐을 수 있습니다", text);
        Assert.Contains("(출력 없음)", text);
    }

    [Fact]
    public void Truncated_Flag_UsesNoTotalCount()
    {
        var text = MessageFormat.Compose(
            Result(stdout: "tail", stdoutTruncated: true),
            Limit);

        Assert.Contains("(출력 1MB 초과, 마지막 4자만 표시)", text);
        Assert.DoesNotContain("전체", text);
    }

    [Fact]
    public void Normalize_RewritesCarriageReturns()
    {
        var text = MessageFormat.Compose(Result(stdout: "a\rb\r\nc"), Limit);

        Assert.Contains("a\nb\nc", text);
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void Normalize_CollapsesBlankLineRuns()
    {
        var text = MessageFormat.Compose(Result(stdout: "a\n\n\n\nb"), Limit);

        Assert.Contains("a\n\nb", text);
    }

    [Fact]
    public void Ansi_IsStrippedFromStderrOnly()
    {
        var stderr = "\u001b[31merr\u001b[0m";
        var stdout = "\u001b[32mout\u001b[0m";

        var text = MessageFormat.Compose(
            Result(ExitKind.Application, stdout: stdout, stderr: stderr, exitCode: 1),
            Limit);

        var stderrPart = text[..text.IndexOf("[stdout", StringComparison.Ordinal)];
        Assert.DoesNotContain('\u001b', stderrPart);
        Assert.Contains("err", stderrPart);
        Assert.Contains('\u001b', text);
    }

    [Fact]
    public void Failure_BothStreamsHuge_StaysUnderBudget()
    {
        var stdout = new string('o', 50_000);
        var stderr = new string('e', 50_000);

        var text = MessageFormat.Compose(
            Result(ExitKind.Application, stdout: stdout, stderr: stderr, exitCode: 1),
            Limit);

        Assert.True(text.Length <= 4000);
        Assert.Contains("마지막 1800자", text);
        Assert.Contains("마지막 1200자", text);
    }

    [Fact]
    public void Failure_NegativeExitCode_OmitsExit()
    {
        var text = MessageFormat.Compose(
            Result(ExitKind.Application, stdout: "x", exitCode: -1),
            Limit);

        Assert.StartsWith("[실패] 1.2초", text);
        Assert.DoesNotContain("exit", text);
    }

    [Fact]
    public void RunnerError_IsReportedAsFailure()
    {
        var text = MessageFormat.Compose(
            Result(ExitKind.RunnerError, stderr: "setsid did not start", exitCode: 127),
            Limit);

        Assert.Contains("실행기 오류", text);
    }

    private static RunResult Result(
        ExitKind kind = ExitKind.Success,
        string stdout = "",
        string stderr = "",
        int exitCode = 0,
        bool stdoutTruncated = false,
        bool stderrTruncated = false,
        double elapsed = 1.2) => new(
            exitCode,
            kind == ExitKind.Timeout,
            kind == ExitKind.Cancelled,
            elapsed,
            0,
            0,
            0,
            kind,
            stdout,
            stdout,
            stderr,
            stderr,
            stdoutTruncated,
            stderrTruncated,
            Encoding.UTF8.GetByteCount(stdout),
            Encoding.UTF8.GetByteCount(stderr));
}
