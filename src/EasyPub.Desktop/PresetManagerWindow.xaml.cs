using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class PresetManagerWindow : Window
{
    private readonly ObservableCollection<NamedConversionPreset> _presets;
    private readonly ConversionProfile _currentProfile;

    public PresetManagerWindow(ObservableCollection<NamedConversionPreset> presets, ConversionProfile currentProfile)
    {
        InitializeComponent();
        _presets = presets;
        _currentProfile = currentProfile;
        PresetList.ItemsSource = presets;
    }

    public bool Changed { get; private set; }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetList.SelectedItem is NamedConversionPreset preset) NameText.Text = preset.Name;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameText.Text.Trim();
        if (name.Length == 0) { InkDialog.Show(this, "请输入转换方案名称。", "EasyPub Modern"); return; }
        var existing = _presets.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) _presets.Remove(existing);
        var preset = new NamedConversionPreset(name, _currentProfile);
        _presets.Add(preset);
        PresetList.SelectedItem = preset;
        Changed = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not NamedConversionPreset preset) return;
        _presets.Remove(preset);
        NameText.Clear();
        Changed = true;
    }

    private void Done_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
