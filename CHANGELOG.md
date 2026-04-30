# Changelog

All notable changes to `KubernetesClient.StrategicPatch` are recorded here. The
format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **Stage 11 — Integration readiness.** Vendored a pinned subset of K8s
  OpenAPI v3 (`v1.36.0`) under `reference/kubernetes/openapi-spec/v3/` and
  baked the resulting `schemas.json` (78 GVKs covering core/v1, apps/v1,
  batch/v1, networking/v1, rbac/v1, policy/v1, autoscaling/v2). Pin is
  recorded in `reference/kubernetes/PINNED_VERSION`; `scripts/regen-schemas.{sh,ps1}`
  refreshes both runtime + generator copies.
- **`EmbeddedSchemaProvider.Shared`** — process-wide singleton, lazy-loaded
  from the embedded resource. Auto-defaulted when callers omit
  `options.SchemaProvider`, eliminating boilerplate from deployment projects.
- **`Schema/SchemaBuilder`** — fluent factory for `SchemaNode`, the
  source-generator's emit target. Public surface is the codegen stability
  contract.
- **`Schema/GenerationManifest` + `IManifestedSchemaProvider`** —
  generator/snapshot identity (sha-256 hash, timestamp, wire-format version,
  GVK count). `EmitOnce` fires an Activity event
  `smp.schema_provider_init` exactly once per provider lifetime.
- **`SchemaNode.StructuralEquals`** — hand-rolled deep walk; record-default
  `Equals` uses reference equality on the `Properties` dictionary, which
  would silently misreport for source-generator round-trip tests.
- **Stage 12 — Kustomize input flow.** `Kustomize/KustomizeOverlay` —
  `LoadAll(string|Stream|TextReader)` for multi-document YAML, plus
  `Index`/`Find`/`TryGetKey` to locate a specific document by GVK + name +
  namespace. Uses YamlDotNet with
  `WithAttemptingUnquotedStringTypeDeserialization` so `replicas: 5` parses
  as a number rather than a quoted string.
- **Stage 13 — Roslyn source generator** —
  `KubernetesClient.StrategicPatch.SourceGenerators` (`netstandard2.0`).
  Emits `partial class GeneratedStrategicPatchSchemaProvider :
  ISchemaProvider, IManifestedSchemaProvider` from the same `schemas.json`
  the runtime library ships. Materialised as static C# data, AOT-friendly,
  no runtime JSON parse. Drop-in for `EmbeddedSchemaProvider`. Round-trip
  parity verified against `EmbeddedSchemaProvider.Shared` for every GVK in
  the snapshot.
- **`Diagnostics/SmpDiagnostics`** — pre-allocated `SmpDescriptor` catalog
  (SMP001..SMP005). Runtime lib does not depend on
  `Microsoft.CodeAnalysis`; the source generator converts each descriptor
  to a `DiagnosticDescriptor` at its boundary.
- **`Diagnostics/SchemaProviderDebug`** — `[Conditional("DEBUG")]` dumpers
  for inner-loop generator iteration (`DumpEmbedded(provider, writer)`,
  `EveryEmbeddedGvk()`). Evaporate to no-ops in Release.
- **`StrategicPatchOptions.MaxDepth`** (default 256) +
  `CancellationToken` parameter on diff/apply entry points. Engines bump
  `depth` and check cancellation at every recursive boundary.
- **`Internal/ScalarKey`** — canonical primitive-value key shared across
  set membership and merge-key construction; unifies behaviour across
  mixed `JsonValue.Create` / `JsonNode.Parse` construction.
- **`tests/SourceGenerators.Sandbox`** + **`tests/SourceGenerators.Tests`** —
  classlib that triggers the generator on every build with
  `EmitCompilerGeneratedFiles=true`, plus vanilla `CSharpGeneratorDriver`
  harness with round-trip parity tests.

### Changed

- `EnforceOptimisticConcurrency` no longer injects
  `metadata.uid`/`resourceVersion` when the patch is empty (`patch.Count == 0`),
  preserving the meaning of `IsEmpty` for ghost-call elision.
- `OpenApiSchemaWalker` unwraps single-element `allOf` wrappers around `$ref`
  (the K8s OpenAPI shape) so vendored specs bake correctly.
- Tests that asserted "no schema → atomic replacement" now pass an empty
  `InMemorySchemaProvider` explicitly to opt out of the auto-default
  (`Service_PortReorder_AtomicListReplace_WhenSchemaForcedAbsent`).
- Activity-listener tests under method-level parallelism use an
  `AsyncLocal<T>` correlation tag so listeners isolate their captures
  cleanly without `[DoNotParallelize]`.

### Documented

- [`Plan-v3.md`](Plan-v3.md) — host-integration plan superseding Plan-v2's
  "next plan after this one."
- [`docs/SOURCE_GEN_DEBUGGING.md`](docs/SOURCE_GEN_DEBUGGING.md) — generator
  iteration loop, why `Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing.MSTest`
  is unsuitable (Roslyn 3.8 transitive pin), debugger attach, snapshot tests.
- Per-project READMEs for `SchemaTool`, `SourceGenerators`,
  `SourceGenerators.Sandbox`, `SourceGenerators.Tests`.

## [0.1.0-alpha] — 2026-04-29

Initial preview release. Algorithmic and API surface lock; the v1 source
generator follow-up is the next planned release.

### Added

- `TwoWayMerge.CreateTwoWayMergePatch` — full SMP two-way diff (objects,
  primitives, lists by `mergeKey`, set / atomic lists, `$retainKeys`,
  `$setElementOrder`, `$deleteFromPrimitiveList`, `$patch:delete`).
- `ThreeWayMerge.CreateThreeWayMergePatch` — three-way merge with conflict
  detection. Throws `StrategicMergePatchConflictException` unless
  `OverwriteConflicts = true`.
- `PatchApply.StrategicMergePatch` — server-side apply matching Go's
  `MergeParallelList=true` semantics. Round-trip property
  `Apply(original, CreateTwoWay(original, modified)) == modified` verified
  across the entire ported corpus.
- `StrategicPatchExtensions` on `IKubernetesObject` — `CreateStrategicPatch`,
  `CreateThreeWayStrategicPatch`, `ApplyStrategicPatch` for typed
  `KubernetesClient` model types.
- `StrategicPatchResult` — `V1Patch`, `IsEmpty`, `PayloadBytes`, `Gvk`. Callers
  branch on `IsEmpty` to skip ghost API calls.
- `StrategicPatchOptions` — `IgnoreNullValuesInModified`,
  `EnforceOptimisticConcurrency`, `OverwriteConflicts`, `Logger`,
  `SchemaProvider`.
- `ISchemaProvider` abstraction with `EmbeddedSchemaProvider`,
  `InMemorySchemaProvider`, and `CompositeSchemaProvider` implementations.
- `SchemaTool` — build-time CLI that ingests OpenAPI v3 documents and emits a
  minified `schemas.json` snapshot. `$ref` cycle-guarded.
- `ActivitySource("KubernetesClient.StrategicPatch")` — spans
  `smp.compute_two_way`, `smp.compute_three_way`, `smp.apply` with consistent
  `smp.gvk`, `smp.empty`, `smp.patch.bytes`, `smp.schema_miss_count` tags.
- Strict UTC `DateTime` / `DateTimeOffset` converters via
  `StrategicPatchJsonOptions.Default`.
- `samples/EksAutoscaler` — runnable end-to-end demo. `samples/Common/GhaLogger`
  emits GitHub Actions workflow commands; the library itself stays CI-vendor-agnostic.

### Vendored

- `k8s.io/apimachinery/pkg/util/strategicpatch` at
  [`v0.36.0`](reference/PINNED_COMMIT) (commit `debe1eba03a0c4134fd07a8f7586c44bb94ec7b0`).
  Read-only documentation; never compiled. Refresh process documented in
  [`reference/README.md`](reference/README.md).

### Known limitations

- A wire-format SMP patch cannot distinguish "field is null" from "field is
  absent" — same constraint as the Go reference and RFC 7396. Round-tripping
  a `modified` that asserts `{x: null}` collapses `x` to absent. Pinned by
  `ExplicitNull_InModified_BehavesAsDelete`.
- `EmbeddedSchemaProvider` ships empty in this preview; consumers regenerate
  via `scripts/regen-schemas.{sh,ps1}` until the source-generator follow-up
  bakes the schema at compile time. Without an embedded snapshot the engine
  falls through to RFC 7396 per-subtree, which is correct but loses
  list-merge directives.
