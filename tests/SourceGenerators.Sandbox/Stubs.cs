// Stub types annotated with [KubernetesEntity] so the source generator has something to scan.
// Mirrors what a deployment project's K8s model surface would look like:
// a handful of types, identified by group/version/kind, that the generator should bake schema
// providers for.
//
// This file deliberately does not import any of the host's K8s model classes — those live
// in KubernetesClient.dll, which the runtime library already references. We just want a
// minimal compilation that exercises the generator's discovery path.

using k8s;
using k8s.Models;

namespace KubernetesClient.StrategicPatch.SourceGenerators.Sandbox;

// References to a couple of K8s model types so the compilation has live [KubernetesEntity]
// usages for the generator to find via Roslyn symbol traversal.
internal static class StubReferences
{
    public static V1Deployment Deployment() => new();
    public static V1Pod Pod() => new();
    public static V1ConfigMap ConfigMap() => new();
    public static V1Service Service() => new();
    public static V1Job Job() => new();
}

// A custom annotated type would also be a target. Today the generator scope is built-ins only,
// but the discovery code path will likely also pick this up. Pinned here so the sandbox covers
// both the "known model" and "user-defined-but-built-in-shape" cases.
[KubernetesEntity(Group = "example.com", ApiVersion = "v1alpha1", Kind = "Widget", PluralName = "widgets")]
public sealed class V1Alpha1Widget : IKubernetesObject<V1ObjectMeta>
{
    public string ApiVersion { get; set; } = "example.com/v1alpha1";
    public string Kind { get; set; } = "Widget";
    public V1ObjectMeta Metadata { get; set; } = new();
}
