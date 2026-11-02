using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DiskManager.Common;
using DiskManager.Models;
using Microsoft.Win32.SafeHandles;

namespace DiskManager.Services;

/// <summary>
/// 古い CD / DVD の読み取り検査とバックアップ。
/// 読めないセクタがあっても中断せず、読めた部分だけを救い出します。
/// </summary>
public static class OpticalService
{
    public const int SectorSize = 2048;
    private const int SectorsPerBlock = 64;              // 128 KB ずつ読み取ります

    public static List<OpticalDriveInfo> EnumerateDrives()
    {
        var list = new List<OpticalDriveInfo>();
        try
        {
            using var s = new ManagementObjectSearcher(Wmi.CimV2Namespace, "SELECT * FROM Win32_CDROMDrive");
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    var letter = Wmi.S(mo, "Drive");
                    if (string.IsNullOrWhiteSpace(letter)) continue;
                    var d = new OpticalDriveInfo
                    {
                        DriveLetter = letter.ToUpperInvariant(),
                        Model = Wmi.S(mo, "Caption"),
                        DeviceId = Wmi.S(mo, "DeviceID")
                    };
                    RefreshMedia(d);
                    list.Add(d);
                }
            }
        }
        catch { }

        if (list.Count == 0)
        {
            foreach (var di in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.CDRom))
            {
                var d = new OpticalDriveInfo
                {
                    DriveLetter = di.Name.TrimEnd('\\').ToUpperInvariant(),
                    Model = "光学ドライブ"
                };
                RefreshMedia(d);
                list.Add(d);
            }
        }
        return list;
    }

    public static void RefreshMedia(OpticalDriveInfo drive)
    {
        if (drive == null) return;
        try
        {
            var di = new DriveInfo(drive.DriveLetter.TrimEnd(':') + ":");
            drive.MediaLoaded = di.IsReady;
            if (di.IsReady)
            {
                drive.VolumeLabel = di.VolumeLabel;
                drive.FileSystem = di.DriveFormat;
                drive.MediaSize = GetMediaLength(drive.DriveLetter);
                if (drive.MediaSize <= 0) drive.MediaSize = di.TotalSize;
            }
            else
            {
                drive.VolumeLabel = "";
                drive.FileSystem = "";
                drive.MediaSize = 0;
            }
        }
        catch
        {
            drive.MediaLoaded = false;
        }
    }

    private static SafeFileHandle OpenRaw(string driveLetter)
    {
        string path = @"\\.\" + driveLetter.Trim().TrimEnd(':').ToUpperInvariant() + ":";
        return NativeMethods.CreateFileW(path, NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
    }

    /// <summary>メディアの総バイト数を取得します。</summary>
    public static long GetMediaLength(string driveLetter)
    {
        using var h = OpenRaw(driveLetter);
        if (h.IsInvalid) return 0;

        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            if (NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_DISK_GET_LENGTH_INFO,
                    IntPtr.Zero, 0, buffer, 8, out _, IntPtr.Zero))
                return Marshal.ReadInt64(buffer);
        }
        catch { }
        finally { Marshal.FreeHGlobal(buffer); }
        return 0;
    }

    public class ScanProgress
    {
        public long BytesDone { get; set; }
        public long BytesTotal { get; set; }
        public long BadSectors { get; set; }
        public double Ratio => BytesTotal <= 0 ? 0 : (double)BytesDone / BytesTotal;
    }

    /// <summary>
    /// メディアを全セクタ読み取ります。outputPath を指定すると ISO イメージとして保存し、
    /// null なら読み取り検査だけを行います。
    /// </summary>
    public static async Task<DiscScanResult> ReadDiscAsync(
        string driveLetter, string outputPath, int retryCount,
        Action<string> log, Action<ScanProgress> progress, CancellationToken ct)
    {
        var result = new DiscScanResult();

        return await Task.Run(() =>
        {
            using var h = OpenRaw(driveLetter);
            if (h.IsInvalid)
            {
                log?.Invoke("ドライブを開けませんでした。メディアが入っているか確認してください。");
                return result;
            }

            long total = GetMediaLength(driveLetter);
            if (total <= 0)
            {
                try { total = new DriveInfo(driveLetter.TrimEnd(':') + ":").TotalSize; } catch { }
            }
            if (total <= 0)
            {
                log?.Invoke("メディアのサイズを取得できませんでした。");
                return result;
            }

            result.TotalSectors = total / SectorSize;
            log?.Invoke($"読み取りを開始します: {driveLetter} / 合計 {Fmt.Bytes(total)} / {result.TotalSectors:N0} セクタ");

            FileStream output = null;
            if (!string.IsNullOrEmpty(outputPath))
                output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

            var block = new byte[SectorSize * SectorsPerBlock];
            var sectorBuf = new byte[SectorSize];
            var zero = new byte[SectorSize];
            var started = DateTime.UtcNow;
            long position = 0;
            var prog = new ScanProgress { BytesTotal = total };

            try
            {
                while (position < total)
                {
                    if (ct.IsCancellationRequested) { result.Cancelled = true; break; }

                    int want = (int)Math.Min(block.Length, total - position);
                    want = (want / SectorSize) * SectorSize;
                    if (want == 0) break;

                    if (TryRead(h, position, block, want, out uint read) && read > 0)
                    {
                        output?.Write(block, 0, (int)read);
                        position += read;
                    }
                    else
                    {
                        // ブロック単位で失敗したらセクタ単位に切り替えて可能な限り救出します
                        long blockEnd = position + want;
                        while (position < blockEnd)
                        {
                            if (ct.IsCancellationRequested) { result.Cancelled = true; break; }

                            bool ok = false;
                            for (int attempt = 0; attempt <= retryCount && !ok; attempt++)
                            {
                                ok = TryRead(h, position, sectorBuf, SectorSize, out uint sr) && sr == SectorSize;
                                if (!ok) Thread.Sleep(40);
                                else if (attempt > 0) result.RecoveredSectors++;
                            }

                            if (ok)
                            {
                                output?.Write(sectorBuf, 0, SectorSize);
                            }
                            else
                            {
                                result.BadSectors++;
                                if (result.BadSectorSamples.Count < 200)
                                    result.BadSectorSamples.Add(position / SectorSize);
                                output?.Write(zero, 0, SectorSize);   // 読めない部分はゼロで埋めて位置を保ちます
                            }
                            position += SectorSize;
                        }
                    }

                    prog.BytesDone = position;
                    prog.BadSectors = result.BadSectors;
                    progress?.Invoke(prog);
                }
            }
            finally
            {
                output?.Flush();
                output?.Dispose();
            }

            var elapsed = DateTime.UtcNow - started;
            if (result.Cancelled)
            {
                log?.Invoke("読み取りを中止しました。");
            }
            else
            {
                log?.Invoke($"読み取りが完了しました。所要 {Fmt.Duration(elapsed)} / 平均 {Fmt.Rate(position, elapsed)}");
                if (result.BadSectors == 0)
                    log?.Invoke("読み取れないセクタはありませんでした。このメディアはまだ健全です。");
                else
                    log?.Invoke($"読み取れないセクタが {result.BadSectors:N0} 個ありました"
                              + $"（全体の {result.BadRatio * 100:0.000} %）。"
                              + (result.RecoveredSectors > 0 ? $" 再試行で {result.RecoveredSectors:N0} セクタを救出しました。" : ""));
            }
            if (!string.IsNullOrEmpty(outputPath) && !result.Cancelled)
                log?.Invoke("ISO イメージを保存しました: " + outputPath);

            return result;
        }, ct);
    }

    private static bool TryRead(SafeFileHandle h, long position, byte[] buffer, int count, out uint read)
    {
        read = 0;
        if (!NativeMethods.SetFilePointerEx(h, position, out _, NativeMethods.FILE_BEGIN)) return false;
        return NativeMethods.ReadFile(h, buffer, (uint)count, out read, IntPtr.Zero);
    }

    public class CopyResult
    {
        public int FilesCopied { get; set; }
        public int FilesFailed { get; set; }
        public long BytesCopied { get; set; }
        public List<string> FailedFiles { get; } = new();
        public bool Cancelled { get; set; }
    }

    /// <summary>ファイル単位でバックアップします。読み取れないファイルは記録してスキップします。</summary>
    public static async Task<CopyResult> CopyFilesAsync(
        string driveLetter, string destination,
        Action<string> log, Action<ScanProgress> progress, CancellationToken ct)
    {
        var result = new CopyResult();

        return await Task.Run(() =>
        {
            string source = driveLetter.Trim().TrimEnd(':').ToUpperInvariant() + ":\\";
            if (!Directory.Exists(source))
            {
                log?.Invoke("メディアを読み取れません。ディスクが入っているか確認してください。");
                return result;
            }

            Directory.CreateDirectory(destination);

            var files = new List<string>();
            CollectFiles(source, files, log);
            log?.Invoke($"{files.Count:N0} 個のファイルをコピーします。");

            long totalBytes = 0;
            foreach (var f in files)
            {
                try { totalBytes += new FileInfo(f).Length; } catch { }
            }

            var prog = new ScanProgress { BytesTotal = Math.Max(totalBytes, 1) };
            var started = DateTime.UtcNow;

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) { result.Cancelled = true; break; }

                string relative = file.Substring(source.Length);
                string target = Path.Combine(destination, relative);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        input.CopyTo(output, 1 << 16);
                    }
                    try { File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file)); } catch { }

                    result.FilesCopied++;
                    try { result.BytesCopied += new FileInfo(target).Length; } catch { }
                }
                catch (Exception ex)
                {
                    result.FilesFailed++;
                    result.FailedFiles.Add(relative);
                    log?.Invoke($"読み取り失敗: {relative} — {ex.Message}");
                    try { if (File.Exists(target)) File.Delete(target); } catch { }
                }

                prog.BytesDone = result.BytesCopied;
                progress?.Invoke(prog);
            }

            var elapsed = DateTime.UtcNow - started;
            log?.Invoke($"コピー完了: 成功 {result.FilesCopied:N0} 件 / 失敗 {result.FilesFailed:N0} 件 / "
                      + $"{Fmt.Bytes(result.BytesCopied)} / {Fmt.Duration(elapsed)}");

            if (result.FilesFailed > 0)
            {
                string reportPath = Path.Combine(destination, "_読み取り失敗ファイル一覧.txt");
                try
                {
                    File.WriteAllLines(reportPath, result.FailedFiles);
                    log?.Invoke("失敗したファイルの一覧を保存しました: " + reportPath);
                }
                catch { }
                log?.Invoke("失敗したファイルはディスクの傷や劣化が原因の可能性があります。"
                          + "ISO イメージとして吸い出すと、読める部分だけでも残せます。");
            }

            return result;
        }, ct);
    }

    private static void CollectFiles(string directory, List<string> files, Action<string> log)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(directory))
                files.Add(f);
        }
        catch (Exception ex)
        {
            log?.Invoke($"ファイル一覧を取得できません: {directory} — {ex.Message}");
        }

        try
        {
            foreach (var d in Directory.EnumerateDirectories(directory))
                CollectFiles(d, files, log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"フォルダーを開けません: {directory} — {ex.Message}");
        }
    }

    /// <summary>メディアの内容を要約します（ファイル数・総容量・最終更新日）。</summary>
    public static string Summarize(string driveLetter, Action<string> log)
    {
        try
        {
            string root = driveLetter.Trim().TrimEnd(':').ToUpperInvariant() + ":\\";
            var files = new List<string>();
            CollectFiles(root, files, log);

            long bytes = 0;
            DateTime newest = DateTime.MinValue, oldest = DateTime.MaxValue;
            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    bytes += fi.Length;
                    if (fi.LastWriteTime > newest) newest = fi.LastWriteTime;
                    if (fi.LastWriteTime < oldest) oldest = fi.LastWriteTime;
                }
                catch { }
            }

            var di = new DriveInfo(driveLetter.TrimEnd(':') + ":");
            return $"ラベル: {(string.IsNullOrEmpty(di.VolumeLabel) ? "(なし)" : di.VolumeLabel)}\n"
                 + $"ファイルシステム: {di.DriveFormat}\n"
                 + $"ファイル数: {files.Count:N0}\n"
                 + $"合計サイズ: {Fmt.Bytes(bytes)}\n"
                 + (files.Count > 0 ? $"更新日: {oldest:yyyy/MM/dd} 〜 {newest:yyyy/MM/dd}" : "");
        }
        catch (Exception ex)
        {
            return "メディアの情報を取得できませんでした: " + ex.Message;
        }
    }
}
