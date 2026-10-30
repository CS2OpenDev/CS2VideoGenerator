# Build-path privacy

Shipped artifacts must not disclose the filesystem layout of the machine that built
them: no home directories, no checkout roots, no CI workspace paths. Compilers and build
systems embed absolute paths in several independent ways, and stripping debug info removes
none of them. This doc covers each mechanism and its fix (`cmake/` paths are relative to
`plugin/Cs2VideoGenerator.Plugin.Grpc/`), why the one locally built native is still safe to
ship, and the gate that enforces all of it.

## 1. Five distinct mechanisms, five fixes

Only the first is the one people expect. A fix that stops there leaves the rest.

### 1.1 `__FILE__` expansion — `cmake/path-privacy.cmake`

gRPC, abseil and OpenSSL expand `__FILE__` in every assertion macro, so each compiled
source file contributes its absolute path as a `__TEXT`/`.rdata` string literal. **These
are not debug info: `strip` does not remove them.** The fix is `-ffile-prefix-map`. It
has to reach both our own translation units and every vcpkg port, and several details
matter:

- vcpkg ports build in their own CMake process, so `add_compile_options()` in our
  `CMakeLists.txt` never reaches them. The maps have to come from the triplet
  (`VCPKG_C_FLAGS`/`VCPKG_CXX_FLAGS`), which is why every triplet in `cmake/triplets/`
  includes `path-privacy.cmake`. vcpkg's ABI hash covers the triplet file but not what it
  `include()`s, so `csvg_apply_path_privacy_flags` also adds `path-privacy.cmake` to
  `VCPKG_HASH_ADDITIONAL_FILES`; otherwise changing the maps would keep reusing cached
  ports built with the old ones. A build that had to skip a map (a path with a space)
  hashes `path-privacy-unmapped.txt` as well, so its leaky ports never share an ABI with
  mapped ones.
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
  passed (`warning D9007`, which vcpkg's port logs hide). Without it, a native
  `release`-preset build embeds the clone's absolute path roughly 400 times per binary,
  which is why the gate below also matches the checkout root, not just home directories.
  Deterministic mode also applies the maps to the `/Fd` compile-PDB path, so `/Zi` fails
  with `C1090`; the project compiles with `/Z7` instead, as vcpkg's ports already do.
- Overlapping maps resolve differently per compiler. Measured: GCC 13 honours the *last*
  matching map, Clang the *longest*, MSVC 19.44 the *first*. Listing the broad `$HOME`
  catch-all first and the specific repo root last is correct for GCC and Clang;
  `path-privacy.cmake` reverses the list for MSVC.

The `$HOME` catch-all is what covers tool caches outside the checkout, notably the xwin
Windows SDK splat (`~/.xwin-cache` locally, `~/.xwin` on CI).

### 1.2 OpenSSL's compiled-in provider directories — overlay-port patch

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

### 1.3 OpenSSL's `buildinf.h` — the fix that leaked the fix

`crypto/build.info` bakes the full build command line into the binary for
`OpenSSL_version(OPENSSL_CFLAGS)`. That command line **contains the
`-ffile-prefix-map=/Users/<name>=…` flags themselves**, so applying 1.1 alone moves the
leak rather than removing it (measured on the macOS mock: from 404 occurrences down to 2,
both inside `cversion.o`). The same patch replaces the recorded string with `redacted`.

Worth remembering as a general hazard: any build system that records its own command line
will re-expose whatever the mapping flags name.

### 1.4 `DT_RUNPATH` in `server.so` — `CMAKE_SKIP_BUILD_RPATH`

Linking the Linux plugin against the HL2SDK stubs made CMake record their directory as a
runtime search path:

```
RUNPATH  /home/runner/work/…/plugin/Cs2VideoGenerator.Plugin.Grpc/external/hl2sdk/lib/linux64
```

This is a linker artifact, so neither `-ffile-prefix-map` nor `strip` affects it. Nothing
needs it: inside CS2 the engine has already loaded tier0, and the recorded directory does
not exist on a user's machine. The build-tree RPATH is disabled.

### 1.5 Recorded PDB paths — linker flag and MSBuild property

Native: `/DEBUG` records the PDB's absolute path in the image's CodeView entry. The `.pdb` is
not packaged, but the string ships inside `server.dll`/`mock_server.exe`.
`/PDBALTPATH:%_PDB%` reduces it to the bare filename.

On the managed side, every assembly records its `.pdb` path, and the `.pdb` records the
source root. `ContinuousIntegrationBuild` (set for CI builds in `Directory.Build.props`)
normalizes both. Verified locally: `Cs2VideoGenerator.Core.dll` goes from
`/home/runner/work/…/Cs2VideoGenerator.Core.pdb` to `/_/artifacts/obj/…`.

### macOS triplets

Two details in `cmake/triplets/arm64-osx-static.cmake` matter for building the macOS mock
locally:

- The chainload condition reads the host architecture from `uname -m`, not
  `CMAKE_HOST_SYSTEM_PROCESSOR`. That variable is empty in CMake script mode (`cmake -P`),
  which is how vcpkg evaluates triplets, so testing it sends every Apple Silicon build down
  the cross-compilation branch.
- `VCPKG_CMAKE_SYSTEM_NAME Darwin` marks port builds as cross-compiling, and CMake 4 does
  not auto-detect the macOS SDK when cross-compiling. The detected flags then contain a bare
  `-isysroot`, which swallows the following flag and breaks every `#include`
  (`'sys/types.h' file not found`, first hit by OpenSSL). `cmake/osx-sysroot.cmake`
  resolves the SDK with `xcrun --show-sdk-path`. vcpkg's own `arm64-osx` host triplet sets
  the same system name, so `cmake/triplets/arm64-osx.cmake` overlays the built-in one,
  including it and adding nothing but the SDK resolution.

The OpenSSL overlay port applies to every preset, so every platform builds the same
OpenSSL version.

## 2. Why a locally-built macOS binary is still acceptable

`prebuilt/osx-arm64/mock_server` is built on a maintainer's Mac and committed, because CS2
has no macOS build and a macOS runner is not worth its cost for a test fixture. Three
things make that safe, and none of them depend on the person running the refresh:

1. Nothing to leak: §1 removes the paths at compile time.
2. `refresh-macos-mock.sh` refuses to produce a bad binary. It rejects a linked git
   worktree (whose path is not covered by the maps) and scans the binary before staging
   it.
3. CI re-scans the committed file on every push and PR (`ci.yml`, job `path-privacy`). A
   binary carrying anyone's home directory cannot reach `main`, however it was produced.

If that ever stops being worth it, the alternative is a `macos-14` leg in
`build-native-plugins.yml` and deleting `prebuilt/`. The embedded paths become
`/Users/runner/…`, which discloses nothing.

## 3. The gate

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
inside binary files and reports a clean result for a leaking artifact. Ad-hoc check:

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

The gate matches known roots, so a leak that fits none of those patterns (a bare username
with no path prefix, say) would pass. Every mechanism found so far produces a rooted
absolute path.
