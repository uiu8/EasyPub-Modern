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
    private sealed class ProposalRow
    {
        public bool Selected { get; set; }
        public HeadingProposal Proposal { get; init; } = null!;
        public int Line => Proposal.Line;
        public string Title => Proposal.Original;
        public string Reference => Proposal.Suggested;
        public string Evidence => Proposal.Evidence;
    }

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
        top.Children.Add(new TextBlock { Text = "原始 TXT 不会被修改，所有调整都可撤销。", TextWrapping = TextWrapping.Wrap });
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
            ToolTip = "对照参考目录补建漏识别章节、统一标题、移除重复章节、建立卷层级；原始 TXT 不变，可撤销",
            Padding = new Thickness(16, 7, 16, 7)
        };
        outlineRow.Children.Add(outline);
        var options = new WrapPanel();
        options.Children.Add(new TextBlock { Text = "扫描正文超过（TXT 实际行数）：", VerticalAlignment = VerticalAlignment.Center });
        var minimum = new TextBox { Text = "200", Width = 75 }; options.Children.Add(minimum);
        var scan = new Button { Content = "检测并预览补建" }; options.Children.Add(scan);
        var referenceTitles = new CheckBox { Content = "采用匹配的参考标题（否则保留原题）", VerticalAlignment = VerticalAlignment.Center }; options.Children.Add(referenceTitles);
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, Height = 240 };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "补建", Binding = new System.Windows.Data.Binding("Selected") });
        foreach (var (header, binding) in new[] { ("原文行", "Line"), ("原始标题", "Title"), ("参考标题", "Reference"), ("依据", "Evidence") })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(binding), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var apply = new Button { Content = "补建勾选章节", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        var degradedPanel = new StackPanel();
        degradedPanel.Children.Add(new TextBlock
        {
            Text = "没有官方目录时，只能依据本地正文启发式推断（独立短行 + 上下空行 + 同名分段），可能误判，请逐条核对。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = secondary, Margin = new Thickness(0, 2, 0, 8)
        });
        degradedPanel.Children.Add(options);
        degradedPanel.Children.Add(grid);
        degradedPanel.Children.Add(apply);
        top.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 6) });
        top.Children.Add(new Expander { Header = "没有参考目录时：本地扫描（启发式，可能误判）", Content = degradedPanel, IsExpanded = false });
        var status = new TextBlock
        {
            Text = preferred.Count > 0
                ? $"本文件夹的映射规则指定优先书源：{string.Join(" / ", preferred)}（其次按全局顺序）。"
                : "书源顺序：起点、番茄优先。可点「书源…」改顺序、检测可用性，或添加自己的书源。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8)
        };
        top.Children.Add(status);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var saveSettings = new Button { Content = "保存本书目录与阈值" };
        var cancel = new Button { Content = "关闭", IsCancel = true };
        footer.Children.Add(saveSettings); footer.Children.Add(cancel); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        outline.Click += (_, _) => ReferenceOutlineAssist(ReferenceCatalogInput.ParseText(catalogText.Text) is { } edited && sources.SelectedItem is ReferenceCatalog picked
            && catalogText.Text == string.Join(Environment.NewLine, picked.Nodes.Select(n => n.Title)) ? picked : null, catalogText.Text);
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyPub Modern", "Catalogs", _document.SourceSha256 + ".json");
        try
        {
            if (File.Exists(settingsPath) && JsonSerializer.Deserialize<CatalogPreferences>(File.ReadAllText(settingsPath)) is { } saved)
            { query.Text = saved.Query; url.Text = saved.Url; catalogText.Text = saved.Text; minimum.Text = saved.MinimumLines.ToString(); }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { status.Text = "已保存目录无法读取，可重新获取或导入。"; }
        using var lifetime = new CancellationTokenSource();
        CancellationTokenSource? request = null;
        IReadOnlyList<ChapterTreeEntry>? snapshot = null;
        ProposalRow[] rows = [];
        void Invalidate() { apply.IsEnabled = false; grid.ItemsSource = null; rows = []; }
        void Save()
        {
            if (!int.TryParse(minimum.Text, out var count) || count < 1) throw new InvalidOperationException("行数必须为正整数。");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var temp = settingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new CatalogPreferences(query.Text, url.Text, catalogText.Text, count)));
            File.Move(temp, settingsPath, true);
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
                    ? await client.DiscoverAsync(searchInput, request.Token, preferred)
                    : new[] { await client.FetchAsync(url.Text, request.Token) };
                if (lifetime.IsCancellationRequested) return;
                sources.ItemsSource = found; sources.IsEnabled = found.Count > 0;
                if (found.Count > 0) sources.SelectedIndex = 0;
                status.Text = found.Count == 0
                    ? "未获取到可用目录，不能据此判断缺章。请粘贴书籍网址／编号，或导入目录文字；番茄同目录下载记录可自动识别。"
                    : $"已获取 {found.Count} 个候选，请核对来源及目录，再点击「依据参考目录调整章节树」。";
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
            sourceNote.Text = $"来源：{catalog.Source}\n页面：{catalog.PageTitle} · {catalog.Titles.Count} 条目录 · 当前已有标题匹配 {matches} 项。仅供参考，不代表全文完整。";
            try { Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = "目录已读取，但未保存：" + ex.Message; }
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
        catalogText.TextChanged += (_, _) => { Invalidate(); outline.IsEnabled = ReferenceCatalogInput.ParseText(catalogText.Text) is not null; };
        outline.IsEnabled = ReferenceCatalogInput.ParseText(catalogText.Text) is not null;
        minimum.TextChanged += (_, _) => Invalidate();
        scan.Click += async (_, _) =>
        {
            if (!int.TryParse(minimum.Text, out var count) || count < 1) { status.Text = "行数必须为正整数。"; return; }
            snapshot = Flatten().Select(n => n.ToEntry()).ToArray();
            var titles = catalogText.Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            scan.IsEnabled = false; status.Text = "正在扫描当前章节树…";
            try
            {
                var proposals = await Task.Run(() => UnnumberedHeadings.Find(_document, snapshot, count, titles));
                if (lifetime.IsCancellationRequested) return;
                rows = proposals.Select(p => new ProposalRow { Proposal = p, Selected = p.Recommended }).ToArray();
                grid.ItemsSource = rows; apply.IsEnabled = rows.Length > 0;
                status.Text = $"发现 {rows.Length} 个候选。请核对后勾选；只补建章节，不删除、不重排，原始 TXT 不变。";
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { scan.IsEnabled = true; }
        };
        saveSettings.Click += (_, _) => { try { Save(); status.Text = "已保存本书目录和阈值；没有保存 Cookie。"; } catch (Exception ex) { status.Text = ex.Message; } };
        apply.Click += (_, _) =>
        {
            grid.CommitEdit(); grid.CommitEdit();
            var selected = rows.Where(r => r.Selected).Select(r => r.Proposal).ToArray();
            if (snapshot is null || selected.Length == 0) { status.Text = "请至少勾选一个候选。"; return; }
            try
            {
                var repaired = UnnumberedHeadings.Apply(_document, snapshot, selected, referenceTitles.IsChecked == true);
                if (!ConfirmWorkbench($"将补建 {selected.Length} 个章节，正文顺序保持不变。确定应用？", "确认补建章节")) return;
                Mutate(() => { Roots.Clear(); foreach (var root in BuildTree(repaired)) Roots.Add(root); _selectedNode = Flatten().FirstOrDefault(n => n.TitleLineNumber == selected[0].Line); });
                SetOperationSelection(_selectedNode is null ? [] : [_selectedNode]); RefreshSelectedLines(); UpdateSummary(); UpdateActionButtons();
                dialog.DialogResult = true;
                SetReviewResult($"已补建 {selected.Length} 个章节，原始 TXT 不变，可撤销。没有重建已删除的章节。");
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        dialog.Closed += (_, _) => { lifetime.Cancel(); request?.Cancel(); };
        dialog.ShowDialog(); request?.Dispose();
    }
}
