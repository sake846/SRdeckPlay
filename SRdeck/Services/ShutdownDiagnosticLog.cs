using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SRdeck.Configuration;

namespace SRdeck.Services;

/// <summary>
/// アプリケーション終了シーケンスの各フェーズの所要時間とスレッド・プロセス状態を
/// ミリ秒単位で高精度に記録する診断ロガーです。
/// ログファイルサイズは最大50kBに制限されます。
/// </summary>
public static class ShutdownDiagnosticLog
{
    private const long MaxLogSizeBytes = 50 * 1024; // 50 kB
    private const long TrimTargetSizeBytes = 30 * 1024; // 上限超過時に最新約30kBを残して古いログを刈り取り

    private static readonly object Sync = new();
    private static Stopwatch? s_stopwatch;
    private static bool s_isStarted;

    public static string LogPath => Path.Combine(
        UserDataPaths.UserDataDirectory,
        "logs",
        "shutdown-diagnostics.log");

    /// <summary>
    /// シャットダウン計測を開始します。
    /// </summary>
    public static void Start(string trigger = "Window_Closing")
    {
        lock (Sync)
        {
            // 新規セッション開始時に前回のログ蓄積が50kBを超えている場合のみ刈り込みを実施。
            // 1回のセッション内では50kBを超えても途中で刈り取らず全ログを完全保存します。
            TrimLogFileIfNeeded(LogPath);

            s_stopwatch = Stopwatch.StartNew();
            s_isStarted = true;

            string priorityStr = "Unknown";
            try
            {
                using var process = Process.GetCurrentProcess();
                priorityStr = process.PriorityClass.ToString();
            }
            catch { }

            var header = new StringBuilder();
            header.AppendLine();
            header.AppendLine(new string('=', 80));
            header.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [SHUTDOWN_START] Trigger={trigger} PID={Environment.ProcessId} ProcessPriority={priorityStr}");
            header.AppendLine($"LogPath: {LogPath} (Max size: 50kB)");
            header.AppendLine(new string('=', 80));

            WriteRaw(header.ToString());
        }
    }

    /// <summary>
    /// 単一のイベント・進捗を記録します。
    /// </summary>
    public static void Write(string phase, string details = "")
    {
        if (!s_isStarted) return;

        double elapsedSec = s_stopwatch?.Elapsed.TotalSeconds ?? 0.0;
        string priorityStr = GetCurrentProcessPriority();
        ThreadPriority threadPriority = Thread.CurrentThread.Priority;
        int tid = Environment.CurrentManagedThreadId;

        string line = $"[+{elapsedSec,7:F3}s] [TID:{tid,-3} / ThrPrio:{threadPriority,-11} / ProcPrio:{priorityStr,-11}] {phase}";
        if (!string.IsNullOrEmpty(details))
        {
            line += $" - {details}";
        }

        WriteRaw(line + Environment.NewLine);
    }

    /// <summary>
    /// 指定された処理区間の所要時間を計測するスコープを作成します。
    /// </summary>
    public static IDisposable Scope(string phaseName, string startDetails = "")
    {
        if (!s_isStarted)
        {
            return EmptyDisposable.Instance;
        }

        return new PhaseScope(phaseName, startDetails);
    }

    /// <summary>
    /// 8秒強制終了安全タイマーが発動した際に記録します。
    /// </summary>
    public static void LogSafetyTimerTriggered()
    {
        double elapsedSec = s_stopwatch?.Elapsed.TotalSeconds ?? 0.0;
        string line = $"[+{elapsedSec,7:F3}s] [CRITICAL] 8-second safety exit timer reached! Invoking FastExit(0) now." + Environment.NewLine;
        WriteRaw(line);
    }

    /// <summary>
    /// AppDomain.ProcessExit によるプロセス最終消滅タイミングを記録します。
    /// </summary>
    public static void LogProcessExit()
    {
        if (!s_isStarted) return;

        double elapsedSec = s_stopwatch?.Elapsed.TotalSeconds ?? 0.0;
        long totalMs = s_stopwatch?.ElapsedMilliseconds ?? 0;
        string line = $"[+{elapsedSec,7:F3}s] [PROCESS_EXIT] AppDomain.ProcessExit fired. Total shutdown elapsed: {totalMs}ms" + Environment.NewLine;
        line += new string('=', 80) + Environment.NewLine;
        WriteRaw(line);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// マネージドクリーンアップが完了した直後にプロセスを即時終了します。
    /// これにより、サードパーティ製ネイティブDLL (sdrplay_api.dllのサービス切断待機やGPUドライバ等)
    /// がOSのExitProcess / DLL_PROCESS_DETACH 内で数秒間ブロッキングし、
    /// Windows UIがフリーズする現象を完全に回避します。
    /// </summary>
    public static void FastExit(int exitCode = 0)
    {
        try
        {
            Write("[FAST_EXIT]", $"Bypassing native DLL_PROCESS_DETACH via TerminateProcess({exitCode}).");
        }
        catch { }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                TerminateProcess(GetCurrentProcess(), (uint)exitCode);
            }
            else
            {
                Environment.Exit(exitCode);
            }
        }
        catch
        {
            try
            {
                Process.GetCurrentProcess().Kill();
            }
            catch
            {
                Environment.Exit(exitCode);
            }
        }
    }

    private static string GetCurrentProcessPriority()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.PriorityClass.ToString();
        }
        catch
        {
            return "Unknown";
        }
    }

    private static void WriteRaw(string text)
    {
        try
        {
            string path = LogPath;
            lock (Sync)
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            Trace.Write(text);
            Console.Error.Write(text);
        }
        catch
        {
            // 診断ロギング自体のエラーでシャットダウンを妨げてはならない
        }
    }

    private static void TrimLogFileIfNeeded(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length <= MaxLogSizeBytes)
            {
                return;
            }

            string existing = File.ReadAllText(path, Encoding.UTF8);
            byte[] existingBytes = Encoding.UTF8.GetBytes(existing);
            if (existingBytes.Length <= TrimTargetSizeBytes)
            {
                return;
            }

            int cutIndex = existingBytes.Length - (int)TrimTargetSizeBytes;
            string trimmedText = Encoding.UTF8.GetString(existingBytes, cutIndex, existingBytes.Length - cutIndex);
            int firstNewline = trimmedText.IndexOf('\n');
            if (firstNewline >= 0 && firstNewline < trimmedText.Length - 1)
            {
                trimmedText = "[... old log entries truncated ...]" + Environment.NewLine + trimmedText[(firstNewline + 1)..];
            }

            File.WriteAllText(path, trimmedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Trimming error should not prevent writing
        }
    }

    private sealed class PhaseScope : IDisposable
    {
        private readonly string _phaseName;
        private readonly long _startTicks;

        public PhaseScope(string phaseName, string startDetails)
        {
            _phaseName = phaseName;
            _startTicks = Stopwatch.GetTimestamp();
            Write($"{phaseName} [START]", startDetails);
        }

        public void Dispose()
        {
            double durationMs = Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;
            Write($"{_phaseName} [COMPLETED]", $"took {durationMs:F1}ms");
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
