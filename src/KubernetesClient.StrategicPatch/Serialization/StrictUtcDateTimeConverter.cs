using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KubernetesClient.StrategicPatch.Serialization;

/// <summary>
/// <see cref="DateTime"/> converter that enforces UTC at both read and write.
/// Reads accept only ISO 8601 with a UTC marker ("Z" or "+00:00"); writes refuse non-UTC kinds.
/// Any deviation throws <see cref="JsonException"/> to surface bugs early — Kubernetes is UTC-only
/// at the API boundary and silent timezone coercion is a recipe for subtle drift.
/// </summary>
public sealed class StrictUtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected ISO 8601 timestamp string for DateTime.");
        }
        var text = reader.GetString()
            ?? throw new JsonException("DateTime string was null.");
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
        {
            throw new JsonException($"Invalid ISO 8601 timestamp: '{text}'.");
        }
        if (parsed.Offset != TimeSpan.Zero)
        {
            throw new JsonException(
                $"Refusing to deserialize non-UTC timestamp '{text}'. Use a 'Z' or '+00:00' suffix.");
        }
        return parsed.UtcDateTime;
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        if (value.Kind == DateTimeKind.Local)
        {
            throw new JsonException(
                "Refusing to serialize DateTimeKind.Local. Convert to UTC before assigning Kubernetes timestamps.");
        }
        var asUtc = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        // ISO 8601 with millisecond precision and 'Z' suffix — matches Kubernetes' canonical form.
        writer.WriteStringValue(asUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
    }
}
