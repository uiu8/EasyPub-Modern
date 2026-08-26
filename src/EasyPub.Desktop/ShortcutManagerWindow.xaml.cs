using System.Collections.ObjectModel;
using System.Windows;

namespace EasyPub.Desktop;

public partial class ShortcutManagerWindow : Window
{
    private readonly IReadOnlyDictionary<string, string> _original;
    public ObservableCollection<ShortcutRow> Rows { get; } = [];

    public ShortcutManagerWindow(IReadOnlyDictionary<string, string> bindings)
    {
        InitializeComponent();
        _original = bindings;
        foreach (var item in ShortcutCatalog.All) Rows.Add(new ShortcutRow(item.Id, item.Name, item.Category, ShortcutCatalog.Resolve(bindings, item.Id)));
        ShortcutGrid.ItemsSource = Rows;
    }

    public IReadOnlyDictionary<string, string> Bindings { get; private set; } = new Dictionary<string, string>();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows) row.Gesture = ShortcutCatalog.All.First(item => item.Id == row.Id).DefaultGesture;
        ShortcutGrid.Items.Refresh();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var invalid = Rows.FirstOrDefault(row => !ShortcutCatalog.TryParse(row.Gesture, out _, out _));
        if (invalid is not null) { InkDialog.Show(this, $"“{invalid.Name}”的快捷键无效：{invalid.Gesture}", "快捷键管理"); return; }
        var conflict = Rows.GroupBy(row => row.Gesture.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (conflict is not null) { InkDialog.Show(this, $"快捷键 {conflict.Key} 被多个操作占用。", "快捷键管理"); return; }
        Bindings = Rows.ToDictionary(row => row.Id, row => row.Gesture.Trim(), StringComparer.OrdinalIgnoreCase);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class ShortcutRow
{
    public ShortcutRow(string id, string name, string category, string gesture) => (Id, Name, Category, Gesture) = (id, name, category, gesture);
    public string Id { get; }
    public string Name { get; }
    public string Category { get; }
    public string Gesture { get; set; }
}
