using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>Select one evidenced pair; reuse the existing compare/Mutate workflow.</summary>
internal sealed class RepeatedSequenceWindow : Window
{
    internal ListBox PairList { get; } = new();
    internal Button CompareButton { get; } = new() { Content = "对比所选两章…", IsDefault = true };
    internal ChapterReviewGroup? SelectedPair => (PairList.SelectedItem as PairChoice)?.Group;
    private sealed record PairChoice(string Label, ChapterReviewGroup Group);

    internal RepeatedSequenceWindow(IReadOnlyList<ChapterReviewGroup> pairs)
    {
        Title = "逐对核对重复区段";
        Width = 760; Height = 440; MinWidth = 580; MinHeight = 360;
        FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new());
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var intro = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        intro.Children.Add(new TextBlock { Text = $"本区段有 {pairs.Count} 对可核对章节", FontSize = 22, FontWeight = FontWeights.SemiBold });
        intro.Children.Add(new TextBlock { Text = "先选一对，再对比正文与前后位置。这里只选择核对对象，不删除章节；保留哪一份由你在下一步决定。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        layout.Children.Add(intro);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PairChoice.Label)));
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        label.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 9, 10, 9));
        PairList.ItemTemplate = new DataTemplate { VisualTree = label };
        PairList.ItemContainerStyle = new Style(typeof(ListBoxItem))
        {
            Setters = { new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) }
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(PairList, ScrollBarVisibility.Disabled);
        PairList.ItemsSource = pairs.Select((p, i) => new PairChoice($"{i + 1}. {p.Issue.Message}", p)).ToArray();
        PairList.SelectionChanged += (_, _) => CompareButton.IsEnabled = SelectedPair is not null;
        PairList.SelectedIndex = pairs.Count > 0 ? 0 : -1;
        CompareButton.IsEnabled = pairs.Count > 0;
        Grid.SetRow(PairList, 1); layout.Children.Add(PairList);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        footer.Children.Add(new Button { Content = "返回区段", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) });
        footer.Children.Add(CompareButton);
        CompareButton.SetResourceReference(BackgroundProperty, "PrimaryActionBrush");
        CompareButton.SetResourceReference(ForegroundProperty, "PrimaryActionTextBrush");
        CompareButton.Click += (_, _) => { if (SelectedPair is not null) DialogResult = true; };
        Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = layout;
    }

    internal static IReadOnlyList<ChapterReviewGroup> FindPairs(ChapterTreeDocument document,
        IReadOnlyList<ChapterTreeEntry> entries, ChapterReviewGroup sequence)
    {
        if (sequence.Issue.Code != "chapter_repeated_sequence") return [];
        var nodes = entries.ToDictionary(e => e.Id);
        var result = new List<ChapterReviewGroup>();
        // Sequence detector stores alternating A/B ids. Do not cross-join members:
        // three copies or overlapping sequences must not invent new pair assignments.
        for (var i = 0; i + 1 < sequence.NodeIds.Count; i += 2)
        {
            if (!nodes.TryGetValue(sequence.NodeIds[i], out var first)
                || !nodes.TryGetValue(sequence.NodeIds[i + 1], out var second)
                || ChapterContentDuplicates.Compare(document, first, second) is not { } match) continue;
            var description = match.Exact ? "正文相同（忽略空白）" : $"正文相似 {match.Similarity:P1}";
            var issue = sequence.Issue with { Code = "chapter_content_duplicate", LineNumber = second.TitleLineNumber,
                Message = $"{first.Title} · 第 {first.TitleLineNumber} / {second.TitleLineNumber} 行 · {description}" };
            result.Add(new(issue, sequence.Category, [first.Id, second.Id],
                new[] { first.TitleLineNumber, second.TitleLineNumber }.OfType<int>().ToArray(), [issue]));
        }
        return result;
    }
}

public partial class ChapterEditorWindow
{
    private void ReviewSequencePairs_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceChanged || ReviewGroup(CurrentReviewIssue()) is not { } sequence) return;
        var pairs = RepeatedSequenceWindow.FindPairs(_document, Flatten().Select(n => n.ToEntry()).ToArray(), sequence);
        if (pairs.Count == 0) { ShowReviewFeedback("本区段已没有可对比的章节对，请重新检查章节。"); return; }
        var picker = new RepeatedSequenceWindow(pairs) { Owner = this };
        if (picker.ShowDialog() == true && picker.SelectedPair is { } selected) CompareDuplicateContents(selected);
    }
}
