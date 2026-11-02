using System.Collections.Generic;
using DiskManager.Common;

namespace DiskManager.Models;

/// <summary>物理ディスク1台の情報。</summary>
public class PhysicalDiskInfo : ObservableObject
{
    public int Index { get; set; }                       // 0,1,2... = \\.\PHYSICALDRIVEn
    public string DeviceId { get; set; }                 // \\.\PHYSICALDRIVE0
    public string PnpDeviceId { get; set; }
    public string Model { get; set; }
    public string SerialNumber { get; set; }
    public string FirmwareRevision { get; set; }
    public string InterfaceType { get; set; }
    public string MediaType { get; set; }
    public string BusType { get; set; }
    public string PartitionStyle { get; set; } = "不明";
    public ulong Size { get; set; }
    public ulong AllocatedSize { get; set; }
    public uint PartitionCount { get; set; }
    public string WmiStatus { get; set; }
    public bool IsRemovable { get; set; }
    public bool IsOffline { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsSsd { get; set; }
    public bool ContainsSystemVolume { get; set; }

    public List<VolumeInfo> Volumes { get; } = new();
    public List<PartitionInfo> Partitions { get; } = new();

    private string _health = "未取得";
    public string HealthStatus { get => _health; set => Set(ref _health, value); }

    private string _healthDetail = "";
    public string HealthDetail { get => _healthDetail; set => Set(ref _healthDetail, value); }

    public string SizeText => Fmt.Bytes(Size);

    public string Title => $"ディスク {Index}";

    public string Subtitle
    {
        get
        {
            var kind = IsRemovable ? "リムーバブル" : (IsSsd ? "SSD" : "HDD");
            var bus = string.IsNullOrEmpty(BusType) ? InterfaceType : BusType;
            return $"{SizeText} · {kind} · {bus}";
        }
    }

    public string ModelText => string.IsNullOrWhiteSpace(Model) ? "(型番不明)" : Model.Trim();

    public string LetterSummary
    {
        get
        {
            if (Volumes.Count == 0) return "ドライブ文字なし";
            var list = new List<string>();
            foreach (var v in Volumes) list.Add(v.DriveLetter);
            return string.Join(" ", list);
        }
    }

    public override string ToString() => $"{Title}: {ModelText}";
}
