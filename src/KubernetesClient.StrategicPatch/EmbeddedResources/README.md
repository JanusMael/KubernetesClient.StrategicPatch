# Embedded resources

This directory holds the strategic-merge schema snapshot that the
`EmbeddedSchemaProvider` reads at runtime.

The file `schemas.json` is **generated** by the SchemaTool from one or more
Kubernetes OpenAPI v3 documents. It is intentionally checked in so that:

- A clean clone produces a deterministic NuGet package without requiring network
  access at build time.
- A `git diff` on the snapshot is reviewable when the K8s spec is bumped.

To regenerate after editing inputs (or pulling a new K8s spec), run:

```
scripts/regen-schemas.sh   <openapi-input-1> [<openapi-input-2> ...]   # Linux/macOS
scripts/regen-schemas.ps1  <openapi-input-1> [<openapi-input-2> ...]   # Windows
```

If `schemas.json` is absent, the library still builds and runs — every
`Resolve` call simply falls through to the RFC 7396 schema-miss path.
