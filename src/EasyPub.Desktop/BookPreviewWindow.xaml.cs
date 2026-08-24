using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class BookPreviewWindow : Window
{
    private readonly BookPreviewPackage _package;
    private readonly KindlePreviewerLauncher _kindlePreviewer = new();
    private KindlePreviewerInstallation? _kindlePreviewerInstallation;

    public BookPreviewWindow(string bookName, BookPreviewPackage package)
    {
        InitializeComponent();
        _package = package;
        BookNameText.Text = $"《{bookName}》 · {package.Items.Count(item => item.IsChapter)} 个章节";
        DataContext = package;
        Closed += (_, _) => _package.Dispose();
        ItemsList.SelectedIndex = 0;
        _kindlePreviewerInstallation = _kindlePreviewer.Discover();
        KindlePreviewButton.ToolTip = _kindlePreviewerInstallation is null
            ? "未检测到官方 Kindle Previewer；点击后可打开官方下载页或选择其他预览器"
            : $"使用官方 Kindle Previewer\n{_kindlePreviewerInstallation.ExecutablePath}";
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is not BookPreviewItem item || !File.Exists(item.HtmlPath)) return;
        PreviewBrowser.Navigate(new Uri(item.HtmlPath, UriKind.Absolute));
    }

    private void KindlePreviewButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _kindlePreviewerInstallation ??= _kindlePreviewer.Discover();
            if (_kindlePreviewerInstallation is not null)
            {
                _kindlePreviewer.Launch(_package.EpubPath, BookNameText.Text, _kindlePreviewerInstallation);
                KindlePreviewButton.Content = "已打开 Kindle Previewer";
                return;
            }

            var choice = MessageBox.Show(
                this,
                "没有检测到官方 Kindle Previewer。\n\n“是”：打开 Amazon 官方下载页\n“否”：选择其他电子书预览器（只提供普通 EPUB 预览）",
                "需要 Kindle Previewer",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Information);
            if (choice == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(KindlePreviewerLauncher.OfficialDownloadPage)
                {
                    UseShellExecute = true,
                });
                return;
            }
            if (choice != MessageBoxResult.No) return;

            var dialog = new OpenFileDialog
            {
                Title = "选择其他电子书预览器",
                Filter = "Windows 程序 (*.exe)|*.exe",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) != true) return;
            _kindlePreviewer.LaunchWithOtherViewer(_package.EpubPath, BookNameText.Text, dialog.FileName);
            KindlePreviewButton.Content = "已打开其他预览器";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开 Kindle 预览", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
