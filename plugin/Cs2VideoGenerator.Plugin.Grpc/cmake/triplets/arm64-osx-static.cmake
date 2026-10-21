# vcpkg triplet for macOS ARM64 (Apple Silicon)
# Static linking for easier distribution

set(VCPKG_TARGET_ARCHITECTURE arm64)
set(VCPKG_CRT_LINKAGE dynamic)
set(VCPKG_LIBRARY_LINKAGE static)
set(VCPKG_BUILD_TYPE release)

set(VCPKG_CMAKE_SYSTEM_NAME Darwin)
set(VCPKG_OSX_ARCHITECTURES arm64)
set(VCPKG_OSX_DEPLOYMENT_TARGET "11.0")

# Strip build-machine paths out of every port's __FILE__ expansions. This triplet is
# the one that produces the committed prebuilt/osx-arm64/mock_server, so it is the one
# that must not disclose the developer's home directory.
include("${CMAKE_CURRENT_LIST_DIR}/../path-privacy.cmake")
csvg_apply_path_privacy_flags(GNU)

# Cross-compilation from non-macOS or from macOS x64.
#
# Host arch comes from `uname -m`, not CMAKE_HOST_SYSTEM_PROCESSOR: vcpkg evaluates
# triplets in CMake SCRIPT mode (cmake -P), where CMAKE_HOST_SYSTEM_PROCESSOR is EMPTY.
# The old condition was therefore always true, and every Apple Silicon build silently
# chainloaded the cross toolchain. (The other triplets already use uname for this.)
if(NOT DEFINED _csvg_host_arch)
    execute_process(COMMAND uname -m OUTPUT_VARIABLE _csvg_host_arch OUTPUT_STRIP_TRAILING_WHITESPACE)
endif()
if(NOT CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin" OR NOT _csvg_host_arch STREQUAL "arm64")
    set(VCPKG_CHAINLOAD_TOOLCHAIN_FILE ${CMAKE_CURRENT_LIST_DIR}/../toolchains/osx-arm64.cmake)
endif()

# Resolve the macOS SDK explicitly; see cmake/osx-sysroot.cmake for why that is necessary.
include("${CMAKE_CURRENT_LIST_DIR}/../osx-sysroot.cmake")
# vcpkg hashes the triplet file but not what it include()s.
list(APPEND VCPKG_HASH_ADDITIONAL_FILES "${CMAKE_CURRENT_LIST_DIR}/../osx-sysroot.cmake")
