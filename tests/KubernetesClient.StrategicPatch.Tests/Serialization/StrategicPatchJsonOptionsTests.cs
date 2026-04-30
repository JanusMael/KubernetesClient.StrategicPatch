using System.Text.Json;
using System.Text.Json.Serialization;
using KubernetesClient.StrategicPatch.Serialization;

namespace KubernetesClient.StrategicPatch.Tests.Serialization;

[TestClass]
public sealed class StrategicPatchJsonOptionsTests
{
    private sealed record SampleDoc(string? Name, DateTime? CreatedAt);

    [TestMethod]
    public void Default_IsCachedAndReadOnly()
    {
        var a = StrategicPatchJsonOptions.Default;
        var b = StrategicPatchJsonOptions.Default;
        Assert.AreSame(a, b);
        Assert.IsTrue(a.IsReadOnly);
    }

    [TestMethod]
    public void Default_DoesNotIgnoreNullProperties()
    {
        // Caller-supplied nulls must survive — under SMP they mark "delete this map key".
        var json = JsonSerializer.Serialize(
            new SampleDoc(null, null),
            StrategicPatchJsonOptions.Default);
        Assert.IsTrue(json.Contains("\"Name\":null", StringComparison.Ordinal));
        Assert.IsTrue(json.Contains("\"CreatedAt\":null", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Default_HasMaxDepth256()
    {
        Assert.AreEqual(256, StrategicPatchJsonOptions.Default.MaxDepth);
    }

    [TestMethod]
    public void Default_DefaultIgnoreCondition_IsNever()
    {
        Assert.AreEqual(JsonIgnoreCondition.Never, StrategicPatchJsonOptions.Default.DefaultIgnoreCondition);
    }

    [TestMethod]
    public void CreateClone_ProducesIndependentMutableCopy()
    {
        var clone = StrategicPatchJsonOptions.CreateClone();
        Assert.IsFalse(clone.IsReadOnly);
        Assert.AreNotSame(clone, StrategicPatchJsonOptions.Default);
        // Adding a converter must not affect Default.
        clone.Converters.Add(new JsonStringEnumConverter());
        Assert.IsFalse(StrategicPatchJsonOptions.Default.Converters.Any(c => c is JsonStringEnumConverter));
    }

    [TestMethod]
    public void DateTime_Utc_RoundTripsCleanly()
    {
        var doc = new SampleDoc("x", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var json = JsonSerializer.Serialize(doc, StrategicPatchJsonOptions.Default);
        Assert.IsTrue(json.Contains("\"2026-01-02T03:04:05.000Z\"", StringComparison.Ordinal),
            $"Unexpected JSON: {json}");

        var roundTrip = JsonSerializer.Deserialize<SampleDoc>(json, StrategicPatchJsonOptions.Default);
        Assert.AreEqual(DateTimeKind.Utc, roundTrip!.CreatedAt!.Value.Kind);
        Assert.AreEqual(doc.CreatedAt, roundTrip.CreatedAt);
    }

    [TestMethod]
    public void DateTime_LocalKind_RefusedOnWrite()
    {
        var doc = new SampleDoc("x", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local));
        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Serialize(doc, StrategicPatchJsonOptions.Default));
    }

    [TestMethod]
    public void DateTime_UnspecifiedKind_TreatedAsUtcOnWrite()
    {
        // Unspecified is the default for DateTime literals in C#; coercing to UTC silently could
        // hide bugs, but throwing on every test fixture is also painful. The chosen middle ground
        // is to pass through Unspecified by tagging it UTC; Local is the one we hard-reject.
        var doc = new SampleDoc("x", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified));
        var json = JsonSerializer.Serialize(doc, StrategicPatchJsonOptions.Default);
        Assert.IsTrue(json.Contains("\"2026-01-02T03:04:05.000Z\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DateTime_NonUtcOffsetInPayload_RefusedOnRead()
    {
        const string Json = """{"Name":"x","CreatedAt":"2026-01-02T03:04:05+05:00"}""";
        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Deserialize<SampleDoc>(Json, StrategicPatchJsonOptions.Default));
    }

    [TestMethod]
    public void DateTimeOffset_NonZeroOffset_RefusedOnWrite()
    {
        var dto = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(3));
        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Serialize(dto, StrategicPatchJsonOptions.Default));
    }

    [TestMethod]
    public void DateTimeOffset_Utc_RoundTripsCleanly()
    {
        var dto = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var json = JsonSerializer.Serialize(dto, StrategicPatchJsonOptions.Default);
        Assert.IsTrue(json.Contains("2026-01-02T03:04:05.000Z", StringComparison.Ordinal));
        var roundTrip = JsonSerializer.Deserialize<DateTimeOffset>(json, StrategicPatchJsonOptions.Default);
        Assert.AreEqual(dto, roundTrip);
    }

    [TestMethod]
    public void DateTimeOffset_NonUtcOffsetInPayload_RefusedOnRead()
    {
        const string Json = "\"2026-01-02T03:04:05+05:00\"";
        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Deserialize<DateTimeOffset>(Json, StrategicPatchJsonOptions.Default));
    }

    [TestMethod]
    public void DateTime_NonStringToken_Refused()
    {
        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Deserialize<DateTime>("123", StrategicPatchJsonOptions.Default));
    }

    [TestMethod]
    public void DateTimeOffset_NonStringToken_Refused()
    {
        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Deserialize<DateTimeOffset>("123", StrategicPatchJsonOptions.Default));
    }

    [TestMethod]
    public void DateTime_GarbageString_Refused()
    {
        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Deserialize<DateTime>("\"not-a-date\"", StrategicPatchJsonOptions.Default));
    }

    [TestMethod]
    public void DateTimeOffset_GarbageString_Refused()
    {
        Assert.ThrowsExactly<JsonException>(
            () => JsonSerializer.Deserialize<DateTimeOffset>("\"not-a-date\"", StrategicPatchJsonOptions.Default));
    }
}
