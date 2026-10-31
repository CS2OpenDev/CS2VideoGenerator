# Contributing to CS2 Video Generator

Thanks for your interest in contributing!

## Prerequisites

- **.NET 10.0 SDK**. The exact version is in `global.json`.
- For the native C++ plugin and mock server: CMake 3.25+, Ninja (recommended), and vcpkg. vcpkg and the HL2SDK are included as submodules, so run `git submodule update --init --recursive` and bootstrap vcpkg.

## Building and testing

```bash
# Build the .NET solution
dotnet build

# Run the test suite
dotnet test
```

Both must pass, 0 errors, before you open a pull request.

You do not need CS2 installed to develop or test — [mock mode](docs/mock-cs2-mode.md) (`--mock`) exercises the full session lifecycle against the bundled `mock_server`.

## Native plugin build

The C++ engine plugin and mock server live in `plugin/Cs2VideoGenerator.Plugin.Grpc/`. Quick start is in its [README](plugin/Cs2VideoGenerator.Plugin.Grpc/README.md); [docs/BUILD.md](plugin/Cs2VideoGenerator.Plugin.Grpc/docs/BUILD.md) has the complete platform-specific and cross-compilation instructions.

```bash
cd plugin/Cs2VideoGenerator.Plugin.Grpc
git submodule update --init
./external/vcpkg/bootstrap-vcpkg.sh    # first time only
cmake --preset release-unix            # Linux/macOS (use `release` on Windows)
cmake --build --preset release-unix --parallel
```

Most .NET-side changes don't need a plugin rebuild.

## Releases

Releases are tag-triggered and automated. [docs/RELEASING.md](docs/RELEASING.md) covers
the versioning model and the step-by-step release process (maintainers).

## Scope guideline

CSVG is a **pure capture executor**: it captures what a compilation JSON tells it to. Features that *generate* clip definitions (event- or kill-driven clip selection) are permanently out of scope. Check [docs/ROADMAP.md](docs/ROADMAP.md) for direction and planned features before proposing large changes.

## License

By contributing, you agree that your contributions are licensed under the project license, MIT (see [LICENSE](LICENSE)).
