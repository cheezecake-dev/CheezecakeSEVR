#!/usr/bin/env bash
# setup.sh - get Space Engineers VR running on native Linux.
#
# Takes a machine that has Pulsar for Linux (with CometWorks' linux-compat) and
# Space Engineers installed, and does what is left:
#
#   1. checks the OpenXR loader and OpenAL and, on request, installs them (--install-deps);
#   2. gives linux-compat a DXVK Native that can host an OpenXR session. Stock
#      DXVK never enables the Vulkan extensions WiVRn and Monado need, so the
#      headset session crashes. Until CometWorks ships the fix this script builds
#      DXVK with it (tools/linux/dxvk-native-xr-external-fd.patch) and installs
#      the two .so files over linux-compat's, keeping the originals;
#   3. registers this repository in Pulsar as a development folder, so the
#      plugin shows up in Pulsar's plugin list;
#   4. checks WiVRn and adb, and prints the headset settings that work;
#   5. optionally installs a desktop launcher (--launcher).
#
# Safe to run again. Each step looks first and only changes what is missing.
# Run it again after linux-compat updates: an update replaces the DXVK files.
#
# Usage: tools/linux/setup.sh [options]
#   --pulsar DIR      Pulsar's install folder (the one holding Interim.bin).
#                     Default: found in the usual places.
#   --se-bin64 DIR    Space Engineers' Bin64 folder. Default: read from Steam.
#   --install-deps    Install missing packages with apt, dnf or pacman (sudo).
#   --no-dxvk         Skip the DXVK step (once CometWorks ships the fix).
#   --launcher        Install a desktop launcher that starts the WiVRn server,
#                     sets up adb and starts Pulsar.
#   --check           Only report what is installed. Changes nothing.
#   -h, --help        Show this text.

set -euo pipefail

STEAM_APP_ID=244850
PLUGIN_ID=SpaceEngineersVR
PLUGIN_DATA_FILE=SpaceEngineersVR.xml
WIVRN_FLATPAK=io.github.wivrn.wivrn

COMETWORKS_DEPS_REPO=https://github.com/CometWorks/linux-dependencies.git
DXVK_REPO=https://github.com/doitsujin/dxvk.git
OUR_PATCH_NAME=dxvk-native-xr-external-fd.patch
DXVK_LIBS=(libdxvk_d3d11.so libdxvk_dxgi.so)

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
CACHE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/cheezecake-sevr"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}"

PULSAR_DIR=""
SE_BIN64=""
INSTALL_DEPS=0
SKIP_DXVK=0
WANT_LAUNCHER=0
CHECK_ONLY=0

# ---- output -----------------------------------------------------------------

if [ -t 1 ]; then
    C_OK=$'\e[32m'; C_WARN=$'\e[33m'; C_BAD=$'\e[31m'; C_DIM=$'\e[2m'; C_OFF=$'\e[0m'
else
    C_OK=""; C_WARN=""; C_BAD=""; C_DIM=""; C_OFF=""
fi

# Everything the user still has to do, printed together at the end.
TODO=()
# Parts still missing. missing() adds one, resolved() takes one off when a step
# fixes it; --check exits non-zero while any remain.
PENDING=0

heading() { printf '\n==> %s\n' "$*"; }
ok()      { printf '  %s[ok]%s   %s\n' "$C_OK" "$C_OFF" "$*"; }
warn()    { printf '  %s[warn]%s %s\n' "$C_WARN" "$C_OFF" "$*"; }
missing() { printf '  %s[miss]%s %s\n' "$C_BAD" "$C_OFF" "$*"; PENDING=$((PENDING + 1)); }
resolved() { PENDING=$((PENDING - 1)); }
note()    { printf '         %s%s%s\n' "$C_DIM" "$*" "$C_OFF"; }
die()     { printf '%sERROR:%s %s\n' "$C_BAD" "$C_OFF" "$*" >&2; exit 1; }

usage() { sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//'; }

# ---- arguments --------------------------------------------------------------

while [ $# -gt 0 ]; do
    case "$1" in
        --pulsar)       [ $# -ge 2 ] || die "--pulsar needs a folder"; PULSAR_DIR="$2"; shift ;;
        --se-bin64)     [ $# -ge 2 ] || die "--se-bin64 needs a folder"; SE_BIN64="$2"; shift ;;
        --install-deps) INSTALL_DEPS=1 ;;
        --no-dxvk)      SKIP_DXVK=1 ;;
        --launcher)     WANT_LAUNCHER=1 ;;
        --check)        CHECK_ONLY=1 ;;
        -h|--help)      usage; exit 0 ;;
        *)              die "unknown option: $1 (try --help)" ;;
    esac
    shift
done

# ---- small helpers ----------------------------------------------------------

# ldconfig lives in /sbin, which an ordinary user's PATH often lacks.
PATH="$PATH:/sbin:/usr/sbin"

have() { command -v "$1" >/dev/null 2>&1; }

# Run a command as root, through sudo unless we already are root.
as_root() {
    if [ "$(id -u)" -eq 0 ]; then "$@"; else sudo "$@"; fi
}

# Which package manager this distribution uses: apt, dnf, pacman, or empty.
package_manager() {
    local ids
    ids="$(. /etc/os-release 2>/dev/null && echo "${ID:-} ${ID_LIKE:-}")"
    case " $ids " in
        *" debian "*|*" ubuntu "*) have apt-get && echo apt ;;
        *" fedora "*|*" rhel "*)   have dnf && echo dnf ;;
        *" arch "*)                have pacman && echo pacman ;;
    esac
}

# Package names per package manager. The OpenXR loader is what the plugin loads
# at run time; the rest is only needed to build DXVK.
loader_packages() {
    case "$1" in
        apt)    echo libopenxr-loader1 ;;
        dnf)    echo openxr-libs ;;
        pacman) echo openxr ;;
    esac
}

# The game's sound engine loads OpenAL through dlopen("libopenal.so.1") and does
# not look beside linux-compat's own copy, so without the distribution's the
# game starts and runs without sound.
openal_packages() {
    case "$1" in
        apt)    echo libopenal1 ;;
        dnf)    echo openal-soft ;;
        pacman) echo openal ;;
    esac
}
build_packages() {
    case "$1" in
        apt)    echo build-essential git meson ninja-build glslang-tools libsdl3-dev pkg-config patchelf ;;
        dnf)    echo gcc-c++ git meson ninja-build glslang SDL3-devel pkgconf-pkg-config patchelf ;;
        pacman) echo base-devel git meson ninja glslang sdl3 pkgconf patchelf ;;
    esac
}
install_command() {
    case "$1" in
        apt)    echo "sudo apt-get install -y ${*:2}" ;;
        dnf)    echo "sudo dnf install -y ${*:2}" ;;
        pacman) echo "sudo pacman -S --needed ${*:2}" ;;
    esac
}

# Install packages now (--install-deps) or print the command that would.
# Returns 1 when the user has to do it themselves.
install_packages() {
    local pm="$1"; shift
    if [ -z "$pm" ]; then
        note "Unknown distribution: install these with your package manager: $*"
        return 1
    fi
    if [ "$INSTALL_DEPS" -eq 1 ]; then
        note "$(install_command "$pm" "$@")"
        case "$pm" in
            apt)    as_root apt-get install -y "$@" ;;
            dnf)    as_root dnf install -y "$@" ;;
            pacman) as_root pacman -S --needed --noconfirm "$@" ;;
        esac
    else
        note "Install with: $(install_command "$pm" "$@")"
        note "or re-run with --install-deps"
        return 1
    fi
}

# ---- locating things --------------------------------------------------------

find_pulsar() {
    local d
    if [ -n "$PULSAR_DIR" ]; then
        [ -f "$PULSAR_DIR/Interim.bin" ] || die "no Interim.bin in --pulsar $PULSAR_DIR"
        return
    fi
    for d in "${PULSAR_HOME:-}" "$HOME/Pulsar" "$DATA_DIR/Pulsar" "$HOME/Games/Pulsar" \
             "$HOME/.local/opt/Pulsar" "$HOME/Downloads/Pulsar" "$HOME/Applications/Pulsar"; do
        if [ -n "$d" ] && [ -f "$d/Interim.bin" ]; then PULSAR_DIR="$d"; return; fi
    done
}

# Steam lists every library folder in libraryfolders.vdf, with the app ids
# installed in each. The folder holding STEAM_APP_ID is the one we want.
find_se_bin64() {
    local vdf lib
    if [ -n "$SE_BIN64" ]; then return; fi
    for vdf in "$HOME/.steam/steam/steamapps/libraryfolders.vdf" \
               "$DATA_DIR/Steam/steamapps/libraryfolders.vdf" \
               "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/libraryfolders.vdf"; do
        [ -f "$vdf" ] || continue
        while IFS= read -r lib; do
            if [ -f "$lib/steamapps/common/SpaceEngineers/Bin64/SpaceEngineers.exe" ]; then
                SE_BIN64="$lib/steamapps/common/SpaceEngineers/Bin64"
                return
            fi
        done < <(awk -v id="\"$STEAM_APP_ID\"" '
            /"path"/ { gsub(/^[^"]*"path"[ \t]*"|"[ \t]*$/, ""); path = $0 }
            $1 == id { print path }' "$vdf")
    done
}

# linux-compat is unpacked into one folder per version under Interim/GitHub.
# Pulsar keeps old ones, so install into every folder that has the files.
compat_bins() {
    local bin
    for bin in "$PULSAR_DIR"/Interim/GitHub/LinuxCompat-*/Bin; do
        [ -f "$bin/${DXVK_LIBS[0]}" ] && printf '%s\n' "$bin"
    done
}

# True when a Pulsar process started from this install folder is running.
pulsar_running() {
    local pid
    for pid in $(pgrep -x Interim.bin 2>/dev/null || true); do
        [ "$(readlink -f "/proc/$pid/exe" 2>/dev/null)" = "$(readlink -f "$PULSAR_DIR/Interim.bin")" ] && return 0
    done
    return 1
}

# ---- the DXVK fix -----------------------------------------------------------

# Does this libdxvk_d3d11.so already contain the fix? The patch makes DXVK
# enable VK_KHR_external_{memory,semaphore,fence}_fd, and DXVK holds extension
# names as string literals. Stock DXVK Native 3.0.2 contains none of the three
# (checked against a stock build), so finding all three means the build came
# from a source with the fix: ours, or CometWorks' once they take it. This looks
# at the library's content, so it does not matter who built it or when.
dxvk_has_fix() {
    local lib="$1" ext
    for ext in VK_KHR_external_memory_fd VK_KHR_external_semaphore_fd VK_KHR_external_fence_fd; do
        grep -aqF "$ext" "$lib" || return 1
    done
}

# ---- plugin registration ----------------------------------------------------

# Pulsar keeps its settings in two XML files, which this edits as text so that
# nothing else in them is touched (the .NET serializer is picky about the
# header and the namespaces, and a parse-and-rewrite would drop them). Python
# only parses the file to look for the entry and to check the result.
#   sources.xml  <LocalPluginSources> lists the development folder.
#   Current.xml  <DevFolder> lists which of those folders are enabled.
# Files are never created: Pulsar writes them on its first start, and a
# sources.xml made by hand lacks the plugin hub, so the core plugins do not load.
# Usage: pulsar_xml check|apply sources|profile FILE
pulsar_xml() {
    python3 -I - "$@" "$REPO_DIR" "$PLUGIN_ID" "$PLUGIN_DATA_FILE" <<'PY'
import os
import re
import sys
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape

mode, kind, path, repo, plugin_id, data_file = sys.argv[1:7]
repo = os.path.normpath(repo)


def same_folder(text):
    return os.path.normpath((text or "").strip()) == repo


def insert_child(text, parent, child):
    """Add child (a list of lines, indented relative to the parent) inside <parent>."""
    empty = re.search(r"^([ \t]*)<%s\s*/>" % parent, text, re.M)
    if empty:
        base = empty.group(1)
        body = "\n".join(base + "  " + line for line in child)
        new = "%s<%s>\n%s\n%s</%s>" % (base, parent, body, base, parent)
        return text[:empty.start()] + new + text[empty.end():]
    close = re.search(r"^([ \t]*)</%s>" % parent, text, re.M)
    if not close:
        sys.exit("%s has no <%s> element; start Pulsar once so it writes its own, then run this again." % (path, parent))
    base = close.group(1)
    body = "\n".join(base + "  " + line for line in child)
    return text[:close.start()] + body + "\n" + text[close.start():]


root = ET.parse(path).getroot()
if kind == "sources":
    candidates = root.findall("./LocalPluginSources/LocalPlugin")
    matches = [c for c in candidates if c.findtext("File") == data_file]
else:
    candidates = root.findall("./DevFolder/LocalFolderConfig")
    matches = [c for c in candidates if c.findtext("Id") == plugin_id]
entry = matches[0] if matches else None
if kind == "sources":
    present = entry is not None and same_folder(entry.findtext("Folder"))
else:
    present = entry is not None

if mode == "check":
    sys.exit(0 if present else 1)

if present:
    sys.exit(0)

raw = open(path, "rb").read()
bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")

if kind == "sources":
    folder = escape(repo)
    if entry is not None:
        # Same data file, other folder: the repository was moved. Repoint it.
        blocks = list(re.finditer(r"<LocalPlugin>.*?</LocalPlugin>", text, re.S))
        index = candidates.index(entry)
        block = blocks[index]
        fixed = re.sub(r"<Folder>.*?</Folder>", lambda _: "<Folder>%s</Folder>" % folder, block.group(0), flags=re.S)
        text = text[:block.start()] + fixed + text[block.end():]
    else:
        text = insert_child(text, "LocalPluginSources", [
            "<LocalPlugin>",
            "  <Name>%s</Name>" % escape(plugin_id),
            "  <Folder>%s</Folder>" % folder,
            "  <File>%s</File>" % escape(data_file),
            "  <Enabled>true</Enabled>",
            "</LocalPlugin>",
        ])
else:
    text = insert_child(text, "DevFolder", [
        "<LocalFolderConfig>",
        "  <Id>%s</Id>" % escape(plugin_id),
        "  <DebugBuild>false</DebugBuild>",
        "</LocalFolderConfig>",
    ])

ET.fromstring(text.encode("utf-8"))  # refuse to write anything that is not well-formed
with open(path, "wb") as out:
    out.write((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))
PY
}

# ---- steps ------------------------------------------------------------------

step_locate() {
    heading "Locating Pulsar and Space Engineers"
    find_pulsar
    if [ -n "$PULSAR_DIR" ]; then
        PULSAR_DIR="$(cd "$PULSAR_DIR" && pwd)"
        ok "Pulsar: $PULSAR_DIR"
    else
        missing "Pulsar for Linux not found"
        note "Download the linux-x64 build from https://github.com/SpaceGT/Pulsar/releases,"
        note "unpack it, then run: $SCRIPT_DIR/setup.sh --pulsar <folder with Interim.bin>"
        TODO+=("Install Pulsar for Linux (https://github.com/SpaceGT/Pulsar), start it once, and run this script again.")
    fi
    find_se_bin64
    if [ -n "$SE_BIN64" ] && [ -f "$SE_BIN64/SpaceEngineers.exe" ]; then
        ok "Space Engineers: $SE_BIN64"
    else
        missing "Space Engineers (Steam app $STEAM_APP_ID) not found"
        note "Install it from Steam, or pass its Bin64 folder with --se-bin64."
        TODO+=("Install Space Engineers from Steam, or pass --se-bin64.")
    fi
}

step_openal() {
    heading "OpenAL (game sound)"
    if have ldconfig && ldconfig -p 2>/dev/null | grep -q 'libopenal\.so\.1'; then
        ok "libopenal.so.1 is installed"
        return
    fi
    missing "libopenal.so.1 not installed; the game would run without sound"
    [ "$CHECK_ONLY" -eq 1 ] && return
    local pm; pm="$(package_manager)"
    # shellcheck disable=SC2046  # the package list is meant to split
    if install_packages "$pm" $(openal_packages "$pm"); then
        resolved
        ok "OpenAL installed"
    else
        TODO+=("Install OpenAL (libopenal1 / openal-soft / openal) and run this script again.")
    fi
}

step_loader() {
    heading "OpenXR loader"
    if have ldconfig && ldconfig -p 2>/dev/null | grep -q 'libopenxr_loader\.so\.1'; then
        ok "libopenxr_loader.so.1 is installed"
        return
    fi
    missing "libopenxr_loader.so.1 not installed"
    note "The plugin uses the distribution's OpenXR loader."
    [ "$CHECK_ONLY" -eq 1 ] && return
    local pm; pm="$(package_manager)"
    # shellcheck disable=SC2046  # the package list is meant to split
    if install_packages "$pm" $(loader_packages "$pm"); then
        resolved
        ok "OpenXR loader installed"
    else
        TODO+=("Install the OpenXR loader (libopenxr-loader1 / openxr-libs / openxr) and run this script again.")
    fi
}

# Tools the DXVK build needs, as commands. Returns the missing ones on stdout.
missing_build_tools() {
    local tool
    for tool in git meson ninja glslangValidator pkg-config gcc g++ patchelf; do
        have "$tool" || echo "$tool"
    done
    pkg-config --exists sdl3 2>/dev/null || echo "sdl3 (development files)"
}

# Fetch CometWorks' patch series and read from it which DXVK they build and how
# they key their build cache. Sets DXVK_VERSION, COMET_DIR and COMET_STAMP.
fetch_cometworks() {
    COMET_DIR="$CACHE_DIR/linux-dependencies"
    if [ -d "$COMET_DIR/.git" ]; then
        git -C "$COMET_DIR" fetch --quiet --depth 1 origin main
        git -C "$COMET_DIR" reset --quiet --hard FETCH_HEAD
    else
        mkdir -p "$CACHE_DIR"
        git clone --quiet --depth 1 --branch main "$COMETWORKS_DEPS_REPO" "$COMET_DIR"
    fi
    # Their script prints "<version> patches=<hash> staging<rev>" without
    # building or touching the network. Using it keeps the version, and the
    # cache key, in step with whatever CometWorks currently ships.
    COMET_STAMP="$(bash "$COMET_DIR/Scripts/build_dxvk.sh" --print-stamp)"
    DXVK_VERSION="${COMET_STAMP%% *}"
}

# Build DXVK Native the way CometWorks' Scripts/build_dxvk.sh does (same source
# tag, same patches, upstream's package-native.sh with --64-only, same patchelf
# fix-ups), with our patch applied last. The result goes to $CACHE_DIR/dxvk/lib
# and is reused until the inputs change.
build_dxvk() {
    local our_patch="$SCRIPT_DIR/$OUR_PATCH_NAME"
    [ -f "$our_patch" ] || die "missing $our_patch (is this a complete checkout?)"

    local tool; tool="$(missing_build_tools | tr '\n' ' ')"
    if [ -n "${tool// /}" ]; then
        missing "DXVK build needs: $tool"
        local pm; pm="$(package_manager)"
        # shellcheck disable=SC2046
        install_packages "$pm" $(build_packages "$pm") || {
            TODO+=("Install the DXVK build tools listed above and run this script again.")
            return 1
        }
        tool="$(missing_build_tools | tr '\n' ' ')"
        [ -z "${tool// /}" ] || die "still missing after install: $tool"
    fi

    fetch_cometworks
    local patches=()
    while IFS= read -r p; do patches+=("$p"); done \
        < <(find "$COMET_DIR/Patches/dxvk" -maxdepth 1 -name '*.patch' | LC_ALL=C sort)
    patches+=("$our_patch")

    local stamp
    stamp="$COMET_STAMP + ours=$(sha256sum "$our_patch" | cut -c1-16)"
    local work="$CACHE_DIR/dxvk" out="$CACHE_DIR/dxvk/lib"
    if [ -f "$work/stamp" ] && [ "$(cat "$work/stamp")" = "$stamp" ] \
       && [ -f "$out/${DXVK_LIBS[0]}" ] && [ -f "$out/${DXVK_LIBS[1]}" ]; then
        ok "Re-using the DXVK build in $out"
        return
    fi

    echo "  Building DXVK Native $DXVK_VERSION with ${#patches[@]} patches (a few minutes)."
    rm -rf "$work/src" "$work/dest" "$out"
    mkdir -p "$work" "$out"
    git -c advice.detachedHead=false clone --quiet --depth 1 --branch "v$DXVK_VERSION" --recurse-submodules --shallow-submodules \
        "$DXVK_REPO" "$work/src"
    local p
    for p in "${patches[@]}"; do
        note "applying $(basename "$p")"
        git -C "$work/src" apply "$p" || die "$(basename "$p") does not apply to DXVK v$DXVK_VERSION."
    done
    mkdir -p "$work/dest"
    (cd "$work/src" && NINJA_OPTS="-j$(nproc)" ./package-native.sh "$DXVK_VERSION" "$work/dest" --64-only --no-package) \
        > "$work/build.log" 2>&1 \
        || { tail -n 25 "$work/build.log" >&2; die "the DXVK build failed; full log: $work/build.log"; }

    local lib src
    for lib in "${DXVK_LIBS[@]}"; do
        # -L follows libdxvk_x.so -> .so.0 -> .so.0.<version> to the real file.
        src="$(find -L "$work/dest" -type f -name "$lib" -print -quit)"
        [ -n "$src" ] || die "the build produced no $lib"
        install -m 0755 "$src" "$out/$lib"
    done
    # As in CometWorks' script: no .so.0 files are shipped, so d3d11 must name
    # dxgi by its plain file name, and DT_RPATH=$ORIGIN lets it find it there.
    for lib in "${DXVK_LIBS[@]}"; do
        patchelf --replace-needed libdxvk_dxgi.so.0 libdxvk_dxgi.so "$out/$lib"
        # shellcheck disable=SC2016  # $ORIGIN is for the dynamic loader, not the shell
        patchelf --force-rpath --set-rpath '$ORIGIN' "$out/$lib"
    done
    dxvk_has_fix "$out/${DXVK_LIBS[0]}" || die "the built library lacks the fix; the patch did not take effect"
    printf '%s\n' "$stamp" > "$work/stamp"
    rm -rf "$work/src" "$work/dest"
    ok "Built DXVK $DXVK_VERSION into $out"
}

step_dxvk() {
    heading "DXVK Native (OpenXR support)"
    [ -n "$PULSAR_DIR" ] || { warn "skipped: Pulsar not found"; return; }
    local bins=() bin stale=()
    while IFS= read -r bin; do bins+=("$bin"); done < <(compat_bins)
    if [ "${#bins[@]}" -eq 0 ]; then
        missing "linux-compat is not installed in Pulsar (no Interim/GitHub/LinuxCompat-*/Bin)"
        note "Start Pulsar once with Space Engineers; it installs linux-compat on its first run."
        TODO+=("Start Pulsar once so it installs linux-compat, then run this script again.")
        return
    fi
    for bin in "${bins[@]}"; do
        if dxvk_has_fix "$bin/${DXVK_LIBS[0]}"; then
            ok "$(basename "$(dirname "$bin")"): DXVK already has the fix"
        else
            stale+=("$bin")
            missing "$(basename "$(dirname "$bin")"): stock DXVK, it crashes when the headset session starts"
        fi
    done
    [ "${#stale[@]}" -gt 0 ] || return 0
    [ "$CHECK_ONLY" -eq 0 ] || return 0
    if [ "$SKIP_DXVK" -eq 1 ]; then
        warn "--no-dxvk given: leaving DXVK alone"
        TODO+=("DXVK is unpatched: the game will crash when the headset session starts. Run without --no-dxvk.")
        return
    fi
    if pulsar_running; then
        die "Pulsar is running from $PULSAR_DIR. Close it first; it has the DXVK files loaded."
    fi

    build_dxvk || return 0
    local out="$CACHE_DIR/dxvk/lib" stamp lib
    stamp="$(date +%Y%m%d-%H%M%S)"
    for bin in "${stale[@]}"; do
        local backup; backup="$(dirname "$bin")/dxvk-original-$stamp"
        mkdir -p "$backup"
        for lib in "${DXVK_LIBS[@]}"; do
            cp -p "$bin/$lib" "$backup/$lib"
            install -m 0755 "$out/$lib" "$bin/$lib"
        done
        resolved
        ok "Installed into $bin"
        note "originals saved in $backup"
    done
    TODO+=("After a linux-compat update, run this script again: the update replaces the DXVK files (until CometWorks ships the fix).")
}

step_plugin() {
    heading "Plugin registration in Pulsar"
    [ -n "$PULSAR_DIR" ] || { warn "skipped: Pulsar not found"; return; }
    [ -f "$REPO_DIR/$PLUGIN_DATA_FILE" ] || die "$REPO_DIR/$PLUGIN_DATA_FILE not found (run this from a complete checkout)"
    local sources="$PULSAR_DIR/Interim/Sources/sources.xml"
    local profile="$PULSAR_DIR/Interim/Profiles/Current.xml"
    local f
    for f in "$sources" "$profile"; do
        if [ ! -f "$f" ]; then
            missing "$f does not exist yet"
            note "Pulsar writes it on its first start. Start Pulsar once, close it, and run this script again."
            TODO+=("Start Pulsar once, close it, and run this script again to register the plugin.")
            return
        fi
    done
    if pulsar_xml check sources "$sources" && pulsar_xml check profile "$profile"; then
        ok "Development folder registered: $REPO_DIR"
        return
    fi
    missing "Development folder not registered"
    [ "$CHECK_ONLY" -eq 0 ] || return 0
    if pulsar_running; then
        die "Pulsar is running from $PULSAR_DIR; it rewrites these files on exit. Close it first."
    fi
    for f in "$sources" "$profile"; do
        cp -p "$f" "$f.bak-$(date +%Y%m%d-%H%M%S)"
    done
    pulsar_xml apply sources "$sources"
    pulsar_xml apply profile "$profile"
    resolved
    ok "Registered $REPO_DIR as a development folder (backups next to the XML files)"
    TODO+=("Start Pulsar: the plugin is listed and enabled. Pulsar compiles it on that start.")
}

step_wivrn() {
    heading "WiVRn and adb"
    if have flatpak && flatpak info "$WIVRN_FLATPAK" >/dev/null 2>&1; then
        ok "WiVRn server: Flatpak $WIVRN_FLATPAK"
    elif have wivrn-server; then
        ok "WiVRn server: $(command -v wivrn-server)"
    else
        missing "WiVRn server not installed"
        note "Flatpak: flatpak install flathub $WIVRN_FLATPAK"
        note "(other options: https://github.com/WiVRn/WiVRn)"
        TODO+=("Install the WiVRn server (flatpak install flathub $WIVRN_FLATPAK).")
    fi
    if have adb; then
        ok "adb: $(command -v adb) (needed only for a USB cable)"
    else
        warn "adb not installed. Needed only to connect the headset by USB (android-tools / adb package)."
    fi
}

step_launcher() {
    heading "Desktop launcher"
    local desktop="$DATA_DIR/applications/space-engineers-vr.desktop"
    local script="$DATA_DIR/cheezecake-sevr/launch.sh"
    if [ "$CHECK_ONLY" -eq 1 ] || [ "$WANT_LAUNCHER" -eq 0 ]; then
        if [ -f "$desktop" ]; then ok "Launcher installed: $desktop"
        else note "not installed (--launcher adds one)"; fi
        return
    fi
    [ -n "$PULSAR_DIR" ] && [ -n "$SE_BIN64" ] || { warn "skipped: needs Pulsar and Space Engineers"; return; }

    mkdir -p "$(dirname "$script")" "$(dirname "$desktop")"
    # The launcher is a script of its own so the desktop entry needs no quoting
    # tricks, and so it can be run from a terminal to see what it does.
    cat > "$script" <<EOF
#!/usr/bin/env bash
# Written by setup.sh --launcher. Starts the WiVRn server, bridges adb if a
# headset is on USB, then starts Pulsar with Space Engineers.
set -u
# The server is ready once it listens on its socket; until then the game's
# OpenXR calls fail with "runtime unavailable".
socket=\${XDG_RUNTIME_DIR:-/run/user/\$(id -u)}/wivrn/comp_ipc
wivrn_up() { pgrep -x wivrn-server >/dev/null 2>&1 && [ -S "\$socket" ]; }
if ! wivrn_up; then
    if flatpak info $WIVRN_FLATPAK >/dev/null 2>&1; then
        flatpak run $WIVRN_FLATPAK >/dev/null 2>&1 &
    elif command -v wivrn-server >/dev/null 2>&1; then
        wivrn-server >/dev/null 2>&1 &
    fi
    for _ in \$(seq 30); do wivrn_up && break; sleep 0.5; done
fi
if command -v adb >/dev/null 2>&1 && adb get-state >/dev/null 2>&1; then
    adb reverse tcp:9757 tcp:9757 >/dev/null 2>&1
fi
cd "$PULSAR_DIR" && exec ./Interim.bin -bin64 "$SE_BIN64" "\$@"
EOF
    chmod +x "$script"
    cat > "$desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Space Engineers VR
Comment=Space Engineers through Pulsar, with the WiVRn server started first
Exec="$script"
Path=$PULSAR_DIR
Terminal=false
Categories=Game;
EOF
    ok "Launcher installed: $desktop"
    note "Open the WiVRn app in the headset and connect before the game's window opens."
}

# ---- summary ----------------------------------------------------------------

summary() {
    heading "Headset settings"
    cat <<'EOF'
  In the WiVRn app on the headset (disconnect first), turn on Extended
  configuration and set:
    Foveated encoding   30% or lower
    Render resolution   120%
    Refresh rate        90
    Bitrate             200 Mbit/s
  Over a USB cable instead of Wi-Fi: run `adb reverse tcp:9757 tcp:9757`,
  pick the USB option in the WiVRn dashboard, and switch the headset's Wi-Fi off.
EOF
    heading "Summary"
    if [ "${#TODO[@]}" -gt 0 ]; then
        echo "  Left for you:"
        local item
        for item in "${TODO[@]}"; do printf '    - %s\n' "$item"; done
    elif [ "$PENDING" -eq 0 ]; then
        echo "  Nothing left to install."
    fi
    if [ "$PENDING" -gt 0 ] && [ "$CHECK_ONLY" -eq 1 ]; then
        echo "  $PENDING part(s) missing. Run without --check to set them up."
    fi
    if [ "$PENDING" -eq 0 ] && [ -n "$PULSAR_DIR" ] && [ -n "$SE_BIN64" ]; then
        echo
        echo "  Start the WiVRn server, connect the headset, then launch with:"
        echo "    cd '$PULSAR_DIR' && ./Interim.bin -bin64 '$SE_BIN64'"
        echo "  (or use the launcher from --launcher)."
    fi
}

main() {
    have python3 || die "python3 is required"
    [ "$(uname -s)" = Linux ] || die "this script is for Linux; on Windows use deploy.ps1 or Pulsar's plugin list"

    [ "$CHECK_ONLY" -eq 1 ] && echo "Checking only; nothing will be changed."
    step_locate
    step_loader
    step_openal
    if [ "$SKIP_DXVK" -eq 1 ] && [ "$CHECK_ONLY" -eq 0 ]; then
        heading "DXVK Native (OpenXR support)"
        warn "skipped (--no-dxvk)"
    else
        step_dxvk
    fi
    step_plugin
    step_wivrn
    step_launcher
    summary

    if [ "$CHECK_ONLY" -eq 1 ]; then [ "$PENDING" -eq 0 ] || exit 1; fi
}

main
