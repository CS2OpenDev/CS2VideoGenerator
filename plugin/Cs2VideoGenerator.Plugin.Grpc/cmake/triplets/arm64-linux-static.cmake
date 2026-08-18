# vcpkg triplet for Linux ARM64
# Static linking for easier distribution

set(VCPKG_TARGET_ARCHITECTURE arm64)
set(VCPKG_CRT_LINKAGE dynamic)
set(VCPKG_LIBRARY_LINKAGE static)
set(VCPKG_BUILD_TYPE release)

set(VCPKG_CMAKE_SYSTEM_NAME Linux)

# Strip build-machine paths out of every port's __FILE__ expansions.
include("${CMAKE_CURRENT_LIST_DIR}/../path-privacy.cmake")
csvg_apply_path_privacy_flags(GNU)

# Cross-compilation settings
if(NOT CMAKE_HOST_SYSTEM_PROCESSOR STREQUAL "aarch64")
    set(VCPKG_CHAINLOAD_TOOLCHAIN_FILE ${CMAKE_CURRENT_LIST_DIR}/../toolchains/linux-arm64.cmake)
endif()

# Host triplet for cross-compilation (host tools like protoc run on build machine)
execute_process(COMMAND uname -m OUTPUT_VARIABLE _host_arch OUTPUT_STRIP_TRAILING_WHITESPACE)
if(CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin")
    if(_host_arch STREQUAL "arm64")
        set(VCPKG_HOST_TRIPLET "arm64-osx")
    else()
        set(VCPKG_HOST_TRIPLET "x64-osx")
    endif()
elseif(CMAKE_HOST_SYSTEM_NAME STREQUAL "Linux")
    if(_host_arch MATCHES "x86_64|AMD64")
        set(VCPKG_HOST_TRIPLET "x64-linux")
    endif()
endif()
