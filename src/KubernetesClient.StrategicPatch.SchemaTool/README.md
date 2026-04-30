# KubernetesClient.StrategicPatch.SchemaTool

Build-time CLI that ingests Kubernetes OpenAPI v3 documents and emits the
minified `schemas.json` snapshot the runtime library and source generator
both consume.

This tool is invoked by maintainers when the Kubernetes OpenAPI pin
(`reference/kubernetes/PINNED_VERSION`) changes — not on every consumer
build. The resulting `schemas.json` is committed to the repo under
`src/KubernetesClient.StrategicPatch/EmbeddedResources/schemas.json` and
mirrored into the source-generator assembly via a linked file.

## Usage

```
SchemaTool <output-schemas.json> <openapi-v3-input>...
```

End-to-end via the convenience scripts (preferred):

```sh
# from repo root
./scripts/regen-schemas.sh    # POSIX
./scripts/regen-schemas.ps1   # Windows
```

Both scripts run the tool over every spec under
`reference/kubernetes/openapi-spec/v3/` and write the result to the
embedded-resource path. They also copy the snapshot into the source
generator project so the two stay in lockstep.

## What it does

1. Parses each OpenAPI v3 document into a schema graph.
2. For every type carrying `x-kubernetes-group-version-kind`, walks the
   property tree following `$ref` (with a `HashSet<string>` cycle guard).
3. Unwraps single-element `allOf` wrappers around `$ref` (the K8s OpenAPI
   shape) so vendored specs bake correctly.
4. Captures `x-kubernetes-patch-merge-key`, `x-kubernetes-patch-strategy`,
   `x-kubernetes-list-type`, and `x-kubernetes-list-map-keys` per field.
5. Serialises the full GVK→tree dictionary via
   `Internal/SchemaWireFormat`'s minified single-letter-key format.

## Exit codes

- `0` — success
- `64` (`EX_USAGE`) — missing arguments
- `66` (`EX_NOINPUT`) — input file missing
- `70` (`EX_SOFTWARE`) — unexpected failure

Errors are emitted as `::error::` workflow commands so CI surfaces them in
the GitHub Actions UI without a separate parser.

## Wire format

Defined in
[`src/KubernetesClient.StrategicPatch/Internal/SchemaWireFormat.cs`](../KubernetesClient.StrategicPatch/Internal/SchemaWireFormat.cs).
The `CurrentVersion` constant is read by both the runtime
`EmbeddedSchemaProvider` and the Roslyn generator to detect a stale
`schemas.json`. Bump it when the on-disk format changes; consumers will be
forced to regenerate.

## Related

- [`scripts/regen-schemas.{sh,ps1}`](../../scripts/) — full refresh flow.
- [`reference/kubernetes/`](../../reference/kubernetes/) — vendored OpenAPI
  v3 inputs and `PINNED_VERSION`.
- [`src/KubernetesClient.StrategicPatch.SourceGenerators/`](../KubernetesClient.StrategicPatch.SourceGenerators/) —
  the consumer-side generator that emits a static-data provider from the
  same snapshot.
