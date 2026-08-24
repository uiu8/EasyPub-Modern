using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class ArtifactValidationTests
{
    [Fact]
    public async Task Epub_validation_checks_structure_and_writes_per_book_report()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-validate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "book.txt");
        var output = Path.Combine(directory, "book.epub");
        await File.WriteAllTextAsync(input, "第一章 开始\n正文。", System.Text.Encoding.UTF8);
        var request = new ConversionRequest(input, output, "验收测试");
        try
        {
            await new EasyPubConverter().ConvertAsync(request);
            var report = await new ArtifactValidationService().ValidateAndSaveAsync(request);

            Assert.True(report.StructurePassed);
            Assert.Equal("EPUB", report.Format);
            Assert.False(report.RequiresKindleHardwareConfirmation);
            Assert.True(File.Exists(report.ReportPath));
            Assert.Equal(
                Path.Combine(directory, ArtifactValidationService.ReportDirectoryName),
                Path.GetDirectoryName(report.ReportPath));
            Assert.False(File.Exists(output + ".easypub-report.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Batch_report_exposes_item_stages_and_validation_result()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-task-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "book.txt");
        var output = Path.Combine(directory, "book.epub");
        await File.WriteAllTextAsync(input, "第一章 开始\n正文。", System.Text.Encoding.UTF8);
        var events = new List<BatchConversionProgress>();
        try
        {
            var outcomes = await new BatchConverter(new EasyPubConverter()).ConvertWithReportAsync(
                [new ConversionRequest(input, output)
                {
                    Options = new ConversionOptions
                    {
                        ArtifactValidation = new ArtifactValidationOptions { Enabled = true },
                    },
                }],
                progress: new ImmediateProgress<BatchConversionProgress>(events.Add));

            Assert.True(outcomes[0].Succeeded);
            Assert.NotNull(outcomes[0].Validation);
            Assert.True(outcomes[0].Validation!.StructurePassed);
            Assert.Contains(events, item => item.ItemStage == BookTaskStage.Waiting);
            Assert.Contains(events, item => item.ItemStage == BookTaskStage.Checking);
            Assert.Contains(events, item => item.ItemStage == BookTaskStage.GeneratingEpub);
            Assert.Contains(events, item => item.ItemStage == BookTaskStage.Validating);
            Assert.Contains(events, item => item.ItemStage == BookTaskStage.Completed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Batch_skips_structural_validation_by_default()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-validation-off-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "book.txt");
        var output = Path.Combine(directory, "book.epub");
        await File.WriteAllTextAsync(input, "第一章 开始\n正文。", System.Text.Encoding.UTF8);
        var events = new List<BatchConversionProgress>();
        try
        {
            var outcomes = await new BatchConverter(new EasyPubConverter()).ConvertWithReportAsync(
                [new ConversionRequest(input, output)],
                progress: new ImmediateProgress<BatchConversionProgress>(events.Add));

            Assert.True(outcomes[0].Succeeded);
            Assert.Null(outcomes[0].Validation);
            Assert.DoesNotContain(events, item => item.ItemStage == BookTaskStage.Validating);
            Assert.Contains(events, item => item.ItemStage == BookTaskStage.Completed && item.Stage.Contains("未启用结构验收"));
            Assert.False(Directory.Exists(ArtifactValidationService.GetReportDirectory(output)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Report_directory_keeps_only_the_latest_ten_reports_by_default()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-report-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "book.txt");
        var output = Path.Combine(directory, "book.epub");
        await File.WriteAllTextAsync(input, "第一章 开始\n正文。", System.Text.Encoding.UTF8);
        var request = new ConversionRequest(input, output);
        try
        {
            await new EasyPubConverter().ConvertAsync(request);
            for (var index = 0; index < 12; index++)
                await new ArtifactValidationService().ValidateAndSaveAsync(request);

            var reportDirectory = ArtifactValidationService.GetReportDirectory(output);
            Assert.Equal(10, Directory.GetFiles(reportDirectory, "*.easypub-report.json").Length);
            Assert.Empty(Directory.GetFiles(directory, "*.easypub-report.json", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Mobi_validation_confirms_joint_boundary_and_asin_but_keeps_hardware_gate()
    {
        var root = FindWorkspaceRoot();
        var kindlegen = Path.Combine(root, "work", "easypub-compat", "legacy-capture", "bin", "kindlegen_v2.9.exe");
        if (!File.Exists(kindlegen)) return;
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-mobi-validate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "book.txt");
        var output = Path.Combine(directory, "book.mobi");
        await File.WriteAllTextAsync(input, "第一章 开始\n正文。", System.Text.Encoding.UTF8);
        var request = new ConversionRequest(input, output, "MOBI 验收测试")
        {
            Options = new ConversionOptions
            {
                Mobi = new MobiOptions { KindleGenPath = kindlegen },
            },
        };
        try
        {
            await new EasyPubConverter().ConvertAsync(request);
            var report = await new ArtifactValidationService().ValidateAndSaveAsync(request);

            Assert.True(report.StructurePassed);
            Assert.True(report.RequiresKindleHardwareConfirmation);
            Assert.Contains(report.Issues, issue => issue.Code == "mobi_joint" && issue.Severity == ArtifactValidationSeverity.Information);
            Assert.DoesNotContain(report.Issues, issue => issue.Code is "asin_missing" or "ebok_missing");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ImmediateProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }


    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "EasyPub.Modern.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate workspace root.");
    }
}
