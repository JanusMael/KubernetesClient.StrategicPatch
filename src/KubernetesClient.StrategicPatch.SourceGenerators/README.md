# KubernetesClient.StrategicPatch.SourceGenerators

Roslyn `IIncrementalGenerator` that emits a compile-time-baked
`GeneratedStrategicPatchSchemaProvider` into the consuming compilation. The
provider is functionally equivalent to the runtime
`EmbeddedSchemaProvider` but materialised as static C# data — no runtime
JSON parse, AOT-friendly, single allocation for the dispatch dictionary.

Targets `netstandard2.0` because source generators run inside the C#
compiler host. The runtime library it pairs with targets `net10.0`.

## Wiring it in

Add an analyzer-only `ProjectReference` from your deployment project:

```xml
<ProjectReference
  Include="..\path\to\KubernetesClient.StrategicPatch.SourceGenerators.csproj"
  OutputItemType="Analyzer"
  ReferenceOutputAssembly="false" />
```

Then opt in by passing `GeneratedStrategicPatchSchemaProvider.Instance` for
`StrategicPatchOptions.SchemaProvider` (or registering it via DI). Without
the opt-in the runtime falls back to `EmbeddedSchemaProvider.Shared`, which
is the same data behind a runtime JSON parse.

## What gets generated

`GeneratedStrategicPatchSchemaProvider.g.cs` — one file containing:

- `partial class GeneratedStrategicPatchSchemaProvider : ISchemaProvider, IManifestedSchemaProvider`
- A `Manifest` carrying generator version, snapshot SHA-256 hash, GVK count,
  and wire-format version.
- One static `BuildXxx_Yyy_Kind()` method per GVK that constructs the
  `SchemaNode` tree via `SchemaBuilder.*` calls.
- A `FrozenDictionary<GroupVersionKind, SchemaNode>` populated lazily from
  those builders; backs `Resolve(...)`.

Inspect the emitted file by setting `EmitCompilerGeneratedFiles=true` in
the consuming project — see
[`docs/SOURCE_GEN_DEBUGGING.md`](../../docs/SOURCE_GEN_DEBUGGING.md) for
the full inner-loop workflow. The
[`tests/SourceGenerators.Sandbox`](../../tests/SourceGenerators.Sandbox/)
project has this enabled by default for iteration.

## Inputs

`schemas.json` is **linked** from
`src/KubernetesClient.StrategicPatch/EmbeddedResources/schemas.json` so the
generator and the runtime library always emit and load the same bytes.
Re-running [`scripts/regen-schemas.{sh,ps1}`](../../scripts/) updates both
sides in one shot.

## Diagnostics

`DiagnosticDescriptors` mirrors the runtime catalog
(`Diagnostics/SmpDiagnostics`):

| ID     | Severity | When |
|--------|----------|------|
| SMP001 | Info     | Per generator run, summarising baked GVK count + wire-format hash |
| SMP002 | Warning  | Type referenced by patch APIs without `[KubernetesEntity]` |
| SMP003 | Warning  | GVK requested but absent from the snapshot |
| SMP004 | Error    | `schemas.json` wire-format version doesn't match the library |
| SMP005 | Error    | Catch-all generator failure (with type + message) |

The runtime library does **not** take a `Microsoft.CodeAnalysis`
dependency. Each `SmpDescriptor` lives in the runtime lib so callers can
introspect the catalog without pulling Roslyn; this project converts each
descriptor to `DiagnosticDescriptor` at its boundary.

`RS2008` (analyzer release tracking) is suppressed via `<NoWarn>` because
this generator ships via ProjectReference, not NuGet.

## Stability contract

`SchemaBuilder` and `SchemaNode`'s public surface are the codegen target.
Breaking changes bump `WireFormat.CurrentVersion` here AND
`Internal/SchemaWireFormat.CurrentVersion` in the runtime library — keep
the two in lockstep so consumers regenerate cleanly.

## Verification

[`tests/SourceGenerators.Tests/GeneratedProviderRoundTripTests`](../../tests/SourceGenerators.Tests/GeneratedProviderRoundTripTests.cs)
spins up `CSharpGeneratorDriver`, instantiates the emitted provider, and
asserts `SchemaNode.StructuralEquals` against
`EmbeddedSchemaProvider.Shared` for every GVK in the snapshot. If
round-trip parity passes, deployment projects can swap providers without
behavioural change.
