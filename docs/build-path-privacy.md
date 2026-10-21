# Build-path privacy

Shipped artifacts must not disclose the filesystem layout of the machine that built
them. 1.0.0 did.

Origin: an upstream report from DemoViewer.NET (2026-07-27), which consumes
`Cs2VideoGenerator.Core` and found the maintainer's home directory inside the macOS
native while auditing its own installers before a public release.

---

## 1. What leaked in 1.0.0

Measured on `Cs2VideoGenerator.Core.1.0.0.nupkg` as downloaded from the `v1.0.0` release
(count = occurrences of the byte string, not distinct paths):

| Artifact | `/Users/` | `/home/` |
|---|---|---|
| `runtimes/osx-arm64/native/mock_server` | **404** | 0 |
| `runtimes/win-x64/native/mock_server.exe` | 0 | 396 |
| `runtimes/win-x64/native/server.dll` | 0 | 396 |
| `runtimes/linux-x64/native/mock_server` | 0 | 412 |
| `runtimes/linux-x64/native/server.so` | 0 | 413 |
| `lib/net10.0/Cs2VideoGenerator.Core.dll` | 0 | 1 |

Only the macOS binary was maintainer-identifying. It is the one native that is **not** a
CI build product (see [RELEASING.md](RELEASING.md)), and ~96% of its paths ran through a
linked git worktree under a scratch directory in the checkout — a local path with no
business being in a shipped binary. The other RIDs carried `/home/runner/…`, anonymous
GitHub runner paths. Still build-machine layout, and still fixed below.

## 2. Five distinct mechanisms, five fixes

Only the first is the one people expect. A fix that stops there leaves the rest.

### 2.1 `__FILE__` expansion — `cmake/path-privacy.cmake`

gRPC, abseil and OpenSSL expand `__FILE__` in every assertion macro, so each compiled
source file contributes its absolute path as a `__TEXT`/`.rdata` string literal. **These
are not debug info: `strip` does not remove them.** The fix is `-ffile-prefix-map`. It
has to reach both our own translation units and every vcpkg port, and three details
matter:

- vcpkg ports build in their own CMake process, so `add_compile_options()` in our
  `CMakeLists.txt` never reaches them. The maps have to come from the triplet
  (`VCPKG_C_FLAGS`/`VCPKG_CXX_FLAGS`), which is why every triplet in `cmake/triplets/`
  includes `path-privacy.cmake`.
- …and the triplet is not enough where a chainload toolchain is in play.
  `VCPKG_CHAINLOAD_TOOLCHAIN_FILE` *replaces* vcpkg's own toolchain, which is the thing
  that would otherwise append `VCPKG_*_FLAGS` — the same trap that forces `/MT` and
  `-fPIC` to be set explicitly in `cmake/toolchains/`. Verified by inspecting a port's
  `CMakeCache.txt`: with the flags only in the triplet, the win-x64 leg received none of
  them. Each cross toolchain now appends the maps to `CMAKE_C/CXX_FLAGS_INIT` itself; the
  triplet keeps its copy as a fallback.
- `clang-cl` **silently ignores** a bare `-ffile-prefix-map`: `warning: unknown argument
  ignored in clang-cl`, exit status unchanged. Forward it as
  `/clang:-ffile-prefix-map=…`, or the win-x64 leg looks fixed and is not.
- Native MSVC's `/pathmap:` is ignored too unless `/experimental:deterministic` is also
  passed (`warning D9007`, which vcpkg's port logs hide). Until 0.10.0 every native
  `release`-preset build embedded the clone's absolute path roughly 400 times per binary;
  the gate below didn't notice because the clone wasn't under a home directory.
  Deterministic mode also applies the maps to the `/Fd` compile-PDB path, so `/Zi` fails
  with `C1090`; the project compiles with `/Z7` instead, as vcpkg's ports already do.
- Overlapping maps resolve differently per compiler. Measured: GCC 13 honours the *last*
  matching map, Clang the *longest*, MSVC 19.44 the *first*. Listing the broad `$HOME`
  catch-all first and the specific repo root last is correct for GCC and Clang;
  `path-privacy.cmake` reverses the list for MSVC.

The `$HOME` catch-all is what covers tool caches outside the checkout, notably the xwin
Windows SDK splat (`~/.xwin-cache` locally, `~/.xwin` on CI).

### 2.2 OpenSSL's compiled-in provider directories — overlay-port patch

`ENGINESDIR`/`MODULESDIR` are `-D` string literals fixed at configure time from vcpkg's
absolute `--prefix`, so **no compiler flag removes them**:

```
…/vcpkg_installed/arm64-osx-static/lib/ossl-modules
```

`cmake/ports/openssl/unix/redact-build-paths.patch` decouples the compiled-in values from
the install prefix. They are inert here: OpenSSL is linked statically with
`no-shared`/`no-module`, so nothing is ever loaded from either directory.

A native MSVC build uses the nmake template instead, and vcpkg's Windows portfile also
passes `--openssldir` inside its packages tree, so `OPENSSLDIR` leaks there too.
`cmake/ports/openssl/windows/redact-build-paths.patch` compiles in OpenSSL's own defaults
for an unconfigured Windows build (`C:\Program Files\Common Files\SSL`,
`C:\Program Files\OpenSSL\lib\…`). Unlike the provider directories, `OPENSSLDIR` is read
at run time (it is where `openssl.cnf` is looked up), which is why it points at an
admin-only location rather than an arbitrary path.

### 2.3 OpenSSL's `buildinf.h` — the fix that leaked the fix

`crypto/build.info` bakes the full build command line into the binary for
`OpenSSL_version(OPENSSL_CFLAGS)`. That command line **contains the
`-ffile-prefix-map=/Users/<name>=…` flags themselves**, so applying 2.1 alone moved the
leak rather than removing it (verified: 404 → 2, both inside `cversion.o`). The same patch
replaces the recorded string with `redacted`.

Worth remembering as a general hazard: any build system that records its own command line
will re-expose whatever the mapping flags name.

### 2.4 `DT_RUNPATH` in `server.so` — `CMAKE_SKIP_BUILD_RPATH`

Linking the Linux plugin against the HL2SDK stubs made CMake record their directory as a
runtime search path:

```
RUNPATH  /home/runner/work/…/plugin/Cs2VideoGenerator.Plugin.Grpc/external/hl2sdk/lib/linux64
```

This is a linker artifact, so neither `-ffile-prefix-map` nor `strip` affects it. Nothing
needs it: inside CS2 the engine has already loaded tier0, and the recorded directory does
not exist on a user's machine. The build-tree RPATH is disabled.

### 2.5 Recorded PDB paths — linker flag and MSBuild property

Native: `/DEBUG` records the PDB's absolute path in the image's CodeView entry. The `.pdb` is
not packaged, but the string ships inside `server.dll`/`mock_server.exe`.
`/PDBALTPATH:%_PDB%` reduces it to the bare filename.

On the managed side, every assembly records its `.pdb` path, and the `.pdb` records the
source root. `ContinuousIntegrationBuild` (set for CI builds in `Directory.Build.props`)
normalizes both. Verified locally: `Cs2VideoGenerator.Core.dll` goes from
`/home/runner/work/…/Cs2VideoGenerator.Core.pdb` to `/_/artifacts/obj/…`.

### Incidental: the macOS port build was already broken

Fixing the above surfaced two pre-existing bugs in `cmake/triplets/arm64-osx-static.cmake`
that had to be repaired to build the mock locally at all:

- The chainload condition tested `CMAKE_HOST_SYSTEM_PROCESSOR`. That variable is empty in
  CMake script mode (`cmake -P`), which is how vcpkg evaluates triplets, so every Apple
  Silicon build was silently taking the cross-compilation branch. Now uses `uname -m`,
  matching the other triplets.
- `VCPKG_CMAKE_SYSTEM_NAME Darwin` marks port builds as cross-compiling, and CMake 4 does
  not auto-detect the macOS SDK when cross-compiling. The detected flags contained a bare
  `-isysroot`, which swallowed the following flag and broke every `#include`
  (`'sys/types.h' file not found`, first hit by OpenSSL). `cmake/osx-sysroot.cmake`
  resolves the SDK with `xcrun --show-sdk-path`. This affects vcpkg's own `arm64-osx` host
  triplet too, since it sets the same system name, so `cmake/triplets/arm64-osx.cmake`
  overlays the built-in one, including it and adding nothing but the SDK resolution.

The OpenSSL overlay port also applies to every preset now, not just `cross-windows-x64`;
macOS and Linux were silently building the registry's 3.4.0 while Windows shipped 3.6.0.

## 3. Why a locally-built macOS binary is still acceptable

`prebuilt/osx-arm64/mock_server` is built on a maintainer's Mac and committed, because CS2
has no macOS build and a macOS runner is not worth its cost for a test fixture. Three
things make that safe, and none of them depend on the person running the refresh:

1. Nothing to leak: §2 removes the paths at compile time.
2. `refresh-macos-mock.sh` refuses to produce a bad binary. It rejects a linked git
   worktree (whose path is not covered by the maps, and which is exactly how 1.0.0 went
   wrong) and scans the binary before staging it.
3. CI re-scans the committed file on every push and PR (`ci.yml`, job `path-privacy`). A
   binary carrying anyone's home directory cannot reach `main`, however it was produced.

If that ever stops being worth it, the alternative is a `macos-14` leg in
`build-native-plugins.yml` and deleting `prebuilt/`. The embedded paths become
`/Users/runner/…`, which discloses nothing.

## 4. The gate

`.github/scripts/check-embedded-paths.py` scans bytes for `/Users/`, `/home/`, `/root/`
and `C:\Users\`, plus the root of the checkout it runs from (both separators, both cases),
which catches a clone outside any home directory. It expands `.nupkg`/`.zip`/`.tar.gz`
so it can be pointed at a packed package. It redacts the user component and the checkout
root of anything it reports, so a failure does not itself republish the path.

| Where | What it covers |
|---|---|
| `ci.yml` → `path-privacy` | the committed macOS prebuilt |
| `native-windows.yml` | a native MSVC `release`-preset build (not shipped; it keeps `/pathmap` honest) |
| `build-native-plugins.yml` | each RID's freshly built natives (`.pdb`/`.lib` exempt — build-tree only, never packaged) |
| `release.yml` | the packed `.nupkg`s and the CLI archives, before anything is pushed |
| `refresh-macos-mock.sh` | the macOS mock, before staging |

The release scan passes `--only` for the CLI archives: those bundle the .NET runtime,
whose binaries carry Microsoft's own CI paths and are not ours to fix.

Byte-level scanning is not optional. BSD `grep` under a UTF-8 locale declines to match
inside binary files and reports a clean result for a leaking artifact; the upstream
report's first scan was a false negative for exactly this reason. Ad-hoc check:

```sh
python3 - path/to/binary <<'EOF'
import sys
d = open(sys.argv[1], 'rb').read()
for n in (b'/Users/', b'/home/', b'C:\\Users\\', b'/root/'):
    print(n.decode('latin1'), d.count(n))
EOF
```

A clean scan alone does not prove the maps applied. It is also what you get if the scan is
looking in the wrong place. Confirm the positive signal too: mapped paths appear as
`/csvg/…` (and `\csvg\…` on the Windows leg, where clang-cl normalizes separators).

```sh
strings mock_server | grep -c 'csvg[/\\]'
```

## 5. Reply to the upstream report

Confirmed and fixed. Verified per RID by building each one and scanning the output.
Occurrences of the builder's home path in `Cs2VideoGenerator.Core`:

| Artifact | 1.0.0 | now |
|---|---|---|
| `runtimes/osx-arm64/native/mock_server` | 404 | **0** |
| `runtimes/win-x64/native/mock_server.exe` | 396 | **0** |
| `runtimes/win-x64/native/server.dll` | 396 | **0** |
| `runtimes/linux-x64/native/mock_server` | 412 | **0** |
| `runtimes/linux-x64/native/server.so` | 413 | **0** |
| `lib/net10.0/Cs2VideoGenerator.Core.dll` | 1 | **0** |

Four corrections to the report's remediation guidance, each found by testing it:

1. `clang-cl` **silently ignores** a bare `-ffile-prefix-map` (`warning: unknown argument
   ignored`, exit status unchanged). It needs `/clang:-ffile-prefix-map=…`.
2. Setting the flags in the vcpkg triplet is not sufficient wherever a chainload toolchain
   is used. The chainload replaces vcpkg's own toolchain, which is what would have applied
   them. Our win-x64 leg received none of them until the toolchain file was fixed.
3. Prefix-mapping does not touch OpenSSL at all: `MODULESDIR`/`ENGINESDIR` are `-D`
   string literals, and `buildinf.h` records the build command line — **which contains
   the prefix-map flags themselves**, so fixing `__FILE__` alone relocates the leak
   rather than removing it. Both need port-level patches.
4. There is a fifth mechanism the report did not cover: `server.so` carried a
   `DT_RUNPATH` naming the build tree. Linker artifact; no compiler flag affects it.

One note on the proposed CI gate: it needs a `*/runner/*` allowlist (or first-party
scoping), or it will flag CI-built binaries from every upstream — Microsoft's, Avalonia's,
and ours. Our own gate scopes with `--only` for exactly this reason.

## 6. Residual exposure

- Published 1.0.0 artifacts still contain the paths. They are superseded by the next
  release, not retroactively fixed. The repo and its GitHub Packages feed are private and
  the package is not on nuget.org, so the exposure is limited to whoever already has it.
- The macOS mock binaries in git history carry only sanitized build paths, so
  nothing in the tree retains the leak.
- The gate matches known roots, so a leak that fits none of those patterns (a bare
  username with no path prefix, say) would pass. Every mechanism found so far produces a
  rooted absolute path.
