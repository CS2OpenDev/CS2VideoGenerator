# Cross-Compilation Testing Guide

## Overview

The cross-compilation setup has been tested across multiple platforms. This is how to check it on your own build host.

## Testing Infrastructure

### Automated Test Script

`scripts/test-cross-compilation.sh` tests every CMake preset available on the current platform.

```bash
cd plugin/Cs2VideoGenerator.Plugin.Grpc

# Test configuration only (fast)
export TEST_CONFIGURE_ONLY=true
./scripts/test-cross-compilation.sh

# Test full builds (slow - requires dependencies)
export TEST_CONFIGURE_ONLY=false
export TEST_BUILD=true
./scripts/test-cross-compilation.sh
```

### CI/CD Testing

CI lives in `.github/workflows/build-native-plugins.yml`, a reusable workflow invoked by `ci.yml` and `release.yml`. There used to be a standalone `cross-compilation-tests.yml`; it was removed. CI now performs full builds of the shipping targets and asserts the produced native assets.

Automated builds run on ubuntu-latest: linux-x64 natively, win-x64 cross-compiled with clang-cl
and xwin. There is no Windows or macOS runner in CI; the osx-arm64 procedures below are run by
hand on an M-series machine.

## Cross-Compilation Verification

Procedures are grouped by build host rather than by target. Each one is the same three steps: configure, build, verify the artifact.

### Verifying from macOS ARM64 (M-Series)

An M-series Mac can target all 4 platforms. Run the steps in order.

#### Prerequisites

```bash
# Install tools
xcode-select --install
brew install cmake ninja llvm

# Setup vcpkg submodule
git submodule update --init
./external/vcpkg/bootstrap-vcpkg.sh

# Add LLVM to PATH (for Windows cross-compilation)
export PATH="/opt/homebrew/opt/llvm/bin:$PATH"

# (Optional) Setup xwin for Windows targets
cargo install xwin
xwin --accept-license splat --output ~/.xwin
export XWIN_DIR=~/.xwin
```

#### Step 1: Native macOS ARM64

```bash
cd plugin/Cs2VideoGenerator.Plugin.Grpc

# Configure
cmake --preset release-unix

# Build (uses all CPU cores)
cmake --build --preset release-unix --parallel

# Verify artifacts
file ../../artifacts/build/Cs2VideoGenerator.Plugin.GRPC/release-unix/bin/osx-arm64/mock_server
# Expected: Mach-O 64-bit executable arm64
```

#### Step 2: Cross-compile to Linux x64

> **Note:** macOS → Linux cross-compilation requires a Linux sysroot (headers + glibc).
> Without a sysroot, the configure phase may succeed but compilation will fail with
> missing headers (`sys/types.h`, `stdio.h`, etc.). Use Docker or a pre-built sysroot.
> This target is best built natively on Linux or via CI.

```bash
# Configure (requires Linux sysroot)
cmake --preset cross-linux-x64

# Build
cmake --build --preset cross-linux-x64 --parallel

# Verify artifacts
file ../../artifacts/build/Cs2VideoGenerator.Plugin.GRPC/cross-linux-x64/bin/linux-x64/mock_server
# Expected: ELF 64-bit LSB executable, x86-64
```

#### Step 3: Cross-compile to Linux ARM64

> **Note:** Same Linux sysroot requirement as Linux x64 above.

```bash
# Configure (requires Linux sysroot)
cmake --preset cross-linux-arm64

# Build
cmake --build --preset cross-linux-arm64 --parallel

# Verify artifacts
file ../../artifacts/build/Cs2VideoGenerator.Plugin.GRPC/cross-linux-arm64/bin/linux-arm64/mock_server
# Expected: ELF 64-bit LSB executable, ARM aarch64
```

#### Step 4: Cross-compile to Windows x64

> **Note:** macOS → Windows cross-compilation uses clang-cl + lld-link + xwin SDK.
> The toolchain can successfully compile most C/C++ libraries (zlib, abseil, protobuf,
> re2, utf8-range), but the vcpkg OpenSSL port requires native Windows build tools
> (cmd.exe, nmake/jom, NASM) that are unavailable on macOS. Full gRPC builds require
> OpenSSL, so this target is best built natively on Windows or via CI.
> The configure phase will partially succeed (dependency compilation) but ultimately
> fail on OpenSSL.

```bash
# Requires xwin setup (see prerequisites)
# Configure (will fail on OpenSSL dependency)
cmake --preset cross-windows-x64

# If OpenSSL is pre-built/available:
cmake --build --preset cross-windows-x64 --parallel

# Verify artifacts
file ../../artifacts/build/Cs2VideoGenerator.Plugin.GRPC/cross-windows-x64/bin/win-x64/mock_server.exe
# Expected: PE32+ executable (console) x86-64
```

#### Quick Verification Script (macOS ARM64)

All four in sequence, with pass/fail reported per preset:

```bash
export XWIN_DIR=~/.xwin  # if available
export PATH="/opt/homebrew/opt/llvm/bin:$PATH"
cd plugin/Cs2VideoGenerator.Plugin.Grpc

PRESETS=(release-unix cross-linux-x64 cross-linux-arm64 cross-windows-x64)

for preset in "${PRESETS[@]}"; do
    echo "=== Testing $preset ==="
    if cmake --preset "$preset" 2>&1; then
        if cmake --build --preset "$preset" --parallel 2>&1; then
            echo "PASS: $preset"
        else
            echo "FAIL (build): $preset"
        fi
    else
        echo "FAIL (configure): $preset"
    fi
    echo ""
done
```

### Verifying from Linux x64

#### Prerequisites

```bash
sudo apt-get update
sudo apt-get install -y build-essential cmake ninja-build git

# Setup vcpkg submodule
git submodule update --init
./external/vcpkg/bootstrap-vcpkg.sh

# For ARM64 targets
sudo apt-get install gcc-aarch64-linux-gnu g++-aarch64-linux-gnu

# For Windows targets
sudo apt-get install clang llvm lld
cargo install xwin
xwin --accept-license splat --output ~/.xwin
export XWIN_DIR=~/.xwin
```

#### Step 1: Native Linux x64

```bash
cmake --preset release-unix
cmake --build --preset release-unix --parallel

file ../../artifacts/build/Cs2VideoGenerator.Plugin.GRPC/release-unix/bin/linux-x64/mock_server
# Expected: ELF 64-bit LSB executable, x86-64
```

#### Step 2: Cross-compile to Linux ARM64

```bash
cmake --preset cross-linux-arm64
cmake --build --preset cross-linux-arm64 --parallel

file ../../artifacts/build/Cs2VideoGenerator.Plugin.GRPC/cross-linux-arm64/bin/linux-arm64/mock_server
# Expected: ELF 64-bit LSB executable, ARM aarch64
```

#### Step 3: Cross-compile to Windows x64

```bash
cmake --preset cross-windows-x64
cmake --build --preset cross-windows-x64 --parallel

file ../../artifacts/build/Cs2VideoGenerator.Plugin.GRPC/cross-windows-x64/bin/win-x64/mock_server.exe
# Expected: PE32+ executable (console) x86-64
```

#### Step 4: Cross-compile to macOS (requires osxcross)

```bash
# macOS cross-compilation from Linux requires osxcross - see BUILD.md
cmake --preset cross-osx-arm64
cmake --build --preset cross-osx-arm64 --parallel
```

### Verifying from Windows x64

#### Prerequisites

```powershell
# Install Visual Studio 2022 with C++ workload
# Or install Build Tools for Visual Studio 2022

# Setup vcpkg submodule
git submodule update --init
.\external\vcpkg\bootstrap-vcpkg.bat
```

#### Step 1: Native Windows x64

```powershell
cmake --preset release
cmake --build --preset release --parallel

# Verify artifact exists
dir ..\..\artifacts\build\Cs2VideoGenerator.Plugin.GRPC\release\bin\win-x64\mock_server.exe
```

#### List Available Presets

```powershell
cmake --list-presets
```

The native Windows presets should be available. The cross-compilation presets will be filtered by conditions (Windows can't cross-compile easily).

## Test Matrix

| Build Host | Target | Preset | Prerequisites | Expected Result |
|------------|--------|---------|---------------|-----------------|
| **Linux x64** | Linux x64 | release-unix | gcc/clang | Full build + ELF x86-64 binary |
| Linux x64 | Linux ARM64 | cross-linux-arm64 | gcc-aarch64-linux-gnu | Full build + ELF aarch64 binary |
| Linux x64 | Windows x64 | cross-windows-x64 | clang + xwin | Full build + PE32+ binary |
| Linux x64 | macOS ARM64 | cross-osx-arm64 | osxcross | Full build + Mach-O arm64 binary |
| **Windows x64** | Windows x64 | release | MSVC | Full build + PE32+ binary |
| **macOS ARM64** | macOS ARM64 | release-unix | Xcode | **Verified** - Full build + Mach-O arm64 binary |
| macOS ARM64 | Linux x64 | cross-linux-x64 | clang + Linux sysroot | Blocked: requires Linux sysroot (use Docker/CI) |
| macOS ARM64 | Linux ARM64 | cross-linux-arm64 | clang + Linux sysroot | Blocked: requires Linux sysroot (use Docker/CI) |
| macOS ARM64 | Windows x64 | cross-windows-x64 | clang-cl + xwin | Blocked: OpenSSL needs native Windows tools (use CI) |

## Interpreting Results

### Success Indicators

Configure prints:

```
-- Running vcpkg install
-- The C compiler identification is ...
-- The CXX compiler identification is ...
-- Configuring done
-- Generating done
```

The build:

```
Building CXX object ...
Linking CXX executable ...
Built target mock_server
```

Then confirm the binary format matches the target. `file` does this on macOS and Linux:

```bash
# macOS ARM64 target
file mock_server
# → Mach-O 64-bit executable arm64

# Linux x64 target
file mock_server
# → ELF 64-bit LSB executable, x86-64

# Linux ARM64 target
file mock_server
# → ELF 64-bit LSB executable, ARM aarch64

# Windows x64 target
file mock_server.exe
# → PE32+ executable (console) x86-64
```

### Common Issues

#### 1. "CMAKE_TOOLCHAIN_FILE is not set"

Bootstrap the vcpkg submodule: `git submodule update --init && ./external/vcpkg/bootstrap-vcpkg.sh`

#### 2. "clang not found"

`sudo apt install clang llvm` on Linux, `brew install llvm` on macOS.

#### 3. "XWIN_DIR not found"

Install and run xwin:

```bash
cargo install xwin
xwin --accept-license splat --output ~/.xwin
export XWIN_DIR=~/.xwin
```

#### 4. "aarch64-linux-gnu-gcc not found"

```bash
sudo apt install gcc-aarch64-linux-gnu g++-aarch64-linux-gnu
```

## Reporting Test Results

When reporting test results, include:

1. System information:
   ```bash
   uname -a
   cmake --version
   ```

2. Test output:
   ```bash
   ./scripts/test-cross-compilation.sh > test-results.txt 2>&1
   ```

3. The preset list:
   ```bash
   cmake --list-presets
   ```

4. Artifact verification:
   ```bash
   # Check all built artifacts
   find ../../artifacts/build -name "mock_server*" -exec file {} \;
   ```

5. Configuration logs, from `artifacts/test/*.log`.

## Automated Testing in CI

The `.github/workflows/build-native-plugins.yml` reusable workflow builds the shipping targets. CI invokes it on push and PR, and the release workflow invokes it on tags. The matrix targets build in parallel and the produced native assets are uploaded as artifacts. The reusable workflow has no manual trigger of its own; for a manual run, dispatch `release.yml` (a dry run builds the full artifact set).

View test results in GitHub Actions tab after each commit.
