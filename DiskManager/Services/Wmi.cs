using System;
using System.Management;

namespace DiskManager.Services;

/// <summary>WMI プロパティを例外なく取り出すためのヘルパー。</summary>
internal static class Wmi
{
    public const string StorageNamespace = @"\\.\ROOT\Microsoft\Windows\Storage";
    public const string CimV2Namespace = @"\\.\ROOT\CIMV2";
    public const string WmiNamespace = @"\\.\ROOT\WMI";

    public static string S(ManagementBaseObject o, string prop)
    {
        try { return o[prop]?.ToString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static ulong U64(ManagementBaseObject o, string prop)
    {
        try
        {
            var v = o[prop];
            if (v == null) return 0;
            return System.Convert.ToUInt64(v);
        }
        catch { return 0; }
    }

    public static uint U32(ManagementBaseObject o, string prop)
    {
        try
        {
            var v = o[prop];
            if (v == null) return 0;
            return System.Convert.ToUInt32(v);
        }
        catch { return 0; }
    }

    public static int I32(ManagementBaseObject o, string prop)
    {
        try
        {
            var v = o[prop];
            if (v == null) return -1;
            return System.Convert.ToInt32(v);
        }
        catch { return -1; }
    }

    public static bool B(ManagementBaseObject o, string prop)
    {
        try
        {
            var v = o[prop];
            return v != null && System.Convert.ToBoolean(v);
        }
        catch { return false; }
    }

    public static byte[] Bytes(ManagementBaseObject o, string prop)
    {
        try { return o[prop] as byte[]; }
        catch { return null; }
    }

    /// <summary>WQL のリテラル内で使えるようにバックスラッシュとクォートをエスケープします。</summary>
    public static string Escape(string value)
        => (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");

    public static ManagementScope Connect(string ns)
    {
        var scope = new ManagementScope(ns, new ConnectionOptions { EnablePrivileges = true });
        scope.Connect();
        return scope;
    }

    public static string BusTypeText(uint code) => code switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE1394",
        5 => "SSA",
        6 => "ファイバチャネル",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "仮想ディスク",
        16 => "記憶域スペース",
        17 => "NVMe",
        _ => "不明"
    };

    public static string MediaTypeText(uint code) => code switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "不明"
    };

    public static string PartitionStyleText(uint code) => code switch
    {
        1 => "MBR",
        2 => "GPT",
        _ => "未初期化 / RAW"
    };
}
