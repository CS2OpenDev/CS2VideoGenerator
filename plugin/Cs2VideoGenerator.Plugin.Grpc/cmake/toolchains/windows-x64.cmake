# Toolchain file for Windows x86_64 target (using clang-cl with MSVC ABI)
# This toolchain enables cross-compilation to Windows from non-Windows hosts
# Requires: clang-cl (LLVM), lld-link, and Windows SDK (via xwin or similar)
#
# Environment variables:
#   XWIN_DIR - Path to xwin splat output (contains sdk/ and crt/ directories)
#             Default locations checked: $HOME/.xwin, /opt/xwin
#
# Setup xwin:
#   1. Install: cargo install xwin  (or use pre-built binaries)
#   2. Download SDK: xwin --accept-license splat --output ~/.xwin
#   3. Use this toolchain

set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_VERSION 10.0)
set(CMAKE_SYSTEM_PROCESSOR AMD64)

# Find xwin directory
if(NOT DEFINED ENV{XWIN_DIR})
    # Check common locations
    if(EXISTS "$ENV{HOME}/.xwin")
        set(XWIN_DIR "$ENV{HOME}/.xwin")
    elseif(EXISTS "/opt/xwin")
        set(XWIN_DIR "/opt/xwin")
    else()
        message(WARNING "XWIN_DIR environment variable not set and xwin not found in common locations.\n"
                       "To enable Windows cross-compilation:\n"
                       "  1. Install xwin: cargo install xwin\n"
                       "  2. Download SDK: xwin --accept-license splat --output ~/.xwin\n"
                       "  3. Set XWIN_DIR=~/.xwin or place xwin output in ~/.xwin\n"
                       "Continuing with system settings, but Windows cross-compilation may fail.")
    endif()
else()
    set(XWIN_DIR "$ENV{XWIN_DIR}")
endif()

# Use clang-cl (MSVC-compatible clang driver) for Windows cross-compilation.
# clang-cl accepts MSVC-style flags (/W3, /D, /EHsc, /MT, etc.) which is
# required because many libraries (abseil, grpc, etc.) use MSVC flags when
# targeting Windows.
#
# On macOS, Homebrew installs clang-cl under /opt/homebrew/opt/llvm/bin
# which is not on PATH by default (keg-only formula).
set(_LLVM_HINTS "")
if(CMAKE_HOST_APPLE)
    execute_process(
        COMMAND brew --prefix llvm
        OUTPUT_VARIABLE _brew_llvm_prefix
        OUTPUT_STRIP_TRAILING_WHITESPACE
        ERROR_QUIET
        RESULT_VARIABLE _brew_result
    )
    if(_brew_result EQUAL 0 AND EXISTS "${_brew_llvm_prefix}/bin")
        set(_LLVM_HINTS "${_brew_llvm_prefix}/bin")
    endif()
endif()

if(NOT CMAKE_C_COMPILER)
    find_program(CLANG_CL_COMPILER clang-cl HINTS ${_LLVM_HINTS})
    if(CLANG_CL_COMPILER)
        set(CMAKE_C_COMPILER ${CLANG_CL_COMPILER})
    else()
        message(WARNING "clang-cl not found. Windows cross-compilation may fail.")
    endif()
endif()

if(NOT CMAKE_CXX_COMPILER)
    find_program(CLANG_CL_CXX_COMPILER clang-cl HINTS ${_LLVM_HINTS})
    if(CLANG_CL_CXX_COMPILER)
        set(CMAKE_CXX_COMPILER ${CLANG_CL_CXX_COMPILER})
    else()
        message(WARNING "clang-cl not found. Windows cross-compilation may fail.")
    endif()
endif()

# Use LLVM tools for linking and archiving
find_program(LLD_LINK lld-link HINTS ${_LLVM_HINTS})
find_program(LLVM_LIB llvm-lib HINTS ${_LLVM_HINTS})
find_program(LLVM_RC llvm-rc HINTS ${_LLVM_HINTS})

if(LLD_LINK)
    set(CMAKE_LINKER ${LLD_LINK})
endif()
if(LLVM_LIB)
    set(CMAKE_AR ${LLVM_LIB})
endif()
if(LLVM_RC)
    set(CMAKE_RC_COMPILER ${LLVM_RC})
endif()

# Target triple for Windows x64
set(TARGET_TRIPLE "x86_64-pc-windows-msvc")

# Configure compiler and linker flags if xwin is available
if(DEFINED XWIN_DIR AND EXISTS "${XWIN_DIR}/sdk/include")
    message(STATUS "Using xwin SDK from: ${XWIN_DIR}")

    # Windows SDK paths from xwin
    set(WINSDK_INCLUDE "${XWIN_DIR}/sdk/include")
    set(WINSDK_LIB "${XWIN_DIR}/sdk/lib")
    set(CRT_INCLUDE "${XWIN_DIR}/crt/include")
    set(CRT_LIB "${XWIN_DIR}/crt/lib")

    # clang-cl compiler flags for cross-compilation
    # -imsvc is the clang-cl flag for MSVC system include paths
    set(CROSS_COMPILE_FLAGS
        "--target=${TARGET_TRIPLE}"
        "-imsvc${CRT_INCLUDE}"
        "-imsvc${WINSDK_INCLUDE}/ucrt"
        "-imsvc${WINSDK_INCLUDE}/um"
        "-imsvc${WINSDK_INCLUDE}/shared"
        "-imsvc${WINSDK_INCLUDE}/winrt"
        "-imsvc${WINSDK_INCLUDE}/cppwinrt"
    )

    # Linker flags for xwin-based cross-compilation
    # lld-link understands /libpath: directly
    set(CROSS_LINK_FLAGS
        "/libpath:${CRT_LIB}/x86_64"
        "/libpath:${WINSDK_LIB}/ucrt/x86_64"
        "/libpath:${WINSDK_LIB}/um/x86_64"
    )

    # Convert lists to strings
    string(REPLACE ";" " " CROSS_COMPILE_FLAGS_STR "${CROSS_COMPILE_FLAGS}")
    string(REPLACE ";" " " CROSS_LINK_FLAGS_STR "${CROSS_LINK_FLAGS}")

    set(CMAKE_C_FLAGS_INIT "${CROSS_COMPILE_FLAGS_STR}")
    set(CMAKE_CXX_FLAGS_INIT "${CROSS_COMPILE_FLAGS_STR}")
    set(CMAKE_EXE_LINKER_FLAGS_INIT "${CROSS_LINK_FLAGS_STR}")
    set(CMAKE_SHARED_LINKER_FLAGS_INIT "${CROSS_LINK_FLAGS_STR}")
    set(CMAKE_MODULE_LINKER_FLAGS_INIT "${CROSS_LINK_FLAGS_STR}")

else()
    # Fallback for native Windows builds or when xwin is not available
    set(CMAKE_C_FLAGS_INIT "--target=${TARGET_TRIPLE}")
    set(CMAKE_CXX_FLAGS_INIT "--target=${TARGET_TRIPLE}")
endif()

# Path privacy (see cmake/path-privacy.cmake). Setting VCPKG_C_FLAGS/VCPKG_CXX_FLAGS in
# the triplet is NOT enough here: VCPKG_CHAINLOAD_TOOLCHAIN_FILE replaces vcpkg's own
# toolchain, which is what would otherwise append them -- the same reason /MT has to be
# set explicitly below. Verified: without this, the flags never reach clang-cl.
# The /clang: forwarding form is required; clang-cl ignores a bare -ffile-prefix-map.
include("${CMAKE_CURRENT_LIST_DIR}/../path-privacy.cmake")
string(APPEND CMAKE_C_FLAGS_INIT " ${CSVG_PATH_MAP_CLANG_CL}")
string(APPEND CMAKE_CXX_FLAGS_INIT " ${CSVG_PATH_MAP_CLANG_CL}")

# Windows-specific settings
set(CMAKE_EXECUTABLE_SUFFIX ".exe")
set(CMAKE_SHARED_LIBRARY_PREFIX "")
set(CMAKE_SHARED_LIBRARY_SUFFIX ".dll")
set(CMAKE_STATIC_LIBRARY_PREFIX "")
set(CMAKE_STATIC_LIBRARY_SUFFIX ".lib")

# Search paths for cross-compilation
# Don't search host programs on target paths
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
# Use BOTH for library/include/package so vcpkg-installed packages can be found
# alongside xwin SDK paths. ONLY mode blocks vcpkg's package discovery.
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY BOTH)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE BOTH)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE BOTH)

# MSVC runtime library settings
# When cross-compiling with xwin, only release CRT libs are available (no libcmtd.lib).
# Use release CRT unconditionally to avoid linker errors during compiler testing.
if(DEFINED XWIN_DIR)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
    set(CMAKE_TRY_COMPILE_CONFIGURATION "Release")
    # Also embed CRT flag directly in compiler flags for projects that don't support
    # CMP0091 (e.g. utf8-range). Without this, CMake defaults to /MD (dynamic CRT).
    set(CMAKE_C_FLAGS_RELEASE "/MT /O2 /Ob2 /DNDEBUG" CACHE STRING "")
    set(CMAKE_CXX_FLAGS_RELEASE "/MT /O2 /Ob2 /DNDEBUG" CACHE STRING "")
    set(CMAKE_C_FLAGS_DEBUG "/MTd /Zi /Ob0 /Od /RTC1" CACHE STRING "")
    set(CMAKE_CXX_FLAGS_DEBUG "/MTd /Zi /Ob0 /Od /RTC1" CACHE STRING "")
else()
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()
