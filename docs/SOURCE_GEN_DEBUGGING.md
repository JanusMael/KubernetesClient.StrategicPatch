# Debugging the strategic-merge-patch source generator

The Roslyn source generator runs inside the C# compiler. When it works, you
get free schema baking; when it doesn't, the failure modes are notoriously
opaque. This page collects the iteration-loop hacks that make Stage 13
development bearable.

## See what the generator actually produced

Add to the consuming project's `.csproj`:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)$(Configuration)\$(TargetFramework)\generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
```

After `dotnet build`, generated files land under
`obj/<config>/<tfm>/generated/<assembly>/<generator-fully-qualified-name>/...`.
Open them in your editor. Compile errors in generated code surface with the
generated file's path, so jumping is direct.

## The fast inner loop

```text
edit generator → save
  ↓
build the sandbox project (tests/.../SourceGenerators.Sandbox)
  ↓
inspect obj/<config>/<tfm>/generated/.../GeneratedStrategicPatchSchemaProvider.g.cs
  ↓
diff against your expectation; iterate
```

The sandbox project is a class library that ProjectReferences the runtime lib
and contains a few `[KubernetesEntity]`-annotated stubs. It exists *only* to
trigger the generator; it has no runtime code path.

## Diagnostics the generator should emit

Pre-allocated in [`Diagnostics/SmpDiagnostics.cs`](../src/KubernetesClient.StrategicPatch/Diagnostics/SmpDiagnostics.cs):

| ID     | Severity | When to fire                                                           |
| ------ | -------- | ---------------------------------------------------------------------- |
| SMP001 | Info     | Once per generator run, summarising baked GVK count + wire format hash |
| SMP002 | Warning  | Type referenced by patch APIs without `[KubernetesEntity]`             |
| SMP003 | Warning  | GVK requested but absent from the snapshot                             |
| SMP004 | Error    | `schemas.json` wire format version doesn't match the library           |
| SMP005 | Error    | Catch-all for unexpected generator failures (with type + message)      |

The generator project converts each `SmpDescriptor` into a
`Microsoft.CodeAnalysis.DiagnosticDescriptor` (the runtime lib does *not*
take that dependency).

## Attaching a debugger

Source generators run inside `csc.exe` (CLI) or the IDE's compilation
process. Two practical approaches:

1. **Attach by process name** in Visual Studio / Rider: pick the IDE's
   compiler-host process, hit a breakpoint inside the generator. Works after
   the project has been loaded.

2. **`Debugger.Launch()` on entry** for stubborn cases:
   ```csharp
   public sealed class StrategicMergePatchGenerator : IIncrementalGenerator
   {
       public void Initialize(IncrementalGeneratorInitializationContext context)
       {
   #if DEBUG
           if (!System.Diagnostics.Debugger.IsAttached)
           {
               System.Diagnostics.Debugger.Launch();
           }
   #endif
           // ...
       }
   }
   ```
   Builds will pause asking for a debugger. Disable when not actively iterating.

## Snapshot tests

> **Why we don't use the canonical `Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing.MSTest`
> harness.** The latest stable release (1.1.2) hard-pins
> `Microsoft.CodeAnalysis` to **3.8.0** transitively, which predates
> `IIncrementalGenerator` (introduced in Roslyn 4.0). The package family
> hasn't shipped a 4.x update in over a year. The community has moved to a
> two-layer pattern:
> 1. Vanilla `CSharpGeneratorDriver` for the actual driving — what
>    [`tests/.../SourceGenerators.Tests/HarnessSmokeTests.cs`](../tests/SourceGenerators.Tests/HarnessSmokeTests.cs)
>    demonstrates today.
> 2. Optionally `Verify.SourceGenerators` (NuGet 2.5+) layered on top once
>    generated output gets large enough that inline string assertions are
>    unwieldy. Verify provides snapshot persistence, drift detection, and
>    nicer diffs.

The shipped harness in `tests/SourceGenerators.Tests/` uses pattern 1:

```csharp
var input = CSharpCompilation.Create(
    "ConsumerAssembly",
    new[] { CSharpSyntaxTree.ParseText("...consumer source...") },
    new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

var driver = CSharpGeneratorDriver.Create(new StrategicMergePatchGenerator());
driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
    input, out var output, out var diagnostics);

var generated = driver.GetRunResult().Results
    .SelectMany(r => r.GeneratedSources)
    .ToImmutableArray();

Assert.AreEqual("GeneratedStrategicPatchSchemaProvider.g.cs", generated[0].HintName);
StringAssert.Contains(generated[0].SourceText.ToString(), "apps/v1/Deployment");
```

When/if the generator emits dozens of files per scenario, swap the manual
`StringAssert` checks for a single `await Verifier.Verify(driver);` —
snapshots land in `tests/.../*.received.txt` and the test fails on diff.

## Inspecting a provider's content at runtime

For "is the generated provider returning what I expect?" sanity checks:

```csharp
SchemaProviderDebug.DumpEmbedded(GeneratedStrategicPatchSchemaProvider.Instance, Console.Out);
```

The `[Conditional("DEBUG")]` gate means the call evaporates in Release, so
ship-it code can keep the dump call as living documentation without paying
runtime cost.

## Comparing generated vs embedded

The pre-Roslyn round-trip test
([`PreRoslynAuditTests.Finding6_*`](../tests/KubernetesClient.StrategicPatch.Tests/PreRoslynAuditTests.cs))
proves that walking a `SchemaNode` tree and rebuilding it via `SchemaBuilder`
produces a structurally-equal tree. Once the generator lands, an additional
test asserts:

```text
SchemaNode.StructuralEquals(
    EmbeddedSchemaProvider.Shared.GetRootSchema(gvk),
    GeneratedStrategicPatchSchemaProvider.Instance.GetRootSchema(gvk))
```

for every GVK in the snapshot. Any divergence means the generator emitted
code that produces a different tree than the wire-format-loaded version.

## Manifest checks at runtime

The generator emits a `GenerationManifest` field. Consumers can assert:

```csharp
Assert.AreEqual(
    expected: KnownGoodManifestHash,
    actual: GeneratedStrategicPatchSchemaProvider.Instance.Manifest.SnapshotContentHash);
```

CI can compare the manifest hash to the hash of the current
`schemas.json` to detect "your generated provider is stale; re-build."
