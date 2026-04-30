namespace KubernetesClient.StrategicPatch.Tests.SchemaTool;

/// <summary>
/// Inline OpenAPI v3 fixtures that exercise the SchemaTool walker.
/// Hand-authored to mirror the shape of the real Kubernetes spec
/// (apps/v1 Deployment + dependencies) without vendoring 1MB of JSON.
/// </summary>
internal static class SchemaToolFixtures
{
    /// <summary>
    /// Slice of the K8s OpenAPI spec covering apps/v1 Deployment down through Container,
    /// with the strategic-merge annotations on .spec.template.spec.containers (and a few
    /// other lists) intact. The schema names match the real spec so $ref strings look
    /// natural to anyone familiar with Kubernetes.
    /// </summary>
    public const string DeploymentSlice = """
        {
          "openapi": "3.0.0",
          "components": {
            "schemas": {
              "io.k8s.api.apps.v1.Deployment": {
                "type": "object",
                "x-kubernetes-group-version-kind": [
                  { "group": "apps", "version": "v1", "kind": "Deployment" }
                ],
                "properties": {
                  "apiVersion": { "type": "string" },
                  "kind": { "type": "string" },
                  "metadata": { "$ref": "#/components/schemas/io.k8s.apimachinery.pkg.apis.meta.v1.ObjectMeta" },
                  "spec": { "$ref": "#/components/schemas/io.k8s.api.apps.v1.DeploymentSpec" }
                }
              },
              "io.k8s.api.apps.v1.DeploymentSpec": {
                "type": "object",
                "properties": {
                  "replicas": { "type": "integer" },
                  "template": { "$ref": "#/components/schemas/io.k8s.api.core.v1.PodTemplateSpec" }
                }
              },
              "io.k8s.api.core.v1.PodTemplateSpec": {
                "type": "object",
                "properties": {
                  "metadata": { "$ref": "#/components/schemas/io.k8s.apimachinery.pkg.apis.meta.v1.ObjectMeta" },
                  "spec": { "$ref": "#/components/schemas/io.k8s.api.core.v1.PodSpec" }
                }
              },
              "io.k8s.api.core.v1.PodSpec": {
                "type": "object",
                "properties": {
                  "containers": {
                    "type": "array",
                    "items": { "$ref": "#/components/schemas/io.k8s.api.core.v1.Container" },
                    "x-kubernetes-patch-merge-key": "name",
                    "x-kubernetes-patch-strategy": "merge",
                    "x-kubernetes-list-type": "map"
                  },
                  "imagePullSecrets": {
                    "type": "array",
                    "items": { "$ref": "#/components/schemas/io.k8s.api.core.v1.LocalObjectReference" },
                    "x-kubernetes-patch-merge-key": "name",
                    "x-kubernetes-patch-strategy": "merge"
                  },
                  "finalizers": {
                    "type": "array",
                    "items": { "type": "string" },
                    "x-kubernetes-patch-strategy": "merge",
                    "x-kubernetes-list-type": "set"
                  }
                }
              },
              "io.k8s.api.core.v1.Container": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "image": { "type": "string" },
                  "ports": {
                    "type": "array",
                    "items": { "$ref": "#/components/schemas/io.k8s.api.core.v1.ContainerPort" },
                    "x-kubernetes-patch-merge-key": "containerPort",
                    "x-kubernetes-patch-strategy": "merge",
                    "x-kubernetes-list-type": "map"
                  },
                  "env": {
                    "type": "array",
                    "items": { "$ref": "#/components/schemas/io.k8s.api.core.v1.EnvVar" },
                    "x-kubernetes-patch-merge-key": "name",
                    "x-kubernetes-patch-strategy": "merge"
                  }
                }
              },
              "io.k8s.api.core.v1.ContainerPort": {
                "type": "object",
                "properties": {
                  "containerPort": { "type": "integer" },
                  "protocol": { "type": "string" },
                  "name": { "type": "string" }
                }
              },
              "io.k8s.api.core.v1.EnvVar": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "value": { "type": "string" }
                }
              },
              "io.k8s.api.core.v1.LocalObjectReference": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" }
                }
              },
              "io.k8s.apimachinery.pkg.apis.meta.v1.ObjectMeta": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "namespace": { "type": "string" },
                  "labels": {
                    "type": "object",
                    "additionalProperties": { "type": "string" }
                  },
                  "ownerReferences": {
                    "type": "array",
                    "items": { "$ref": "#/components/schemas/io.k8s.apimachinery.pkg.apis.meta.v1.OwnerReference" },
                    "x-kubernetes-patch-merge-key": "uid",
                    "x-kubernetes-patch-strategy": "merge"
                  }
                }
              },
              "io.k8s.apimachinery.pkg.apis.meta.v1.OwnerReference": {
                "type": "object",
                "properties": {
                  "uid": { "type": "string" },
                  "kind": { "type": "string" }
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Self-referential schema (a tree node) used to verify the cycle guard does not stack-overflow.
    /// </summary>
    public const string CyclicTree = """
        {
          "openapi": "3.0.0",
          "components": {
            "schemas": {
              "demo.v1.TreeNode": {
                "type": "object",
                "x-kubernetes-group-version-kind": [
                  { "group": "demo", "version": "v1", "kind": "TreeNode" }
                ],
                "properties": {
                  "value": { "type": "string" },
                  "child": { "$ref": "#/components/schemas/demo.v1.TreeNode" }
                }
              }
            }
          }
        }
        """;
}
