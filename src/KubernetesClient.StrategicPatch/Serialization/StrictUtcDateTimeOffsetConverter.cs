using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KubernetesClient.StrategicPatch.Serialization;

/// <summary>
/// <see cref="DateTimeOffset"/> companion to <see cref="StrictUtcDateTimeConverter"/>.
/// Refuses any offset other than <see cref="TimeSpan.Zero"/>.
/// </summary>
public sealed class StrictUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected ISO 8601 timestamp string for DateTimeOffset.");
        }
        var text = reader.GetString()
            ?? throw new JsonException("DateTimeOffset string was null.");
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            throw new JsonException($"Invalid ISO 8601 timestamp: '{text}'.");
        }
        if (parsed.Offset != TimeSpan.Zero)
        {
            throw new JsonException(
                $"Refusing to deserialize non-UTC timestamp '{text}'. Use a 'Z' or '+00:00' suffix.");
        }
        return parsed;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new JsonException(
                $"Refusing to serialize DateTimeOffset with non-UTC offset {value.Offset}.");
        }
        writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
    }
}
