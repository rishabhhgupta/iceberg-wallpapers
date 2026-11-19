# Mobutu — Lively + Moment (working plan)

> Working name only. The Store name is yours to choose (see "Store identity").

This file is the record of what was decided, what is proven working, and what
comes next. It is written to be readable without reading code.

---

## 1. What is proven working right now

| Thing | State |
|---|---|
| Lively builds on this PC | **Yes** — 0 errors, 508 pre-existing warnings |
| A Store package (.msix) builds | **Yes** — 353.9 MB, produced by script |
| Build is repeatable | **Yes** — one command, see below |
| Phase 1 logic core + tests | **53 tests, all passing** (solar, day phases, weather, rotation, favourites) |
| Disk space | 7.03 GB free → **9.96 GB free** |
| .NET 9 SDK | Installed at `C:\Users\DumperTag\.dotnet` |

### The two commands that matter

```powershell
# Build a Store package
.\lively-core-separation\lively-core-separation\scripts\build-msix.ps1 -Dotnet "$env:USERPROFILE\.dotnet\dotnet.exe"

# Set your own Store identity (once, before first submission)
.\lively-core-separation\lively-core-separation\scripts\set-store-identity.ps1 `
    -IdentityName   '12345YourName.YourApp' `
    -Publisher      'CN=00000000-0000-0000-0000-000000000000' `
    -DisplayName    'Your App Name' `
    -PublisherDisplayName 'Your Name'
```

### Two things that were broken on this PC and are now fixed

1. **NuGet had no package sources.** Your `%APPDATA%\NuGet\NuGet.Config` was an
   empty `<configuration />`, so *no* C# project on this machine could download
   packages. Added `nuget.org`. This is a machine-wide fix.
2. **No .NET SDK.** Only the runtime was installed. Installed SDK 9.0.320 into
   your user folder (no admin needed).

---

## 2. Decisions that are locked

| Area | Decision |
|---|---|
| Shape | Fork Lively. One app, one window, one installer. |
| Removed | Lively **Gallery** (nav, login, account, subscription). **Quotes** everywhere. |
| Nav | **Home** · **Library** · **Favourites** · **Settings** |
| Sources | Every Moment provider that works, plus Local and Favourites |
| Browsing | Infinite scroll with dynamic loading from the internet |
| Rotation | Timer-based, survives sleep and restart |
| Time/weather | Day / afternoon / night detection + weather from IP. Reuses the logic Nagomi already has |
| What time/weather changes | **Both** which wallpapers are chosen **and** the app's look |
| Theme | Lively's existing light/dark/system, plus "match colours to current wallpaper" |
| Favourites | Anything starrable; stored as a plain file |
| Muzei | Rebuilt as a "Muzei-style art" source (Muzei itself is Android-only) |
| Image filters | None (blur/greyscale/etc. skipped) |
| Image loading | SkiaSharp decoding + a Glide-style loader (cache, cancel, placeholder) |
| Testing | Code-level only. No screenshot-based pass/fail. |
| Store | **No store in v1**, but the app stays MSIX-packaged and designed so a store can be added without rewriting screens |

---

## 3. Things you must know (not opinions)

### 3.1 The Store identity is still the original author's

`Package.appxmanifest` currently says:

```
Name              12030rocksdanister.LivelyWallpaper
Publisher         CN=D80CB9E2-21E6-4D9B-8533-660C768F3C5B
DisplayName       Lively Wallpaper
PublisherDisplayName  rocksdanister
```

You **cannot** submit under those. You also cannot use the name
"Lively Wallpaper" — it is already taken on the Store by the original author,
and the name is his brand. Your fork needs its own name. The
`set-store-identity.ps1` script exists to make this a one-line change.

### 3.2 Licence

Lively is GPL-3. Selling it is allowed. But if you distribute the modified app,
you must publish the modified source. Since you plan to open-source anyway this
is fine — just do not later decide to close the source.

### 3.3 A security warning surfaced in the build

`Magick.NET-Q8-AnyCPU 14.9.1` is flagged with **one high-severity, several
moderate and several low** known vulnerabilities. It is used by Lively's image
processing. This needs an upgrade decision before submission; Store review can
reject on this kind of report.

### 3.4 Paid marketplace reality (parked, but recorded)

Microsoft Store purchases only sell add-ons **you** create in Partner Center.
There is no way for a user to upload a wallpaper, set a price, and be paid
through the Store. A user-seller marketplace needs Stripe + payouts + seller
identity checks. That is a separate product, deferred.

---

## 4. How the new code will be organised

Four new pieces, each testable without a screen:

```
src/Lively/
  Lively.Sources/            NEW  - wallpaper providers (Wallhaven, Unsplash, ...)
  Lively.Discovery/          NEW  - rotation engine, day/night, weather, queue
  Lively.Favourites/         NEW  - favourites storage (plain JSON file)
  Lively.Imaging/            NEW  - SkiaSharp loader: memory + disk cache, cancel
  Lively.UI.WinUI/           EDIT - new pages, Gallery removed
  Lively.UI.Shared/          EDIT - view models for the new pages
```

Why separate projects: each one can be unit-tested with no window, no network,
and no Graphics. That is the whole testing strategy.

### The rotation engine (most important logic)

Inputs → decision → result:

```
now, timezone, coordinates, weather, enabled sources, favourites, history
        ↓
   candidate pool  →  filter by time of day  →  filter by weather
        ↓
   avoid repeating recent  →  pick next  →  apply wallpaper
```

Every branch is a pure function, so every edge case can be tested:
midnight rollover, DST change, sleep across a scheduled change, weather API
down, no network, empty source, all sources failing, wallpaper deleted.

---

## 5. Delivery in phases

### Phase 0 — Foundation ✅ done
SDK, NuGet source, baseline build, MSIX packaging, identity script.

### Phase 1 — Test harness + domain core (partly done)
Built and tested in `src\Lively\Lively.Discovery`:
- `Solar\SolarCalculator.cs` — sunrise/sunset, ported from Nagomi's tested Meeus model
- `Time\DayPhase.cs` — Night / Dawn / Morning / Afternoon / GoldenHour, with a wall-clock fallback
- `Weather\WeatherCode.cs` — WMO code → weather kind, unknown stays unknown
- `Rotation\RotationClock.cs` — next change, sleep-safe, missed-window reporting
- `Rotation\RotationPlanner.cs` — seeded picker that avoids repeats
- `Favourites\FavouriteStore.cs` — atomic JSON store, corrupt files quarantined

53 unit tests pass. Still to do in this phase: the scripted "virtual user" console harness.

### Phase 2 — Providers
- Port Moment's provider list, starting with keyless ones
- Paging, cancellation, rate-limit handling, error states
- Recorded fixture responses so tests never depend on the live internet

### Phase 3 — UI
- Home feed: virtualised grid, fast-scroll correctness, placeholders
- Favourites page
- Settings: Appearance, Wallpaper rotation, Sources, Cache
- Remove Gallery everywhere

### Phase 4 — Wiring
- Rotation actually changes the desktop wallpaper
- Day/night + weather driver
- Match-colours-to-wallpaper
- Notification and recovery when a source fails

### Phase 5 — Production
- Store assets, privacy policy, age rating
- Accessibility, keyboard navigation, high-DPI
- Full offline behaviour
- MSIX submission dry run

---

## 6. Testing approach (your requirement)

No screenshots for pass/fail. Instead:

1. **Unit tests** (xUnit) for every decision: which wallpaper, why, when next.
2. **A virtual environment harness** — a console app that plays a scripted user:
   "open Home, scroll fast, star an item, kill the network, resume, wait for the
   timer" — and prints PASS/FAIL per step with the values it checked.
3. **Fixture-based provider tests** — recorded JSON, so tests are deterministic
   and free.
4. **Build verification** — compile + package must stay green.

Screenshots will only ever be used if *you* ask to look at a design.

---

## 7. Open items I need from you

1. **Your app name** (Store name cannot be "Lively Wallpaper").
2. **Partner Center identity values** when you have them (the four fields the
   identity script takes).
3. **API keys** for providers that need them. I will start with keyless ones:
   Wallhaven, Wikimedia, Openverse, NASA, Reddit.
4. **Decision on Magick.NET** upgrade (security).
5. **Confirm disk budget** — builds now use ~2.6 GB. At 10 GB free, three more
   full rebuild cycles is the comfortable limit before something must be cleaned.

---

## 8. Honest status

The infrastructure is real and proven: the app compiles, a Store package builds
from a script, and the first logic layer has 53 passing tests.

**No screen has been built yet.** The Home feed, Favourites page and the new
Settings groups do not exist. That is the next chunk of work, and it is the
largest single piece left.

What exists today in new code is deliberately the part that is hardest to get
right and easiest to test: when to change wallpaper, what counts as "night",
how weather overrides the clock, and how favourites survive a crash.

The one thing that would speed everything up: your app name and Partner Center
identity, because every later build has to be packaged under them.

---

## 9. Status update — 27 October 2026

### Now working (code + tests)

| Area | What it does | Tests |
| --- | --- | --- |
| `Lively.Discovery` | Sunrise/sunset, day phases, weather codes, colour palettes, rotation planner, favourites file, settings file, atmosphere service | 105 |
| `Lively.Sources` | Wallhaven, Unsplash, Pixabay, Openverse, NASA + merged feed with per-source failure handling | 24 |
| `Lively.Imaging` | Memory + disk image cache, download de-duplication, SkiaSharp thumbnails, dominant-colour extraction | 21 |

**150 tests, 0 failures.** The Store package builds from `scripts/build-msix.ps1`
(371.4 MB, 0 errors).

### Screens built

* **Home** — live feed from every enabled source, mixed together; endless scroll;
  search; "Surprise me"; tap a tile to set it; star to favourite.
* **Favourites** — saved wallpapers, tap to set, star to remove.
* **Settings** — one hub page with Look (time of day, weather, match wallpaper),
  Rotation (on/off, minutes, change now), Sources (per-provider switches),
  Storage (cache size, clear), and doors into Lively's own five pages.

The left navigation is now **Home · Library · Favourites · Settings**. The
Updates item was removed to match the four-screen navigation you asked for, and
Gallery is gone from the navigation; its screens remain in the source tree but
are unreachable.

The app's colours follow the time of day and the weather, and can follow the
current wallpaper's dominant colour. Rotation runs once a minute while Lively is
running (including in the tray) and re-reads the sky every five minutes.

### Not done yet

1. **Real Partner Center identity.** The publisher is still all zeros.
2. **Iceberg-specific API keys.** The keys currently in `DefaultApiKeys` belong
   to the Moment app and could be revoked at any time.
3. **Gallery source files.** Removed from navigation but not deleted, because
   other parts of Lively still reference those types. Deleting is a separate,
   riskier change.

---

## 10. Status update — 28 October 2026 (overnight run)

### The big one: setting a wallpaper now really works

This used to fail because Lively's normal route (open a preview, wait for a
player) could not work without a player installed. Iceberg now writes its own
Lively wallpaper package and tells the core to show it directly.

Proven on this machine:

* `library\<hash>\` contains `wallpaper.png` and `LivelyInfo.json`.
* The core log shows the image player starting.
* Windows' own `TranscodedWallpaper` file was rewritten at 23:43:02.
* "Surprise me" changed the desktop again at 00:27 (key
  `wallhaven:d8vvlo` -> `wallhaven:p2j7pe`).

### Players installed

Mpv, WebView2 and Watchdog were taken from the official Lively v2.2.1.0
installer (without installing it), stripped of debug files, and placed in
`Build\Plugins` (121.5 MB). The core log shows the watchdog and the image player
starting, and the wallpaper comes back after a core restart.

A copy now also lives in `artifacts\plugins`, so `build-msix.ps1` can put the
players back even after a clean build.

### Screens checked on the real display

* **Home** — "Good night", "Overcast · 24°C · Prayagraj", a plain-English source
  line, four columns of cards, hover hint "Set wallpaper", star that saves.
* **Favourites** — same dark palette, saved card shows, remove works.
* **Settings** — Look / Rotation / Sources / Storage, "Right now: ..." line,
  "30 min" label on the slider, and doors into Lively's five pages.
* **Library** — Lively's own page still lists the user's wallpapers, including
  Nagomi - Living Koi Pond.

All three Iceberg screens now share the time-of-day background, so moving
between them no longer flashes a different colour.

### Fixes made tonight

* Nav shows exactly Home · Library · Favourites · Settings.
* The "what's new" update popup can no longer appear; Lively's updater is inert
  in a packaged app.
* A single slow picture site no longer shows a red warning bar. The feed simply
  shows what worked; the bar is kept for "nothing loaded at all".
* "Wallpaper set · source" now disappears after four seconds and the weather
  line comes back. Before, it replaced the weather for good.
* Image downloads are limited to six at a time (picture sites rate-limit bursts).
* Thumbnails fall back to the full picture when a preview link is dead.
* Image requests now send a User-Agent; some sites refuse anonymous clients.
* The window opens at 1180x760 and shrinks to fit small screens (this PC is
  1366x768, so it now opens 1180x696 instead of running off the bottom).
* New iceberg icon, tile logos, splash screen and `appicon.ico`.

### The Store package was launched, not just built

The 412 MB package was registered on this PC (Developer Mode is on) and run:

* A single click on the app tile opens the window. Windows starts the core
  (`Build\Lively.exe`), which then starts the UI. Before tonight, a returning
  user got a core with no window; the core now opens the window when it was
  started from Explorer (Start menu/tile) and stays quiet when Windows starts
  it at log-in.
* The packaged app reads the same favourites, settings and wallpaper library
  as the unpackaged run.
* The imagery, the feed, the time-of-day palette and the window layout all
  render correctly from inside the package.
* Windows' own `TranscodedWallpaper` was rewritten at 00:55:40 by the packaged
  core, so the wallpaper engine is not blocked by packaging.

The core log still prints harmless `WorkerW` errors on start-up while restoring
the previous picture. Lively tries to host the picture inside the desktop
window first and falls back to Windows' wallpaper API, which succeeds.

### "Surprise me" is now resilient

If the chosen picture cannot be downloaded (Pixabay currently answers 429), the
app tries up to three other pictures from the same page before showing the
error. A rotation will no longer do nothing because one file was refused.

### Known external problem

Pixabay's picture server answers **HTTP 429 (too many requests)** for the
Moment API key used here. Search works; the pictures do not download. This is a
key/quota problem, not a code problem — a fresh Pixabay key should fix it. Until
then those cards show the placeholder and a small "broken picture" mark and the
rest of the feed is unaffected.

### Still open

1. **Real Partner Center publisher** (`CN=...`) before signing.
2. **Fresh API keys** for all providers (Pixabay still answers 429).

### Closing the loop (28 Oct, after 01:00)

* **Rotation proved for real.** Turned on in Settings at a 5-minute interval; it
  changed the wallpaper at 01:06:18 (Unsplash) and again at 01:14:04, each time
  writing a new `library\<hash>` package — the same path a manual apply uses.
* **Close means minimise while rotation is on.** Pressing X hides the window and
  keeps the process alive, so the timer keeps running. The tray icon restores it.
  A real log-off/restart still closes it cleanly (a `WM_QUERYENDSESSION` guard
  lets Windows shut the app down instead of swallowing the close).
* **Rotation survives a log-in.** Windows starts the core minimised at log-in
  when rotation is enabled; the core tells the UI to start minimised, so nothing
  pops up in the user's face but the wallpaper still changes. The trigger can
  only happen at a real log-in, so this is verified in code, not yet observed:
  the core checks the rotation setting, starts the UI with `--icebergMinimized`,
  and the UI defers a `Minimize()` (the same WinUIEx call the X button uses).
* **One tile click opens the window.** The core checks whether Explorer started
  it (parent process check) and asks the UI to show itself; the log-in/scheduled
  start stays quiet.
* **Tray restore brings the window forward**, not just back into existence
  (`SW_RESTORE`, then `Activate`, then `SetForegroundWindow`).
* **Branding finished:** the core's notification-area icon is the Iceberg icon
  too (backup of the original pair kept in the temp folder).
* **Store version reset to `1.0.0.0`** with automatic revision bumping turned
  off, so the submitted package has a clean version number.
* Still to do outside the code: paste the real publisher ID from Partner Center
  and replace the borrowed Moment API keys.

### Final package (02:25, 28 Oct)

`artifacts\msix\Lively.UI.WinUI_1.0.0.0_x64.msix` — **411.9 MB, 0 errors**.
Identity `com.reeshabh.icebergwallapaper`, version `1.0.0.0`, x64. It contains
the core, the three player plugins, the assets and the Iceberg icons.

Freshly extracted, re-registered and run:

* One click on the tile opens the window, already focused.
* The X button minimises it to the taskbar; the process stays alive and the
  rotation timer keeps running.
* `--showApp true` (the tray's "show" path) restores the window **and brings it
  to the front**.
* The notification area now shows the Iceberg mountain, not Lively's flower.
  (Two icons had to be replaced: the exe's file icon and the embedded tray
  icon. The tray one was cached by an earlier build and needed a forced
  rebuild of the resource.)

One testing note for later: launching the packaged `Lively.UI.WinUI.exe`
directly from the extracted folder crashes with "Class not registered". That is
Windows App SDK refusing to run without package identity, not an Iceberg bug.
Start the app from its tile, or start the core and let it start the UI.

---

## 11. Status update — 28 October 2026 (the six reported issues)

These are the six things you asked for after using the build. All six are in the
current package.

| # | Issue you reported | What the app does now |
| --- | --- | --- |
| 1 | Feed stays empty after the internet comes back | Home watches the network. When a connection returns, it waits three seconds (to avoid retrying on every flicker), clears the "this source failed" cooldown, and fills the feed again. No restart needed. |
| 2 | Filters like Moment's toplist / random | The **Filters** button opens a small dialog with **Sort**: Featured, Top rated, Newest, Random, and **Shape**: Landscape, Portrait, Any shape. Apply / Reset / Cancel. The choice is saved and survives restarts. Each provider maps these to its own options (Wallhaven top range and sorting, Unsplash order, Pixabay order, Openverse ordering). |
| 3 | Cannot view a wallpaper full size before setting it | Tapping a card now opens a full-size preview instead of setting it immediately. **Set desktop** and **Set lock screen** live in the preview, so you can look first and then choose. The star also works inside the preview. Favourites cards open the same preview. |
| 4 | Separate lock-screen and desktop wallpapers | The preview has two separate buttons. Settings also has a **Windows lock screen** section: set the lock screen to the last wallpaper used, or turn on **Change with rotation** so every rotation changes both screens. Setting the desktop never changes the lock screen unless you ask. |
| 5 | Greeting and weather block uses too much room | The large header block is gone. A single slim line at the bottom now shows the greeting, weather and city (for example "Good morning · Overcast · 23°C · Prayagraj"), and the right end of the same line shows "Sources: Wallhaven". The wallpaper grid gets the space back. |
| 6 | Multiple search boxes | The search box in the title bar is gone. Home keeps "Search wallpapers"; Library keeps its own search because it searches your saved wallpapers, not the internet. |

### The lock screen change also works after a restart

Earlier, "Use last wallpaper" only worked in the same session that had set a
wallpaper. It now reads the app's record of the last wallpaper and reuses the
copy already on disk, so it works after closing and reopening the app. Tested
both unpackaged and inside the Store package: the lock screen changed, and the
Settings summary changed to "Last set from Wallhaven."

### Final package

`artifacts\msix\Lively.UI.WinUI_1.0.0.0_x64.msix` — **411.9 MB, 0 errors**.

It was extracted fresh, registered on this PC and launched from its tile. In
the packaged app I checked:

* The title bar has no search box.
* Home has the search box, Filters and Surprise me; cards show resolution and
  source; the bottom line shows greeting, weather, city and sources.
* Tapping a card opens the full preview with **Set lock screen** and
  **Set desktop**.
* Settings "Use last wallpaper" changed the Windows lock screen successfully.
* Settings "Change with rotation" is On and persisted.

### Tests

**152 tests, 0 failures** (105 discovery, 26 sources, 21 imaging). The UI builds
with 0 errors. What is *not* machine-tested: the visual layout is checked by
looking at the real window, as before; and the network-reconnect path has not
been forced by physically unplugging the connection — it is code-reviewed and
uses Windows' own network event, with the existing "Try again" button as a
backup.

### Still outside the code

1. **Real Partner Center publisher** (`CN=...`) before signing and submission.
2. **Fresh API keys** — the current ones belong to Moment and may be revoked;
   Pixabay's picture server still answers 429 with this key.
3. **GPL-3** — selling is allowed, but the modified source must be published
   when the app is distributed.
4. **Magick.NET security warning** — decide on an upgrade before Store review.

---

## 12. Manual test round — 28 October 2026 (agreed, not yet built)

Twelve issues were reported after using the package. Each was checked against
the real code, then the intended behaviour was agreed. **Nothing below is
implemented yet.** Work starts only on the owner's word.

| # | Reported | Agreed behaviour |
| --- | --- | --- |
| 1 | White text on a white background in Settings; theme says Dark | **Remove the automatic tint entirely.** No screen paints its own background or text colour. The app follows the System / Light / Dark choice only. Time of day and weather no longer colour the app. "Match the wallpaper colours" is removed with it. |
| 2 | Sources can only be changed in Settings | A **Sources** button sits next to Filters on Home and opens a panel with a switch per site. The list is **removed from Settings** so there is one place. The bottom "Sources:" line becomes clickable and readable. **"Surprise me" moves to the title bar**, just left of the + icon. |
| 3, 4, 10 | Error after setting from Library; offline shows the empty page; Home re-downloads on every tab switch | **One fix: Home must keep its pictures.** The page is kept alive, pictures are served from the disk cache first, and nothing is re-downloaded on a tab switch. The internet is asked again only on first open, Refresh / Try again, or a change of search, filters or sources. A failed refresh keeps the pictures already on screen and shows a small warning. Offline shows the cached pictures. |
| 5 | Every card shows "Wallpaper", a size, and the site name | **No permanent text on cards.** Hovering shows "View wallpaper" and the size; all details stay in the preview. The filler word "Wallpaper" disappears. The bottom-right "Sources:" line gets bigger and clearer so the enabled sites can be read at a glance. |
| 6 | "Change with the time of day" and "Use the weather" were unclear and interfered with the timer | Rotation gets **one trigger, chosen**: **Time of day** or **Timer**. Choosing one turns the other off. **Time of day** means bright/light wallpapers by day and dark ones by night — not "sunset photos at sunset" — and it changes at **sunrise and sunset**. **Timer** offers **Daily** (midnight, local time), **Every 24 hours** (counted from the last change) or **Custom** (hours and minutes). **Weather is removed completely**: no forecast lookup, no temperature line, no weather words. Default on a new install: **rotation ON, mode Time of day**. Consequence accepted: a fresh install changes the desktop by itself at the next sunrise or sunset. |
| 7 | Settings was a maze of doors | **One expanded page**, no nesting: **Appearance & behaviour** (theme, taskbar theme, language, start with Windows, tray icon, reduce motion) · **Wallpaper rotation** · **Storage** · **Animated wallpapers** (player options, performance, screensaver) · **Advanced** (collapsed: debug window, export logs) · **About**. No "System" group. **"Change now" is removed** (it lives in the title bar as Surprise me). The beta/stable channel line is removed. Animated options stay visible because Iceberg will later accept user-uploaded static **and** animated wallpapers in every format Lively supports; Nagomi is app-provided, not user-provided. |
| 8 | Back jumped to Home instead of walking back through Settings | **Real history-based Back.** Back from a Lively page returns to Settings; back from Settings returns to the screen you came from; Back on Home does nothing and the arrow is hidden there. (Today there is no back handler at all — the arrow is wired to nothing.) |
| 11 | After a restart the window opened itself instead of staying in the tray | **Never open a window at log-in.** The log-in start runs with no window and no taskbar button, tray icon only. Opening the app from the tile or the tray shows the already-running window. This removes the unreliable "is my parent Explorer?" guess that causes the window to appear at every log-in. The X button hides to the tray (instead of the taskbar) while rotation is on. |
| 12 | After a restart, setting a wallpaper changed the lock screen but not the desktop | **Use the saved picture file directly, through the same Windows personalisation API the lock screen uses** (`UserProfilePersonalizationSettings.TrySetWallpaperImageAsync`), with a direct `SystemParametersInfo` call as fallback. No monitor device ID is involved, so a stale ID after a restart cannot make it fail silently. WorkerW hosting and the player screenshot are skipped for still pictures. The `Collection was modified` crash during restore is fixed too. |

### Evidence for #12 (from the app's own log)

`%LOCALAPPDATA%\Lively Wallpaper\logs\20261028_092220.txt`, 09:22:

```
Failed to set lockscreen/desktop wallpaper: The method or operation is not implemented.
HRESULT: 1400, Failed to set perscreen wallpaper
Failed to set wallpaper as child of WorkerW
NotImplementedException at Lively.Core.Wallpapers.PictureWinApi.ScreenCapture
Failed to restore wallpaper: System.InvalidOperationException: Collection was modified
```

The desktop route depends on hanging the picture inside the desktop window
(`WorkerW`, which fails with "invalid window handle" when the core starts at
log-in before the desktop is ready) and then on a screenshot of the player,
which was never implemented (`ScreenCapture` throws). The lock screen is
unaffected because it is set by a direct Windows call.

### Why WorkerW exists, and why it is not deprecated (#12)

WorkerW is the hidden desktop window that sits behind the icons. Animated
wallpapers (video, web pages, GIFs, Nagomi) can only be shown by putting the
player's own window inside it. That is the whole reason the mechanism exists,
and it has nothing to do with the rotation timer or the time of day. It will be
needed again when user-uploaded animated wallpapers arrive.

A still picture is different: Windows can display it natively. `PictureWinApi`
already does exactly that (`desktop.SetWallpaper(monitorId, file)`), and the
monitor device ID is the weak part — after a log-in restart the ID can be stale
and Windows ignores the call without raising an error. The screenshot route
exists for animated wallpapers (a still frame is captured so the real Windows
wallpaper matches, keeping the sign-in and UAC screens correct, and giving
Windows a picture to take accent colours from). Pointless for a photo that is
already a file, and in this build it is a stub that throws.

So: WorkerW and the screenshot are legitimate features for moving wallpapers,
not dead code. For still pictures they are unnecessary indirection, which is why
#12 removes them from that path only.

### How the route is chosen (#12, agreed)

The test is **"does this wallpaper have its own window?"**, not "what type is
it?". A still picture reports no window and goes straight to Windows; video,
GIF and web wallpapers report a window and go to WorkerW, exactly as today. This
also survives the case where someone chooses mpv as their picture player (the
photo then does have a window), and any wallpaper type added later.

After a restart the app restores the saved wallpaper immediately through the
correct route so the desktop is never blank, **then lets the normal rotation
tick decide** — including catching up if a sunrise or sunset happened while the
machine was off. This replaces Lively's old restore step, which is the code that
crashes with `Collection was modified`.

### Deliberately not in this round

- Weather of any kind (removed, per #6).
- App colours following the clock (removed, per #1).
- User-uploaded wallpapers, static or animated (future feature, per #7).
- Favourites, Library and the full-size preview keep their current behaviour.

### Implementation order (when approved)

1. #1 and #7 together — one theme, one Settings page. Removes the white-on-white
   fault at the root and stops the settings screens from fighting each other.
2. #6 — rotation trigger, after the weather and tint code is gone.
3. #2 and #5 — Home controls and card captions.
4. #3, #4, #10 — keep Home alive, serve from cache, never wipe on failure.
5. #8 — real Back history.
6. #11 — quiet log-in start.
7. #12 — desktop picture from the saved file, and the restore crash.

Each step keeps the existing tests green; new tests are added for the rotation
trigger, the feed cache and the back history.

### Version numbering (agreed)

Windows requires four numbers in the package identity, so the manifest uses
`1.0.0.0` for this release. The first two numbers are the public version and the
last two are for quiet fixes (`1.2.0.0`, then `1.2.1.0`). The About page shows
the friendly form only — **"Version 1.0"** — because trailing zeros tell a person
nothing. Store updates must always move upward.
