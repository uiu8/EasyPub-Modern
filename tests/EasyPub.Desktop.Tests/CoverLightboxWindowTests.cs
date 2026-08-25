using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyPub.Desktop;

namespace EasyPub.Desktop.Tests;

public sealed class CoverLightboxWindowTests
{
    [Fact]
    public void Displays_book_name_cover_and_source_information()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            CoverLightboxWindow? window = null;
            try
            {
                var cover = new DrawingImage(new DrawingGroup());
                cover.Freeze();
                window = new CoverLightboxWindow("雨夜", cover, "cover.webp\n1200 × 1600 · WEBP → JPG")
                {
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Opacity = 0,
                };
                window.Show();
                window.UpdateLayout();

                Assert.Contains("雨夜", Assert.IsType<TextBlock>(window.FindName("BookNameText")).Text);
                Assert.Same(cover, Assert.IsType<Image>(window.FindName("CoverImage")).Source);
                Assert.Contains("WEBP", Assert.IsType<TextBlock>(window.FindName("CoverInfoText")).Text);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "封面大图窗口测试超时。");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
