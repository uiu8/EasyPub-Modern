using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class ChapterEditorWindow
{
    private async void CatalogAssist_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceChanged) return;
        // Sources and folder rules are read once per open: an Apply must not touch the disk on every click.
        IReadOnlyList<string> preferred = [];
        try
        {
            ReferenceCatalogClient.Sources = await BookSourceStore.CreateDefault().LoadAsync();
            var rules = await MetadataMappingStore.CreateDefault().LoadAsync();
            preferred = MetadataMappingResolver.Match(_document.SourcePath, rules)?.Sources ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var dialog = ThemedWindow("目录辅助修复 · 获取、核对、补建", 1050, 760);
        var panel = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        var secondary = TryFindResource("SecondaryTextBrush") as System.Windows.Media.Brush;
        void Section(string title, string detail)
        {
            top.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 6) });
            top.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
            top.Children.Add(new TextBlock
            {
                Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Foreground = secondary, Margin = new Thickness(0, 2, 0, 6)
            });
        }
        top.Children.Add(new TextBlock { Text = "目录辅助修复", FontSize = 22, FontWeight = FontWeights.SemiBold });
        // 这里不能说"原始 TXT 不会被修改"：下面的「预览章节调整…」进入的是统一修复确认窗，
        // 而那个窗口支持「同时改原文」。是否写文件在下一步决定，这句话必须如实说。
        top.Children.Add(new TextBlock
        {
            Text = "这里只做目录匹配与预览，不会写任何文件。是否修改原始 TXT，在下一步的修复确认窗里决定。",
            TextWrapping = TextWrapping.Wrap
        });
        Section("第一步 · 选择参考目录", "有了参考目录，补建漏识别章节、统一标题、移除重复、建立卷层级才有依据。不上传 TXT。");
        top.Children.Add(new TextBlock { Text = "书名、书籍网址或番茄编号", Margin = new Thickness(0, 4, 0, 4) });
        var search = new WrapPanel(); top.Children.Add(search);
        var query = new TextBox { Text = ChapterAutoRepair.ExtractBookName(_document.SourcePath), Width = 360, ToolTip = "书名、完整书籍网址或番茄书籍编号" };
        var auto = new Button { Content = "一键自动获取目录" };
        var cancelNetwork = new Button { Content = "取消获取", IsEnabled = false };
        var sourceSettings = new Button
        {
            Content = "书源…",
            ToolTip = "选择并排序目录书源（默认起点、番茄优先），检测哪些站点此刻可用，也可以添加自己的书源",
        };
        sourceSettings.Click += (_, _) => BookSources_Click(this, new RoutedEventArgs());
        search.Children.Add(query); search.Children.Add(auto); search.Children.Add(cancelNetwork); search.Children.Add(sourceSettings);
        var sources = new ComboBox { IsEnabled = false, DisplayMemberPath = "PageTitle", MinWidth = 300, Margin = new Thickness(0, 6, 0, 6) };
        top.Children.Add(sources);
        var sourceNote = new TextBlock { Text = "尚未选择参考目录。", TextWrapping = TextWrapping.Wrap }; top.Children.Add(sourceNote);
        var fallback = new StackPanel();
        top.Children.Add(new Expander { Header = "粘贴书籍网址或导入目录（番茄、登录受限时使用）", Content = fallback, IsExpanded = true });
        fallback.Children.Add(new TextBlock { Text = "书籍网址", Margin = new Thickness(0, 6, 0, 3) });
        var urlRow = new WrapPanel(); fallback.Children.Add(urlRow);
        var url = new TextBox { Width = 570, ToolTip = "起点、番茄、和图书的 HTTPS 书籍目录页" };
        var fetch = new Button { Content = "读取网址" }; var login = new Button { Content = "浏览器打开/登录" };
        urlRow.Children.Add(url); urlRow.Children.Add(fetch); urlRow.Children.Add(login);
        fallback.Children.Add(new TextBlock { Text = "目录文字：每行一个标题，卷名也保留；登录设置请使用上方「书源…」", Margin = new Thickness(0, 6, 0, 3) });
        var catalogText = new TextBox { Height = 100, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ToolTip = "每行一个标题，保留「第一卷…」等卷名行。也可从浏览器复制目录。" }; fallback.Children.Add(catalogText);
        var import = new Button { Content = "导入目录 TXT", HorizontalAlignment = HorizontalAlignment.Left }; fallback.Children.Add(import);
        Section("第二步 · 依据参考目录调整章节树", "补建漏识别章节、统一标题、移除重复章节、建立卷层级。逐项可勾选、可预览、可撤销。");
        var outlineRow = new WrapPanel(); top.Children.Add(outlineRow);
        var outline = new Button
        {
            Content = "预览章节调整…", IsEnabled = false,
            ToolTip = "对照参考目录补建漏识别章节、统一标题、移除重复章节、建立卷层级；"
                + "与顶部「预览目录修复」打开同一个修复确认窗。是否改原文在确认窗里决定，可撤销",
            Padding = new Thickness(16, 7, 16, 7)
        };
        outlineRow.Children.Add(outline);
        top.Children.Add(new TextBlock
        {
            Text = "本按钮与顶部「预览目录修复」进入同一个修复确认窗，依据就是你在这里选定或粘贴的目录；"
                + "不再有第二套预览与勾选。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = secondary, Margin = new Thickness(0, 6, 0, 0)
        });
        var status = new TextBlock
        {
            Text = preferred.Count > 0
                ? $"本文件夹的映射规则指定优先书源：{string.Join(" / ", preferred)}（其次按全局顺序）。"
                : "书源顺序：起点、番茄优先。可点「书源…」改顺序、检测可用性，或添加自己的书源。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8)
        };
        top.Children.Add(status);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var saveSettings = new Button
        {
            Content = "记住这份目录",
            ToolTip = "把当前选定或粘贴的目录存到本书名下，下次打开自动使用。不改原始 TXT。"
        };
        var cancel = new Button { Content = "关闭", IsCancel = true };
        footer.Children.Add(saveSettings); footer.Children.Add(cancel); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        // 扫描阈值不再是本对话框的界面元素：它只影响自助扫描，而自助扫描已并入统一入口。
        // 保留这个值是为了不改动已保存的目录偏好格式 —— 读写都照旧，只是不再出现在界面上。
        var minimumLines = 200;
        // 与顶部「一键目录修复」同一个方法。区别只在传参：这里把用户当场选定/粘贴的目录直接传进去，
        // 不再另外写盘再读回来，确认窗里显示的也就是这一份目录。
        outline.Click += async (_, _) =>
        {
            var edited = ReferenceCatalogInput.ParseText(catalogText.Text);
            if (edited is null) { status.Text = "目录文字里没有可用的章节标题。"; return; }
            var picked = sources.SelectedItem as ReferenceCatalog;
            var chosen = picked is not null
                && catalogText.Text == string.Join(Environment.NewLine, picked.Nodes.Select(n => n.Title))
                    ? picked : edited;
            Save();
            status.Text = $"已按这份目录（{chosen.Titles.Count} 章）生成方案，正在打开修复确认窗…";
            // 只把要显示的文本传进去，线程编组由统一入口负责 —— 对话框自己不做跨线程处理。
            await RunRepairReviewAsync(chosen, text => status.Text = text);
            status.Text = "修复确认窗已关闭。可继续修改目录后再次预览。";
        };
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        try
        {
            if (ReferenceCatalogInput.Load(_document.SourceSha256) is { } saved)
            {
                // A saved record is allowed to carry an empty query: SaveCatalog's parameter defaults
                // to "" and two callers (the one-click repair, and applying from the reference panel)
                // do not pass one. Assigning it unconditionally wiped the book name the box was seeded
                // with, so it came up blank for anyone who had ever saved a directory.
                query.Text = string.IsNullOrWhiteSpace(saved.Query)
                    ? ChapterAutoRepair.ExtractBookName(_document.SourcePath)
                    : saved.Query;
                url.Text = saved.Url; catalogText.Text = saved.Text;
                if (saved.MinimumLines > 0) minimumLines = saved.MinimumLines;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { status.Text = "已保存目录无法读取，可重新获取或导入。"; }
        using var lifetime = new CancellationTokenSource();
        CancellationTokenSource? request = null;
        void Save()
        {
            var exact = sources.SelectedItem as ReferenceCatalog;
            if (exact is not null && catalogText.Text != string.Join(Environment.NewLine, exact.Nodes.Select(n => n.Title))) exact = null;
            ReferenceCatalogInput.Save(_document.SourceSha256,
                new CatalogPreferences(query.Text, url.Text, catalogText.Text, minimumLines) { Catalog = exact });
            InvalidateBreakpoints();
            ScheduleBreakpoints();
        }
        async Task Network(bool discover)
        {
            request?.Cancel(); request?.Dispose(); request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            auto.IsEnabled = fetch.IsEnabled = false; cancelNetwork.IsEnabled = true;
            var enabled = ReferenceCatalogClient.Sources.Count(source => source.Enabled);
            status.Text = discover ? $"正在按书源顺序搜索公开目录（{enabled} 个已启用的书源）…" : "正在读取目录…";
            try
            {
                var client = new ReferenceCatalogClient();
                var searchInput = query.Text;
                if (discover && searchInput == ChapterAutoRepair.ExtractBookName(_document.SourcePath))
                    searchInput = await Task.Run(() => ReferenceCatalogInput.FindLocalBookUrl(_document.SourcePath), request.Token) ?? searchInput;
                var found = discover
                    ? await client.DiscoverAsync(searchInput, request.Token, preferred, refresh: true)
                    : new[] { await client.FetchAsync(url.Text, request.Token) };
                if (lifetime.IsCancellationRequested) return;
                sources.ItemsSource = found; sources.IsEnabled = found.Count > 0;
                if (found.Count > 0) sources.SelectedIndex = 0;
                status.Text = found.Count == 0
                    ? "未获取到可用目录，不能据此判断缺章。请粘贴书籍网址／编号，或导入目录文字；番茄同目录下载记录可自动识别。"
                    : $"已获取 {found.Count} 个候选。请核对来源与章数：点「预览章节调整…」就用这一份（不落盘），"
                      + "点「记住这份目录」才会存下来供下次自动使用。";
            }
            catch (OperationCanceledException) { status.Text = "获取已取消或超时，可使用备用导入。"; }
            catch (Exception ex) { status.Text = "获取失败：" + ex.Message; }
            finally { auto.IsEnabled = fetch.IsEnabled = true; cancelNetwork.IsEnabled = false; }
        }
        sources.SelectionChanged += (_, _) =>
        {
            if (sources.SelectedItem is not ReferenceCatalog catalog) return;
            url.Text = catalog.Source; catalogText.Text = string.Join(Environment.NewLine, catalog.Nodes.Select(n => n.Title));
            var keys = catalog.Titles.Select(UnnumberedHeadings.Key).ToHashSet();
            var matches = Flatten().Count(n => keys.Contains(UnnumberedHeadings.Key(n.Title)));
            // Naming the sources that were NOT taken. Several sites usually answer, and the repair
            // silently keeps the largest directory of those within 10% of it
            // (ReferenceCatalogInput.Pick); without this line "3 sites matched" looked like
            // "only one site was asked".
            var others = (sources.ItemsSource as IEnumerable<ReferenceCatalog> ?? [])
                .Where(candidate => !ReferenceEquals(candidate, catalog)).ToArray();
            var comparison = others.Length == 0 ? ""
                : $"\n另有 {others.Length} 份目录未采用（本次只用一份）："
                  + string.Join("、", others.Take(3).Select(other => $"{HostOf(other.Source)} {other.Titles.Count} 章"))
                  + (others.Length > 3 ? $" 等 {others.Length} 个" : "") + "。";
            sourceNote.Text = $"来源：{catalog.Source}\n页面：{catalog.PageTitle} · {catalog.Titles.Count} 条目录 · 当前已有标题匹配 {matches} 项。仅供参考，不代表全文完整。" + comparison;
            // **选中不等于确认。** 这里以前直接 Save()，而上面 Network() 会"自动选中第一个候选" ——
            // 两件事连起来就是"程序替用户选定并保存了第一份目录"，而用户从没说过要它。
            // 现在这里只预览：落盘留给「记住这份目录」（下次自动用），或者干脆不落盘 ——
            // 点「预览章节调整…」用的是当场这份目录（见 outline.Click）。
        };
        auto.Click += async (_, _) => await Network(true); fetch.Click += async (_, _) => await Network(false);
        cancelNetwork.Click += (_, _) => request?.Cancel();
        login.Click += (_, _) =>
        {
            if (!Uri.TryCreate(url.Text, UriKind.Absolute, out var target) || !ReferenceCatalogClient.Supported(target)) { status.Text = "请先填写支持的书籍目录网址。"; return; }
            Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
            status.Text = "请在浏览器完成登录，再复制目录文字到备用入口。请自行完成登录；这里只读取你主动导入的目录或填写的 Cookie。";
        };
        import.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "目录文本|*.txt" };
            if (picker.ShowDialog(dialog) != true) return;
            try { if (new FileInfo(picker.FileName).Length > 2 * 1024 * 1024) throw new IOException("目录文件超过 2 MB。"); catalogText.Text = File.ReadAllText(picker.FileName); url.Text = ""; sourceNote.Text = "来源：本地目录文件（用户导入）"; }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        catalogText.TextChanged += (_, _) => outline.IsEnabled = ReferenceCatalogInput.ParseText(catalogText.Text) is not null;
        outline.IsEnabled = ReferenceCatalogInput.ParseText(catalogText.Text) is not null;
        saveSettings.Click += (_, _) => { try { Save(); status.Text = "已保存本书目录；没有保存 Cookie。"; } catch (Exception ex) { status.Text = ex.Message; } };
        dialog.Closed += (_, _) => { lifetime.Cancel(); request?.Cancel(); };
        dialog.ShowDialog(); request?.Dispose();
    }

    /// <summary>Host of a catalog's address, for naming a source the user recognises.</summary>
    private static string HostOf(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri.Host : source;
}

