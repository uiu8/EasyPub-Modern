using EasyPub.Core;
using EasyPub.Desktop;

namespace EasyPub.Desktop.Tests;

public sealed class TaskCenterViewModelTests
{
    [Fact]
    public void Validation_report_updates_task_status_issues_and_retry_state()
    {
        var task = new BookTaskViewModel(@"C:\books\demo.txt", @"C:\out\demo.mobi");
        var report = new ArtifactValidationReport(
            @"C:\out\demo.mobi",
            "MOBI",
            [new ArtifactValidationIssue(ArtifactValidationSeverity.Warning, "cover", "封面需要检查。")],
            DateTimeOffset.Now,
            true,
            @"C:\out\demo.mobi.easypub-report.json");

        task.Update(BookTaskStage.Warning, 1, report.ResultLabel, report);

        Assert.Equal("完成（有提醒）", task.StatusText);
        Assert.Equal(1, task.Progress);
        Assert.True(task.CanRetry);
        Assert.True(task.RequiresHardwareConfirmation);
        Assert.Single(task.Issues);
        Assert.Contains("封面需要检查", task.Issues[0]);
    }
}
