# Contributing to KubernetesClient.StrategicPatch

Thank you for your interest in contributing! This library is maintained by
[Brian Bennewitz](https://github.com/JanusMael). All contributors are expected
to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Getting started

**Prerequisites:** .NET 10 SDK

```sh
git clone https://github.com/JanusMael/KubernetesClient.StrategicPatch.git
cd KubernetesClient.StrategicPatch
dotnet restore
dotnet build
dotnet test
```

### Regenerate the embedded schema snapshot

If you update the OpenAPI spec or the SchemaTool, regenerate the embedded
`schemas.json` before submitting a PR:

```sh
# Linux / macOS
scripts/regen-schemas.sh

# Windows
scripts/regen-schemas.ps1
```

## Submitting a pull request

1. **Branch** from `main` — pick a short, descriptive branch name.
2. **Keep commits focused.** One logical change per commit; conventional commit
   prefixes (`fix:`, `feat:`, `docs:`, `refactor:`, `test:`) are appreciated
   but not required.
3. **CI must pass.** Both the Linux and Windows matrix jobs must be green,
   including the 80% line-coverage gate.
4. **Update `CHANGELOG.md`** under the `[Unreleased]` section with a one-line
   summary of your change.
5. Open a PR against `main` and fill in the PR template.

All PRs are reviewed by the maintainer. Please be patient — this is a
side-project maintained in spare time.

## Reporting bugs

Use the [bug report issue template](https://github.com/JanusMael/KubernetesClient.StrategicPatch/issues/new?template=bug_report.yml).
Include a minimal, self-contained repro whenever possible.

## Requesting features

Use the [feature request issue template](https://github.com/JanusMael/KubernetesClient.StrategicPatch/issues/new?template=feature_request.yml).

## NuGet publish

Publishing to NuGet.org is gated on the maintainer configuring the
`NUGET_API_KEY` secret in the repository. The release workflow already contains
the commented-out publish step — no code change is needed to enable it.

## License

By contributing you agree that your work will be licensed under the same
[Apache-2.0](LICENSE) license that covers the project.
