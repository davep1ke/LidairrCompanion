# LidarrCompanion

A companion app for [Lidarr](https://lidarr.audio) that handles the parts of a music-import
workflow Lidarr doesn't: triaging its manual-import queue, matching unmatched files to artists,
sifting a folder of ripped/downloaded tracks (keep/trash with preview playback), and gating
imports on cover art before they land in the library.

**This project is mid-migration** from a Windows-only WPF desktop app to a Blazor Server web app
that runs in Docker (target: the owner's TrueNAS box), reachable from any browser on the LAN. The
full migration plan, including the reasoning behind every major decision below, lives at
`/home/davepike/.claude/plans/peaceful-prancing-abelson.md`. Read it before making architectural
changes — it explains *why*, not just *what*.

## Standing instructions for future sessions

- **Keep this file current.** When you make a non-obvious architectural decision, find a gotcha
  that cost real debugging time, or establish a convention, add it here. Don't wait to be asked.
- **Write tests for new logic as you go**, not just when asked. See "Testing" below for what's
  realistically testable in this codebase and where it lives. A bug fix to pure logic (a query
  builder, a message formatter, a path-mapping fallback) should come with a regression test in
  the same change, the same way you'd expect from any other project.
- **Don't add user-facing hints without asking** — no explanatory text, tooltips, "this can take a
  while" notes or advisory suffixes on messages unless the user requested them. Ship the behaviour
  and only the messages asked for; put explanations in the reply or here instead.
- Run `dotnet test LidarrCompanion.Core.Tests` before considering a change to `LidarrCompanion.Core`
  done. It's fast (well under a second) and every green run so far has been a real regression
  check, not theater — this suite has caught real signature-mismatch breaks during refactors.

## Solution layout

Two generations of the app currently coexist in this repo:

- **`LidarrCompanion.csproj`** (root) + `Forms/`, and the root-level `Helpers/`, `Models/`,
  `Services/` — the **original WPF desktop app**. Windows-only, left untouched on purpose so it
  stays a working fallback until the Blazor app has full parity. Don't "clean up" or delete these
  without being asked.
- **`LidarrCompanion.Core`** — portable business logic (`net10.0`, no UI framework references).
  Helpers, services, and DTOs shared by both the old WPF app and the new web app conceptually,
  though in practice the WPF app has its own copies (see below) and only the web app currently
  references this project.
- **`LidarrCompanion.Web`** — the new Blazor Server app. Everything UI-facing.
- **`LidarrCompanion.Core.Tests`** — xUnit tests for `LidarrCompanion.Core` only.

**Important:** `LidarrCompanion.Core/Helpers/*.cs` and the root `Helpers/*.cs` are **separate,
independently-maintained copies** with the same class names (e.g. two `Logger.cs`, two
`MusicBrainzHelper.cs`). A fix made in one does not apply to the other. When editing shared-sounding
logic, check `find . -iname "<FileName>.cs" -not -path "*/bin/*" -not -path "*/obj/*"` first —
it's easy to edit the WPF copy by accident (it's usually the shorter path) and wonder why nothing
changed in the browser.

## Architecture

### Why Blazor Server (not a JS frontend, not Avalonia)

The owner is a C# developer, not a JS developer. Blazor Server also let the vast majority of the
WPF app's `Helpers`/`Services` logic move to `LidarrCompanion.Core` nearly verbatim — the real
rewrite work was UI and a handful of genuinely-Windows-only subsystems (playback, cover-art image
handling, "open folder"). See the migration plan for the full audit of what ported as-is vs. what
needed redesign.

### Singleton services with cross-scope endpoints (the recurring DI gotcha)

`TriageService`, `SiftService`, `CoverArtGateService`, and `PrefetchService` are all registered as
**`AddSingleton`**, not `AddScoped`, even though each backs exactly one Blazor page's state. The
reason: minimal API endpoints like `/audio/stream/{id}` and `/audio/sift-stream/{id}` run in their
own per-request DI scope, completely separate from the Blazor circuit's scope. A `Scoped`
registration would hand that endpoint a *different, empty* instance than the one the page is
actually using — the stream endpoint would 404 on every id. Singleton means both sides resolve the
exact same instance.

Consequence: **these services hold real mutable state for the app's assumed single active
session.** There's no multi-user isolation. If multi-session support is ever wanted, this is the
piece that needs redesigning first (probably: move the state into something keyed by circuit/user,
and give the audio endpoints another way to resolve it — e.g. a token in the URL instead of DI).

`IPlaybackService` is `AddScoped` by contrast — it's not needed by any minimal API endpoint, so it
can be normal per-circuit state.

### Settings naming convention: `...Lidarr` vs `...Companion`

Path-mapping settings come in pairs: one side is the path *as Lidarr's own container sees it*, the
other is the path *as this app's container sees it* (they may be mounted differently even though
both point at the same underlying files). Naming convention, chosen deliberately over the old
`Local`/`Server` naming since both sides are server-based now:

- `ImportPathLidarr` / `ImportPathCompanion`
- `DownloadPathLidarr` / `DownloadPathCompanion`
- `LibraryPathLidarr` / `LibraryPathCompanion`

`FileOperationsHelper.ResolveMappedPathAnyKnown` is what actually applies these mappings when
translating a path Lidarr handed back into a path this container can open.

### Cover-art import gate

Old WPF behavior: a blocking modal (`CoverArtWindow.ShowDialog()`) mid-pipeline. That can't work
server-side (nothing to block — there's no thread to hang, and even if there were, one user
shouldn't be able to freeze the app for everyone). New design in `CoverArtGateService`: **passive
state, not a blocking await.** `ImportRunner` figures out which actions still need artwork and
hands them back; `TriageService` stashes them in `CoverArtGateService.BeginGate(...)` and returns
immediately rather than blocking the triage page. The `/cover-art` page resolves the gate
(Save & Next / Complete / Abort), and completing/aborting calls back into `TriageService` to
resume or cancel the rest of the pipeline. `Import.razor` auto-navigates to `/cover-art` the moment
a gate opens (`CoverArtGate.HasPending`), rather than making the user click a notification link.

### Cover art search: Discogs + SerpApi, with a shared preview-then-drag step

`DiscogsHelper` (`LidarrCompanion.Core/Helpers`) is the cover-art gate's primary lookup, **not**
MusicBrainz — moved off MusicBrainz because Discogs' catalog (vinyl, promos, remixes, DJ-culture
releases) has noticeably better coverage for this app's actual library than MusicBrainz's more
retail-release-oriented one, and because MusicBrainz kept needing query-forgiveness fixes for
real-world tag variance (see the git history — two separate exact-phrase-match bugs, on the artist
field and then the album field, both confirmed live against the API before fixing). Needs a free
personal access token (`SettingKey.DiscogsToken`, self-serve at
discogs.com/settings/developers, no approval wait) — Discogs deliberately omits image URLs from
*unauthenticated* search responses regardless of the per-minute rate limit, confirmed directly
against the live API, so a missing token means every search comes back with no images at all, not
a subtler failure.

`MusicBrainzHelper` itself is untouched and still used for artist search elsewhere
(`ManualMatchDialog`) — only the cover-art gate's own lookup moved off it. If MusicBrainz's own
release-title matching is ever revisited, its query builder already went through the same
loosening fix Discogs never needed (Discogs' search isn't Lucene-exact-phrase in the first place).

Three tiers, all manual (see below for why none of them auto-run or auto-stage):
1. **`SearchDiscogs`** — structured `artist=`/`release_title=` search via Discogs' own fields
   (`DiscogsHelper.SearchAsync`).
2. **`BrowseByArtist`** — artist-only Discogs search for when the structured search finds nothing,
   showing everything Discogs has for the artist as a clickable grid.
3. **`SearchManual`** — `SerpApiHelper`, a Google Images search, for when Discogs has nothing at
   all. SerpApi's free tier is 250 searches/month; **billing is 1 credit per search call
   regardless of result count** — downloading a selected image is a plain HTTP GET to the image's
   own host and doesn't touch SerpApi at all, so it's free. Confirmed via SerpApi's own docs, not
   assumed.

**No tier auto-runs on landing on a file, and no tier auto-stages its first result.** Both used to
happen (an automatic MusicBrainz search fired the moment an item loaded) and were removed after a
direct complaint: on a loosened, more-forgiving query, the first hit is more likely to be a
plausible-but-wrong match than a clearly-bad one, so silently staging it just moves the mistake
later in the pipeline instead of preventing it. All three tiers now populate a list of candidates
for the user to look at instead.

**The shared preview-then-drag step** (`_previewSrc`/`LoadPreview`/`CommitPreview` in
`CoverArt.razor`) replaced an earlier click-thumbnail-straight-to-stage design, and before that a
modal-dialog lightbox design — both rejected as "click -> open window -> wait -> click" friction.
Clicking any result thumbnail (from either Discogs tier or the SerpApi grid) loads it into a side
panel next to the main cover-art box with its pixel dimensions shown (via a plain `onload` JS
attribute reading `naturalWidth`/`naturalHeight` — no JS interop module needed for that); the user
then either drags that panel's image onto the main box or clicks "Use this image" as a fallback.
Both paths call the same `CommitPreview`, which is the only point that actually downloads the full
image server-side — loading the preview itself is just pointing an `<img>` at Discogs'/SerpApi's
own URL directly, no network call from the server at all until commit.

### Global audio player

One `<audio>` element and its JS interop wrapper live in `MainLayout.razor` (via `PlayerBar.razor`),
outside `@Body`, so navigating between pages never destroys/recreates it. `IPlaybackService`
(scoped) holds current track/position/volume/playing state; every `Play()` call implicitly stops
whatever was previously playing since it's the same `<audio>` element. This is the fix for a real
bug in the old WPF app, where the main window's track-preview player and the Sift window's player
were two independent `MediaPlayer` instances that could both be driving audio at once. Both the
triage screen's preview (Alt+P) and Sift's playback go through this same service now.

`audioPlayer.js` drives the seek bar client-side: `timeupdate` updates the bar's value unless a
drag is in progress (tracked via `pointerdown`/`pointerup`), and dragging sets `audioEl.currentTime`
directly in JS with no server round-trip. This is deliberate — binding the bar's `value` to
server-side `Playback.Position` would fight the user's drag on every re-render.

### Background prefetch

`PrefetchService` is both a singleton (so `TriageService` can enqueue jobs and read the cache
directly) and a hosted service (`AddHostedService(sp => sp.GetRequiredService<PrefetchService>())`
— note it resolves the *same* singleton instance rather than creating a second one). It queues
"unimported release files per queue record" and "artist release tracks per matched artist" jobs
onto a `Channel<T>`, processed with a concurrency-2 throttle. `Import.razor` and `Home.razor` fire
a refresh on arrival when `TriageService.NeedsRefresh` (see below), and there's a manual "Refresh"
button instead of separate load buttons.

**Freshness / "new session" rule.** `TriageService` and `PrefetchService` are singletons that
used to keep their data for the life of the process — a page left overnight showed yesterday's
queue. Now `TriageService.LastLoadedUtc` + `Core/Helpers/RefreshPolicy` decide: data is stale if
never loaded, invalidated by a new sign-in (`Login.razor` calls `Triage.MarkSessionStale()`), or
older than 6 hours (`RefreshPolicy.DefaultMaxAge`). `RefreshAsync` is the single "get current data"
path: it calls `PrefetchService.Reset()` (drops every cache; a generation counter stops a fetch
that was already in flight from writing a stale result back), fetches queue + artists, **runs
auto-match itself** (there is no separate Auto Match button/Alt+3 any more), then queues
prefetches. It's guarded so Home and Import both calling it on arrival don't double-run. An empty
artist track list is deliberately **not cached** — a just-created artist has no albums until
Lidarr's own metadata refresh finishes.

**Gotcha already hit once:** each background job needs its own fresh `LidarrHelper` (i.e.
`new LidarrHelper()`), not a shared `HttpClient` passed into multiple `LidarrHelper` instances —
`LidarrHelper`'s constructor sets `_client.Timeout`, and .NET throws `InvalidOperationException` if
you try to do that on an `HttpClient` that's already sent a request. This bit the first
implementation and produced ~130 identical background-job failures before being caught via the log.

### Import page: keyboard flow, auto-advance, multi-select

- **Shortcuts**: `M` (Mark Match), `X` (Delete), `U` (Unlink) work bare *and* as Alt+key; Alt+1
  Refresh, Alt+A Match Artist, Alt+P play/stop. Bare keys are ignored on key-repeat and while focus
  is in a *text-entry* field (`isTypingTarget` in `keyboardShortcuts.js`) — **not** for
  checkboxes/radios/buttons: a checkbox keeps focus right after you tick it for multi-select, and
  treating it as "typing" silently killed M/X/U at exactly the moment they're wanted (real bug).
  Alt combos match the physical key (`e.code`), since `e.key` differs under Alt on some
  platforms/layouts (macOS Alt+M = 'µ'). `Import.OnShortcut` ignores everything while the page is
  busy or the match dialog is open.
- **Global page lock is now minimal.** `.triage-page.busy` (opacity + `pointer-events:none`, driven
  by `StatusService.IsBusy`) only applies to Refresh, AI Match and Process Actions/import.
  **Selecting a release does not lock**: `OnQueueRecordSelectedAsync` highlights the record and
  clears the lists at once, shows "Loading files..." / "Loading releases..." rows
  (`IsLoadingSelection`), and a `_selectionVersion` counter makes the last click win if loads
  overlap (a slow Lidarr filesystem scan for an uncached release used to freeze the whole page).
  `LoadArtistReleasesAsync` clears+refills the tracks collection in one synchronous step *after*
  its await, so overlapping loads can't leave duplicate rows. Don't reintroduce `RunBusyAsync`
  around anything the user might want to click past.
- **Auto-advance** (`TriageService.AdvanceAfterActionAsync`, called by the page after Mark Match /
  Unlink / Delete / Move): selects the next file that still has no action (next after the one acted
  on, else the first skipped one), re-sorts the tracks list for it; only when every file in the
  release has an action does it move on to the **next queue record**. (Mark Match used to
  auto-create Unlink rows for the matched file's folder siblings, which made a match jump straight
  to the next artist — removed; see the next bullet.) Pure index maths is
  `Core/Helpers/FileSelection.NextUnhandledIndex` (tested).
- **"No action = Unlink" at processing time** (`TriageService.AddImplicitUnlinksAsync`, rules in
  `Core/Helpers/ImplicitUnlink`, tested). **Why: Lidarr deletes whatever is left unmatched in a
  folder once it imports from it**, so every file the user left without an action must be moved out
  first (Unlink moves it to the import root). Scope is only releases with ≥1 *Import* action
  (that's when Lidarr cleans up; a lone Delete/Move in a release doesn't put its other files at
  risk, and unlinking those would surprise people). File lists come from the prefetch cache
  (`GetOrFetchQueueRecordFilesAsync`), files that no longer exist on disk are skipped (a previous
  run already moved them), and the synthetic rows are flagged `IsImplicitUnlink`, visible in Actions
  to Take, and **rebuilt from scratch on every run** (so a release that lost its Import via Unselect
  doesn't keep them). If the queue record or its file list can't be obtained the whole run is
  refused with a message rather than importing anyway — importing would let Lidarr delete the
  unaccounted-for files. The Import page dims (italic, tooltip) files that will be auto-unlinked.
  Live-verified only up to the backup step (dev instance with an unwritable backup folder so no real
  file moved); the actual moves reuse the existing Unlink path.
- **Multi-select on the files list**: plain click selects; Shift-click checks the range from the
  last anchor (`FileSelection.RangeBetween`); Ctrl/Cmd-click toggles one. "Checked" files are what
  Delete/Unlink/Move act on. Cells are `user-select:none` so shift-click doesn't highlight text.

### Non-blocking manual artist match

`ApplyManualMatch` (TriageService) does **not** take the global busy lock. `ManualMatchDialog`
closes immediately (for an artist not yet in Lidarr it hands back a placeholder `LidarrArtist` with
`Id = 0` + the foreign id); the record is marked matched at once, and a `Task.Run` job creates the
artist if needed, then polls (`Core/Helpers/Polling.UntilAsync`, 24×5s) until Lidarr's async
metadata refresh has produced albums/tracks. Progress: `IsArtistPending`/`PendingArtistMessage`
drive a ⏳ in the queue table and a note in the tracks table; `ArtistJobsChanged` /
`ArtistReleasesReady` events fire **from the background thread**, so the page marshals with
`InvokeAsync` before touching `ArtistReleaseTracks` (an `ObservableCollection` bound to render).
`Artists` is swapped copy-on-write for the same reason. On failure the record's previous match is
restored. `Import.OnInitialized` reloads the tracks if a job finished while no page was mounted.
Live-verified against a real Lidarr with an *existing* artist (a 16s, 8,416-track fetch); the
create-a-new-artist branch is unit-covered only via `Polling` — it was not exercised live because
it mutates the real Lidarr.

### Import page layout

On desktop-sized viewports (`min-width:641px` and `min-height:620px`) `.triage-page` is a
viewport-bounded flex column (`height: calc(100dvh - 1.1rem - 5.5rem)`; queue 3 : files+tracks 6 :
actions 2), every list `flex:1; min-height:0; overflow:auto`, so lists grow with the window. Below
that the older fixed `max-height`s apply and the page scrolls. The controls column spans both grid
rows in bounded mode (otherwise the Move buttons need their own scrollbar on a laptop).

### Status bar, cover-art hand-off, cookie

- `StatusService` (now in **Core**, `Core/Models`, so its timing is unit-tested) auto-dismisses
  Info/Error after `AutoDismissAfter` (2 min) using a version counter so an old message's timer
  can't wipe a newer one; Busy is never auto-dismissed. Info messages also get a ✕ now.
- Cover art: **Save & Finish** (saving the last missing item) and **Finish Import** both call
  `CoverArt.Complete()`, which starts `ResumeImportAfterCoverArtAsync` fire-and-forget and
  navigates to `/import` immediately — the busy state + status-bar summary carry progress. The old
  "N files need cover art…" status message is gone (the page itself is the message), and the
  status-bar cover-art link hides while you're on `/cover-art`.
- The login cookie is `IsPersistent = true` (`Login.razor`). Without it ASP.NET issues a *session*
  cookie regardless of `ExpireTimeSpan`, which is why the password kept being re-asked. Confirmed
  live: cookie expiry is ~30 days out. (If it still re-prompts on TrueNAS, check `/data/keys`
  really persists — see Docker section.)
- Web image search: grid uses SerpApi's small `thumbnail` (`SerpApiHelper.ChooseImageUrls`), the
  full `original` is used only for the selected preview/download; 20 shown at a time with
  "Show more" revealing the rest of the already-fetched (single-credit) results. Remaining latency
  is SerpApi/Google itself (~10-20s uncached) and can't be fixed on our side.

### Cover-art save and MP3s TagLib can't open

`FileAndAudioService.SaveCoverArt` used to swallow every exception, so the user just saw "Failed to
save cover art to '<path>'". Reproduced with the real file: it was a valid MP3 (ffprobe fine) with
~38 KB of zero padding between the ID3v2 tag and the first MPEG frame; TagLib only searches a short
distance for audio and throws `CorruptFileException: MPEG audio header not found` — so it can't
open *or* write the file. `Id3PaddingRepair` grows the ID3 tag's size field to cover the padding
(4-byte in-place edit; audio and tag content untouched) and the save is retried once.
`TrySaveCoverArt` now returns the reason, which the gate puts in the error. Note
`ExtractCoverArt`/`ExtractMetadata` still just return empty for such a file (read-only; they never
repair), so artist/album can be blank on the cover-art page for them. To debug a tag failure, don't
guess: copy the file to scratch and drive TagLib directly (a throwaway console project referencing
`LidarrCompanion.Core` works).

## Blazor-specific gotchas actually hit in this codebase

These cost real debugging time. Read before touching render logic.

- **A fire-and-forget async call from `OnInitialized()` (not a direct `@onclick`-bound handler)
  does not automatically trigger a re-render when it completes.** Blazor's "re-render after the
  event handler's task completes" behavior only applies to methods invoked through the normal
  component event-binding pipeline. `CoverArt.razor`'s opt-in auto-search is kicked off from
  `LoadItem()` (`_ = SearchManual()`, reached from `OnInitialized()` via
  `SelectFirstMissingOrFirst()`), so `SearchManual`'s `finally` needs an explicit
  `await InvokeAsync(StateHasChanged)` — without it, the fields update correctly (confirmable via
  server logs) but the page visually never changes, which looks exactly like a hung network call
  and isn't one.
- **`@rendermode InteractiveServer` prerenders by default**, which runs `OnInitialized` twice per
  page visit — once for a throwaway static prerender, once for the real interactive circuit. For
  a page whose `OnInitialized` has side effects (an outbound API call, in `CoverArt.razor`'s case),
  this silently doubles the effect and can trip an external rate limit. Use
  `@rendermode @(new InteractiveServerRenderMode(prerender: false))` for any page whose
  initialization isn't idempotent or that depends on JS interop / live singleton service state
  anyway (which is most pages in this app).
- **`display: block` set directly on a `<table>` element breaks `table-layout`/percentage-width
  CSS on that element entirely** — the browser generates a separate anonymous table box for actual
  cell layout that the real `<table>` element's CSS can't reach. If a table needs its own scroll
  container, wrap it in a `<div class="table-scroll">` instead of setting `display:block` on the
  table itself.
- **A flex item's default `min-width: auto`** is computed from its content's intrinsic minimum
  width, which for a table with `white-space: nowrap` cells is the longest unbreakable string —
  this silently overrides percentage/fixed widths and causes page-wide horizontal overflow. Set
  `min-width: 0` on the flex item. This bug existed in two places at once: custom triage-table CSS,
  *and* the default Blazor project template's own `MainLayout.razor.css` (`main { flex: 1; }` was
  missing it) — check the template's own layout CSS, not just your own additions, when chasing
  unexplained horizontal overflow.
- Settings persist as `SettingKey.ToString()` → JSON key in `data/appsettings.json`. Renaming a
  `SettingKey` enum member silently resets that setting to default for existing installs unless you
  also migrate the JSON file's keys. A brand-new key just defaults (no migration needed). The
  Settings page auto-generates its grid from the `SettingKey` enum's `[Setting(...)]` attributes,
  so a new setting needs no page markup.
- **`StatusService.IsBusy` is one global singleton flag, and `.triage-page.busy` sets
  `pointer-events: none` on the whole page.** Anything that leaves it true — or a page that
  doesn't re-render when it clears — leaves every click on the page silently dead (no error,
  nothing in the logs). A page that renders `Triage.IsBusy` must subscribe to `Status.Changed` and
  `InvokeAsync(StateHasChanged)` (see `Import.razor`; `StatusBar.razor` is the reference pattern),
  unsubscribing in `Dispose`. Also: hammering the app with many concurrent test tabs overlaps busy
  operations on this shared flag and makes it look far worse than it is for a single real user.
- **The global auth `FallbackPolicy` gates static assets too.** `MapStaticAssets()` needs
  `.AllowAnonymous()` or the login page's own CSS is redirected to `/login` (unstyled login page
  for everyone without a cookie). Known residual gap: `_framework/blazor.web.js` and the SignalR
  negotiate endpoint are registered separately and are still gated pre-login — harmless because
  the login form is a plain HTML POST that needs no circuit.
- **A dead Blazor circuit (container restarted under an open tab) looks like a frozen page**:
  stale text, dead clicks, no error. The default `ReconnectModal.razor.js` only retries on
  `visibilitychange`, which never fires for a tab that stayed focused; ours adds an 8s
  `location.reload()` fallback in the `failed` state.
- **`display: flex` column parents stretch buttons to full width** — `.cover-art-page button` sets
  `align-self: flex-start` for that reason. The cover-art pages are viewport-bounded
  (`height: calc(100vh - 5.5rem)`) with only `.cover-art-results-frame` flexing/scrolling; every
  other section is `flex-shrink: 0`.

## DI registration reference (`LidarrCompanion.Web/Program.cs`)

| Service | Lifetime | Why |
|---|---|---|
| `ILogService` | Singleton | One log stream app-wide |
| `StatusService` | Singleton | Injected into other singletons (`TriageService`, `SiftService`); lives in `Core/Models` |
| `ThemeService` | Singleton | |
| `IPlaybackService` | **Scoped** | Not needed by any minimal API endpoint |
| `CoverArtGateService` | Singleton | Triage page and `/cover-art` page must see the same gate state |
| `PrefetchService` | Singleton **and** HostedService | Singleton for DI injection, HostedService so ASP.NET Core actually runs its `ExecuteAsync` loop — registered as `AddHostedService(sp => sp.GetRequiredService<PrefetchService>())`, not a second instance |
| `TriageService` | Singleton | `/audio/stream/{id}` needs the same instance the page is using |
| `SiftService` | Singleton | `/audio/sift-stream/{id}` / `/audio/sift-cover/{id}` same reasoning |

Auth is a single shared-password cookie gate (`AddCookie`, fallback policy requires auth on
everything except `[AllowAnonymous]` — just `/login`). Settings/state persist as flat JSON on a
mounted volume (`data/appsettings.json`, `data/logs/`), not a database — see `AppSettings` in Core.

## Testing

`LidarrCompanion.Core.Tests` (xUnit) is the only test project. Run it with:

```
dotnet test LidarrCompanion.Core.Tests
```

**What's realistically unit-testable here, and where:**

- Pure logic with no I/O — string/path helpers, query builders, message formatters — belongs in
  `LidarrCompanion.Core` and gets a direct test. If the logic currently lives in
  `LidarrCompanion.Web` but has no Blazor/ASP.NET dependency (no injected services, no HTTP
  context), consider **moving it to `Core`** rather than standing up a second test project —
  that's what happened with `ImportSummary` (was in `TriageService.cs`, moved to
  `Core/Models/ImportSummary.cs` specifically so its message-formatting logic — the exact thing
  that had a real double-counting bug — could be tested directly).
- A helper method that's genuinely private but pure (no network call inside it) can be made
  `internal` and exercised directly via the `InternalsVisibleTo` grant in
  `LidarrCompanion.Core.csproj` (currently grants `LidarrCompanion.Core.Tests`) — see
  `MusicBrainzHelper.BuildReleaseGroupQuery`/`SanitizeForQuery` and
  `SerpApiHelper.IsFetchableUrl`. Don't make something public just to test it if `internal` +
  `InternalsVisibleTo` gets you there with a smaller API surface.
- What's genuinely **not** covered and would need real infrastructure investment (mocked
  `HttpClient`, a fake filesystem, or a `LidarrCompanion.Web.Tests` project with a `WebApplicationFactory`)
  to test properly: `LidarrHelper`'s actual HTTP calls, `TriageService`/`ImportRunner`'s
  orchestration logic (heavy `AppSettings`/file-system/Lidarr-API coupling), `PrefetchService`'s
  background job scheduling, anything Blazor-component-rendering-specific (the `StateHasChanged`
  class of bugs above). These get caught by live testing (see below), not unit tests — don't feel
  obligated to force a unit test where the honest cost is a fake HTTP stack for little payoff.
- `AppSettings`/`Logger` are static singletons ported as-is from the WPF app (same call
  signatures on purpose). Tests that touch them **cannot run in parallel** with each other — see
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]` in
  `LidarrCompanion.Core.Tests/AssemblyInfo.cs`. Don't remove that attribute without accounting for
  the race it prevents.

- Timing-based tests (`StatusServiceTests`, `PollingTests`) use millisecond delays and assert on
  "did/didn't clear", never on speed. Pure Web-layer logic gets moved into Core to be testable:
  `RefreshPolicy`, `FileSelection`, `Polling`, `StatusService`.

**Live/manual verification**, when a change touches Blazor rendering, JS interop, or an actual
external API (MusicBrainz, SerpApi, Lidarr): run the dev server (`dotnet run --project
LidarrCompanion.Web --urls http://127.0.0.1:5299`) and drive it with a real browser. This has
caught real bugs unit tests wouldn't (the `StateHasChanged`/prerender issues above were both found
this way, not by a failing test). If using CDP/browser automation for this, **close the tab(s) you
open** — leaving dozens of stale tabs open across a debugging session makes the browser's tab list
useless for reasoning about what's actually live and wastes resources.

## Docker

Phase 7 of the migration plan is done: `Dockerfile` (multi-stage, `sdk:10.0` build →
`aspnet:10.0` runtime) and `docker-compose.yml` at the repo root, verified locally on this dev
machine (image build, container start, settings load correctly, host-mounted paths readable
*and* writable from inside the container, login sessions and settings both survive a container
restart) before ever touching the real TrueNAS target.

- **CI publishes the image to GHCR on every push to `master`** (`.github/workflows/build.yml`'s
  `docker-build` job) — tagged both `:latest` and `:<commit-sha>`, using the workflow's own
  built-in `GITHUB_TOKEN` via `docker/login-action`, so no separate registry credential needs
  creating or rotating. Gated to `github.event_name == 'push'` specifically so a pull-request
  build (which runs the same job to validate the Dockerfile still builds) never overwrites
  `:latest` with unreviewed code. **One manual, one-time step this can't do via CI**: a package
  first published via `GITHUB_TOKEN` is private by default — pulling it from the TrueNAS box (or
  anywhere outside this repo's own Actions runs) needs the package's visibility changed to public
  from the repo's Packages tab on GitHub, or a PAT configured on the pulling side otherwise.

- **Build context is `LidarrCompanion.Core` + `LidarrCompanion.Web` only** — the old WPF project,
  its root-level `Helpers`/`Models`/`Services`, and `LidarrCompanion.Core.Tests` are excluded via
  `.dockerignore`, both for build-context size and so the WPF project's presence can never
  accidentally affect the container image.
- **The real `data/appsettings.json` (Lidarr API key, admin password hash, SerpApi key) is
  explicitly excluded from the build context** (`.dockerignore`) — it must never end up baked
  into an image layer. The container gets its own copy via the `/data` volume mount
  (`./docker-data:/data` in `docker-compose.yml`, itself gitignored the same way
  `LidarrCompanion.Web/data/` already was).
- **Runs as root inside the container** (`USER root` in the Dockerfile, overriding the base
  image's non-root default). Deliberate, not an oversight: this app needs read/write access to
  whatever host paths get bind-mounted for the music library/import/backup folders, and those are
  owned by whatever user already manages them on the NAS host, not a UID this image controls. This
  is a single-user, LAN-only, password-gated tool (see `AdminPasswordHash`), not a multi-tenant
  service, so the usual "don't run containers as root" tradeoff leans the other way here. If a
  future deployment wants a specific host UID/GID instead, add that mapping explicitly (compose
  `user:` or an entrypoint script) rather than assuming the base image's default non-root user
  will have the right permissions on arbitrary NAS mounts.
- **`docker-compose.yml`'s volume list beyond `/data` bind-mounts this dev machine's real paths**
  (`/mnt/Music`, `/mnt/lidarr-backup`, `/home/davepike/Nextcloud`) so local verification exercises
  the exact paths already configured in Settings without changing any path settings. **These are
  dev-machine-specific and must be repointed at the equivalent TrueNAS paths before deploying
  there** — the path-mapping settings (`ImportPathCompanion` etc., see above) exist precisely to
  absorb a mismatch between this container's mount layout and Lidarr's, so getting the exact
  TrueNAS dataset paths right isn't blocking, but the bind-mount sources in the compose file still
  need to point at *something real* on whatever host is actually running the container.
- **Data Protection keys are explicitly persisted to `/data/keys`**
  (`builder.Services.AddDataProtection().PersistKeysToFileSystem(...)` in `Program.cs`) — found
  during Docker verification, not anticipated up front. Without this, ASP.NET Core generates a
  fresh key-encryption key on the container's ephemeral filesystem on every start, which silently
  invalidates every existing login cookie on every restart/redeploy — directly undermining the
  30-day sliding-expiration cookie the auth setup is supposed to provide. Confirmed fixed by
  checking the key file's name is unchanged across a `docker compose restart`.
- Kestrel listens on `:8080` inside the container (`ASPNETCORE_URLS=http://+:8080`, matching the
  modern ASP.NET Core container convention), mapped to host port `5299` in
  `docker-compose.yml` — change the host side freely, the container side has no reason to change.
- **Never split the Dockerfile into `dotnet restore` (csproj-only layer) + `publish --no-restore`.**
  It looks like the standard cache optimization but silently produced an image whose static web
  assets manifest was missing `blazor.web.js` — no build error, just a 404 at runtime and a page
  where *nothing* is clickable. Verify any Dockerfile change with
  `docker exec <c> grep -o 'blazor\.web[^"]*\.js' /app/LidarrCompanion.Web.staticwebassets.endpoints.json`
  (should list `blazor.web.js`), and use `docker compose build --no-cache` when checking.
- **Deploying to TrueNAS (custom YAML) — the two mistakes already made once:** the port mapping's
  container side must be `8080` (`'3010:8080'`, not `'3010:3010'`), and a `/data` volume is
  mandatory (settings, admin password hash, login keys and logs all live there; without it they
  reset on every restart). A fresh `/data` means Settings starts empty — the path-mapping settings
  (`ImportPathCompanion`, `LibraryPathCompanion`, `BackupRootFolder`, destinations) must be set to
  the *container-side* mount paths chosen in that YAML. Their compose uses `/mnt/music` and
  `/mnt/backups`, lowercase and different from the dev machine's `/mnt/Music`.
- **Bind-mounting a network (CIFS) path that isn't in `/etc/fstab`**: after a host reboot Docker
  starts the container before the share is mounted, so the container binds an empty directory and
  every file is "not found" (backup step fails first). Even after the host remounts, the container
  keeps the stale empty view — `docker compose restart` fixes it. (`/mnt/Music` on the dev machine
  is exactly this case.) Hit again in practice: the log showed `File not found:
  '/mnt/Music/1ToClean/...'` on every retry while the host was fine; `docker exec <c> mount | grep
  Music` showed the *container's own ext4* instead of cifs. **A restart loses all in-memory state
  (proposed actions, selection)** — nothing about proposals is persisted, so redo the matches after.
  Rebuilding/recreating the container (`docker compose up -d`) re-binds too, but binds whatever the
  host has mounted *at that moment* — check `mount | grep cifs` on the host first.
  Propagation flags (`rslave`) don't help because the share is mounted *over* the bound directory
  rather than inside it. (An in-app hint for this was added and then removed at the owner's
  request — see the no-hints standing instruction.)
- **`/cover-art-test`** is a hidden dev harness (no nav link, route still live) working against
  copies in `/mnt/Music/.cover-art-test` (`TestFolder` constant) with no dependency on the import
  pipeline. Its search/preview/drag code is a *parallel copy* of `CoverArt.razor`'s, not shared —
  keep the two in sync if either changes, or delete it once it's no longer useful.
- `UseHttpsRedirection()` in `Program.cs` is intentionally left unconditional (not gated to
  Development) — with no HTTPS port configured (true both in local dev and in this Docker setup),
  the middleware just logs "Failed to determine the https port for redirect" and passes the
  request through unredirected. It's a no-op here by construction, not a bug to "fix" by wiring up
  certs — this app is meant to be reached over plain HTTP on a trusted LAN.

**Dev-server gotchas when live-testing** (each cost time):
- **`http://127.0.0.1:5299` is the Docker container, not the working tree.** It runs whatever image
  `docker compose build` last produced; code changes are invisible there until
  `docker compose build && docker compose up -d` (settings/login keys persist in `./docker-data`).
  "I don't see any changes" almost always means this — check with
  `docker exec lidarrcompanion grep -c <some-new-string> /app/wwwroot/js/keyboardShortcuts.js`
  or `docker ps` (RunningFor). Live-test on 5390 with the isolated data dir below instead.
- Singleton state (`TriageService` etc.) survives across test runs within one dev-server process, so
  a script that dies half-way leaves proposals/selection behind and the next run's numbers are
  meaningless — restart the server before every scripted run.
- Run against an isolated data dir so the real `LidarrCompanion.Web/data/appsettings.json` is never
  touched: copy it elsewhere, drop `AdminPasswordHash` (the login page then offers "set password"),
  and start with `DataDirectory=<dir> dotnet run --no-build --urls http://127.0.0.1:5390`. Port 5299
  is the Docker container's; the real Lidarr *is* reachable from the dev machine, so anything that
  mutates it (creating an artist, Process Actions) must not be driven from a test.
- Don't `pkill -f -- "--urls http://127.0.0.1:5390"` from a Bash tool call — the pattern is in the
  shell's own command line and kills it (exit 144). Find the PID with `ss -ltnp | grep :5390`.
- No node/pip on this machine: headless `google-chrome --remote-debugging-port=9222` plus a
  hand-rolled stdlib-Python CDP websocket client worked fine (real mouse events via
  `Input.dispatchMouseEvent` with `modifiers=8` for shift-click, key events for shortcuts).
