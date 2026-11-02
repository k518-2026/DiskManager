using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using DiskManager.Models;

namespace DiskManager.Services;

/// <summary>物理ディスク・ボリューム・パーティションを列挙します。</summary>
public static class DiskService
{
    public static string SystemDriveLetter
    {
        get
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            return root.TrimEnd('\\').ToUpperInvariant();
        }
    }

    public static List<PhysicalDiskInfo> EnumerateDisks(Action<string> log = null)
    {
        var disks = new List<PhysicalDiskInfo>();
        var sysLetter = SystemDriveLetter;

        using (var searcher = new ManagementObjectSearcher(
            Wmi.CimV2Namespace, "SELECT * FROM Win32_DiskDrive"))
        {
            foreach (ManagementObject drive in searcher.Get())
            {
                using (drive)
                {
                    var d = new PhysicalDiskInfo
                    {
                        Index = Wmi.I32(drive, "Index"),
                        DeviceId = Wmi.S(drive, "DeviceID"),
                        PnpDeviceId = Wmi.S(drive, "PNPDeviceID"),
                        Model = Wmi.S(drive, "Model"),
                        SerialNumber = Wmi.S(drive, "SerialNumber").Trim(),
                        FirmwareRevision = Wmi.S(drive, "FirmwareRevision").Trim(),
                        InterfaceType = Wmi.S(drive, "InterfaceType"),
                        MediaType = Wmi.S(drive, "MediaType"),
                        Size = Wmi.U64(drive, "Size"),
                        PartitionCount = Wmi.U32(drive, "Partitions"),
                        WmiStatus = Wmi.S(drive, "Status")
                    };
                    d.IsRemovable = d.MediaType.IndexOf("Removable", StringComparison.OrdinalIgnoreCase) >= 0;
                    d.BusType = d.InterfaceType;

                    LoadVolumes(d, sysLetter);
                    disks.Add(d);
                }
            }
        }

        EnrichWithStorageWmi(disks, log);
        LoadPartitions(disks, log);
        FillVolumesFromPartitions(disks, sysLetter);

        return disks.OrderBy(x => x.Index).ToList();
    }

    /// <summary>
    /// ASSOCIATORS による関連付けは環境によって取得できないことがあります。
    /// その場合に備え、パーティションのドライブ文字を手がかりにボリュームを補完します。
    /// </summary>
    private static void FillVolumesFromPartitions(List<PhysicalDiskInfo> disks, string sysLetter)
    {
        Dictionary<string, VolumeInfo> byLetter;
        try
        {
            byLetter = new Dictionary<string, VolumeInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in EnumerateVolumes())
                byLetter[v.DriveLetter] = v;
        }
        catch
        {
            return;
        }

        foreach (var d in disks)
        {
            foreach (var p in d.Partitions)
            {
                if (p.IsUnallocated || string.IsNullOrEmpty(p.DriveLetter)) continue;
                if (d.Volumes.Any(x => string.Equals(x.DriveLetter, p.DriveLetter, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!byLetter.TryGetValue(p.DriveLetter, out var src)) continue;

                var v = new VolumeInfo
                {
                    DriveLetter = src.DriveLetter,
                    Label = src.Label,
                    FileSystem = src.FileSystem,
                    Capacity = src.Capacity,
                    FreeSpace = src.FreeSpace,
                    DriveTypeCode = src.DriveTypeCode,
                    DiskNumber = d.Index,
                    PartitionNumber = p.PartitionNumber,
                    IsSystemVolume = string.Equals(src.DriveLetter, sysLetter, StringComparison.OrdinalIgnoreCase),
                    IsBootVolume = p.IsBoot
                };
                if (v.IsSystemVolume || v.IsBootVolume || p.IsSystem) d.ContainsSystemVolume = true;
                d.Volumes.Add(v);
            }

            d.Volumes.Sort((a, b) =>
                string.Compare(a.DriveLetter, b.DriveLetter, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void LoadVolumes(PhysicalDiskInfo disk, string sysLetter)
    {
        var deviceId = Wmi.Escape(disk.DeviceId);
        string q = $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{deviceId}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition";

        try
        {
            using var partSearcher = new ManagementObjectSearcher(Wmi.CimV2Namespace, q);
            foreach (ManagementObject part in partSearcher.Get())
            {
                using (part)
                {
                    var partId = Wmi.Escape(Wmi.S(part, "DeviceID"));
                    string q2 = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partId}'}} WHERE AssocClass=Win32_LogicalDiskToPartition";
                    using var ldSearcher = new ManagementObjectSearcher(Wmi.CimV2Namespace, q2);
                    foreach (ManagementObject ld in ldSearcher.Get())
                    {
                        using (ld)
                        {
                            var letter = Wmi.S(ld, "DeviceID").ToUpperInvariant();
                            var v = new VolumeInfo
                            {
                                DriveLetter = letter,
                                Label = Wmi.S(ld, "VolumeName"),
                                FileSystem = Wmi.S(ld, "FileSystem"),
                                Capacity = Wmi.U64(ld, "Size"),
                                FreeSpace = Wmi.U64(ld, "FreeSpace"),
                                DriveTypeCode = Wmi.U32(ld, "DriveType"),
                                DiskNumber = disk.Index,
                                PartitionNumber = Wmi.I32(part, "Index") + 1
                            };
                            v.IsSystemVolume = string.Equals(letter, sysLetter, StringComparison.OrdinalIgnoreCase);
                            v.IsBootVolume = Wmi.B(part, "BootPartition");
                            if (v.IsSystemVolume || v.IsBootVolume) disk.ContainsSystemVolume = true;
                            disk.Volumes.Add(v);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // 一部のディスクで関連付けが取得できない場合は空のまま続行します
        }
    }

    /// <summary>Storage 名前空間（Windows 8 以降）から追加情報を取得します。</summary>
    private static void EnrichWithStorageWmi(List<PhysicalDiskInfo> disks, Action<string> log)
    {
        try
        {
            var scope = Wmi.Connect(Wmi.StorageNamespace);

            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_Disk")))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    using (mo)
                    {
                        int number = Wmi.I32(mo, "Number");
                        var d = disks.FirstOrDefault(x => x.Index == number);
                        if (d == null) continue;
                        d.PartitionStyle = Wmi.PartitionStyleText(Wmi.U32(mo, "PartitionStyle"));
                        d.AllocatedSize = Wmi.U64(mo, "AllocatedSize");
                        d.IsOffline = Wmi.B(mo, "IsOffline");
                        d.IsReadOnly = Wmi.B(mo, "IsReadOnly");
                        d.BusType = Wmi.BusTypeText(Wmi.U32(mo, "BusType"));
                        if (Wmi.B(mo, "IsSystem") || Wmi.B(mo, "IsBoot")) d.ContainsSystemVolume = true;
                        var friendly = Wmi.S(mo, "FriendlyName");
                        if (string.IsNullOrWhiteSpace(d.Model) && !string.IsNullOrWhiteSpace(friendly)) d.Model = friendly;
                    }
                }
            }

            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk")))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    using (mo)
                    {
                        int number;
                        if (!int.TryParse(Wmi.S(mo, "DeviceId"), out number)) continue;
                        var d = disks.FirstOrDefault(x => x.Index == number);
                        if (d == null) continue;

                        uint mediaType = Wmi.U32(mo, "MediaType");
                        d.IsSsd = mediaType == 4;
                        if (mediaType == 3 || mediaType == 4 || mediaType == 5)
                            d.MediaType = Wmi.MediaTypeText(mediaType);

                        uint health = Wmi.U32(mo, "HealthStatus");
                        d.HealthStatus = health switch
                        {
                            0 => "良好",
                            1 => "注意",
                            2 => "不良",
                            _ => "未取得"
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("記憶域 WMI から追加情報を取得できませんでした: " + ex.Message);
        }
    }

    /// <summary>パーティション一覧を取得し、未割り当て領域も算出します。</summary>
    private static void LoadPartitions(List<PhysicalDiskInfo> disks, Action<string> log)
    {
        bool storageOk = false;
        try
        {
            var scope = Wmi.Connect(Wmi.StorageNamespace);
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_Partition"));
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    int diskNumber = Wmi.I32(mo, "DiskNumber");
                    var d = disks.FirstOrDefault(x => x.Index == diskNumber);
                    if (d == null) continue;

                    var letterRaw = Wmi.S(mo, "DriveLetter");
                    var letter = string.IsNullOrWhiteSpace(letterRaw) || letterRaw == "\0"
                        ? string.Empty
                        : letterRaw.Trim().TrimEnd(':').ToUpperInvariant() + ":";

                    var p = new PartitionInfo
                    {
                        DiskNumber = diskNumber,
                        PartitionNumber = Wmi.I32(mo, "PartitionNumber"),
                        Offset = Wmi.U64(mo, "Offset"),
                        Size = Wmi.U64(mo, "Size"),
                        DriveLetter = letter,
                        IsSystem = Wmi.B(mo, "IsSystem"),
                        IsBoot = Wmi.B(mo, "IsBoot"),
                        IsHidden = Wmi.B(mo, "IsHidden"),
                        TypeText = DescribePartitionType(mo)
                    };
                    d.Partitions.Add(p);
                    storageOk = true;
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("MSFT_Partition を取得できませんでした: " + ex.Message);
        }

        if (!storageOk)
        {
            // 代替: Win32_DiskPartition（パーティション番号は Index + 1）
            foreach (var d in disks)
            {
                try
                {
                    string q = $"SELECT * FROM Win32_DiskPartition WHERE DiskIndex={d.Index}";
                    using var s = new ManagementObjectSearcher(Wmi.CimV2Namespace, q);
                    foreach (ManagementObject mo in s.Get())
                    {
                        using (mo)
                        {
                            d.Partitions.Add(new PartitionInfo
                            {
                                DiskNumber = d.Index,
                                PartitionNumber = Wmi.I32(mo, "Index") + 1,
                                Offset = Wmi.U64(mo, "StartingOffset"),
                                Size = Wmi.U64(mo, "Size"),
                                TypeText = Wmi.S(mo, "Type"),
                                IsBoot = Wmi.B(mo, "BootPartition")
                            });
                        }
                    }
                }
                catch { }
            }
        }

        foreach (var d in disks)
        {
            // ドライブ文字をボリューム情報から補完
            foreach (var p in d.Partitions)
            {
                if (!string.IsNullOrEmpty(p.DriveLetter)) continue;
                var v = d.Volumes.FirstOrDefault(x => x.PartitionNumber == p.PartitionNumber);
                if (v != null) p.DriveLetter = v.DriveLetter;
            }

            var sorted = d.Partitions.OrderBy(p => p.Offset).ToList();
            d.Partitions.Clear();

            ulong cursor = 0;
            const ulong MinGap = 8UL * 1024 * 1024;   // 8MB 未満の隙間はアライメント調整とみなして表示しません
            foreach (var p in sorted)
            {
                if (p.Offset > cursor && p.Offset - cursor >= MinGap)
                {
                    d.Partitions.Add(new PartitionInfo
                    {
                        DiskNumber = d.Index,
                        PartitionNumber = -1,
                        Offset = cursor,
                        Size = p.Offset - cursor,
                        IsUnallocated = true,
                        TypeText = "未割り当て"
                    });
                }
                d.Partitions.Add(p);
                cursor = p.Offset + p.Size;
            }
            if (d.Size > cursor && d.Size - cursor >= MinGap)
            {
                d.Partitions.Add(new PartitionInfo
                {
                    DiskNumber = d.Index,
                    PartitionNumber = -1,
                    Offset = cursor,
                    Size = d.Size - cursor,
                    IsUnallocated = true,
                    TypeText = "未割り当て"
                });
            }
        }
    }

    private static string DescribePartitionType(ManagementBaseObject mo)
    {
        var gpt = Wmi.S(mo, "GptType");
        if (!string.IsNullOrWhiteSpace(gpt))
        {
            switch (gpt.Trim('{', '}').ToLowerInvariant())
            {
                case "c12a7328-f81f-11d2-ba4b-00a0c93ec93b": return "EFI システム";
                case "e3c9e316-0b5c-4db8-817d-f92df00215ae": return "Microsoft 予約 (MSR)";
                case "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7": return "基本データ";
                case "de94bba4-06d1-4d40-a16a-bfd50179d6ac": return "Windows 回復環境";
                default: return "GPT " + gpt;
            }
        }
        uint mbr = Wmi.U32(mo, "MbrType");
        return mbr switch
        {
            1 => "FAT12",
            4 => "FAT16",
            5 => "拡張",
            6 => "Huge",
            7 => "IFS (NTFS/exFAT)",
            12 => "FAT32",
            _ => "基本データ"
        };
    }

    /// <summary>
    /// USB 接続のボリュームに印を付けます。
    /// ポータブル SSD / HDD は「ローカルディスク」に見え、UASP 接続だと Win32_DiskDrive の InterfaceType も
    /// SCSI になるため、記憶域 WMI のバスの種類（BusType=USB）でも判定します。
    /// </summary>
    public static void MarkUsbVolumes(IEnumerable<PhysicalDiskInfo> disks, IEnumerable<VolumeInfo> volumes)
    {
        var usbLetters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in disks)
        {
            bool usb = string.Equals(d.BusType, "USB", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(d.InterfaceType, "USB", StringComparison.OrdinalIgnoreCase);
            if (!usb) continue;
            foreach (var dv in d.Volumes) usbLetters.Add(dv.DriveLetter);
        }
        foreach (var v in volumes)
            v.IsUsb = v.DriveTypeCode == 2 || usbLetters.Contains(v.DriveLetter);
    }

    /// <summary>ドライブ文字を持つ全ボリューム（光学・ネットワークを除く）。</summary>
    public static List<VolumeInfo> EnumerateVolumes()
    {
        var list = new List<VolumeInfo>();
        var sysLetter = SystemDriveLetter;
        try
        {
            using var s = new ManagementObjectSearcher(
                Wmi.CimV2Namespace, "SELECT * FROM Win32_LogicalDisk WHERE DriveType=2 OR DriveType=3");
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    var letter = Wmi.S(mo, "DeviceID").ToUpperInvariant();
                    list.Add(new VolumeInfo
                    {
                        DriveLetter = letter,
                        Label = Wmi.S(mo, "VolumeName"),
                        FileSystem = Wmi.S(mo, "FileSystem"),
                        Capacity = Wmi.U64(mo, "Size"),
                        FreeSpace = Wmi.U64(mo, "FreeSpace"),
                        DriveTypeCode = Wmi.U32(mo, "DriveType"),
                        IsSystemVolume = string.Equals(letter, sysLetter, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
        }
        catch { }
        return list.OrderBy(v => v.DriveLetter).ToList();
    }
}
