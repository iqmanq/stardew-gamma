#!/usr/bin/env bash
# Runs Stardew Valley + SMAPI headlessly (no window, no audio) so the mod's built-in
# UI test harness can drive the chat/settings screens, take screenshots, send one
# real chat request, and quit the game by itself.
#
# Usage:  ./run-headless-test.sh
# Optional: GAMMA_AUTO_TEST_MODEL=<model-id> to override the model for this run.
#
# Screenshots land in ~/.local/share/StardewValley/Screenshots/gamma-test-*.png
set -e
GAME_DIR="${GAME_DIR:-$HOME/.steam/steam/steamapps/common/Stardew Valley}"
OUT=$(mktemp -d)

# Mute everything: SDL gets the dummy driver, OpenAL Soft (the game's audio lib) is
# pointed at its null backend via ALSOFT_CONF.
cat > "$OUT/alsoft.ini" <<'EOF'
[General]
drivers = null
[null]
EOF

export GAMMA_AUTO_TEST="${GAMMA_AUTO_TEST:-1}"
export GAMMA_AUTO_TEST_MODEL="${GAMMA_AUTO_TEST_MODEL:-}"
export LIBGL_ALWAYS_SOFTWARE=1          # software GL: no GPU/window needed
export SDL_VIDEODRIVER=x11              # render into the virtual display
export SDL_AUDIODRIVER=dummy            # SDL: no audio device
export ALSOFT_CONF="$OUT/alsoft.ini"    # OpenAL: null backend, fully silent

xvfb-run -a -s "-screen 0 ${GAMMA_TEST_RES:-1920x1080}x24" "$GAME_DIR/StardewModdingAPI" </dev/null 2>&1 | tee "$OUT/smapi.log"

echo
echo "=== headless test finished (log: $OUT/smapi.log) ==="
ls -la "$HOME/.local/share/StardewValley/Screenshots/" 2>/dev/null | grep gamma || echo "(no gamma screenshots found)"
