using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KubernetesClient.StrategicPatch.Internal;

/// <summary>
/// Deep equality for <see cref="JsonNode"/> trees with the semantics SMP needs:
/// <list type="bullet">
///   <item>Object property order is irrelevant; same key set with pairwise-equal values wins.</item>
///   <item>Array order matters; equality is position-wise.</item>
///   <item>Numbers are compared canonically (123 == 123.0) so JSON round-trips that promote ints
///         to doubles do not register as diffs.</item>
///   <item><c>null</c> nodes equal each other; a <c>null</c> node never equals a non-null node.</item>
/// </list>
/// Mirrors the Go reference's <c>reflect.DeepEqual</c> on <c>map[string]interface{}</c> trees,
/// which is what <c>k8s.io/apimachinery/pkg/util/strategicpatch</c> ultimately compares.
/// </summary>
internal static class JsonNodeEquality
{
    public static bool DeepEquals(JsonNode? left, JsonNode? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left is null || right is null)
        {
            return false;
        }

        return (left, right) switch
        {
            (JsonObject lo, JsonObject ro) => ObjectsEqual(lo, ro),
            (JsonArray la, JsonArray ra) => ArraysEqual(la, ra),
            (JsonValue lv, JsonValue rv) => ValuesEqual(lv, rv),
            _ => false,
        };
    }

    private static bool ObjectsEqual(JsonObject a, JsonObject b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        foreach (var (key, valueA) in a)
        {
            if (!b.TryGetPropertyValue(key, out var valueB))
            {
                return false;
            }
            if (!DeepEquals(valueA, valueB))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ArraysEqual(JsonArray a, JsonArray b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (var i = 0; i < a.Count; i++)
        {
            if (!DeepEquals(a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValuesEqual(JsonValue a, JsonValue b)
    {
        var ka = a.GetValueKind();
        var kb = b.GetValueKind();
        if (ka != kb)
        {
            return false;
        }

        return ka switch
        {
            JsonValueKind.String => StringsEqual(a, b),
            JsonValueKind.Number => NumbersEqual(a, b),
            // True/False/Null are pure tag identity once kinds match.
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            // Object/Array kinds are handled before we reach JsonValue; anything else is unequal.
            _ => false,
        };
    }

    private static bool StringsEqual(JsonValue a, JsonValue b)
    {
        // Both are strings; pull as <string?> to avoid re-encoding via ToJsonString.
        return string.Equals(a.GetValue<string>(), b.GetValue<string>(), StringComparison.Ordinal);
    }

    private static bool NumbersEqual(JsonValue a, JsonValue b)
    {
        // Compare via the underlying JsonElement when present (avoids string allocation),
        // otherwise fall back to invariant decimal/double parsing of the textual form.
        if (a.TryGetValue<JsonElement>(out var ea) && b.TryGetValue<JsonElement>(out var eb))
        {
            return ea.GetRawText().Equals(eb.GetRawText(), StringComparison.Ordinal)
                || NumericTextEquals(ea.GetRawText(), eb.GetRawText());
        }

        return NumericTextEquals(a.ToJsonString(), b.ToJsonString());
    }

    private static bool NumericTextEquals(string left, string right)
    {
        if (left.Equals(right, StringComparison.Ordinal))
        {
            return true;
        }

        // Try decimal first to preserve precision for integer-shaped values.
        if (decimal.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var ld) &&
            decimal.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var rd))
        {
            return ld == rd;
        }

        if (double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var ldd) &&
            double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var rdd))
        {
            return ldd.Equals(rdd);
        }

        return false;
    }
}
