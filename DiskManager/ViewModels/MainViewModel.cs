using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DiskManager.Common;
using DiskManager.Models;
using DiskManager.Services;
using DiskManager.Views;

namespace DiskManager.ViewModels;

public class MainViewModel : ObservableObject
{
    private CancellationTokenSource _cts;

    public MainViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        LoadSmartCommand = new AsyncRelayCommand(_ => LoadSmartAsync(), _ => SelectedDisk != null);

        CreatePartitionCommand = new AsyncRelayCommand(_ => CreatePartitionAsync(), _ => SelectedDisk != null);
        DeletePartitionCommand = new AsyncRelayCommand(_ => DeletePartitionAsync(), _ => SelectedPartition != null && !SelectedPartition.IsUnallocated);
        InitializeDiskCommand = new AsyncRelayCommand(_ => InitializeDiskAsync(), _ => SelectedDisk != null);
        ClearDiskCommand = new AsyncRelayCommand(_ => ClearDiskAsync(), _ => SelectedDisk != null);
        ToggleOnlineCommand = new AsyncRelayCommand(_ => ToggleOnlineAsync(), _ => SelectedDisk != null);
        AssignLetterCommand = new AsyncRelayCommand(_ => AssignLetterAsync(), _ => SelectedPartition != null && !SelectedPartition.IsUnallocated);

        FormatCommand = new AsyncRelayCommand(_ => FormatAsync(), _ => SelectedFormatVolume != null);
        FormatCheckedCommand = new AsyncRelayCommand(_ => FormatCheckedAsync(),
            _ => !IsBusy && FormatVolumes.Any(v => v.IsChecked && v.CanFormat));
        WipeFreeSpaceCommand = new AsyncRelayCommand(_ => WipeFreeSpaceAsync(), _ => SelectedFormatVolume != null);
        SetLabelCommand = new RelayCommand(_ => SetLabel(), _ => SelectedFormatVolume != null);

        SafeRemoveCommand = new AsyncRelayCommand(_ => SafeRemoveAsync(), _ => SelectedVolume != null);
        RescanCommand = new AsyncRelayCommand(_ => RescanAsync());
        RunDiskPartCommand = new AsyncRelayCommand(_ => RunDiskPartAsync(), _ => !string.IsNullOrWhiteSpace(DiskPartScript));
        ClearReadOnlyCommand = new AsyncRelayCommand(_ => ClearReadOnlyAsync(), _ => SelectedDisk != null);

        OpticalRefreshCommand = new AsyncRelayCommand(_ => RefreshOpticalAsync());
        OpticalSummaryCommand = new AsyncRelayCommand(_ => OpticalSummaryAsync(), _ => SelectedOptical != null);
        OpticalVerifyCommand = new AsyncRelayCommand(_ => OpticalVerifyAsync(), _ => SelectedOptical != null);
        OpticalIsoCommand = new AsyncRelayCommand(_ => OpticalIsoAsync(), _ => SelectedOptical != null);
        OpticalCopyCommand = new AsyncRelayCommand(_ => OpticalCopyAsync(), _ => SelectedOptical != null);
        OpticalEjectCommand = new RelayCommand(_ => DeviceService.EjectMedia(SelectedOptical.DriveLetter, Log), _ => SelectedOptical != null);
        OpticalLoadCommand = new RelayCommand(_ => DeviceService.LoadMedia(SelectedOptical.DriveLetter, Log), _ => SelectedOptical != null);

        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        ClearLogCommand = new RelayCommand(_ => LogLines.Clear());
        SaveLogCommand = new RelayCommand(_ => SaveLog());
    }

    // ---------------- コレクション ----------------

    public ObservableCollection<PhysicalDiskInfo> Disks { get; } = new();
    public ObservableCollection<PartitionInfo> Partitions { get; } = new();
    public ObservableCollection<VolumeInfo> Volumes { get; } = new();
    public ObservableCollection<VolumeInfo> AllVolumes { get; } = new();
    /// <summary>フォーマット タブに出すボリューム（「USB 接続だけ」の絞り込み後）。</summary>
    public ObservableCollection<VolumeInfo> FormatVolumes { get; } = new();
    public ObservableCollection<SmartAttribute> SmartAttributes { get; } = new();
    public ObservableCollection<OpticalDriveInfo> OpticalDrives { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public event EventHandler LogAppended;
    public event EventHandler DiskMapChanged;

    // ---------------- 選択 ----------------

    private PhysicalDiskInfo _selectedDisk;
    public PhysicalDiskInfo SelectedDisk
    {
        get => _selectedDisk;
        set
        {
            if (!Set(ref _selectedDisk, value)) return;
            RebuildDiskDetails();
            OnPropertyChanged(nameof(HasSelectedDisk));
            OnPropertyChanged(nameof(DiskSummaryText));
        }
    }

    public bool HasSelectedDisk => _selectedDisk != null;

    private PartitionInfo _selectedPartition;
    public PartitionInfo SelectedPartition
    {
        get => _selectedPartition;
        set => Set(ref _selectedPartition, value);
    }

    private VolumeInfo _selectedVolume;
    public VolumeInfo SelectedVolume
    {
        get => _selectedVolume;
        set => Set(ref _selectedVolume, value);
    }

    private VolumeInfo _selectedFormatVolume;
    public VolumeInfo SelectedFormatVolume
    {
        get => _selectedFormatVolume;
        set
        {
            if (!Set(ref _selectedFormatVolume, value)) return;
            if (value != null)
            {
                FormatLabel = value.Label;
                if (!string.IsNullOrEmpty(value.FileSystem)) FormatFileSystem = value.FileSystem;
            }
            OnPropertyChanged(nameof(FormatTargetWarning));
        }
    }

    private OpticalDriveInfo _selectedOptical;
    public OpticalDriveInfo SelectedOptical
    {
        get => _selectedOptical;
        set => Set(ref _selectedOptical, value);
    }

    // ---------------- 状態 ----------------

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle)); }
    }
    public bool IsIdle => !_isBusy;

    private string _status = "準備完了";
    public string StatusText { get => _status; set => Set(ref _status, value); }

    private double _progress;
    public double ProgressValue { get => _progress; set => Set(ref _progress, value); }

    private bool _progressIndeterminate;
    public bool ProgressIndeterminate { get => _progressIndeterminate; set => Set(ref _progressIndeterminate, value); }

    // ---------------- S.M.A.R.T. 表示 ----------------

    private string _smartSummary = "未取得";
    public string SmartSummary { get => _smartSummary; set => Set(ref _smartSummary, value); }

    private string _smartDetail = "ディスクを選んで「健康状態を取得」を押してください。";
    public string SmartDetail { get => _smartDetail; set => Set(ref _smartDetail, value); }

    private string _smartVitals = "";
    public string SmartVitals { get => _smartVitals; set => Set(ref _smartVitals, value); }

    // ---------------- パーティション作成の入力 ----------------

    private bool _useMaxSize = true;
    public bool UseMaxSize { get => _useMaxSize; set => Set(ref _useMaxSize, value); }

    private string _newPartitionSizeMb = "10240";
    public string NewPartitionSizeMb { get => _newPartitionSizeMb; set => Set(ref _newPartitionSizeMb, value); }

    private string _newPartitionLetter = "(自動)";
    public string NewPartitionLetter { get => _newPartitionLetter; set => Set(ref _newPartitionLetter, value); }

    public List<string> LetterChoices { get; } = BuildLetterChoices();

    private bool _initializeAsGpt = true;
    public bool InitializeAsGpt { get => _initializeAsGpt; set => Set(ref _initializeAsGpt, value); }

    private bool _zeroOutOnClear;
    public bool ZeroOutOnClear { get => _zeroOutOnClear; set => Set(ref _zeroOutOnClear, value); }

    public string DiskSummaryText
    {
        get
        {
            var d = SelectedDisk;
            if (d == null) return "ディスクが選択されていません。";
            return $"{d.ModelText}\nシリアル {(!string.IsNullOrWhiteSpace(d.SerialNumber) ? d.SerialNumber : "不明")}"
                 + $" · ファーム {(!string.IsNullOrWhiteSpace(d.FirmwareRevision) ? d.FirmwareRevision : "不明")}"
                 + $"\n{d.SizeText} · {d.PartitionStyle} · {d.BusType}"
                 + (d.IsOffline ? " · オフライン" : "")
                 + (d.IsReadOnly ? " · 読み取り専用" : "")
                 + (d.ContainsSystemVolume ? "\n⚠ このディスクには Windows のシステムボリュームが含まれます。" : "");
        }
    }

    // ---------------- フォーマットの入力 ----------------

    public List<string> FileSystemChoices { get; } = new() { "NTFS", "exFAT", "FAT32", "FAT", "ReFS" };
    public List<string> AllocationUnitChoices { get; } = new() { "既定", "512", "1024", "2048", "4096", "8192", "16384", "32768", "65536" };
    public List<string> PassChoices { get; } = new() { "1", "2", "3", "5", "7" };

    private string _formatFileSystem = "NTFS";
    public string FormatFileSystem { get => _formatFileSystem; set => Set(ref _formatFileSystem, value); }

    private string _formatLabel = "";
    public string FormatLabel { get => _formatLabel; set => Set(ref _formatLabel, value); }

    private string _allocationUnit = "既定";
    public string AllocationUnit { get => _allocationUnit; set => Set(ref _allocationUnit, value); }

    private int _formatModeIndex;      // 0=簡易 1=通常 2=ディープ
    public int FormatModeIndex
    {
        get => _formatModeIndex;
        set { if (Set(ref _formatModeIndex, value)) OnPropertyChanged(nameof(FormatModeDescription)); }
    }

    private string _securePasses = "3";
    public string SecurePasses
    {
        get => _securePasses;
        set { if (Set(ref _securePasses, value)) OnPropertyChanged(nameof(FormatModeDescription)); }
    }

    private bool _enableCompression;
    public bool EnableCompression { get => _enableCompression; set => Set(ref _enableCompression, value); }

    private bool _formatUsbOnly = true;
    /// <summary>USB メモリと USB 接続のポータブル SSD / HDD だけを一覧に出す（旧 FORMATApp の動作）。</summary>
    public bool FormatUsbOnly
    {
        get => _formatUsbOnly;
        set { if (Set(ref _formatUsbOnly, value)) RebuildFormatVolumes(); }
    }

    private bool _formatting;

    public string FormatModeDescription => FormatModeIndex switch
    {
        0 => "ファイル管理情報だけを作り直します。数秒で終わりますが、データは復元ソフトで読み出せる状態のまま残ります。",
        1 => "全セクタにゼロを書き込みながら不良セクタを検査します。容量に応じて数十分〜数時間かかります。",
        2 => $"ゼロ・1・乱数を {SecurePasses} 回書き込み、復元を困難にします。譲渡や廃棄の前に使ってください。"
             + "SSD では代替ブロックまでは消去されないため、可能ならメーカーの Secure Erase を併用してください。",
        _ => ""
    };

    public string FormatTargetWarning
    {
        get
        {
            var v = SelectedFormatVolume;
            if (v == null) return "";
            if (v.IsSystemVolume || v.IsBootVolume) return "⚠ システムドライブのためフォーマットできません。";
            return $"⚠ {v.DriveLetter} の中身（{v.UsedText} 使用中）はすべて失われます。";
        }
    }

    // ---------------- 光学ドライブ ----------------

    private string _opticalSummary = "ドライブを選んで「メディアを確認」を押してください。";
    public string OpticalSummary { get => _opticalSummary; set => Set(ref _opticalSummary, value); }

    private string _retryCount = "3";
    public string RetryCount { get => _retryCount; set => Set(ref _retryCount, value); }

    // ---------------- diskpart ----------------

    private string _diskPartScript = "list disk\nlist volume";
    public string DiskPartScript { get => _diskPartScript; set => Set(ref _diskPartScript, value); }

    // ---------------- コマンド ----------------

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand LoadSmartCommand { get; }
    public AsyncRelayCommand CreatePartitionCommand { get; }
    public AsyncRelayCommand DeletePartitionCommand { get; }
    public AsyncRelayCommand InitializeDiskCommand { get; }
    public AsyncRelayCommand ClearDiskCommand { get; }
    public AsyncRelayCommand ToggleOnlineCommand { get; }
    public AsyncRelayCommand AssignLetterCommand { get; }
    public AsyncRelayCommand FormatCommand { get; }
    public AsyncRelayCommand FormatCheckedCommand { get; }
    public AsyncRelayCommand WipeFreeSpaceCommand { get; }
    public RelayCommand SetLabelCommand { get; }
    public AsyncRelayCommand SafeRemoveCommand { get; }
    public AsyncRelayCommand RescanCommand { get; }
    public AsyncRelayCommand RunDiskPartCommand { get; }
    public AsyncRelayCommand ClearReadOnlyCommand { get; }
    public AsyncRelayCommand OpticalRefreshCommand { get; }
    public AsyncRelayCommand OpticalSummaryCommand { get; }
    public AsyncRelayCommand OpticalVerifyCommand { get; }
    public AsyncRelayCommand OpticalIsoCommand { get; }
    public AsyncRelayCommand OpticalCopyCommand { get; }
    public RelayCommand OpticalEjectCommand { get; }
    public RelayCommand OpticalLoadCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand SaveLogCommand { get; }

    // ---------------- ログ ----------------

    public void Log(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(new Action(() => Append(line)));
        else
            Append(line);
    }

    private void Append(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > 3000) LogLines.RemoveAt(0);
        LogAppended?.Invoke(this, EventArgs.Empty);
    }

    private void SaveLog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "ログを保存",
            Filter = "テキストファイル (*.txt)|*.txt",
            FileName = "ディスク管理ログ_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllLines(dlg.FileName, LogLines, Encoding.UTF8);
            Log("ログを保存しました: " + dlg.FileName);
        }
        catch (Exception ex)
        {
            Log("ログを保存できませんでした: " + ex.Message);
        }
    }

    private void Cancel()
    {
        if (_formatting && MessageBox.Show(
                "フォーマットを途中で止めると、そのドライブは一時的に使えない状態（RAW）になります。\n"
              + "もう一度簡易フォーマットすれば使えるようになります。\n\n中止しますか？",
                "中止の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try { _cts?.Cancel(); } catch { }
        StatusText = "中止を要求しました…";
        Log("処理の中止を要求しました。");
    }

    private void SetProgress(OpticalService.ScanProgress p)
    {
        var dispatcher = Application.Current?.Dispatcher;
        Action update = () =>
        {
            ProgressIndeterminate = false;
            ProgressValue = p.Ratio * 100;
            StatusText = $"{Fmt.Bytes(p.BytesDone)} / {Fmt.Bytes(p.BytesTotal)}"
                       + (p.BadSectors > 0 ? $"  読み取り不能 {p.BadSectors:N0} セクタ" : "");
        };
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(update);
        else update();
    }

    private async Task RunBusyAsync(string status, Func<CancellationToken, Task> work, bool indeterminate = true)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = status;
        ProgressIndeterminate = indeterminate;
        ProgressValue = 0;
        _cts = new CancellationTokenSource();
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("処理を中止しました。");
        }
        catch (Exception ex)
        {
            Log("エラー: " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            ProgressIndeterminate = false;
            ProgressValue = 0;
            StatusText = "準備完了";
        }
    }

    // ---------------- 一覧の更新 ----------------

    public async Task RefreshAsync()
    {
        await RunBusyAsync("ディスクを検出しています…", ct => ReloadAsync(ct));
        await RefreshOpticalAsync();
    }

    /// <summary>実際の再スキャン。処理中の操作からも呼べるよう、進捗表示の外側に置いています。</summary>
    private async Task ReloadAsync(CancellationToken ct)
    {
        int previous = SelectedDisk?.Index ?? -1;
        var disks = await Task.Run(() => DiskService.EnumerateDisks(Log), ct);
        var volumes = await Task.Run(() => DiskService.EnumerateVolumes(), ct);

        Disks.Clear();
        foreach (var d in disks) Disks.Add(d);

        DiskService.MarkUsbVolumes(disks, volumes);

        AllVolumes.Clear();
        foreach (var v in volumes) AllVolumes.Add(v);
        RebuildFormatVolumes();

        SelectedDisk = Disks.FirstOrDefault(x => x.Index == previous) ?? Disks.FirstOrDefault();
        Log($"{Disks.Count} 台の物理ディスクと {AllVolumes.Count} 個のボリュームを検出しました"
          + $"（うち USB 接続 {AllVolumes.Count(v => v.IsUsb)} 個）。");
    }

    private void RebuildFormatVolumes()
    {
        var previous = SelectedFormatVolume?.DriveLetter;
        FormatVolumes.Clear();
        foreach (var v in AllVolumes)
        {
            if (v.IsProtected) continue;               // C: とシステム・起動ボリュームは出さない
            if (FormatUsbOnly && !v.IsUsb) continue;
            FormatVolumes.Add(v);
        }
        SelectedFormatVolume = FormatVolumes.FirstOrDefault(v => v.DriveLetter == previous) ?? FormatVolumes.FirstOrDefault();
    }

    private static void OnUi(Action a)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(a);
        else a();
    }

    private void RebuildDiskDetails()
    {
        Partitions.Clear();
        Volumes.Clear();
        SmartAttributes.Clear();
        SmartSummary = SelectedDisk?.HealthStatus ?? "未取得";
        SmartDetail = "「健康状態を取得」を押すと S.M.A.R.T. を読み取ります。";
        SmartVitals = "";

        if (SelectedDisk != null)
        {
            foreach (var p in SelectedDisk.Partitions) Partitions.Add(p);
            foreach (var v in SelectedDisk.Volumes) Volumes.Add(v);
            SelectedVolume = Volumes.FirstOrDefault();
            SelectedPartition = Partitions.FirstOrDefault(p => !p.IsUnallocated);
        }
        DiskMapChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- 健康状態 ----------------

    private async Task LoadSmartAsync()
    {
        var disk = SelectedDisk;
        if (disk == null) return;

        await RunBusyAsync("S.M.A.R.T. を読み取っています…", async ct =>
        {
            var result = await Task.Run(() => SmartService.Read(disk, Log), ct);

            SmartAttributes.Clear();
            foreach (var a in result.Attributes.OrderBy(x => x.Id)) SmartAttributes.Add(a);

            SmartSummary = result.Summary;
            SmartDetail = result.Detail;
            disk.HealthStatus = result.Summary;
            disk.HealthDetail = result.Detail;

            var vitals = new List<string>();
            if (result.TemperatureCelsius.HasValue) vitals.Add($"温度 {result.TemperatureCelsius.Value} ℃");
            if (result.PowerOnHours.HasValue) vitals.Add($"通電 {result.PowerOnHours.Value:N0} 時間");
            if (result.PowerCycleCount.HasValue) vitals.Add($"電源投入 {result.PowerCycleCount.Value:N0} 回");
            vitals.Add($"代替処理済 {result.ReallocatedSectors:N0}");
            vitals.Add($"保留中 {result.PendingSectors:N0}");
            vitals.Add($"回復不可 {result.UncorrectableSectors:N0}");
            SmartVitals = string.Join("   ·   ", vitals);

            Log($"ディスク {disk.Index} の健康状態: {result.Summary} — {result.Detail}");
        });
    }

    // ---------------- パーティション ----------------

    private async Task CreatePartitionAsync()
    {
        var disk = SelectedDisk;
        if (disk == null) return;

        if (disk.ContainsSystemVolume && !ConfirmDialog.Ask(
                "システムディスクへの操作",
                $"ディスク {disk.Index} には Windows のシステムボリュームが含まれます。\n"
              + "続行すると起動できなくなる可能性があります。",
                $"ディスク{disk.Index}"))
            return;

        ulong size = 0;
        if (!UseMaxSize)
        {
            if (!double.TryParse(NewPartitionSizeMb, out double mb) || mb <= 0)
            {
                Log("サイズの指定が正しくありません。MB 単位の数値を入力してください。");
                return;
            }
            size = (ulong)(mb * 1024 * 1024);
        }

        char letter = '\0';
        if (NewPartitionLetter != null && NewPartitionLetter.Length >= 1 && NewPartitionLetter[0] >= 'A' && NewPartitionLetter[0] <= 'Z')
            letter = NewPartitionLetter[0];

        await RunBusyAsync("パーティションを作成しています…", async ct =>
        {
            var res = await Task.Run(() =>
                StorageService.CreatePartition(disk.Index, size, UseMaxSize, letter, letter == '\0'), ct);
            Log(res.Message);

            if (res.Success)
            {
                if (!string.IsNullOrEmpty(res.CreatedDriveLetter))
                    Log($"作成したパーティションは未フォーマットです。「フォーマット」タブで {res.CreatedDriveLetter} をフォーマットしてください。");
                await ReloadAsync(ct);
            }
        });
    }

    private async Task DeletePartitionAsync()
    {
        var disk = SelectedDisk;
        var part = SelectedPartition;
        if (disk == null || part == null || part.IsUnallocated) return;

        if (part.IsSystem || part.IsBoot)
        {
            Log("システム／起動パーティションは削除できません。");
            return;
        }

        string what = $"ディスク {disk.Index} のパーティション {part.PartitionNumber}"
                    + (string.IsNullOrEmpty(part.DriveLetter) ? "" : $"（{part.DriveLetter}）");

        if (!ConfirmDialog.Ask(
                "パーティションの削除",
                $"{what} · {part.SizeText} を削除します。\n中のデータはすべて失われ、元に戻せません。",
                "削除"))
            return;

        await RunBusyAsync("パーティションを削除しています…", async ct =>
        {
            var res = await Task.Run(() => StorageService.DeletePartition(disk.Index, part.PartitionNumber), ct);
            Log(res.Message);
            if (res.Success) await ReloadAsync(ct);
        });
    }

    private async Task InitializeDiskAsync()
    {
        var disk = SelectedDisk;
        if (disk == null) return;

        if (!ConfirmDialog.Ask(
                "ディスクの初期化",
                $"ディスク {disk.Index}（{disk.ModelText} · {disk.SizeText}）を "
              + $"{(InitializeAsGpt ? "GPT" : "MBR")} で初期化します。",
                "初期化"))
            return;

        await RunBusyAsync("ディスクを初期化しています…", async ct =>
        {
            var res = await Task.Run(() => StorageService.InitializeDisk(disk.Index, InitializeAsGpt), ct);
            Log(res.Message);
            if (res.Success) await ReloadAsync(ct);
        });
    }

    private async Task ClearDiskAsync()
    {
        var disk = SelectedDisk;
        if (disk == null) return;

        if (disk.ContainsSystemVolume)
        {
            Log("システムボリュームを含むディスクは消去できません。");
            return;
        }

        string detail = ZeroOutOnClear
            ? $"ディスク {disk.Index}（{disk.SizeText}）の全域にゼロを書き込みます。\n"
            + "容量によっては数時間かかり、途中で止めることはできません。"
            : $"ディスク {disk.Index}（{disk.SizeText}）のパーティションをすべて削除します。";

        if (!ConfirmDialog.Ask("ディスクの消去", detail + "\nデータは元に戻せません。", $"ディスク{disk.Index}"))
            return;

        await RunBusyAsync("ディスクを消去しています…", async ct =>
        {
            var res = await Task.Run(() => StorageService.ClearDisk(disk.Index, ZeroOutOnClear), ct);
            Log(res.Message);
            if (res.Success) await ReloadAsync(ct);
        });
    }

    private async Task ToggleOnlineAsync()
    {
        var disk = SelectedDisk;
        if (disk == null) return;
        bool goOnline = disk.IsOffline;

        await RunBusyAsync("ディスクの状態を変更しています…", async ct =>
        {
            var res = await Task.Run(() => StorageService.SetOnline(disk.Index, goOnline), ct);
            Log(res.Message);
            if (res.Success) await ReloadAsync(ct);
        });
    }

    private async Task AssignLetterAsync()
    {
        var disk = SelectedDisk;
        var part = SelectedPartition;
        if (disk == null || part == null) return;

        char letter = '\0';
        if (NewPartitionLetter != null && NewPartitionLetter.Length >= 1 && NewPartitionLetter[0] >= 'A' && NewPartitionLetter[0] <= 'Z')
            letter = NewPartitionLetter[0];

        await RunBusyAsync("ドライブ文字を割り当てています…", async ct =>
        {
            var res = await Task.Run(() => StorageService.AssignDriveLetter(disk.Index, part.PartitionNumber, letter), ct);
            Log(res.Message);
            if (res.Success) await ReloadAsync(ct);
        });
    }

    private async Task ClearReadOnlyAsync()
    {
        var disk = SelectedDisk;
        if (disk == null) return;
        await RunBusyAsync("読み取り専用属性を解除しています…", async ct =>
        {
            await DiskPartService.ClearReadOnlyAsync(disk.Index, Log, ct);
            await ReloadAsync(ct);
        });
    }

    // ---------------- フォーマット ----------------

    private async Task FormatAsync()
    {
        var vol = SelectedFormatVolume;
        if (vol == null) return;

        if (vol.IsProtected)
        {
            Log("システムドライブ（C: を含む）はフォーマットできません。");
            return;
        }

        string modeName = CurrentModeName();

        if (!ConfirmDialog.Ask(
                modeName,
                $"{vol.DriveLetter} {(string.IsNullOrEmpty(vol.Label) ? "" : "「" + vol.Label + "」")} "
              + $"（{vol.CapacityText}、うち {vol.UsedText} 使用中）を {FormatFileSystem} でフォーマットします。\n"
              + "中のデータはすべて失われます。",
                vol.DriveLetter.TrimEnd(':')))
            return;

        var options = BuildFormatOptions(vol.DriveLetter);

        _formatting = true;
        try
        {
            await RunBusyAsync(modeName + "を実行しています…", async ct =>
            {
                bool ok = await FormatService.FormatAsync(options, Log, ct, p => OnUi(() =>
                {
                    ProgressIndeterminate = false;
                    ProgressValue = p;
                    StatusText = $"{modeName}を実行しています… {vol.DriveLetter} {p} %";
                }));
                if (ok) await ReloadAsync(CancellationToken.None);
            });
        }
        finally { _formatting = false; }
    }

    private FormatService.FormatMode CurrentMode() => FormatModeIndex switch
    {
        1 => FormatService.FormatMode.Full,
        2 => FormatService.FormatMode.Secure,
        _ => FormatService.FormatMode.Quick
    };

    private string CurrentModeName() => CurrentMode() switch
    {
        FormatService.FormatMode.Full => "通常フォーマット",
        FormatService.FormatMode.Secure => $"ディープフォーマット（{SecurePasses} 回上書き）",
        _ => "簡易フォーマット"
    };

    private FormatService.FormatOptions BuildFormatOptions(string driveLetter)
    {
        int alloc = 0;
        if (AllocationUnit != "既定") int.TryParse(AllocationUnit, out alloc);
        if (!int.TryParse(SecurePasses, out int passes)) passes = 3;

        return new FormatService.FormatOptions
        {
            DriveLetter = driveLetter,
            FileSystem = FormatFileSystem,
            Label = FormatLabel,
            Mode = CurrentMode(),
            SecurePasses = passes,
            AllocationUnitBytes = alloc,
            EnableCompression = EnableCompression
        };
    }

    /// <summary>
    /// チェックした複数のドライブを同時にフォーマットします（旧 DriveFormatterApp の並列フォーマット）。
    /// 形式・ラベル・種類は画面の設定を全ドライブに使います。
    /// </summary>
    private async Task FormatCheckedAsync()
    {
        var targets = FormatVolumes.Where(v => v.IsChecked && v.CanFormat).ToList();
        if (targets.Count == 0)
        {
            Log("まとめてフォーマットするドライブにチェックを入れてください。");
            return;
        }

        string modeName = CurrentModeName();
        string list = string.Join("\n", targets.Select(v =>
            $"  {v.Display}（{v.CapacityText}、うち {v.UsedText} 使用中）"));

        if (!ConfirmDialog.Ask(
                $"{targets.Count} 台をまとめて{modeName}",
                $"次のドライブを同時に {FormatFileSystem} でフォーマットします。中のデータはすべて失われます。\n{list}",
                "フォーマット"))
            return;

        foreach (var v in targets) { v.FormatStatus = "待機中"; v.FormatPercent = 0; }

        _formatting = true;
        try
        {
            await RunBusyAsync($"{targets.Count} 台を{modeName}しています…", async ct =>
            {
                void UpdateOverall() => OnUi(() =>
                {
                    ProgressIndeterminate = false;
                    ProgressValue = targets.Average(t => t.FormatPercent);
                });

                var tasks = targets.Select(async v =>
                {
                    var options = BuildFormatOptions(v.DriveLetter);
                    OnUi(() => v.FormatStatus = "フォーマット中…");
                    bool ok;
                    try
                    {
                        ok = await FormatService.FormatAsync(options, line => Log($"[{v.DriveLetter}] {line}"), ct,
                            p => OnUi(() => { v.FormatPercent = p; v.FormatStatus = $"{p} %"; UpdateOverall(); }));
                    }
                    catch (Exception ex)
                    {
                        Log($"[{v.DriveLetter}] エラー: {ex.Message}");
                        ok = false;
                    }
                    OnUi(() => v.FormatStatus = ok ? "完了" : (ct.IsCancellationRequested ? "中止" : "失敗"));
                    return ok;
                }).ToList();

                var results = await Task.WhenAll(tasks);
                Log($"まとめてフォーマットが終わりました: 成功 {results.Count(r => r)} 台 / 全 {results.Length} 台");

                // 一覧を読み直しても結果が見えるよう、ドライブ文字ごとに状態を引き継ぐ
                var status = targets.ToDictionary(t => t.DriveLetter, t => t.FormatStatus, StringComparer.OrdinalIgnoreCase);
                await ReloadAsync(CancellationToken.None);
                foreach (var v in FormatVolumes)
                    if (status.TryGetValue(v.DriveLetter, out var s)) { v.FormatStatus = s; v.FormatPercent = s == "完了" ? 100 : 0; }
            }, indeterminate: false);
        }
        finally { _formatting = false; }
    }

    private async Task WipeFreeSpaceAsync()
    {
        var vol = SelectedFormatVolume;
        if (vol == null) return;

        if (!ConfirmDialog.Ask(
                "空き領域の上書き",
                $"{vol.DriveLetter} の空き領域（{vol.FreeText}）を上書きし、削除済みデータを復元できなくします。\n"
              + "既存のファイルはそのまま残ります。容量によっては数時間かかります。",
                "実行"))
            return;

        await RunBusyAsync("空き領域を上書きしています…", async ct =>
        {
            await FormatService.WipeFreeSpaceAsync(vol.DriveLetter, Log, ct);
        });
    }

    private void SetLabel()
    {
        var vol = SelectedFormatVolume;
        if (vol == null) return;
        if (FormatService.SetLabel(vol.DriveLetter, FormatLabel, Log))
            _ = RefreshAsync();
    }

    // ---------------- 取り外し・再認識 ----------------

    private async Task SafeRemoveAsync()
    {
        var vol = SelectedVolume;
        if (vol == null) return;

        if (vol.IsSystemVolume || vol.IsBootVolume)
        {
            Log("システムドライブは取り外せません。");
            return;
        }

        await RunBusyAsync("ドライブを取り外しています…", async ct =>
        {
            bool isOptical = vol.DriveTypeCode == 5;
            await Task.Run(() => DeviceService.SafelyRemove(vol.DriveLetter, isOptical, Log), ct);
            await ReloadAsync(ct);
        });
    }

    private async Task RescanAsync()
    {
        await RunBusyAsync("ハードウェアを再認識しています…", async ct =>
        {
            DeviceService.RescanDevices(Log);
            await DiskPartService.RescanAsync(Log, ct);
            await Task.Delay(800, ct);
            await ReloadAsync(ct);
            await RefreshOpticalAsync();
        });
    }

    private async Task RunDiskPartAsync()
    {
        string script = DiskPartScript;
        if (string.IsNullOrWhiteSpace(script)) return;

        bool destructive = new[] { "clean", "delete", "format", "convert", "shrink", "extend" }
            .Any(k => script.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

        if (destructive && !ConfirmDialog.Ask(
                "diskpart スクリプトの実行",
                "スクリプトにデータを破壊するコマンドが含まれています。\n内容をもう一度確認してください。",
                "実行"))
            return;

        await RunBusyAsync("diskpart を実行しています…", async ct =>
        {
            await DiskPartService.RunScriptAsync(script, Log, ct);
            await ReloadAsync(ct);
        });
    }

    // ---------------- 光学ドライブ ----------------

    private async Task RefreshOpticalAsync()
    {
        var drives = await Task.Run(() => OpticalService.EnumerateDrives());
        var previous = SelectedOptical?.DriveLetter;
        OpticalDrives.Clear();
        foreach (var d in drives) OpticalDrives.Add(d);
        SelectedOptical = OpticalDrives.FirstOrDefault(x => x.DriveLetter == previous) ?? OpticalDrives.FirstOrDefault();
    }

    private async Task OpticalSummaryAsync()
    {
        var drive = SelectedOptical;
        if (drive == null) return;

        await RunBusyAsync("メディアを確認しています…", async ct =>
        {
            OpticalService.RefreshMedia(drive);
            if (!drive.MediaLoaded)
            {
                OpticalSummary = "メディアが入っていません。ディスクを入れてからもう一度お試しください。";
                return;
            }
            OpticalSummary = await Task.Run(() => OpticalService.Summarize(drive.DriveLetter, Log), ct);
            Log($"{drive.DriveLetter} のメディアを確認しました。");
        });
    }

    private async Task OpticalVerifyAsync()
    {
        var drive = SelectedOptical;
        if (drive == null) return;
        int retries = 3;
        int.TryParse(RetryCount, out retries);

        await RunBusyAsync("メディアを検査しています…", async ct =>
        {
            var result = await OpticalService.ReadDiscAsync(
                drive.DriveLetter, null, retries, Log, SetProgress, ct);

            if (result.Cancelled) return;

            if (result.BadSectors == 0)
                OpticalSummary = $"検査結果: 良好\n{result.TotalSectors:N0} セクタすべてを読み取れました。";
            else
                OpticalSummary = $"検査結果: 劣化あり\n"
                    + $"読み取り不能: {result.BadSectors:N0} / {result.TotalSectors:N0} セクタ"
                    + $"（{result.BadRatio * 100:0.000} %）\n"
                    + "読める間に ISO イメージとして吸い出しておくことをおすすめします。";
        }, indeterminate: false);
    }

    private async Task OpticalIsoAsync()
    {
        var drive = SelectedOptical;
        if (drive == null) return;

        OpticalService.RefreshMedia(drive);
        if (!drive.MediaLoaded)
        {
            Log("メディアが入っていません。");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "ISO イメージの保存先",
            Filter = "ディスクイメージ (*.iso)|*.iso",
            FileName = (string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "disc" : SanitizeFileName(drive.VolumeLabel))
                     + "_" + DateTime.Now.ToString("yyyyMMdd") + ".iso"
        };
        if (dlg.ShowDialog() != true) return;

        int retries = 3;
        int.TryParse(RetryCount, out retries);

        await RunBusyAsync("ISO イメージを作成しています…", async ct =>
        {
            var result = await OpticalService.ReadDiscAsync(
                drive.DriveLetter, dlg.FileName, retries, Log, SetProgress, ct);

            if (!result.Cancelled)
                OpticalSummary = result.BadSectors == 0
                    ? "ISO イメージを完全な状態で作成しました。"
                    : $"ISO イメージを作成しましたが、{result.BadSectors:N0} セクタは読み取れずゼロで埋めました。"
                    + "一部のファイルは破損している可能性があります。";
        }, indeterminate: false);
    }

    private async Task OpticalCopyAsync()
    {
        var drive = SelectedOptical;
        if (drive == null) return;

        OpticalService.RefreshMedia(drive);
        if (!drive.MediaLoaded)
        {
            Log("メディアが入っていません。");
            return;
        }

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "バックアップ先のフォルダーを選ぶ"
        };
        if (dlg.ShowDialog() != true) return;

        string dest = Path.Combine(dlg.FolderName,
            (string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "disc" : SanitizeFileName(drive.VolumeLabel))
            + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm"));

        await RunBusyAsync("ファイルをコピーしています…", async ct =>
        {
            var result = await OpticalService.CopyFilesAsync(drive.DriveLetter, dest, Log, SetProgress, ct);
            if (!result.Cancelled)
                OpticalSummary = $"コピー先: {dest}\n"
                    + $"成功 {result.FilesCopied:N0} 件 / 失敗 {result.FilesFailed:N0} 件 / {Fmt.Bytes(result.BytesCopied)}";
        }, indeterminate: false);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    private static List<string> BuildLetterChoices()
    {
        var list = new List<string> { "(自動)" };
        for (char c = 'D'; c <= 'Z'; c++) list.Add(c.ToString());
        return list;
    }
}
