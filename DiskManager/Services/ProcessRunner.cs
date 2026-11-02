using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiskManager.Services;

/// <summary>
/// コンソールツール（format.exe / diskpart.exe / cipher.exe）を起動し、
/// 出力を1行ずつ、進捗表示の CR 区切りも含めてリアルタイムに受け取ります。
/// </summary>
public static class ProcessRunner
{
    private static Encoding _consoleEncoding;

    private static Encoding ConsoleEncoding
    {
        get
        {
            if (_consoleEncoding == null)
            {
                try { _consoleEncoding = Encoding.GetEncoding((int)NativeMethods.GetOEMCP()); }
                catch { _consoleEncoding = Encoding.UTF8; }
            }
            return _consoleEncoding;
        }
    }

    public static string SystemTool(string exeName)
        => System.IO.Path.Combine(Environment.SystemDirectory, exeName);

    /// <summary>プロセスを実行し、終了コードを返します。出力は onLine で通知します。</summary>
    public static async Task<int> RunAsync(
        string fileName, string arguments, Action<string> onLine,
        string stdinText = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        if (stdinText != null)
        {
            try
            {
                await proc.StandardInput.WriteAsync(stdinText);
                await proc.StandardInput.FlushAsync();
            }
            catch { /* すでに終了している場合は無視 */ }
        }
        try { proc.StandardInput.Close(); } catch { }

        var outTask = PumpAsync(proc.StandardOutput, onLine);
        var errTask = PumpAsync(proc.StandardError, onLine);

        using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } }))
        {
            await Task.WhenAll(outTask, errTask);
            await proc.WaitForExitAsync(CancellationToken.None);
        }

        return proc.ExitCode;
    }

    /// <summary>CR / LF どちらでも区切って出力を通知します（format の進捗表示は CR 区切り）。</summary>
    private static async Task PumpAsync(System.IO.StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[512];
        var sb = new StringBuilder();
        while (true)
        {
            int n;
            try { n = await reader.ReadAsync(buffer, 0, buffer.Length); }
            catch { break; }
            if (n <= 0) break;

            for (int i = 0; i < n; i++)
            {
                char c = buffer[i];
                if (c == '\r' || c == '\n')
                {
                    var line = sb.ToString().TrimEnd();
                    sb.Clear();
                    if (line.Length > 0) onLine?.Invoke(line);
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        var last = sb.ToString().TrimEnd();
        if (last.Length > 0) onLine?.Invoke(last);
    }
}
