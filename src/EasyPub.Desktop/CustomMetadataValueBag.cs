using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Data;
using EasyPub.Core;

namespace EasyPub.Desktop;

public sealed class CustomMetadataValueBag : INotifyPropertyChanged
{
    private readonly IReadOnlyList<CalibreCustomMetadata> _definitions;
    private readonly Dictionary<string, string> _values;

    public CustomMetadataValueBag(
        IEnumerable<CalibreCustomMetadata>? definitions,
        IEnumerable<CalibreCustomMetadata>? values = null)
    {
        var prepared = CalibreCustomMetadata.PrepareAssignments(definitions, values);
        _definitions = prepared.Select(item => item with { Value = string.Empty }).ToArray();
        _values = prepared.ToDictionary(
            item => item.CalibreLookupName,
            item => item.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public string this[string lookupName]
    {
        get
        {
            var key = CalibreCustomMetadata.NormalizeLookupName(lookupName);
            return _values.TryGetValue(key, out var value) ? value : string.Empty;
        }
        set
        {
            var key = CalibreCustomMetadata.NormalizeLookupName(lookupName);
            var normalized = value ?? string.Empty;
            if (_values.TryGetValue(key, out var current) && current == normalized) return;
            _values[key] = normalized;
            OnPropertyChanged(Binding.IndexerName);
        }
    }

    public IReadOnlyList<CalibreCustomMetadata> ToMetadata(bool includeEmpty = false)
    {
        var values = _definitions.Select(definition => definition with
        {
            Value = this[definition.CalibreLookupName],
        });
        return CalibreCustomMetadata.NormalizeAll(
            includeEmpty ? values : values.Where(item => item.HasValue));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal static class CustomMetadataColumnFactory
{
    public static void AddEditableColumns(
        DataGrid grid,
        IReadOnlyList<CalibreCustomMetadata> definitions,
        int insertIndex,
        string valueBagProperty)
    {
        foreach (var definition in definitions)
        {
            var lookupName = definition.CalibreLookupName.TrimStart('#');
            var column = new DataGridTextColumn
            {
                Header = definition.DisplayHeading,
                Width = new DataGridLength(140),
                IsReadOnly = false,
                Binding = new Binding($"{valueBagProperty}[{lookupName}]")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                },
            };
            grid.Columns.Insert(insertIndex++, column);
        }
    }

    public static IReadOnlyList<CalibreCustomMetadata> CollectDefinitions(
        IEnumerable<CalibreCustomMetadata>? primaryDefinitions,
        IEnumerable<CalibreCustomMetadata>? existingValues)
    {
        return CalibreCustomMetadata.PrepareAssignments(primaryDefinitions, existingValues)
            .Select(item => item with { Value = string.Empty })
            .ToArray();
    }
}

public sealed class CustomMetadataFieldEditRow(
    CalibreCustomMetadata definition,
    CustomMetadataValueBag values)
{
    public string LookupName { get; } = definition.CalibreLookupName;
    public string DisplayHeading { get; } = definition.DisplayHeading;
    public string TypeLabel { get; } = definition.TypeLabel;
    public string Value
    {
        get => values[definition.CalibreLookupName];
        set => values[definition.CalibreLookupName] = value;
    }
}
