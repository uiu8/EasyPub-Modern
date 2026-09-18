using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 启动就写一条：这样日志文件**立刻存在**，而不是等用户点了第一个按钮才出现 ——
        // "日志在哪"本身不该是个需要猜的问题。它同时记录了版本，方便事后对齐行为。
        InteractionLog.Outcome("启动", new
        {
            版本 = typeof(App).Assembly.GetName().Version?.ToString(),
            日志 = InteractionLog.Path,
        });
        // **一个钩子覆盖全部按钮**，而不是在每个点击处理器里加一行。
        //
        // 理由：这个软件的按钮有一大半是运行时建出来的（专项处理菜单、目录面板、
        // 修复确认窗左栏），逐个插桩既容易漏又会被后续改动悄悄绕过。类级别的事件处理器
        // 挂在类型上，动态生成的按钮同样命中。
        //
        // 记的是**按钮自己说的那句话**（Content），因为事后复盘要回答的正是
        // "他按的是哪一个" —— 而用户看到的也只有那句话。
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent,
            new RoutedEventHandler((sender, _) => InteractionLog.User(
                sender is FrameworkElement { Name.Length: > 0 } named ? named.Name : sender.GetType().Name,
                new { text = Label(sender), window = WindowOf(sender) })));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.ClickEvent,
            new RoutedEventHandler((sender, _) => InteractionLog.User(
                "MenuItem", new { text = sender is HeaderedItemsControl { Header: string header } ? header : "", window = WindowOf(sender) })));
    }

    /// <summary>按钮上写的那句话：直接写在 Content 上的，或者里面那个 TextBlock 的。</summary>
    private static string Label(object? sender) => sender switch
    {
        ContentControl { Content: string text } => text,
        ContentControl { Content: System.Windows.Media.Visual visual } => FirstText(visual),
        HeaderedItemsControl { Header: string header } => header,
        _ => "",
    };

    private static string FirstText(System.Windows.Media.Visual visual)
    {
        if (visual is System.Windows.Controls.TextBlock block) return block.Text;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(visual);
        for (var i = 0; i < count; i++)
        {
            var found = FirstText((System.Windows.Media.Visual)System.Windows.Media.VisualTreeHelper.GetChild(visual, i));
            if (!string.IsNullOrWhiteSpace(found)) return found;
        }
        return "";
    }

    private static string WindowOf(object? sender) =>
        sender is DependencyObject node && Window.GetWindow(node) is { } window ? window.Title : "";

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) ThemeManager.ApplyWindowChrome(window);
    }
}
