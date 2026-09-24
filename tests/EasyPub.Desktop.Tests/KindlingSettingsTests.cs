using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

public class KindlingSettingsTests
{
    [Fact]
    public void Engine_selection_applies_and_renders()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var app = new App(); app.InitializeComponent();
                var draft = ConversionSettingsDraft.CreateDefault(Path.GetTempPath());
                var view = new ConversionSettingsWindow(draft, new("", 1, 0, [], Path.GetTempPath()));
                window = new Window { Style = null, Content = view, Width = 1080, Height = 900, Left = -4000, Top = -4000,
                    ShowInTaskbar = false, Title = "EasyPub Modern · 制作设置" };
                window.Show();
                view.ShowCategory(2);
                var engine = (ComboBox)view.FindName("KindleEngineCombo");
                engine.SelectedIndex = 1;
                Assert.False(((ComboBox)view.FindName("CompressionCombo")).IsEnabled);
                Assert.Equal(Visibility.Visible, ((StackPanel)view.FindName("KindlingSettingsPanel")).Visibility);
                ConversionSettingsDraft? applied = null;
                view.Applied += d => applied = d;
                typeof(ConversionSettingsWindow).GetMethod("Apply_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(view, [view, new RoutedEventArgs()]);
                Assert.NotNull(applied);
                Assert.Equal(KindleConversionEngine.Kindling, applied.Profile.Options.Mobi.Engine);
                window.UpdateLayout();
                var path = Environment.GetEnvironmentVariable("EASYPUB_KINDLING_SCREENSHOT");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var bitmap = new RenderTargetBitmap(1080, 900, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(path); encoder.Save(file);
                }
                engine.SelectedIndex = 0;
                Assert.True(((ComboBox)view.FindName("CompressionCombo")).IsEnabled);
            }
            catch (Exception error) { failure = error; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
    }
}
