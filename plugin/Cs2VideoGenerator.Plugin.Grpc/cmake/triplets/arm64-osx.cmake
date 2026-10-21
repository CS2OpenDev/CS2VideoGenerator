# Overlay of vcpkg's built-in arm64-osx triplet.
#
# This is the HOST triplet on Apple Silicon: vcpkg builds host tools (protoc, the gRPC
# codegen plugins) with it while the target triplet is arm64-osx-static. It exists here
# only to add the macOS SDK resolution below -- without it, autotools ports fail to build
# on CMake 4. Everything else is inherited from vcpkg's own definition rather than copied,
# so this does not pin the triplet's contents.
include("${CMAKE_CURRENT_LIST_DIR}/../../external/vcpkg/triplets/arm64-osx.cmake")

include("${CMAKE_CURRENT_LIST_DIR}/../osx-sysroot.cmake")
# vcpkg hashes the triplet file but not what it include()s.
list(APPEND VCPKG_HASH_ADDITIONAL_FILES "${CMAKE_CURRENT_LIST_DIR}/../osx-sysroot.cmake")
