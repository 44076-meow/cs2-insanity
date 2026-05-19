# Discussions — one-time setup

GitHub does not let you create Discussion *categories* from a file in the
repo. Posting templates (`.github/DISCUSSION_TEMPLATE/<slug>.yml`) only take
effect once a category with the matching slug exists. This file is the
checklist to bring the UI side in line with the templates already committed
here.

## 1. Enable Discussions

Repo **Settings → General → Features → Discussions → Enable**. (One-time;
skip if already on.)

## 2. Create the categories below

**Settings → Discussions → Categories → New category.** For each row:
match the slug exactly (GitHub derives the slug from the name — if the
auto-slug differs, click **Edit** on the category and override it). The
*Format* column controls the discussion type chip; pick it from the radio
group when creating.

| # | Name | Emoji | Slug (must match) | Format | Purpose |
|---|---|---|---|---|---|
| 1 | Announcements | 📣 | `announcements` | Announcement | Releases, CS2-update advisories. Maintainer-post only. |
| 2 | Q&A | ❓ | `q-a` | Question / Answer | General questions. Has accepted-answer flow. |
| 3 | Ideas | 💡 | `ideas` | Open-ended | Feature / research pitches. |
| 4 | Show & Tell | 🙌 | `show-and-tell` | Open-ended | Demos, screenshots, server setups using the stack. |
| 5 | Schema drift watch | 🧬 | `schema-drift-watch` | Open-ended | "CS2 update broke X" reports. One thread per drift event. |
| 6 | Detours & engine internals | 🪝 | `detours-and-engine-internals` | Open-ended | `UpdateLookAngles`, `ProcessUsercmds`, hook ordering. |
| 7 | Bot behavior & AimState | 🤖 | `bot-behavior-and-aimstate` | Open-ended | Persona, aim error, BotProfile, pool versioning. |
| 8 | Build & reproducibility | 🛠️ | `build-and-reproducibility` | Open-ended | CI, sha256 drift, vendored SDK patches. |
| 9 | Deployment & server ops | 🚀 | `deployment-and-server-ops` | Open-ended | `server.cfg`, `deploy.sh`, mapchange, telemetry. |
| 10 | Schema fields & gamedata | 📐 | `schema-fields-and-gamedata` | Open-ended | Offsets, struct layout, gamedata JSON. |

The posting templates for these slugs are already in
[`.github/DISCUSSION_TEMPLATE/`](../.github/DISCUSSION_TEMPLATE) and will
attach automatically once the categories exist.

## 3. (Optional) Remove or hide built-in default categories

GitHub seeds a fresh Discussions tab with `General`, `Ideas`, `Polls`,
`Q&A`, `Show and tell`, `Announcements`. You can:

- delete `General` and `Polls` (we don't use them), and
- edit the seeded `Q&A`, `Ideas`, `Show and tell`, `Announcements` to use
  the slugs/emoji from the table above instead of creating duplicates.

If you do the latter, **don't change slugs of categories that already have
threads in them** without checking — links will break. On a brand-new
Discussions tab there's nothing to lose.

## 4. Verify templates are wired

After creating each custom category, click **New discussion → pick the
category** and confirm the form template shows up (e.g. for
`schema-drift-watch` you should see the "CS2 dedicated server build" input
as the first field). If the form doesn't appear, the slug doesn't match —
fix the category slug, not the file name.

## 5. Pin a couple of meta threads

Worth pinning under Announcements once categories are live:

- "How to file a Schema drift watch report" (link to the category, explain
  the one-event-per-thread rule)
- "Plugin compatibility matrix" — link to [`docs/COMPAT.md`](../docs/COMPAT.md)
