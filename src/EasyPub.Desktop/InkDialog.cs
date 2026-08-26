using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EasyPub.Desktop;

internal static class InkDialog
{
    public static MessageBoxResult Show(Window owner, string message, string caption) =>
        Show(owner, message, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(
        Window owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        var dialog = new InkDialogWindow(message, caption, buttons, image) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Result;
    }
}

internal sealed class InkDialogWindow : Window
{
    public InkDialogWindow(string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        Title = string.IsNullOrWhiteSpace(caption) ? "EasyPub Modern" : caption;
        Width = 540;
        MinWidth = 420;
        MaxWidth = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Content = BuildContent(message, buttons, image);
    }

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

    private UIElement BuildContent(string message, MessageBoxButton buttons, MessageBoxImage image)
    {
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var contentCard = new Border
        {
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(12),
            Background = ResourceBrush("SurfaceBrush"),
            BorderBrush = ResourceBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
        };
        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var glyph = new TextBlock
        {
            Text = Glyph(image),
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 25,
            Foreground = ResourceBrush(GlyphBrush(image)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 23,
            MaxWidth = 610,
            Foreground = ResourceBrush("PrimaryTextBrush"),
        };
        Grid.SetColumn(text, 2);
        contentGrid.Children.Add(glyph);
        contentGrid.Children.Add(text);
        contentCard.Child = contentGrid;
        root.Children.Add(contentCard);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetRow(actions, 2);
        foreach (var (label, result, primary, cancel) in Actions(buttons))
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = primary,
                IsCancel = cancel,
            };
            if (primary)
            {
                button.Background = ResourceBrush("PrimaryActionBrush");
                button.Foreground = ResourceBrush("PrimaryActionTextBrush");
                button.BorderBrush = ResourceBrush("PrimaryActionBrush");
            }
            button.Click += (_, _) =>
            {
                Result = result;
                DialogResult = true;
            };
            actions.Children.Add(button);
        }
        root.Children.Add(actions);
        return root;
    }

    private static IReadOnlyList<(string Label, MessageBoxResult Result, bool Primary, bool Cancel)> Actions(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OKCancel => [("取消", MessageBoxResult.Cancel, false, true), ("确定", MessageBoxResult.OK, true, false)],
        MessageBoxButton.YesNo => [("否", MessageBoxResult.No, false, true), ("是", MessageBoxResult.Yes, true, false)],
        MessageBoxButton.YesNoCancel => [("取消", MessageBoxResult.Cancel, false, true), ("否", MessageBoxResult.No, false, false), ("是", MessageBoxResult.Yes, true, false)],
        _ => [("确定", MessageBoxResult.OK, true, true)],
    };

    private static string Glyph(MessageBoxImage image) => image switch
    {
        MessageBoxImage.Error => "\uE783",
        MessageBoxImage.Warning => "\uE7BA",
        MessageBoxImage.Question => "\uE897",
        _ => "\uE946",
    };

    private static string GlyphBrush(MessageBoxImage image) => image switch
    {
        MessageBoxImage.Error => "ErrorBrush",
        MessageBoxImage.Warning => "WarningBrush",
        _ => "PrimaryTextBrush",
    };

    private static Brush ResourceBrush(string key) =>
        (Brush)(Application.Current?.Resources[key] ?? Brushes.Transparent);
}
