using System;
using System.Collections.Generic;
using DiskManager.Common;

namespace DiskManager.Models;

/// <summary>ドライブ文字を持つボリューム（論理ドライブ）の情報。</summary>
public class VolumeInfo : ObservableObject
{
    public string DriveLetter { get; set; }          // 例: "E:"
    public string Label { get; set; }
    public string FileSystem { get; set; }
    public ulong Capacity { get; set; }
    public ulong FreeSpace { get; set; }
    public uint DriveTypeCode { get; set; }          // Win32_LogicalDisk.DriveType
    public int DiskNumber { get; set; } = -1;
    public int PartitionNumber { get; set; } = -1;
    public bool IsSystemVolume { get; set; }
    public bool IsBootVolume { get; set; }

    /// <summary>USB 接続（USB メモリ、または USB でつないだポータブル SSD / HDD）。</summary>
    public bool IsUsb { get; set; }

    /// <summary>フォーマットしてはいけないボリューム。C: はシステムの判定に関係なく常に保護します。</summary>
    public bool IsProtected
        => IsSystemVolume || IsBootVolume
           || string.Equals(DriveLetter, "C:", StringComparison.OrdinalIgnoreCase);

    public bool CanFormat => !IsProtected;

    // ---- まとめてフォーマットの一覧で使う状態 ----

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value && !IsProtected); }

    private string _formatStatus = "";
    public string FormatStatus { get => _formatStatus; set => Set(ref _formatStatus, value); }

    private double _formatPercent;
    public double FormatPercent { get => _formatPercent; set => Set(ref _formatPercent, value); }

    public ulong UsedSpace => Capacity > FreeSpace ? Capacity - FreeSpace : 0;
    public double UsedRatio => Capacity == 0 ? 0 : (double)UsedSpace / Capacity;

    public string CapacityText => Fmt.Bytes(Capacity);
    public string FreeText => Fmt.Bytes(FreeSpace);
    public string UsedText => Fmt.Bytes(UsedSpace);
    public string UsedPercentText => Capacity == 0 ? "-" : (UsedRatio * 100).ToString("0.0") + " %";

    public string DriveTypeText => DriveTypeCode switch
    {
        2 => "リムーバブル",
        3 => "ローカルディスク",
        4 => "ネットワーク",
        5 => "光学ドライブ",
        6 => "RAMディスク",
        _ => "不明"
    };

    public string ProtectionText
        => IsProtected ? "保護（システム）" : "";

    public string Display
        => (string.IsNullOrEmpty(Label)
            ? $"{DriveLetter} ({FileSystem})"
            : $"{DriveLetter} {Label} ({FileSystem})")
           + (IsUsb ? " · USB" : "");

    public override string ToString() => Display;
}
