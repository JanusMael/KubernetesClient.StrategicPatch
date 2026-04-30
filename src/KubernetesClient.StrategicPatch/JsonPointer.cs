using System.Collections;
using System.Text;

namespace KubernetesClient.StrategicPatch;

/// <summary>
/// Minimal RFC 6901 JSON Pointer. Immutable; segments are stored decoded.
/// Used for diagnostics and for navigating the schema/document trees.
/// </summary>
public readonly struct JsonPointer : IEquatable<JsonPointer>, IReadOnlyList<string>
{
    private readonly string[] _segments;

    public static JsonPointer Root { get; } = new(Array.Empty<string>());

    private JsonPointer(string[] segments) => _segments = segments;

    /// <summary>Number of segments. Root is empty.</summary>
    public int Count => _segments?.Length ?? 0;

    public string this[int index] => _segments[index];

    /// <summary>True if this pointer has no segments.</summary>
    public bool IsRoot => Count == 0;

    /// <summary>Parses an RFC 6901 pointer string ("/a/b~1c/d").</summary>
    public static JsonPointer Parse(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        if (pointer.Length == 0)
        {
            return Root;
        }
        if (pointer[0] != '/')
        {
            throw new FormatException($"JSON pointer must start with '/' (got: '{pointer}').");
        }
        var raw = pointer.AsSpan(1);
        var parts = raw.ToString().Split('/');
        var decoded = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            decoded[i] = Decode(parts[i]);
        }
        return new JsonPointer(decoded);
    }

    /// <summary>Builds a pointer from already-decoded segments.</summary>
    public static JsonPointer FromSegments(IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return new JsonPointer(segments.ToArray());
    }

    /// <summary>Returns a new pointer with one additional segment appended.</summary>
    public JsonPointer Append(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var next = new string[Count + 1];
        if (Count > 0)
        {
            Array.Copy(_segments, next, Count);
        }
        next[^1] = segment;
        return new JsonPointer(next);
    }

    /// <summary>Returns the canonical RFC 6901 string form ("/a/b~1c").</summary>
    public override string ToString()
    {
        if (IsRoot)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        foreach (var seg in _segments)
        {
            sb.Append('/').Append(Encode(seg));
        }
        return sb.ToString();
    }

    public bool Equals(JsonPointer other)
    {
        if (Count != other.Count)
        {
            return false;
        }
        for (var i = 0; i < Count; i++)
        {
            if (!string.Equals(_segments[i], other._segments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is JsonPointer other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        if (_segments is not null)
        {
            foreach (var seg in _segments)
            {
                hash.Add(seg, StringComparer.Ordinal);
            }
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(JsonPointer left, JsonPointer right) => left.Equals(right);
    public static bool operator !=(JsonPointer left, JsonPointer right) => !left.Equals(right);

    public IEnumerator<string> GetEnumerator()
    {
        if (_segments is null)
        {
            yield break;
        }
        foreach (var s in _segments)
        {
            yield return s;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static string Decode(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal)
               .Replace("~0", "~", StringComparison.Ordinal);

    private static string Encode(string segment) =>
        // Per RFC 6901: encode '~' first, then '/'.
        segment.Replace("~", "~0", StringComparison.Ordinal)
               .Replace("/", "~1", StringComparison.Ordinal);
}
