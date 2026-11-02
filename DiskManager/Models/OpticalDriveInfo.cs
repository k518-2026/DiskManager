using System.Collections.Generic;
using DiskManager.Common;

namespace DiskManager.Models;

/// <summary>光学ドライブと挿入メディアの情報。</summary>
public class OpticalDriveInfo : ObservableObject
{
    public string DriveLetter { get; set; }      // 例: "D:"
    public string Model { get; set; }
    public string DeviceId { get; set; }

    private bool _mediaLoaded;
    public bool MediaLoaded { get => _mediaLoaded; set { Set(ref _mediaLoaded, value); OnPropertyChanged(nameof(StatusText)); } }

    private string _label = "";
    public string VolumeLabel { get => _label; set => Set(ref _label, value); }

    private string _fs = "";
    public string FileSystem { get => _fs; set => Set(ref _fs, value); }

    private long _mediaSize;
    public long MediaSize { get => _mediaSize; set { Set(ref _mediaSize, value); OnPropertyChanged(nameof(MediaSizeText)); } }

    public string MediaSizeText => MediaSize <= 0 ? "-" : Fmt.Bytes(MediaSize);

    public string StatusText => MediaLoaded ? "メディアあり" : "メディアなし";

    public string Display => $"{DriveLetter}  {Model}";
    public override string ToString() => Display;
}

/// <summary>ディスクの読み取り検査（ベリファイ）の結果。</summary>
public class DiscScanResult
{
    public long TotalSectors { get; set; }
    public long BadSectors { get; set; }
    public long RecoveredSectors { get; set; }
    public List<long> BadSectorSamples { get; } = new();
    public bool Cancelled { get; set; }

    public double BadRatio => TotalSectors == 0 ? 0 : (double)BadSectors / TotalSectors;
}
