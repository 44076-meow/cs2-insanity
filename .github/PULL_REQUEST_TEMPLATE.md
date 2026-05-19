<!--
Thanks for the PR. Keep the summary focused on *why*. The "what" should be
obvious from the diff.
-->

## Summary

<!-- One paragraph: what problem this solves, why this approach. -->

## Side(s) touched

- [ ] InsanityRevive (C# / CSSharp / .NET 8)
- [ ] InsanityHider (C++ / Metamod:Source)
- [ ] Shared mmap protocol (`/tmp/insanityrevive_fake_slots.bin`)
- [ ] CI / build / `scripts/`
- [ ] Docs / `notes/`

## Pool / protocol compatibility

- [ ] No change to the shared-pool layout.
- [ ] Pool layout changed — version bumped in both `InsanityRevive` and
      `InsanityHider`, and the older version is rejected with a clear log
      line on both sides.

## Detour / schema impact

- [ ] No schema or detour-anchor change.
- [ ] Schema field / detour anchor changed — gamedata updated, anchor
      re-verified on the current CS2 build (note which `PatchVersion=`).

## CI / reproducibility

- [ ] `dotnet build` clean locally.
- [ ] Two-pass C# build still produces identical sha256 (or the
      `sha256`-check job in CI is green on the PR).
- [ ] C++ build green against the vendored hl2sdk / metamod-source after
      `scripts/ci-patch-sdks.sh`.

## Behavior on a live server

<!-- Skip if docs/CI-only. Otherwise: which map(s), bot count, what you
verified. "Connected one human client, confirmed scoreboard hides BOT
icon and renders a non-zero ping" beats "looks fine". -->

## Out of scope

<!-- Anything intentionally not addressed here so reviewers don't ask. -->

## Linked issues / discussions

<!-- "Closes #N" / "Refs discussion #N". -->
