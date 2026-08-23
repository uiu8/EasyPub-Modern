using System.IO;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class BookPreviewWindow : Window
{
    private readonly BookPreviewPackage _package;

    public BookPreviewWindow(string bookName, BookPreviewPackage package)
    {
        InitializeComponent();
        _package = package;
        BookNameText.Text = $"《{bookName}》 · {package.Items.Count(item => item.IsChapter)} 个章节";
        DataContext = package;
        Closed += (_, _) => _package.Dispose();
        ItemsList.SelectedIndex = 0;
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is not BookPreviewItem item || !File.Exists(item.HtmlPath)) return;
        PreviewBrowser.Navigate(new Uri(item.HtmlPath, UriKind.Absolute));
    }
}
