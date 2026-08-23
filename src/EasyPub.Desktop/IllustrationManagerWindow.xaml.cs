using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class IllustrationManagerWindow : Window
{
    private readonly string _inputPath;
    private readonly string? _chapterPattern;
    private readonly TextEncodingMode _encodingMode;
    private ChapterEditingDocument? _document;

    public IllustrationManagerWindow(
        string bookName,
        string inputPath,
        string? chapterPattern,
        TextEncodingMode encodingMode,
        IEnumerable<BookIllustration> illustrations,
        string? selectedMarker = null)
    {
        InitializeComponent();
        _inputPath = Path.GetFullPath(inputPath);
        _chapterPattern = chapterPattern;
        _encodingMode = encodingMode;
        BookNameText.Text = $"当前小说：《{bookName}》";
        foreach (var illustration in illustrations)
            Items.Add(new IllustrationEditorItem(
                illustration.Marker,
                illustration.ImagePath,
                illustration.AltText,
                illustration.InsertAfterLine));
        DataContext = this;
        Items.CollectionChanged += (_, _) => UpdateCount();
        UpdateCount();
        if (!string.IsNullOrWhiteSpace(selectedMarker))
        {
            IllustrationsGrid.SelectedItem = Items.FirstOrDefault(item =>
                string.Equals(item.Marker, selectedMarker, StringComparison.OrdinalIgnoreCase));
            if (IllustrationsGrid.SelectedItem is not null)
                IllustrationsGrid.ScrollIntoView(IllustrationsGrid.SelectedItem);
        }
    }

    public ObservableCollection<IllustrationEditorItem> Items { get; } = [];
    public IReadOnlyList<BookIllustration> Result { get; private set; } = [];

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "为当前小说添加正文插图",
            Filter = "支持的图片 (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg|PNG (*.png)|*.png|WebP (*.webp)|*.webp",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        foreach (var path in dialog.FileNames)
        {
            var baseMarker = Path.GetFileNameWithoutExtension(path).Trim();
            if (baseMarker.Length == 0) baseMarker = "插图";
            var marker = baseMarker;
            var suffix = 2;
            while (Items.Any(item => string.Equals(item.Marker.Trim(), marker, StringComparison.OrdinalIgnoreCase)))
                marker = baseMarker + suffix++;
            Items.Add(new IllustrationEditorItem(marker, Path.GetFullPath(path), null, null));
        }
        IllustrationsGrid.SelectedItem = Items.LastOrDefault();
        IllustrationsGrid.ScrollIntoView(IllustrationsGrid.SelectedItem);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in IllustrationsGrid.SelectedItems.Cast<IllustrationEditorItem>().ToArray())
            Items.Remove(item);
    }

    private async void ChoosePosition_Click(object sender, RoutedEventArgs e)
    {
        if (IllustrationsGrid.SelectedItem is not IllustrationEditorItem item)
        {
            MessageBox.Show(this, "请先选中一张插图。", "EasyPub Modern");
            return;
        }

        try
        {
            CountText.Text = "正在读取 TXT 正文…";
            _document ??= await Task.Run(() =>
                ChapterEditingDocument.LoadAsync(_inputPath, _chapterPattern, _encodingMode));
            var chooser = new IllustrationPositionWindow(_document, item.InsertAfterLine) { Owner = this };
            if (chooser.ShowDialog() == true)
            {
                item.InsertAfterLine = chooser.SelectedLineNumber;
                CountText.Text = item.InsertAfterLine is int line
                    ? $"“{item.Marker}”将插入在第 {line} 行之后"
                    : $"“{item.Marker}”已改用手动正文标记";
            }
            else
            {
                UpdateCount();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法读取 TXT 正文", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateCount();
        }
    }

    private void UseManualMarker_Click(object sender, RoutedEventArgs e)
    {
        if (IllustrationsGrid.SelectedItem is not IllustrationEditorItem item)
        {
            MessageBox.Show(this, "请先选中一张插图。", "EasyPub Modern");
            return;
        }
        item.InsertAfterLine = null;
        CountText.Text = $"“{item.Marker}”已改用手动正文标记：{item.MarkerToken}";
    }

    private void CopyMarker_Click(object sender, RoutedEventArgs e)
    {
        if (IllustrationsGrid.SelectedItem is not IllustrationEditorItem item)
        {
            MessageBox.Show(this, "请先选中一张插图。", "EasyPub Modern");
            return;
        }
        Clipboard.SetText(item.MarkerToken);
        CountText.Text = $"已复制：{item.MarkerToken}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        IllustrationsGrid.CommitEdit();
        var duplicateCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.Marker))
            {
                MessageBox.Show(this, "插图标记不能为空。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!duplicateCheck.Add(item.Marker.Trim()))
            {
                MessageBox.Show(this, $"插图标记重复：{item.Marker.Trim()}", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!File.Exists(item.ImagePath))
            {
                MessageBox.Show(this, $"找不到插图文件：\n{item.ImagePath}", "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        Result = Items.Select(item => new BookIllustration(
            item.Marker.Trim(),
            item.ImagePath,
            string.IsNullOrWhiteSpace(item.AltText) ? null : item.AltText.Trim(),
            item.InsertAfterLine)).ToArray();
        DialogResult = true;
    }

    private void UpdateCount() => CountText.Text = $"共 {Items.Count} 张正文插图";
}

public sealed class IllustrationEditorItem : INotifyPropertyChanged
{
    private string _marker;
    private string? _altText;
    private int? _insertAfterLine;

    public IllustrationEditorItem(string marker, string imagePath, string? altText, int? insertAfterLine)
    {
        _marker = marker;
        ImagePath = Path.GetFullPath(imagePath);
        _altText = altText;
        _insertAfterLine = insertAfterLine;
    }

    public string Marker
    {
        get => _marker;
        set
        {
            if (_marker == value) return;
            _marker = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MarkerToken));
        }
    }

    public string? AltText
    {
        get => _altText;
        set
        {
            if (_altText == value) return;
            _altText = value;
            OnPropertyChanged();
        }
    }

    public string ImagePath { get; }
    public int? InsertAfterLine
    {
        get => _insertAfterLine;
        set
        {
            if (_insertAfterLine == value) return;
            _insertAfterLine = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PositionLabel));
        }
    }

    public string PositionLabel => InsertAfterLine is int line ? $"第 {line} 行之后" : "手动标记";
    public string MarkerToken => $"[[插图:{Marker.Trim()}]]";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
