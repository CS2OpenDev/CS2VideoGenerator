# vcpkg triplet for native Windows x86_64 builds
# Static linking for easier distribution

set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE static)
set(VCPKG_LIBRARY_LINKAGE static)

set(VCPKG_BUILD_TYPE release)

# Strip build-machine paths out of every port's __FILE__ expansions. Native MSVC's
# analogue of -ffile-prefix-map is /pathmap:.
include("${CMAKE_CURRENT_LIST_DIR}/../path-privacy.cmake")
csvg_apply_path_privacy_flags(MSVC)
