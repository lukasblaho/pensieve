# macOS Menu Bar / Sidepanel Widget

Add a small macOS UI to control Pensieve without the terminal: trigger ad-hoc
`sync`, start/stop the `watch` process, and show recent meetings (title, tags,
timestamp) pulled from `OUTPUT_DIR`.

## Recommended approach: menu bar app (NSStatusItem / SwiftUI `MenuBarExtra`)

- True WidgetKit widgets (Notification Center/desktop) are read-only,
  timeline-refreshed, sandboxed, and can't shell out to `dotnet-script` or
  watch a folder — not a good fit for interactive controls.
- A menu bar app with a popover is the standard pattern instead (e.g.
  Bartender, Ollama's menu bar app).
- "Run sync now" / "start watch" = shell out to `dotnet-script main.csx sync`
  (or a published binary) via `Process`.
- "Recents" list = just read the newest folders under `OUTPUT_DIR` (already
  sortable by naming convention) and parse `note.md` frontmatter — no new
  Pensieve code required.
- For live status while `watch` runs persistently (e.g. via the `launchd`
  agent from the README), add a tiny local status file or Unix
  socket/HTTP endpoint in Pensieve that the menu bar app polls, instead of
  re-parsing files on every open.
- Optional companion: a WidgetKit widget sharing an App Group container with
  the menu bar app for a glanceable view + simple App Intents button (e.g.
  "Run Sync"), fed by a status JSON the main app writes.

## Build requirements

- Xcode (free) + Swift, or just Swift Package Manager (`swift build`) with
  Xcode Command Line Tools — no Xcode GUI required either way.
- Free Apple ID + "Personal Team" signing is enough to build/run locally;
  no paid Apple Developer Program membership needed for personal use.
- Paid Developer Program ($99/yr) only required if distributing to other
  people/Macs: Developer ID signing + notarization (`notarytool`/`stapler`)
  to avoid Gatekeeper warnings, or App Store distribution (which also forces
  stricter sandboxing that would complicate shelling out to
  `dotnet-script`/Pensieve).

## Alternative implementations considered

- **Raycast extension** — lighter-weight than a standalone app; users
  already running Raycast get a command palette entry for "Run Pensieve
  sync" / "Show recent meetings" for free, no code signing/notarization
  concerns at all. Good low-effort alternative if a dedicated menu bar
  presence isn't required.
- **Browser-based local dashboard** — a tiny local web server (could live
  in Pensieve itself) serving a simple HTML/JS page with recents + a "run
  sync" button; opened as a pinned browser tab or a minimal
  `NSStatusItem` that just opens the URL. Avoids Swift/Xcode entirely, but
  loses "always one click away in the menu bar" feel.
- **BitBar/xbar plugin** — a shell script menu bar plugin (Ruby/Python/Bash)
  running under the free xbar app; near-zero Swift/Xcode work, refresh
  interval based, good for read-only "recents" display, weaker for
  buttons that need custom logic beyond simple `bash="..." refresh=true`
  actions.
- **Full WidgetKit widget only** (no menu bar app) — simplest to ship if
  interactivity requirements stay minimal (just an App Intent "Run Sync"
  button + timeline-refreshed recents list), but no live/instant control
  and requires wrapping Pensieve in a proper macOS app bundle anyway.
