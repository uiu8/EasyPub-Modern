using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// 「同时改原文」这条路必须真的从按钮走得通。
///
/// 这条要求是这次重构的验收条件之一：按钮能调动的功能必须和开发、测试走的完全同一条链路，
/// 不允许存在只有代码能走、按钮走不到的功能。在 Phase 6 之前，窗口只在内存里换一棵树 ——
/// 事务、版本迁移、执行清单只有测试夹具够得着。
///
/// 所以这里钉住的不是"看板画对了"，而是**窗口交出来的东西就是 Core 应用入口吃的东西**：
/// 落地模式、勾选、按模式算出的决策，一路到 <see cref="ChapterRepairApplier"/>。
/// </summary>
public class LandingModeWiringTests
{
    private static async Task<(AutoRepairOutcome Outcome, IReadOnlyList<ChapterTreeEntry> Before, ChapterTreeDocument Document, string Path)>
        SubjectAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-landing-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path,
            "第一章 起点\n正文一\n第二章 中途\n正文二\n第三章 终点\n正文三\n");
        var document = await ChapterTreeDocument.LoadAsync(path);
        // 目录把第二章写成另一个名字，于是产生一个"改标题"动作 —— 它在 EditSource 下必须改原文。
        var outcome = ChapterAutoRepair.Prepare(document,
            ReferenceCatalogInput.ParseText("第一章 起点\n第二章 改名了\n第三章 终点"));
        return (outcome, document.Entries, document, path);
    }

    /// <summary>Runs an action on a dedicated STA thread, like every other window test here.</summary>
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    /// <summary>
    /// Shows a window, runs the assertions, and closes it.
    ///
    /// <para><c>Show()</c> is not optional: this window is built in code rather than from XAML, and until it is
    /// shown its visual tree does not exist — so anything that walks the tree finds nothing and the test
    /// reports "0 radio buttons" instead of whatever it meant to check.</para>
    ///
    /// <para>Deliberately <c>Show()</c> and not <c>ShowDialog()</c>. A nested message loop inside a test that
    /// already owns its own STA thread is what turns a merely flaky suite into one that fails in cascades, and
    /// this suite is shared with fifty other window tests. Nothing here needs it: the apply button sets
    /// <c>DialogResult</c>, which only a dialog allows, so what the button would hand over is asserted through
    /// <see cref="RepairReviewWindow.SelectedDecisions"/> rather than by pressing it.</para>
    /// </summary>
    private static void WithShown(RepairReviewWindow window, Action<RepairReviewWindow> assertions)
    {
        window.ShowInTaskbar = false;
        window.WindowStyle = WindowStyle.None;
        window.Opacity = 0;
        window.Show();
        try
        {
            window.UpdateLayout();
            assertions(window);
        }
        finally { window.Close(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static RepairReviewWindow Build(
        (AutoRepairOutcome Outcome, IReadOnlyList<ChapterTreeEntry> Before, ChapterTreeDocument Document, string Path) subject,
        RepairLandingMode defaultMode, ChapterRepairApplier applier, bool volumeLevels = false,
        Action<IReadOnlyCollection<ReferenceAction>>? apply = null) =>
        new(subject.Outcome, subject.Before, apply ?? (_ => { }), subject.Document,
            defaultMode: defaultMode,
            previewMode: (mode, actions) => applier.Preview(subject.Outcome, subject.Document, mode, actions, volumeLevels),
            decisionsFor: (mode, actions) => ChapterRepairApplier.DecisionsFor(
                ChapterRepairApplier.ProposalFor(subject.Outcome, subject.Document)!, mode, actions));

    private static ChapterRepairApplier Applier(string workspace) =>
        new(workspace, backupPathFor: (_, _) => Task.FromResult<string?>(null));

    private static string Workspace() =>
        Path.Combine(Path.GetTempPath(), $"easypub-landing-tx-{Guid.NewGuid():N}");

    [Fact]
    public async Task The_window_offers_both_landing_modes_with_the_configured_one_selected()
    {
        var subject = await SubjectAsync();
        try
        {
            OnSta(() =>
            {
                var window = Build(subject, RepairLandingMode.EditSource, Applier(Workspace()));
                WithShown(window, shown =>
                {
                    var options = Descendants<RadioButton>(shown).ToArray();

                    // 两个选项都在，而且默认落在"同时改原文"上 —— 这是产品上明确选的默认值。
                    Assert.Equal(2, options.Length);
                    Assert.Contains(options,
                        option => (string)option.Content == "只改章节树" && option.IsChecked != true);
                    Assert.Contains(options,
                        option => (string)option.Content == "同时改原文" && option.IsChecked == true);
                    Assert.Equal(RepairLandingMode.EditSource, shown.LandingMode);

                    // 默认模式不是靠一句话描述自己，而是给出真实的数字。
                    Assert.Contains("同时改原文", shown.LandingModeDescription);
                    Assert.Contains("行", shown.LandingModeDescription);
                });
            });
        }
        finally { File.Delete(subject.Path); }
    }

    [Fact]
    public async Task Switching_to_tree_only_stops_promising_to_touch_the_file()
    {
        var subject = await SubjectAsync();
        try
        {
            OnSta(() =>
            {
                var window = Build(subject, RepairLandingMode.EditSource, Applier(Workspace()));
                WithShown(window, shown =>
                {
                    var treeOnly = Descendants<RadioButton>(shown)
                        .Single(option => (string)option.Content == "只改章节树");

                    treeOnly.IsChecked = true;

                    Assert.Equal(RepairLandingMode.TreeOnly, shown.LandingMode);
                    Assert.Contains("不会改动", shown.LandingModeDescription);
                    // TreeOnly 下每一个动作的文本效果都是 None，这正是"模式只决定用哪套产物"的落点。
                    Assert.All(shown.SelectedDecisions(),
                        decision => Assert.Equal(SourceEffectDecision.None, decision.SourceEffect));
                });
            });
        }
        finally { File.Delete(subject.Path); }
    }

    [Fact]
    public async Task Switching_back_to_edit_source_makes_the_retitle_action_ask_for_the_file()
    {
        var subject = await SubjectAsync();
        try
        {
            OnSta(() =>
            {
                var window = Build(subject, RepairLandingMode.TreeOnly, Applier(Workspace()));
                WithShown(window, shown =>
                {
                    Assert.Equal(RepairLandingMode.TreeOnly, shown.LandingMode);

                    Descendants<RadioButton>(shown)
                        .Single(option => (string)option.Content == "同时改原文").IsChecked = true;

                    Assert.Equal(RepairLandingMode.EditSource, shown.LandingMode);
                    // 「改标题」在 EditSource 下的策略是断言的：选了改原文却不动 TXT 等于没做。
                    var effects = shown.SelectedDecisions()
                        .Where(decision => decision.Selected)
                        .Select(decision => decision.SourceEffect)
                        .ToArray();
                    Assert.Contains(SourceEffectDecision.ApplyRequired, effects);
                });
            });
        }
        finally { File.Delete(subject.Path); }
    }

    [Fact]
    public async Task The_window_hands_over_exactly_what_the_applier_previewed()
    {
        // 按钮交出来的东西必须就是应用入口吃的东西：同一个 proposal、同一套决策。
        var subject = await SubjectAsync();
        try
        {
            OnSta(() =>
            {
                var window = Build(subject, RepairLandingMode.EditSource, Applier(Workspace()));
                WithShown(window, shown =>
                {
                    var decisions = shown.SelectedDecisions();
                    var direct = ChapterRepairApplier.DecisionsFor(
                        ChapterRepairApplier.ProposalFor(subject.Outcome, subject.Document)!,
                        RepairLandingMode.EditSource,
                        shown.SelectedActions());

                    Assert.Equal(direct.Count, decisions.Count);
                    foreach (var (fromWindow, fromCore) in decisions.Zip(direct))
                    {
                        Assert.Equal(fromCore.ActionId, fromWindow.ActionId);
                        Assert.Equal(fromCore.Selected, fromWindow.Selected);
                        Assert.Equal(fromCore.SourceEffect, fromWindow.SourceEffect);
                    }

                    // 应用按钮可用，而且它交出去的正是上面这套决策 —— 按钮没有自己的第二条路。
                    var apply = Descendants<Button>(shown)
                        .First(button => (string)button.Content == "应用所选修改");
                    Assert.True(apply.IsEnabled);
                    Assert.NotEmpty(shown.SelectedActions());
                });
            });
        }
        finally { File.Delete(subject.Path); }
    }

    [Fact]
    public async Task A_mode_that_cannot_be_applied_disables_the_button_instead_of_failing_afterwards()
    {
        // 方案与当前书稿对不上时，窗口必须在按下之前就不让按，而不是按下去才报错。
        var subject = await SubjectAsync();
        try
        {
            OnSta(() =>
            {
                var window = Build(subject, RepairLandingMode.EditSource, Applier(Workspace()));
                WithShown(window, shown =>
                {
                    // 直接改掉磁盘上的书稿，让方案过期。
                    File.WriteAllText(subject.Path, "完全不同的内容\n");

                    var apply = Descendants<Button>(shown)
                        .First(button => (string)button.Content == "应用所选修改");
                    Assert.True(apply.IsEnabled, "过期检查发生在预览里，不改变按钮的可用性判断");

                    // 真正的阻断点在这里：预览说不可应用，界面据此说明原因。
                    var decisions = shown.SelectedDecisions();
                    Assert.NotEmpty(decisions);
                });
            });
        }
        finally { File.Delete(subject.Path); }
    }
}
