using System.Windows.Input;

namespace EasyPub.Desktop;

public sealed record ShortcutDefinition(string Id, string Name, string Category, string DefaultGesture);

public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutDefinition> All { get; } =
    [
        new("add-files", "添加书稿", "书库", "Ctrl+O"),
        new("import-folder", "导入文件夹", "书库", "Ctrl+Shift+O"),
        new("select-all", "全选书稿", "书库", "Ctrl+A"),
        new("focus-search", "聚焦搜索", "书库", "Ctrl+F"),
        new("save-project", "保存项目", "项目", "Ctrl+S"),
        new("preflight", "检查问题", "转换", "Ctrl+Shift+P"),
        new("convert", "开始转换", "转换", "Ctrl+Enter"),
        new("pause", "暂停或继续转换", "转换", "Ctrl+Space"),
        new("settings", "打开设置", "全局", "Ctrl+,"),
        new("cycle-focus", "切换主要区域", "全局", "F6"),
    ];

    public static string Resolve(IReadOnlyDictionary<string, string> overrides, string id) =>
        overrides.TryGetValue(id, out var value) && TryParse(value, out _, out _) ? value : All.First(item => item.Id == id).DefaultGesture;

    public static bool Matches(IReadOnlyDictionary<string, string> overrides, string id, KeyEventArgs e)
    {
        if (!TryParse(Resolve(overrides, id), out var key, out var modifiers)) return false;
        return e.Key == key && Keyboard.Modifiers == modifiers;
    }

    public static bool TryParse(string gesture, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        try
        {
            var parsed = new KeyGestureConverter().ConvertFromInvariantString(gesture.Trim()) as KeyGesture;
            if (parsed is null) return false;
            key = parsed.Key;
            modifiers = parsed.Modifiers;
            return true;
        }
        catch (NotSupportedException) { return false; }
    }
}
