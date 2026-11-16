using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Assistant.Bot;

internal enum ExitKind
{
    Success,
    Application,
    ExternalSignal,
    RunnerError,
    Timeout,
    Cancelled,
}

internal sealed record RunResult(
    int ExitCode,
    bool TimedOut,
    bool Cancelled,
    double ElapsedSeconds,
    int RemainingInGroup,
    int ZombiesInGroup,
    int Pgid,
    ExitKind Kind,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    long TotalStdoutBytes,
    long TotalStderrBytes);

internal static class Runner
{
    private const int OutputLimit = 1 << 20;
    private const int DefaultTimeoutSeconds = 300;
    private const int TermGraceMilliseconds = 2500;
    private const int DrainGraceSeconds = 2;
    private const int Esrch = 3;

    private static readonly string[] PassthroughNames =
    {
        "PATH", "HOME", "TMPDIR", "LANG", "LC_ALL", "TZ",
    };

    private static readonly string[] PassthroughPrefixes =
    {
        "XDG_", "OPENCODE_",
    };

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    public static int TimeoutSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("ASSISTANT_JOB_TIMEOUT_SECONDS");
        if (int.TryParse(raw, out var seconds))
        {
            return Math.Clamp(seconds, 1, 3600);
        }

        return DefaultTimeoutSeconds;
    }

    public static async Task<RunResult> RunAsync(string binary, string instruction, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/setsid",
            WorkingDirectory = "/work",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add(binary);
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--pure");
        psi.ArgumentList.Add("--auto");
        psi.ArgumentList.Add("--dir");
        psi.ArgumentList.Add("/work");
        // "--"가 없으면 "--version" 같은 지시가 옵션으로 해석된다.
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(instruction);

        psi.Environment.Clear();
        foreach (var (key, value) in SnapshotEnvironment())
        {
            psi.Environment[key] = value;
        }

        var stopwatch = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("setsid did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            stopwatch.Stop();
            Log.Error($"runner start failed: {ex.GetType().Name}: {ex.Message}");
            return new RunResult(127, false, false, Math.Round(stopwatch.Elapsed.TotalSeconds, 2), 0, 0, 0,
                ExitKind.RunnerError, string.Empty, ex.Message, false, false, 0, 0);
        }

        using (process)
        {
            // compose run은 stdin 파이프를 열어둔 채 시작한다. EOF를 보내 자식이 입력 대기로 멈추지 않게 한다.
            process.StandardInput.Close();

            var pgid = await ResolvePgidAsync(process.Id);
            if (pgid != process.Id)
            {
                Log.Warn($"pgid {pgid} != child pid {process.Id}; 다른 그룹을 칠 수 있으므로 pid로 폴백한다");
                pgid = process.Id;
            }

            var stdout = new Capture(OutputLimit);
            var stderr = new Capture(OutputLimit);
            var outTask = PumpAsync(process.StandardOutput.BaseStream, stdout);
            var errTask = PumpAsync(process.StandardError.BaseStream, stderr);

            var timedOut = false;
            var cancelled = false;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds()));

            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                cancelled = ct.IsCancellationRequested;
                await KillGroupAsync(pgid, process);
            }

            var (alive, zombies) = ScanGroup(pgid);
            if (alive > 0)
            {
                Log.Warn($"group {pgid}에 살아있는 프로세스 {alive}개, 정리한다");
                await KillGroupAsync(pgid, process);
                (alive, zombies) = ScanGroup(pgid);
            }

            if (!await WaitDrainAsync(outTask, errTask))
            {
                // 손자가 stdout 파이프를 물고 있으면 EOF가 오지 않는다. 그룹을 정리해 파이프를 닫는다.
                Log.Warn($"출력이 {DrainGraceSeconds}s 안에 닫히지 않았다. 그룹 {pgid}를 정리한다");
                await KillGroupAsync(pgid, process);
                (alive, zombies) = ScanGroup(pgid);
                await WaitDrainAsync(outTask, errTask);
            }

            stopwatch.Stop();
            var exitCode = SafeExitCode(process);
            var kind = Classify(exitCode, timedOut, cancelled, stderr.Text);

            return new RunResult(
                exitCode,
                timedOut,
                cancelled,
                Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
                alive,
                zombies,
                pgid,
                kind,
                stdout.Text,
                stderr.Text,
                stdout.Truncated,
                stderr.Truncated,
                stdout.Total,
                stderr.Total);
        }
    }

    private static ExitKind Classify(int exitCode, bool timedOut, bool cancelled, string stderr)
    {
        if (timedOut)
        {
            return ExitKind.Timeout;
        }

        if (cancelled)
        {
            return ExitKind.Cancelled;
        }

        if ((exitCode == 126 || exitCode == 127) && stderr.Contains("setsid: failed to execute", StringComparison.Ordinal))
        {
            return ExitKind.RunnerError;
        }

        if (exitCode == 137 || exitCode == 143)
        {
            return ExitKind.ExternalSignal;
        }

        return exitCode == 0 ? ExitKind.Success : ExitKind.Application;
    }

    private static Dictionary<string, string> SnapshotEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || entry.Value is not string value)
            {
                continue;
            }

            var keep = PassthroughNames.Contains(key, StringComparer.Ordinal)
                || PassthroughPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));

            if (keep)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static async Task<int> ResolvePgidAsync(int pid)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (TryReadStat(pid) is { } stat)
            {
                return stat.Pgrp > 1 ? stat.Pgrp : pid;
            }

            await Task.Delay(5, CancellationToken.None);
        }

        return pid;
    }

    private static (int Alive, int Zombies) ScanGroup(int pgid)
    {
        var alive = 0;
        var zombies = 0;
        if (pgid <= 1)
        {
            return (0, 0);
        }

        string[] entries;
        try
        {
            entries = Directory.GetDirectories("/proc");
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot read /proc: {ex.GetType().Name}");
            return (0, 0);
        }

        foreach (var dir in entries)
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId)
            {
                continue;
            }

            if (TryReadStat(pid) is not { } stat || stat.Pgrp != pgid)
            {
                continue;
            }

            if (stat.State == 'Z')
            {
                zombies++;
            }
            else
            {
                alive++;
            }
        }

        return (alive, zombies);
    }

    private static (char State, int Pgrp)? TryReadStat(int pid)
    {
        string text;
        try
        {
            text = File.ReadAllText($"/proc/{pid}/stat");
        }
        catch
        {
            return null;
        }

        // comm(2번 필드)은 공백과 괄호를 포함할 수 있으므로 마지막 ')' 뒤만 자른다.
        var close = text.LastIndexOf(')');
        if (close < 0 || close + 2 >= text.Length)
        {
            return null;
        }

        var parts = text[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // parts[0]=state(3), parts[1]=ppid(4), parts[2]=pgrp(5)
        if (parts.Length < 3 || parts[0].Length == 0 || !int.TryParse(parts[2], out var pgrp))
        {
            return null;
        }

        return (parts[0][0], pgrp);
    }

    private static async Task KillGroupAsync(int pgid, Process process)
    {
        if (pgid > 1 && pgid != OwnPgid())
        {
            Signal(pgid, 15);
            await Task.Delay(TermGraceMilliseconds, CancellationToken.None);
            Signal(pgid, 9);
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"tree kill failed: {ex.GetType().Name}");
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log.Warn($"reap failed: {ex.GetType().Name}");
        }
    }

    private static void Signal(int pgid, int signal)
    {
        var rc = kill(-pgid, signal);
        if (rc == -1)
        {
            var errno = Marshal.GetLastWin32Error();
            if (errno != Esrch)
            {
                Log.Warn($"kill(-{pgid}, {signal}) failed: errno {errno}");
            }
        }
    }

    private static int OwnPgid() => TryReadStat(Environment.ProcessId)?.Pgrp ?? 0;

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<bool> WaitDrainAsync(Task outTask, Task errTask)
    {
        try
        {
            await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(DrainGraceSeconds), CancellationToken.None);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static async Task PumpAsync(Stream stream, Capture capture)
    {
        var buffer = new byte[81920];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, CancellationToken.None);
                if (read <= 0)
                {
                    return;
                }

                capture.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"pump stopped: {ex.GetType().Name}");
        }
    }

    private sealed class Capture
    {
        private readonly MemoryStream buffer = new();
        private readonly int limit;

        public Capture(int limit)
        {
            this.limit = limit;
        }

        public long Total { get; private set; }

        public bool Truncated { get; private set; }

        public string Text => Encoding.UTF8.GetString(buffer.ToArray());

        public void Append(ReadOnlySpan<byte> chunk)
        {
            Total += chunk.Length;
            var room = limit - (int)buffer.Length;
            if (room <= 0)
            {
                Truncated = true;
                return;
            }

            if (chunk.Length > room)
            {
                buffer.Write(chunk[..room]);
                Truncated = true;
                return;
            }

            buffer.Write(chunk);
        }
    }
}
