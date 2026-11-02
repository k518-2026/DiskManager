using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskManager.Models;
using DiskManager.ViewModels;

namespace DiskManager.Views;

public partial class OverviewView : UserControl
{
    private MainViewModel _vm;

    public OverviewView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.DiskMapChanged -= OnMapChanged;
        _vm = DataContext as MainViewModel;
        if (_vm != null) _vm.DiskMapChanged += OnMapChanged;
        BuildMap();
    }

    private void OnMapChanged(object sender, EventArgs e) => BuildMap();

    /// <summary>パーティションを容量に比例した帯として描きます。</summary>
    private void BuildMap()
    {
        MapGrid.Children.Clear();
        MapGrid.ColumnDefinitions.Clear();
        MapLegend.Text = "";

        var disk = _vm?.SelectedDisk;
        if (disk == null || disk.Partitions.Count == 0)
        {
            MapLegend.Text = disk == null
                ? "ディスクが選択されていません。"
                : "パーティションがありません。「パーティション」タブで初期化と作成ができます。";
            return;
        }

        var colors = new[]
        {
            Color.FromRgb(0x35, 0xC1, 0xA6),
            Color.FromRgb(0x4E, 0x9E, 0xD8),
            Color.FromRgb(0x9B, 0x8C, 0xE0),
            Color.FromRgb(0xE0, 0x8C, 0xB4),
            Color.FromRgb(0x7F, 0xC3, 0x6B)
        };

        var legend = new StringBuilder();
        int colorIndex = 0;

        for (int i = 0; i < disk.Partitions.Count; i++)
        {
            var p = disk.Partitions[i];
            MapGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(1, (double)p.Size), GridUnitType.Star),
                MinWidth = 6
            });

            Brush fill;
            Brush stroke;
            if (p.IsUnallocated)
            {
                fill = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x34));
                stroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x46, 0x54));
            }
            else if (p.IsSystem || p.IsBoot)
            {
                fill = new SolidColorBrush(Color.FromArgb(0x44, 0xE8, 0xA3, 0x3D));
                stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
            }
            else
            {
                var c = colors[colorIndex++ % colors.Length];
                fill = new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B));
                stroke = new SolidColorBrush(c);
            }

            var text = new StackPanel { Margin = new Thickness(8, 6, 6, 6) };
            text.Children.Add(new TextBlock
            {
                Text = p.IsUnallocated ? "未割り当て" :
                       (string.IsNullOrEmpty(p.DriveLetter) ? "パーティション " + p.PartitionNumber : p.DriveLetter),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEC, 0xF2)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            text.Children.Add(new TextBlock
            {
                Text = p.SizeText,
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x9B, 0xAA)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            text.Children.Add(new TextBlock
            {
                Text = p.TypeText,
                FontSize = 10,
                Margin = new Thickness(0, 1, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x9B, 0xAA)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var border = new Border
            {
                Background = fill,
                BorderBrush = stroke,
                BorderThickness = new Thickness(0, 3, 0, 0),
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                CornerRadius = new CornerRadius(2),
                Child = text,
                ToolTip = $"{p.Display}\n開始位置 {p.OffsetText}\n{p.TypeText}"
            };

            Grid.SetColumn(border, i);
            MapGrid.Children.Add(border);

            if (!p.IsUnallocated)
                legend.Append($"[{p.PartitionNumber}] {(string.IsNullOrEmpty(p.DriveLetter) ? "文字なし" : p.DriveLetter)} {p.SizeText} {p.TypeText}   ");
        }

        legend.Append($"\n合計 {disk.SizeText}");
        MapLegend.Text = legend.ToString();
    }
}
