# Releasing

Releases are cut by pushing a `vX.Y.Z` tag. The [release workflow](../.github/workflows/release.yml)
takes it from there: it builds the native plugins (via the same reusable workflow CI uses),
asserts the native-asset set is complete, runs the test suite, then packs the NuGet packages
(`Cs2VideoGenerator.Core` and the `Cs2VideoGenerator.Cli` dotnet tool) and publishes them to
[NuGet.org](https://www.nuget.org/packages/Cs2VideoGenerator.Cli). It also publishes
self-contained CLI archives (`csvg-<version>-win-x64.zip`, `csvg-<version>-linux-x64.tar.gz`)
and creates a GitHub Release whose body is the CHANGELOG section for that version, with the
nupkg/snupkg and CLI archives attached.

Publishing uses **NuGet.org Trusted Publishing** (OIDC): the workflow's `id-token` is exchanged
for a short-lived push key via the `NuGet/login` action, so there is no nuget.org API key or
repository secret to store. One-time setup: on NuGet.org (account `sid2934`) add a Trusted
Publishing policy bound to repository `CS2OpenDev/CS2VideoGenerator` and workflow file
`release.yml`. On the first successful push the (currently unclaimed) package IDs are created
under that account.

## Versioning model

Versions come from [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning)
(`version.json` at the repo root). On `main`, `version.json` carries a prerelease version (e.g.
`1.0.2-alpha.{height}`). Every main build therefore produces a unique prerelease package that
can never collide with a published release.

> **Important caveat:** pushing a `v*` tag does **not** by itself produce a clean `X.Y.Z`
> package. NBGV computes the version from the **tagged commit's** `version.json`; the tag only
> marks the ref as a public release (dropping the `-gXXXX` git suffix). If the tagged commit's
> `version.json` still says `1.0.1-alpha.{height}`, a `v1.0.1` tag yields `1.0.1-alpha.N`
> packages. The release workflow guards this: it fails if the computed package version does not
> equal the tag's version.

## Release steps

1. **Update the changelog.** Rename the `## [Unreleased]` section of `CHANGELOG.md` to
   `## [X.Y.Z] - YYYY-MM-DD`, and add the compare/tag link at the bottom. The workflow
   extracts exactly this section as the GitHub Release body.

2. **Set the release version.** Run the NBGV release flow from the repo root:

   ```bash
   nbgv prepare-release   # dotnet tool install -g nbgv
   ```

   That creates a release branch whose `version.json` is set to the clean `X.Y.Z` and bumps
   `main` to the next `-alpha` prerelease. (Manual alternative: edit `version.json` to
   `"version": "X.Y.Z"` in a PR targeting the release ref, and in a follow-up bump `main` to
   `X.Y.(Z+1)-alpha.{height}`.)

3. **Merge and verify.** Land the release-version commit, changelog rename included, and
   confirm CI is green on it. Then check the version locally:

   ```bash
   git checkout <release-commit>
   nbgv get-version -v NuGetPackageVersion   # must print X.Y.Z exactly
   ```

4. **Tag the prepared commit and push:**

   ```bash
   git tag vX.Y.Z <release-commit>
   git push origin vX.Y.Z
   ```

   The workflow does the rest. It publishes to NuGet.org via Trusted Publishing (the workflow
   grants itself `id-token: write` and mints a short-lived key — no repository secret to
   configure). Pushes use `--skip-duplicate`, so re-running the workflow after a partial
   failure is safe.

   The last step before anything is published scans every packed artifact for absolute build
   paths and fails the release if it finds one — so a package can never ship the build
   machine's directory layout again. See `docs/build-path-privacy.md`.

5. **Check the results:** the GitHub Release (nupkg/snupkg + CLI archives attached, changelog
   body) and the published package versions on
   [NuGet.org](https://www.nuget.org/packages/Cs2VideoGenerator.Cli).

## Dry run

Trigger the release workflow manually (`workflow_dispatch` on the Actions tab, or
`gh workflow run release.yml`). A manual run builds and uploads the complete artifact set
(NuGet packages + CLI archives, as the `release-artifacts` workflow artifact) but skips the
NuGet.org push and the GitHub Release.

Off-tag dry runs from a branch that is **not** a public-release ref produce prerelease-suffixed
versions, and warn that no matching CHANGELOG section exists — both expected. Dispatching from
`main` stamps a clean `X.Y.Z` when `version.json` carries a release version, since `main` is a
public-release ref.

## What ships where

Both nupkgs — `Cs2VideoGenerator.Core` and the `Cs2VideoGenerator.Cli` dotnet tool, which
provides the `csvg` command — are published to NuGet.org and also attached to the GitHub
Release. The `Core` snupkg (symbols) is published to NuGet.org's symbol server (and rides along
on the Release). The CLI archives are Release-only: `csvg-<version>-win-x64.zip` and
`csvg-<version>-linux-x64.tar.gz`, self-contained, no .NET runtime required.

## Consuming the packages

The packages are on NuGet.org, so no source configuration or authentication is needed. Install
the CLI tool:

```bash
dotnet tool install -g Cs2VideoGenerator.Cli
```

Or reference `Cs2VideoGenerator.Core` as a normal NuGet dependency. Every release also attaches
the raw `.nupkg` and the self-contained CLI archives to its **GitHub Release** for direct
download.

The CLI archives are self-contained **folder** archives (a `csvg` executable next to its
support files), not single-file bundles: `Cs2VideoGenerator.Core`'s `NativeAssetProvider`
resolves the bundled `runtimes/<rid>/native/` plugin assets relative to `Assembly.Location`,
which is empty inside a single-file bundle. Keep the archive layout (`csvg`,
`appsettings.json`, `runtimes/<rid>/native/`) aligned with what that code probes.

## The macOS mock is a committed prebuilt (not a CI build product)

Every native asset is built from source in CI (`build-native-plugins.yml`) **except one**: the
`osx-arm64` `mock_server`. CS2 does not run on macOS, so there is no macOS server plugin to
build, and GitHub's macOS runners are ~10× the cost of Linux runners for a binary that only
lets Apple Silicon developers exercise `--mock` mode. So it is built **locally on an Apple
Silicon Mac** and committed to the repo at
`plugin/Cs2VideoGenerator.Plugin.Grpc/prebuilt/osx-arm64/mock_server`.

`.github/actions/setup-native-assets` folds that committed binary into the native-asset layout
alongside the CI-built `win-x64`/`linux-x64` artifacts, so `Cs2VideoGenerator.Core` packs it
into `runtimes/osx-arm64/native/` with no csproj change (the pack glob is RID-agnostic). The
completeness gate treats it as required, so an accidentally-deleted prebuilt fails the release
rather than silently shipping a macOS package without its mock.

**Refresh it whenever the mock engine or its wire protocol changes** (otherwise the committed
binary drifts from the current `.proto`/mock sources). Nothing warns you when it has drifted:
the test suite runs the freshly built mock rather than this one. It was last refreshed for
1.1.2, against the 2.0 wire protocol.

On an Apple Silicon Mac, run
`plugin/Cs2VideoGenerator.Plugin.Grpc/scripts/refresh-macos-mock.sh`, then commit the updated
binary. The script builds the `mock_server` target with the `release-unix` preset, strips it,
re-signs it ad-hoc (a stripped arm64 binary with an invalid signature is `SIGKILL`ed by macOS),
and stages it to the tracked path.

### Verify the refreshed binary before you tag

CI cannot do this for you. Its only contact with the committed binary is the `path-privacy`
strings scan and the existence check in `setup-native-assets` — together those prove the binary
leaks no build paths and is a file, not that it can answer the current protocol. That is how
1.1.0 shipped green with a mock that could not.

Point the mock end-to-end tests at the **committed** binary rather than the one the build just
produced:

```bash
CSVG_MOCK_SERVER_PATH="$PWD/plugin/Cs2VideoGenerator.Plugin.Grpc/prebuilt/osx-arm64/mock_server" \
  dotnet test test/Cs2VideoGenerator.Core.Tests/Cs2VideoGenerator.Core.Tests.csproj \
    --filter "FullyQualifiedName~MockModeEndToEnd"
```

These are the only tests that exercise the committed artifact, and they are sensitive to
protocol drift: run against the 1.1.0 binary they fail 2 of 3, and against the 1.1.1 refresh
they pass 3 of 3. A green run here is what backs the claim that the shipped macOS mock speaks
the current wire format.

### Why shipping a locally-built binary is safe

A binary built on a developer's machine can carry that machine's filesystem layout. Compilers
expand `__FILE__` into string literals, so an unmapped build embeds the absolute path of every
source file it compiled. The shipped 1.0.0 macOS mock contained 404 of them. Three things
prevent a repeat, and none depend on the person running the refresh:

1. `cmake/path-privacy.cmake` maps those paths away at compile time, for our own sources
   and for every vcpkg dependency, so a correct build has nothing to leak.
2. `refresh-macos-mock.sh` refuses to run from a linked git worktree (whose path would be
   baked in and is not covered by the maps), and scans the binary before staging it.
3. CI's `path-privacy` job re-scans the committed prebuilt on every push and PR. A binary
   carrying anyone's home directory cannot reach `main`, however it was produced.

The release workflow runs the same scan over the packed `.nupkg`s and CLI archives before
anything is pushed. See `docs/build-path-privacy.md`.
