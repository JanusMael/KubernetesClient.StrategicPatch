#!/usr/bin/env bash
# Regenerates src/KubernetesClient.StrategicPatch/EmbeddedResources/schemas.json
# from one or more OpenAPI v3 input documents.
#
# With no arguments, defaults to every vendored .json file under
# reference/kubernetes/openapi-spec/v3/. Pass explicit paths to override.
#
# Usage:
#   scripts/regen-schemas.sh                            # use vendored inputs
#   scripts/regen-schemas.sh /path/to/extra.json ...    # explicit overrides
set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." &>/dev/null && pwd)
out="$repo_root/src/KubernetesClient.StrategicPatch/EmbeddedResources/schemas.json"

if [[ $# -eq 0 ]]; then
  vendored_dir="$repo_root/reference/kubernetes/openapi-spec/v3"
  if [[ ! -d "$vendored_dir" ]]; then
    echo "no inputs given and $vendored_dir is missing; run scripts/refresh-k8s-openapi.sh first" >&2
    exit 64
  fi
  mapfile -t inputs < <(find "$vendored_dir" -maxdepth 1 -type f -name '*.json' | sort)
  if [[ ${#inputs[@]} -eq 0 ]]; then
    echo "no .json files under $vendored_dir" >&2
    exit 64
  fi
else
  inputs=("$@")
fi

echo "Baking schemas.json from ${#inputs[@]} input(s):"
for f in "${inputs[@]}"; do echo "  $f"; done

dotnet run --project "$repo_root/src/KubernetesClient.StrategicPatch.SchemaTool" \
  --configuration Release \
  -- "$out" "${inputs[@]}"

echo "Wrote $out"
