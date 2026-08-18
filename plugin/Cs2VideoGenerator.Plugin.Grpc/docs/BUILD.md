# Cross-Compilation Build Guide

## Table of Contents

- [Overview](#overview)
- [Build Matrix](#build-matrix)
- [Prerequisites](#prerequisites)
- [Platform-Specific Setup](#platform-specific-setup)
- [Building](#building)
- [Troubleshooting](#troubleshooting)

## Overview

Two targets, and they don't have the same reach. mock_server builds for everything: Windows, Linux, macOS, x64 and ARM64. server_plugin builds only for Windows x64 and Linux x64 — the platforms CS2 supports.

### Key Technologies

CMake 3.20+ drives the build and carries the presets, vcpkg supplies gRPC and protobuf, and Clang/LLVM is the primary compiler for cross-compilation. Ninja is the recommended generator.

## Build Matrix

### mock_server

|               | win-x64 | linux-x64 | linux-arm64 | osx-arm64 |
| ------------- | ------- | --------- | ----------- | --------- |
| **win-x64**   | untested ([MSVC](#for-native-windows-builds)) | untested ([Clang + xwin](#for-windows-cross-compilation-from-linux)) | untested ([gcc-aarch64](#for-linux-arm64-cross-compilation)) | untested |
| **linux-x64** | untested ([Clang + xwin](#for-windows-cross-compilation-from-linux)) | untested ([GCC](#for-native-linux-builds)) | untested ([gcc-aarch64](#for-linux-arm64-cross-compilation)) | untested ([osxcross](#for-macos-cross-compilation-from-linux)) |
| **osx-arm64** | works ([clang-cl + xwin](#for-windows-cross-compilation-from-macos)) | works ([GCC cross-toolchain](#for-linux-cross-compilation-from-macos)) | works ([GCC cross-toolchain](#for-linux-cross-compilation-from-macos)) | works (native) |

### server_plugin (server.dll / server.so)

Built only when the target is win-x64 or linux-x64, since CS2 only officially supports those platforms.

|               | win-x64 | linux-x64 |
| ------------- | ------- | --------- |
| **win-x64**   | untested ([MSVC](#for-native-windows-builds)) | untested ([Clang + xwin](#for-windows-cross-compilation-from-linux)) |
| **linux-x64** | untested ([Clang + xwin](#for-windows-cross-compilation-from-linux)) | untested ([GCC](#for-native-linux-builds)) |
| **osx-arm64** | works ([clang-cl + xwin](#for-windows-cross-compilation-from-macos)) | works ([GCC cross-toolchain](#for-linux-cross-compilation-from-macos)) ¹ |

¹ Requires the HL2SDK linux-x64 tier libraries (`external/hl2sdk/lib/linux64/`: `tier1.a` + the `libtier0.so` stub) to produce server_plugin. On server-plugin targets (win-x64, linux-x64), a missing HL2SDK submodule or missing tier libraries is a configure-time **FATAL_ERROR**; pass `-DCSVG_SKIP_SERVER_PLUGIN=ON` to explicitly skip the server plugin (mock_server still builds).

### Legend

**works** means the build succeeded and the artifact was verified. **untested** means nobody has run it. Either way, the toolchain in parentheses links to its setup instructions. A failed build says broken; one blocked by a missing dependency says blocked, with a note.

## Prerequisites

### All Platforms

1. **CMake 3.20+**
   ```bash
   cmake --version  # Should be 3.20 or higher
   ```

2. **vcpkg** (included as a git submodule)
   ```bash
   # Initialize the submodule (from the repo root)
   git submodule update --init

   # Bootstrap vcpkg (first time only)
   # On Linux/macOS:
   ./external/vcpkg/bootstrap-vcpkg.sh

   # On Windows:
   .\external\vcpkg\bootstrap-vcpkg.bat
   ```

3. **Ninja** (recommended for cross-compilation)
   ```bash
   # Ubuntu/Debian
   sudo apt install ninja-build
   
   # macOS
   brew install ninja
   
   # Windows
   choco install ninja  # or winget install Ninja-build.Ninja
   ```

## Platform-Specific Setup

### Linux (Build Host)

#### For Native Linux Builds

```bash
# Ubuntu/Debian
sudo apt install build-essential cmake ninja-build git

# Fedora/RHEL
sudo dnf groupinstall "Development Tools"
sudo dnf install cmake ninja-build git
```

#### For Linux ARM64 Cross-Compilation

```bash
# Install cross-compiler toolchain
sudo apt install gcc-aarch64-linux-gnu g++-aarch64-linux-gnu
```

#### For Windows Cross-Compilation (from Linux)

xwin fetches the Windows SDK, so you don't need a Windows machine.

1. **Install Rust** (needed for xwin):
   ```bash
   curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh
   source $HOME/.cargo/env
   ```

2. **Install xwin**:
   ```bash
   cargo install xwin
   ```

3. **Download Windows SDK**:
   ```bash
   # Download and extract Windows SDK to ~/.xwin
   xwin --accept-license splat --output ~/.xwin
   
   # This downloads:
   # - Windows 10/11 SDK headers and libraries
   # - MSVC C Runtime (CRT)
   # - Takes ~500MB and about 5-10 minutes
   ```

4. **Install Clang/LLVM** (if not already installed):
   ```bash
   # Ubuntu/Debian
   sudo apt install clang llvm lld
   
   # Fedora/RHEL
   sudo dnf install clang llvm lld
   ```

5. **Set environment variable**:
   ```bash
   export XWIN_DIR=~/.xwin
   ```

#### For macOS Cross-Compilation (from Linux)

This one needs osxcross.

1. **Install dependencies**:
   ```bash
   sudo apt install clang llvm cmake git patch libxml2-dev
   ```

2. **Build osxcross**:
   ```bash
   git clone https://github.com/tpoechtrager/osxcross.git ~/osxcross
   cd ~/osxcross
   
   # Download macOS SDK (you'll need to obtain this from Apple)
   # Place the SDK tarball in tarballs/ directory
   # For example: MacOSX13.3.sdk.tar.xz
   
   ./build.sh
   ```

3. **Set environment variable**:
   ```bash
   export OSXCROSS_ROOT=~/osxcross/target
   export PATH=$OSXCROSS_ROOT/bin:$PATH
   ```

### Windows (Build Host)

#### For Native Windows Builds

1. **Install Visual Studio 2022** with "Desktop development with C++" workload

2. **Or install Build Tools**:
   - Download from: https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022
   - Select "C++ build tools" workload

3. **Install Clang** (optional but recommended):
   ```powershell
   # Using Chocolatey
   choco install llvm
   
   # Or download from https://releases.llvm.org/
   ```

### macOS (Build Host)

#### For Native macOS Builds

1. **Install Xcode Command Line Tools**:
   ```bash
   xcode-select --install
   ```

2. **Install Homebrew**:
   ```bash
   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
   ```

3. **Install dependencies**:
   ```bash
   brew install cmake ninja
   ```

#### For Linux Cross-Compilation (from macOS)

Homebrew has pre-built GCC cross-toolchains that bundle a complete glibc sysroot.

**Prerequisites:**

1. **Install GCC cross-toolchains**:
   ```bash
   brew tap messense/macos-cross-toolchains
   brew install x86_64-unknown-linux-gnu    # For Linux x64
   brew install aarch64-unknown-linux-gnu   # For Linux ARM64
   ```

2. **Verify installation**:
   ```bash
   x86_64-unknown-linux-gnu-gcc --version
   aarch64-unknown-linux-gnu-gcc --version
   ```

**Usage:**
```bash
# Cross-compile to Linux x64
cmake --preset cross-linux-x64
cmake --build --preset cross-linux-x64 --parallel

# Cross-compile to Linux ARM64
cmake --preset cross-linux-arm64
cmake --build --preset cross-linux-arm64 --parallel
```

The toolchain files detect macOS as the host and pick up the GCC cross-compilers with their bundled sysroot and linker.

#### For Windows Cross-Compilation (from macOS)

Same xwin approach as on Linux.

**Prerequisites:**

1. **Install LLVM** (for complete toolchain):
   ```bash
   brew install llvm
   
   # Add LLVM to PATH
   echo 'export PATH="/opt/homebrew/opt/llvm/bin:$PATH"' >> ~/.zshrc
   source ~/.zshrc
   
   # Or for Intel Macs:
   echo 'export PATH="/usr/local/opt/llvm/bin:$PATH"' >> ~/.zshrc
   source ~/.zshrc
   ```

2. **Install Rust** (for xwin):
   ```bash
   curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh
   source ~/.cargo/env
   ```

3. **Install and setup xwin**:
   ```bash
   cargo install xwin
   
   # Download Windows SDK (takes ~5-10 minutes)
   xwin --accept-license splat --output ~/.xwin
   
   # Set environment variable
   echo 'export XWIN_DIR="$HOME/.xwin"' >> ~/.zshrc
   export XWIN_DIR="$HOME/.xwin"
   ```

**Usage:**
```bash
# Cross-compile to Windows x64
cmake --preset cross-windows-x64
cmake --build --preset cross-windows-x64 --parallel
```

The binaries come out MSVC ABI-compatible, so they work with CS2 on Windows.

## Building

### Quick Start (Native Build)

```bash
# Navigate to plugin directory
cd plugin/Cs2VideoGenerator.Plugin.Grpc

# Configure (picks the right preset for your platform)
cmake --preset release-unix    # On Linux/macOS
cmake --preset release          # On Windows

# Build
cmake --build --preset release-unix --parallel   # On Linux/macOS
cmake --build --preset release --parallel        # On Windows
```

### Cross-Compilation

Use the cross-compilation presets:

```bash
# List available presets
cmake --list-presets

# Configure for target platform
cmake --preset cross-linux-x64      # Target Linux x64
cmake --preset cross-linux-arm64    # Target Linux ARM64
cmake --preset cross-windows-x64    # Target Windows x64
cmake --preset cross-osx-arm64      # Target macOS ARM64

# Build
cmake --build --preset cross-linux-x64 --parallel
# (use the matching build preset for your target)
```

### Build Artifacts

Artifacts land in a directory named after the CMake build type, not the preset name. That's `Release` for all release and cross presets, `Debug` for the debug presets:

```
artifacts/
└── build/
    └── Cs2VideoGenerator.Plugin.GRPC/
        └── <build-type>/            # Release or Debug
            └── bin/
                └── <runtime-id>/
                    ├── mock_server (or mock_server.exe)
                    └── server.so (or server.dll, if applicable)
```

For example, `cmake --build --preset cross-windows-x64` produces `artifacts/build/Cs2VideoGenerator.Plugin.GRPC/Release/bin/win-x64/`.

Runtime IDs follow .NET conventions: `win-x64` for Windows x86_64, `linux-x64` for Linux x86_64, `linux-arm64` for Linux ARM64, `osx-arm64` for macOS ARM64 (Apple Silicon).

### Example: Cross-Compile from Linux to Windows

```bash
# 1. Ensure xwin is set up (see Platform-Specific Setup)
export XWIN_DIR=~/.xwin

# 2. Configure
cd plugin/Cs2VideoGenerator.Plugin.Grpc
cmake --preset cross-windows-x64

# 3. Build
cmake --build --preset cross-windows-x64 --parallel

# 4. Find artifacts
ls -lh ../../artifacts/build/Cs2VideoGenerator.Plugin.GRPC/Release/bin/win-x64/
```

### Example: Cross-Compile from macOS to Windows

```bash
# 1. Ensure LLVM and xwin are set up (see macOS setup section)
export XWIN_DIR=~/.xwin

# 2. Configure
cd plugin/Cs2VideoGenerator.Plugin.Grpc
cmake --preset cross-windows-x64

# 3. Build
cmake --build --preset cross-windows-x64 --parallel

# 4. Find artifacts
ls -lh ../../artifacts/build/Cs2VideoGenerator.Plugin.GRPC/Release/bin/win-x64/
```

### Example: Cross-Compile from macOS to Linux

```bash
# 1. Install GCC cross-toolchains (one-time)
brew tap messense/macos-cross-toolchains
brew install x86_64-unknown-linux-gnu aarch64-unknown-linux-gnu

# 2. Configure for Linux x64
cd plugin/Cs2VideoGenerator.Plugin.Grpc
cmake --preset cross-linux-x64

# 3. Build
cmake --build --preset cross-linux-x64 --parallel

# 4. Find artifacts
ls -lh ../../artifacts/build/Cs2VideoGenerator.Plugin.GRPC/Release/bin/linux-x64/

# Or for Linux ARM64
cmake --preset cross-linux-arm64
cmake --build --preset cross-linux-arm64 --parallel
```

### Example: Build All Targets from M-Series Mac

```bash
# From an M-series MacBook (macOS ARM64), you can build for all platforms:

export XWIN_DIR=~/.xwin

# Native macOS ARM64
cmake --preset release-unix && cmake --build --preset release-unix --parallel

# Cross to Linux x64
cmake --preset cross-linux-x64 && cmake --build --preset cross-linux-x64 --parallel

# Cross to Linux ARM64
cmake --preset cross-linux-arm64 && cmake --build --preset cross-linux-arm64 --parallel

# Cross to Windows x64 (requires xwin setup)
cmake --preset cross-windows-x64 && cmake --build --preset cross-windows-x64 --parallel
```

## Troubleshooting

### Common Issues

#### 1. vcpkg baseline mismatch

**Error**: `error: while checking out baseline from commit '...', failed to git show versions/baseline.json`

**Solution**: The `builtin-baseline` in `vcpkg.json` must match the vcpkg submodule commit. Update them together:
```bash
cd external/vcpkg
git rev-parse HEAD
# Copy this hash to vcpkg.json "builtin-baseline" field
```

#### 2. xwin not found (Windows cross-compilation)

**Error**: `XWIN_DIR environment variable not set and xwin not found`

**Solution**:
```bash
# Install and run xwin
cargo install xwin
xwin --accept-license splat --output ~/.xwin
export XWIN_DIR=~/.xwin
```

#### 3. Clang/LLVM not found

**Error**: `clang not found. Windows cross-compilation may fail.`

**Solution**:
```bash
# Ubuntu/Debian
sudo apt install clang llvm lld

# macOS
brew install llvm
export PATH="/usr/local/opt/llvm/bin:$PATH"

# Windows
choco install llvm
```

#### 4. Missing Linux cross-toolchain

**Error**: `Linux x64 cross-toolchain not found` or `Linux ARM64 cross-toolchain not found`

**Solution (macOS)**:
```bash
brew tap messense/macos-cross-toolchains
brew install x86_64-unknown-linux-gnu    # For Linux x64
brew install aarch64-unknown-linux-gnu   # For Linux ARM64
```

**Solution (Linux)**:
```bash
sudo apt install gcc-aarch64-linux-gnu g++-aarch64-linux-gnu
```

#### 5. vcpkg taking too long

Building gRPC and protobuf from source can take 20-30 minutes on first build.

**Solutions**:
- Use binary caching: `export VCPKG_BINARY_SOURCES="clear;default,readwrite"`
- Use prebuilt binaries if available
- Otherwise wait it out; subsequent builds are cached

#### 6. HL2SDK missing (configure-time FATAL_ERROR)

The server plugin requires the HL2SDK submodule at `external/hl2sdk`, including the platform tier libraries (`lib/public/win64/tier0.lib` + `tier1.lib` on Windows; `lib/linux64/tier1.a` + the `libtier0.so` stub on Linux). When targeting win-x64 or linux-x64, configuring **fails with a FATAL_ERROR** if the submodule or its tier libraries are missing.

**Solution**:
```bash
cd plugin/Cs2VideoGenerator.Plugin.Grpc
git submodule update --init --recursive
```

If you only need mock_server, skip the plugin explicitly instead:
```bash
cmake --preset <preset> -DCSVG_SKIP_SERVER_PLUGIN=ON
```

### Getting Help

1. Check the vcpkg install log:
   ```
   artifacts/cmake/Cs2VideoGenerator.Plugin.GRPC/<preset>/vcpkg-manifest-install.log
   ```

2. Clean build and try again:
   ```bash
   rm -rf artifacts/cmake/Cs2VideoGenerator.Plugin.GRPC/<preset>
   cmake --preset <preset-name>
   ```

3. Verify your toolchain setup:
   ```bash
   cmake --preset <preset> -LA | grep CMAKE_
   ```

## Advanced Topics

### Custom Toolchains

Custom toolchain files go in `cmake/toolchains/` and get referenced from CMakePresets.json. For a custom Clang configuration:
```cmake
# cmake/toolchains/custom-clang.cmake
set(CMAKE_C_COMPILER clang-15)
set(CMAKE_CXX_COMPILER clang++-15)
set(CMAKE_CXX_FLAGS_INIT "-stdlib=libc++")
```

### Custom vcpkg Triplets

Custom triplets are in `cmake/triplets/`. Modify those or add your own.

### CI/CD Integration

See `.github/workflows/ci.yml`.

## References

- [CMake Documentation](https://cmake.org/documentation/)
- [vcpkg Documentation](https://vcpkg.io/)
- [xwin GitHub](https://github.com/Jake-Shadle/xwin)
- [osxcross GitHub](https://github.com/tpoechtrager/osxcross)
- [LLVM Cross-Compilation Guide](https://clang.llvm.org/docs/CrossCompilation.html)
