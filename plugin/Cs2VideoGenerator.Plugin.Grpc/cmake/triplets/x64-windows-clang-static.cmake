# vcpkg triplet for Windows x86_64 using clang-cl for cross-compilation
# Static linking for easier distribution
# MSVC ABI compatible (required for CS2 compatibility)

set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE static)
set(VCPKG_LIBRARY_LINKAGE static)

# Leave VCPKG_CMAKE_SYSTEM_NAME undefined so vcpkg sets VCPKG_TARGET_IS_WINDOWS=ON.
# Setting it to "Windows" explicitly breaks vcpkg's detection logic.
set(VCPKG_CMAKE_SYSTEM_VERSION 10.0)

# Use our custom Windows cross-compilation toolchain
set(VCPKG_CHAINLOAD_TOOLCHAIN_FILE ${CMAKE_CURRENT_LIST_DIR}/../toolchains/windows-x64.cmake)

# clang-cl accepts MSVC-style flags on all platforms
set(VCPKG_CXX_FLAGS "/EHsc")
set(VCPKG_C_FLAGS "")

# Strip build-machine paths out of every port's __FILE__ expansions. The CLANG_CL
# spelling is mandatory here: clang-cl ignores a bare -ffile-prefix-map with nothing
# worse than a warning, which would leave this leg silently unfixed.
#
# NOTE: on this triplet these flags are a fallback only. VCPKG_CHAINLOAD_TOOLCHAIN_FILE
# above replaces vcpkg's toolchain, which is what would normally append VCPKG_*_FLAGS,
# so cmake/toolchains/windows-x64.cmake is what actually delivers the maps (verified by
# inspecting a port's CMakeCache). They are kept here so the triplet stays correct if
# the chainload is ever dropped.
include("${CMAKE_CURRENT_LIST_DIR}/../path-privacy.cmake")
csvg_apply_path_privacy_flags(CLANG_CL)

set(VCPKG_BUILD_TYPE release)

# Host triplet for cross-compilation (host tools like protoc run on build machine)
if(NOT CMAKE_HOST_SYSTEM_NAME STREQUAL "Windows")
    execute_process(COMMAND uname -m OUTPUT_VARIABLE _host_arch OUTPUT_STRIP_TRAILING_WHITESPACE)
    if(CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin")
        if(_host_arch STREQUAL "arm64")
            set(VCPKG_HOST_TRIPLET "arm64-osx")
        else()
            set(VCPKG_HOST_TRIPLET "x64-osx")
        endif()
    elseif(CMAKE_HOST_SYSTEM_NAME STREQUAL "Linux")
        if(_host_arch STREQUAL "aarch64")
            set(VCPKG_HOST_TRIPLET "arm64-linux")
        else()
            set(VCPKG_HOST_TRIPLET "x64-linux")
        endif()
    endif()
endif()
