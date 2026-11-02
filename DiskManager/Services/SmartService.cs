using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using DiskManager.Models;

namespace DiskManager.Services;

/// <summary>S.M.A.R.T. 情報（ROOT\WMI）を読み取り、健康状態を判定します。</summary>
public static class SmartService
{
    public class SmartResult
    {
        public bool Available { get; set; }
        public bool PredictFailure { get; set; }
        public string Summary { get; set; } = "未取得";
        public string Detail { get; set; } = "";
        public int? TemperatureCelsius { get; set; }
        public ulong? PowerOnHours { get; set; }
        public ulong? PowerCycleCount { get; set; }
        public ulong ReallocatedSectors { get; set; }
        public ulong PendingSectors { get; set; }
        public ulong UncorrectableSectors { get; set; }
        public List<SmartAttribute> Attributes { get; } = new();
    }

    /// <summary>重大な劣化を示す属性 ID（生の値が 0 より大きいと注意）。</summary>
    private static readonly HashSet<byte> CriticalIds = new() { 5, 10, 184, 187, 188, 196, 197, 198, 201 };

    private static readonly Dictionary<byte, string> Names = new()
    {
        { 1,   "リードエラーレート" },
        { 2,   "スループットパフォーマンス" },
        { 3,   "スピンアップ時間" },
        { 4,   "スタート/ストップ回数" },
        { 5,   "代替処理済セクタ数" },
        { 7,   "シークエラーレート" },
        { 8,   "シークタイムパフォーマンス" },
        { 9,   "使用時間（時間）" },
        { 10,  "スピンアップ再試行回数" },
        { 11,  "キャリブレーション再試行回数" },
        { 12,  "電源投入回数" },
        { 170, "予備ブロック残数" },
        { 171, "プログラム失敗回数" },
        { 172, "消去失敗回数" },
        { 173, "平均消去回数（ウェアレベリング）" },
        { 174, "予期しない電源断回数" },
        { 175, "プログラム失敗回数（チップ）" },
        { 177, "ウェアレベリング数" },
        { 179, "使用済み予備ブロック数" },
        { 180, "未使用予備ブロック数" },
        { 181, "プログラム失敗合計" },
        { 182, "消去失敗合計" },
        { 183, "SATA ダウンシフトエラー" },
        { 184, "エンドツーエンドエラー" },
        { 187, "訂正不可能エラー報告数" },
        { 188, "コマンドタイムアウト" },
        { 189, "高フライ書き込み" },
        { 190, "温度差（エアフロー）" },
        { 191, "Gセンサーエラー" },
        { 192, "電源断リトラクト回数" },
        { 193, "ロード/アンロードサイクル数" },
        { 194, "温度" },
        { 195, "ハードウェア ECC 訂正" },
        { 196, "再配置イベント数" },
        { 197, "代替処理保留中セクタ数" },
        { 198, "回復不可能セクタ数" },
        { 199, "UltraDMA CRC エラー数" },
        { 200, "書き込みエラーレート" },
        { 201, "ソフトリードエラーレート" },
        { 202, "データアドレスマークエラー" },
        { 231, "残り寿命（SSD）" },
        { 232, "予備ブロック残量" },
        { 233, "メディア消耗指標（SSD）" },
        { 241, "総書き込み量（LBA）" },
        { 242, "総読み込み量（LBA）" },
        { 246, "累積 NAND 書き込み量" }
    };

    public static SmartResult Read(PhysicalDiskInfo disk, Action<string> log = null)
    {
        var result = new SmartResult();
        if (disk == null) return result;

        var target = NormalizeInstance(disk.PnpDeviceId);

        try
        {
            var scope = Wmi.Connect(Wmi.WmiNamespace);

            // 障害予測ステータス
            using (var s = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM MSStorageDriver_FailurePredictStatus")))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    using (mo)
                    {
                        if (!InstanceMatches(Wmi.S(mo, "InstanceName"), target)) continue;
                        result.Available = true;
                        result.PredictFailure = Wmi.B(mo, "PredictFailure");
                    }
                }
            }

            // 属性データ（VendorSpecific 512 バイト）
            using (var s = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM MSStorageDriver_FailurePredictData")))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    using (mo)
                    {
                        if (!InstanceMatches(Wmi.S(mo, "InstanceName"), target)) continue;
                        var raw = Wmi.Bytes(mo, "VendorSpecific");
                        if (raw != null)
                        {
                            result.Available = true;
                            ParseAttributes(raw, result);
                        }
                    }
                }
            }

            // しきい値
            using (var s = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT * FROM MSStorageDriver_FailurePredictThresholds")))
            {
                foreach (ManagementObject mo in s.Get())
                {
                    using (mo)
                    {
                        if (!InstanceMatches(Wmi.S(mo, "InstanceName"), target)) continue;
                        var raw = Wmi.Bytes(mo, "VendorSpecific");
                        if (raw != null) ApplyThresholds(raw, result);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("S.M.A.R.T. の読み取りに失敗しました: " + ex.Message);
        }

        Judge(disk, result);
        return result;
    }

    private static string NormalizeInstance(string s)
        => (s ?? string.Empty).Replace("\\", "").ToUpperInvariant();

    private static bool InstanceMatches(string instanceName, string normalizedPnp)
    {
        if (string.IsNullOrEmpty(instanceName) || string.IsNullOrEmpty(normalizedPnp)) return false;
        var a = NormalizeInstance(instanceName);
        if (a.EndsWith("_0", StringComparison.Ordinal)) a = a.Substring(0, a.Length - 2);
        return a == normalizedPnp || a.StartsWith(normalizedPnp, StringComparison.Ordinal)
               || normalizedPnp.StartsWith(a, StringComparison.Ordinal);
    }

    private static void ParseAttributes(byte[] raw, SmartResult result)
    {
        // 先頭 2 バイトはリビジョン。以降 12 バイトごとに属性が並びます。
        for (int offset = 2; offset + 11 < raw.Length; offset += 12)
        {
            byte id = raw[offset];
            if (id == 0) continue;

            ulong rawValue = 0;
            for (int i = 5; i >= 0; i--)
                rawValue = (rawValue << 8) | raw[offset + 5 + i];

            var attr = new SmartAttribute
            {
                Id = id,
                Name = Names.TryGetValue(id, out var n) ? n : "ベンダー固有属性",
                Current = raw[offset + 3],
                Worst = raw[offset + 4],
                RawValue = rawValue,
                IsCritical = CriticalIds.Contains(id)
            };
            result.Attributes.Add(attr);

            switch (id)
            {
                case 5: result.ReallocatedSectors = rawValue; break;
                case 9: result.PowerOnHours = rawValue & 0xFFFFFFFF; break;
                case 12: result.PowerCycleCount = rawValue & 0xFFFFFFFF; break;
                case 194:
                case 190:
                    int t = (int)(rawValue & 0xFF);
                    if (t > 0 && t < 120) result.TemperatureCelsius = t;
                    break;
                case 197: result.PendingSectors = rawValue; break;
                case 198: result.UncorrectableSectors = rawValue; break;
            }
        }
    }

    private static void ApplyThresholds(byte[] raw, SmartResult result)
    {
        for (int offset = 2; offset + 11 < raw.Length; offset += 12)
        {
            byte id = raw[offset];
            if (id == 0) continue;
            var attr = result.Attributes.FirstOrDefault(a => a.Id == id);
            if (attr != null) attr.Threshold = raw[offset + 1];
        }
    }

    private static void Judge(PhysicalDiskInfo disk, SmartResult result)
    {
        var notes = new List<string>();

        if (!result.Available)
        {
            result.Summary = disk.HealthStatus != "未取得" ? disk.HealthStatus : "取得不可";
            result.Detail = "このドライブは S.M.A.R.T. を公開していません。"
                          + "USB 変換ブリッジ経由の外付けドライブや一部の NVMe では取得できないことがあります。"
                          + (disk.HealthStatus != "未取得" ? $"（Windows の記憶域による判定: {disk.HealthStatus}）" : "");
            return;
        }

        bool bad = false, warn = false;

        if (result.PredictFailure) { bad = true; notes.Add("ドライブ自身が故障予測フラグを立てています。ただちにバックアップしてください。"); }
        if (result.ReallocatedSectors > 0) { warn = true; notes.Add($"代替処理済セクタが {result.ReallocatedSectors} 個あります。"); }
        if (result.PendingSectors > 0) { bad = true; notes.Add($"代替処理保留中セクタが {result.PendingSectors} 個あります。読み取り不能領域が発生しています。"); }
        if (result.UncorrectableSectors > 0) { bad = true; notes.Add($"回復不可能セクタが {result.UncorrectableSectors} 個あります。"); }

        foreach (var a in result.Attributes)
        {
            if (a.Threshold > 0 && a.Current <= a.Threshold)
            {
                bad = true;
                notes.Add($"属性 {a.Id}（{a.Name}）がしきい値を下回っています。");
            }
        }

        if (result.TemperatureCelsius.HasValue && result.TemperatureCelsius.Value >= 55)
        {
            warn = true;
            notes.Add($"温度が {result.TemperatureCelsius.Value} ℃ と高めです。冷却を確認してください。");
        }

        result.Summary = bad ? "不良" : (warn ? "注意" : "良好");
        if (notes.Count == 0)
            notes.Add("重大な劣化の兆候は見つかりませんでした。");
        if (result.PowerOnHours.HasValue)
            notes.Add($"通電時間は約 {result.PowerOnHours.Value:N0} 時間です。");

        result.Detail = string.Join(" ", notes);
    }
}
