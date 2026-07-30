#!/usr/bin/env bash
#
# sideload.sh — build, package, ship, and (gated) restart Jellyfin on Servarr.
#
# Releases install via the plugin catalog (GitHub Actions + Pages manifest);
# this script is the fast dev-iteration path for testing before tagging.
#
# Flags:
#   --no-restart        Sideload the plugin but skip the restart step entirely
#   --restart-yes       Skip the interactive confirmation and restart anyway
#                       (intended for "I know what I'm doing" reruns; don't
#                       use this from automation that runs without watching)
#
# Strict gate (per ~/zigerusgames/CLAUDE.md destructive-action rules):
#   The restart step interrupts production Jellyfin streams. Default behavior
#   is to prompt for explicit "yes" before issuing `docker restart jellyfin`.

set -euo pipefail

# -----------------------------------------------------------------------------
# Config
# -----------------------------------------------------------------------------
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_DIR="$REPO_ROOT/Jellyfin.Plugin.Dtdd"
PROPS_FILE="$REPO_ROOT/Directory.Build.props"
BUILD_YAML="$REPO_ROOT/build.yaml"

REMOTE_HOST="servarr"
REMOTE_TMP="/tmp"
# Jellyfin loads plugins from /config/data/plugins inside the container.
# With the linuxserver/jellyfin compose mount /home/zigerus/appdata/jellyfin:/config,
# that maps to /home/zigerus/appdata/jellyfin/data/plugins on the host.
# (The older /config/plugins location was found empty during Phase 3 pre-flight;
# the real install path is /config/data/plugins per the running container's layout.)
REMOTE_PLUGINS_DIR="/home/zigerus/appdata/jellyfin/data/plugins"
REMOTE_CONTAINER="jellyfin"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"

# -----------------------------------------------------------------------------
# Flags
# -----------------------------------------------------------------------------
RESTART_MODE="prompt"   # prompt | yes | no
for arg in "$@"; do
    case "$arg" in
        --no-restart)    RESTART_MODE="no" ;;
        --restart-yes)   RESTART_MODE="yes" ;;
        -h|--help)
            # Print only the top-of-file usage block (up to the first blank line).
            sed -n '2,/^$/p' "$0" | sed 's/^# \?//'
            exit 0
            ;;
        *)
            echo "Unknown flag: $arg" >&2
            exit 64
            ;;
    esac
done

# -----------------------------------------------------------------------------
# 1) Build
# -----------------------------------------------------------------------------
echo "==> Building Release configuration"
"$DOTNET" build "$PROJECT_DIR" -c Release --nologo

# -----------------------------------------------------------------------------
# 2) Read version + GUID + manifest fields
# -----------------------------------------------------------------------------
VERSION="$(grep -oP '<Version>\K[^<]+' "$PROPS_FILE")"
GUID="$(grep -oP '^guid:\s*"\K[^"]+' "$BUILD_YAML")"
TARGET_ABI="$(grep -oP '^targetAbi:\s*"\K[^"]+' "$BUILD_YAML")"
PLUGIN_NAME="$(grep -oP '^name:\s*"\K[^"]+' "$BUILD_YAML")"
OWNER="$(grep -oP '^owner:\s*"\K[^"]+' "$BUILD_YAML")"

if [[ -z "$VERSION" || -z "$GUID" ]]; then
    echo "ERROR: failed to parse version or guid from build metadata" >&2
    exit 1
fi

DLL_PATH="$PROJECT_DIR/bin/Release/net9.0/Jellyfin.Plugin.Dtdd.dll"
if [[ ! -f "$DLL_PATH" ]]; then
    echo "ERROR: build artifact missing at $DLL_PATH" >&2
    exit 1
fi

echo "==> Plugin: $PLUGIN_NAME $VERSION (guid=$GUID, targetAbi=$TARGET_ABI)"

# -----------------------------------------------------------------------------
# 3) Package: zip = DLL + meta.json
# -----------------------------------------------------------------------------
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

cp "$DLL_PATH" "$STAGE/"

TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%S)"
cat > "$STAGE/meta.json" <<EOF
{
  "category": "General",
  "changelog": "Sideloaded development build ($TIMESTAMP).",
  "description": "Per-user content warnings driven by doesthedogdie.com.",
  "guid": "$GUID",
  "name": "$PLUGIN_NAME",
  "overview": "Per-user content warnings driven by doesthedogdie.com",
  "owner": "$OWNER",
  "targetAbi": "$TARGET_ABI",
  "timestamp": "$TIMESTAMP",
  "version": "$VERSION"
}
EOF

ZIP_NAME="Jellyfin.Plugin.Dtdd_$VERSION.zip"
ZIP_PATH="$STAGE/$ZIP_NAME"
# Use Python's zipfile rather than `zip` so this script works on hosts where
# the `zip` binary isn't installed (HP Mini doesn't ship it by default).
python3 -c "
import sys, zipfile
stage, zip_name = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(f'{stage}/{zip_name}', 'w', zipfile.ZIP_DEFLATED) as z:
    z.write(f'{stage}/Jellyfin.Plugin.Dtdd.dll', 'Jellyfin.Plugin.Dtdd.dll')
    z.write(f'{stage}/meta.json', 'meta.json')
" "$STAGE" "$ZIP_NAME"
echo "==> Packaged $ZIP_NAME ($(stat -c%s "$ZIP_PATH") bytes)"

# -----------------------------------------------------------------------------
# 4) Ship to Servarr
# -----------------------------------------------------------------------------
echo "==> Copying to $REMOTE_HOST:$REMOTE_TMP/"
scp -q "$ZIP_PATH" "$REMOTE_HOST:$REMOTE_TMP/$ZIP_NAME"

# -----------------------------------------------------------------------------
# 5) Unpack into versioned plugin dir
# -----------------------------------------------------------------------------
REMOTE_PLUGIN_DIR="$REMOTE_PLUGINS_DIR/${PLUGIN_NAME}_$VERSION"
echo "==> Unpacking on $REMOTE_HOST into $REMOTE_PLUGIN_DIR"
# Unquoted heredoc is deliberate: all $VARS here are LOCAL (computed on HP Mini
# from the build); we want them substituted before the script is piped to the
# remote bash. Nothing in the body needs to expand server-side.
# Servarr doesn't ship `unzip` either, so use python's zipfile module — same
# approach as the local packaging step above for consistency.
# shellcheck disable=SC2087
ssh "$REMOTE_HOST" bash -s <<REMOTE_EOF
set -euo pipefail
mkdir -p "$REMOTE_PLUGIN_DIR"
python3 -c "
import zipfile
with zipfile.ZipFile('$REMOTE_TMP/$ZIP_NAME') as z:
    z.extractall('$REMOTE_PLUGIN_DIR')
"
rm -f "$REMOTE_TMP/$ZIP_NAME"
ls -la "$REMOTE_PLUGIN_DIR"
REMOTE_EOF

# -----------------------------------------------------------------------------
# 6) Strict gate: restart Jellyfin
# -----------------------------------------------------------------------------
echo
echo "==> Plugin staged. To activate, Jellyfin needs to restart."
echo "    This will interrupt any active streams. (Production destructive action.)"
echo "    Command: ssh $REMOTE_HOST docker restart $REMOTE_CONTAINER"
echo

case "$RESTART_MODE" in
    no)
        echo "==> --no-restart given; skipping restart."
        echo "    Restart manually when ready:"
        echo "      ssh $REMOTE_HOST docker restart $REMOTE_CONTAINER"
        exit 0
        ;;
    yes)
        echo "==> --restart-yes given; proceeding without prompt."
        ;;
    prompt)
        if [[ ! -t 0 ]]; then
            echo "ERROR: not running on a TTY and no --restart-yes / --no-restart flag." >&2
            echo "       Refusing to restart without explicit consent." >&2
            exit 2
        fi
        read -rp "Restart Jellyfin now? [y/N] " ans
        case "$ans" in
            [Yy]|[Yy][Ee][Ss]) ;;
            *)
                echo "==> Declined. Plugin is staged; restart manually when ready:"
                echo "      ssh $REMOTE_HOST docker restart $REMOTE_CONTAINER"
                exit 0
                ;;
        esac
        ;;
esac

echo "==> Restarting Jellyfin"
# Client-side expansion of $REMOTE_CONTAINER is intentional — it's a hardcoded
# constant ("jellyfin") set at the top of this script.
# shellcheck disable=SC2029
ssh "$REMOTE_HOST" "docker restart $REMOTE_CONTAINER"
echo "==> Restart issued. Verify via: ssh $REMOTE_HOST docker logs $REMOTE_CONTAINER --tail 50"
