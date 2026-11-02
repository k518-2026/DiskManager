using System;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using DiskManager.ViewModels;

namespace DiskManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.LogAppended += OnLogAppended;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PrivilegeText.Text = IsAdministrator() ? "管理者として実行中" : "管理者権限がありません";

        if (!IsAdministrator())
        {
            _vm.Log("管理者として実行していないため、多くの操作が失敗します。アプリを右クリックして「管理者として実行」で起動し直してください。");
        }

        _vm.Log("ディスクを検出しています…");
        await _vm.RefreshAsync();
    }

    private void OnLogAppended(object sender, EventArgs e)
    {
        if (LogList.Items.Count == 0) return;
        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
