using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;
using EasyPub.Desktop;

namespace EasyPub.Desktop.Tests;

public sealed class CustomMetadataWindowTests
{
    [Fact]
    public void Custom_metadata_editor_renders_existing_values_and_can_add_a_row()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            CustomMetadataWindow? window = null;
            try
            {
                window = new CustomMetadataWindow([
                    new CalibreCustomMetadata
                    {
                        LookupName = "kindlecollections",
                        ColumnHeading = "Kindle书架",
                        Type = CalibreCustomMetadataType.TextList,
                        Value = "起点, 完结",
                    },
                ])
                {
                    Width = 820,
                    Height = 480,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Opacity = 0,
                };
                window.Show();
                window.UpdateLayout();

                var grid = Assert.IsType<DataGrid>(window.FindName("MetadataGrid"));
                var row = Assert.Single(window.Rows);
                Assert.Equal("kindlecollections", row.LookupName);
                Assert.Equal("Kindle书架", row.ColumnHeading);
                Assert.Equal(CalibreCustomMetadataType.TextList, row.Type);
                Assert.Equal("起点, 完结", row.Value);
                Assert.True(grid.ActualWidth > 700);

                var add = Assert.IsType<Button>(window.FindName("AddButton"));
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, window.Rows.Count);
                Assert.Equal(CalibreCustomMetadataType.TextList, window.Rows[1].Type);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "自定义元数据窗口测试超时。");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
