#!/usr/bin/env bash
#
# Installs the VoiceWin Linux build into the current user's application
# launcher (no root required). Copies the self-contained binary to
# ~/.local/bin, the icon into the hicolor theme, and writes a .desktop entry.
#
# Run `dotnet publish ... -o publish/linux-x64` first (see README), then:
#   ./install-linux.sh
#
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BINARY="$REPO_DIR/publish/linux-x64/voicewin"
ICON_SRC="$REPO_DIR/src/VoiceWin/Assets/vwtrans-crop.png"

BIN_DIR="$HOME/.local/bin"
ICON_DIR="$HOME/.local/share/icons/hicolor/512x512/apps"
APP_DIR="$HOME/.local/share/applications"
DESKTOP_FILE="$APP_DIR/voicewin.desktop"

if [[ ! -x "$BINARY" ]]; then
  echo "error: $BINARY not found. Publish first:" >&2
  echo "  dotnet publish src/VoiceWin.Linux -c Release -r linux-x64 --self-contained \\" >&2
  echo "    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \\" >&2
  echo "    -p:IncludeNativeLibrariesForSelfExtract=true -o publish/linux-x64" >&2
  exit 1
fi

mkdir -p "$BIN_DIR" "$ICON_DIR" "$APP_DIR"

install -m 755 "$BINARY" "$BIN_DIR/voicewin"
install -m 644 "$ICON_SRC" "$ICON_DIR/voicewin.png"

cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=VoiceWin
GenericName=Speech to Text
Comment=Speech-to-text transcription with auto-paste
Exec=$BIN_DIR/voicewin
Icon=voicewin
Terminal=false
Categories=Utility;
Keywords=voice;speech;transcription;dictation;stt;
StartupNotify=false
StartupWMClass=voicewin
EOF
chmod 644 "$DESKTOP_FILE"

# Refresh the menu caches so the entry shows up immediately.
update-desktop-database "$APP_DIR" >/dev/null 2>&1 || true
command -v kbuildsycoca6 >/dev/null 2>&1 && kbuildsycoca6 >/dev/null 2>&1 || \
command -v kbuildsycoca5 >/dev/null 2>&1 && kbuildsycoca5 >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && \
  gtk-update-icon-cache "$HOME/.local/share/icons/hicolor" >/dev/null 2>&1 || true

echo "Installed:"
echo "  binary   -> $BIN_DIR/voicewin"
echo "  icon     -> $ICON_DIR/voicewin.png"
echo "  launcher -> $DESKTOP_FILE"
echo "VoiceWin should now appear in your application launcher."
