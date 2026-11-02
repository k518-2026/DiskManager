using DiskManager.Common;

namespace DiskManager.Models;

/// <summary>物理ディスク上のパーティション1件。</summary>
public class PartitionInfo : ObservableObject
{
    public int DiskNumber { get; set; }
    public int PartitionNumber { get; set; }     // diskpart / MSFT_Partition と同じ 1 始まりの番号
    public ulong Offset { get; set; }
    public ulong Size { get; set; }
    public string DriveLetter { get; set; }      // 無い場合は空
    public string TypeText { get; set; }
    public bool IsSystem { get; set; }
    public bool IsBoot { get; set; }
    public bool IsHidden { get; set; }
    public bool IsUnallocated { get; set; }

    public string NumberText => IsUnallocated ? "—" : PartitionNumber.ToString();
    public string SizeText => Fmt.Bytes(Size);
    public string OffsetText => Fmt.Bytes(Offset);
    public string ProtectedText => IsSystem || IsBoot ? "システム" : (IsHidden ? "隠し" : "");

    public string Display => IsUnallocated
        ? $"未割り当て領域  {SizeText}"
        : $"パーティション {PartitionNumber}  {(string.IsNullOrEmpty(DriveLetter) ? "(文字なし)" : DriveLetter)}  {SizeText}";

    public override string ToString() => Display;
}
