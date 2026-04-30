# SourceGenerators.Sandbox

Throwaway class library that exists **only to trigger the source generator
on every build** so a contributor can read the emitted `.g.cs` directly
during inner-loop iteration.

It has no runtime code path. It is not referenced by any other project. It
does not run tests. The sandbox csproj sets:

```xml
<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
<CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)$(Configuration)\$(TargetFramework)\generated</CompilerGeneratedFilesOutputPath>
```

So after `dotnet build` of this project, the emitted provider lands at:

```
obj/<config>/<tfm>/generated/KubernetesClient.StrategicPatch.SourceGenerators/
  KubernetesClient.StrategicPatch.SourceGenerators.StrategicMergePatchGenerator/
    GeneratedStrategicPatchSchemaProvider.g.cs
```

## What's in here

`Stubs.cs` carries a few `[KubernetesEntity]` references (`V1Deployment`,
`V1Pod`, `V1ConfigMap`, `V1Service`, `V1Job`, plus one user-defined
`V1Alpha1Widget`). The current generator's emit doesn't depend on consumer
syntax — the schema set is fully determined by the embedded `schemas.json`
— but the sandbox keeps live `[KubernetesEntity]` usage in the
compilation so any future symbol-traversal-based discovery has something
to find.

## Inner-loop workflow

```
edit src/.../SourceGenerators/SourceWriter.cs
  ↓
dotnet build tests/SourceGenerators.Sandbox
  ↓
open obj/.../generated/.../GeneratedStrategicPatchSchemaProvider.g.cs in your editor
  ↓
diff against expectation, iterate
```

Compile errors in generated code surface with the generated file's path,
so jumping to the offending line is direct.

For attaching a debugger, snapshot tests, and the diagnostic catalog, see
[`docs/SOURCE_GEN_DEBUGGING.md`](../../docs/SOURCE_GEN_DEBUGGING.md).

## Related

- [`tests/SourceGenerators.Tests/`](../SourceGenerators.Tests/) — the
  proper test project that drives the generator via `CSharpGeneratorDriver`
  and asserts round-trip parity vs `EmbeddedSchemaProvider`.
- [`src/KubernetesClient.StrategicPatch.SourceGenerators/`](../../src/KubernetesClient.StrategicPatch.SourceGenerators/) —
  the generator under test.
