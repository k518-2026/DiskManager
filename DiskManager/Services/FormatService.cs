using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DiskManager.Services;

/// <summary>フォーマットと安全消去。Windows 標準の format / cipher を利用します。</summary>
public static class FormatService
{
    public enum FormatMode
    {
        /// <summary>簡易フォーマット。ファイルテーブルだけを作り直します。データは復元可能です。</summary>
        Quick,
        /// <summary>通常フォーマット。全セクタを検査しゼロを書き込みます。</summary>
        Full,
        /// <summary>ディープフォーマット。ゼロと乱数を指定回数書き込みます（データ復元を困難にします）。</summary>
        Secure
    }

    public class FormatOptions
    {
        public string DriveLetter { get; set; }          // 例: "E:"
        public string FileSystem { get; set; } = "NTFS"; // NTFS / exFAT / FAT32 / FAT / ReFS
        public string Label { get; set; } = "";
        public FormatMode Mode { get; set; } = FormatMode.Quick;
        public int SecurePasses { get; set; } = 3;
        public int AllocationUnitBytes { get; set; }     // 0 = 既定
        public bool EnableCompression { get; set; }
    }

    /// <summary>format の出力にある「12 パーセント完了しました」などから数字を拾います。</summary>
    private static readonly System.Text.RegularExpressions.Regex PercentRx =
        new(@"(\d{1,3})\s*(?:パーセント|percent|%)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>進み具合の行なら 0〜100 を返します。それ以外は null。</summary>
    public static int? ParsePercent(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = PercentRx.Match(line);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int p) && p >= 0 && p <= 100) return p;
        return null;
    }

    /// <summary>
    /// フォーマットを実行します。戻り値は成功したかどうか。
    /// onPercent には format が出す進み具合（0〜100）を渡します（簡易フォーマットでは出ないことがあります）。
    /// </summary>
    public static async Task<bool> FormatAsync(
        FormatOptions options, Action<string> log, CancellationToken ct, Action<int> onPercent = null)
    {
        if (options == null || string.IsNullOrWhiteSpace(options.DriveLetter))
        {
            log?.Invoke("ドライブが指定されていません。");
            return false;
        }

        var letter = options.DriveLetter.Trim().TrimEnd(':').ToUpperInvariant() + ":";

        // C: は Windows の場所に関係なく常に対象外（旧 FORMATApp と同じ最終防衛線）
        if (string.Equals(letter, DiskService.SystemDriveLetter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(letter, "C:", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke("システムドライブ（C: を含む）はフォーマットできません。");
            return false;
        }

        var args = new StringBuilder();
        args.Append(letter);
        args.Append(" /FS:").Append(options.FileSystem);
        args.Append(" /Y");

        switch (options.Mode)
        {
            case FormatMode.Quick:
                args.Append(" /Q");
                break;
            case FormatMode.Full:
                // 既定（/Q なし）で全セクタにゼロを書き込みます
                break;
            case FormatMode.Secure:
                int passes = Math.Max(1, Math.Min(9, options.SecurePasses));
                args.Append(" /P:").Append(passes);
                break;
        }

        if (options.AllocationUnitBytes > 0)
            args.Append(" /A:").Append(options.AllocationUnitBytes);

        if (options.EnableCompression &&
            string.Equals(options.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            args.Append(" /C");

        log?.Invoke($"フォーマットを開始します: {letter} / {options.FileSystem} / {ModeText(options)}");
        log?.Invoke("format " + args);

        // 「続行しますか」「ボリュームラベル」「強制的にマウント解除しますか」への応答
        string stdin = "\r\nY\r\nY\r\nY\r\n";

        // 進み具合の行は数字だけ拾い、ログには 10 % ごとに残す（毎行出すとログが埋まるため）
        int lastLogged = -1;
        Action<string> onLine = line =>
        {
            if (ParsePercent(line) is int p)
            {
                onPercent?.Invoke(p);
                if (p / 10 != lastLogged / 10 || lastLogged < 0)
                {
                    lastLogged = p;
                    log?.Invoke(line);
                }
                return;
            }
            log?.Invoke(line);
        };

        int exit;
        try
        {
            exit = await ProcessRunner.RunAsync(
                ProcessRunner.SystemTool("format.com"), args.ToString(), onLine, stdin, ct);
        }
        catch (Exception ex)
        {
            log?.Invoke("format の実行に失敗しました: " + ex.Message);
            return false;
        }

        if (ct.IsCancellationRequested)
        {
            log?.Invoke("フォーマットを中止しました。ボリュームが不完全な状態の可能性があります。");
            return false;
        }

        if (exit != 0)
        {
            log?.Invoke($"フォーマットは終了コード {exit} で終わりました。");
            return false;
        }
        onPercent?.Invoke(100);

        // ラベルは Unicode で確実に設定できるよう、フォーマット後に付け直します
        if (!string.IsNullOrWhiteSpace(options.Label))
        {
            try
            {
                var di = new DriveInfo(letter);
                di.VolumeLabel = options.Label.Length > 32 ? options.Label.Substring(0, 32) : options.Label;
                log?.Invoke("ボリュームラベルを設定しました: " + options.Label);
            }
            catch (Exception ex)
            {
                log?.Invoke("ボリュームラベルを設定できませんでした: " + ex.Message);
            }
        }

        log?.Invoke("フォーマットが完了しました。");
        return true;
    }

    private static string ModeText(FormatOptions o) => o.Mode switch
    {
        FormatMode.Quick => "簡易フォーマット",
        FormatMode.Full => "通常フォーマット（全域ゼロ書き込み）",
        FormatMode.Secure => $"ディープフォーマット（{o.SecurePasses} 回上書き）",
        _ => ""
    };

    /// <summary>
    /// 空き領域だけを上書きします。既存ファイルを残したまま、
    /// 削除済みデータの復元を困難にします（cipher /w)。
    /// </summary>
    public static async Task<bool> WipeFreeSpaceAsync(string driveLetter, Action<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(driveLetter)) return false;
        var letter = driveLetter.Trim().TrimEnd(':').ToUpperInvariant() + ":";

        log?.Invoke($"{letter} の空き領域の上書きを開始します。容量によっては数時間かかります。");
        try
        {
            int exit = await ProcessRunner.RunAsync(
                ProcessRunner.SystemTool("cipher.exe"), $"/w:{letter}\\", log, null, ct);

            if (ct.IsCancellationRequested)
            {
                log?.Invoke("空き領域の上書きを中止しました。");
                return false;
            }
            log?.Invoke(exit == 0 ? "空き領域の上書きが完了しました。" : $"cipher は終了コード {exit} で終わりました。");
            return exit == 0;
        }
        catch (Exception ex)
        {
            log?.Invoke("cipher の実行に失敗しました: " + ex.Message);
            return false;
        }
    }

    /// <summary>ボリュームラベルだけを変更します。</summary>
    public static bool SetLabel(string driveLetter, string label, Action<string> log)
    {
        try
        {
            var di = new DriveInfo(driveLetter.Trim().TrimEnd(':') + ":");
            di.VolumeLabel = label ?? "";
            log?.Invoke($"{di.Name} のラベルを「{label}」に変更しました。");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("ラベルを変更できませんでした: " + ex.Message);
            return false;
        }
    }
}
