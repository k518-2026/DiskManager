using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiskManager.Services;

/// <summary>diskpart をスクリプトモードで実行します（詳細操作用の逃げ道）。</summary>
public static class DiskPartService
{
    /// <summary>スクリプトを一時ファイルに書き出して diskpart /s で実行します。</summary>
    public static async Task<int> RunScriptAsync(string script, Action<string> log, CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(),
            "dm_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");

        try
        {
            // diskpart のスクリプトは ASCII のコマンドのみで構成します
            File.WriteAllText(path, script.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n", Encoding.ASCII);

            log?.Invoke("diskpart スクリプトを実行します:");
            foreach (var line in script.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0) log?.Invoke("  > " + t);
            }

            return await ProcessRunner.RunAsync(
                ProcessRunner.SystemTool("diskpart.exe"), "/s \"" + path + "\"", log, null, ct);
        }
        catch (Exception ex)
        {
            log?.Invoke("diskpart の実行に失敗しました: " + ex.Message);
            return -1;
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>ディスクの再スキャン（ハードウェア変更の反映）。</summary>
    public static Task<int> RescanAsync(Action<string> log, CancellationToken ct)
        => RunScriptAsync("rescan", log, ct);

    /// <summary>ディスクの読み取り専用属性を解除します。</summary>
    public static Task<int> ClearReadOnlyAsync(int diskNumber, Action<string> log, CancellationToken ct)
        => RunScriptAsync($"select disk {diskNumber}\nattributes disk clear readonly", log, ct);

    /// <summary>パーティションをアクティブにします（MBR の起動用）。</summary>
    public static Task<int> SetActiveAsync(int diskNumber, int partitionNumber, Action<string> log, CancellationToken ct)
        => RunScriptAsync($"select disk {diskNumber}\nselect partition {partitionNumber}\nactive", log, ct);
}
