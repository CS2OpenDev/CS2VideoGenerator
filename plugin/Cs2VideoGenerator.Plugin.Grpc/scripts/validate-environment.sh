#!/bin/bash
# Cross-Compilation Environment Validator
# Checks if all necessary tools and dependencies are available

set -e

# Always run from the project root (one level up from scripts/)
cd "$(dirname "${BASH_SOURCE[0]}")/.."

COLOR_GREEN='\033[0;32m'
COLOR_YELLOW='\033[1;33m'
COLOR_RED='\033[0;31m'
COLOR_BLUE='\033[0;34m'
COLOR_RESET='\033[0m'

print_header() {
    echo -e "${COLOR_BLUE}=====================================${COLOR_RESET}"
    echo -e "${COLOR_BLUE}$1${COLOR_RESET}"
    echo -e "${COLOR_BLUE}=====================================${COLOR_RESET}"
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

check_command() {
    local cmd=$1
    local name=$2
    local required=$3
    
    if command -v "$cmd" &> /dev/null; then
        local version=$("$cmd" --version 2>&1 | head -n 1 || echo "unknown")
        print_success "$name found: $version"
        return 0
    else
        if [ "$required" == "true" ]; then
            print_error "$name not found (REQUIRED)"
            return 1
        else
            print_warning "$name not found (optional for some targets)"
            return 0
        fi
    fi
}

# Like check_command but also checks Homebrew LLVM path on macOS
check_llvm_command() {
    local cmd=$1
    local name=$2
    local required=$3

    if command -v "$cmd" &> /dev/null; then
        local version=$("$cmd" --version 2>&1 | head -n 1 || echo "unknown")
        print_success "$name found: $version"
        return 0
    fi

    # On macOS, check Homebrew LLVM (keg-only, not in PATH by default)
    if [ "$(uname -s)" = "Darwin" ]; then
        local llvm_bin=""
        if [ -d "/opt/homebrew/opt/llvm/bin" ]; then
            llvm_bin="/opt/homebrew/opt/llvm/bin"
        elif [ -d "/usr/local/opt/llvm/bin" ]; then
            llvm_bin="/usr/local/opt/llvm/bin"
        fi
        if [ -n "$llvm_bin" ] && [ -x "$llvm_bin/$cmd" ]; then
            local version=$("$llvm_bin/$cmd" --version 2>&1 | head -n 1 || echo "unknown")
            print_success "$name found (Homebrew LLVM): $version"
            return 0
        fi
    fi

    if [ "$required" == "true" ]; then
        print_error "$name not found (REQUIRED)"
        return 1
    else
        print_warning "$name not found (optional for some targets)"
        return 0
    fi
}

check_env_var() {
    local var=$1
    local name=$2
    local required=$3
    
    if [ -n "${!var}" ]; then
        if [ -d "${!var}" ]; then
            print_success "$name: ${!var}"
            return 0
        else
            print_warning "$name set but directory doesn't exist: ${!var}"
            return 1
        fi
    else
        if [ "$required" == "true" ]; then
            print_error "$name not set (REQUIRED)"
            return 1
        else
            print_warning "$name not set (needed for specific targets)"
            return 0
        fi
    fi
}

# Main validation
print_header "CS2 Video Generator - Cross-Compilation Validator"
echo ""

# Check basic tools
print_header "Basic Build Tools"
check_command cmake "CMake" true
check_command ninja "Ninja" false
check_command git "Git" true
echo ""

# Check compilers
print_header "Compilers"
check_command gcc "GCC" false
check_command g++ "G++" false
check_command clang "Clang" false
check_command clang++ "Clang++" false
echo ""

# Check LLVM tools (for cross-compilation)
print_header "LLVM Tools (for Windows cross-compilation)"
check_llvm_command lld "LLD Linker" false
check_llvm_command lld-link "LLD Link (MSVC-style)" false
check_llvm_command llvm-ar "LLVM ar" false
check_llvm_command llvm-lib "LLVM lib (Windows archives)" false
check_llvm_command llvm-ranlib "LLVM ranlib" false
check_llvm_command llvm-rc "LLVM RC" false
echo ""

# Check cross-compilation toolchains
print_header "Cross-Compilation Toolchains"
if [ "$(uname -s)" = "Darwin" ]; then
    # macOS uses messense/homebrew-macos-cross-toolchains
    check_command x86_64-unknown-linux-gnu-gcc "Linux x64 Cross-Compiler (brew: x86_64-unknown-linux-gnu)" false
    check_command aarch64-unknown-linux-gnu-gcc "Linux ARM64 Cross-Compiler (brew: aarch64-unknown-linux-gnu)" false
else
    check_command x86_64-linux-gnu-gcc "x64 Linux Cross-Compiler" false
    check_command aarch64-linux-gnu-gcc "ARM64 Linux Cross-Compiler" false
fi
echo ""

# Check vcpkg submodule
print_header "vcpkg"
if [ -x "external/vcpkg/vcpkg" ] || [ -x "external/vcpkg/vcpkg.exe" ]; then
    _vcpkg_ver=$(external/vcpkg/vcpkg version 2>&1 | head -2 | tail -1 || echo "unknown")
    print_success "vcpkg submodule bootstrapped: $_vcpkg_ver"
elif [ -f "external/vcpkg/bootstrap-vcpkg.sh" ]; then
    print_warning "vcpkg submodule present but not bootstrapped"
    print_info "Run: ./external/vcpkg/bootstrap-vcpkg.sh"
else
    print_error "vcpkg submodule not found — run: git submodule update --init"
fi
echo ""

# Check environment variables
print_header "Environment Variables"
check_env_var "XWIN_DIR" "XWIN_DIR (Windows SDK)" false
# OSXCROSS is only needed when cross-compiling TO macOS FROM Linux
if [ "$(uname -s)" != "Darwin" ]; then
    check_env_var "OSXCROSS_ROOT" "OSXCROSS_ROOT (macOS SDK)" false
fi
echo ""

# Check for xwin
print_header "Windows Cross-Compilation (xwin)"
if check_command xwin "xwin" false; then
    if [ -n "$XWIN_DIR" ] && [ -d "$XWIN_DIR" ]; then
        if [ -d "$XWIN_DIR/sdk/include" ]; then
            print_success "Windows SDK found at $XWIN_DIR"
        else
            print_warning "XWIN_DIR set but SDK not properly extracted"
            print_info "Run: xwin --accept-license splat --output $XWIN_DIR"
        fi
    else
        print_warning "xwin installed but XWIN_DIR not set or invalid"
        print_info "Run: xwin --accept-license splat --output ~/.xwin"
        print_info "Then: export XWIN_DIR=~/.xwin"
    fi
fi
echo ""

# Check CMake presets
print_header "CMake Presets"
if [ -f "CMakePresets.json" ]; then
    print_success "CMakePresets.json found"
    print_info "Available presets:"
    if cmake --list-presets 2>/dev/null | grep -q "configure presets"; then
        cmake --list-presets 2>/dev/null | grep "^  \"" | head -10
    else
        print_warning "Could not list presets (bootstrap vcpkg first)"
    fi
else
    print_error "CMakePresets.json not found - run from plugin directory"
fi
echo ""

# Summary
print_header "Summary"
echo ""
print_info "Your system can build for the following targets:"
echo ""

# Detect host OS
HOST_OS=$(uname -s)
HOST_ARCH=$(uname -m)

# Native build
if command -v gcc &> /dev/null || command -v clang &> /dev/null; then
    print_success "Native $HOST_OS $HOST_ARCH ← Native compilation"
else
    print_error "No native compiler available"
fi

# macOS-specific targets
if [ "$HOST_OS" = "Darwin" ]; then
    # Linux targets from macOS - uses GCC cross-toolchains from Homebrew
    if command -v x86_64-unknown-linux-gnu-gcc &> /dev/null; then
        print_success "Linux x64 ← Cross-compilation available"
    else
        print_warning "Linux x64 ← Install: brew tap messense/macos-cross-toolchains && brew install x86_64-unknown-linux-gnu"
    fi
    if command -v aarch64-unknown-linux-gnu-gcc &> /dev/null; then
        print_success "Linux ARM64 ← Cross-compilation available"
    else
        print_warning "Linux ARM64 ← Install: brew tap messense/macos-cross-toolchains && brew install aarch64-unknown-linux-gnu"
    fi

    # Windows from macOS - requires clang-cl + xwin
    # clang-cl may be in Homebrew LLVM path rather than default PATH
    _has_clang_cl=false
    if command -v clang-cl &> /dev/null; then
        _has_clang_cl=true
    elif [ -x "/opt/homebrew/opt/llvm/bin/clang-cl" ] || [ -x "/usr/local/opt/llvm/bin/clang-cl" ]; then
        _has_clang_cl=true
    fi

    # Determine script directory for locating overlay ports
    _project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

    if [ "$_has_clang_cl" = true ] && [ -d "${XWIN_DIR:-/nonexistent}/sdk/include" ]; then
        if [ -d "$_project_dir/cmake/ports/openssl" ]; then
            print_success "Windows x64 ← Cross-compilation available (OpenSSL via overlay port)"
        else
            print_warning "Windows x64 ← Toolchain works but OpenSSL requires overlay port or native Windows tools"
        fi
    elif [ "$_has_clang_cl" = true ]; then
        print_warning "Windows x64 ← clang-cl found, but need xwin SDK"
        print_info "  Setup: cargo install xwin"
        print_info "  Then: xwin --accept-license splat --output ~/.xwin"
    else
        print_warning "Windows x64 ← Need: brew install llvm + xwin SDK"
    fi
fi

# Linux-specific targets
if [ "$HOST_OS" = "Linux" ]; then
    # Linux ARM64
    if command -v aarch64-linux-gnu-gcc &> /dev/null; then
        print_success "Linux ARM64 ← Cross-compilation available"
    else
        print_warning "Linux ARM64 ← Install: sudo apt install gcc-aarch64-linux-gnu g++-aarch64-linux-gnu"
    fi

    # Windows x64
    if command -v clang-cl &> /dev/null && [ -d "${XWIN_DIR:-/nonexistent}/sdk/include" ]; then
        print_success "Windows x64 ← Cross-compilation available"
    elif command -v clang &> /dev/null; then
        print_warning "Windows x64 ← Need: clang-cl + xwin SDK"
    else
        print_warning "Windows x64 ← Need: clang + xwin SDK"
    fi

    # macOS
    if [ -d "${OSXCROSS_ROOT:-/nonexistent}/bin" ]; then
        print_success "macOS (x64 & ARM64) ← Cross-compilation available"
    else
        print_warning "macOS ← Need: osxcross toolchain"
    fi
fi

echo ""
print_info "See BUILD.md for detailed setup instructions for each target."
echo ""
