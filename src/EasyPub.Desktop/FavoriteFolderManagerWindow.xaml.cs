using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class FavoriteFolderManagerWindow : Window
{
    private readonly FavoriteFolderStore _store = FavoriteFolderStore.CreateDefault();

    public FavoriteFolderManagerWindow(IEnumerable<string> folders)
    {
        InitializeComponent();
        Folders = new ObservableCollection<string>(folders);
        DataContext = this;
    }

    public ObservableCollection<string> Folders { get; }
    public IReadOnlyList<string> Result => Folders.ToArray();

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要收藏的小说文件夹",
            InitialDirectory = Folders.FirstOrDefault(Directory.Exists) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        var folders = await _store.AddAsync(dialog.FolderName);
        Replace(folders, dialog.FolderName);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (FoldersList.SelectedItem is not string selected) return;
        var folders = await _store.RemoveAsync(selected);
        Replace(folders);
    }

    private void FoldersList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RemoveButton.IsEnabled = FoldersList.SelectedItem is string;
    private void Done_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Replace(IReadOnlyList<string> folders, string? select = null)
    {
        Folders.Clear();
        foreach (var folder in folders) Folders.Add(folder);
        if (select is not null) FoldersList.SelectedItem = Folders.FirstOrDefault(folder => string.Equals(folder, select, StringComparison.OrdinalIgnoreCase));
    }
}
