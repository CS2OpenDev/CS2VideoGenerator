# CS2 Video Generator Plugin - gRPC Server

The C++ gRPC server for the CS2 Video Generator Plugin project. CMake and vcpkg underneath, set up so it builds on and for more than one platform.

## Features

Windows, Linux, and macOS are all supported, M-series included. One host can build for the others:

- macOS → Windows, Linux x64, Linux ARM64
- Linux → Windows, Linux ARM64, macOS (via osxcross)

The rest is unremarkable: C++17, gRPC, vcpkg for dependency management, and a CMake preset per build configuration.

## Quick Start

### Prerequisites

CMake 3.20+. vcpkg comes in as a submodule, so `git submodule update --init` and then `./external/vcpkg/bootstrap-vcpkg.sh`. Ninja is recommended for cross-compilation.

Platform-specific setup is in [BUILD.md](docs/BUILD.md).

### Native Build

```bash
cd plugin/Cs2VideoGenerator.Plugin.Grpc
git submodule update --init
./external/vcpkg/bootstrap-vcpkg.sh    # first time only

cmake --preset release-unix    # Linux/macOS
cmake --build --preset release-unix --parallel

cmake --preset release          # Windows
cmake --build --preset release --parallel
```

### Cross-Compilation

```bash
# List all available presets
cmake --list-presets

# Example: build for Linux x64 from macOS
cmake --preset cross-linux-x64
cmake --build --preset cross-linux-x64 --parallel
```

Toolchain setup, the full build matrix, and more cross-compilation examples are in [BUILD.md](docs/BUILD.md).

## Documentation

- [BUILD.md](docs/BUILD.md) — prerequisites, platform setup, cross-compilation, troubleshooting
- [CROSS-COMPILATION-TESTING.md](docs/CROSS-COMPILATION-TESTING.md) — cross-compilation testing procedures

API reference: run `doxygen Doxyfile` in the plugin root; the HTML lands in `docs/html/`.

## Dependencies

gRPC 1.40.0+, Protocol Buffers 3.15.0+, CMake 3.20+, C++17.

## IDE Setup (Optional)

### Visual Studio Code

Open the project folder in VS Code — it will prompt you to install the recommended extensions (C++ tools, CMake tools). The workspace is pre-configured in `.vscode/` for CMake preset integration and IntelliSense.

### CLion

Import the CMake project directly and set the CMake toolchain to vcpkg in settings.

## Contributing

Set up per [BUILD.md](docs/BUILD.md), branch, make your changes, test on your platform, and open a pull request.

## License

GPL-3.0-only, as part of the Cs2VideoGenerator project. See the repository root [LICENSE](../../LICENSE) file for details.
