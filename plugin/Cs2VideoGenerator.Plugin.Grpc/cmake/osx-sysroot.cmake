# Resolve the macOS SDK for vcpkg port builds.
#
# Every macOS triplet -- ours and vcpkg's built-in ones -- sets VCPKG_CMAKE_SYSTEM_NAME
# to Darwin, which marks port builds as cross-compiling. CMake 4 no longer auto-detects
# CMAKE_OSX_SYSROOT in that case, so vcpkg's detected flags come out as
#
#     -isysroot  -mmacosx-version-min=11.0 ...
#
# with nothing between `-isysroot` and the next flag. The compiler consumes
# `-mmacosx-version-min=11.0` as the sysroot path and every #include then fails with
# "'sys/types.h' file not found". Autotools ports (OpenSSL) hit this first.
#
# Included from the macOS triplets in cmake/triplets/, including the overlay of vcpkg's
# built-in host triplet (host tools like protoc build with that one).

if(NOT DEFINED VCPKG_OSX_SYSROOT AND CMAKE_HOST_SYSTEM_NAME STREQUAL "Darwin")
    execute_process(COMMAND xcrun --sdk macosx --show-sdk-path
                    OUTPUT_VARIABLE _csvg_osx_sdk
                    OUTPUT_STRIP_TRAILING_WHITESPACE
                    ERROR_QUIET)
    if(_csvg_osx_sdk AND EXISTS "${_csvg_osx_sdk}")
        set(VCPKG_OSX_SYSROOT "${_csvg_osx_sdk}")
    endif()
endif()
