using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class ChapterEditorWindow : Window
{
    private readonly ChapterEditingDocument _document;

    public ChapterEditorWindow(ChapterEditingDocument document)
    {
        InitializeComponent();
        _document = document;
        Rows = new ObservableCollection<ChapterEditorRow>(
            document.Candidates.Select(candidate => new ChapterEditorRow(candidate)));
        DataContext = this;

        SourceText.Text = document.SourcePath;
        SourceText.ToolTip = document.SourcePath;
        var recognized = Rows.Count(row => row.Kind == ChapterCandidateKind.Recognized);
        var numeric = Rows.Count - recognized;
        SummaryText.Text = $"已识别 {recognized} 章 · 待规范化 {numeric} 章";
        if (Rows.Count > 0) CandidatesGrid.SelectedIndex = 0;
    }

    public ObservableCollection<ChapterEditorRow> Rows { get; }

    public string? SavedPath { get; private set; }

    private void CandidatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidatesGrid.SelectedItem is ChapterEditorRow row)
            PreviewText.Text = _document.GetPreview(row.LineNumber, 6);
        else
            PreviewText.Clear();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var allLines = _document.CreateAllSuggestedEdits()
            .Select(edit => edit.LineNumber)
            .ToHashSet();
        foreach (var row in Rows.Where(row => allLines.Contains(row.LineNumber)))
            row.IsApplied = true;
    }

    private void NormalizeAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows.Where(row => row.Kind == ChapterCandidateKind.NumericTitle))
        {
            row.TargetTitle = row.SuggestedTitle;
            row.IsApplied = true;
        }
        CandidatesGrid.Items.Refresh();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows) row.IsApplied = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        CandidatesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CandidatesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var dialog = new SaveFileDialog
        {
            Title = "另存章节编辑结果",
            Filter = "文本文件 (*.txt)|*.txt",
            AddExtension = true,
            DefaultExt = ".txt",
            InitialDirectory = Path.GetDirectoryName(_document.SourcePath),
            FileName = Path.GetFileNameWithoutExtension(_document.SourcePath) + ".章节已编辑.txt",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var edits = Rows
                .Where(row => row.IsApplied)
                .Select(row => new ChapterTitleEdit(row.LineNumber, row.TargetTitle))
                .ToArray();
            await _document.SaveAsAsync(dialog.FileName, edits);
            SavedPath = Path.GetFullPath(dialog.FileName);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法保存章节编辑结果", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public sealed class ChapterEditorRow : INotifyPropertyChanged
{
    private bool _isApplied;
    private string _targetTitle;
    private bool _initialized;

    public ChapterEditorRow(ChapterCandidate candidate)
    {
        LineNumber = candidate.LineNumber;
        OriginalTitle = candidate.OriginalTitle;
        SuggestedTitle = candidate.SuggestedTitle;
        Kind = candidate.Kind;
        _targetTitle = candidate.SuggestedTitle;
        _initialized = true;
    }

    public int LineNumber { get; }

    public string OriginalTitle { get; }

    public string SuggestedTitle { get; }

    public ChapterCandidateKind Kind { get; }

    public string KindLabel => Kind == ChapterCandidateKind.NumericTitle ? "数字标题" : "已识别";

    public bool IsApplied
    {
        get => _isApplied;
        set => SetField(ref _isApplied, value);
    }

    public string TargetTitle
    {
        get => _targetTitle;
        set
        {
            if (!SetField(ref _targetTitle, value)) return;
            if (_initialized) IsApplied = true;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
