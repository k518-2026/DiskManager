using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DiskManager.Views;

/// <summary>
/// 取り消せない操作の前に、対象名を手で入力させる確認ダイアログ。
/// 間違ったドライブを選んだままの実行を防ぎます。
/// </summary>
public partial class ConfirmDialog : Window
{
    private string _required;

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>requiredWord を入力させて確認します。OK なら true。</summary>
    public static bool Ask(string title, string message, string requiredWord)
    {
        var dialog = new ConfirmDialog
        {
            _required = requiredWord ?? "実行",
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.PromptText.Text = $"続けるには「{dialog._required}」と入力してください。";
        return dialog.ShowDialog() == true;
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        OkButton.IsEnabled = string.Equals(Input.Text.Trim(), _required, StringComparison.OrdinalIgnoreCase);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
