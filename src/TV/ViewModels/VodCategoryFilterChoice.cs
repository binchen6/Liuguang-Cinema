using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using TV.Models;

namespace TV.ViewModels;

public sealed record VodFilterValue(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("v"), JsonConverter(typeof(FlexibleStringConverter))] string Value);

internal sealed record VodFilterDefinition(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] List<VodFilterValue> Values);

public sealed class VodCategoryFilterChoice : ObservableObject
{
    private VodFilterValue _selected;
    private readonly Action _changed;

    internal VodCategoryFilterChoice(string key, string name, IReadOnlyList<VodFilterValue> values, Action changed)
    {
        Key = key;
        Name = name;
        Values = values;
        _selected = values[0];
        _changed = changed;
    }

    public string Key { get; }
    public string Name { get; }
    public IReadOnlyList<VodFilterValue> Values { get; }
    public VodFilterValue Selected
    {
        get => _selected;
        set
        {
            if (value is not null && SetProperty(ref _selected, value)) _changed();
        }
    }
}
