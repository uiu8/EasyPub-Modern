using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>
/// Asks which restore the reader wants, before anything is written.
///
/// <para>The two answers are different products rather than two ways of doing one thing. Restoring the text
/// always works; restoring the chapter tree that was saved with those bytes needs one to have been saved.
/// This dialog says what each one costs — the arrangements that will be gone — and disables the second when
/// the version has no tree, instead of offering it and refusing afterwards.</para>
/// </summary>
internal sealed class SourceRestoreWindow : Window
{
    private readonly RadioButton _sourceOnly = new()
    {
        Content = "只恢复原文（章节重新识别）",
        Margin = new Thickness(0, 0, 0, 6),
        IsChecked = true,
    };

    private readonly RadioButton _fullState = new()
    {
        Content = "恢复原文与当时的章节树",
        Margin = new Thickness(0, 0, 0, 6),
    };

    public SourceRestoreWindow(string sourceName, SourceBackupEntry entry, bool hasSavedTree)
    {
        Title = "恢复这个版本";
        Width = 660; Height = 420; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Theme resources only exist in a real application; a window built without one still has to work,
        // because that is how the tests drive it.
        if (Application.Current is not null)
        {
            SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
            SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        }

        var panel = new DockPanel { Margin = new Thickness(20) };

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = $"把「{sourceName}」恢复到 {entry.SavedAtLabel} 的版本。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });
        header.Children.Add(new TextBlock
        {
            Text = $"这份备份：{entry.KindLabel} · {entry.SizeLabel} · 内容校验值 {entry.ShortHash}"
                + (hasSavedTree ? " · 含章节树" : " · 不含章节树"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(4) };
        cancel.Click += (_, _) => DialogResult = false;
        var confirm = new Button { Content = "开始恢复", IsDefault = true, Margin = new Thickness(4) };
        confirm.Click += (_, _) => DialogResult = true;
        footer.Children.Add(cancel);
        footer.Children.Add(confirm);
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);

        var options = new StackPanel();
        options.Children.Add(new TextBlock { Text = "恢复到什么程度", Margin = new Thickness(0, 0, 0, 8) });
        options.Children.Add(_sourceOnly);
        options.Children.Add(new TextBlock
        {
            Text = "  章节按恢复后的原文重新识别。手工改过的标题、层级、目录勾选都会没有。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        options.Children.Add(_fullState);
        options.Children.Add(new TextBlock
        {
            Text = hasSavedTree
                ? "  连当时保存的章节树一起还原，包括当时的编排。"
                : "  这个版本没有保存章节树，所以只能选上面那一项。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        if (!hasSavedTree) _fullState.IsEnabled = false;
        options.Children.Add(new TextBlock
        {
            Text = "当前原文会先自动备份一份，所以这次恢复本身还能再恢复回去。"
                + "恢复过程中如果程序中断，下次打开会自动收尾。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(options);

        Content = panel;
    }

    /// <summary>What the reader chose. Only meaningful when the dialog returned true.</summary>
    public RestoreTargetPolicy Policy => _fullState.IsChecked == true
        ? RestoreTargetPolicy.FullBookState
        : RestoreTargetPolicy.SourceOnly;

    /// <summary>For a caller — a test, in practice — that makes the choice without a mouse.</summary>
    internal void Choose(RestoreTargetPolicy policy) =>
        _fullState.IsChecked = policy == RestoreTargetPolicy.FullBookState;
}
