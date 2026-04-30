# SourceGenerators.Tests

MSTest harness for `KubernetesClient.StrategicPatch.SourceGenerators`. Two
test classes:

- **`HarnessSmokeTests`** — drives the generator via
  `CSharpGeneratorDriver` against a synthetic consumer compilation,
  asserting it emits `GeneratedStrategicPatchSchemaProvider.g.cs` and
  reports the expected SMP00x diagnostics.
- **`GeneratedProviderRoundTripTests`** — the headline test: instantiates
  the emitted `GeneratedStrategicPatchSchemaProvider` and verifies, for
  every GVK in the embedded `schemas.json`, that
  `SchemaNode.StructuralEquals(EmbeddedSchemaProvider.Shared.GetRootSchema(g),
  Generated.Instance.GetRootSchema(g))` holds. If round-trip parity passes,
  deployment projects can swap providers without behavioural change.

The round-trip test is the safety net for any change in
`SourceWriter.Emit` — if the generator emits code that materialises a tree
shape different from what `Internal/SchemaWireFormat.Deserialize`
produces, this test fails loudly with the offending GVK.

## Why not the canonical MSTest source-generator harness

`Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing.MSTest` (the
"official" harness) hard-pins `Microsoft.CodeAnalysis` to **3.8.0**
transitively, which predates `IIncrementalGenerator` (Roslyn 4.0). The
package family has not shipped a 4.x update in over a year, so we use a
vanilla `CSharpGeneratorDriver` instead. See
[`docs/SOURCE_GEN_DEBUGGING.md`](../../docs/SOURCE_GEN_DEBUGGING.md) for
the full rationale and the `Verify.SourceGenerators` migration path if
inline assertions become unwieldy.

## Running

```sh
# whole solution
dotnet test

# this project only
dotnet test tests/SourceGenerators.Tests
```

Method-level parallelism is on (`MSTestSettings.cs`); tests that mutate
process-global `ActivityListener` state isolate via the `AsyncLocal<T>`
correlation tag pattern documented in `docs/AI_AGENT_CONTEXT.md`.

## Related

- [`tests/SourceGenerators.Sandbox/`](../SourceGenerators.Sandbox/) —
  inner-loop "build it and read the .g.cs" iteration project. Not a test
  project.
- [`src/KubernetesClient.StrategicPatch.SourceGenerators/`](../../src/KubernetesClient.StrategicPatch.SourceGenerators/) —
  the generator under test.
- [`Diagnostics/SchemaProviderDebug`](../../src/KubernetesClient.StrategicPatch/Diagnostics/SchemaProviderDebug.cs) —
  `[Conditional("DEBUG")]` dumpers (`DumpEmbedded(provider, writer)`,
  `EveryEmbeddedGvk()`) for ad-hoc inspection during iteration.
