using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>
/// Source settings: which sites are asked, in what order, and whether they answer today.
/// Deliberately one dialog. Splitting the ordering, the health check and the credentials across three
/// screens would leave the user guessing which one actually wins.
/// </summary>
public partial class ChapterEditorWindow
{
    private sealed class SourceRow : INotifyPropertyChanged
    {
        private bool _enabled;
        private string _status = "未检测";
        private string _detail = "";

        public BookSource Source { get; private set; } = null!;
        public string Name => Source.Name;
        public string Capability => Source.Capability;
        public bool BuiltIn => Source.BuiltIn;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                Source = Source with { Enabled = value };
                Raise();
            }
        }

        public string Status
        {
            get => _status;
            private set { _status = value; Raise(); }
        }

        public string Detail
        {
            get => _detail;
            private set { _detail = value; Raise(); }
        }

        public static SourceRow From(BookSource source) => new()
        {
            Source = source,
            _enabled = source.Enabled,
        };

        public void Bind(BookSource source)
        {
            Source = source;
            _enabled = source.Enabled;
            Raise(nameof(Name));
            Raise(nameof(Capability));
            Raise(nameof(Enabled));
        }

        public void Apply(BookSourceProbe probe)
        {
            Status = probe.Reachable ? (probe.Usable ? "可用" : "受限") : "不可用";
            Detail = probe.Detail;
        }

        public void Forget()
        {
            Status = "未检测";
            Detail = "";
        }

        public void Fail(string message)
        {
            Status = "检测中断";
            Detail = message;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private async void BookSources_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceChanged) return;
        var store = BookSourceStore.CreateDefault();
        IReadOnlyList<BookSource> saved;
        try { saved = await store.LoadAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetReviewResult("书源设置无法读取：" + ex.Message);
            return;
        }

        var rows = saved.Select(SourceRow.From).ToList();
        var dialog = ThemedWindow("目录书源 · 顺序与可用性", 940, 680);
        var secondary = TryFindResource("SecondaryTextBrush") as System.Windows.Media.Brush;
        var panel = new DockPanel { Margin = new Thickness(18) };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);

        top.Children.Add(new TextBlock { Text = "目录书源", FontSize = 22, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock
        {
            Text = "列表顺序就是询问顺序。起点和番茄排在最前，因为它们的目录带出版社自己的分卷，是最可靠的依据。" +
                   "改完点「保存并关闭」才会生效，改动只保存在本机。",
            TextWrapping = TextWrapping.Wrap, Foreground = secondary, Margin = new Thickness(0, 4, 0, 12)
        });

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserResizeRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            MinHeight = 220,
            ItemsSource = rows,
        };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "启用", Binding = new Binding("Enabled"), Width = 54 });
        grid.Columns.Add(new DataGridTextColumn { Header = "书源", Binding = new Binding("Name"), IsReadOnly = true, Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "能力", Binding = new Binding("Capability"), IsReadOnly = true, Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding("Status"), IsReadOnly = true, Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "说明", Binding = new Binding("Detail"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        DockPanel.SetDock(grid, Dock.Top);
        panel.Children.Add(grid);

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(actions, Dock.Top);
        panel.Children.Add(actions);
        var up = new Button { Content = "上移" };
        var down = new Button { Content = "下移" };
        var probe = new Button { Content = "检测可用性" };
        var remove = new Button { Content = "删除书源" };
        actions.Children.Add(up);
        actions.Children.Add(down);
        actions.Children.Add(probe);
        actions.Children.Add(remove);
        actions.Children.Add(new TextBlock { Text = "检测用书名：", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
        var sample = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(_document.SourcePath),
            Width = 220,
            ToolTip = "用这本书的书名去试每个源，结果就是它此刻在这台机器上的真实表现。",
        };
        actions.Children.Add(sample);

        var credential = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(credential, Dock.Top);
        panel.Children.Add(credential);
        credential.Children.Add(new TextBlock
        {
            Text = "所选书源的登录 Cookie（可选）：",
            VerticalAlignment = VerticalAlignment.Center,
        });
        var cookie = new PasswordBox { Width = 420, ToolTip = "起点等站点需要登录才显示完整分卷。只保存在本机，不会上传，也不会写进电子书。" };
        var saveCookie = new Button { Content = "填到所选书源" };
        credential.Children.Add(cookie);
        credential.Children.Add(saveCookie);

        var custom = new Expander { Header = "添加自定义书源（支持任何按书名搜索的网站）", IsExpanded = false, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(custom, Dock.Top);
        panel.Children.Add(custom);
        var customBody = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        custom.Content = customBody;
        customBody.Children.Add(new TextBlock
        {
            Text = "搜索地址里用 {q} 代表书名，例如 https://www.example.com/search?q={q} 。软件会自动识别目录结构，通常不需要额外配置。",
            TextWrapping = TextWrapping.Wrap, Foreground = secondary, FontSize = 12, Margin = new Thickness(0, 0, 0, 6)
        });
        var customRow = new WrapPanel();
        customBody.Children.Add(customRow);
        var customName = new TextBox { Width = 200, ToolTip = "显示名称，例如：少年梦" };
        var customUrl = new TextBox { Width = 460, ToolTip = "搜索地址模板，用 {q} 代表书名" };
        var add = new Button { Content = "添加" };
        customRow.Children.Add(new TextBlock { Text = "名称", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        customRow.Children.Add(customName);
        customRow.Children.Add(new TextBlock { Text = "搜索地址", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
        customRow.Children.Add(customUrl);
        customRow.Children.Add(add);

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(status, Dock.Bottom);
        panel.Children.Add(status);

        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        var save = new Button { Content = "保存并关闭", IsDefault = true };
        var cancel = new Button { Content = "取消", IsCancel = true };
        footer.Children.Add(save);
        footer.Children.Add(cancel);
        dialog.Content = panel;

        void Renumber()
        {
            for (var index = 0; index < rows.Count; index++)
                rows[index].Bind(rows[index].Source with { Priority = (index + 1) * 10 });
            grid.Items.Refresh();
        }

        void Move(int delta)
        {
            if (grid.SelectedItem is not SourceRow row) { status.Text = "请先在列表里选一行。"; return; }
            var index = rows.IndexOf(row);
            var target = index + delta;
            if (target < 0 || target >= rows.Count) return;
            rows.RemoveAt(index);
            rows.Insert(target, row);
            Renumber();
            grid.SelectedItem = row;
            status.Text = $"{row.Name} 已移到第 {target + 1} 位。";
        }

        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is not SourceRow row) return;
            cookie.Password = row.Source.Cookie ?? "";
            remove.IsEnabled = !row.BuiltIn;
        };
        remove.IsEnabled = false;
        remove.Click += (_, _) =>
        {
            if (grid.SelectedItem is not SourceRow row || row.BuiltIn) { status.Text = "内置书源不能删除，取消勾选即可停用。"; return; }
            rows.Remove(row);
            Renumber();
            status.Text = $"{row.Name} 已移除，保存后生效。";
        };
        saveCookie.Click += (_, _) =>
        {
            if (grid.SelectedItem is not SourceRow row) { status.Text = "请先在列表里选一行。"; return; }
            row.Bind(row.Source with { Cookie = string.IsNullOrWhiteSpace(cookie.Password) ? null : cookie.Password.Trim() });
            var stored = string.IsNullOrWhiteSpace(cookie.Password) ? "已清空" : "已填入";
            status.Text = $"{row.Name} 的 Cookie {stored}（未显示明文）。保存后生效。";
        };
        add.Click += (_, _) =>
        {
            var name = customName.Text.Trim();
            var url = customUrl.Text.Trim();
            if (name.Length < 2) { status.Text = "请填写书源名称。"; return; }
            if (!url.Contains("{q}", StringComparison.Ordinal)) { status.Text = "搜索地址里必须包含 {q}，它会被替换成书名。"; return; }
            if (!Uri.TryCreate(url.Replace("{q}", "test", StringComparison.Ordinal), UriKind.Absolute, out var probeUri)
                || !ReferenceCatalogClient.Supported(probeUri))
            {
                status.Text = "搜索地址无效，或指向了内网/回环地址。";
                return;
            }
            var id = BookSourceCatalog.CustomId(url);
            if (rows.Any(row => string.Equals(row.Source.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                status.Text = "这个网站已经有一个书源了；直接改它的地址即可。";
                return;
            }
            var created = new BookSource { Id = id, Name = name, SearchUrl = url, Priority = (rows.Count + 1) * 10 };
            var row = SourceRow.From(created);
            rows.Add(row);
            Renumber();
            grid.SelectedItem = row;
            customName.Text = "";
            customUrl.Text = "";
            status.Text = $"已添加「{name}」。建议点「检测可用性」确认它能搜到书。";
        };
        probe.Click += async (_, _) =>
        {
            probe.IsEnabled = false;
            grid.CommitEdit();
            var name = sample.Text.Trim();
            status.Text = $"正在用「{name}」逐一检测 {rows.Count} 个书源…";
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var client = new ReferenceCatalogClient();
            try
            {
                await Task.WhenAll(rows.Select(async row =>
                {
                    try { row.Apply(await client.ProbeAsync(row.Source, name, lifetime.Token)); }
                    catch (Exception ex) { row.Fail("检测中断：" + ex.Message); }
                }));
                var usable = rows.Count(row => row.Status == "可用");
                status.Text = $"检测完成：{usable}/{rows.Count} 个书源此刻可用。标「受限」的站点能打开但没搜到这本书，不代表站点坏了。";
            }
            finally { probe.IsEnabled = true; }
        };
        save.Click += async (_, _) =>
        {
            grid.CommitEdit();
            try
            {
                var ordered = BookSourceCatalog.Renumber(rows.Select(row => row.Source));
                await store.SaveAsync(ordered);
                // The catalog client reads this list on the next lookup, so the order takes effect at once.
                ReferenceCatalogClient.Sources = ordered;
                dialog.DialogResult = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = "保存失败：" + ex.Message;
            }
        };

        dialog.ShowDialog();
    }
}
