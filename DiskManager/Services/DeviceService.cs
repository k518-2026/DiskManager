using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DiskManager.Services;

/// <summary>ドライブの取り外し（安全な取り外し）、メディアの排出、ハードウェアの再認識。</summary>
public static class DeviceService
{
    private static readonly IntPtr InvalidHandle = new IntPtr(-1);

    private static SafeFileHandle OpenVolume(string driveLetter, uint access)
    {
        var letter = driveLetter.Trim().TrimEnd(':').ToUpperInvariant();
        string path = @"\\.\" + letter + ":";
        return NativeMethods.CreateFileW(path, access,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
    }

    /// <summary>ボリュームが属する物理デバイス番号を取得します。</summary>
    public static int GetDeviceNumber(string devicePath)
    {
        using var h = NativeMethods.CreateFileW(devicePath, 0,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return -1;

        int size = Marshal.SizeOf<NativeMethods.STORAGE_DEVICE_NUMBER>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_STORAGE_GET_DEVICE_NUMBER,
                    IntPtr.Zero, 0, buffer, (uint)size, out _, IntPtr.Zero))
                return -1;
            var sdn = Marshal.PtrToStructure<NativeMethods.STORAGE_DEVICE_NUMBER>(buffer);
            return sdn.DeviceNumber;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>デバイス番号からデバイスノード（DevInst）を探します。</summary>
    private static uint? FindDevInst(int deviceNumber, Guid interfaceGuid)
    {
        IntPtr set = NativeMethods.SetupDiGetClassDevsW(ref interfaceGuid, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
        if (set == InvalidHandle) return null;

        try
        {
            var ifData = new NativeMethods.SP_DEVICE_INTERFACE_DATA();
            ifData.cbSize = Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DATA>();

            for (uint index = 0; ; index++)
            {
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, index, ref ifData))
                    break;

                var devInfo = new NativeMethods.SP_DEVINFO_DATA();
                devInfo.cbSize = Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>();

                NativeMethods.SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out uint required, ref devInfo);
                if (required == 0) continue;

                IntPtr buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, NativeMethods.InterfaceDetailSize);
                    var devInfo2 = new NativeMethods.SP_DEVINFO_DATA();
                    devInfo2.cbSize = Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>();

                    if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                            set, ref ifData, buffer, required, out _, ref devInfo2))
                        continue;

                    string path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                    if (string.IsNullOrEmpty(path)) continue;
                    if (GetDeviceNumber(path) == deviceNumber) return devInfo2.DevInst;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }
        return null;
    }

    /// <summary>ボリュームをロックしてマウント解除します。</summary>
    private static bool LockAndDismount(SafeFileHandle h, Action<string> log)
    {
        bool locked = false;
        for (int i = 0; i < 10; i++)
        {
            if (NativeMethods.DeviceIoControl(h, NativeMethods.FSCTL_LOCK_VOLUME,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                locked = true;
                break;
            }
            Thread.Sleep(300);
        }
        if (!locked)
            log?.Invoke("ボリュームをロックできませんでした。ドライブ内のファイルを開いているアプリを閉じてください。");

        bool dismounted = NativeMethods.DeviceIoControl(h, NativeMethods.FSCTL_DISMOUNT_VOLUME,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        if (!dismounted)
            log?.Invoke("マウント解除に失敗しました。");

        return locked && dismounted;
    }

    private static void AllowMediaRemoval(SafeFileHandle h)
    {
        var pmr = new NativeMethods.PREVENT_MEDIA_REMOVAL { PreventMediaRemoval = false };
        int size = Marshal.SizeOf<NativeMethods.PREVENT_MEDIA_REMOVAL>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(pmr, buffer, false);
            NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_STORAGE_MEDIA_REMOVAL,
                buffer, (uint)size, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>USB メモリなどを安全に取り外します。</summary>
    public static bool SafelyRemove(string driveLetter, bool isOptical, Action<string> log)
    {
        var letter = driveLetter.Trim().TrimEnd(':').ToUpperInvariant() + ":";
        log?.Invoke($"{letter} の取り外しを開始します。");

        int deviceNumber;
        using (var h = OpenVolume(letter, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE))
        {
            if (h.IsInvalid)
            {
                log?.Invoke("ドライブを開けませんでした。管理者権限で実行しているか確認してください。");
                return false;
            }

            deviceNumber = GetDeviceNumber(@"\\.\" + letter);
            LockAndDismount(h, log);
            AllowMediaRemoval(h);

            // メディアの排出（光学ドライブはトレイが開き、一部の USB は取り外し可能になります）
            NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_STORAGE_EJECT_MEDIA,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }

        if (deviceNumber < 0)
        {
            log?.Invoke("デバイス番号を取得できませんでした。マウント解除のみ行いました。");
            return false;
        }

        var guid = isOptical ? NativeMethods.GUID_DEVINTERFACE_CDROM : NativeMethods.GUID_DEVINTERFACE_DISK;
        var devInst = FindDevInst(deviceNumber, guid);
        if (devInst == null)
        {
            log?.Invoke("デバイスノードが見つかりませんでした。マウント解除のみ行いました。");
            return false;
        }

        // 親（USB 大容量記憶装置など）を取り外し対象にします
        uint target = devInst.Value;
        if (NativeMethods.CM_Get_Parent(out uint parent, devInst.Value, 0) == NativeMethods.CR_SUCCESS)
            target = parent;

        var veto = new StringBuilder(260);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            veto.Clear();
            int cr = NativeMethods.CM_Request_Device_EjectW(target, out int vetoType, veto, veto.Capacity, 0);
            if (cr == NativeMethods.CR_SUCCESS && veto.Length == 0)
            {
                log?.Invoke($"{letter} を安全に取り外しました。ケーブルを抜いて構いません。");
                return true;
            }
            if (veto.Length > 0)
                log?.Invoke($"取り外しが拒否されました（{VetoText(vetoType)}: {veto}）。再試行します…");
            Thread.Sleep(600);
        }

        log?.Invoke("取り外しを完了できませんでした。ドライブを使用中のアプリを閉じてから再試行してください。");
        return false;
    }

    /// <summary>光学ドライブのトレイを開きます。</summary>
    public static bool EjectMedia(string driveLetter, Action<string> log)
    {
        using var h = OpenVolume(driveLetter, NativeMethods.GENERIC_READ);
        if (h.IsInvalid) { log?.Invoke("ドライブを開けませんでした。"); return false; }
        AllowMediaRemoval(h);
        bool ok = NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_STORAGE_EJECT_MEDIA,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        log?.Invoke(ok ? "メディアを排出しました。" : "メディアを排出できませんでした。");
        return ok;
    }

    /// <summary>光学ドライブのトレイを閉じます。</summary>
    public static bool LoadMedia(string driveLetter, Action<string> log)
    {
        using var h = OpenVolume(driveLetter, NativeMethods.GENERIC_READ);
        if (h.IsInvalid) { log?.Invoke("ドライブを開けませんでした。"); return false; }
        bool ok = NativeMethods.DeviceIoControl(h, NativeMethods.IOCTL_STORAGE_LOAD_MEDIA,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        log?.Invoke(ok ? "トレイを閉じました。" : "トレイを閉じられませんでした（スロット式では使用できません）。");
        return ok;
    }

    /// <summary>ハードウェアの変更をスキャンします（デバイスマネージャーの「ハードウェア変更のスキャン」相当）。</summary>
    public static bool RescanDevices(Action<string> log)
    {
        int cr = NativeMethods.CM_Locate_DevNodeW(out uint root, null, NativeMethods.CM_LOCATE_DEVNODE_NORMAL);
        if (cr != NativeMethods.CR_SUCCESS)
        {
            log?.Invoke("デバイスツリーのルートを取得できませんでした。");
            return false;
        }
        cr = NativeMethods.CM_Reenumerate_DevNode(root, 0);
        log?.Invoke(cr == NativeMethods.CR_SUCCESS
            ? "ハードウェアの再認識を実行しました。"
            : "再認識に失敗しました。");
        return cr == NativeMethods.CR_SUCCESS;
    }

    private static string VetoText(int vetoType) => vetoType switch
    {
        1 => "レガシデバイス",
        2 => "ページングファイル使用中",
        3 => "ハイバネーションファイル使用中",
        4 => "クラッシュダンプファイル使用中",
        5 => "ファイルシステムがロック不可",
        6 => "アプリケーションが拒否",
        7 => "デバイスドライバーが拒否",
        8 => "他のデバイスが拒否",
        9 => "ドライバーがサポートしていない",
        10 => "非依存デバイス",
        11 => "取り外し不可",
        _ => "理由不明"
    };
}
