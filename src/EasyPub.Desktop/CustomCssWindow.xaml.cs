using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class CustomCssWindow : Window
{
    public CustomCssWindow(string? css, string? sourcePath)
    {
        InitializeComponent();
        CssEditor.Text = css ?? string.Empty;
        SourcePath = sourcePath;
        UpdateSourceText();
        Loaded += (_, _) => CssEditor.Focus();
    }

    public string CssText => CssEditor.Text;
    public string? SourcePath { get; private set; }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入定制 CSS",
            Filter = "CSS 样式表 (*.css)|*.css|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            CssEditor.Text = await File.ReadAllTextAsync(dialog.FileName);
            SourcePath = dialog.FileName;
            UpdateSourceText();
        }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "无法导入 CSS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出定制 CSS 副本",
            Filter = "CSS 样式表 (*.css)|*.css",
            AddExtension = true,
            DefaultExt = ".css",
            FileName = "easypub-custom.css",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, CssEditor.Text);
            SourcePath = dialog.FileName;
            UpdateSourceText();
        }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "无法导出 CSS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        CssEditor.Clear();
        SourcePath = null;
        UpdateSourceText();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void UpdateSourceText() => SourceText.Text = SourcePath is null
        ? "直接编辑内容；应用后会保存到当前设置与预设"
        : $"来源：{SourcePath}";
}
