#!/usr/bin/env bash
# Apply small patches to the vendored alliedmodders SDKs to keep this
# project buildable against current upstream HEAD. Each patch is gated by
# a grep so re-runs are idempotent (safe on cache-hit CI runs and on
# repeated local builds).
#
# Usage: scripts/ci-patch-sdks.sh <hl2sdk-root>
# Example: scripts/ci-patch-sdks.sh ReplicaHider/hl2sdk

set -eu -o pipefail

SDK_ROOT="${1:?usage: $0 <path/to/hl2sdk>}"

[ -d "$SDK_ROOT" ] || { echo "ERROR: $SDK_ROOT is not a directory" >&2; exit 1; }

# ---- 1. CPlayerSlot needs a default constructor.
#
# SourceHook's SH_DECL_HOOK1 macro expands to `my_rettype orig_ret{};`
# which value-initializes the rettype with a brace-init-list. CPlayerSlot
# in current hl2sdk/cs2 only declares `CPlayerSlot( int slot )`, so the
# compiler suppresses the implicit default ctor and the macro fails to
# compile when hooking a function that returns CPlayerSlot (e.g.
# IVEngineServer::CreateFakeClient). We add a default ctor that
# initializes to the invalid sentinel (-1), matching the semantics of
# the existing Invalidate() method.
#
# Hardening (issue #54): the legacy script silently no-op'd if the file
# moved (path drift) or if upstream reformatted the anchor line (regex
# drift). Both modes produced downstream macro-error builds rather than
# a clear "the patch didn't apply" diagnostic. Now:
#   - missing playerslot.h     → exit 3 (layout drift)
#   - anchor line not found    → exit 2 (regex drift)
#   - sed applied but post-grep doesn't see the new line → exit 2
# Already-patched (default ctor present) remains a clean idempotent no-op.
playerslot="$SDK_ROOT/public/playerslot.h"
if [ ! -f "$playerslot" ]; then
    echo "ERROR: $playerslot not found (hl2sdk layout changed?)" >&2
    exit 3
fi
if ! grep -q 'CPlayerSlot() : m_Data' "$playerslot"; then
    if ! grep -q 'CPlayerSlot( int slot ) : m_Data( slot ) {}' "$playerslot"; then
        echo "ERROR: $playerslot has no 'CPlayerSlot( int slot ) : m_Data( slot ) {}' anchor line — upstream reformatted? regex needs updating" >&2
        exit 2
    fi
    sed -i 's|CPlayerSlot( int slot ) : m_Data( slot ) {}|CPlayerSlot() : m_Data( -1 ) {}\n\tCPlayerSlot( int slot ) : m_Data( slot ) {}|' \
        "$playerslot"
    if ! grep -q 'CPlayerSlot() : m_Data' "$playerslot"; then
        echo "ERROR: sed claimed success but post-check failed in $playerslot (silent regex mismatch?)" >&2
        exit 2
    fi
    echo "patched: $playerslot (added CPlayerSlot default ctor)"
else
    echo "skip: $playerslot already has CPlayerSlot default ctor"
fi
