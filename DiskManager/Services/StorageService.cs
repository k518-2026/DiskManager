using System;
using System.Linq;
using System.Management;

namespace DiskManager.Services;

/// <summary>
/// 記憶域 WMI（ROOT\Microsoft\Windows\Storage）を通じたパーティション操作。
/// diskpart のテキスト解析に依存しないため、番号のずれが起きません。
/// </summary>
public static class StorageService
{
    public class OpResult
    {
        public bool Success { get; set; }
        public uint ReturnValue { get; set; }
        public string Message { get; set; } = "";
        public string CreatedDriveLetter { get; set; } = "";
    }

    private static ManagementObject GetDisk(ManagementScope scope, int diskNumber)
    {
        using var s = new ManagementObjectSearcher(scope,
            new ObjectQuery($"SELECT * FROM MSFT_Disk WHERE Number={diskNumber}"));
        foreach (ManagementObject mo in s.Get()) return mo;
        return null;
    }

    private static ManagementObject GetPartition(ManagementScope scope, int diskNumber, int partitionNumber)
    {
        using var s = new ManagementObjectSearcher(scope,
            new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DiskNumber={diskNumber} AND PartitionNumber={partitionNumber}"));
        foreach (ManagementObject mo in s.Get()) return mo;
        return null;
    }

    /// <summary>パーティションを作成します。</summary>
    public static OpResult CreatePartition(
        int diskNumber, ulong sizeBytes, bool useMaximumSize, char driveLetter, bool assignAutomatically)
    {
        try
        {
            var scope = Wmi.Connect(Wmi.StorageNamespace);
            using var disk = GetDisk(scope, diskNumber);
            if (disk == null)
                return Fail($"ディスク {diskNumber} が見つかりませんでした。");

            var inParams = disk.GetMethodParameters("CreatePartition");
            if (useMaximumSize)
            {
                inParams["UseMaximumSize"] = true;
            }
            else
            {
                inParams["Size"] = sizeBytes;
            }

            if (driveLetter != '\0')
                inParams["DriveLetter"] = driveLetter;
            else if (assignAutomatically)
                inParams["AssignDriveLetter"] = true;

            using var outParams = disk.InvokeMethod("CreatePartition", inParams, null);
            var res = Interpret(outParams);

            if (res.Success)
            {
                try
                {
                    var created = outParams["CreatedPartition"] as ManagementBaseObject;
                    if (created != null)
                    {
                        var l = Wmi.S(created, "DriveLetter");
                        if (!string.IsNullOrWhiteSpace(l) && l != "\0")
                            res.CreatedDriveLetter = l.Trim().TrimEnd(':').ToUpperInvariant() + ":";
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(res.CreatedDriveLetter) && driveLetter != '\0')
                    res.CreatedDriveLetter = driveLetter + ":";
                res.Message = "パーティションを作成しました。"
                            + (string.IsNullOrEmpty(res.CreatedDriveLetter) ? "" : " ドライブ文字: " + res.CreatedDriveLetter);
            }
            return res;
        }
        catch (Exception ex)
        {
            return Fail("パーティションの作成に失敗しました: " + ex.Message);
        }
    }

    /// <summary>パーティションを削除します。</summary>
    public static OpResult DeletePartition(int diskNumber, int partitionNumber)
    {
        try
        {
            var scope = Wmi.Connect(Wmi.StorageNamespace);
            using var part = GetPartition(scope, diskNumber, partitionNumber);
            if (part == null)
                return Fail($"ディスク {diskNumber} のパーティション {partitionNumber} が見つかりませんでした。");

            if (Wmi.B(part, "IsSystem") || Wmi.B(part, "IsBoot"))
                return Fail("システム／起動パーティションのため削除できません。");

            var inParams = part.GetMethodParameters("DeleteObject");
            using var outParams = part.InvokeMethod("DeleteObject", inParams, null);
            var res = Interpret(outParams);
            if (res.Success) res.Message = $"パーティション {partitionNumber} を削除しました。";
            return res;
        }
        catch (Exception ex)
        {
            return Fail("パーティションの削除に失敗しました: " + ex.Message);
        }
    }

    /// <summary>未初期化ディスクを MBR または GPT で初期化します。</summary>
    public static OpResult InitializeDisk(int diskNumber, bool useGpt)
    {
        try
        {
            var scope = Wmi.Connect(Wmi.StorageNamespace);
            using var disk = GetDisk(scope, diskNumber);
            if (disk == null) return Fail($"ディスク {diskNumber} が見つかりませんでした。");

            var inParams = disk.GetMethodParameters("Initialize");
            inParams["PartitionStyle"] = (ushort)(useGpt ? 2 : 1);
            using var outParams = disk.InvokeMethod("Initialize", inParams, null);
            var res = Interpret(outParams);
            if (res.Success) res.Message = $"ディスク {diskNumber} を {(useGpt ? "GPT" : "MBR")} で初期化しました。";
            return res;
        }
        catch (Exception ex)
        {
            return Fail("ディスクの初期化に失敗しました: " + ex.Message);
        }
    }

    /// <summary>ディスク全体を消去します。zeroOut = true でディスク全域にゼロを書き込みます（時間がかかります）。</summary>
    public static OpResult ClearDisk(int diskNumber, bool zeroOut)
    {
        try
        {
            var scope = Wmi.Connect(Wmi.StorageNamespace);
            using var disk = GetDisk(scope, diskNumber);
            if (disk == null) return Fail($"ディスク {diskNumber} が見つかりませんでした。");

            if (Wmi.B(disk, "IsSystem") || Wmi.B(disk, "IsBoot"))
                return Fail("システムディスクのため消去できません。");

            var inParams = disk.GetMethodParameters("Clear");
            inParams["RemoveData"] = true;
            inParams["RemoveOEM"] = true;
            inParams["ZeroOutEntireDisk"] = zeroOut;
            using var outParams = disk.InvokeMethod("Clear", inParams, null);
            var res = Interpret(outParams);
            if (res.Success)
                res.Message = zeroOut
                    ? $"ディスク {diskNumber} の全域にゼロを書き込み、パーティションを削除しました。"
                    : $"ディスク {diskNumber} のパーティション情報を削除しました。";
            return res;
        }
        catch (Exception ex)
        {
            return Fail("ディスクの消去に失敗しました: " + ex.Message);
        }
    }

    /// <summary>ディスクをオンライン／オフラインに切り替えます。</summary>
    public static OpResult SetOnline(int diskNumber, bool online)
    {
        try
        {
            var scope = Wmi.Connect(Wmi.StorageNamespace);
            using var disk = GetDisk(scope, diskNumber);
            if (disk == null) return Fail($"ディスク {diskNumber} が見つかりませんでした。");

            string method = online ? "Online" : "Offline";
            var inParams = disk.GetMethodParameters(method);
            using var outParams = disk.InvokeMethod(method, inParams, null);
            var res = Interpret(outParams);
            if (res.Success)
                res.Message = $"ディスク {diskNumber} を{(online ? "オンライン" : "オフライン")}にしました。";
            return res;
        }
        catch (Exception ex)
        {
            return Fail("状態の切り替えに失敗しました: " + ex.Message);
        }
    }

    /// <summary>ドライブ文字を割り当てます。</summary>
    public static OpResult AssignDriveLetter(int diskNumber, int partitionNumber, char letter)
    {
        try
        {
            var scope = Wmi.Connect(Wmi.StorageNamespace);
            using var part = GetPartition(scope, diskNumber, partitionNumber);
            if (part == null) return Fail("パーティションが見つかりませんでした。");

            var inParams = part.GetMethodParameters("AddAccessPath");
            if (letter == '\0') inParams["AssignDriveLetter"] = true;
            else inParams["AccessPath"] = letter + ":\\";
            using var outParams = part.InvokeMethod("AddAccessPath", inParams, null);
            var res = Interpret(outParams);
            if (res.Success) res.Message = "ドライブ文字を割り当てました。";
            return res;
        }
        catch (Exception ex)
        {
            return Fail("ドライブ文字の割り当てに失敗しました: " + ex.Message);
        }
    }

    private static OpResult Fail(string message)
        => new OpResult { Success = false, Message = message };

    private static OpResult Interpret(ManagementBaseObject outParams)
    {
        var res = new OpResult();
        uint rv = 0;
        try { rv = Convert.ToUInt32(outParams["ReturnValue"]); } catch { }
        res.ReturnValue = rv;
        res.Success = rv == 0;

        if (!res.Success)
        {
            string extended = "";
            try
            {
                var ext = outParams["ExtendedStatus"] as ManagementBaseObject;
                if (ext != null)
                {
                    var m = Wmi.S(ext, "Message");
                    if (!string.IsNullOrWhiteSpace(m)) extended = " / " + m;
                }
            }
            catch { }

            res.Message = ErrorText(rv) + $"（コード {rv}）" + extended;
        }
        return res;
    }

    private static string ErrorText(uint code) => code switch
    {
        1 => "この操作はサポートされていません。",
        2 => "エラーが発生しました。",
        3 => "タイムアウトしました。",
        4 => "処理に失敗しました。",
        5 => "パラメーターが不正です。",
        4096 => "処理をジョブとして開始しました。",
        40000 => "アクセスが拒否されました。",
        40001 => "アクセスが拒否されました。管理者として実行しているか確認してください。",
        40002 => "指定した領域が使用中です。",
        41001 => "サイズがディスクの最大値を超えています。",
        41002 => "サイズが最小値を下回っています。",
        42002 => "十分な空き領域がありません。",
        42010 => "パーティションが見つかりません。",
        43000 => "ディスクがオフラインです。オンラインにしてから実行してください。",
        43001 => "ディスクが読み取り専用です。",
        _ => "処理に失敗しました。"
    };
}
