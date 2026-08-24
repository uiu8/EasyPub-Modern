using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using EasyPub.Core;
using EasyPub.Desktop;

namespace EasyPub.Desktop.Tests;

public sealed class TaskCenterViewModelTests
{
    [Fact]
    public void Opening_task_center_does_not_try_to_write_read_only_progress()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            TaskCenterWindow? window = null;
            try
            {
                window = new TaskCenterWindow(new ObservableCollection<BookTaskViewModel>
                {
                    new(@"C:\books\demo.txt", @"C:\out\demo.mobi"),
                })
                {
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Opacity = 0,
                };
                window.Show();
                window.UpdateLayout();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "任务中心窗口测试超时。");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

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

    [Fact]
    public void Completed_task_without_validation_is_labeled_as_skipped()
    {
        var task = new BookTaskViewModel(@"C:\books\demo.txt", @"C:\out\demo.mobi");

        task.Update(BookTaskStage.Completed, 1, "转换完成（未启用结构验收）");

        Assert.Equal("完成", task.StatusText);
        Assert.Equal("未启用（可在高级中开启）", task.ValidationText);
        Assert.Empty(task.Issues);
    }
}
