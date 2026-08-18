# Toolchain file for Linux x86_64 target
# Can be used for both native and cross-compilation from other architectures

set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR x86_64)

# Detect if we're cross-compiling from macOS
if(CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin")
    # Cross-compiling from macOS using GCC cross-toolchain
    # Install: brew tap messense/macos-cross-toolchains && brew install x86_64-unknown-linux-gnu
    set(_cross_prefix "x86_64-unknown-linux-gnu")
    set(_brew_prefix "/opt/homebrew/bin")

    find_program(_cross_gcc "${_cross_prefix}-gcc" PATHS "${_brew_prefix}" NO_DEFAULT_PATH)
    find_program(_cross_gxx "${_cross_prefix}-g++" PATHS "${_brew_prefix}" NO_DEFAULT_PATH)
    find_program(_cross_ar "${_cross_prefix}-ar" PATHS "${_brew_prefix}" NO_DEFAULT_PATH)
    find_program(_cross_ranlib "${_cross_prefix}-ranlib" PATHS "${_brew_prefix}" NO_DEFAULT_PATH)

    if(NOT _cross_gcc OR NOT _cross_gxx)
        message(FATAL_ERROR
            "Linux x64 cross-toolchain not found.\n"
            "Install it with:\n"
            "  brew tap messense/macos-cross-toolchains\n"
            "  brew install x86_64-unknown-linux-gnu\n"
        )
    endif()

    set(CMAKE_C_COMPILER "${_cross_gcc}")
    set(CMAKE_CXX_COMPILER "${_cross_gxx}")
    set(CMAKE_AR "${_cross_ar}")
    set(CMAKE_RANLIB "${_cross_ranlib}")

    # GCC cross-toolchain has sysroot built-in and uses its own linker — no lld needed
    # -fPIC: when used as VCPKG_CHAINLOAD_TOOLCHAIN_FILE this REPLACES vcpkg's
    # stock linux.cmake, which normally injects -fPIC (and VCPKG_C/CXX_FLAGS)
    # into every port. Without it, static ports (e.g. re2) get non-PIC objects
    # and server.so fails to link. Same pitfall as /MT on the Windows toolchain.
    set(CMAKE_C_FLAGS_INIT "-m64 -fPIC")
    set(CMAKE_CXX_FLAGS_INIT "-m64 -fPIC")

    message(STATUS "Cross-compiling from macOS to Linux x64 using GCC cross-toolchain")
else()
    # Native Linux or cross from other Linux architecture
    # Use system compilers by default (can be overridden)
    if(NOT CMAKE_C_COMPILER)
        set(CMAKE_C_COMPILER gcc)
    endif()
    if(NOT CMAKE_CXX_COMPILER)
        set(CMAKE_CXX_COMPILER g++)
    endif()

    # Cross-compilation settings from non-x64 Linux
    if(NOT CMAKE_HOST_SYSTEM_PROCESSOR STREQUAL "x86_64")
        # Set cross-compiler prefix if cross-compiling
        set(CMAKE_C_COMPILER x86_64-linux-gnu-gcc)
        set(CMAKE_CXX_COMPILER x86_64-linux-gnu-g++)
    endif()

    # Standard flags (-fPIC: see note in the Darwin branch above)
    set(CMAKE_C_FLAGS_INIT "-m64 -fPIC")
    set(CMAKE_CXX_FLAGS_INIT "-m64 -fPIC")
endif()

# Path privacy (see cmake/path-privacy.cmake) — appended here for the same reason -fPIC
# is: chainloading this file replaces vcpkg's stock linux.cmake, so the triplet's
# VCPKG_C_FLAGS/VCPKG_CXX_FLAGS never reach the compiler.
include("${CMAKE_CURRENT_LIST_DIR}/../path-privacy.cmake")
string(APPEND CMAKE_C_FLAGS_INIT " ${CSVG_PATH_MAP_GNU}")
string(APPEND CMAKE_CXX_FLAGS_INIT " ${CSVG_PATH_MAP_GNU}")

# Search paths for cross-compilation
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY BOTH)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE BOTH)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE BOTH)
