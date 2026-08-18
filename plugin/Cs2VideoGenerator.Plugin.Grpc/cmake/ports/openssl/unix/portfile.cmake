if (VCPKG_TARGET_IS_LINUX)
    message(NOTICE [[
openssl requires Linux kernel headers from the system package manager.
   They can be installed on Alpine systems via `apk add linux-headers`.
   They can be installed on Ubuntu systems via `apt install linux-libc-dev`.
]])
endif()

if(VCPKG_HOST_IS_WINDOWS)
    vcpkg_acquire_msys(MSYS_ROOT PACKAGES make perl)
    set(MAKE "${MSYS_ROOT}/usr/bin/make.exe")
    set(PERL "${MSYS_ROOT}/usr/bin/perl.exe")
else()
    find_program(MAKE make)
    if(NOT MAKE)
        message(FATAL_ERROR "Could not find make. Please install it through your package manager.")
    endif()
    vcpkg_find_acquire_program(PERL)
endif()
set(INTERPRETER "${PERL}")

execute_process(
    COMMAND "${PERL}" -e "use IPC::Cmd;"
    RESULT_VARIABLE perl_ipc_cmd_result
)
if(NOT perl_ipc_cmd_result STREQUAL "0")
    message(FATAL_ERROR "\nPerl cannot find IPC::Cmd. Please install it through your system package manager.\n")
endif()

# Ideally, OpenSSL should use `CC` from vcpkg as is (absolute path).
# But in reality, OpenSSL expects to locate the compiler via `PATH`,
# and it makes its own choices e.g. for Android.
vcpkg_cmake_get_vars(cmake_vars_file)
include("${cmake_vars_file}")
cmake_path(GET VCPKG_DETECTED_CMAKE_C_COMPILER PARENT_PATH compiler_path)
cmake_path(GET VCPKG_DETECTED_CMAKE_C_COMPILER FILENAME compiler_name)
find_program(compiler_in_path NAMES "${compiler_name}" PATHS ENV PATH NO_DEFAULT_PATH)
if(NOT compiler_in_path)
    vcpkg_host_path_list(APPEND ENV{PATH} "${compiler_path}")
elseif(NOT compiler_in_path STREQUAL VCPKG_DETECTED_CMAKE_C_COMPILER)
    vcpkg_host_path_list(PREPEND ENV{PATH} "${compiler_path}")
endif()

vcpkg_list(SET MAKEFILE_OPTIONS)
if(VCPKG_TARGET_IS_ANDROID)
    set(ENV{ANDROID_NDK_ROOT} "${VCPKG_DETECTED_CMAKE_ANDROID_NDK}")
    set(OPENSSL_ARCH "android-${VCPKG_DETECTED_CMAKE_ANDROID_ARCH}")
    if(VCPKG_DETECTED_CMAKE_ANDROID_ARCH STREQUAL "arm" AND NOT VCPKG_DETECTED_CMAKE_ANDROID_ARM_NEON)
        vcpkg_list(APPEND CONFIGURE_OPTIONS no-asm)
    endif()
elseif(VCPKG_TARGET_IS_LINUX)
    if(VCPKG_TARGET_ARCHITECTURE MATCHES "arm64")
        set(OPENSSL_ARCH linux-aarch64)
    elseif(VCPKG_TARGET_ARCHITECTURE MATCHES "arm")
        set(OPENSSL_ARCH linux-armv4)
    elseif(VCPKG_TARGET_ARCHITECTURE MATCHES "x64")
        set(OPENSSL_ARCH linux-x86_64)
    elseif(VCPKG_TARGET_ARCHITECTURE MATCHES "x86")
        set(OPENSSL_ARCH linux-x86)
    else()
        set(OPENSSL_ARCH linux-generic32)
    endif()
elseif(VCPKG_TARGET_IS_IOS)
    if(VCPKG_TARGET_ARCHITECTURE MATCHES "arm64")
        set(OPENSSL_ARCH ios64-xcrun)
    elseif(VCPKG_TARGET_ARCHITECTURE MATCHES "arm")
        set(OPENSSL_ARCH ios-xcrun)
    elseif(VCPKG_TARGET_ARCHITECTURE MATCHES "x86" OR VCPKG_TARGET_ARCHITECTURE MATCHES "x64")
        set(OPENSSL_ARCH iossimulator-xcrun)
    else()
        message(FATAL_ERROR "Unknown iOS target architecture: ${VCPKG_TARGET_ARCHITECTURE}")
    endif()
    # disable that makes linkage error (e.g. require stderr usage)
    list(APPEND CONFIGURE_OPTIONS no-ui no-asm)
elseif(VCPKG_TARGET_IS_TVOS OR VCPKG_TARGET_IS_WATCHOS)
    set(OPENSSL_ARCH iphoneos-cross)
    # disable that makes linkage error (e.g. require stderr usage)
    list(APPEND CONFIGURE_OPTIONS no-ui no-asm)
elseif(VCPKG_TARGET_IS_OSX)
    if(VCPKG_TARGET_ARCHITECTURE MATCHES "arm64")
        set(OPENSSL_ARCH darwin64-arm64)
    else()
        set(OPENSSL_ARCH darwin64-x86_64)
    endif()
elseif(VCPKG_TARGET_IS_BSD)
    set(OPENSSL_ARCH BSD-nodef-generic64)
elseif(VCPKG_TARGET_IS_SOLARIS)
    if(VCPKG_TARGET_ARCHITECTURE STREQUAL "x64")
        set(OPENSSL_ARCH solaris64-x86_64-gcc)
    else()
        set(OPENSSL_ARCH solaris-x86-gcc)
    endif()
elseif(VCPKG_TARGET_IS_MINGW)
    if(VCPKG_TARGET_ARCHITECTURE STREQUAL "x64")
        set(OPENSSL_ARCH mingw64)
    elseif(VCPKG_TARGET_ARCHITECTURE STREQUAL "arm64")
        set(OPENSSL_ARCH mingwarm64)
    else()
        set(OPENSSL_ARCH mingw)
    endif()
elseif(VCPKG_TARGET_IS_EMSCRIPTEN)
    set(OPENSSL_ARCH linux-x32)
    vcpkg_list(APPEND CONFIGURE_OPTIONS
        no-engine
        no-asm
        no-sse2
        no-srtp
        --cross-compile-prefix=
    )
    # Cf. https://emscripten.org/docs/porting/pthreads.html:
    # For Pthreads support, not just openssl but everything
    # must be compiled and linked with `-pthread`.
    # This makes it a triplet/toolchain-wide setting.
    if(NOT " ${VCPKG_DETECTED_CMAKE_C_FLAGS} " MATCHES " -pthread ")
        vcpkg_list(APPEND CONFIGURE_OPTIONS no-threads)
    endif()
elseif(VCPKG_TARGET_IS_WINDOWS AND NOT VCPKG_HOST_IS_WINDOWS)
    # Cross-compiling to Windows from Unix host
    # Use custom OpenSSL config target with Unix build scheme (GNU make, not nmake)

    # Find xwin SDK
    if(DEFINED ENV{XWIN_DIR})
        set(_xwin_dir "$ENV{XWIN_DIR}")
    elseif(EXISTS "$ENV{HOME}/.xwin-cache/splat/sdk/include")
        set(_xwin_dir "$ENV{HOME}/.xwin-cache/splat")
    elseif(EXISTS "$ENV{HOME}/.xwin/sdk/include")
        set(_xwin_dir "$ENV{HOME}/.xwin")
    elseif(EXISTS "/opt/xwin/sdk/include")
        set(_xwin_dir "/opt/xwin")
    else()
        message(FATAL_ERROR "Windows cross-compilation requires xwin SDK.\n"
                "Install: cargo install xwin\n"
                "Download: xwin --accept-license splat --output ~/.xwin\n"
                "Set: export XWIN_DIR=~/.xwin")
    endif()

    # Write custom OpenSSL config target
    set(_cross_config_dir "${CURRENT_BUILDTREES_DIR}/cross-config")
    file(MAKE_DIRECTORY "${_cross_config_dir}")
    configure_file("${CMAKE_CURRENT_LIST_DIR}/../cross-windows/10-clang-cross.conf"
                   "${_cross_config_dir}/10-clang-cross.conf" COPYONLY)
    set(ENV{OPENSSL_LOCAL_CONFIG_DIR} "${_cross_config_dir}")

    # Override compiler to plain clang (not clang-cl) for Unix Makefile compat.
    # vcpkg_configure_make uses z_vcpkg_get_cmake_vars internally (separate from
    # vcpkg_cmake_get_vars used above), generating its own cmake-vars-* files.
    # We patch our cmake-get-vars wrapper with overrides, then pre-seed the
    # z_vcpkg_get_cmake_vars cache to make vcpkg_configure_make and vcpkg_build_make
    # use our patched file instead of generating a new one.
    find_program(_clang clang REQUIRED)
    find_program(_llvm_ar llvm-ar REQUIRED)
    find_program(_llvm_ranlib llvm-ranlib REQUIRED)

    # Patch the cmake vars wrapper file to append our overrides.
    set(_vars_wrapper "${cmake_vars_file}")
    message(STATUS "Patching cmake vars file: ${_vars_wrapper}")
    if(EXISTS "${_vars_wrapper}")
        # Build replacement C flags: use -isystem (clang) instead of -imsvc (clang-cl)
        # These flags REPLACE the detected ones, which is where the triplet's
        # VCPKG_C_FLAGS would normally have arrived -- so the path-privacy maps have to
        # be re-added here explicitly, in the GNU spelling (this build drives plain
        # clang, not clang-cl). CSVG_PATH_MAP_GNU comes from the triplet.
        set(_cross_cflags "--target=x86_64-pc-windows-msvc -isystem ${_xwin_dir}/crt/include -isystem ${_xwin_dir}/sdk/include/ucrt -isystem ${_xwin_dir}/sdk/include/um -isystem ${_xwin_dir}/sdk/include/shared -O2 -DNDEBUG ${CSVG_PATH_MAP_GNU}")
        set(_cross_cxxflags "${_cross_cflags}")
        file(APPEND "${_vars_wrapper}" "
# OpenSSL cross-compile overrides: use plain clang instead of clang-cl
set(VCPKG_DETECTED_CMAKE_C_COMPILER \"${_clang}\")
set(VCPKG_DETECTED_CMAKE_CXX_COMPILER \"${_clang}++\")
set(VCPKG_DETECTED_CMAKE_C_COMPILER_ID \"Clang\")
set(VCPKG_DETECTED_CMAKE_CXX_COMPILER_ID \"Clang\")
set(VCPKG_DETECTED_CMAKE_C_COMPILER_FRONTEND_VARIANT \"GNU\")
set(VCPKG_DETECTED_CMAKE_CXX_COMPILER_FRONTEND_VARIANT \"GNU\")
set(VCPKG_DETECTED_CMAKE_AR \"${_llvm_ar}\")
set(VCPKG_DETECTED_CMAKE_RANLIB \"${_llvm_ranlib}\")
set(VCPKG_DETECTED_CMAKE_C_FLAGS_RELEASE \"${_cross_cflags}\")
set(VCPKG_DETECTED_CMAKE_CXX_FLAGS_RELEASE \"${_cross_cxxflags}\")
set(VCPKG_DETECTED_CMAKE_STATIC_LINKER_FLAGS_RELEASE \"\")
set(VCPKG_DETECTED_CMAKE_SHARED_LINKER_FLAGS_RELEASE \"\")
set(VCPKG_DETECTED_CMAKE_EXE_LINKER_FLAGS_RELEASE \"\")
")
    endif()

    # Pre-seed z_vcpkg_get_cmake_vars cache so vcpkg_configure_make and
    # vcpkg_build_make use our patched file instead of detecting their own.
    if(DEFINED VCPKG_BUILD_TYPE)
        set(Z_VCPKG_GET_CMAKE_VARS_FILE_${VCPKG_BUILD_TYPE} "${cmake_vars_file}"
            CACHE PATH "Patched cmake vars for OpenSSL cross-compile" FORCE)
    else()
        set(Z_VCPKG_GET_CMAKE_VARS_FILE "${cmake_vars_file}"
            CACHE PATH "Patched cmake vars for OpenSSL cross-compile" FORCE)
    endif()

    set(OPENSSL_ARCH clang-cross-win64a)
    vcpkg_list(APPEND CONFIGURE_OPTIONS no-asm)
else()
    message(FATAL_ERROR "Unknown platform")
endif()

file(MAKE_DIRECTORY "${SOURCE_PATH}/vcpkg")
file(COPY "${CMAKE_CURRENT_LIST_DIR}/configure" DESTINATION "${SOURCE_PATH}/vcpkg")
vcpkg_configure_make(
    SOURCE_PATH "${SOURCE_PATH}"
    PROJECT_SUBPATH "vcpkg"
    NO_ADDITIONAL_PATHS
    OPTIONS
        "${INTERPRETER}"
        "${SOURCE_PATH}/Configure"
        ${OPENSSL_ARCH}
        ${CONFIGURE_OPTIONS}
        "--openssldir=/etc/ssl"
        "--libdir=lib"
    OPTIONS_DEBUG
        --debug
)
vcpkg_install_make(
    ${MAKEFILE_OPTIONS}
    BUILD_TARGET build_inst_sw
)

# Rename Unix-style .a archives to .lib for Windows target
if(VCPKG_TARGET_IS_WINDOWS AND NOT VCPKG_HOST_IS_WINDOWS)
    foreach(_lib ssl crypto)
        if(EXISTS "${CURRENT_PACKAGES_DIR}/lib/lib${_lib}.a")
            file(RENAME "${CURRENT_PACKAGES_DIR}/lib/lib${_lib}.a"
                        "${CURRENT_PACKAGES_DIR}/lib/lib${_lib}.lib")
        endif()
    endforeach()
endif()

vcpkg_fixup_pkgconfig()

if("tools" IN_LIST FEATURES)
    file(MAKE_DIRECTORY "${CURRENT_PACKAGES_DIR}/tools/${PORT}")
    file(RENAME "${CURRENT_PACKAGES_DIR}/bin/c_rehash" "${CURRENT_PACKAGES_DIR}/tools/${PORT}/c_rehash")
    file(REMOVE "${CURRENT_PACKAGES_DIR}/debug/bin/c_rehash")
    vcpkg_copy_tools(TOOL_NAMES openssl AUTO_CLEAN)
elseif(VCPKG_LIBRARY_LINKAGE STREQUAL "static" OR NOT VCPKG_TARGET_IS_WINDOWS)
    file(REMOVE_RECURSE "${CURRENT_PACKAGES_DIR}/bin" "${CURRENT_PACKAGES_DIR}/debug/bin")
    file(REMOVE_RECURSE "${CURRENT_PACKAGES_DIR}/etc/ssl/misc")
endif()

file(TOUCH "${CURRENT_PACKAGES_DIR}/etc/ssl/certs/.keep")
file(TOUCH "${CURRENT_PACKAGES_DIR}/etc/ssl/private/.keep")

file(REMOVE_RECURSE
    "${CURRENT_PACKAGES_DIR}/debug/etc"
    "${CURRENT_PACKAGES_DIR}/debug/include"
    "${CURRENT_PACKAGES_DIR}/debug/share"
)

# For consistency of mingw build with nmake build
file(GLOB engines "${CURRENT_PACKAGES_DIR}/lib/ossl-modules/*.dll")
if(NOT engines STREQUAL "")
    file(COPY ${engines} DESTINATION "${CURRENT_PACKAGES_DIR}/bin")
    file(REMOVE_RECURSE "${CURRENT_PACKAGES_DIR}/lib/ossl-modules")
endif()
file(GLOB engines "${CURRENT_PACKAGES_DIR}/debug/lib/ossl-modules/*.dll")
if(NOT engines STREQUAL "")
    file(COPY ${engines} DESTINATION "${CURRENT_PACKAGES_DIR}/debug/bin")
    file(REMOVE_RECURSE "${CURRENT_PACKAGES_DIR}/debug/lib/ossl-modules")
endif()
