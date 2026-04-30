# Vendored upstream reference

The Go strategic-merge-patch reference is the source of truth for algorithmic
parity. The files under `k8s.io-apimachinery/strategicpatch/` are a
**read-only snapshot** of the upstream package at a pinned commit (recorded in
[PINNED_COMMIT](PINNED_COMMIT)). They are never compiled or executed in this
repo — they exist solely as documentation we port from.

Everything in `k8s.io-apimachinery/` retains the upstream Apache-2.0 license
(see the original repository for the LICENSE file).

## Refresh process

When bumping the pin:

1. Pick the new tag, e.g. `v0.37.0`.
2. Resolve to its commit SHA (annotated tags require dereferencing):

   ```bash
   gh api repos/kubernetes/apimachinery/git/refs/tags/v0.37.0 --jq .object.sha \
     | xargs -I{} gh api repos/kubernetes/apimachinery/git/tags/{} --jq .object.sha
   ```

3. Re-fetch the package files at that SHA:

   ```bash
   commit=<sha>
   for f in OWNERS errors.go meta.go patch.go patch_test.go types.go; do
     curl -sf "https://raw.githubusercontent.com/kubernetes/apimachinery/$commit/pkg/util/strategicpatch/$f" \
       -o "reference/k8s.io-apimachinery/strategicpatch/$f"
   done
   ```

4. `git diff` the result. Any new test cases added upstream get ported into
   the C# corpus (tagged with `[TestCategory("go:OriginalTestName")]`).
5. Update [PINNED_COMMIT](PINNED_COMMIT) and commit alongside the test ports.
