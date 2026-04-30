#!/usr/bin/env bash
# Refreshes the vendored Kubernetes OpenAPI v3 spec under
# reference/kubernetes/openapi-spec/v3/ to a new pin.
#
# Usage:
#   scripts/refresh-k8s-openapi.sh v1.37.0
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <kubernetes-tag>   (e.g. v1.37.0)" >&2
  exit 64
fi

tag=$1
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." &>/dev/null && pwd)
out="$repo_root/reference/kubernetes/openapi-spec/v3"

# Resolve the tag to a commit SHA. Annotated tags need a second hop.
tag_obj=$(curl -sf "https://api.github.com/repos/kubernetes/kubernetes/git/refs/tags/$tag" \
  | python -c 'import json,sys;d=json.load(sys.stdin);print(d["object"]["sha"], d["object"]["type"])')
read -r sha kind <<<"$tag_obj"
if [[ "$kind" == "tag" ]]; then
  sha=$(curl -sf "https://api.github.com/repos/kubernetes/kubernetes/git/tags/$sha" \
    | python -c 'import json,sys;print(json.load(sys.stdin)["object"]["sha"])')
fi
echo "Refreshing K8s OpenAPI to $tag ($sha)"

mkdir -p "$out"

# Set of OpenAPI files for the API groups deployment projects patch.
files=(
  "api__v1_openapi.json"
  "apis__apps__v1_openapi.json"
  "apis__batch__v1_openapi.json"
  "apis__networking.k8s.io__v1_openapi.json"
  "apis__rbac.authorization.k8s.io__v1_openapi.json"
  "apis__policy__v1_openapi.json"
  "apis__autoscaling__v2_openapi.json"
)

for f in "${files[@]}"; do
  echo "  fetching $f"
  curl -sf "https://raw.githubusercontent.com/kubernetes/kubernetes/$sha/api/openapi-spec/v3/$f" \
    -o "$out/$f"
done

# Update the pin file.
cat > "$repo_root/reference/kubernetes/PINNED_VERSION" <<EOF
repo:    github.com/kubernetes/kubernetes
tag:     $tag
commit:  $sha
fetched: $(date -u +%Y-%m-%d)
license: Apache-2.0

Vendored API groups (file names are the canonical OpenAPI v3 split):
$(for f in "${files[@]}"; do printf '  %s\n' "$f"; done)

Refresh: scripts/refresh-k8s-openapi.sh   (re-fetch and update this file)
EOF

echo "Done. Re-bake schemas.json next:"
echo "  scripts/regen-schemas.sh"
