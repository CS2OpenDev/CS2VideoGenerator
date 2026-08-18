# Toolchain file for macOS ARM64 (Apple Silicon) target
# For cross-compilation from Linux, requires osxcross or similar

set(CMAKE_SYSTEM_NAME Darwin)
set(CMAKE_SYSTEM_PROCESSOR arm64)

# Check if we're cross-compiling (not on macOS ARM64)
if(NOT CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin")
    # Cross-compilation from Linux - requires osxcross
    if(DEFINED ENV{OSXCROSS_ROOT})
        set(OSXCROSS_ROOT $ENV{OSXCROSS_ROOT})
        set(CMAKE_C_COMPILER ${OSXCROSS_ROOT}/bin/arm64-apple-darwin-clang)
        set(CMAKE_CXX_COMPILER ${OSXCROSS_ROOT}/bin/arm64-apple-darwin-clang++)
        set(CMAKE_FIND_ROOT_PATH ${OSXCROSS_ROOT})
    else()
        message(WARNING "Cross-compiling to macOS requires osxcross. Set OSXCROSS_ROOT environment variable.")
    endif()
elseif(NOT CMAKE_HOST_SYSTEM_PROCESSOR STREQUAL "arm64")
    # On macOS x64 targeting ARM64
    if(NOT CMAKE_C_COMPILER)
        set(CMAKE_C_COMPILER clang)
    endif()
    if(NOT CMAKE_CXX_COMPILER)
        set(CMAKE_CXX_COMPILER clang++)
    endif()
else()
    # Native ARM64 macOS build
    if(NOT CMAKE_C_COMPILER)
        set(CMAKE_C_COMPILER clang)
    endif()
    if(NOT CMAKE_CXX_COMPILER)
        set(CMAKE_CXX_COMPILER clang++)
    endif()
endif()

# macOS-specific settings
set(CMAKE_OSX_ARCHITECTURES arm64)
set(CMAKE_OSX_DEPLOYMENT_TARGET "11.0" CACHE STRING "Minimum macOS deployment version (ARM64 requires 11.0+)")

# Search paths for cross-compilation
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)

# Path privacy (see cmake/path-privacy.cmake). Chainloading this file replaces vcpkg's
# stock osx.cmake, so the triplet's VCPKG_C_FLAGS/VCPKG_CXX_FLAGS never reach the
# compiler; the maps have to be appended here to survive.
include("${CMAKE_CURRENT_LIST_DIR}/../path-privacy.cmake")
string(APPEND CMAKE_C_FLAGS_INIT " ${CSVG_PATH_MAP_GNU}")
string(APPEND CMAKE_CXX_FLAGS_INIT " ${CSVG_PATH_MAP_GNU}")
