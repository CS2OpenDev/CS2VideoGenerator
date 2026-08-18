# vcpkg triplet for Linux x86_64
# Static linking for easier distribution

set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE dynamic)
set(VCPKG_LIBRARY_LINKAGE static)
set(VCPKG_BUILD_TYPE release)

# The static libraries built with this triplet are linked into server.so
# (a shared library), so every port must be built position-independent.
# Without this, libprotobuf.a / libabsl_*.a contain R_X86_64_32S relocations
# and the server plugin fails to link.
set(VCPKG_C_FLAGS "-fPIC")
set(VCPKG_CXX_FLAGS "-fPIC")

# Strip build-machine paths out of every port's __FILE__ expansions (appends to the
# -fPIC flags above).
include("${CMAKE_CURRENT_LIST_DIR}/../path-privacy.cmake")
csvg_apply_path_privacy_flags(GNU)

set(VCPKG_CMAKE_SYSTEM_NAME Linux)

# When cross-compiling from macOS, chainload the toolchain so vcpkg ports
# are built with the correct --target triple and compiler flags.
if(CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin" OR EXISTS "/usr/bin/sw_vers")
    set(VCPKG_CHAINLOAD_TOOLCHAIN_FILE "${CMAKE_CURRENT_LIST_DIR}/../toolchains/linux-x64.cmake")
endif()

# Host triplet for cross-compilation (host tools like protoc run on build machine)
if(NOT CMAKE_HOST_SYSTEM_NAME STREQUAL "Linux")
    execute_process(COMMAND uname -m OUTPUT_VARIABLE _host_arch OUTPUT_STRIP_TRAILING_WHITESPACE)
    if(CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin")
        if(_host_arch STREQUAL "arm64")
            set(VCPKG_HOST_TRIPLET "arm64-osx")
        else()
            set(VCPKG_HOST_TRIPLET "x64-osx")
        endif()
    endif()
endif()

# Use system package manager for dependencies when possible
set(VCPKG_POLICY_EMPTY_PACKAGE enabled)
