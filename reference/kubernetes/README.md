# Vendored Kubernetes OpenAPI v3 spec subset

The files under [`openapi-spec/v3/`](openapi-spec/v3/) are a pinned snapshot
of the Kubernetes OpenAPI v3 spec for the API groups the host's deployment
projects patch. They are the input to the `SchemaTool`, which produces the
embedded `schemas.json` artifact under
`src/KubernetesClient.StrategicPatch/EmbeddedResources/`.

The pin is recorded in [`PINNED_VERSION`](PINNED_VERSION).

The OpenAPI files retain their upstream Apache-2.0 license. They are never
compiled — they are read-only build inputs for the schema baker.

## Refresh

When a new Kubernetes version is adopted host-side:

```sh
scripts/refresh-k8s-openapi.sh v1.37.0     # tag name
scripts/regen-schemas.sh                   # re-bake schemas.json
git diff src/KubernetesClient.StrategicPatch/EmbeddedResources/schemas.json
git add reference/kubernetes/ src/KubernetesClient.StrategicPatch/EmbeddedResources/schemas.json
git commit -m "chore: bump K8s OpenAPI to v1.37.0"
```

## Why this set

The vendored API groups match the resources the host's deployment projects
actually patch:

| Group                          | Examples                                                                             |
| ------------------------------ | ------------------------------------------------------------------------------------ |
| `core/v1`                      | Pod, Service, ConfigMap, Secret, ServiceAccount, Namespace, PersistentVolume(Claim) |
| `apps/v1`                      | Deployment, StatefulSet, DaemonSet, ReplicaSet                                       |
| `batch/v1`                     | Job, CronJob                                                                         |
| `networking.k8s.io/v1`         | Ingress, NetworkPolicy, IngressClass                                                 |
| `rbac.authorization.k8s.io/v1` | Role, RoleBinding, ClusterRole, ClusterRoleBinding                                   |
| `policy/v1`                    | PodDisruptionBudget                                                                  |
| `autoscaling/v2`               | HorizontalPodAutoscaler                                                              |

Adding a new group is just: drop the file into `openapi-spec/v3/`, rerun
`regen-schemas.sh`, and commit the result.
