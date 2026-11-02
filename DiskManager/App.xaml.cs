using System;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DiskManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // format.exe / diskpart.exe の出力は OEM コードページ（日本語環境では CP932）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "処理中にエラーが発生しました。\n\n" + e.Exception.Message,
            "ディスク管理ツール", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
