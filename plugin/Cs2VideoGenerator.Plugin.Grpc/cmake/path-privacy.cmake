# Path privacy: keep the build machine's filesystem layout out of shipped binaries.
#
# gRPC/abseil/OpenSSL expand __FILE__ in every assertion macro, so an unmapped build
# embeds the absolute path of every source file it compiled. Those end up as __TEXT
# string literals, not debug info, so strip(1) does NOT remove them -- the only fix is
# at compile time. Prior to this, the shipped osx-arm64 mock_server carried 404
# occurrences of the maintainer's home directory (see docs/build-path-privacy.md).
#
# Included from BOTH:
#   - the top-level CMakeLists.txt, for our own translation units, and
#   - every triplet in cmake/triplets, because vcpkg builds each port in a separate
#     CMake process where add_compile_options() from our project never applies.
#
# ORDERING RULE (measured, not assumed): with overlapping prefixes, GCC 13 honours the
# LAST matching map while Clang honours the LONGEST. Listing the broad catch-all first
# and the specific root last is correct under both.
#
# A vcpkg checkout outside the repo (VCPKG_ROOT) is covered by the $HOME map when it
# lives under the home directory; a system-wide one (e.g. /opt/vcpkg) discloses nothing
# in the first place.

# Deliberately has no include guard: a chainload toolchain and the top-level CMakeLists
# can both include it in the same scope, and an early return would leave the second
# consumer with empty variables and no diagnostic. Everything here is idempotent.

# This file lives at <repo>/plugin/Cs2VideoGenerator.Plugin.Grpc/cmake/, so the root is
# three levels up. Deriving it from the module's own location (rather than from
# CMAKE_SOURCE_DIR) means it stays correct when the build runs from a linked git
# worktree, where the worktree path -- not the main checkout -- is what gets baked in.
get_filename_component(CSVG_REPO_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../.." ABSOLUTE)

# A prefix containing a space cannot be passed through the flag strings vcpkg splits on
# whitespace; skip it rather than emit a broken flag that fails obscurely later.
set(_csvg_maps "")
if(DEFINED ENV{HOME} AND NOT "$ENV{HOME}" STREQUAL "" AND NOT "$ENV{HOME}" MATCHES " ")
    list(APPEND _csvg_maps "$ENV{HOME}=/csvg-home")
endif()
if(NOT CSVG_REPO_ROOT MATCHES " ")
    list(APPEND _csvg_maps "${CSVG_REPO_ROOT}=/csvg")
endif()

# Three spellings of the same maps:
#   GNU      - clang/apple-clang/gcc, and clang driving the OpenSSL cross build
#   CLANG_CL - clang-cl SILENTLY IGNORES a bare -ffile-prefix-map ("unknown argument
#              ignored in clang-cl"); it must be forwarded with /clang:
#   MSVC     - native cl.exe. UNVERIFIED: there is no native-MSVC leg today (win-x64
#              cross-compiles with clang-cl), and MSVC documents a single /pathmap:
#              taking ';'-separated pairs rather than a repeated flag. Emitted as
#              separate flags here because a ';' inside VCPKG_C_FLAGS would be re-split
#              as a CMake list. If a native Windows leg is ever added, check this against
#              a real build: should MSVC honour only the last flag, the repo-root map is
#              deliberately last, so the common case still maps. The release gate is the
#              backstop either way.
set(CSVG_PATH_MAP_GNU "")
set(CSVG_PATH_MAP_CLANG_CL "")
set(CSVG_PATH_MAP_MSVC "")
set(CSVG_PATH_MAP_GNU_LIST "")
set(CSVG_PATH_MAP_CLANG_CL_LIST "")
set(CSVG_PATH_MAP_MSVC_LIST "")
foreach(_csvg_map IN LISTS _csvg_maps)
    string(APPEND CSVG_PATH_MAP_GNU " -ffile-prefix-map=${_csvg_map}")
    string(APPEND CSVG_PATH_MAP_CLANG_CL " /clang:-ffile-prefix-map=${_csvg_map}")
    string(APPEND CSVG_PATH_MAP_MSVC " /pathmap:${_csvg_map}")
    list(APPEND CSVG_PATH_MAP_GNU_LIST "-ffile-prefix-map=${_csvg_map}")
    list(APPEND CSVG_PATH_MAP_CLANG_CL_LIST "/clang:-ffile-prefix-map=${_csvg_map}")
    list(APPEND CSVG_PATH_MAP_MSVC_LIST "/pathmap:${_csvg_map}")
endforeach()
string(STRIP "${CSVG_PATH_MAP_GNU}" CSVG_PATH_MAP_GNU)
string(STRIP "${CSVG_PATH_MAP_CLANG_CL}" CSVG_PATH_MAP_CLANG_CL)
string(STRIP "${CSVG_PATH_MAP_MSVC}" CSVG_PATH_MAP_MSVC)

# Appends the right spelling to VCPKG_C_FLAGS/VCPKG_CXX_FLAGS for a triplet. Appends
# (never assigns): the Linux triplets already carry -fPIC in these variables.
#   csvg_apply_path_privacy_flags(GNU|CLANG_CL|MSVC)
macro(csvg_apply_path_privacy_flags _csvg_flavor)
    set(VCPKG_C_FLAGS "${VCPKG_C_FLAGS} ${CSVG_PATH_MAP_${_csvg_flavor}}")
    set(VCPKG_CXX_FLAGS "${VCPKG_CXX_FLAGS} ${CSVG_PATH_MAP_${_csvg_flavor}}")
    string(STRIP "${VCPKG_C_FLAGS}" VCPKG_C_FLAGS)
    string(STRIP "${VCPKG_CXX_FLAGS}" VCPKG_CXX_FLAGS)
endmacro()
