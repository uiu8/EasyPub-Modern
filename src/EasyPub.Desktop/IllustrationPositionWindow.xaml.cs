using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class IllustrationPositionWindow : Window
{
    private readonly IReadOnlyList<TextSourceLine> _lines;

    public IllustrationPositionWindow(ChapterEditingDocument document, int? currentLine)
    {
        InitializeComponent();
        _lines = document.GetLines();
        LinesGrid.ItemsSource = _lines;
        ChapterCombo.ItemsSource = document.Candidates
            .Select(candidate => new ChapterJumpItem(
                candidate.LineNumber,
                $"第 {candidate.LineNumber} 行 · {candidate.OriginalTitle}"))
            .ToArray();
        Loaded += (_, _) => SelectAndReveal(
            currentLine is >= 1 && currentLine <= document.LineCount
                ? currentLine.Value
                : _lines.FirstOrDefault(line => line.Text.Trim().Length > 0)?.LineNumber ?? 1);
    }

    public int? SelectedLineNumber { get; private set; }

    private void ChapterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChapterCombo.SelectedItem is ChapterJumpItem chapter)
            SelectAndReveal(chapter.LineNumber);
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();

    private void SearchText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        FindNext();
        e.Handled = true;
    }

    private void FindNext()
    {
        var query = SearchText.Text.Trim();
        if (query.Length == 0)
        {
            SearchText.Focus();
            return;
        }

        var start = LinesGrid.SelectedItem is TextSourceLine selected ? selected.LineNumber : 0;
        var match = _lines.FirstOrDefault(line =>
                        line.LineNumber > start && line.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    ?? _lines.FirstOrDefault(line =>
                        line.LineNumber <= start && line.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        if (match is null)
        {
            MessageBox.Show(this, $"没有找到：{query}", "查找正文", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectAndReveal(match.LineNumber);
    }

    private void LinesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LinesGrid.SelectedItem is not TextSourceLine line)
        {
            SelectedPositionText.Text = "请选择一行";
            return;
        }
        var content = line.Text.Trim();
        if (content.Length > 90) content = content[..90] + "…";
        SelectedPositionText.Text = $"插图将放在第 {line.LineNumber} 行之后：{(content.Length == 0 ? "（空行）" : content)}";
    }

    private void LinesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LinesGrid.SelectedItem is TextSourceLine) ConfirmSelection();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void ConfirmSelection()
    {
        if (LinesGrid.SelectedItem is not TextSourceLine line)
        {
            MessageBox.Show(this, "请先选择一行。", "EasyPub Modern");
            return;
        }
        SelectedLineNumber = line.LineNumber;
        DialogResult = true;
    }

    private void UseManualMarker_Click(object sender, RoutedEventArgs e)
    {
        SelectedLineNumber = null;
        DialogResult = true;
    }

    private void SelectAndReveal(int lineNumber)
    {
        var line = _lines.FirstOrDefault(candidate => candidate.LineNumber == lineNumber);
        if (line is null) return;
        LinesGrid.SelectedItem = line;
        LinesGrid.ScrollIntoView(line);
        LinesGrid.Focus();
    }

    private sealed record ChapterJumpItem(int LineNumber, string Label);
}
