using System.IO;
using System.Windows;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// **具名样式与画刷必须真的解析得出来。**
///
/// <para>`Controls.xaml` 是合并进 `App.xaml` 的独立字典。这里有两种**不说话**的坏法：</para>
/// <list type="bullet">
/// <item>`BasedOn="{StaticResource 某样式}"` 键名写错 —— 走 StaticResource，会抛；</item>
/// <item>`{DynamicResource 某画刷}` 键名写错 —— **不抛**，只留一个 null，
/// 界面照常打开、那一块没有颜色，谁也不会发现。</item>
/// </list>
///
/// <para>第二种正是这一整轮在修的那类失败：坏了但不说话。所以分成两条查 ——
/// 样式走"真的把字典载进来"，画刷走"逐个点名核对 App.xaml 里有没有声明"。</para>
///
/// <para>⚠️ 不能靠 <see cref="WorkbenchHarness.OnStaThread"/>：它建的是
/// <c>new Application()</c>，**不载 App.xaml**，所以那里查什么都是 null
/// （现有窗口测试能过，是因为 DynamicResource 缺键本来就不抛）。</para>
/// </summary>
public class ControlStyleContractTests
{
    /// <summary>用户会直接看到的那些样式。加样式时**顺手加到这里** —— 不查就等于没验证。</summary>
    private static readonly string[] StyleKeys =
    [
        "ScreenTitle", "CardHeading", "ActionHeading", "BodyText", "RowTitle", "MetaText",
        "BigNumber", "TallyNumber", "TallyNumberWarning", "RowActionText",
        "GhostButton", "ToolButton",
        "Chip", "ChipText",
        "Badge", "BadgeText", "BadgeWarning", "BadgeWarningText",
        "Pane", "PaneHeader", "RowSeparator", "ActionCard",
        "TallyCard", "TallyCardWarning", "SourcePreview",
    ];

    /// <summary>这批样式用到的画刷键。少一个，那块界面就是无色而不是报错。</summary>
    private static readonly string[] BrushKeys =
    [
        "AppBackgroundBrush", "SurfaceBrush", "RaisedSurfaceBrush", "SubtleSurfaceBrush",
        "SelectedSurfaceBrush", "PrimaryTextBrush", "SecondaryTextBrush", "DisabledTextBrush",
        "BorderBrush", "StrongBorderBrush", "PrimaryActionBrush", "PrimaryActionTextBrush",
        "SuccessBrush", "WarningBrush", "ErrorBrush", "WarningSurfaceBrush", "InfoSurfaceBrush",
        "WarningBorderBrush", "HairlineBrush",
    ];

    private static string WorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "EasyPub.Desktop", "App.xaml")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根（src/EasyPub.Desktop/App.xaml）");
    }

    /// <summary>
    /// 真的把 `Controls.xaml` 载成一个字典。`BasedOn` 链在载入时解析，
    /// 所以键名写错会在这里抛出来。
    /// </summary>
    [Fact]
    public void Controls_dictionary_loads_and_every_named_style_resolves()
    {
        var thread = new Thread(() =>
        {
            // pack:// 这个 scheme 要等 WPF 初始化才注册，否则 new Uri(...) 会报
            // "Invalid port specified"（实测踩到）。所以先建 Application。
            if (Application.Current is null) _ = new Application();
            var dictionary = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/EasyPub.Desktop;component/Controls.xaml",
                    UriKind.Absolute),
            };
            var missing = StyleKeys.Where(key => !dictionary.Contains(key)).ToArray();
            Assert.True(missing.Length == 0, "解析不出来的样式：" + string.Join("、", missing));
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    /// <summary>
    /// 逐个点名核对画刷。走文本核对而不是资源查找，是因为画刷声明在 `App.xaml` 里 ——
    /// 那是一个 `Application` 而不是字典，没法像上面那样单独载入。
    /// </summary>
    [Fact]
    public void Every_brush_the_styles_reference_is_declared()
    {
        var appXaml = File.ReadAllText(
            Path.Combine(WorkspaceRoot(), "src", "EasyPub.Desktop", "App.xaml"));

        var missing = BrushKeys.Where(key => !appXaml.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal))
            .ToArray();

        Assert.True(missing.Length == 0, "App.xaml 里没有声明的画刷：" + string.Join("、", missing));
    }

    /// <summary>`App.xaml` 必须真的把 `Controls.xaml` 合进来 —— 否则上面全部白查。</summary>
    [Fact]
    public void App_merges_the_controls_dictionary()
    {
        var appXaml = File.ReadAllText(
            Path.Combine(WorkspaceRoot(), "src", "EasyPub.Desktop", "App.xaml"));

        Assert.Contains("MergedDictionaries", appXaml, StringComparison.Ordinal);
        Assert.Contains("Source=\"Controls.xaml\"", appXaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// **决定性的一条**：真的建一个 `App`（载 App.xaml），查 `Chip` 在不在。
    ///
    /// <para>上面那条只证明"文本里有这句话"，这条证明**合并真的生效**。两者的区别很实际：
    /// `<c>Source="Controls.xaml"</c>` 是相对 URI，文本对不代表运行时找得到。
    /// 实测时工作台在测试里报「无法找到名为 Chip 的资源」，而**同一个文件**里第 9 行的
    /// `<c>BasedOn="{StaticResource {x:Type Button}}"</c>` 却能解析 ——
    /// 说明那个宿主**有** App.xaml 的资源，缺的正是被合并进来的这一份。</para>
    /// </summary>
    [Fact]
    public void Loading_the_real_app_makes_the_new_styles_findable()
    {
        string[] missing = [];
        var thread = new Thread(() =>
        {
            if (Application.Current is null)
            {
                var app = new App();
                app.InitializeComponent();
            }
            missing = StyleKeys.Where(key => Application.Current?.TryFindResource(key) is null).ToArray();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(missing.Length == 0,
            "载入 App 之后仍然解析不出来的样式：" + string.Join("、", missing));
    }

    /// <summary>
    /// **只有一个实心主按钮样式。** 这条锁的是按钮规划的第 1 条：
    /// 每屏一个 PrimaryButton，它 = 这一屏此刻该做的事。
    /// 再冒出一个"看起来也是主按钮"的样式，就等于把推荐取消了。
    /// </summary>
    [Fact]
    public void There_is_exactly_one_primary_button_style()
    {
        var appXaml = File.ReadAllText(
            Path.Combine(WorkspaceRoot(), "src", "EasyPub.Desktop", "App.xaml"));
        var controlsXaml = File.ReadAllText(
            Path.Combine(WorkspaceRoot(), "src", "EasyPub.Desktop", "Controls.xaml"));
        var all = appXaml + controlsXaml;

        foreach (var suspicious in new[] { "PrimaryActionButton", "AccentButton", "CtaButton", "MainButton" })
            Assert.DoesNotContain($"x:Key=\"{suspicious}\"", all, StringComparison.Ordinal);

        Assert.Contains("x:Key=\"PrimaryButton\"", appXaml, StringComparison.Ordinal);
    }
}
