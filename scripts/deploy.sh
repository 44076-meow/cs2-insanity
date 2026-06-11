#!/usr/bin/env bash
# =============================================================================
# scripts/deploy.sh — build + verify + deploy Replica.dll
# =============================================================================
#
# WHAT it does:
#   1. Verifies working tree is committed (no dirty diff).
#   2. Builds Replica in Release config.
#   3. Computes sha256 of the built DLL + reads current commit-sha.
#   4. Prints a chat.md-ready stanza for paste into claude/chat.md.
#   5. Optionally deploys the DLL+PDB to the live server's plugin dir.
#
# WHY it exists:
#   Twice in this project we hit "DLL drift" — the deployed binary on the
#   server didn't match any tagged commit. Root cause was build-and-deploy
#   without a recorded baseline. This script makes the baseline mandatory:
#   every deploy prints (and optionally appends) the sha256 + commit-sha so
#   future drift can be diagnosed.
#
# USAGE:
#   ./scripts/deploy.sh                   build + print + interactive deploy
#   ./scripts/deploy.sh --build-only      build + print, no deploy
#   ./scripts/deploy.sh --auto            build + deploy without prompt
#   ./scripts/deploy.sh --append-chat     also append to claude/chat.md
#
# Set DEPLOY_DIR to override the deploy target (default = live CS2 server).
# =============================================================================

set -eu -o pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJ_DIR="$REPO_ROOT/Replica"
DEPLOY_DIR="${DEPLOY_DIR:-/mnt/storage/cs2-server/game/csgo/addons/counterstrikesharp/plugins/Replica}"

mode="interactive"
append_chat=false
for arg in "$@"; do
    case "$arg" in
        --build-only)  mode="build-only" ;;
        --auto)        mode="auto" ;;
        --append-chat) append_chat=true ;;
        -h|--help)     sed -n '4,30p' "$0"; exit 0 ;;
        *) echo "unknown arg: $arg" >&2; exit 1 ;;
    esac
done

cd "$REPO_ROOT"

# 1. Working-tree clean check.
if ! git diff --quiet || ! git diff --cached --quiet; then
    echo "ERROR: working tree has uncommitted changes. Commit before deploy." >&2
    git status --short >&2
    exit 1
fi

commit_sha=$(git rev-parse HEAD)
commit_short=$(git rev-parse --short HEAD)
branch=$(git rev-parse --abbrev-ref HEAD)
nearest_tag=$(git describe --tags --abbrev=0 2>/dev/null || echo "no-tag")

# 2. Build.
echo "==> Building Replica (Release)..."
cd "$PROJ_DIR"
dotnet build -c Release --nologo --verbosity minimal >/dev/null
dll_path="$PROJ_DIR/bin/Release/net8.0/Replica.dll"
pdb_path="$PROJ_DIR/bin/Release/net8.0/Replica.pdb"
[ -f "$dll_path" ] || { echo "ERROR: build did not produce $dll_path" >&2; exit 2; }
cd "$REPO_ROOT"

# 3. Compute baseline.
dll_sha=$(sha256sum "$dll_path" | awk '{print $1}')

# 4. Print stanza.
ts=$(date -u +%Y-%m-%dT%H:%M:%SZ)
echo
echo "============== chat.md baseline stanza =============="
cat <<EOF
**Deploy baseline @ $ts**
- branch: $branch
- commit: $commit_sha ($commit_short)
- nearest tag: $nearest_tag
- dll sha256: \`$dll_sha\`
- dll path: $dll_path
EOF
echo "======================================================"
echo

if $append_chat; then
    chat_md="$REPO_ROOT/claude/chat.md"
    {
        echo
        echo "---"
        echo
        echo "## $ts — deploy.sh auto-stamp"
        echo
        echo "**Deploy baseline**"
        echo "- branch: $branch"
        echo "- commit: $commit_sha ($commit_short)"
        echo "- nearest tag: $nearest_tag"
        echo "- dll sha256: \`$dll_sha\`"
        echo
    } >> "$chat_md"
    echo "==> Appended to $chat_md"
fi

# 5. Deploy (or not).
deploy_now=false
case "$mode" in
    build-only) ;;
    auto)       deploy_now=true ;;
    interactive)
        printf "Deploy DLL to %s? [y/N] " "$DEPLOY_DIR"
        read -r reply
        case "$reply" in y|Y|yes) deploy_now=true ;; esac
        ;;
esac

if $deploy_now; then
    [ -d "$DEPLOY_DIR" ] || { echo "ERROR: deploy dir missing: $DEPLOY_DIR" >&2; exit 3; }
    cp "$dll_path" "$DEPLOY_DIR/Replica.dll"
    cp "$pdb_path" "$DEPLOY_DIR/Replica.pdb"
    deployed_sha=$(sha256sum "$DEPLOY_DIR/Replica.dll" | awk '{print $1}')
    if [ "$deployed_sha" = "$dll_sha" ]; then
        echo "==> Deployed OK. Hash matches baseline."
    else
        echo "ERROR: deployed sha ($deployed_sha) != built sha ($dll_sha)" >&2
        exit 4
    fi
    echo "Hot-reload: rcon \"css_plugins reload Replica\""
fi
