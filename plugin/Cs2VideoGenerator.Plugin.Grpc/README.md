# CS2 Video Generator Plugin

The C++ CS2 plugin for CS2 Video Generator, which connects to the .NET orchestrator as a gRPC client, plus the `mock_server` test double. CMake and vcpkg underneath, set up so it builds on and for more than one platform.

## Features

The server plugin (`server.dll` / `server.so`) builds for Windows x64 and Linux x64, the platforms CS2 runs on. `mock_server` also builds for Linux ARM64 and macOS ARM64 (Apple Silicon). Linux and macOS hosts can cross-compile for the other targets:

- macOS → Windows x64 (clang-cl + xwin), Linux x64, Linux ARM64
- Linux → Windows x64 (clang-cl + xwin), Linux ARM64, macOS ARM64 (via osxcross, untested)

Windows hosts build natively only. Released Windows binaries come from the Linux → Windows cross-build; see [BUILD.md](docs/BUILD.md) for the full matrix and what CI covers.

The rest is unremarkable: C++17, gRPC, vcpkg for dependency management, and a CMake preset per build configuration.

## Quick Start

### Prerequisites

CMake 3.25+ (`CMakePresets.json` uses preset schema version 6). vcpkg and the HL2SDK come in as submodules, so `git submodule update --init --recursive` and then `./external/vcpkg/bootstrap-vcpkg.sh`. The Unix and cross-compilation presets use Ninja.

Platform-specific setup is in [BUILD.md](docs/BUILD.md).

### Native Build

```bash
cd plugin/Cs2VideoGenerator.Plugin.Grpc
git submodule update --init --recursive
./external/vcpkg/bootstrap-vcpkg.sh    # first time only (bootstrap-vcpkg.bat on Windows)

cmake --preset release-unix    # Linux/macOS
cmake --build --preset release-unix --parallel

cmake --preset release          # Windows (Visual Studio 2022)
cmake --build --preset release --parallel
```

Binaries land in `artifacts/build/Cs2VideoGenerator.Plugin.GRPC/<Release|Debug>/bin/<rid>/` at the repository root.

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

- [BUILD.md](docs/BUILD.md) — prerequisites, platform setup, cross-compilation, verifying builds, troubleshooting

API reference: run `doxygen Doxyfile` in the plugin root; the HTML lands in `docs/html/`.

## Dependencies

gRPC and Protocol Buffers from vcpkg, at the versions pinned by `builtin-baseline` in `vcpkg.json`; the AlliedModders HL2SDK (`cs2` branch) for the server plugin; CMake 3.25+; C++17.

## IDE Setup (Optional)

### Visual Studio Code

Open the project folder in VS Code — it will prompt you to install the recommended extensions (C++ tools, CMake tools). The workspace is pre-configured in `.vscode/` for CMake preset integration and IntelliSense.

### CLion

Open the plugin folder; CLion loads `CMakePresets.json`, and the presets already point CMake at the vcpkg toolchain.

## Contributing

Set up per [BUILD.md](docs/BUILD.md), branch, make your changes, test on your platform, and open a pull request.

## License

MIT, as part of the Cs2VideoGenerator project. See the repository root [LICENSE](../../LICENSE) file for details. The plugin binaries statically link third-party libraries and include Valve SDK code; see [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md).
