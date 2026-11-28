using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TV.Models;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString() ?? string.Empty,
        JsonTokenType.Number => ReadRaw(ref reader),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        JsonTokenType.Null => string.Empty,
        _ => ReadRaw(ref reader)
    };

    private static string ReadRaw(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public sealed class FlexibleIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number)) return number;
        if (reader.TokenType == JsonTokenType.String)
            return int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
        if (reader.TokenType == JsonTokenType.True) return 1;
        reader.Skip();
        return 0;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
