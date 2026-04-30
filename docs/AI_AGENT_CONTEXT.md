# AI agent ramp-up

If you're an AI agent picking up work in this repo, read this file first.
It's the smallest set of facts that lets you make non-trivial changes
without re-deriving the architecture from scratch.

## What this repo is

A C# 10 / .NET 10 library that ports Kubernetes Strategic Merge Patch (SMP)
from Go (`k8s.io/apimachinery/pkg/util/strategicpatch`). It pairs with the
official `KubernetesClient` NuGet package: callers diff two typed K8s
objects, get back a `V1Patch` ready for `PatchNamespacedXxxAsync`.

The repo ships:

- **`KubernetesClient.StrategicPatch`** (`net10.0`) — the runtime library.
- **`KubernetesClient.StrategicPatch.SchemaTool`** — build-time CLI that
  ingests OpenAPI v3 documents and writes the embedded `schemas.json`.
- **`KubernetesClient.StrategicPatch.SourceGenerators`** (`netstandard2.0`) —
  Roslyn `IIncrementalGenerator` that emits `GeneratedStrategicPatchSchemaProvider`
  into the consuming compilation. Consumed via analyzer-only `ProjectReference`.
- **`samples/EksAutoscaler`** — runnable demo wired to GitHub Actions /
  EKS-specific glue (the library itself stays CI-vendor-agnostic).
- **Tests** — `KubernetesClient.StrategicPatch.Tests` (main suite),
  `tests/SourceGenerators.Sandbox` (classlib that triggers the generator on
  every build for inner-loop inspection), `tests/SourceGenerators.Tests`
  (vanilla `CSharpGeneratorDriver` round-trip parity vs `EmbeddedSchemaProvider`).

## Where to start reading code

In order:

1. [`docs/ARCHITECTURE.md`](ARCHITECTURE.md) — full project map, control flow, Go-parity table, source-generator pipeline.
2. [`Plan-v3.md`](../Plan-v3.md) — current host-integration plan (Stages 11-13); supersedes [`Plan-v2.md`](../Plan-v2.md).
3. [`README.md`](../README.md) — public surface and parity matrix.
4. [`reference/README.md`](../reference/README.md) — vendoring policy for the Go source-of-truth.
5. [`docs/SOURCE_GEN_DEBUGGING.md`](SOURCE_GEN_DEBUGGING.md) — generator iteration loop and diagnostics.
6. [`docs/CODE_REVIEW_2026-04-29.md`](CODE_REVIEW_2026-04-29.md) and [`docs/PRE_ROSLYN_AUDIT_2026-04-30.md`](PRE_ROSLYN_AUDIT_2026-04-30.md) — point-in-time hardening passes.

For a concrete change, find the analogous Go function in
`reference/k8s.io-apimachinery/strategicpatch/patch.go` first. The
file-by-file mapping is in `ARCHITECTURE.md`'s "Vendored Go reference" table.

## Operating contract for changes

### Engine code (`src/.../StrategicMerge/`)

- **Inputs are read-only.** Never mutate caller-supplied `original`/`modified`/`current`/`patch`.
  Always `DeepClone()` before mutating.
- **Recursion takes `int depth, CancellationToken ct`.** Bump `depth` at every
  recursive call; check `ct.ThrowIfCancellationRequested()` at every boundary.
- **Schema-miss is normal.** If `SchemaProvider` returns null for a path, fall through to
  RFC 7396 semantics for that subtree and call `SchemaMissTracking.RecordMiss`.
  This is observable but never fatal.
- **Stay parallel-test-clean.** Public statics that hold per-call state (we don't have any)
  would break `MSTestSettings.cs`'s method-level parallelism. If you need
  per-call state, thread it through parameters.

### Schema code (`src/.../Schema/`)

- `SchemaNode` is **immutable**. Construct via `SchemaBuilder` (preferred) or the record
  initializer.
- `SchemaBuilder`'s public signatures are part of the **source-generator stability contract**.
  Any breaking change bumps `Internal/SchemaWireFormat.CurrentVersion` so consumers regenerate.
- `ISchemaProvider` is the only abstraction the diff/apply engines see. Adding a new provider
  (live API server, file-system, registry, etc.) is purely additive.

### Tests (`tests/`)

- MSTest. Method-level parallelism is on. Tests that mutate process-global state
  (env vars, the global `ActivitySource`) must isolate via `[DoNotParallelize]`
  or `AsyncLocal<T>` correlation.
- New behavioural tests should carry `[TestCategory("go-parity")]` when they
  mirror a Go test, plus `[TestCategory("go:OriginalGoTestName")]` for traceability.

## Style and convention

- Nullable reference types are **on** project-wide.
- Namespaces follow folders.
- One primary type per file. File name == type name.
- Private helpers live with the type that owns them; cross-cutting helpers go in `Internal/`.
- Avoid `dynamic`. Use `JsonNode`/`JsonObject`/`JsonArray`/`JsonValue` directly.
- Avoid `Console.*` in the library. The library is CI-vendor-agnostic;
  GitHub Actions / EKS / etc. integration lives in `samples/Common/`.
- Prefer immutable collections (`FrozenDictionary` for hot lookups, plain
  `IReadOnlyDictionary` exposure on records).
- Public APIs accept `CancellationToken` as the last optional parameter.
- New public types document their **thread-safety contract** in `<remarks>`.

## "I want to..." cheat sheet

| Goal | Touch |
|---|---|
| Add a new `$directive` | `Directives.cs` constants, `TwoWayMerge`/`PatchApply` recognition, port the Go behaviour |
| Add a new schema source | Implement `ISchemaProvider`, register via `CompositeSchemaProvider` |
| Change wire format | `Internal/SchemaWireFormat.cs` AND `SourceGenerators/WireFormat.cs` — bump both `CurrentVersion`s in lockstep |
| Add a new public option | `StrategicPatchOptions.cs` — record `init` setter with default |
| Add an OTel tag | `StrategicMerge/{TwoWayMerge,ThreeWayMerge,PatchApply}.cs` near the existing `activity?.SetTag` calls |
| Add a Go-corpus test case | `tests/.../StrategicMerge/*.cs` — find or grow the relevant `[DataTestMethod]` table |
| Regenerate `schemas.json` | `scripts/regen-schemas.{sh,ps1}` against vendored OpenAPI v3 inputs (rebuilds runtime + generator copies) |
| Bump the Go pin | `reference/PINNED_COMMIT` + `reference/README.md` instructions |
| Bump the K8s OpenAPI pin | `reference/kubernetes/PINNED_VERSION` + rerun `scripts/regen-schemas.{sh,ps1}` |
| Add a new generator emit shape | `SourceGenerators/SourceWriter.cs`; round-trip test in `tests/SourceGenerators.Tests/GeneratedProviderRoundTripTests` will keep it honest |
| Inspect a provider's content | `SchemaProviderDebug.DumpEmbedded(provider, writer)` (DEBUG-only, evaporates in Release) |
| Add a Kustomize input case | `Kustomize/KustomizeOverlay.cs` — `LoadAll`/`Index`/`Find`/`TryGetKey` |

## Verification you should run before commit

```bash
dotnet build --nologo                           # must be clean
dotnet test --no-build --nologo                 # all green
```

For broader changes:

```bash
dotnet test --collect:"XPlat Code Coverage" \   # >= 80% line gate
  --results-directory ./TestResults
```

Round-trip property runs as part of the suite (`PatchApplyTests::RoundTrip_*`).

## Things to watch out for

- **`SchemaProvider` auto-defaults.** `StrategicPatchExtensions.CreateStrategicPatch`
  / `CreateThreeWayStrategicPatch` substitute `EmbeddedSchemaProvider.Shared`
  when `options.SchemaProvider` is `null`. Tests that previously relied on
  "no provider → atomic everything" must pass an empty
  `InMemorySchemaProvider` explicitly (see
  `Service_PortReorder_AtomicListReplace_WhenSchemaForcedAbsent`).
- **`{x: null}` in modified ≠ `{x: absent}`.** SMP cannot distinguish "field
  is null" from "field is absent" in the wire format. The pinned test
  `ExplicitNull_InModified_BehavesAsDelete` documents this; if you find
  yourself trying to fix it, you're going against the spec.
- **Compound merge keys read as single key.** `x-kubernetes-list-map-keys`
  may carry `[containerPort, protocol]` but the Go reference reads only
  `x-kubernetes-patch-merge-key`. We intentionally match Go; pinned by test.
- **`MaxDepth` and `CancellationToken` thread through.** Recursive engines
  bump `depth` at every call and check `ct.ThrowIfCancellationRequested()`
  at every boundary. `StrategicPatchOptions.MaxDepth` defaults to 256;
  exceeding it throws `StrategicMergePatchException`.
- **`$setElementOrder` racing through `PatchMerger`.** When threading new diff
  flags through the recursion, make sure the gate on `setElementOrder`
  emission only fires when actual content was emitted at that level —
  otherwise you'll get duplicate ordering arrays after `PatchMerger.Merge`.
  Stage 5's commit message details the original incident.
- **`EnforceOptimisticConcurrency` on no-op patches.** It must NOT inject
  `metadata.uid`/`resourceVersion` when `patch.Count == 0`, or `IsEmpty` is
  meaningless. Stage 9's commit message details the original incident.
- **Activity tag races.** `SchemaMissTracking.RecordMiss` does
  `GetTagItem` + `SetTag` on a single activity. The activity is owned by a
  single call (created with `using var activity = ...`), so the
  read-modify-write is single-threaded by construction. Don't share an
  activity reference across threads.

## When to stop and ask

- The user changes a Go function we ported, and the change is non-obvious.
- A test that asserts a documented limitation (the null-vs-absent collapse)
  starts failing. Don't "fix" it without confirming intent.
- The wire-format version needs bumping. That's a public-API change and
  consumers regenerate.
