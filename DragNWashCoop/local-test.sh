#!/bin/sh
# Starts two copies of Drag'n Wash on this PC for a co-op local test.
# The first copy hosts and the second joins it over localhost; both open the co-op menu (F7).
# Steam must be running. Close both games to end the test.
# Logs: BepInEx/LogOutput.local-host.log and BepInEx/LogOutput.local-guest.log
#
# Both copies are windows rendering at WIDTHxHEIGHT (default 1080p): two copies at 4K ran an
# 8 GB graphics card out of memory.
GAME="${DNW_GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/Drag'n Wash}"
W="${WIDTH:-1920}"
H="${HEIGHT:-1080}"
cd "$GAME" || exit 1

# Hyprland tiles windows to any size (a 4K tile renders at 4K), so float both at exactly WxH pixels,
# side by side on the widest monitor: host on the left, guest on the right.
place() { # $1 = pid, $2 = host|guest
    command -v hyprctl > /dev/null 2>&1 || return 0
    tries=0
    until hyprctl clients -j 2> /dev/null | grep -q "\"pid\": $1,"; do
        tries=$((tries + 1)); [ "$tries" -gt 90 ] && return 0; sleep 1
    done
    # logical size and position on the widest monitor (pixels / monitor scale, below its bar)
    spot=$(hyprctl monitors -j | python3 -c "
import json, sys
m = max(json.load(sys.stdin), key=lambda m: (m['width'] / m['scale'], m['focused']))
scale = m['scale']
mw, mh = m['width'] / scale, m['height'] / scale
top = m['reserved'][1]
w, h = round($W / scale), round($H / scale)
x = max(0, mw / 2 - w) if '$2' == 'host' else min(mw - w, mw / 2)
y = top + max(0, (mh - top - h) / 2)
print(w, h, round(m['x'] + x), round(m['y'] + y), m['activeWorkspace']['name'])")
    set -- "$1" $spot
    # onto that monitor first: a window keeps the scale of the monitor it belongs to, and moving it
    # by pixels alone does not change which one that is
    hyprctl dispatch "hl.dsp.window.move({ workspace = \"$6\", follow = false, window = \"pid:$1\" })" > /dev/null
    hyprctl dispatch "hl.dsp.window.float({ action = \"enable\", window = \"pid:$1\" })" > /dev/null
    hyprctl dispatch "hl.dsp.window.resize({ x = $2, y = $3, \"exact\", window = \"pid:$1\" })" > /dev/null
    hyprctl dispatch "hl.dsp.window.move({ x = $4, y = $5, \"exact\", window = \"pid:$1\" })" > /dev/null
}

# The game's output must not go to a terminal: BepInEx's Linux console setup crashes on some
# terminals (e.g. kitty) and the game then starts without any mods.
# Each copy also gets its own Unity log (the second copy would otherwise rotate the first one's away).
./run_bepinex.sh ./DragNWash -screen-fullscreen 0 -screen-width "$W" -screen-height "$H" \
    -logFile "$GAME/BepInEx/Player.local-host.log" -coop-local-host > /dev/null 2>&1 &
place $! host
./run_bepinex.sh ./DragNWash -screen-fullscreen 0 -screen-width "$W" -screen-height "$H" \
    -logFile "$GAME/BepInEx/Player.local-guest.log" -coop-local-join > /dev/null 2>&1 &
place $! guest
echo "Started host and guest. Logs: $GAME/BepInEx/LogOutput.local-host.log and LogOutput.local-guest.log"
wait
