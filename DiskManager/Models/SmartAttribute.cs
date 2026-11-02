using DiskManager.Common;

namespace DiskManager.Models;

/// <summary>S.M.A.R.T. 属性1件。</summary>
public class SmartAttribute : ObservableObject
{
    public byte Id { get; set; }
    public string Name { get; set; }
    public byte Current { get; set; }
    public byte Worst { get; set; }
    public byte Threshold { get; set; }
    public ulong RawValue { get; set; }
    public bool IsCritical { get; set; }

    public string IdText => $"{Id:D3} (0x{Id:X2})";
    public string RawText => RawValue.ToString();
    public string ThresholdText => Threshold == 0 ? "-" : Threshold.ToString();

    public string Judgement
    {
        get
        {
            if (Threshold > 0 && Current <= Threshold) return "不良";
            if (Threshold > 0 && Current <= Threshold + 10) return "注意";
            if (IsCritical && RawValue > 0) return "注意";
            return "良好";
        }
    }
}
