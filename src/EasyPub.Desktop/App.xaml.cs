using System.Configuration;
using System.Data;
using System.Windows;

namespace EasyPub.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) ThemeManager.ApplyWindowChrome(window);
    }
}

