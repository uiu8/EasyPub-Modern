using System.Windows;
using System.Windows.Media;

namespace EasyPub.Desktop;

public partial class CoverLightboxWindow : Window
{
    public CoverLightboxWindow(string bookName, ImageSource cover, string? coverInfo)
    {
        InitializeComponent();
        BookNameText.Text = $"《{bookName}》";
        CoverImage.Source = cover;
        CoverInfoText.Text = coverInfo ?? string.Empty;
    }
}
