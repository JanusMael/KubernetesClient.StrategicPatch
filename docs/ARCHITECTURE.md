# Architecture

A guided tour of the KubernetesClient.StrategicPatch codebase, intended for
contributors and AI agents who need to land non-trivial changes without
re-discovering the whole tree first.

## High-level shape

```
typed K8s object (V1Deployment, V1Pod, ...)
   │
   │  KubernetesJson.Serialize  (preserves IntOrString, ResourceQuantity, ISO timestamps)
   ▼
JsonObject (System.Text.Json DOM)
   │
   │  TwoWayMerge / ThreeWayMerge / PatchApply
   │  (schema-driven via ISchemaProvider; falls back to RFC 7396 on miss)
   ▼
JsonObject (the patch)
   │
   │  ToJsonString
   ▼
V1Patch (StrategicMergePatch type) — ready for KubernetesClient PATCH calls
```

Every public entry point on `StrategicPatchExtensions` does the boundary
conversion and delegates to a static engine. Engines never touch
`KubernetesClient` types; they operate on `JsonObject` only. That separation
is what lets the source generator (planned next) emit schema metadata without
needing access to the runtime engine internals.

## Project map

| Path | Role |
|---|---|
| `src/KubernetesClient.StrategicPatch/` | Runtime library (net10.0) |
| `src/KubernetesClient.StrategicPatch.SchemaTool/` | Build-time CLI: OpenAPI v3 → `schemas.json` |
| `src/KubernetesClient.StrategicPatch.SourceGenerators/` | Roslyn `IIncrementalGenerator` (netstandard2.0) emitting `GeneratedStrategicPatchSchemaProvider` |
| `tests/KubernetesClient.StrategicPatch.Tests/` | Main MSTest suite, mirrors `src/` layout |
| `tests/SourceGenerators.Sandbox/` | classlib with `[KubernetesEntity]` stubs to trigger the generator on every build (see `docs/SOURCE_GEN_DEBUGGING.md`) |
| `tests/SourceGenerators.Tests/` | Vanilla `CSharpGeneratorDriver` harness + round-trip parity tests vs `EmbeddedSchemaProvider` |
| `samples/Common/` + `samples/EksAutoscaler/` | CI-vendor-specific glue (not in the lib) |
| `reference/k8s.io-apimachinery/strategicpatch/` | Vendored Go reference (read-only) |
| `reference/kubernetes/openapi-spec/v3/` | Vendored K8s OpenAPI v3 spec, source for `schemas.json` |

## Library internals

```
KubernetesClient.StrategicPatch (top-level namespace)
├── GroupVersionKind.cs                ←  (Group, Version, Kind) value type
├── JsonPointer.cs                     ←  RFC 6901 with ~0/~1 escaping
├── StrategicMergePatchException.cs    ←  base + .Path/.Group/.Version/.Kind
├── StrategicMergePatchConflictException.cs  ←  three-way conflict; carries paths
├── StrategicPatchOptions.cs           ←  IgnoreNullValuesInModified, EnforceOptimisticConcurrency,
│                                         OverwriteConflicts, MaxDepth, Logger, SchemaProvider
├── StrategicPatchResult.cs            ←  V1Patch + IsEmpty + PayloadBytes + Gvk
├── StrategicPatchExtensions.cs        ←  typed-object boundary
├── StrategicPatchActivity.cs          ←  ActivitySource("KubernetesClient.StrategicPatch")
│
├── Schema/                            ←  SMP metadata
│   ├── SchemaNode.cs                  ←  immutable record: Kind, PatchMergeKey, Strategy, ListType,
│   │                                       Properties, Items + StructuralEquals (deep walk;
│   │                                       record-default Equals uses reference equality on the
│   │                                       Properties dictionary, so we hand-roll deep compare)
│   ├── SchemaBuilder.cs               ←  fluent factory; the source-generator's emit target —
│   │                                       its public surface is the codegen stability contract
│   ├── ISchemaProvider.cs             ←  GVK + JsonPointer → SchemaNode (+ IManifestedSchemaProvider)
│   ├── GenerationManifest.cs          ←  generator/snapshot identity (sha-256, timestamp, wire-format
│   │                                       version, gvk count). EmitOnce → Activity event
│   │                                       "smp.schema_provider_init" with manifest tags
│   ├── EmbeddedSchemaProvider.cs      ←  process-wide singleton (.Shared); lazy-loads schemas.json
│   ├── InMemorySchemaProvider.cs      ←  test/CRD provider
│   ├── CompositeSchemaProvider.cs     ←  layering
│   └── KubernetesEntityResolver.cs    ←  Type → GVK via [KubernetesEntity], thread-safe cache
│
├── Serialization/
│   ├── StrategicPatchJsonOptions.cs   ←  Default options (Never-ignore, MaxDepth=256, strict UTC)
│   ├── StrictUtcDateTimeConverter.cs
│   └── StrictUtcDateTimeOffsetConverter.cs
│
├── Kustomize/
│   └── KustomizeOverlay.cs            ←  multi-doc YAML → JsonObject; Index/Find/TryGetKey for
│                                          locating "Deployment named api in default" against a stream
│
├── Diagnostics/
│   ├── SmpDiagnostics.cs              ←  pre-allocated SmpDescriptor catalog (SMP001..SMP005);
│   │                                       runtime lib does not depend on Microsoft.CodeAnalysis,
│   │                                       so the source generator converts these to
│   │                                       DiagnosticDescriptor at its own boundary
│   └── SchemaProviderDebug.cs         ←  [Conditional("DEBUG")] dumpers — DumpEmbedded(provider, writer);
│                                          evaporate to no-ops in Release builds
│
├── StrategicMerge/                    ←  diff / apply engines
│   ├── Directives.cs                  ←  $patch / $deleteFromPrimitiveList / $setElementOrder /
│   │                                       $retainKeys constants
│   ├── DiffFlags.cs                   ←  IgnoreDeletions / IgnoreChangesAndAdditions (internal)
│   ├── TwoWayMerge.cs                 ←  CreateTwoWayMergePatch + DiffObject (recursive)
│   ├── ThreeWayMerge.cs               ←  CreateThreeWayMergePatch (delta + deletions + conflicts)
│   ├── ListDiff.cs                    ←  atomic / set / map dispatch + $setElementOrder
│   ├── PatchApply.cs                  ←  StrategicMergePatch (server-side apply)
│   ├── PatchMerger.cs                 ←  deep-union of two patch DOMs
│   └── ConflictDetector.cs            ←  walk patch + changed, return JsonPointers of disagreements
│
└── Internal/                          ←  shared helpers
    ├── ScalarKey.cs                   ←  canonical primitive-value key (set membership / merge key);
    │                                       unified across mixed JsonValue.Create / JsonNode.Parse
    ├── JsonNodeEquality.cs            ←  order-independent objects, ordered arrays, number-canonical
    ├── JsonNodeCloning.cs             ←  null-safe DeepClone wrapper
    ├── SchemaWireFormat.cs            ←  schemas.json reader/writer (minified, single-letter keys)
    └── SchemaMissTracking.cs          ←  Activity event + smp.schema_miss_count tag
```

## Two-way diff control flow

```
CreateTwoWayMergePatch(original, modified, options)
  │
  ├── ValidateRootIdentity (apiVersion/kind must agree where both present)
  ├── ResolveGvk + SchemaProvider → rootSchema (or null → RFC 7396 fallback)
  ├── StartActivity("smp.compute_two_way") + smp.gvk tag
  │
  └── DiffObject(original, modified, schema, /, depth=0, ct):
        │
        ├── For each key in modified:
        │     ├── if value is null     → HandleNullInModified → emit delete (or skip under IgnoreNullValues)
        │     ├── if not in original   → emit add (gated by DiffFlags.IgnoreChangesAndAdditions)
        │     └── if both present      → DiffExisting:
        │            ├── kind mismatch → wholesale replace
        │            ├── both objects  → DiffObject (recurse, depth+1)
        │            ├── both arrays   → ListDiff.Diff (depth+1)
        │            └── both leaves   → DeepEquals; replace if different
        │
        ├── For each key in original missing from modified:
        │     └── emit delete (gated by DiffFlags.IgnoreDeletions and IgnoreNullValues)
        │
        └── if schema has RetainKeys flag and patch is non-empty: emit $retainKeys
```

`ListDiff` then dispatches on schema:

- **Atomic** (no Merge flag) → wholesale replace if `!DeepEquals`
- **Object list with merge-key** → index by key, recurse for matches, append new, emit
  `$patch:delete` element + `$setElementOrder/<field>`
- **Primitive merge / set** → additions in modified order + parallel
  `$deleteFromPrimitiveList/<field>` + `$setElementOrder/<field>`

## Three-way diff

```
delta      = diff(current, modified, IgnoreDeletions)
deletions  = diff(original, modified, IgnoreChangesAndAdditions)
patch      = MergePatches(deletions, delta)         ← deep-union
if !OverwriteConflicts:
    changed = diff(original, current)
    throw if patch and changed disagree on the same path
```

Critical detail: `IgnoreChangesAndAdditions` does NOT short-circuit Pass-1 of
the object diff loop. Pass-1 is the only path into nested objects, and
nested deletions hide there. Only the leaf emit is gated by the flag.

## Apply (server-side)

```
StrategicMergePatch(original, patch, options)
  │
  └── MergeMap(workingClone, patchClone, schema, depth=0, ct):
        ├── object-level $patch:delete/replace/merge
        ├── extract parallel-list directives ($deleteFromPrimitiveList, $setElementOrder)
        ├── apply $retainKeys (drops original keys not in the list)
        ├── for each remaining patch key:
        │     ├── null     → delete from original
        │     ├── not in original → take patch (with embedded directives stripped)
        │     ├── kind mismatch  → take patch
        │     ├── both objects   → recurse MergeMap
        │     ├── both arrays    → MergeSlice
        │     └── leaf           → take patch
        └── apply directives whose field had no main-line patch entry
```

## Observability

```
ActivitySource: "KubernetesClient.StrategicPatch"
spans:
  smp.compute_two_way   tags: smp.gvk, smp.empty, smp.patch.bytes, smp.schema_miss_count
  smp.compute_three_way tags: smp.gvk, smp.empty, smp.patch.bytes, smp.schema_miss_count
  smp.apply             tags: smp.gvk, smp.patch.bytes
events: smp.schema_miss { smp.path = "..." }    ← per subtree where schema is null

logs (caller-supplied ILogger):
  Information at boundary: "smp.compute_two_way gvk=... empty=... bytes=... schema_miss=..."
  Debug per path: "smp.add path=...", "smp.delete path=..."
```

The library never touches `Console`. Vendor-specific log shapes (GitHub
Actions workflow commands, EKS audit-id correlation) live in
`samples/Common/GhaLogger.cs`.

## Thread safety

- Every entry point is **safe to call concurrently** with different inputs.
- Engines **must not** see caller-side mutation of `original`/`modified`/`patch`
  during a call. `JsonNode` is not thread-safe (per S.T.J. docs); we never
  mutate the caller's tree, but we read it freely.
- Statics: `ActivitySource`, `KubernetesEntityResolver.Cache`
  (`ConcurrentDictionary`), `EmbeddedSchemaProvider.DefaultRoots` (`Lazy<>`,
  `isThreadSafe: true` default), `StrategicPatchJsonOptions.Default`
  (sealed read-only). All concurrent-read-safe.
- `Activity` mutation in `SchemaMissTracking.RecordMiss` uses `GetTagItem`
  + `SetTag` on the activity owned by a single call (never shared across
  threads), so the read-modify-write is single-threaded by construction.

## Source generator (Stage 13 — implemented)

`KubernetesClient.StrategicPatch.SourceGenerators` is a Roslyn
`IIncrementalGenerator` that emits a `GeneratedStrategicPatchSchemaProvider`
into the consumer's compilation. It is wired into the consumer via an
analyzer-only `ProjectReference`:

```xml
<ProjectReference Include="..\KubernetesClient.StrategicPatch.SourceGenerators\
                          KubernetesClient.StrategicPatch.SourceGenerators.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

1. **Inputs:** the runtime library's `schemas.json`, embedded into the
   generator assembly at build time (linked via the .csproj). Re-baking the
   snapshot via `scripts/regen-schemas.sh` updates both inputs in lockstep.
2. **Trigger:** `RegisterSourceOutput(CompilationProvider)`. The generator's
   behaviour does not depend on consumer syntax — the schema set is fully
   determined by the embedded resource. Using `CompilationProvider` (rather
   than the post-initialisation context) gives us
   `SourceProductionContext.ReportDiagnostic` for SMP00x emission.
3. **Output:** `partial class GeneratedStrategicPatchSchemaProvider :
   ISchemaProvider, IManifestedSchemaProvider`. Per-GVK static methods build
   `SchemaNode` literals via `SchemaBuilder.*`; a `FrozenDictionary` dispatches
   `Resolve(...)`. `Instance` is a singleton. `Manifest` carries the
   compile-time hash of `schemas.json` (CI can compare it against the runtime
   hash to detect "your generated provider is stale").
4. **Diagnostics:** `DiagnosticDescriptors` mirrors `SmpDiagnostics`'
   pre-allocated catalog (SMP001 Info on success summary, SMP00x for error
   modes). `RS2008` (release tracking) is suppressed via `<NoWarn>` because
   we ship via ProjectReference, not NuGet.
5. **Stability contract:** `SchemaBuilder` and `SchemaNode`'s public surface
   is the codegen target. Breaking changes bump
   `SchemaWireFormat.CurrentVersion` (and `WireFormat.CurrentVersion` in the
   generator assembly) so consumers regenerate.
6. **Verification:** `tests/SourceGenerators.Tests/GeneratedProviderRoundTripTests`
   spins up `CSharpGeneratorDriver`, instantiates the emitted provider, and
   asserts `SchemaNode.StructuralEquals` against `EmbeddedSchemaProvider.Shared`
   for every GVK in the snapshot. If the round-trip passes, deployment
   projects can swap providers without behavioural change.

The runtime lib does **not** take a `Microsoft.CodeAnalysis` dependency.
`SmpDescriptor` lives in the runtime lib so callers can introspect the
catalog without pulling Roslyn; the generator project converts each
descriptor into `DiagnosticDescriptor` at its own boundary.

For the inner-loop iteration workflow (where to find generated `.g.cs`,
attaching a debugger, snapshot tests), see
[`docs/SOURCE_GEN_DEBUGGING.md`](SOURCE_GEN_DEBUGGING.md).

## Vendored Go reference

`reference/k8s.io-apimachinery/strategicpatch/` is a read-only snapshot of
the upstream package at the commit recorded in `reference/PINNED_COMMIT`.
**Never compiled.** The C# implementation maps function-by-function:

| Go (patch.go)                         | C#                                              |
| ------------------------------------- | ----------------------------------------------- |
| `CreateTwoWayMergePatch`              | `TwoWayMerge.CreateTwoWayMergePatch`            |
| `CreateThreeWayMergePatch`            | `ThreeWayMerge.CreateThreeWayMergePatch`        |
| `StrategicMergePatch`                 | `PatchApply.StrategicMergePatch`                |
| `diffMaps`                            | `TwoWayMerge.DiffObject`                        |
| `handleSliceDiff` + `diffLists`       | `ListDiff.Diff` + `DiffListsOfMaps/Scalars`     |
| `mergeMap`                            | `PatchApply.MergeMap`                           |
| `mergeSlice`                          | `PatchApply.MergeSlice` + helpers               |
| `MergingMapsHaveConflicts`            | `ConflictDetector.FindConflicts`                |
| `applyRetainKeysDirective`            | `PatchApply.ApplyRetainKeys`                    |

Refresh process documented in `reference/README.md`.
