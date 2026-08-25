using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EasyPub.Core;

public enum CalibreCustomMetadataType
{
    Text,
    TextList,
}

public sealed record CalibreCustomMetadata
{
    public string LookupName { get; init; } = string.Empty;
    public string? ColumnHeading { get; init; }
    public CalibreCustomMetadataType Type { get; init; } = CalibreCustomMetadataType.Text;
    public string Value { get; init; } = string.Empty;

    [JsonIgnore]
    public string CalibreLookupName => NormalizeLookupName(LookupName);

    [JsonIgnore]
    public string DisplayHeading => string.IsNullOrWhiteSpace(ColumnHeading)
        ? CalibreLookupName.TrimStart('#')
        : ColumnHeading.Trim();

    [JsonIgnore]
    public string TypeLabel => Type == CalibreCustomMetadataType.TextList ? "逗号分隔文本" : "单值文本";

    public static string NormalizeLookupName(string lookupName)
    {
        var value = (lookupName ?? string.Empty).Trim();
        if (value.StartsWith('#')) value = value[1..];
        value = value.ToLowerInvariant();
        if (!Regex.IsMatch(value, "^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Calibre 检索名只能使用英文字母、数字和下划线，并且必须以字母开头。", nameof(lookupName));
        return "#" + value;
    }

    public static IReadOnlyList<CalibreCustomMetadata> NormalizeAll(
        IEnumerable<CalibreCustomMetadata>? definitions)
    {
        if (definitions is null) return [];
        var result = new List<CalibreCustomMetadata>();
        foreach (var definition in definitions)
        {
            if (definition is null) continue;
            var lookupName = NormalizeLookupName(definition.LookupName);
            var value = definition.Value?.Trim() ?? string.Empty;
            if (value.Length == 0)
                throw new ArgumentException($"自定义元数据 {lookupName} 的值不能为空。", nameof(definitions));
            var normalizedValue = definition.Type == CalibreCustomMetadataType.TextList
                ? string.Join(", ", SplitList(value))
                : value;
            if (normalizedValue.Length == 0)
                throw new ArgumentException($"自定义元数据 {lookupName} 至少需要一个有效值。", nameof(definitions));
            var normalized = definition with
            {
                LookupName = lookupName,
                ColumnHeading = string.IsNullOrWhiteSpace(definition.ColumnHeading)
                    ? lookupName.TrimStart('#')
                    : definition.ColumnHeading.Trim(),
                Value = normalizedValue,
            };
            var existingIndex = result.FindIndex(candidate => string.Equals(
                candidate.CalibreLookupName,
                normalized.CalibreLookupName,
                StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0) result[existingIndex] = normalized;
            else result.Add(normalized);
        }
        return result;
    }

    internal static string BuildOpfMetadataContent(CalibreCustomMetadata definition)
    {
        var normalized = NormalizeAll([definition]).Single();
        var label = normalized.CalibreLookupName.TrimStart('#');
        var isMultiple = normalized.Type == CalibreCustomMetadataType.TextList;
        object value = isMultiple ? SplitList(normalized.Value) : normalized.Value;
        var metadata = new Dictionary<string, object?>
        {
            ["#value#"] = value,
            ["#extra#"] = null,
            ["datatype"] = "text",
            ["display"] = new Dictionary<string, object?>(),
            ["is_custom"] = true,
            ["is_editable"] = true,
            ["is_category"] = true,
            ["is_csp"] = false,
            ["is_multiple"] = isMultiple ? "|" : null,
            ["is_multiple2"] = isMultiple
                ? new Dictionary<string, string>
                {
                    ["cache_to_list"] = "|",
                    ["ui_to_list"] = ",",
                    ["list_to_ui"] = ", ",
                }
                : new Dictionary<string, string>(),
            ["kind"] = "field",
            ["label"] = label,
            ["name"] = normalized.DisplayHeading,
            ["search_terms"] = new[] { normalized.CalibreLookupName },
        };
        return JsonSerializer.Serialize(metadata);
    }

    private static string[] SplitList(string value) => value
        .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(item => item.Length > 0)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToArray();
}
