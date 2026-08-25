using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
                Assert.Equal("当前范围的值（可留空）", grid.Columns[3].Header);

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

    [Fact]
    public void Per_book_editor_exposes_each_defined_custom_field_as_an_editable_column()
    {
        RunInSta(() =>
        {
            var definition = KindleShelfDefinition();
            var book = new InputBookItem(@"C:\books\小说.txt");
            var window = new BatchMetadataWindow([book], [definition])
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Opacity = 0,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var grid = Assert.IsType<DataGrid>(window.FindName("MetadataGrid"));
                var column = Assert.Single(grid.Columns, item => Equals(item.Header, "Kindle书架"));
                var textColumn = Assert.IsType<DataGridTextColumn>(column);
                Assert.False(column.IsReadOnly);
                Assert.Equal("CustomValues[kindlecollections]", Assert.IsType<Binding>(textColumn.Binding).Path.Path);
                Assert.DoesNotContain(grid.Columns, item => Equals(item.Header, "自定义列"));

                var row = Assert.Single(window.Rows);
                row.CustomValues["kindlecollections"] = "起点, 完结";
                Assert.Equal("起点, 完结", Assert.Single(row.CustomValues.ToMetadata()).Value);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Folder_mapping_exposes_each_defined_custom_field_as_an_editable_column()
    {
        RunInSta(() =>
        {
            var definition = KindleShelfDefinition();
            var rule = new FolderMetadataRule(@"C:\books", new BookMetadataOverrides
            {
                CustomMetadata = [definition with { Value = "起点" }],
            });
            var window = new MetadataMappingWindow([rule], [definition])
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Opacity = 0,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var grid = Assert.IsType<DataGrid>(window.FindName("RulesGrid"));
                var column = Assert.Single(grid.Columns, item => Equals(item.Header, "Kindle书架"));
                var textColumn = Assert.IsType<DataGridTextColumn>(column);
                Assert.False(column.IsReadOnly);
                Assert.Equal("CustomValues[kindlecollections]", Assert.IsType<Binding>(textColumn.Binding).Path.Path);
                Assert.DoesNotContain(grid.Columns, item => Equals(item.Header, "自定义列"));

                var row = Assert.Single(window.RuleRows);
                Assert.Equal("起点", row.CustomValues["kindlecollections"]);
                row.CustomValues["kindlecollections"] = "晋江";
                Assert.Equal("晋江", Assert.Single(row.ToRule().Metadata.CustomMetadata).Value);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Folder_rule_editor_shows_the_real_custom_field_name_and_value()
    {
        RunInSta(() =>
        {
            var definition = KindleShelfDefinition();
            var rule = new FolderMetadataRule(@"C:\books", new BookMetadataOverrides
            {
                CustomMetadata = [definition with { Value = "起点" }],
            });
            var window = new MetadataMappingRuleWindow(rule, [definition])
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Opacity = 0,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var items = Assert.IsType<ItemsControl>(window.FindName("CustomMetadataFieldsItems"));
                var field = Assert.IsType<CustomMetadataFieldEditRow>(Assert.Single(items.Items));
                Assert.Equal("Kindle书架", field.DisplayHeading);
                Assert.Equal("#kindlecollections", field.LookupName);
                Assert.Equal("起点", field.Value);
                field.Value = "番茄";
                Assert.Equal("番茄", field.Value);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static CalibreCustomMetadata KindleShelfDefinition() => new()
    {
        LookupName = "kindlecollections",
        ColumnHeading = "Kindle书架",
        Type = CalibreCustomMetadataType.TextList,
    };

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "动态自定义元数据列窗口测试超时。");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
