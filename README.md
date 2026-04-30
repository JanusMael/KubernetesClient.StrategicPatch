# KubernetesClient.StrategicPatch

[![CI](https://github.com/JanusMael/KubernetesClient.StrategicPatch/actions/workflows/ci.yml/badge.svg)](https://github.com/JanusMael/KubernetesClient.StrategicPatch/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/KubernetesClient.StrategicPatch.svg)](https://www.nuget.org/packages/KubernetesClient.StrategicPatch)
[![NuGet Downloads](https://img.shields.io/nuget/dt/KubernetesClient.StrategicPatch.svg)](https://www.nuget.org/packages/KubernetesClient.StrategicPatch)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

Kubernetes [Strategic Merge Patch (SMP)][smp-design] for .NET 10. Pairs with
[`KubernetesClient`][kubernetes-client], generating and applying the same patch
shape that `kubectl apply` and `kubectl patch --type=strategic` produce — with
full parity against the Go reference (`k8s.io/apimachinery/pkg/util/strategicpatch`).

[smp-design]: https://git.k8s.io/design-proposals-archive/cli/preserve-order-in-strategic-merge-patch.md
[kubernetes-client]: https://github.com/kubernetes-client/csharp

## Why

Plain JSON Merge Patch (RFC 7396) cannot express "merge a single container by
its `name` while leaving the others alone" — you have to either send the whole
list (clobbering anything an admission controller injected) or drop down to
JSON Patch (`type=json`) and hand-author a positional path. SMP solves this by
reading the OpenAPI `x-kubernetes-patch-merge-key` / `x-kubernetes-patch-strategy`
extensions to drive list-merge semantics, plus emitting `$setElementOrder`,
`$deleteFromPrimitiveList`, and `$retainKeys` directives so the API server can
reconstruct the intended state without losing server-side-injected fields.

## Install

```sh
dotnet add package KubernetesClient.StrategicPatch
```

The Roslyn source generator (compile-time schema baking) is bundled in the package
and activated automatically — no extra package reference required.

**Debugging / contributing from source:** add `ProjectReference` entries directly to
`src/KubernetesClient.StrategicPatch/KubernetesClient.StrategicPatch.csproj` and, if
you want the generator active, also reference
`src/KubernetesClient.StrategicPatch.SourceGenerators/KubernetesClient.StrategicPatch.SourceGenerators.csproj`
as an analyzer-only reference (`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`).

## Quickstart

```csharp
using k8s;
using k8s.Models;
using KubernetesClient.StrategicPatch;

V1Deployment current = await client.AppsV1.ReadNamespacedDeploymentAsync("api", "default");

var sparseModified = new V1Deployment
{
    ApiVersion = current.ApiVersion,
    Kind = current.Kind,
    Metadata = new V1ObjectMeta { Name = current.Metadata.Name },
    Spec = new V1DeploymentSpec { Replicas = 5 },
};

var result = current.CreateStrategicPatch(sparseModified, new StrategicPatchOptions
{
    IgnoreNullValuesInModified = true,    // sparse modified — preserve admission-controller injections
    EnforceOptimisticConcurrency = true,  // 409 if the resource has drifted since `current` was read
});

// SchemaProvider auto-defaults to EmbeddedSchemaProvider.Shared when omitted —
// no boilerplate. Pass an explicit provider (Composite + custom + Embedded, etc.)
// to override.

if (result.IsEmpty)
{
    return; // no drift — skip the API call
}

await client.AppsV1.PatchNamespacedDeploymentAsync(result.Patch, "api", "default");
```

`StrategicPatchResult` exposes `IsEmpty`, `PayloadBytes`, and the resolved
`GroupVersionKind` for OTel tagging or step-summary reporting.

## Three-way merge

```csharp
var result = original.CreateThreeWayStrategicPatch(modified, current);
```

Throws [`StrategicMergePatchConflictException`][exc] when caller-side and
server-side changes disagree on a field. Set `OverwriteConflicts = true`
to take caller-side unconditionally (mirrors Go's `overwrite` parameter).

[exc]: src/KubernetesClient.StrategicPatch/StrategicMergePatchConflictException.cs

## Apply on the client side

```csharp
V1Deployment merged = original.ApplyStrategicPatch(result);
```

Useful for tests and for client-side simulation of what the server will produce.
The round-trip property `Apply(original, CreateTwoWay(original, modified)) == modified`
is verified across the entire ported test corpus.

## Observability

The library exposes `ActivitySource("KubernetesClient.StrategicPatch")` plus
structured `ILogger` events:

| Span                    | Tags                                                                              |
| ----------------------- | --------------------------------------------------------------------------------- |
| `smp.compute_two_way`   | `smp.gvk`, `smp.empty`, `smp.patch.bytes`, `smp.schema_miss_count`               |
| `smp.compute_three_way` | `smp.gvk`, `smp.empty`, `smp.patch.bytes`, `smp.schema_miss_count`               |
| `smp.apply`             | `smp.gvk`, `smp.patch.bytes`                                                      |

Per-subtree `smp.schema_miss` events fire when no schema metadata is available
for a path (the engine falls back to RFC 7396 for that subtree). At the
boundary, an `Information`-level summary log carries the same fields.

The library is **CI-vendor-agnostic**. GitHub Actions workflow commands
(`::group::`, `::notice::`, `$GITHUB_STEP_SUMMARY`, `Audit-Id` extraction) live
in [`samples/Common/GhaLogger.cs`](samples/Common/GhaLogger.cs) and the runnable
[`samples/EksAutoscaler`](samples/EksAutoscaler/) demo, never in the
strategic-merge code path.

## Schema metadata

`KubernetesClient.StrategicPatch` resolves SMP metadata
(`x-kubernetes-patch-merge-key`, `x-kubernetes-patch-strategy`,
`x-kubernetes-list-type`) via an `ISchemaProvider`:

- **`EmbeddedSchemaProvider.Shared`** — process-wide singleton that reads a
  baked `schemas.json` snapshot (78 GVKs covering apps/batch/networking/rbac/
  policy/autoscaling + core/v1). Auto-defaulted when callers omit
  `options.SchemaProvider`. Re-bake via
  [`scripts/regen-schemas.sh`](scripts/regen-schemas.sh) /
  [`scripts/regen-schemas.ps1`](scripts/regen-schemas.ps1) after bumping the
  K8s OpenAPI pin under `reference/kubernetes/`. When the snapshot doesn't
  cover a path, the engine falls through to RFC 7396 for that subtree and
  records an `smp.schema_miss` activity event.
- **`GeneratedStrategicPatchSchemaProvider`** — emitted by the Roslyn source
  generator (`KubernetesClient.StrategicPatch.SourceGenerators`). Same GVK
  set as the embedded provider but materialised as static C# data — no
  runtime JSON parse, AOT-friendly, structurally verified against the
  embedded snapshot via round-trip test. Drop-in replacement: pass
  `GeneratedStrategicPatchSchemaProvider.Instance` for `SchemaProvider`.
- **`InMemorySchemaProvider`** — for tests and runtime CRD discovery.
- **`CompositeSchemaProvider`** — chains providers; first match wins.

## Kustomize input flow

```csharp
using KubernetesClient.StrategicPatch.Kustomize;

var docs = KustomizeOverlay.LoadAll(File.ReadAllText("overlay.yaml"));
var modified = KustomizeOverlay.Find(
    docs,
    new GroupVersionKind("apps", "v1", "Deployment"),
    @namespace: "default",
    name: "api");

var result = current.CreateStrategicPatch(modified!);
```

`LoadAll` parses multi-document YAML (kustomize-build output) into
`JsonObject`s; `Index` and `Find` locate a specific document by GVK + name
(+ optional namespace) for the typical "diff cluster state vs. overlay" flow.

## Parity with the Go reference

The Go upstream (`k8s.io/apimachinery/pkg/util/strategicpatch` at
[`v0.36.0`](reference/PINNED_COMMIT)) is vendored read-only under
[`reference/k8s.io-apimachinery/`](reference/) as the source of truth. Refresh
process and porting conventions are documented in
[`reference/README.md`](reference/README.md).

| Feature                                      | This library | Go reference |
| -------------------------------------------- | :----------: | :----------: |
| `CreateTwoWayMergePatch`                     |      ✅      |      ✅      |
| `CreateThreeWayMergePatch` (incl. conflicts) |      ✅      |      ✅      |
| `StrategicMergePatch` (apply)                |      ✅      |      ✅      |
| `$patch: replace` / `delete` / `merge`       |      ✅      |      ✅      |
| `$retainKeys`                                |      ✅      |      ✅      |
| `$setElementOrder/<field>`                   |      ✅      |      ✅      |
| `$deleteFromPrimitiveList/<field>`           |      ✅      |      ✅      |
| Object-list merge by `mergeKey`              |      ✅      |      ✅      |
| Primitive-list `set` / `merge`               |      ✅      |      ✅      |
| Atomic lists (no merge strategy)             |      ✅      |      ✅      |
| RFC 7396 fallback on schema miss             |      ✅      |      ✅      |
| Optimistic concurrency injection             |      ✅      |   (manual)   |
| `OverwriteConflicts` (Go's `overwrite`)      |      ✅      |      ✅      |
| Server-side `MergeParallelList` apply        |      ✅      |      ✅      |
| `mergeMap` directive interleaving            |      ✅      |      ✅      |

Documented limitation, shared with the Go reference and RFC 7396: a wire-format
patch cannot distinguish "field is null" from "field is absent." Round-tripping
a `modified` document that asserts `{x: null}` collapses `x` to absent; the test
[`ExplicitNull_InModified_BehavesAsDelete`](tests/KubernetesClient.StrategicPatch.Tests/StrategicMerge/PatchApplyTests.cs)
pins this so a regression would fail loudly.

## Repository layout

```
src/
  KubernetesClient.StrategicPatch/                    # the library (net10.0)
  KubernetesClient.StrategicPatch.SchemaTool/         # build-time CLI: OpenAPI v3 → schemas.json
  KubernetesClient.StrategicPatch.SourceGenerators/   # Roslyn IIncrementalGenerator (netstandard2.0)
samples/
  Common/                                             # GhaLogger (vendor-specific glue)
  EksAutoscaler/                                      # runnable end-to-end demo
tests/
  KubernetesClient.StrategicPatch.Tests/              # MSTest, mirrors src/ layout
  SourceGenerators.Sandbox/                           # classlib with [KubernetesEntity] stubs to
                                                      #   trigger the generator during inner-loop
                                                      #   inspection (see docs/SOURCE_GEN_DEBUGGING.md)
  SourceGenerators.Tests/                             # vanilla CSharpGeneratorDriver harness
reference/
  k8s.io-apimachinery/strategicpatch/                 # vendored Go source of truth (v0.36.0)
  kubernetes/openapi-spec/v3/                         # vendored K8s OpenAPI v3 (v1.36.0)
scripts/
  regen-schemas.sh / .ps1                             # regenerate the embedded schemas snapshot
docs/
  ARCHITECTURE.md                                     # full project map + control-flow walkthrough
  AI_AGENT_CONTEXT.md                                 # AI-agent ramp-up cheat sheet
  SOURCE_GEN_DEBUGGING.md                             # generator iteration loop and diagnostics
```

## Development

```sh
dotnet build
dotnet test
```

CI runs on Linux + Windows with an 80% line-coverage gate.

## License

[Apache-2.0](LICENSE). Vendored Go reference under
[`reference/k8s.io-apimachinery/`](reference/) retains its upstream Apache-2.0
license; see the upstream repository for its `LICENSE` file.
