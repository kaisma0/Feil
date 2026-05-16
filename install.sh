#!/usr/bin/env bash
set -euo pipefail

REPO="kaisma0/Feil" 
API_URL="https://api.github.com/repos/$REPO/releases/latest"

INSTALL_DIR="$HOME/.local/bin"
APPLICATIONS_DIR="$HOME/.local/share/applications"
ICONS_DIR="$HOME/.local/share/icons/hicolor/scalable/apps"

if [ -t 1 ] && [ -t 0 ]; then
    GREEN='\033[0;32m'
    RED='\033[0;31m'
    NC='\033[0m'
else
    GREEN=''
    RED=''
    NC=''
fi

log_info()  { echo -e "${GREEN}[INFO]${NC} $*"; }
log_error() { echo -e "${RED}[ERROR]${NC} $*"; }

download_text() {
    if command -v curl >/dev/null 2>&1; then
        curl -L --fail --silent "$1"
    elif command -v wget >/dev/null 2>&1; then
        wget -qO- "$1"
    else
        log_error "Need curl or wget to query GitHub releases"
        return 1
    fi
}

download_file() {
    if command -v curl >/dev/null 2>&1; then
        curl -L --fail --output "$2" "$1"
    elif command -v wget >/dev/null 2>&1; then
        wget -O "$2" "$1"
    else
        log_error "Need curl or wget to download files"
        return 1
    fi
}

get_latest_url() {
    local json
    if ! json="$(download_text "$API_URL" 2>/dev/null)"; then
        return 1
    fi
    # Extract the browser_download_url for the .AppImage asset (excluding Velopack delta packages)
    echo "$json" | grep '"browser_download_url"' | grep -E '\.AppImage"$' | grep -vi 'delta' | head -n1 | cut -d '"' -f 4
}

check_fuse() {
    if ! ldconfig -p 2>/dev/null | grep -q libfuse.so.2 && ! [ -f /usr/lib/libfuse.so.2 ]; then
        echo ""
        log_error "WARNING: libfuse2 (FUSE 2.x) was not found on your system."
        log_info  "Feil is distributed as an AppImage which requires FUSE 2 to run."
        log_info  "On Debian/Ubuntu:  sudo apt install libfuse2"
        log_info  "On Arch/CachyOS:   sudo pacman -S fuse2"
        log_info  "On Fedora:         sudo dnf install fuse"
        echo ""
    fi
}

install_appimage() {
    local appimage_path="$1"
    local target_appimage="$INSTALL_DIR/Feil.AppImage"

    log_info "Installing Feil AppImage..."
    mkdir -p "$INSTALL_DIR" "$APPLICATIONS_DIR" "$ICONS_DIR"

    # Try to stop running instance safely
    if pgrep -f "Feil.AppImage" >/dev/null; then
        log_info "Stopping running Feil instance..."
        pkill -f "Feil.AppImage" || true
        sleep 1
    fi

    # Install AppImage
    log_info "Copying AppImage to $target_appimage..."
    cp -f "$appimage_path" "$target_appimage"
    chmod +x "$target_appimage"

    # Extract icon from AppImage
    log_info "Extracting icon from AppImage..."
    local temp_extract_dir
    temp_extract_dir="$(mktemp -d)"
    pushd "$temp_extract_dir" > /dev/null

    "$target_appimage" --appimage-extract > /dev/null 2>&1
    
    # Locate the first .svg file within the extracted directory
    FOUND_SVG=$(find squashfs-root -name "*.svg" -print -quit)

    if [ -n "$FOUND_SVG" ] && [ -f "$FOUND_SVG" ]; then
        cp "$FOUND_SVG" "$ICONS_DIR/feil.svg"
        log_info "Icon installed successfully from $FOUND_SVG."
        
        # Rebuild icon/service cache — kbuildsycoca on KDE, gtk cache on GTK desktops
        if command -v kbuildsycoca6 >/dev/null 2>&1; then
            kbuildsycoca6 --noincremental 2>/dev/null || true
        elif command -v gtk-update-icon-cache >/dev/null 2>&1; then
            gtk-update-icon-cache -f -t "$HOME/.local/share/icons/hicolor" 2>/dev/null || true
        fi
    else
        log_error "Warning: Could not find any .svg icon in the AppImage."
    fi
    popd > /dev/null
    rm -rf "$temp_extract_dir"

    # Create desktop entry
    log_info "Creating desktop entry..."
    cat << EOF > "$APPLICATIONS_DIR/feil.desktop"
[Desktop Entry]
Name=Feil
Comment=A Steam application downloader
Exec=$target_appimage
Icon=feil
Terminal=false
Type=Application
Categories=Utility;Game;
EOF

    chmod +x "$APPLICATIONS_DIR/feil.desktop"

    # Update desktop database
    log_info "Updating desktop database..."
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database "$APPLICATIONS_DIR" 2>/dev/null || true
    fi

    # Warn if ~/.local/bin is not in PATH
    if [[ ":$PATH:" != *":$INSTALL_DIR:"* ]]; then
        echo ""
        log_info "WARNING: $INSTALL_DIR is not in your PATH."
        log_info "You may need to add it to your ~/.bashrc or ~/.zshrc:"
        log_info "export PATH=\"\$HOME/.local/bin:\$PATH\""
        echo ""
    fi

    check_fuse
    log_info "Installation complete! You should now be able to find Feil in your application launcher."
}

main() {
    # If a local file is provided, install from it
    if [ "$#" -ge 1 ]; then
        if [ -f "$1" ]; then
            install_appimage "$(readlink -f "$1")"
            return 0
        else
            log_error "Provided argument is not a valid file: $1"
            echo "Usage: $0 [path_to_Feil.AppImage]"
            exit 1
        fi
    fi

    # Auto-download mode
    log_info "Resolving latest Feil release from GitHub ($REPO)..."
    local url
    url="$(get_latest_url)"

    if [ -z "$url" ]; then
        log_error "Could not find a release asset in the latest GitHub release."
        log_error "Make sure $REPO has a published release with a '.AppImage' asset attached."
        exit 1
    fi

    log_info "Latest release asset found: $url"
    local temp_dl
    temp_dl="$(mktemp)"

    log_info "Downloading AppImage..."
    if download_file "$url" "$temp_dl"; then
        chmod +x "$temp_dl"
        install_appimage "$temp_dl"
        rm -f "$temp_dl"
    else
        log_error "Failed to download AppImage from GitHub."
        rm -f "$temp_dl"
        exit 1
    fi
}

main "$@"
