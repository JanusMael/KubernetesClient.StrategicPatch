using System.Text.Json.Nodes;
using KubernetesClient.StrategicPatch.Internal;
using KubernetesClient.StrategicPatch.StrategicMerge;

namespace KubernetesClient.StrategicPatch.Tests;

/// <summary>
/// Property-based round-trip tests. The library's strongest correctness guarantee is
/// <c>Apply(original, CreateTwoWayMergePatch(original, modified)) == modified</c> for every
/// well-formed pair. The Stage 6 tests pin a hand-written corpus; these tests exercise the same
/// property against thousands of randomly-generated documents drawn from a small but
/// representative grammar.
/// </summary>
/// <remarks>
/// <para>The generator is deterministic for a given seed so failures are reproducible. We use a
/// single fixed seed for the in-suite runs; an ad-hoc tester can pass the seed via the env
/// var <c>SMP_PROPERTY_SEED</c>. The grammar avoids the documented <c>{x: null}</c> ambiguity
/// (see <see cref="KubernetesClient.StrategicPatch.Tests.StrategicMerge.PatchApplyTests.ExplicitNull_InModified_BehavesAsDelete"/>)
/// by never producing explicit null values in <c>modified</c>.</para>
/// </remarks>
[TestClass]
public sealed class PropertyTests
{
    private const int CaseCount = 500;
    private const int DefaultSeed = 0x5eed_da7a;

    [TestMethod]
    public void RoundTrip_RandomDocuments_HoldsAcrossAllCases()
    {
        var seed = ResolveSeed();
        var rng = new Random(seed);
        var failures = new List<string>();

        for (var i = 0; i < CaseCount; i++)
        {
            var original = GenerateObject(rng, depth: 0, maxDepth: 4);
            var modified = MutateObject(original, rng, depth: 0, maxDepth: 4);
            try
            {
                var patch = TwoWayMerge.CreateTwoWayMergePatch(original, modified);
                var applied = patch is null
                    ? (JsonObject)original.DeepClone()
                    : PatchApply.StrategicMergePatch(original, patch);
                if (!JsonNodeEquality.DeepEquals(modified, applied))
                {
                    failures.Add(
                        $"case#{i} seed={seed}{Environment.NewLine}"
                        + $"  original: {original.ToJsonString()}{Environment.NewLine}"
                        + $"  modified: {modified.ToJsonString()}{Environment.NewLine}"
                        + $"  patch:    {patch?.ToJsonString() ?? "<null>"}{Environment.NewLine}"
                        + $"  applied:  {applied.ToJsonString()}");
                    if (failures.Count >= 3)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"case#{i} seed={seed} threw: {ex.GetType().Name}: {ex.Message}");
                if (failures.Count >= 3)
                {
                    break;
                }
            }
        }

        Assert.IsEmpty(failures,
            $"{failures.Count} property failures (showing first 3):{Environment.NewLine}"
            + string.Join(Environment.NewLine + "---" + Environment.NewLine, failures));
    }

    [TestMethod]
    public void IdentityPair_AlwaysProducesEmptyOrNullPatch()
    {
        var rng = new Random(ResolveSeed() + 1);
        for (var i = 0; i < 200; i++)
        {
            var doc = GenerateObject(rng, depth: 0, maxDepth: 3);
            var clone = (JsonObject)doc.DeepClone();
            var patch = TwoWayMerge.CreateTwoWayMergePatch(doc, clone);
            Assert.IsNull(patch, $"Identity pair produced a non-null patch: {patch?.ToJsonString()}");
        }
    }

    private static int ResolveSeed()
    {
        var env = Environment.GetEnvironmentVariable("SMP_PROPERTY_SEED");
        return int.TryParse(env, out var s) ? s : DefaultSeed;
    }

    // ---- generator ---------------------------------------------------------------------------

    private static JsonObject GenerateObject(Random rng, int depth, int maxDepth)
    {
        var obj = new JsonObject();
        var width = rng.Next(0, 5);
        for (var i = 0; i < width; i++)
        {
            var key = "k" + i;
            obj[key] = GenerateValue(rng, depth, maxDepth);
        }
        return obj;
    }

    private static JsonNode? GenerateValue(Random rng, int depth, int maxDepth)
    {
        // Avoid null values in generated documents — see remarks on the class for why.
        var maxKind = depth >= maxDepth ? 4 : 6; // no nesting at max depth
        return rng.Next(maxKind) switch
        {
            0 => JsonValue.Create(rng.Next(-1000, 1000)),
            1 => JsonValue.Create(RandomString(rng, length: rng.Next(1, 6))),
            2 => JsonValue.Create(rng.Next(2) == 0),
            3 => GeneratePrimitiveArray(rng),
            4 => GenerateObject(rng, depth + 1, maxDepth),
            5 => GenerateObjectArray(rng, depth + 1, maxDepth),
            _ => JsonValue.Create(0),
        };
    }

    private static JsonArray GeneratePrimitiveArray(Random rng)
    {
        var arr = new JsonArray();
        var len = rng.Next(0, 5);
        for (var i = 0; i < len; i++)
        {
            arr.Add(JsonValue.Create("v" + rng.Next(0, 10)));
        }
        return arr;
    }

    private static JsonArray GenerateObjectArray(Random rng, int depth, int maxDepth)
    {
        var arr = new JsonArray();
        var len = rng.Next(0, 4);
        for (var i = 0; i < len; i++)
        {
            arr.Add(GenerateObject(rng, depth, maxDepth));
        }
        return arr;
    }

    private static string RandomString(Random rng, int length)
    {
        const string Alpha = "abcdefghijklmnopqrstuvwxyz";
        Span<char> buf = stackalloc char[length];
        for (var i = 0; i < length; i++)
        {
            buf[i] = Alpha[rng.Next(Alpha.Length)];
        }
        return new string(buf);
    }

    // ---- mutator -----------------------------------------------------------------------------

    private static JsonObject MutateObject(JsonObject source, Random rng, int depth, int maxDepth)
    {
        var copy = (JsonObject)source.DeepClone();
        var existingKeys = copy.Select(kv => kv.Key).ToList();

        // Maybe delete some keys.
        var deleteCount = rng.Next(0, Math.Min(2, existingKeys.Count + 1));
        for (var i = 0; i < deleteCount && existingKeys.Count > 0; i++)
        {
            var idx = rng.Next(existingKeys.Count);
            var key = existingKeys[idx];
            existingKeys.RemoveAt(idx);
            copy.Remove(key);
        }

        // Maybe replace some surviving values.
        foreach (var key in existingKeys)
        {
            if (rng.Next(3) == 0)
            {
                copy[key] = GenerateValue(rng, depth, maxDepth);
            }
        }

        // Maybe add new keys.
        var addCount = rng.Next(0, 3);
        for (var i = 0; i < addCount; i++)
        {
            copy["m" + rng.Next(0, 1_000_000)] = GenerateValue(rng, depth, maxDepth);
        }

        return copy;
    }
}
