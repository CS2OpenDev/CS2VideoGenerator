# Toolchain file for Linux ARM64 (aarch64) target

set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR aarch64)

# Detect if we're cross-compiling from macOS
if(CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin")
    # Cross-compiling from macOS using GCC cross-toolchain
    # Install: brew tap messense/macos-cross-toolchains && brew install aarch64-unknown-linux-gnu
    set(_cross_prefix "aarch64-unknown-linux-gnu")
    set(_brew_prefix "/opt/homebrew/bin")

    find_program(_cross_gcc "${_cross_prefix}-gcc" PATHS "${_brew_prefix}" NO_DEFAULT_PATH)
    find_program(_cross_gxx "${_cross_prefix}-g++" PATHS "${_brew_prefix}" NO_DEFAULT_PATH)
    find_program(_cross_ar "${_cross_prefix}-ar" PATHS "${_brew_prefix}" NO_DEFAULT_PATH)
    find_program(_cross_ranlib "${_cross_prefix}-ranlib" PATHS "${_brew_prefix}" NO_DEFAULT_PATH)

    if(NOT _cross_gcc OR NOT _cross_gxx)
        message(FATAL_ERROR
            "Linux ARM64 cross-toolchain not found.\n"
            "Install it with:\n"
            "  brew tap messense/macos-cross-toolchains\n"
            "  brew install aarch64-unknown-linux-gnu\n"
        )
    endif()

    set(CMAKE_C_COMPILER "${_cross_gcc}")
    set(CMAKE_CXX_COMPILER "${_cross_gxx}")
    set(CMAKE_AR "${_cross_ar}")
    set(CMAKE_RANLIB "${_cross_ranlib}")

    # GCC cross-toolchain has sysroot built-in and uses its own linker — no lld needed

    message(STATUS "Cross-compiling from macOS to Linux ARM64 using GCC cross-toolchain")
else()
    # Cross-compilation from Linux (or native ARM64)
    # Use cross-compiler for ARM64
    if(NOT CMAKE_C_COMPILER)
        set(CMAKE_C_COMPILER aarch64-linux-gnu-gcc)
    endif()
    if(NOT CMAKE_CXX_COMPILER)
        set(CMAKE_CXX_COMPILER aarch64-linux-gnu-g++)
    endif()
endif()

# Search paths for cross-compilation
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY BOTH)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE BOTH)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE BOTH)

# Path privacy (see cmake/path-privacy.cmake). Chainloading this file replaces vcpkg's
# stock linux.cmake, so the triplet's VCPKG_C_FLAGS/VCPKG_CXX_FLAGS never reach the
# compiler; the maps have to be appended here to survive.
include("${CMAKE_CURRENT_LIST_DIR}/../path-privacy.cmake")
string(APPEND CMAKE_C_FLAGS_INIT " ${CSVG_PATH_MAP_GNU}")
string(APPEND CMAKE_CXX_FLAGS_INIT " ${CSVG_PATH_MAP_GNU}")
