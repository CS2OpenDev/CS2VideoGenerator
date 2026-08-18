#!/bin/bash
# Cross-Compilation Test Script
# Tests cmake configuration for all available presets on the current platform
# Can be run on Linux, macOS, or Windows (via Git Bash/WSL)

set -eo pipefail

# Always run from the project root (one level up from scripts/)
cd "$(dirname "${BASH_SOURCE[0]}")/.."

# Colors for output
COLOR_GREEN='\033[0;32m'
COLOR_YELLOW='\033[1;33m'
COLOR_RED='\033[0;31m'
COLOR_BLUE='\033[0;34m'
COLOR_RESET='\033[0m'

print_header() {
    echo -e "${COLOR_BLUE}========================================${COLOR_RESET}"
    echo -e "${COLOR_BLUE}$1${COLOR_RESET}"
    echo -e "${COLOR_BLUE}========================================${COLOR_RESET}"
}

print_success() {
    echo -e "${COLOR_GREEN}✓${COLOR_RESET} $1"
}

print_warning() {
    echo -e "${COLOR_YELLOW}⚠${COLOR_RESET} $1"
}

print_error() {
    echo -e "${COLOR_RED}✗${COLOR_RESET} $1"
}

print_info() {
    echo -e "${COLOR_BLUE}ℹ${COLOR_RESET} $1"
}

# Configuration
TEST_BUILD="${TEST_BUILD:-true}"  # configure and build by default; set to false to only test configure
ARTIFACTS_DIR="../../artifacts/test"

# Counters
TOTAL_TESTS=0
PASSED_TESTS=0
FAILED_TESTS=0
SKIPPED_TESTS=0

# Results arrays
declare -a TEST_RESULTS
declare -a TEST_DETAILS
LAST_VERIFY_OUTPUT=""

# Verify that a built artifact has the correct binary format for its target platform
verify_build_artifact() {
    local preset_name=$1
    local project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    local artifacts_base="${project_dir}/../../artifacts/build/Cs2VideoGenerator.Plugin.GRPC"

    # Determine build type from preset name
    local build_type=""
    case "$preset_name" in
        debug-unix|debug)   build_type="Debug" ;;
        release-unix|release) build_type="Release" ;;
        cross-*)            build_type="Release" ;;
        *)
            print_warning "Unknown preset '$preset_name' — skipping artifact verification"
            return 0
            ;;
    esac

    # Determine RID and executable name from preset name
    local rid=""
    local exe_name="mock_server"
    case "$preset_name" in
        cross-linux-x64)    rid="linux-x64" ;;
        cross-linux-arm64)  rid="linux-arm64" ;;
        cross-windows-x64)  rid="win-x64"; exe_name="mock_server.exe" ;;
        cross-osx-arm64)    rid="osx-arm64" ;;
        cross-osx-x64)      rid="osx-x64" ;;
        *)
            # Native build — determine RID from host platform
            local host_os host_arch
            host_os=$(uname -s)
            host_arch=$(uname -m)
            case "${host_os}-${host_arch}" in
                Darwin-arm64)   rid="osx-arm64" ;;
                Darwin-x86_64)  rid="osx-x64" ;;
                Linux-x86_64)   rid="linux-x64" ;;
                Linux-aarch64)  rid="linux-arm64" ;;
            esac
            ;;
    esac

    if [ -z "$rid" ]; then
        print_warning "Could not determine RID for preset '$preset_name' — skipping artifact verification"
        return 0
    fi

    # Expected file(1) output pattern for each RID
    local expected_pattern=""
    case "$rid" in
        linux-x64)    expected_pattern="ELF 64-bit.*x86-64" ;;
        linux-arm64)  expected_pattern="ELF 64-bit.*aarch64" ;;
        win-x64)      expected_pattern="PE32\+.*x86-64" ;;
        osx-arm64)    expected_pattern="Mach-O 64-bit.*arm64" ;;
        osx-x64)      expected_pattern="Mach-O 64-bit.*x86_64" ;;
        *)
            print_warning "No file pattern defined for RID '$rid' — skipping artifact verification"
            return 0
            ;;
    esac

    local artifact_path="${artifacts_base}/${build_type}/bin/${rid}/${exe_name}"

    if [ ! -f "$artifact_path" ]; then
        print_error "Artifact not found: $artifact_path"
        return 1
    fi

    local file_output
    file_output=$(file "$artifact_path")
    LAST_VERIFY_OUTPUT="$file_output"

    if echo "$file_output" | grep -qE "$expected_pattern"; then
        print_success "Artifact verified: $file_output"
        return 0
    else
        print_error "Artifact format mismatch for $preset_name (target: $rid)"
        print_error "  Expected pattern: $expected_pattern"
        print_error "  Actual: $file_output"
        return 1
    fi
}

# Function to test a preset
test_preset() {
    local preset_name=$1
    local test_type=${2:-"configure"}  # "configure" or "build"

    TOTAL_TESTS=$((TOTAL_TESTS + 1))

    print_info "Testing preset: $preset_name ($test_type)"

    # Create log directory
    local log_file="${ARTIFACTS_DIR}/${preset_name}.configure.log"

    # Run cmake and capture exit code without a pipe (pipes mask exit codes)
    local cmake_exit=0
    cmake --preset "$preset_name" > "$log_file" 2>&1 || cmake_exit=$?
    cat "$log_file"

    # Also check for cmake error messages in case cmake returns 0 despite errors
    if [ $cmake_exit -eq 0 ] && grep -q "Configuring incomplete, errors occurred" "$log_file" 2>/dev/null; then
        cmake_exit=1
    fi

    if [ $cmake_exit -eq 0 ]; then
        print_success "Configure succeeded for $preset_name"

        if [ "$test_type" = "build" ] && [ "$TEST_BUILD" = "true" ]; then
            # Try to build
            local build_log="${ARTIFACTS_DIR}/${preset_name}.build.log"
            local build_exit=0
            cmake --build --preset "$preset_name" --parallel > "$build_log" 2>&1 || build_exit=$?
            cat "$build_log"

            if [ $build_exit -eq 0 ]; then
                print_success "Build succeeded for $preset_name"
                # Verify the artifact is the correct binary format for the target
                LAST_VERIFY_OUTPUT=""
                if verify_build_artifact "$preset_name"; then
                    PASSED_TESTS=$((PASSED_TESTS + 1))
                    TEST_RESULTS+=("PASS: $preset_name (configure + build + verified)")
                    TEST_DETAILS+=("$LAST_VERIFY_OUTPUT")
                    return 0
                else
                    FAILED_TESTS=$((FAILED_TESTS + 1))
                    TEST_RESULTS+=("FAIL: $preset_name (build succeeded but artifact verification failed)")
                    TEST_DETAILS+=("$LAST_VERIFY_OUTPUT")
                    return 1
                fi
            else
                print_error "Build failed for $preset_name"
                FAILED_TESTS=$((FAILED_TESTS + 1))
                TEST_RESULTS+=("FAIL: $preset_name (build failed)")
                TEST_DETAILS+=("")
                return 1
            fi
        else
            PASSED_TESTS=$((PASSED_TESTS + 1))
            TEST_RESULTS+=("PASS: $preset_name (configure only)")
            TEST_DETAILS+=("")
            return 0
        fi
    else
        if grep -q "condition" "$log_file" 2>/dev/null; then
            print_warning "Preset $preset_name skipped (condition not met)"
            SKIPPED_TESTS=$((SKIPPED_TESTS + 1))
            TEST_RESULTS+=("SKIP: $preset_name (condition not met)")
            TEST_DETAILS+=("")
            return 2
        else
            print_error "Configure failed for $preset_name (exit code: $cmake_exit)"
            FAILED_TESTS=$((FAILED_TESTS + 1))
            TEST_RESULTS+=("FAIL: $preset_name (configure failed)")
            TEST_DETAILS+=("")
            return 1
        fi
    fi
}

# Main test execution
main() {
    print_header "Cross-Compilation Test Suite"
    echo ""

    # Display system info
    print_info "System: $(uname -s) $(uname -m)"
    print_info "CMake: $(cmake --version | head -1)"
    print_info "Test mode: $([ "$TEST_BUILD" = "true" ] && echo "configure + build" || echo "configure only (TEST_BUILD=false)")"
    echo ""

    # Check prerequisites
    print_header "Checking Prerequisites"

    if [ -x "external/vcpkg/vcpkg" ] || [ -x "external/vcpkg/vcpkg.exe" ]; then
        print_success "vcpkg submodule: external/vcpkg"
    elif [ -f "external/vcpkg/bootstrap-vcpkg.sh" ]; then
        print_error "vcpkg submodule present but not bootstrapped — run: ./external/vcpkg/bootstrap-vcpkg.sh"
        exit 1
    else
        print_error "vcpkg submodule not found — run: git submodule update --init"
        exit 1
    fi

    # Create artifacts directory
    mkdir -p "$ARTIFACTS_DIR"

    echo ""
    print_header "Getting Available Presets"

    # Get list of configure presets
    local presets=$(cmake --list-presets 2>/dev/null | grep -E '^\s+"[^"]+"\s*-' | sed 's/^[[:space:]]*"\([^"]*\)".*/\1/')

    if [ -z "$presets" ]; then
        print_error "No presets found. Make sure you're in the correct directory."
        exit 1
    fi

    echo "Found presets:"
    echo "$presets" | while read preset; do
        echo "  - $preset"
    done
    echo ""

    print_header "Running Tests"
    echo ""

    # Test each preset (|| true prevents set -e from exiting on failure)
    while IFS= read -r preset; do
        test_preset "$preset" "build" || true
        echo ""
    done <<< "$presets"

    # Summary
    print_header "Test Summary"
    echo ""
    echo    "Total tests:   $TOTAL_TESTS"
    echo -e "Passed:        ${COLOR_GREEN}${PASSED_TESTS}${COLOR_RESET}"
    echo -e "Failed:        ${COLOR_RED}${FAILED_TESTS}${COLOR_RESET}"
    echo -e "Skipped:       ${COLOR_YELLOW}${SKIPPED_TESTS}${COLOR_RESET}"
    echo ""

    print_header "Detailed Results"
    echo ""
    for i in "${!TEST_RESULTS[@]}"; do
        local result="${TEST_RESULTS[$i]}"
        local detail="${TEST_DETAILS[$i]}"
        if [[ $result == PASS:* ]]; then
            echo -e "${COLOR_GREEN}$result${COLOR_RESET}"
        elif [[ $result == FAIL:* ]]; then
            echo -e "${COLOR_RED}$result${COLOR_RESET}"
        elif [[ $result == SKIP:* ]]; then
            echo -e "${COLOR_YELLOW}$result${COLOR_RESET}"
        fi
        if [ -n "$detail" ]; then
            echo -e "  ${COLOR_BLUE}→${COLOR_RESET} $detail"
        fi
    done
    echo ""

    # Save results to file
    local results_file="${ARTIFACTS_DIR}/test-results.txt"
    {
        echo "Cross-Compilation Test Results"
        echo "=============================="
        echo "Date: $(date)"
        echo "System: $(uname -s) $(uname -m)"
        echo ""
        echo "Total tests: $TOTAL_TESTS"
        echo "Passed: $PASSED_TESTS"
        echo "Failed: $FAILED_TESTS"
        echo "Skipped: $SKIPPED_TESTS"
        echo ""
        echo "Detailed Results:"
        echo "----------------"
        for result in "${TEST_RESULTS[@]}"; do
            echo "$result"
        done
    } > "$results_file"

    print_info "Results saved to: $results_file"
    echo ""

    # Exit with appropriate code
    if [ $FAILED_TESTS -gt 0 ]; then
        print_error "Some tests failed!"
        exit 1
    elif [ $PASSED_TESTS -eq 0 ]; then
        print_warning "No tests passed (all skipped)"
        exit 2
    else
        print_success "All tests passed!"
        exit 0
    fi
}

# Run main function
main "$@"
