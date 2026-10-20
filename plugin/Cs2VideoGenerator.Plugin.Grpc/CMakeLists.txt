# CMakeList.txt : CMake project for Cs2VideoGenerator.Plugin.Grpc
# Modern CMake configuration for gRPC-based CS2 video generator plugin
#
cmake_minimum_required(VERSION 3.20)

# Set policies for modern CMake
cmake_policy(SET CMP0091 NEW) # MSVC runtime library flags
if(POLICY CMP0135)
    cmake_policy(SET CMP0135 NEW) # ExternalProject download methods
endif()

# Require toolchain to be provided by preset (no path scanning)
# Allow VCPKG_OVERLAY_TRIPLETS to be set for custom triplets
if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
    message(FATAL_ERROR "CMAKE_TOOLCHAIN_FILE is not set. Configure via the provided presets (debug/release) so the vcpkg toolchain is supplied.")
endif()

# Enable custom vcpkg triplets from this project
if(NOT DEFINED VCPKG_OVERLAY_TRIPLETS)
    set(VCPKG_OVERLAY_TRIPLETS "${CMAKE_CURRENT_SOURCE_DIR}/cmake/triplets")
endif()

# Default to static triplet for native builds so linkage matches cross-compilation presets.
# Cross presets set VCPKG_TARGET_TRIPLET explicitly; native presets leave it unset.
# CMAKE_HOST_SYSTEM_PROCESSOR is unavailable before project(), so use uname -m.
if(NOT DEFINED VCPKG_TARGET_TRIPLET)
    execute_process(COMMAND uname -m OUTPUT_VARIABLE _HOST_ARCH OUTPUT_STRIP_TRAILING_WHITESPACE)
    if(CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin")
        if(_HOST_ARCH STREQUAL "arm64")
            set(VCPKG_TARGET_TRIPLET "arm64-osx-static" CACHE STRING "")
        else()
            set(VCPKG_TARGET_TRIPLET "x64-osx-static" CACHE STRING "")
        endif()
    elseif(CMAKE_HOST_SYSTEM_NAME STREQUAL "Linux")
        if(_HOST_ARCH STREQUAL "aarch64")
            set(VCPKG_TARGET_TRIPLET "arm64-linux-static" CACHE STRING "")
        else()
            set(VCPKG_TARGET_TRIPLET "x64-linux-static" CACHE STRING "")
        endif()
    endif()
endif()

# Native Windows: build vcpkg ports under <repo>/.vcpkg-bt. gRPC's generated sources sit
# 181 characters below the buildtrees root, and the default root inside external/vcpkg
# pushes any clone deeper than ~15 characters past MAX_PATH, which cl.exe can't open
# (C1083) even with LongPathsEnabled. Appended rather than preset so that passing your
# own -DVCPKG_INSTALL_OPTIONS doesn't silently drop it; vcpkg.cmake writes the result
# back to the cache. An explicit --x-buildtrees-root wins.
if(CMAKE_HOST_WIN32 AND NOT VCPKG_INSTALL_OPTIONS MATCHES "--x-buildtrees-root")
    get_filename_component(_CSVG_BUILDTREES_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../.vcpkg-bt" ABSOLUTE)
    list(APPEND VCPKG_INSTALL_OPTIONS "--x-buildtrees-root=${_CSVG_BUILDTREES_ROOT}")
endif()

# Derive the CSVG plugin version from the repository's version.json (the same
# source Nerdbank.GitVersioning uses for the .NET packages) so the plugin binary
# reports a version comparable to the orchestrator's. Falls back to 0.0.0 if the
# file is missing or unparsable (never fail the build over version metadata).
set(_VERSION_JSON_PATH "${CMAKE_CURRENT_SOURCE_DIR}/../../version.json")
set(CSVG_PLUGIN_VERSION "0.0.0")
if(EXISTS "${_VERSION_JSON_PATH}")
    file(READ "${_VERSION_JSON_PATH}" _VERSION_JSON)
    string(JSON _RAW_VERSION ERROR_VARIABLE _VERSION_JSON_ERROR GET "${_VERSION_JSON}" version)
    if(NOT _VERSION_JSON_ERROR AND _RAW_VERSION MATCHES "^([0-9]+\\.[0-9]+\\.[0-9]+)")
        set(CSVG_PLUGIN_VERSION "${CMAKE_MATCH_1}")
    else()
        message(WARNING "Could not parse version from ${_VERSION_JSON_PATH}; embedding ${CSVG_PLUGIN_VERSION}")
    endif()
else()
    message(WARNING "version.json not found at ${_VERSION_JSON_PATH}; embedding ${CSVG_PLUGIN_VERSION}")
endif()

# Project definition with version
project(Cs2VideoGenerator.Plugin.GRPC
    VERSION ${CSVG_PLUGIN_VERSION}
    DESCRIPTION "CS2 Video Generator Plugin gRPC Server"
    LANGUAGES CXX C
)

# Configure HL2SDK (Windows x64 and Linux x64 only - CS2 supported platforms)
option(CSVG_SKIP_SERVER_PLUGIN "Explicitly skip building the CS2 server plugin (server.dll / server.so)" OFF)
set(BUILD_SERVER_PLUGIN OFF)
set(HL2SDK_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/external/hl2sdk")

# Check if target platform supports server plugin (Windows x64 or Linux x64)
set(TARGET_SUPPORTS_SERVER_PLUGIN OFF)
if(CMAKE_SYSTEM_NAME STREQUAL "Windows" AND CMAKE_SYSTEM_PROCESSOR MATCHES "AMD64|x86_64")
    set(TARGET_SUPPORTS_SERVER_PLUGIN ON)
    set(HL2SDK_PLATFORM "win64")
    # MSVC import/static libraries live under lib/public/win64
    set(HL2SDK_LIB_DIR "${HL2SDK_ROOT}/lib/public/win64")
    set(HL2SDK_LIBRARIES
        "${HL2SDK_LIB_DIR}/tier0.lib"
        "${HL2SDK_LIB_DIR}/tier1.lib"
    )
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux" AND CMAKE_SYSTEM_PROCESSOR MATCHES "x86_64|AMD64|amd64")
    set(TARGET_SUPPORTS_SERVER_PLUGIN ON)
    set(HL2SDK_PLATFORM "linux64")
    # Linux libraries live under lib/linux64 (NOT lib/public/linux64):
    # static tier1.a plus the libtier0.so stub (the game provides the real
    # libtier0.so at runtime). tier1.a must precede libtier0.so - GNU ld
    # resolves the archive's tier0 symbols from DSOs listed after it.
    set(HL2SDK_LIB_DIR "${HL2SDK_ROOT}/lib/linux64")
    set(HL2SDK_LIBRARIES
        "${HL2SDK_LIB_DIR}/tier1.a"
        "${HL2SDK_LIB_DIR}/libtier0.so"
    )
endif()

if(CSVG_SKIP_SERVER_PLUGIN)
    message(STATUS "Server plugin explicitly skipped (CSVG_SKIP_SERVER_PLUGIN=ON)")
elseif(NOT TARGET_SUPPORTS_SERVER_PLUGIN)
    message(STATUS "Server plugin only supported on Windows x64 and Linux x64 (CS2 platforms)")
else()
    if(NOT EXISTS "${HL2SDK_ROOT}/public")
        message(FATAL_ERROR
            "HL2SDK submodule is missing at ${HL2SDK_ROOT}.\n"
            "Run: git submodule update --init --recursive\n"
            "Or pass -DCSVG_SKIP_SERVER_PLUGIN=ON to explicitly skip building the server plugin.")
    endif()

    set(_HL2SDK_MISSING_LIBS "")
    foreach(LIB ${HL2SDK_LIBRARIES})
        if(NOT EXISTS "${LIB}")
            list(APPEND _HL2SDK_MISSING_LIBS "${LIB}")
        endif()
    endforeach()
    if(_HL2SDK_MISSING_LIBS)
        list(JOIN _HL2SDK_MISSING_LIBS "\n  " _HL2SDK_MISSING_LIBS_PRETTY)
        message(FATAL_ERROR
            "HL2SDK ${HL2SDK_PLATFORM} tier libraries are missing:\n"
            "  ${_HL2SDK_MISSING_LIBS_PRETTY}\n"
            "The server plugin (server.dll / server.so) cannot be built without them.\n"
            "Make sure the hl2sdk submodule is checked out at a cs2-branch commit that ships them\n"
            "(git submodule update --init --recursive), or pass -DCSVG_SKIP_SERVER_PLUGIN=ON\n"
            "to explicitly skip building the server plugin.")
    endif()

    # HL2SDK include directories (simplified)
    set(HL2SDK_INCLUDE_DIRS
        "${HL2SDK_ROOT}/public"
        "${HL2SDK_ROOT}/public/tier0"
        "${HL2SDK_ROOT}/public/tier1"
        "${HL2SDK_ROOT}/public/mathlib"
    )

    set(BUILD_SERVER_PLUGIN ON)
    message(STATUS "HL2SDK configured at: ${HL2SDK_ROOT} (${HL2SDK_PLATFORM})")
endif()

# Calculate repository root (2 levels up from this CMakeLists.txt)
get_filename_component(REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../.." ABSOLUTE)

# Determine the .NET Runtime Identifier (RID) for the target platform
# This allows native assets to be packaged in a NuGet-friendly structure
# Uses CMAKE_SYSTEM_* for cross-compilation support

# Determine the OS part based on target system
if(CMAKE_SYSTEM_NAME STREQUAL "Windows")
    set(DOTNET_OS "win")
elseif(CMAKE_SYSTEM_NAME STREQUAL "Darwin")
    set(DOTNET_OS "osx")
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    set(DOTNET_OS "linux")
else()
    message(WARNING "Unknown OS '${CMAKE_SYSTEM_NAME}' - using 'unknown'")
    set(DOTNET_OS "unknown")
endif()

# Determine the Architecture part based on target processor
if(CMAKE_SYSTEM_PROCESSOR MATCHES "amd64|x86_64|AMD64")
    set(DOTNET_ARCH "x64")
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64|ARM64")
    set(DOTNET_ARCH "arm64")
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "i386|i686|x86")
    set(DOTNET_ARCH "x86")
else()
    message(WARNING "Unknown architecture '${CMAKE_SYSTEM_PROCESSOR}' - using 'unknown'")
    set(DOTNET_ARCH "unknown")
endif()

set(DOTNET_RID "${DOTNET_OS}-${DOTNET_ARCH}")
message(STATUS "Target .NET RID: ${DOTNET_RID} (${CMAKE_SYSTEM_NAME} ${CMAKE_SYSTEM_PROCESSOR})")

# Set project-specific output directories for all build artifacts
set(PROJECT_ARTIFACTS_DIR "${REPO_ROOT}/artifacts/build/Cs2VideoGenerator.Plugin.GRPC")

# For single-config generators (Ninja, Makefiles), use CMAKE_BUILD_TYPE
# Binaries go to config/bin/RID for separation and NuGet packaging
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${PROJECT_ARTIFACTS_DIR}/${CMAKE_BUILD_TYPE}/lib/${DOTNET_RID}")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${PROJECT_ARTIFACTS_DIR}/${CMAKE_BUILD_TYPE}/bin/${DOTNET_RID}")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${PROJECT_ARTIFACTS_DIR}/${CMAKE_BUILD_TYPE}/bin/${DOTNET_RID}")

# Generated sources also land under artifacts so build trees can live elsewhere
set(GENERATED_DIR "${PROJECT_ARTIFACTS_DIR}/generated")

# Set per-configuration output directories (for multi-config generators like Visual Studio)
foreach(CONFIG ${CMAKE_CONFIGURATION_TYPES})
    string(TOUPPER ${CONFIG} CONFIG_UPPER)
    set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY_${CONFIG_UPPER} "${PROJECT_ARTIFACTS_DIR}/${CONFIG}/lib/${DOTNET_RID}")
    set(CMAKE_LIBRARY_OUTPUT_DIRECTORY_${CONFIG_UPPER} "${PROJECT_ARTIFACTS_DIR}/${CONFIG}/bin/${DOTNET_RID}")
    set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_${CONFIG_UPPER} "${PROJECT_ARTIFACTS_DIR}/${CONFIG}/bin/${DOTNET_RID}")
endforeach()

message(STATUS "=== Build Artifact Locations ===")
message(STATUS "Repository root: ${REPO_ROOT}")
message(STATUS ".NET RID: ${DOTNET_RID}")
message(STATUS "Project artifacts directory: ${PROJECT_ARTIFACTS_DIR}")
message(STATUS "================================")

# Prioritize vcpkg packages over system installations
set(CMAKE_FIND_PACKAGE_PREFER_CONFIG TRUE)
if(DEFINED VCPKG_TARGET_TRIPLET)
    list(PREPEND CMAKE_PREFIX_PATH "${_VCPKG_INSTALLED_DIR}/${VCPKG_TARGET_TRIPLET}")
endif()

# Set C++ standard with modern features
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF) # Disable compiler-specific extensions

# Build everything position-independent: cs2_proto_lib (static) is linked into
# the server_plugin shared library, which fails to link on Linux without -fPIC.
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Enable debug info in release builds. /Z7 (debug info in each object) rather than /Zi:
# the /experimental:deterministic that path-privacy.cmake passes to cl.exe makes /pathmap
# rewrite the /Fd compile-PDB path too, so /Zi tries to write its PDB under /csvg and
# fails with C1090. The linker still writes the .pdb for /DEBUG. CMake's own Debug and
# RelWithDebInfo defaults carry /Zi, hence the replace.
if(MSVC)
    add_compile_options(/Z7)
    add_link_options(/DEBUG)
    foreach(_csvg_flags_var IN ITEMS CMAKE_C_FLAGS_DEBUG CMAKE_CXX_FLAGS_DEBUG
                                     CMAKE_C_FLAGS_RELWITHDEBINFO CMAKE_CXX_FLAGS_RELWITHDEBINFO)
        string(REPLACE "/Zi" "/Z7" ${_csvg_flags_var} "${${_csvg_flags_var}}")
    endforeach()
endif()

# Path privacy for our own translation units. The vcpkg triplets carry the same maps
# for dependency ports (see cmake/path-privacy.cmake); this covers the sources CMake
# compiles directly. Verify with: strings <binary> | grep -c "$HOME"  -> 0.
include("${CMAKE_CURRENT_LIST_DIR}/cmake/path-privacy.cmake")
if(MSVC)
    # clang-cl takes this branch too, and needs the /clang: forwarding form.
    if(CMAKE_CXX_COMPILER_FRONTEND_VARIANT STREQUAL "MSVC" AND CMAKE_CXX_COMPILER_ID MATCHES "Clang")
        add_compile_options(${CSVG_PATH_MAP_CLANG_CL_LIST})
    else()
        add_compile_options(${CSVG_PATH_MAP_MSVC_LIST})
    endif()
    # /Zi above records the PDB's absolute path in the CodeView debug directory of the
    # image itself; the .pdb is not packaged but that string ships inside server.dll and
    # mock_server.exe. %_PDB% expands to the bare filename.
    add_link_options(/PDBALTPATH:%_PDB%)
else()
    add_compile_options(${CSVG_PATH_MAP_GNU_LIST})
endif()

# ...and no build-tree RPATH either. Linking server.so against the HL2SDK stubs in
# external/hl2sdk/lib/linux64 made CMake record that directory as DT_RUNPATH, so the
# shipped 1.0.0 plugin advertised the build machine's absolute path. Nothing needs it:
# inside CS2 the engine has already loaded tier0, and on a user's machine the recorded
# directory does not exist. Unlike __FILE__ strings, RPATH is a linker artifact that
# -ffile-prefix-map does not touch.
set(CMAKE_SKIP_BUILD_RPATH TRUE)

# Find required packages
set(protobuf_MODULE_COMPATIBLE TRUE)
find_package(gRPC REQUIRED)
find_package(Protobuf REQUIRED)

# Ensure we have a host triplet hint (vcpkg installs tools for the host)
# Use CMAKE_HOST_SYSTEM_* variables to detect the actual build machine,
# not the target platform (CMAKE_SYSTEM_* may differ during cross-compilation).
if(NOT VCPKG_HOST_TRIPLET)
    if(CMAKE_HOST_SYSTEM_NAME STREQUAL "Windows")
        if(CMAKE_HOST_SYSTEM_PROCESSOR MATCHES "AMD64|x86_64")
            set(VCPKG_HOST_TRIPLET "x64-windows")
        else()
            set(VCPKG_HOST_TRIPLET "x86-windows")
        endif()
    elseif(CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin")
        if(CMAKE_HOST_SYSTEM_PROCESSOR MATCHES "arm64|aarch64")
            set(VCPKG_HOST_TRIPLET "arm64-osx")
        else()
            set(VCPKG_HOST_TRIPLET "x64-osx")
        endif()
    elseif(CMAKE_HOST_SYSTEM_NAME STREQUAL "Linux")
        if(CMAKE_HOST_SYSTEM_PROCESSOR MATCHES "x86_64|AMD64")
            set(VCPKG_HOST_TRIPLET "x64-linux")
        else()
            set(VCPKG_HOST_TRIPLET "x86-linux")
        endif()
    endif()
endif()

# Derive the vcpkg installed root if the toolchain did not expose the helper
if(NOT DEFINED _VCPKG_INSTALLED_DIR)
    if(DEFINED VCPKG_INSTALLED_DIR)
        set(_VCPKG_INSTALLED_DIR "${VCPKG_INSTALLED_DIR}")
    else()
        set(_VCPKG_INSTALLED_DIR "${CMAKE_BINARY_DIR}/vcpkg_installed")
    endif()
endif()

# Validate proto file exists
set(PROTO_BASE_DIR "${CMAKE_SOURCE_DIR}/../../protos")
set(PROTO_FILE "${PROTO_BASE_DIR}/cs2-video-generator.proto")
if(NOT EXISTS "${PROTO_FILE}")
    message(FATAL_ERROR "Proto file not found: ${PROTO_FILE}")
endif()

# Ensure generated directory exists
file(MAKE_DIRECTORY "${GENERATED_DIR}")

# Record the HL2SDK submodule commit the plugin is built against (version handshake).
# Best-effort: "unknown" when git or the submodule checkout is unavailable.
set(CSVG_HL2SDK_COMMIT "unknown")
find_package(Git QUIET)
if(Git_FOUND AND EXISTS "${HL2SDK_ROOT}")
    execute_process(
        COMMAND "${GIT_EXECUTABLE}" -C "${HL2SDK_ROOT}" rev-parse HEAD
        OUTPUT_VARIABLE _HL2SDK_COMMIT
        OUTPUT_STRIP_TRAILING_WHITESPACE
        ERROR_QUIET
        RESULT_VARIABLE _HL2SDK_COMMIT_RESULT)
    if(_HL2SDK_COMMIT_RESULT EQUAL 0 AND _HL2SDK_COMMIT)
        set(CSVG_HL2SDK_COMMIT "${_HL2SDK_COMMIT}")
    endif()
endif()
message(STATUS "Embedding plugin version ${CSVG_PLUGIN_VERSION} (HL2SDK commit: ${CSVG_HL2SDK_COMMIT})")

# Generate plugin_version.h next to the generated proto sources so both the
# server plugin and the mock server (which must report the same version) see it.
configure_file(
    "${CMAKE_CURRENT_SOURCE_DIR}/cmake/plugin_version.h.in"
    "${GENERATED_DIR}/plugin_version.h"
    @ONLY)

# Ensure proto include path is visible to protobuf_generate_cpp
set(PROTOBUF_INCLUDE_DIR "${_VCPKG_INSTALLED_DIR}/${VCPKG_TARGET_TRIPLET}/include")
set(PROTOBUF_INCLUDE_DIRS "${PROTOBUF_INCLUDE_DIR}")
set(Protobuf_INCLUDE_DIRS "${PROTOBUF_INCLUDE_DIR}")
set(Protobuf_INCLUDE_DIR "${PROTOBUF_INCLUDE_DIR}")
set(PROTOBUF_IMPORT_DIRS "${PROTO_BASE_DIR};${PROTOBUF_INCLUDE_DIR}")

set(PROTOBUF_INCLUDE_DIR "${_VCPKG_INSTALLED_DIR}/${VCPKG_TARGET_TRIPLET}/include")
set(PROTOBUF_INCLUDE_DIRS "${PROTOBUF_INCLUDE_DIR}")
set(Protobuf_INCLUDE_DIRS "${PROTOBUF_INCLUDE_DIR}")
set(Protobuf_INCLUDE_DIR "${PROTOBUF_INCLUDE_DIR}")
set(PROTOBUF_IMPORT_DIRS "${PROTO_BASE_DIR};${PROTOBUF_INCLUDE_DIR}")

# Use packaged protoc and grpc_cpp_plugin for codegen without path scanning
set(PROTOC "${Protobuf_PROTOC_EXECUTABLE}")
# Host tools run on the build machine, so use host executable suffix (not target)
if(CMAKE_HOST_SYSTEM_NAME STREQUAL "Windows")
    set(_HOST_EXECUTABLE_SUFFIX ".exe")
else()
    set(_HOST_EXECUTABLE_SUFFIX "")
endif()
set(GRPC_CPP_PLUGIN "${_VCPKG_INSTALLED_DIR}/${VCPKG_HOST_TRIPLET}/tools/grpc/grpc_cpp_plugin${_HOST_EXECUTABLE_SUFFIX}")
if(NOT EXISTS "${GRPC_CPP_PLUGIN}")
    message(FATAL_ERROR "gRPC C++ plugin not found at ${GRPC_CPP_PLUGIN}")
endif()

get_filename_component(proto_name "${PROTO_FILE}" NAME_WE)
set(pb_cc "${GENERATED_DIR}/${proto_name}.pb.cc")
set(pb_h "${GENERATED_DIR}/${proto_name}.pb.h")
set(grpc_pb_cc "${GENERATED_DIR}/${proto_name}.grpc.pb.cc")
set(grpc_pb_h "${GENERATED_DIR}/${proto_name}.grpc.pb.h")

add_custom_command(
    OUTPUT ${pb_cc} ${pb_h} ${grpc_pb_cc} ${grpc_pb_h}
    COMMAND ${PROTOC}
    ARGS --proto_path=${PROTO_BASE_DIR}
         --cpp_out=${GENERATED_DIR}
         --grpc_out=${GENERATED_DIR}
         --plugin=protoc-gen-grpc=${GRPC_CPP_PLUGIN}
         ${PROTO_FILE}
    DEPENDS ${PROTO_FILE}
    COMMENT "Generating protobuf and gRPC sources for ${proto_name}"
    VERBATIM
)

set(GENERATED_PROTO_SOURCES ${pb_cc} ${grpc_pb_cc})
set(GENERATED_PROTO_HEADERS ${pb_h} ${grpc_pb_h})

# Function to configure HL2SDK for a target
function(configure_hl2sdk_target target_name)
    target_include_directories(${target_name} PRIVATE ${HL2SDK_INCLUDE_DIRS})
    target_link_libraries(${target_name} PRIVATE ${HL2SDK_LIBRARIES})

    # HL2SDK specific compiler options and definitions per toolchain
    if(MSVC)
        # HL2SDK compiler definitions for MSVC-style toolchains (Windows x64)
        target_compile_definitions(${target_name} PRIVATE
            COMPILER_MSVC
            COMPILER_MSVC64
            _USRDLL
        )

        target_compile_options(${target_name} PRIVATE
            /W3           # Warning level 3
            /WX-          # Don't treat warnings as errors
            $<$<CONFIG:Release>:/O2>        # Release optimization
            /GR           # Enable RTTI
            /EHsc         # Exception handling model
            /Zc:threadSafeInit-  # Disable thread-safe init
            /fp:precise   # Floating point model
            /Gd           # Calling convention
            /wd4267       # Silence size_t to int truncation noise from HL2SDK
            /wd4312       # Silence pointer-size cast warnings from HL2SDK
        )
        
        # Link options
        target_link_options(${target_name} PRIVATE
            /SUBSYSTEM:WINDOWS
            /LARGEADDRESSAWARE
            /NXCOMPAT
            /DYNAMICBASE
        )
    else()
        # HL2SDK compiler definitions for GCC/Clang targeting Linux x64
        # (the same set AlliedModders projects use for hl2sdk-cs2 Linux builds)
        target_compile_definitions(${target_name} PRIVATE
            _LINUX
            LINUX
            POSIX
            GNUC
            COMPILER_GCC
            PLATFORM_64BITS
            HAVE_STDINT_H
            stricmp=strcasecmp
            _stricmp=strcasecmp
            strnicmp=strncasecmp
            _strnicmp=strncasecmp
            strcmpi=strcasecmp
            _snprintf=snprintf
            _vsnprintf=vsnprintf
            _alloca=alloca
        )

        target_compile_options(${target_name} PRIVATE
            -fPIC
            -fno-strict-aliasing
            # HL2SDK headers are noisy; keep diagnostics readable without -Werror
            -Wno-unknown-pragmas
            -Wno-invalid-offsetof
            -Wno-overloaded-virtual
            -Wno-attributes
            -Wno-unused-parameter
        )

        # Link libstdc++/libgcc statically so the plugin does not depend on a
        # newer GLIBCXX than the Steam Runtime the game runs under provides.
        target_link_options(${target_name} PRIVATE
            -static-libstdc++
            -static-libgcc
        )
    endif()
endfunction()

# Create library for generated code (better organization)
add_library(cs2_proto_lib STATIC
    ${GENERATED_PROTO_SOURCES}
    ${GENERATED_PROTO_HEADERS}
)

# Set properties for generated library
target_include_directories(cs2_proto_lib PUBLIC 
    ${GENERATED_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}
)

target_link_libraries(cs2_proto_lib PUBLIC
    gRPC::grpc++
    protobuf::libprotobuf
)

# Disable warnings for generated code
if(MSVC)
    target_compile_options(cs2_proto_lib PRIVATE /W0)
else()
    target_compile_options(cs2_proto_lib PRIVATE -w)
endif()

# Main executable
add_executable(mock_server
    src/mock/mock_plugin.cpp
    src/mock/MockEngineInterface.cpp
    src/mock/mock_log.cpp
    src/plugin/Cs2PluginClient.cpp
)

# Set target properties
set_target_properties(mock_server PROPERTIES
    CXX_STANDARD 17
    CXX_STANDARD_REQUIRED ON
)

# Include directories and link libraries
target_include_directories(mock_server PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/include/plugin
    ${CMAKE_CURRENT_SOURCE_DIR}/include/mock
    ${GENERATED_DIR}
)

target_link_libraries(mock_server PRIVATE
    cs2_proto_lib
    gRPC::grpc++
)

# Compiler-specific optimizations and warnings
if(MSVC)
    target_compile_options(mock_server PRIVATE 
        /W4 /WX- # High warning level, don't treat warnings as errors
        $<$<CONFIG:Release>:/O2 /GL> # Optimize for speed, whole program optimization
    )
    target_link_options(mock_server PRIVATE
        $<$<CONFIG:Release>:/LTCG> # Link time code generation
    )
else()
    target_compile_options(mock_server PRIVATE
        -Wall -Wextra -Wpedantic
        $<$<CONFIG:Release>:-O3>
        $<$<CONFIG:Debug>:-g3 -O0>
    )
endif()

# Server Plugin (Windows x64 / Linux x64, HL2SDK)
if(BUILD_SERVER_PLUGIN)
    add_library(server_plugin SHARED
        src/plugin/server_plugin.cpp
        src/plugin/Cs2PluginClient.cpp
        src/plugin/present_capture.cpp
        ${HL2SDK_ROOT}/tier1/convar.cpp
    )

    # Configure HL2SDK for server plugin
    configure_hl2sdk_target(server_plugin)

    # The plugin binary must be named exactly server.dll (Windows) / server.so
    # (Linux) - it is loaded in place of the real server library via gameinfo.gi.
    if(CMAKE_SYSTEM_NAME STREQUAL "Windows")
        set(SERVER_PLUGIN_SUFFIX ".dll")
    else()
        set(SERVER_PLUGIN_SUFFIX ".so")
    endif()

    # Set target properties
    set_target_properties(server_plugin PROPERTIES
        CXX_STANDARD 17
        CXX_STANDARD_REQUIRED ON
        OUTPUT_NAME "server"
        PREFIX ""
        SUFFIX "${SERVER_PLUGIN_SUFFIX}"
    )

    # Include directories and link libraries
    target_include_directories(server_plugin PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/include
        ${CMAKE_CURRENT_SOURCE_DIR}/include/plugin
        ${CMAKE_CURRENT_SOURCE_DIR}/include/mock
        ${GENERATED_DIR}
    )

    # Re-enable gRPC client linkage for plugin control
    # CMAKE_DL_LIBS provides dlopen/dlsym used to load the real libserver.so on Linux
    find_package(Threads REQUIRED)
    target_link_libraries(server_plugin PRIVATE
        ${HL2SDK_LIBRARIES}
        cs2_proto_lib
        gRPC::grpc++
        Threads::Threads
        ${CMAKE_DL_LIBS}
    )

    message(STATUS "Server plugin target configured for HL2SDK (server${SERVER_PLUGIN_SUFFIX})")
else()
    message(STATUS "Server plugin target skipped (unsupported target platform or CSVG_SKIP_SERVER_PLUGIN=ON)")
endif()

# Installation rules (optional)
include(GNUInstallDirs)
install(TARGETS mock_server
    RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
    LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
)

# Development helpers
if(CMAKE_BUILD_TYPE STREQUAL "Debug")
    message(STATUS "Debug build - enabling additional development features")
    target_compile_definitions(mock_server PRIVATE DEBUG_MODE=1)
endif()

# Print configuration summary
message(STATUS "=== CS2 Video Generator Plugin Configuration ===")
message(STATUS "Build type: ${CMAKE_BUILD_TYPE}")
message(STATUS "C++ standard: ${CMAKE_CXX_STANDARD}")
message(STATUS "Compiler: ${CMAKE_CXX_COMPILER_ID} ${CMAKE_CXX_COMPILER_VERSION}")
message(STATUS "Protobuf version: ${Protobuf_VERSION}")
message(STATUS "gRPC version: ${gRPC_VERSION}")
message(STATUS "HL2SDK root: ${HL2SDK_ROOT}")
message(STATUS "Proto file: ${PROTO_FILE}")
message(STATUS "Generated files dir: ${GENERATED_DIR}")
message(STATUS "=====================================================")

# Optional: Add testing support
option(BUILD_TESTING "Build tests" OFF)
if(BUILD_TESTING)
    enable_testing()
    # Add test subdirectory if it exists
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/tests")
        add_subdirectory(tests)
    endif()
endif()