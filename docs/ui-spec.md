# Shoal for Jellyfin: admin UI handoff spec

Author: LUMEN (UI/UX). Date: 2026-10-15. Status: ready for Kiro 2, **synced with DESIGN v2.2**, plus the v2.3 client-text rules (W4-15) and owner D12-D14 in v2.2.1 (see "v2.2.1 changes" and "v2.2 changes" below, then "v2.1 changes" and "v2 sync"). Reviewer at build time: Lumen, using the checklist in section 15.

> **Shoal for Jellyfin.** Your friends' libraries, next to yours. Nothing copied, nothing exposed.

## v2.2.1 changes (2026-10-15, after Lumen review 01)

Copy-only patch from DESIGN v2.3 (Warden W4-15) and owner decisions D12-D14. Nothing in tokens, layout or components changed.
1. **8.3.12 "Which apps work" rewritten.** Three lists instead of two: **Works** (only apps with a recorded real-device pass on the shipping build), **Should work** (supported but not tried on a real device), **Doesn't work** (was "Not yet"). No "yet" anywhere (Option A may never ship). The summary line no longer uses a possessive ("titles from {Server}", so "Hops" never becomes "Hops's"). The rows are data with a `verified` flag, so moving an app into Works is a one-line change. Ship gate adds the T-CLI-13 record.
2. **8.5 item 3 and 8.5.1: "Streams at once per person"** follows owner D13: 1 to 4, default 4, with a help line.

**What Kiro 2 must change:** `appsLead`, `appsSummary`, `appsNotYet` and `appsWhatBody` in `STRINGS`; the new `appsShould*` strings and the `verified` row table (8.3.12); `MaxStreamsPerUser` clamp and default (UI field, `SettingsValidator`, `PluginConfiguration`) and the new `fMaxStreamsHelp`. Details in `docs/reviews/lumen-01-admin-ui.md`.

## v2.2 changes (2026-10-15)

DESIGN v2.2 answered every question in 14.2 (its "Lumen block" L1-L8) and added client compatibility (DESIGN 6.12). Section numbers are unchanged; the one new subsection is 8.3.12. Nothing in tokens, layout or components changed: this is copy, states and data names.

**What changed**
1. **Pop-up for safety stops only** (8.2.9; DESIGN 4.2a "Two kinds", L1). Jellyfin's `DisplayMessage` goes out only for `requestReplaced`, `codesDidNotMatch`, `serverChanged`, `adminAccount` and `badPeerResponse`. Every other kind shows "Pairing didn't finish" on the card and sends **no** pop-up, so a network blip never trains admins to ignore the real ones.
2. **New outcome `codeMismatch`, separate from `denied`** (8.2.5, section 7; DESIGN 4.1b step 3, 4.2 step 3, L4). Invite path only. Three wrong safety codes end that request **and its invite**; Home now says "The safety code didn't match" instead of folding it into "turned down". `denied` now means only that the friend chose Deny.
3. **`interrupted` has two phases** (8.2.5, 8.2.6; DESIGN 4.2a "Abort is durable"). Before approval it reads "The code expired" (invite path "The invite expired") and, on the Quick Connect path, asks the friend to delete a stray `Shoal: {Home}` device if one appeared. After approval it stays "Shoal restarted before you confirmed". The Expired row no longer covers restarts.
4. **"Which apps work"** (new 8.3.12; DESIGN 6.12, owner D11, manager decision G1). A short, calm note in Server detail and in Settings > Playback: Moonfin everywhere and Jellyfin's own web and desktop apps work; the Jellyfin Android TV app, Swiftfin, Findroid and external players don't yet. Ships only after the owner confirms G1 and Warden reads the text (ship gate in 8.3.12).
4a. **Session limit blocking a pairing** (Pilot-02, `docs/tests/pilot-02-tsec22-tcli-baseline.md`). When the limit is still 1, the friend sees only a generic error ("Error processing request."); "maximum number of sessions" appears only in their server's log. No copy tells anyone to look for that message any more. 8.2.3 (re-pair note, Phase A on re-pairs) and 8.2.8 step 5 now say: "If your friend sees an error when they approve, the session limit is probably still at 1. Ask them to set it to 0, then try again." New stopped kind `sessionLimitBlocked` (name proposed; Kiro is adding it) with its own 8.2.5 row, no pop-up.
5. **Share side**: the third wrong code cancels the request and its invite (6.18, 8.2.5 share side), confirmed by DESIGN.
6. **Proposed shapes confirmed** (13): `DismissPairing`, `ConfirmRestore`, `confirmName`, `peerSilent`, `probeExpired`, `paired`, `phase`, `sessionLimit`, `accountNotes`, `pairingSession`, `pairingError`, `pairingStoppedUtc`, the new `policy` values, `inviteFailures`, `proxySuspected`, `backupByCopy`, `guardLastGood`, `restoredFrom`, `secretsReason`, `keySource`, `isShareUser`. Two renames to DESIGN's names: approval `serverIdPrefix` → `homeServerIdPrefix`, approval state `notCollected` → `uncollected`; `restoredFrom.writtenUtc` → `atUtc`; `pairingSession` is a handle, not a boolean.
7. **14.2 closed**: each item now names the DESIGN section that resolved it. A short 14.3 lists what is still open after v2.2.

**What Kiro 2 must change**
- 8.2.9: send the pop-up only for the five safety kinds; delete the "other kinds" pop-up string.
- 8.2.5 and section 7: add the `codeMismatch` row and card hint; rewrite the `denied` row; split `interrupted` by `phase` (and by method for the before-approval copy); remove "Also shown when this server restarted…" from Expired.
- 8.2.3 step 3 (invite path): `codeMismatch` and `denied` lead to different outcomes.
- Session limit (Pilot-02): new 8.2.5 row for `sessionLimitBlocked` (use the kind name Kiro implements); new re-pair line in Quick Connect Phase A; reworded re-pair note (8.2.3 step 2) and 8.2.8 step 5. Remove any string mentioning "too many sessions" or "maximum number of sessions" from UI copy.
- 6.18 and 8.2.5 share side: "Request cancelled" copy now says the invite ended too.
- New 8.3.12 block, rendered in Server detail (after 8.3.6) and Settings item 3; reuse 6.8 `details`.
- Mock JSON (13): session `phase`, `codeMismatch` in the error list, approval `homeServerIdPrefix` and `uncollected`, `restoredFrom.atUtc`, `pairingSession` as a handle.
- `STRINGS`: re-diff against 6.18, 7, 8.2.3, 8.2.5, 8.2.6, 8.2.9, 8.3.12, 8.5 item 3.
- Checklist: D4 and D11 changed, D16 is new (15).

## v2.1 changes (2026-10-15)

DESIGN v2.1 (the Warden-02 patch) changed what the friend can set, how the invite path is approved, and what "stop sharing" honestly means. Section numbers are unchanged. Where DESIGN 9.3 or 9.5 still carry v2 wording that the patched 4.x sections overrule, this spec follows 4.1, 4.1b, 4.2, 4.2a and 4.5 and lists the conflict in 14.2.

**What changed**
1. **Invite path: the friend types the safety code.** Home shows the 8-character code; the friend reads it from Home's admin and types it into a code box on the approval card. The sharing side never shows or receives the code (6.10, 6.18, 8.2.3 step 3, 8.2.4 step 3). "The codes match" and "They don't match" are **gone from the invite path**; they stay on the Quick Connect path, where they drive the server's AwaitingConfirm step. A wrong code gets an inline "doesn't match, {n} tries left"; three wrong codes cancel that one request (8.2.5 share side). *(v2.2: and its invite; Home shows its own `codeMismatch` outcome instead of `denied`.)*
2. **"Invites cancelled after failed attempts" is withdrawn** everywhere (section 7, 8.2.5, 8.7, 13). Wrong invites from strangers never cancel anything; Shoal slows them down. New calm notice: "Someone tried {n} wrong invites on your server in the last hour. Your invites still work." (8.1.1 item 7, 8.5 item 7). Home's message for its own limit: "Too many tries. Wait {m} min, then try again." (8.2.5).
3. **Steps for your friend (stock Jellyfin)** rewritten in two parts: *Set these* and *Jellyfin has no switch for these*, plus **step 9, the session limit of 1**, and a truthful "stop sharing" and "pause" footer: deleting the sharing account is the only clean stop; pausing needs a new password (8.2.8). Step 9 and the pause text ship only after the lab check T-SEC-22 passes.
4. **Every "disable the sharing account" became "delete the sharing account and make a new one"** (8.2.5, 8.2.8, 8.3.2 `foreignDevice`). Remove server tells the admin what their friend still has to do (8.3.8).
5. **Pairing stopped, durable** (section 7, 8.1, 8.2.5, 8.2.9 new): a stopped pairing stays on its Overview card through reloads and restarts until **Dismiss** or a successful pairing. Safety stops ("Stopped for safety") are separated from ordinary ones ("Pairing didn't finish") so a network blip never reads as an attack. After `requestReplaced` or `codesDidNotMatch`, "Start again" requires a **new sharing account** (a required checkbox on step 2). Jellyfin also gets a short pop-up for admins (8.2.9). *(v2.2: safety stops only; ordinary stops send no pop-up.)*
6. **New pairing outcomes** keyed by DESIGN 4.2a: `quickConnectUnreachable`, `badPeerResponse`, `interrupted`, plus the non-final "waiting for the server" status, `requestReplaced` reworded per 4.2a, the 409 "already pairing" and the expired check (8.2.5). Every retry is a new admin click from "Check the server".
7. **Quick Connect path**: the 6-digit code shows only after Shoal confirmed it with the friend's server ("Getting a code…" first); a confirm countdown (15 min) in phase B; a re-pair note about deleting the old Shoal device first (8.2.3). *(Corrected in v2.2: deleting the old device isn't enough, because Jellyfin checks the session limit before it replaces a device's key. The note now says to set the session limit to 0 until pairing is done, then back to 1. DESIGN 4.1 step 9, 4.4.)*
8. **Invites need encryption**: an invite is refused on a home network over plain http; the form says to use Quick Connect instead (8.2.3 step 1).
9. **Server detail**: session limit next to the device lock; a new `sessionLimit` alert; `policy` alert gains Live TV and remote control; a closed "Notes about this sharing account" for the facts Jellyfin 12.1 can't change, never counted as alerts (8.3.1, 8.3.2). The address is editable, with a typed confirmation because changing the host signs Shoal out (8.3.7).
10. **Libraries & access**: wording follows the honest guarantee, "playback is checked every time; lists catch up within about an hour" (8.4.4, 8.4.5); the picker never lists sharing accounts (8.4.3).
11. **Settings and banners**: the restored-from-backup banner now needs **Confirm** (all libraries go back to "Only selected users" after a restore) and is no longer dismissible per browser; recovery banner says when protection for shared libraries is off; new key-format and two-variable key errors; key-file source; reverse-proxy warning; "backups by copy" note; a limit (not a cap) on transcodes (8.5, 8.8).
12. **API shapes** (13): approval never carries the safety code and sends `{approve, sas}`; the full 4.2a error list; `isShareUser`; `confirmName` on destructive calls; new proposed fields listed in 14.

**What Kiro 2 must adjust**
- Components: add the **code input** (6.10) and rebuild the approval card around it (6.18). Add the abort card variants (section 7).
- Copy: re-diff `STRINGS` against 6.18, 7, 8.2.3, 8.2.5, 8.2.8, 8.2.9, 8.3, 8.4.4, 8.5, 8.8.
- Mock JSON: drop `safetyCode` from the approval mock; add `triesLeft`, the new `error` kinds, `sessionLimit`, `isShareUser`, `inviteFailures`, `restoredFrom.needsConfirm` (13).
- Remove every "Invites cancelled after failed attempts" string and the `cancelledAfterFailures` invite state.

## v2 sync (2026-10-14)

This revision makes the spec match `docs/DESIGN.md` v2 section 9 (9.1-9.6) and the owner decisions in `docs/DECISIONS.md` (D3-D9). Section numbers are unchanged. New material is added as new subsections at the end of a section (4.3 `code-sm`, 6.16-6.19, 8.2.8, 8.3.11, 8.4.4-8.4.6, 8.5.1, 8.8) or as lettered items (8.5 items 8a, 8b).

**What changed**
1. Every **[v2]** marker is resolved; section 14 maps each of DESIGN 9.5's answers to the place it landed and lists what is still open.
2. **Safety code** has two formats: invite path 8 characters `K7QM 2XA4`, Quick Connect path 4 characters `K7QM` (6.10). It is never shown at "Check the server". The Server detail header shows **Server ID** (8 hex), not a safety code (8.3.1).
3. **Pairing** (8.2): watch steps are now Connect, Check the server, Pair, Choose what to add. The invite path takes the invite plus the address. The Quick Connect path compares the safety code after approval in the friend's Devices list and gives copyable steps for the friend (8.2.8). **Removed:** "Enter the code {Friend} sent" (share side), "Approve a code" (Friend detail) and the "If they have Shoal" Quick Connect instruction: with Shoal on both sides, pairing is invite-only. The share side gets an approval card (6.18). Outcomes are keyed by the API's error kinds, including `requestReplaced` and `codesDidNotMatch` (8.2.5). Pairing polls every 2 s.
4. **Who can see a friend's library** (D3/D4): one access switch per library, "Only selected users" / "Everyone on this server" (6.17, 8.4), the pairing admin pre-selected, picker notes from DESIGN 9.5 #3, the picker's top row renamed **"Select all"** (so it isn't confused with the switch), the parental-limits acknowledgement, confirm dialogs that list who gains or loses access, and the drift list.
5. **Server states and alerts**: `TlsKeyChanged` ("Different server" chip, certificate alert with "Trust new certificate") and `PairingAborted` ("Stopped for safety"); new alerts for held additions, another device on your account, account policy, merged versions, storage limit and caps; a Storage and limits block, an opt-in certificate pin, pause and resume (7, 8.3).
6. **Friend detail** (8.7): invite section, approval card, unexpected devices, transcode limit, and **Stop sharing** as one guided flow.
7. **Settings** (8.5): new-titles hold, storage limit, picture quality, offline message; "Protect your shared videos" offers Log only and Require sign-in, with **Off under Troubleshooting**, plus the other protection counters; `JELLYFIN_SHOAL_KEY`; parental-limit tag; backups and checks; a field-to-API table (8.5.1).
8. **Banners on every screen** (6.16, 8.8): recovery mode (blocking), keys can't be unlocked (blocking for friends' servers), settings restored from a backup (info).
9. **Section 13** uses v2 field names and the `Shoal/Admin/*` routes of DESIGN 9.6. Shapes DESIGN doesn't define yet are marked *(proposed)* and listed in 14.
10. Owner decisions D3, D5 and D6 close section 16. D7 (the friend needs no plugin) changes the first-run steps (8.1.5).

**What Kiro 2 must adjust in U1** (page shell, tokens, components, mock data)
- Shell: add the banner slot `.shoal-banners` between the nav and the views (1.2) and build the banner component (6.16).
- Tokens: add the `code-sm` type token (4.3). No color, space or radius token changed.
- Components: code display variants for the 8- and 4-character safety codes, the one-time invite box, the invite input and the Server ID (6.10); access switch = the 6.6 segmented control plus a label, a mode description and a confirm step (6.17); approval card (6.18); step checklist (6.19). Two new grid rows (3.3).
- Status vocabulary: add the `TlsKeyChanged` and `PairingAborted` chips, friend and device chips, library access chips (7).
- Picker: the top row says "Select all", not "Everyone" (8.4.3).
- Mock JSON: use the v2 names in 13 (for example `serverIdPrefix` on the probe, not `safetyCode`; the pairing session `{direction, method, step, state, code, safetyCode, expiresUtc, error}`; library `access: {mode, selectedUserIds, includeRestricted}` and `drift`; users `managedByShoal`, `restricted`; friends `unexpectedDeviceCount`, `transcodingNow`; hardening `{mode, modes, sinceUtc, counters, guardErrors, shieldHits, transcodeCapHits}`) and the `Shoal/Admin/*` routes. Drop any mock for "Approve a code".
- `STRINGS`: re-diff against section 8 (pairing, friend detail, settings and banner copy changed).

**Sources**
- Approved mockup: "Native Plus", https://claude.ai/artifact/KnZfMfTsjY4XsKNQ9E5icT (`project/Main.dc.html`, version 1792013507-a434, including the owner's edits). Open it next to this spec. Where the two disagree, this spec wins; every deliberate change is listed in 0.3.
- `docs/DESIGN.md` **v2.2** (v2 structure; v2.1 and v2.2 changelogs at its top; 6.12 client compatibility is new in v2.2): section 9 (9.1 rules, 9.3 what each screen needs, 9.4 states, 9.5 answers to this spec's section 14, 9.6 admin API), plus 3.7 (recovery), 4.1-4.5 (pairing, revoke), 5.4 (images), 5.6 (access modes), 5.7 (offline), 5.8 (overlap), 5.10 (caps), 7 (speed and verdicts), 8.2 (keys), 8.4 (stream protection).
- `docs/DECISIONS.md`: D3 and D4 (access mode), D5 (offline card), D6 (friends section on Overview), D7 (friend needs no plugin), D9 (log-only protection with one-click enforce).
- jellyfin-web `v12.1` and jellyfin `v12.1` source (Scout's clones, `$JF` as in `docs/research/scout-01.md`). File references below are `web:path:line`.

**Owner's 2 edits on the canvas.** Diffing my published file against the current one shows one change beyond markup re-serialization (self-closing SVG tags were rewritten; that is the editor, not the owner): the layout frame `.shell` was resized to an inline `width: 1498px; height: 1967px`, i.e. a width edit (+58 px past the 1440 board) and a height edit (+47 px past 1920). Effect on the design: the main column now reaches its 1240 px cap instead of stopping at 1204 px, and the page's last line (the image credit) is no longer cropped by the board. This spec honors both: content max width is **1240 px** (3.1), and the page **always grows to its full content height**: no fixed heights, no inner scroll areas, nothing clipped at the bottom (3.1, checklist A1-A2).

---

## 0. Decisions in one screen

### 0.1 What Kiro builds
1. **Three files, embedded resources, one plugin page.** `shoal.html` (markup), `shoal.js` (ES module controller), `shoal.css` (all styling). No inline script, no inline styles, no inline event handlers, no CDN, no web fonts. (Section 1.)
2. **Six screens** on one page, chosen by a `view` query parameter: Overview, Libraries & access, Settings, plus three sub-screens: Pair a server, Server detail, Overlap report (and Friend detail on the sharing side). (Section 2.)
3. **Theme-following surfaces, Shoal-owned signal colors.** Backgrounds, text and dividers come from Jellyfin's `--jf-palette-*` CSS variables. Status, link and button colors come from two verified Shoal sets (dark and light). (Sections 4-5.)
4. **Primary button = `#0a7cab` fill with white text (4.68:1)**, replacing white on `#00a4dc` (2.86:1, fails). Hover `#086c95` (5.85:1), pressed `#075a7d` (7.59:1). When the admin's theme has a non-blue primary, the button follows the theme instead. (5.2.)
5. **Status is always icon + text**, never color alone. One status vocabulary everywhere (section 7).
6. **Images only from the Home server**: stored stub art served by core, addressed by local item id. Never a peer URL. (Section 10.)

### 0.2 Plain-word vocabulary (UI copy only; code keeps DESIGN's terms)
| DESIGN term | UI word | Why |
|---|---|---|
| Peer | **server** ("friends' servers", "Pair a server") | "peer" is jargon; the mockup's button already said "Pair a server" |
| Home role / Peer role | "Watch a friend's library" / "Share your library with a friend" | direction in plain words |
| Share, share user | "friend", "sharing account" | |
| Federated library | "library from {Server}" / "shared with you" | "federated" never appears in UI |
| Fingerprint (v1), SAS (invite path), check (Quick Connect path) | **safety code** | familiar from messaging apps; one word for both formats (6.10). v2.1: on the invite path Home shows it and the friend **types** it; on the Quick Connect path both admins compare it |
| Server `Id` prefix (`serverIdPrefix`) | **Server ID** | 8 hex characters; detects a wrong or reset server, so it is never called a code |
| Hardening filter, modes `LogOnly` / `Enforce` / `Off` | **"Protect your shared videos"**: "Log only (test first)" / "Require sign-in" / "Off" (under Troubleshooting) | DESIGN 8.4, 9.5 #4 |
| Access mode `Selected` / `Everyone`, selection | **"Only selected users"** / **"Everyone on this server"**; "people you choose" | owner D3/D4 |
| Drift | "Access changed outside Shoal" | |
| Held removals / held additions | "removals on hold" / "new titles on hold" | |
| Invite (`shoal1-…`) | "invite" | |
| Revoke | **"Stop sharing"**; on a stock Jellyfin server: **"delete the sharing account"** (never "disable") | DESIGN 4.5 |
| Pause on a stock Peer | "turn the account off and give it a new password" | DESIGN 4.5: disabling alone is best-effort |
| `MaxActiveSessions=1` | "session limit of 1" | DESIGN 4.1 step 9 |
| `TlsKeyChanged` | chip "Different server", alert "{Server}'s certificate changed" | DESIGN 9.3 |
| `PairingAborted` | safety kinds: "Pairing stopped for your safety" (chip "Stopped for safety"); other kinds: "Pairing didn't finish" (chip "Pairing stopped") | DESIGN 9.3, 4.2a; split by kind in section 7 |
| Recovery mode / `SecretsUnreadable` | "Shoal couldn't read its settings" / "Shoal can't unlock its saved keys" | banners, 8.8 |
| Restricted profile | "profile with parental limits" | |
| AuthFailed | "Access removed" | |
| IdentityMismatch | "Different server" | |
| Disabled | "Paused" | |
| DERP relay | "relayed" (DERP region only in diagnostics) | |
| Throughput | "speed" | |
| Transcode | "transcode" (kept: Jellyfin's own dashboard uses it, audience is admins) | |

### 0.3 Changes from the approved mockup (deliberate)
| # | Mockup | Build | Reason |
|---|---|---|---|
| 1 | "Federation" title and nav item | "Shoal" | rename (`docs/naming.md`) |
| 2 | Noto Sans 500/600 from Google Fonts | inherit the dashboard's Noto Sans; **weights 400 and 700 only** | jellyfin-web bundles only 400 and 700 (`web:styles/noto-sans/base-400-normal.scss`, `base-700-normal.scss`); 500/600 would silently render as 400/700. No network fonts |
| 3 | Surfaces `#1c1c1c` / `#232323` | theme paper `--jf-palette-background-paper` (`#202020` in Dark) and a 5% ink well | follows custom themes |
| 4 | Link text `#00a4dc` | `--jf-palette-primary-light` (`#33b6e3`) in dark themes, `#005f80` in light themes | `#00a4dc` fails 4.5:1 on Blue Radiance (4.45) and WMC (4.30) page backgrounds and on Light (2.4) |
| 5 | Quiet button `#262626` with `#363636` border | Jellyfin's own `.raised` colors (`#424242`, hover `#616161`; Light `#d8d8d8`) | native look; the mockup border was 1.57:1 |
| 6 | Segmented control: selected = `#2e2e2e` fill | selected = 3 px accent underline + bold + `aria-pressed` | old selected state was 1.2:1 against unselected |
| 7 | Offline card: hatched "No preview while offline" | stored backdrop shown **desaturated** with an "Offline" pill; hatch only when no image exists | DESIGN 5.7: offline titles stay listed and art is stored locally. **Owner D5** |
| 8 | "titles hidden until it's back", poster placeholder "hidden while offline" | "titles stay listed but won't play until it's back"; the hidden-titles placeholder appears only when parking is on and has happened | matches DESIGN 5.7 default (park off) |
| 9 | Status line "3 titles shared with you" | sum of active titles (1,204 + 386 = "1,590 titles shared with you") | data error in mockup |
| 10 | Chart thresholds 4K ~50 / 1080p ~20 Mbps, "measured every 15 minutes" | thresholds from DESIGN 7.2 (4K 30, 1080p 15 Mbps); sparse samples drawn with point markers | DESIGN: light test every 6 h plus passive play samples |
| 11 | Cards selected by clicking the card | the speed panel has a **Server** select; clicking a card surface is a pointer shortcut for the same thing; the card name links to Server detail | keyboard access (2.1.1) and a path to detail |
| 12 | KPIs "Throughput now", "Evening low" | "Speed now", "Lowest in 24 h" | plain words, and "evening" is a guess the data can't make |
| 13 | Header buttons only | local nav: Overview / Libraries & access / Settings | the screens the owner asked for |
| 14 | no access control UI | per incoming library: access switch "Only selected users" / "Everyone on this server" plus the "Who can see this" picker | DESIGN v2 default-deny, owner D3/D4 |

---

## 1. How plugin pages actually load in Jellyfin 12.1 (read before coding)

Facts, verified in `$JF` source:

| Fact | Evidence | Consequence |
|---|---|---|
| Plugin pages are served by `GET /web/ConfigurationPage?name=<PluginPageInfo.Name>` with a MIME type from the resource's extension | `jellyfin:Jellyfin.Api/Controllers/DashboardController.cs:76-95` | `.js` and `.css` resources can be served the same way as the `.html` |
| The dashboard route is `configurationpage?name=…`; the drawer lists pages with `EnableInMainMenu=true` using `DisplayName` and a Material icon name in `MenuIcon` | `web:apps/dashboard/routes/routes.tsx:15,49`, `web:apps/dashboard/components/drawer/sections/PluginDrawerSection.tsx` | Shoal gets its own drawer item under Plugins |
| The page HTML is fetched with `location.search` appended, and the view reloads when `location.search` changes; the controller receives `params` | `web:components/ServerContentPage.tsx`, `web:components/viewManager/viewManager.js:23-28` | each screen gets its own URL, so browser Back works |
| `data-controller="__plugin/<Name>"` loads `configurationpage?name=<Name>` as an ES module and calls its default export with `(view, params)` | `web:components/viewContainer.js:9-29` | the controller lives in `shoal.js`, not an inline `<script>` |
| The fetched HTML is passed through `globalize.translateHtml`, which rewrites every `${…}` | `web:lib/globalize/index.js:265-285` | **the HTML must not contain `${`** (another reason to keep JS out of it) |
| Only `emby-button` is registered globally. `emby-input`, `emby-select`, `emby-checkbox`, `emby-collapse`, `emby-toggle` and their SCSS load only when some core screen imported them first | `web:index.jsx:33-34` ("files should ensure the component is imported before use"); `web:elements/*/` | a plugin page **cannot rely** on those elements upgrading or on their CSS. `.emby-checkbox` even hides the native input when its CSS is present (`web:elements/emby-checkbox/emby-checkbox.scss`) |
| The same is true of `paperList`, `listItem`, `cardBox`, `inputContainer`, `selectContainer` structure CSS (component SCSS) | `web:components/listview/listview.scss:286`, `web:components/cardbuilder/card.scss:88`, `web:elements/emby-input/emby-input.scss:32` | Shoal CSS must be self-sufficient |
| Theme colors are MUI CSS variables with prefix `jf`, switched by `html[data-theme="<id>"]`; built-in ids: `dark`, `light`, `appletv`, `blueradiance`, `purplehaze`, `wmc` | `web:themes/index.ts:12-27`, `web:scripts/themeManager.js:46` | Shoal reads `--jf-palette-*` with hex fallbacks |
| Theme-level classes (`.raised`, `.button-submit`, `.button-delete`, `.button-link`, `.emby-input`, `.emby-select-withcolor`, `.listItem-border`) are colored in the always-loaded theme CSS | `web:themes/_base/_theme.scss:166-197, 382-417` | Shoal reuses these class names so admin custom CSS that targets them still applies |
| Material icon font (`material-design-icons-iconfont`) and Noto Sans 400/700 are bundled | `web:../package.json:82,123`, `web:styles/fonts.noto.scss` | no network for icons or fonts |

### 1.1 Files and registration (Kiro)
```
Configuration/shoal.html   PluginPageInfo { Name = "Shoal",     DisplayName = "Shoal", EnableInMainMenu = true, MenuIcon = "share", EmbeddedResourcePath = "<ns>.Configuration.shoal.html" }
Configuration/shoal.js     PluginPageInfo { Name = "Shoal.js",  EmbeddedResourcePath = "<ns>.Configuration.shoal.js" }
Configuration/shoal.css    PluginPageInfo { Name = "Shoal.css", EmbeddedResourcePath = "<ns>.Configuration.shoal.css" }
```
- `MenuIcon = "share"` is the Material glyph matching the mockup's nav icon.
- The spike's `configPage.html` is retired (Kiro's rename task).

### 1.2 Page shell (exact)
```html
<div id="ShoalPage" data-role="page" class="page type-interior pluginConfigurationPage shoalPage"
     data-controller="__plugin/Shoal.js">
  <link rel="stylesheet" href="configurationpage?name=Shoal.css">
  <div data-role="content">
    <div class="content-primary">
      <div class="shoal-root" data-view="overview">
        <!-- local nav or back link: section 2.2 -->
        <div class="shoal-banners" data-shoal-banners></div>  <!-- v2: page banners, 6.16 and 8.8; empty when none -->
        <!-- views, templates, icon sprite, live regions: see sections 2, 6, appendix A -->
      </div>
    </div>
  </div>
</div>
```
- The controller also injects the stylesheet link via `ApiClient.getUrl('web/ConfigurationPage', { name: 'Shoal.css' })` if no `link[data-shoal-css]` exists (handles a `BaseUrl` or unusual document base). Keep one link only.
- Do **not** add a `<main>` landmark: the dashboard already has one. Use `<section aria-labelledby>` and `<nav aria-label>`.

### 1.3 CSP-ready rules (design for `script-src 'self'; style-src 'self'`)
- No `<script>` in the HTML. No `onclick=` or any `on*` attribute. No `javascript:` URLs. No `eval`/`new Function`/string timers.
- No `style="…"` in the HTML. Runtime geometry goes through SVG attributes or `element.style.setProperty('--shoal-…', value)` (CSSOM is not blocked by CSP).
- Build DOM from `<template>` elements in the HTML (`content.cloneNode(true)`) and fill with `textContent`. **Never `innerHTML` with any data** (DESIGN T18, test T-SEC-11). All peer-supplied strings (server names, library names, titles, device names, user agents) are text only.
- All copy lives in one `STRINGS` object in `shoal.js` (future translation).
- Note: Jellyfin core itself still writes inline styles (for example `web:elements/emby-select/emby-select.js:125`), so a future core CSP may allow inline styles. Shoal must not need that.

---

## 2. Screens and navigation

### 2.1 Screen map
```
Dashboard drawer > Plugins > Shoal
  Overview .................. view=overview (default)           Home + Peer roles at a glance
  Libraries & access ........ view=libraries                     access switch and selection per friend's library; what each friend sees from you
  Settings .................. view=settings                      settings, "Protect your shared videos", danger zone
     Pair a server .......... view=pair[&dir=watch|share][&id=<handle>]   wizard, both directions; &id shows that server's stopped pairing (8.2.9)
     Server detail .......... view=server&id=<handle>            one friend's server: speed, diagnostics, libraries
     Overlap report ......... view=overlap&id=<handle>           storage overlap for one server
     Friend detail .......... view=friend&id=<handle>            someone watching from you (sharing side)
```
`<handle>` is the local handle from the admin API (never a peer GUID, DESIGN 9.1).

Every screen: page banners (6.16, 8.8) sit between the nav and the view when Shoal is in recovery mode, can't unlock its saved keys, or has just restored its settings from a backup. "Friends watching from you" stays at the bottom of Overview; there is no separate tab (owner D6).

### 2.2 Navigation rules
- Screen changes use `Dashboard.navigate('configurationpage?name=Shoal&view=…')`, so every screen has a URL and the browser Back button works. The controller reads `params.view` and `params.id`, shows that view, hides the others (`hidden` attribute).
- **Local nav** (top three screens only): `<nav class="shoal-localnav" aria-label="Shoal">` with three links: **Overview**, **Libraries & access**, **Settings**. Current link gets `aria-current="page"`. Visual: text links 14/700, 44 px tall, 3 px accent underline under the current one, muted text for the others.
- **Sub-screens** replace the local nav with a back link: chevron-left icon + "Overview" (or "Back" target per screen, listed in section 8), 44 px tall, link color.
- Pair a server: wizard steps are **not** in the URL. The pairing session lives on the server; reopening `view=pair` resumes at the server-reported step (8.2.6).
- After a view change, move focus to that view's `h1` (`tabindex="-1"`), and `scrollTo(0,0)`.
- An unknown `view`, or an `id` that no longer exists: show Overview with an inline notice "That server isn't paired anymore." (for `view=friend`: "That friend isn't on your list anymore."), announced in the polite live region.
- Server cards in the `Pairing` state link to `view=pair` (which resumes the session). Cards in the `PairingAborted` state link to `view=pair&id=<handle>`, which shows that server's stored outcome (8.2.9) even after a restart, because the live session is gone by then. Neither links to Server detail.

### 2.3 Data refresh (DESIGN 9.1)
- Overview and Server detail: poll the status endpoint every **10 s** while the view is shown and `document.visibilityState === 'visible'`. Stop on `viewhide`/`viewdestroy` and on `visibilitychange` to hidden.
- Pairing status: `GET Shoal/Admin/Pairing` every **2 s** while waiting (DESIGN 9.1). The server polls the friend on its own schedule and keeps going if the page closes; the page only reads the session.
- Share side: `GET Shoal/Admin/Shares/{id}/Approval` every **2 s** while an invite is active (wizard step 2 or Friend detail), so the approval card appears without a reload. Polling never touches the code box: a poll result re-renders the card's facts only, so the admin's typing and focus survive (6.18).
- Libraries & access, Settings, Friend detail: load once on `viewshow`, reload after each save. Banners (8.8) come from the Status response, so every screen also reads `GET Shoal/Admin/Status` on `viewshow`.
- Updates patch the DOM in place (no skeletons after first load, no layout jumps). Only **state changes** are announced (8.1.9, 12.4).

---

## 3. Layout system

### 3.1 Container
- `.shoal-root`: `max-width: 1240px` (owner's edit), left-aligned under the dashboard header (`margin-inline: 0 auto`), `container-type: inline-size; container-name: shoal`.
- Padding (in addition to the dashboard's own `.content-primary` padding of 16 px each side): top 32, sides 24, bottom 48 at L; top 24, sides 16, bottom 40 at M; top 16, sides 0, bottom 40 at S. Result: 40 px gutter on desktop, 32 on tablet, **16 px on phones**.
- The page has **no fixed height and no inner scroll areas**. Only data tables may scroll horizontally inside their own wrapper (12.6).

### 3.2 Breakpoints (container width of `.shoal-root`, not the viewport)
The dashboard drawer takes about 240 px on desktop, so viewport breakpoints would mis-fire. Use `@container shoal (…)`. Fallback for browsers without container queries (`@supports not (container-type: inline-size)`): the same rules on viewport widths +256 px for L and M, and the S rules below `max-width: 639px`.

| Name | Container width | Typical device |
|---|---|---|
| **L** | ≥ 1080 px | desktop, drawer open |
| **M** | 640 - 1079 px | tablet, small laptop |
| **S** | < 640 px | phone (min 288 px content at 320 px viewport) |

### 3.3 Grids per screen
| Region | L | M | S |
|---|---|---|---|
| Page header (h1 + status / actions) | row, `space-between`, align end, wrap, gap 24 | same, actions wrap under title when needed | column; actions become a 2-up row, each button `flex: 1` |
| Server cards | `repeat(3, minmax(0,1fr))`, gap 20 | `repeat(2, minmax(0,1fr))`, gap 16 | 1 column, gap 16 |
| Speed panel | chart `1.75fr` + ladder `1fr`, gap 36 | 1 column, gap 24 | 1 column, gap 20 |
| Overlap panel | text `1fr` + bar `1.15fr`, gap 36, centered | same until 760 px, then 1 column, gap 20 | 1 column, gap 16 |
| Poster shelf | `repeat(auto-fill, minmax(160px,1fr))`, gap 20, max 6 per row (extra posters hidden; "See all" covers them) | `minmax(140px,1fr)`, gap 16 | `repeat(2, minmax(0,1fr))`, gap 14 |
| Friends list, library tables | full width | full width | rows reflow to stacked label/value (12.6) |
| Server detail header | banner full width; title row with actions on the right | actions wrap below title | actions 2-up grid, full width |
| Pair wizard | single column, `max-width: 720px` | same | full width |
| Settings | single column of sections, `max-width: 820px` | same | full width |
| Dialogs | `width: min(560px, 100vw - 32px)` centered | same | same, max-height 85vh, body scrolls |
| Page banner (6.16) | full width of `.shoal-root`; grid `24px 1fr auto` (icon, text, actions), gap 16 | actions wrap under the text | icon + text, actions stacked full width below |
| Library access block (8.4.1) | name and meta on the left, held lines below; access switch and summary in one row under it, "Choose people" on the right | switch row wraps: switch, then summary + button | everything stacked; switch segments stack (6.17) |

---

## 4. Design tokens

### 4.1 CSS (copy into `shoal.css`, top of file)
```css
/* ---------- Shoal tokens. Everything is scoped to .shoalPage ---------- */
.shoalPage {
  /* Theme-following (Jellyfin MUI vars, prefix "jf"), Dark fallbacks */
  --shoal-bg:        var(--jf-palette-background-default, #101010);
  --shoal-surface:   var(--jf-palette-background-paper, #202020);
  --shoal-text:      var(--jf-palette-text-primary, #ffffff);
  --shoal-muted:     var(--jf-palette-text-secondary, rgba(255,255,255,.70));
  --shoal-line:      var(--jf-palette-divider, rgba(255,255,255,.12));
  --shoal-hover:     var(--jf-palette-action-hover, rgba(255,255,255,.08));
  --shoal-quiet-bg:        var(--jf-palette-Button-inheritContainedBg, #424242);
  --shoal-quiet-bg-hover:  var(--jf-palette-Button-inheritContainedHoverBg, #616161);
  --shoal-danger-fill:     var(--jf-palette-error-main, #c62828);

  /* Dark signal set (default; also Blue Radiance, WMC, Purple Haze, unknown dark themes) */
  --shoal-ink: 255 255 255;                      /* channel for tints */
  --shoal-surface-2:       rgb(var(--shoal-ink) / .05);   /* inset wells over surface */
  --shoal-control-border:  rgb(var(--shoal-ink) / .38);   /* inputs, segmented, checkboxes */
  --shoal-skeleton:        rgb(var(--shoal-ink) / .08);
  --shoal-accent: var(--jf-palette-primary-main, #00a4dc);  /* non-text: focus ring, chart line, selected outline */
  --shoal-link:   var(--jf-palette-primary-light, #33b6e3); /* link and info text */
  --shoal-ok:   #5cc98f;  --shoal-ok-bg:   rgba(92,201,143,.12);
  --shoal-warn: #f2b84b;  --shoal-warn-bg: rgba(242,184,75,.12);
  --shoal-bad:  #ff8a80;  --shoal-bad-bg:  rgba(255,138,128,.12);
  --shoal-off:  #a8a8a8;  --shoal-off-bg:  rgba(168,168,168,.12);
  --shoal-info-bg: rgba(51,182,227,.12);
  --shoal-hint-warn-bg: rgba(242,184,75,.10);  --shoal-hint-warn-border: rgba(242,184,75,.36);
  --shoal-hint-bad-bg:  rgba(255,138,128,.10); --shoal-hint-bad-border:  rgba(255,138,128,.40);
  --shoal-chart-area: rgba(0,164,220,.12);
  --shoal-chart-threshold: #8a8a8a;
  --shoal-hatch-a: #8a8a8a; --shoal-hatch-b: #4e4e4e;
  --shoal-shadow-poster: 0 8px 20px rgba(0,0,0,.5);
  --shoal-shadow-dialog: 0 24px 48px rgba(0,0,0,.5);
  color-scheme: dark;

  /* Primary button: theme-following by default (see 5.2) */
  --shoal-fill:        var(--jf-palette-primary-main, #00a4dc);
  --shoal-fill-hover:  var(--jf-palette-primary-dark, #00729a);
  --shoal-fill-active: var(--jf-palette-primary-dark, #00729a);
  --shoal-on-fill:     var(--jf-palette-primary-contrastText, rgba(0,0,0,.87));

  /* Fixed (both sets) */
  --shoal-scrim: rgba(12,12,12,.84);  --shoal-on-scrim: #f2f2f2;
  --shoal-font: inherit;              /* the dashboard's Noto Sans stack */
  --shoal-mono: ui-monospace, "Cascadia Mono", "Segoe UI Mono", "SF Mono", Menlo, Consolas, "Liberation Mono", monospace;

  /* Space, radius, motion */
  --shoal-space-1: 4px;  --shoal-space-2: 8px;  --shoal-space-3: 12px; --shoal-space-4: 16px;
  --shoal-space-5: 20px; --shoal-space-6: 24px; --shoal-space-7: 32px; --shoal-space-8: 40px; --shoal-space-9: 48px;
  --shoal-radius-sm: 6px;  --shoal-radius-md: 8px; --shoal-radius-lg: 10px; --shoal-radius-xl: 12px; --shoal-radius-pill: 999px;
  --shoal-dur-fast: 120ms; --shoal-dur: 200ms; --shoal-ease: cubic-bezier(.2,0,0,1);
}

/* Jellyfin-blue themes: the contrast-fixed primary button */
.shoalPage.shoal-defaultBlue,
html[data-theme="dark"] .shoalPage, html[data-theme="light"] .shoalPage, html[data-theme="appletv"] .shoalPage,
html[data-theme="blueradiance"] .shoalPage, html[data-theme="wmc"] .shoalPage {
  --shoal-fill: #0a7cab; --shoal-fill-hover: #086c95; --shoal-fill-active: #075a7d; --shoal-on-fill: #ffffff;
}
/* Set by JS when the computed primary is not #00a4dc (5.2). Specificity (0,3,1) beats the
   html[data-theme] rules above (0,2,1), so a custom CSS theme on top of "dark" still wins. */
html[data-theme] .shoalPage.shoal-themedPrimary, .shoalPage.shoal-themedPrimary.shoal-themedPrimary {
  --shoal-fill: var(--jf-palette-primary-main); --shoal-fill-hover: var(--jf-palette-primary-dark);
  --shoal-fill-active: var(--jf-palette-primary-dark); --shoal-on-fill: var(--jf-palette-primary-contrastText);
}

/* Light signal set (Light, Apple TV, and any theme JS detects as light) */
html[data-theme="light"] .shoalPage, html[data-theme="appletv"] .shoalPage, .shoalPage.shoal-light {
  --shoal-ink: 0 0 0;
  --shoal-muted: rgba(0,0,0,.68);
  --shoal-control-border: rgb(0 0 0 / .45);
  --shoal-accent: #086c95;
  --shoal-link:   #005f80;
  --shoal-ok:   #11643a;  --shoal-ok-bg:   rgba(17,100,58,.12);
  --shoal-warn: #704800;  --shoal-warn-bg: rgba(112,72,0,.12);
  --shoal-bad:  #9f1f18;  --shoal-bad-bg:  rgba(159,31,24,.12);
  --shoal-off:  #555555;  --shoal-off-bg:  rgba(85,85,85,.12);
  --shoal-info-bg: rgba(0,95,128,.10);
  --shoal-hint-warn-bg: rgba(112,72,0,.08);  --shoal-hint-warn-border: rgba(112,72,0,.40);
  --shoal-hint-bad-bg:  rgba(159,31,24,.07); --shoal-hint-bad-border:  rgba(159,31,24,.40);
  --shoal-chart-area: rgba(8,108,149,.10);
  --shoal-chart-threshold: #6b6b6b;
  --shoal-hatch-a: #6b6b6b; --shoal-hatch-b: #a8a8a8;
  --shoal-shadow-poster: 0 4px 12px rgba(0,0,0,.18);
  --shoal-shadow-dialog: 0 16px 40px rgba(0,0,0,.25);
  color-scheme: light;
}
```

### 4.2 Color tokens (reference)
Hex values are the Dark and Light built-in themes. "Theme var" means the value follows the admin's theme.

| Token | Dark | Light | Source | Used for |
|---|---|---|---|---|
| `--shoal-bg` | `#101010` | `#f2f2f2` | `--jf-palette-background-default` | page background (set by Jellyfin; Shoal never paints it) |
| `--shoal-surface` | `#202020` | `#e8e8e8` | `--jf-palette-background-paper` | cards, panels, dialogs, list containers |
| `--shoal-surface-2` | `#2b2b2b` (5% white over surface) | `#dcdcdc` | ink 5% | metrics strip, code boxes, bottleneck note, skeleton art |
| `--shoal-text` | `#ffffff` | `#000000` | `--jf-palette-text-primary` | body, headings |
| `--shoal-muted` | `rgba(255,255,255,.70)` ≈ `#bcbcbc` on surface | `rgba(0,0,0,.68)` ≈ `#4a4a4a` (Light's own secondary is 87% black, too close to primary for hierarchy) | theme var / Shoal (light) | meta, help text, axis labels |
| `--shoal-line` | `rgba(255,255,255,.12)` | `rgba(0,0,0,.14)` | `--jf-palette-divider` | card borders, dividers, chart grid (decorative) |
| `--shoal-control-border` | `#757575` on surface | `#808080` on surface | ink 38% / 45% | input, select, segmented, checkbox outlines (≥ 3:1) |
| `--shoal-accent` | `#00a4dc` | `#086c95` | `--jf-palette-primary-main` / Shoal | focus ring, chart line, selected card outline, tab underline, "new" swatch |
| `--shoal-link` | `#33b6e3` | `#005f80` | `--jf-palette-primary-light` / Shoal | links, info text, "Speed shown below" |
| `--shoal-fill` | `#0a7cab` | `#0a7cab` | Shoal (theme primary on non-blue themes) | primary button |
| `--shoal-fill-hover` / `-active` | `#086c95` / `#075a7d` | same | Shoal | primary hover / pressed |
| `--shoal-on-fill` | `#ffffff` | `#ffffff` | Shoal | primary button text and icon |
| `--shoal-quiet-bg` / `-hover` | `#424242` / `#616161` | `#d8d8d8` / `#cccccc` | `--jf-palette-Button-inheritContainedBg` / `HoverBg` | secondary buttons (Jellyfin `.raised`) |
| `--shoal-danger-fill` | `#c62828` | `#c62828` | `--jf-palette-error-main` | destructive confirm buttons only |
| `--shoal-ok` | `#5cc98f` | `#11643a` | Shoal | online, smooth, approved |
| `--shoal-warn` | `#f2b84b` | `#704800` | Shoal | relayed, unstable, may buffer, held deletions |
| `--shoal-bad` | `#ff8a80` | `#9f1f18` | Shoal | access removed, different server, won't play, errors |
| `--shoal-off` | `#a8a8a8` | `#555555` | Shoal | offline, paused, unknown values |
| `--shoal-*-bg` | 12% of the status color | 12% | Shoal | chip backgrounds (only on `--shoal-surface`) |
| `--shoal-scrim` / `--shoal-on-scrim` | `rgba(12,12,12,.84)` / `#f2f2f2` | same | Shoal | pills on images (worst case over white image: 11.29:1) |
| `--shoal-chart-threshold` | `#8a8a8a` | `#6b6b6b` | Shoal | dashed quality lines |
| `--shoal-hatch-a/b` | `#8a8a8a` / `#4e4e4e` | `#6b6b6b` / `#a8a8a8` | Shoal | "in both libraries" hatched bar |

### 4.3 Type scale
Font family: inherit from the dashboard (Noto Sans stack, `web:styles/fonts.noto.scss`). **Weights 400 and 700 only.** Express sizes in `rem` (px / 16). Numbers in metrics, KPIs, counts, tables, countdowns: `font-variant-numeric: tabular-nums`.

| Token | Size / line height | Weight | Use |
|---|---|---|---|
| `display` | 32/38 (S: 26/32), letter-spacing -0.01em | 700 | `h1` per screen ("Shoal", "Hops", "Pair a server") |
| `title` | 20/26 | 700 | `h2` section and panel titles |
| `subtitle` | 18/24 | 700 | `h3` card names, dialog titles |
| `body-lg` | 16/26 | 400 (700 for inline numbers) | overlap sentence, empty-state body, pairing instructions |
| `body` | 15/22 | 400 | default text, list rows, ladder rows |
| `label` | 14/20 | 700 | buttons, local nav, link buttons, field labels |
| `meta` | 13/19 | 400; chips 700 | help text, captions, status line, chip text, metrics labels |
| `micro` | 12/16 | 400; pills 700 | chart axis and threshold labels, image pills. Never smaller than 12 px |
| `kpi` | 28/32, unit at 15/20 400 muted with 4 px gap | 700 | KPI numbers |
| `metric` | 15/20 | 700 | metric strip values |
| `code-lg` | 40/44 `--shoal-mono`, letter-spacing .12em | 700 | Quick Connect code |
| `code` | 24/30 `--shoal-mono`, letter-spacing .06em | 700 | safety code (8 or 4 characters) |
| `code-sm` (v2) | 15/24 `--shoal-mono`, letter-spacing .02em, `overflow-wrap: anywhere` | 700 | one-time invite text, Server ID in fact lists, `tailscale ping` command, certificate fingerprints (13 px 400 in meta lines) |

### 4.4 Spacing, radius, elevation, motion
| Token or rule | Value and use |
|---|---|
| `space-1..9` | 4, 8, 12, 16, 20, 24, 32, 40, 48. Only these values, with one exception: the speed and overlap grid gap on L is 36 px (mockup value, `space-7` + `space-1`) |
| Section rhythm | page header bottom padding `space-7` (32) plus a 1 px `--shoal-line` divider; sections `margin-top: space-8` (40) on L, `space-7` on M/S; section head to content `space-3` |
| Card padding | body `space-4 space-5 space-5` (16 top, 20 sides and bottom; mockup 16/18 rounded to the scale); S: `space-4` all |
| Panel padding | `space-6` (24); S: `space-4` |
| `radius-sm` 6 px | buttons, inputs, selects, segmented control |
| `radius-md` 8 px | metrics strip, hints and alerts, posters, code boxes, bottleneck note |
| `radius-lg` 10 px | cards, panels, choice cards |
| `radius-xl` 12 px | dialogs |
| `radius-pill` | chips, image pills, progress bars |
| Card and panel elevation | none: 1 px `--shoal-line` border on `--shoal-surface` |
| Poster elevation | `--shoal-shadow-poster` |
| Dialog elevation | `--shoal-shadow-dialog`; `::backdrop` `rgba(0,0,0,.6)` |
| Motion | hover and press color changes `--shoal-dur-fast`; selected-card outline `--shoal-dur-fast`; `<details>` chevron rotation `--shoal-dur`; skeleton pulse 1.2 s ease-in-out alternate (opacity .55 to .9); spinner 1 s linear rotate. **`prefers-reduced-motion: reduce` turns off pulse, rotation and transitions** |

---

## 5. Theming rules

### 5.1 Two signal sets, theme-following surfaces
- Surfaces, text, dividers and quiet buttons always come from the theme variables, so Blue Radiance looks blue, Purple Haze purple, Apple TV light grey-blue.
- Signal colors (status, link, accent on light themes) come from the **dark set** by default and the **light set** for `html[data-theme="light"]`, `html[data-theme="appletv"]`, or when JS detects a light theme.
- Verified for all six built-in themes (section 12.1).

### 5.2 Theme detection (JS, on `viewshow`)
```js
const cs = getComputedStyle(document.documentElement);
const primary = cs.getPropertyValue('--jf-palette-primary-main').trim().toLowerCase();
page.classList.toggle('shoal-defaultBlue', primary === '' || primary === '#00a4dc');
page.classList.toggle('shoal-themedPrimary', primary !== '' && primary !== '#00a4dc');
const paper = cs.getPropertyValue('--jf-palette-background-paper').trim(); // parse #rgb/#rrggbb/rgb()
page.classList.toggle('shoal-light', relativeLuminance(paper) > 0.5);
```
- `shoal-themedPrimary` wins over the `data-theme` selectors by specificity (see the comment in 4.1). With it, the primary button uses the theme's own primary and its `contrastText` (Purple Haze: 87% black on `#48c3c8`, 8.51:1).
- Admins can override any `--shoal-*` token from Dashboard > General > Custom CSS. Document this in the README.

### 5.3 Jellyfin focus quirks to neutralize (scoped)
- Jellyfin turns `.raised:focus` into `secondary-main` background with 70% white text (about 2.3:1) and `.button-submit:focus` into `#00a4dc` with black text (`web:themes/_base/_theme.scss:166-191`). Inside `.shoalPage`, keep the resting colors on `:focus` and show the Shoal focus ring on `:focus-visible` instead.

---

## 6. Components

### 6.1 Mapping to Jellyfin
Rule: use Jellyfin's class names (so theme and admin custom CSS still reach them) plus a `shoal-` class that carries the full structure. Do not add `is="emby-input|emby-select|emby-checkbox|emby-collapse|emby-toggle"` (section 1). `is="emby-button"` is allowed (globally registered).

| Shoal component | Markup | Jellyfin equivalent it mirrors |
|---|---|---|
| Primary button | `<button is="emby-button" type="button" class="raised button-submit emby-button shoal-btn shoal-btn--primary">` | `.raised.button-submit` |
| Quiet button | `<button is="emby-button" type="button" class="raised emby-button shoal-btn">` | `.raised` |
| Danger button (dialogs only) | `class="raised button-delete emby-button shoal-btn shoal-btn--danger"` | `.button-delete` |
| Link button / text link | `<a class="button-link shoal-link">` or `<button class="button-link shoal-link">` | `.button-link` |
| Icon button | `<button type="button" class="paper-icon-button-light shoal-iconbtn" aria-label="…">` | `paper-icon-button-light` |
| Text input | `<div class="inputContainer shoal-field"><label class="inputLabel" for=…>…</label><input class="emby-input shoal-input" …><div class="fieldDescription shoal-help" id=…>…</div><p class="shoal-fieldError" id=… hidden>…</p></div>` | `emby-input` |
| Select | `<div class="selectContainer shoal-field"><label class="selectLabel" for=…>…</label><select class="emby-select-withcolor emby-select shoal-select" …>` + chevron icon `<svg>` positioned right | `emby-select` |
| Checkbox row | `<label class="shoal-check"><input type="checkbox"><span class="shoal-check__text">…<span class="shoal-check__note">…</span></span></label>` | `emby-checkbox` (look only) |
| Radio choice card | `<label class="shoal-choice"><input type="radio" name=…><span class="shoal-choice__title">…</span><span class="shoal-choice__body">…</span></label>` | none (new) |
| Collapsible section | `<details class="shoal-collapse"><summary>…</summary>…</details>` | `emby-collapse` |
| Card | `<article class="shoal-card" aria-labelledby=…>` | `cardBox visualCardBox` (look: surface + border) |
| Panel | `<section class="shoal-panel" aria-labelledby=…>` | `verticalSection` with paper background |
| List | `<ul class="paperList shoal-list">` with `<li class="listItem listItem-border shoal-row">` | `paperList` / `listItem` |
| Dialog | `<dialog class="shoal-dialog" aria-labelledby=…>` opened with `showModal()` | `dialog` component (look) |
| Tabs-as-links | `<nav class="shoal-localnav">` | emby-tabs look |

### 6.2 Buttons
| Variant | Size | Colors (rest / hover / active / disabled) | Notes |
|---|---|---|---|
| Primary | min-height 44, padding 0 18, radius 6, gap 8, `label` type, icon 16 | `--shoal-fill` + `--shoal-on-fill` / `--shoal-fill-hover` / `--shoal-fill-active` / 45% opacity, `cursor: not-allowed` | one per view or per step at most ("Pair a server", "Check server", "Start pairing", "The codes match", "Approve", "Create invite", "Save") |
| Quiet | same; `small` variant padding 0 14, 13 px | `--shoal-quiet-bg` + `--shoal-text` / `--shoal-quiet-bg-hover` / same / 45% opacity | "Sync now", "Test link again", "Retry now", "Cancel" |
| Danger | same | `--shoal-danger-fill` + white (5.62:1) / `#b3261e` (6.54:1) | only the final confirm in destructive dialogs |
| Link | min-height 44 when standalone (inline-flex), `label` type | `--shoal-link`; underline on hover and focus | "See all", "Full report", "Open full diagnostics" |
| Icon | 44 × 44, round, icon 20 | `--shoal-text`; hover `--shoal-hover` | always `aria-label` |
| Busy state | label becomes the progress text ("Syncing…", "Checking…"), leading icon becomes `sync` spinning; `aria-disabled="true"` (keep it focusable), clicks ignored | | |
| Cooldown state (DESIGN 9.1) | `aria-disabled="true"`, plus visible helper text next to it: "Available again in 4 min" (`meta`, muted), and `aria-describedby` to that text | | from `Retry-After` |

Disabled buttons use `aria-disabled` rather than `disabled` when the admin should be able to focus them and hear why (cooldowns); use `disabled` only in forms that are not yet valid, and then the reason is shown as text near the button.

### 6.3 Status chip
`<span class="shoal-chip shoal-chip--ok"><svg class="shoal-icon shoal-icon--s" aria-hidden="true"><use href="#shoal-i-ok"/></svg>Online</span>`
- Padding 3/10/3/8, radius pill, `meta` 13/700, gap 6, icon 16, `white-space: nowrap`.
- Color `--shoal-{state}`, background `--shoal-{state}-bg`. **Only placed on `--shoal-surface`** (contrast verified there).
- Text is always present. The chip is not focusable. When the chip is the only place a state appears, it is inside the element whose accessible name it completes (the card is `aria-labelledby` name + chip id).

### 6.4 Server card (Overview)
Anatomy, top to bottom (mockup structure kept):
1. **Art** 118 px (S: 104 px). `<img alt="" loading="lazy" decoding="async">`, `object-fit: cover; object-position: 50% 30%`. Bottom-left pill (`--shoal-scrim`, `micro` 12/700, padding 3/10, radius pill, 12 px from left and 10 px from bottom): "Latest from Hops: Sintel". Offline (owner D5): the stored backdrop stays, `filter: grayscale(1) brightness(.55)`, and the pill reads "Offline" with the offline icon. No image: hatched placeholder (`repeating-linear-gradient(135deg, surface-2 0 12px, transparent 12px 24px)` over surface) with the server glyph and "No pictures yet" (`meta`, muted).
2. **Name row**: `<h3>` containing `<a class="shoal-card__name" href="…view=server&id=h">Hops<svg chevron-right 16 muted/></a>`; the link text is `--shoal-text` (not link blue), underline on hover/focus. Status chip right-aligned. Name: one line, `text-overflow: ellipsis` (full text stays in the DOM for screen readers).
3. **Via line** (`meta`, muted, icon 16): lock icon + "via Tailscale" · host (host `--shoal-text`, ellipsis). Public: globe + "via internet (HTTPS)". LAN: home icon + "via home network". Proxy adds " · through proxy".
4. **Metrics strip** `<dl>`: 3 cells (`1.25fr 1fr 1fr`), `--shoal-surface-2`, 1 px `--shoal-line` dividers, radius 8, cell padding 8/12. `dt` `meta` muted; `dd` `metric`. Values: Connection = Direct (arrow icon) / Relayed (relay icon) / Proxy / None; Latency "38 ms"; Speed "42 Mbps". Unknown values show "—" plus visually hidden "unknown" (`<span aria-hidden="true">—</span><span class="shoal-sr">unknown</span>`).
5. **Verdict line** (`body` 14/22, play icon accent): "1080p smooth · 4K needs transcode". From DESIGN 7.2 summary.
6. **Hint** (only when there is one; section 7 lists the texts): `role="note"`, padding 10/12, radius 8, `--shoal-hint-warn-bg` with 1 px `--shoal-hint-warn-border` (bad variant for access removed and different server), icon 16 in the status color, text `meta` 13/19 in `--shoal-text`.
7. **Footer** (border-top 1 px line, padding-top 12, min-height 57, `meta`): left "**1,204** titles shared" (number 700 `--shoal-text`); right: the contextual action (quiet small button: "Test link again", "Retry now", "Pair again", "Resume", "Check certificate", "Continue pairing", "See what to do"; section 7 says which) **or**, on the selected card with no contextual action, the note "Speed shown below" (chevron-down icon, `--shoal-link`, is an in-page link to `#shoal-speed`).

States: selected = border `--shoal-accent` plus `box-shadow: 0 0 0 1px var(--shoal-accent)` (2 px total, ≥ 3:1 on every theme). Hover (pointer, card surface): border `rgb(var(--shoal-ink)/.24)`. Pointer click on the card surface (anything that is not a link or button) selects that server for the speed and overlap panels and updates the panel's Server select. The `article` itself is not focusable.

### 6.5 Panel
`--shoal-surface`, 1 px line border, radius 10, padding 24 (S 16). Header row: `h2` + optional subtitle (`meta`, muted) on the left; controls on the right; on S the controls drop below the title.

### 6.6 Segmented control (time range)
`<div class="shoal-seg" role="group" aria-label="Time range">` with `<button type="button" aria-pressed="true">24 h</button>`… Outer border 1 px `--shoal-control-border`, radius 8, buttons min 44 × 68, padding 0 14, `meta` 13. Separators 1 px control border. **Selected**: 700 weight, `--shoal-text`, inset bottom bar `box-shadow: inset 0 -3px 0 var(--shoal-accent)`. Unselected: 400, muted. Hover: `--shoal-hover`.

### 6.7 Fields
- Input and select: min-height 44, padding 0 12, radius 6, background `var(--jf-palette-FilledInput-bg, rgba(255,255,255,.09))`, border 1.5 px `--shoal-control-border`, `body` type, `--shoal-text`. Focus: border `--shoal-accent` plus focus ring. Placeholder text: muted (never the only label).
- Label above the field (`label` 14/700, margin-bottom 6). Help text below (`meta`, muted, margin-top 6). Field gap 20.
- Error: `aria-invalid="true"`, border `--shoal-bad`, error text below the help text: bad icon 16 + text in `--shoal-bad` (`meta` 700), connected with `aria-describedby` (help id + error id). On submit with errors: a summary at the top of the form with `role="alert"`: "Fix 2 fields to continue." Each item is a link that focuses the field.
- Checkbox row (`.shoal-check`): flex, gap 12, min-height 48, padding 10/12, input 20 × 20, `accent-color: var(--shoal-fill)`. Optional note line (`meta`, muted). Hover row `--shoal-hover`. Disabled row: 50% opacity with the reason in the note ("Coming in a later version").
- Radio choice card (`.shoal-choice`): surface, 1 px control border, radius 10, padding 16, grid `24px 1fr`, title `label`, body `meta` muted. Checked: 2 px accent outline (border + 1 px shadow) and title gets a check icon. Focus-visible ring on the card (`:has(:focus-visible)`, with a `.is-focus` class fallback set by JS).

### 6.8 Collapsible (`details`)
Summary: min-height 48, flex, `label` type, chevron-down icon 20 on the right that rotates 180° when open. Content padding-top 12. Keyboard is native (Enter/Space). Do not animate height.

### 6.9 Dialog
Native `<dialog>` with `showModal()` (focus is trapped and Esc closes natively). Surface, radius 12, padding 24 (S 20), dialog shadow. Header `h2` in `subtitle` style. Footer: buttons right-aligned, gap 12; on S full-width stacked, primary on top. Initial focus: the first field, or the **safe** button (Cancel) in destructive dialogs. On close, return focus to the button that opened it. Typed confirmation: an input "Type Hops to confirm"; the danger button is `disabled` until the trimmed value matches (case-sensitive), and the help text says so.

### 6.10 Code display (Quick Connect code, safety code, invite, Server ID)
- Box: `--shoal-surface-2`, radius 8, padding 16 20, centered text, `--shoal-mono`.
- Quick Connect code (`code`, 6 digits): `code-lg`, shown as two groups of 3 ("482 913"); copy value has no space. Accessible name: `aria-label="Code 4 8 2 9 1 3"` on the box (`role="group"`), and the visible text is `aria-hidden`.
- **Safety code** (`safetyCode`; DESIGN 9.5 #1). Crockford base32: digits and capitals without `I L O U`, so the monospace face never has to tell `0`/`O` or `1`/`I` apart. Style `code`; groups are separate `<span>`s with a 0.5em gap (no space character, so a copy or a screen reader gets clean groups). A small label above the box (`meta` 700, muted): "Safety code".
  - Invite path (SAS): **8 characters in two groups of 4**, `K7QM 2XA4`. `aria-label="Safety code K 7 Q M, 2 X A 4"`. **Shown on the watching side only** (v2.1, DESIGN 4.1b step 3). The sharing side never displays it and its API never sends it; the friend types it into the code input below. Quiet small "Copy code" under the box, because the admin may text it (copy value `K7QM2XA4`, announcement "Code copied").
  - Quick Connect path (check): **4 characters, one group**, `K7QM`. `aria-label="Safety code K 7 Q M"`. On S it sits under the Quick Connect code; on L/M to its right (two boxes, gap 16). No copy button: it is compared by eye with the friend's Devices list, never typed.
- **Safety code input** (v2.1; sharing side, inside the approval card 6.18). `<input type="text" class="emby-input shoal-input shoal-input--code" id=… autocomplete="off" autocapitalize="characters" spellcheck="false" inputmode="text" maxlength="11" aria-describedby="{help} {error}">` with a real `<label>`. Look: the 6.7 field with the `code` type token (24/30 mono 700, letter-spacing .06em), width `min(100%, 14ch)`, text left-aligned. No auto-inserted spaces or hyphens (they make the caret jump); the admin may type them, the server ignores them. The page uppercases the shown value on `input` without moving the caret, strips spaces and hyphens for its own check, and maps `I`/`L` to `1` and `O` to `0` like the server. Ready = exactly 8 characters from `0-9 A-Z` without `U` after that mapping. The page cannot tell whether the code is right: it never has it. That answer comes from the server (6.18).
  - Help (`meta`, muted): "8 letters and numbers. Spaces and capitals don't matter."
  - Format error (only after a submit attempt or on blur with something typed, never while typing): "Check the code. It has 8 letters and numbers."
- **Invite, one-time display** (sharing side; DESIGN 4.1b step 2): the `invite` string, for example `shoal1-1a2b3c4d-K7QM-2XA4-9FZD-R3TB-W8NE-HC5J-6P`. Style `code-sm`, left-aligned, padding 12 16, `user-select: all`, wraps at any point on S. Under it: quiet "Copy invite" (copy icon), the countdown (6.11, 15 min), and a warn hint (`role="note"`): "Shoal shows this invite only once. Copy it now and send it privately, for example in Signal or a text. It works one time, for 15 minutes." The string lives only in memory: it is never put in `localStorage`, the URL or a log, and it is cleared from the DOM on `viewhide`. When the admin comes back while the invite is still active, the box is replaced by the line "An invite is active · expires in {m:ss}" with "Create a new invite" and "Cancel invite" (8.7).
- **Invite input** (watch side): `<input type="text" class="emby-input shoal-input shoal-input--mono">` with `autocomplete="off"`, `autocapitalize="characters"`, `spellcheck="false"`, full width. Accept any case, spaces and hyphens (the server normalizes; the page only trims). Client check before submit: after removing spaces and hyphens it must start with `shoal1` and be at least 40 characters, else "That doesn't look like a Shoal invite. It starts with shoal1-."
- **Server ID** (`serverIdPrefix`): 8 lowercase hex characters, `code-sm` in fact lists and 13 px mono in meta lines. Never labelled "code" and never given a copy button.
- Copy buttons (quiet small, copy icon, "Copy code" / "Copy invite" / "Copy steps"). After copy: label "Copied" for 2 s and a polite announcement "Code copied" / "Invite copied" / "Steps copied".

### 6.11 Countdown
Text "Expires in 9:41" (`meta`, tabular) plus a 4 px bar (track `--shoal-surface-2`, fill `--shoal-accent`, width from `--shoal-progress`). Updates every second visually, computed from the server's `expiresUtc` (never a local timer started on click). Lifetimes: Quick Connect code 10 min, invite 15 min. Screen readers: the text has no live region; the page's polite region announces "5 minutes left", "1 minute left", "The code expired" (invite: "The invite expired").

### 6.12 Skeleton
Blocks in `--shoal-skeleton`, radius 6 (art 0). Shapes copy the real layout (card: art, name bar 40% width, via bar 70%, metrics strip, two lines, footer). Container `aria-busy="true"` and a visually hidden "Loading Shoal". Pulse off under reduced motion.

### 6.13 Poster tile
`<a class="shoal-poster" href="#/details?id={localItemId}&serverId={ApiClient.serverId()}">` wrapping `<figure>`: 2:3 image box (radius 8, poster shadow, `--shoal-surface-2` while loading), `<img alt="">` (the caption names it), source pill bottom-left on the scrim (share icon in `#33b6e3` + server name; for "Your copy", a check icon in `#5cc98f` + "Your copy"; these fixed colors are used in both sets because the pill is always dark). Caption: title `body` 15/700 two-line clamp; meta `meta` muted ("2010 · 15 min · from Hops"). Focus ring around the image box. Missing image: hatched box with the film icon and the title in `meta`.

### 6.14 Overlap bar
Height 16, radius 8, two segments with a 3 px gap. "In both": hatch `repeating-linear-gradient(135deg, var(--shoal-hatch-a) 0 3px, var(--shoal-hatch-b) 3px 7px)`. "New": `--shoal-accent`. Widths via `grid-template-columns: <both>fr <new>fr` set with `style.setProperty('--shoal-both', n)` and `--shoal-new`. `role="img"` with a full-sentence `aria-label`. Legend below: swatch 12 × 12 (radius 3, same fills) + text with numbers, so color is never the only key.

### 6.15 Live regions (one each per page, inside `.shoal-root`)
`<div class="shoal-sr" role="status" aria-live="polite" aria-atomic="true"></div>` and `<div class="shoal-sr" role="alert"></div>`. `.shoal-sr` is the standard visually-hidden pattern (clip, 1 px). Clear the message after 5 s so repeats are announced.

### 6.16 Page banner (v2; used by 8.8)
```html
<section class="shoal-banner shoal-banner--bad" aria-labelledby="shoal-banner-recovery-t">
  <svg class="shoal-icon shoal-banner__icon" aria-hidden="true" focusable="false"><use href="#shoal-i-x"/></svg>
  <div class="shoal-banner__text">
    <p class="shoal-banner__title" id="shoal-banner-recovery-t">Shoal couldn't read its settings</p>
    <p class="shoal-banner__body">…</p>
    <details class="shoal-collapse">…optional "How to fix it"…</details>
  </div>
  <div class="shoal-banner__actions">…buttons…</div>
</section>
```
- Look: background `--shoal-hint-bad-bg` (info variant: `--shoal-info-bg`), **full 1 px border** in `--shoal-hint-bad-border` (info: `rgb(var(--shoal-ink)/.24)`), radius 10, padding 16 20 (S 16), margin-bottom `space-6`. No left-border accent stripe. Icon 24 in `--shoal-bad` (info: `--shoal-link`). Title `subtitle` 18/24 700 in `--shoal-text`; body `body` in `--shoal-text`; actions are normal buttons (one primary at most).
- The title is a `<p>`, not a heading, so the view's `h1` stays the first heading. The view's `h1` gets `aria-describedby` = the banner title id while a banner shows.
- Variants: **bad** (recovery mode, keys can't be unlocked; not dismissible) and **info** (settings restored from a backup; v2.1: closes only when an admin presses "Confirm", 8.8).
- On first render after page load, the banner title is sent once to the alert region (bad) or the polite region (info). Polling never re-announces it.

### 6.17 Access switch (v2; owner D4; used by 8.4.1)
The 6.6 segmented control with a label, two options and a confirm step. It is one control, so "flip the switch" stays literally true, and both choices stay visible in plain words.
```html
<div class="shoal-access" role="group" aria-labelledby="acc-{m}-l">
  <p class="shoal-access__label" id="acc-{m}-l">Who can see {Library}</p>
  <div class="shoal-seg shoal-seg--access">
    <button type="button" aria-pressed="true"><svg …#shoal-i-check/>Only selected users</button>
    <button type="button" aria-pressed="false">Everyone on this server</button>
  </div>
  <p class="shoal-access__desc">Only the people you choose can see and play it. Everyone else won't know it's there.</p>
</div>
```
- Label `label` 14/700. Segments as 6.6 (min-height 44, `meta` 13, selected = 700 + 3 px accent underline), plus a 16 px check icon before the selected label so the state doesn't rest on the underline alone.
- On S (or whenever both labels don't fit on one line), the segments stack vertically, full width, with a 1 px control-border separator between them; the selected one keeps the check icon, the 700 weight and the same inset 3 px bottom bar as 6.6 (no side stripe).
- Description line under it (`meta`, muted) follows the current mode: Selected: "Only the people you choose can see and play it. Everyone else won't know it's there." Everyone: "Everyone with an account here can see and play it, including people you add later."
- Behavior: pressing the **unselected** segment opens the confirm dialog (8.4.4). Nothing changes until the admin confirms; on Cancel the pressed state stays as it was and focus returns to that segment. Pressing the selected segment does nothing. While saving: the pressed segment shows the spinning `sync` icon and `aria-disabled="true"` on both. Arrow keys are not used (simple toggle buttons, like 6.6), so moving focus never opens a dialog.

### 6.18 Approval card (v2, rebuilt in v2.1; share side; used by 8.2.4 step 3 and 8.7)
Shown when a friend's Shoal redeems your invite (DESIGN 4.1b step 3). **v2.1: the admin types the safety code; this side never shows it.** Reading a code off your own screen made "compare" easy to skip; typing it can't be skipped.
- Container: `.shoal-panel` with the selected outline (`--shoal-accent` border + 1 px accent shadow) so it reads as "your turn". `aria-labelledby` its title.
- Title (`h2`, in the wizard and in Friend detail): "{homeName} wants to connect". `homeName` is text from the friend's server (`textContent`, one line, ellipsis).
- Facts `dl` (two columns on L/M, stacked on S): "Server ID" `{homeServerIdPrefix}` (`code-sm`); "Connecting from" "a Tailscale address" / "a home network address" / "an internet address" (from `addressClass`).
- Body (`body`): "Ask {Friend} to read you the safety code on their screen, by phone, text or in person. Type it here to approve. If you weren't expecting this, choose Deny."
- Re-pair note, only when this friend has connected before (`deviceCount` > 0): `meta` muted "Approving replaces {Friend}'s current connection."
- **Safety code input** (6.10), label "Safety code from {Friend}" (`label` 14/700), help and format error as 6.10.
- Buttons: primary "Approve" and quiet "Deny".
  - "Approve" is `disabled` until the input holds 8 valid characters; the reason text sits under the button row (`meta`, muted): "Type the 8-character code to approve." Sends `POST Shoal/Admin/Shares/{id}/Approval {approve: true, sas}`. Busy "Checking…". The server alone decides whether the code matches.
  - "Deny" needs no code and is always available: `{approve: false}`.
- **Wrong code** (server answers no match, with `triesLeft`): the input gets `aria-invalid="true"` and the 6.7 error line "That code doesn't match. Check it with {Friend} and type it again. {n} tries left." ("1 try left"); the line is also sent to the alert region; the typed value stays (so the admin can spot the slip), selected, and focus returns to the input. No other change: the request is still pending.
- **Third wrong code**: the server cancels this request **and its invite** (DESIGN 4.1b step 3, v2.2: the invite is already bound to that one requester, so nobody else could use it; other invites are untouched). The card is replaced by the 8.2.5 share-side state "Request cancelled". The friend's Shoal shows `codeMismatch` (8.2.5).
- After Approve: status row clock + "Approved. Waiting for {homeName} to connect…" (the friend's server collects its key within 2 minutes), then ok "{Friend}'s server is connected." Failure and Deny outcomes: 8.2.5 share side.
- Polling (2.3) updates the facts only; it never clears, re-renders or refocuses the input.
- Arrival is announced politely ("{homeName} wants to connect. Ask {Friend} for the safety code."). Focus does not move (12.2).

### 6.19 Step checklist (v2; used by 8.2.8 and Stop sharing in 8.7)
`<ol class="shoal-checklist">`, one `<li>` per step: status icon 20 (`check` ok = done, `sync` = in progress, `x` bad = failed, a 24 px numbered circle on `--shoal-surface-2` = to do), text `body`, optional action on the right (quiet small button or a "Done" checkbox). Row min-height 48, gap 12, 1 px `--shoal-line` between rows. Each icon has visually hidden status text ("done", "working", "failed", "to do") so the state isn't carried by the icon alone. A failed row's text is `--shoal-bad` 700 for its first sentence.

---

## 7. Status model and iconography

Icons are inline SVG from the sprite in appendix A (stroke 1.75, 24 px grid, `currentColor`, round caps and joins). 20 px in buttons and ladders, 16 px in chips and meta lines. Always `aria-hidden="true"`; the text next to it carries the meaning.

| Server state (DESIGN) | Chip (icon, token, text) | Card treatment | Hint text (card) | Footer action |
|---|---|---|---|---|
| `Online`, path direct | `ok` ok "Online" | normal | none | none (or "Speed shown below" when selected) |
| `Online`, path DERP | `relay` warn "Online, relayed" | normal | "Traffic is relayed, so it's slower than a direct link. Ask {Server}'s admin to allow UDP 41641 on their router." | "Test link again" |
| `Online`, possibly relayed (heuristic) | `relay` warn "Online, maybe relayed" | normal | "This link may be relayed. To check, run tailscale ping {host} on your server." | "Test link again" |
| `Degraded` (unstable or slow) | `warn` warn "Online, unstable" | normal | "The link is unstable ({jitter} ms jitter, {loss}% loss), so playback may pause." | "Test link again" |
| `Offline` | `offline` off "Offline" | art desaturated, name `--shoal-muted`, metrics "—" | none; verdict line reads "Offline for {duration} · titles stay listed but won't play until it's back" | "Retry now"; meta "Next check in {m} min" replaces the titles count |
| `Disabled` | `pause` off "Paused" | art desaturated | "You paused {Server}. Its titles stay listed but won't play." | "Resume" |
| `AuthFailed` (revoked) | `keyoff` bad "Access removed" | normal art | bad hint: "{Server}'s admin removed your access, or the key stopped working. Pair again to keep watching." | "Pair again" |
| `IdentityMismatch` | `shield` bad "Different server" | normal art | bad hint: "The server at this address isn't the one you paired with, so Shoal stopped talking to it. If {Server} was reinstalled, pair again and compare the safety code." | "Pair again" |
| `TlsKeyChanged` (v2) | `shield` bad "Different server" | normal art | bad hint: "{Server}'s certificate changed, so Shoal stopped talking to it. That's normal after your friend renews it, and a warning sign if they didn't. Check with them before you trust it." | "Check certificate" (Server detail, certificate alert) |
| `Pairing` | `clock` info "Pairing" | hatched art "Waiting for {Server}" | none | "Continue pairing" (opens Pair) |
| `PairingAborted`, safety kind (v2.1: `pairingError` ∈ `requestReplaced`, `codesDidNotMatch`, `serverChanged`, `adminAccount`, `badPeerResponse`) | `shield` bad "Stopped for safety" | hatched art "Pairing stopped" | bad hint by `pairingError`: `requestReplaced` "Your pairing request was replaced. Tell your friend not to enter that code." · `codesDidNotMatch` "The safety codes didn't match, so Shoal saved nothing." · `serverChanged` "The server changed during pairing, so Shoal saved nothing." · `adminAccount` "That was an admin account, so Shoal refused it." · `badPeerResponse` "{Server} didn't answer like Jellyfin, so Shoal stopped." | "See what to do" (opens `view=pair&id=…`, 8.2.9) |
| `PairingAborted`, other kind (v2.1: every other `pairingError`, for example `expired`, `network`, `interrupted`, `quickConnectOff`, `quickConnectUnreachable`, `denied`; v2.2 adds `codeMismatch` and `sessionLimitBlocked`) | `pause` warn "Pairing stopped" | hatched art "Pairing stopped" | warn hint: the 8.2.5 title for that kind, for example "The code expired", "Lost contact with {Server}", "Shoal restarted before you confirmed", (v2.2) "Your friend chose Deny", "The safety code didn't match". `interrupted` uses the title for its `phase` (8.2.5) | "See what to do" |
| Sync running (overlay on any state) | `sync` info "Syncing" (spinner unless reduced motion) | normal | none | none |
| Alerts (`alertCount` > 0) | chip unchanged; a warn line above the footer, warn icon + the first that applies: "{heldRemovals} removals on hold", "{heldAdditions} new titles on hold", else "{alertCount} things to check" ("1 thing to check"); when more alerts exist than the line names, append " · {k} more". The line is a link to Server detail `#alerts` | normal | | |
| Keys can't be unlocked (banner 8.8 showing) | chip unchanged | normal | none (the banner explains) | "Pair again" on every card; verdict line "Not in use until Shoal can unlock its keys" |

Other status words used across screens:

| Context | Icon | Token | Text |
|---|---|---|---|
| Ladder: smooth | `check` | ok | "Smooth" |
| Ladder: may buffer | `warn` | warn | "Might pause at busy scenes" |
| Ladder: needs transcode | `warn` | warn | "Plays if {Server} transcodes it" |
| Ladder: will buffer | `x` | bad | "Won't play smoothly · needs about {n} Mbps" |
| Pairing: waiting (Quick Connect) | `clock` | info (`--shoal-link`) | "Waiting for {Server} to approve…" |
| Pairing: waiting (invite) | `clock` | info | "Waiting for your friend to approve on {Server}…" |
| Pairing: approved | `ok` | ok | "Approved. Connecting…" (Quick Connect, before the safety code check: "Approved. Shoal is connected to {Server}.") |
| Pairing: failed | `x` | bad | per error (8.2.5) |
| Pairing: friend's server not answering (v2.1, not final yet) | `warn` | warn | "Waiting for {Server} to answer…" |
| Pairing: getting a code (v2.1, Quick Connect) | `sync` | info | "Getting a code from {Server}…" |
| Pairing: stopped for safety | `shield` | bad | "Pairing stopped for your safety" |
| Pairing: stopped, other reason (v2.1) | `pause` | warn | "Pairing didn't finish" |
| Quick Connect device lock (Server detail) | `lock` / `warn` | ok / warn | "Lock confirmed" / "Ask your friend to lock the sharing account to Shoal's device" |
| Quick Connect session limit (v2.1, Server detail) | `lock` / `warn` | ok / warn | "Session limit 1" / "Ask your friend to set the session limit to 1" |
| Friend: active / paused / removed (`Active` / `Paused` / `AccessRemoved`) | `ok` / `pause` / `keyoff` | ok / off / bad | "Active" / "Paused" / "Access removed" |
| Friend: unexpected device (`unexpectedDeviceCount` > 0) | `warn` | warn | "{n} unexpected devices signed out" ("1 unexpected device signed out") |
| Device row: expected / unexpected | `server` / `warn` | ok / warn | "{Friend}'s server" / "Unexpected device signed out" |
| Invite | `clock` / `clock` | info / off | "Invite active · expires in {m:ss}" / "Invite expired" |
| Wrong invites tried by someone (v2.1, share side) | `info` | info (text color, icon `--shoal-link`) | "{n} wrong invite attempts in the last hour" |
| Approval waiting (share side) | `shield` | info | "Waiting for your approval" |
| Approval cancelled after 3 wrong codes (v2.1) | `x` | off | "Request cancelled" |
| Library access: `Everyone` | `eye` | ok | "Everyone on this server" |
| Library access: `Selected`, n ≥ 1 | `eye` | (text color, icon in `--shoal-link`) | "{n} people: {name}, {name} and {k} more" ("Only you" when the selection is just the current admin) |
| Library: nobody can see it (`Selected`, 0 people) | `eyeoff` | warn | "Hidden from everyone" |
| Library: held removals / additions | `warn` | warn | "{n} removals on hold" / "{n} new titles on hold" |
| Library: drift present | `warn` | warn | "Access changed outside Shoal for {n} people" |
| Stream protection mode (`Enforce` / `LogOnly` / `Off`) | `shield` | ok / warn / off | "Require sign-in" / "Log only" / "Off" |
| Stream protection errors (`guardErrors` > 0) | `warn` | warn | "{n} requests let through after an error" |
| Header summary | `ok` when all online, `warn` when any problem | ok / warn | "{online} of {total} servers online" |

---

## 8. Screens

Every screen below lists: purpose, layout, content and copy, states, focus order. Copy is final unless marked. `{Server}`, `{Friend}`, `{host}` are text inserted with `textContent`.

### 8.1 Overview (`view=overview`)
Purpose: health of every friend's server at a glance, how fast each link is, what you gain, and who watches from you. This is the approved mockup.

**8.1.1 Layout (top to bottom)**
1. Local nav.
2. Page header: `h1` "Shoal"; status line (`meta` 14, muted, items separated by a muted "·" that is `aria-hidden`): status icon + "{online} of {total} servers online" (ok color when all online; warn color + warn icon + "· {n} need attention" link when any card has a bad or warn state; the link focuses the first such card's name link) · "{n} titles shared with you" · clock icon + "Last sync {relative}" (while syncing: "Syncing now…"). Actions: quiet "Sync now" (sync icon), primary "Pair a server" (plus icon). Divider below.
3. Section "Friends' servers": `h2` + right note "Select a server to see its speed below" (`meta`, muted; hidden on S). Card grid.
4. Speed panel `id="shoal-speed"`: `h2` "Link speed" + inline Server select (`aria-label="Server"`, visually "Link speed: [Hops ▾]"; with one server, plain text "Link speed: Hops"). Subtitle "Last 24 hours · {t} tests and {p} plays, measured from your server". Segmented control "24 h" / "7 days". Left: KPIs ("Speed now", "Latency", "Lowest in 24 h" or "Lowest in 7 days"), chart (section 9), x-axis. Right: ladder `h3` "What plays from {Server}", 4 rows (4K remux, 4K, 1080p, 720p; each 46 px min-height, grid `30px 88px 1fr`), bottleneck note (`--shoal-surface-2`, radius 8, padding 12/14, arrow-up icon accent): text from DESIGN 7.3, for example "Limited by {Server}'s upload, not your connection." Link "Open full diagnostics" (to Server detail, `#speed`).
5. Overlap panel: `h2` "Library overlap with {Server}"; sentence (`body-lg`): "You both have **342 titles** (1.8 TB of your storage). {Server} adds **862 titles** (4.1 TB) you don't store."; bar; legend "In both libraries · 342 · 1.8 TB" / "New from {Server} · 862 · 4.1 TB"; link "Full report".
6. Section "Recently shared with you": `h2` + link "See all" (to Libraries & access). Poster shelf, newest first, up to 12 items (one row shows; the rest are hidden by the grid limit on L, and S shows 4).
7. Section "Friends watching from you" (always the **last** section of Overview, owner D6): `h2` + quiet button "Share with a friend" (to `view=pair&dir=share`). List rows (min-height 64), one per entry of `friends[]`: friend name (`name`, `body` 700) and meta "{libraryCount} libraries · {deviceCount} devices · last seen {lastSeenUtc, relative}" · chip from `status` (Active / Paused / Access removed) · "Playing now: {playingNow}" when > 0, with " ({transcodingNow} transcoding)" when `transcodingNow` > 0 · chevron. When `unexpectedDeviceCount` > 0 a second meta line in warn: warn icon + "{n} unexpected devices signed out". The whole row is one link to Friend detail (`<a class="shoal-row">`, no nested buttons).
   - v2.1, wrong invites (DESIGN 4.1b Limits): when `inviteFailures.notice` is true, an info hint (`role="note"`, `--shoal-info-bg`, info icon, full 1 px border as 6.16 info) sits between the `h2` row and the list: "Someone tried {n} wrong invites on your server in the last hour. Your invites still work, and Shoal is slowing those attempts down. Keep sending invites privately." No action, no alert count: nothing is broken and the admin has nothing to fix. It goes away when the hour has no burst.
8. Image credit line only when demo art is shown (lab); never in production data.

**8.1.2 Data** (`GET Shoal/Admin/Status`, section 13): per server card fields, recently shared list, friends list, overlap for the selected server, speed series for the selected server (separate call when the selection or range changes).

**8.1.3 Selection** (speed + overlap panels follow one selected server)
- Default selection: the first online server by display name; if none is online, the first server. Remembered per browser in `localStorage['shoal.selected']` (try/catch).
- Changing the Server select, or a pointer click on a card surface, sets the selection, updates both panels, updates the selected card styling and its footer note, and announces "Showing link speed for {Server}".

**8.1.4 Order of cards**: alphabetical by display name (stable; problems are surfaced by the header "need attention" link and the chips, not by reordering).

**8.1.5 States**
| State | What shows |
|---|---|
| First load | skeleton: header with 2 grey bars, 3 card skeletons (2 on M, 1 on S), panel skeletons. `aria-busy` on `.shoal-root` |
| Load failed | header shows; in place of the grid a bad hint box with `role="alert"`: "Shoal couldn't load. Your server didn't answer. Try again in a moment." + quiet "Try again". Never show a server error body |
| No servers and no friends (first run) | **Empty state** (below). Speed, overlap, posters and friends sections are not rendered |
| No servers, but friends watch from you | empty card area shows a compact version: "You aren't watching anyone's library yet." + primary "Pair a server"; friends section renders normally |
| Servers paired, no titles yet | poster section: "Nothing shared with you yet. Titles appear after the first sync." Overlap: "Overlap shows up after the first full sync." |
| Selected server has fewer than 2 speed samples | chart area shows existing points and the text "Not enough tests yet. Shoal tests every 6 hours." + quiet small "Test now" |
| Selected server offline | panel keeps the last data; chart shows the offline band; KPI "Speed now" shows "—" + "offline"; ladder hidden, replaced by "{Server} is offline. Last measured {relative}." |
| Parking on and a server parked (DESIGN 5.7 option) | poster placeholder card: offline icon + "{Server}'s titles are hidden while it's offline." caption "They come back on their own." |
| Sync running | "Sync now" busy; status line "Syncing now…"; cards of syncing servers show the Syncing chip |
| Sync cooldown | "Sync now" `aria-disabled`, helper "Available again in {m} min" |
| Action failed | inline under the actions: bad icon + "Sync didn't start. Try again in a moment." (`role="alert"`) |
| Recovery mode (v2) | banner (8.8), the `h1` and one muted line "Shoal is paused until its settings are fixed." Nothing else renders; "Sync now" and "Pair a server" are hidden |
| Keys can't be unlocked (v2) | banner (8.8); the page renders normally with every card's action set to "Pair again" (section 7) |
| A card is `TlsKeyChanged` or `PairingAborted` (v2) | card per section 7; counts toward "{n} need attention". v2.1: a `PairingAborted` card stays after reloads and restarts until the admin chooses Dismiss or pairs successfully (8.2.9). A brand-new server whose first pairing stopped has no titles: the footer's left side reads "Not paired" instead of the title count |
| A friend has unexpected devices (v2) | warn line on that friend's row; counts toward "{n} need attention" (the link then targets the friends section) |

**Empty state (first run)**, centered panel, max-width 640, padding 40/32:
- Shoal mark (appendix B), 120 × 48, `--shoal-accent`.
- `h2` "Watch your friends' movies from your own server"
- `body-lg`: "Shoal adds a friend's Jellyfin library next to yours. Nothing is copied, your friend doesn't need to install anything, and you choose who here can see it."
- Numbered list (`meta` 14, muted numbers in 24 px circles of `--shoal-surface-2`): "1. Your friend shares their server with you in Tailscale, or gives you its https address." "2. Pair: send them a code to approve, or paste the invite they sent if they have Shoal." "3. Check the safety code together, then try it yourself before you open it to everyone here." (Owner D7: the friend needs no plugin.)
- Primary "Pair a server"; quiet "Share with a friend".

**8.1.6 Focus order**: local nav (3) → "{n} need attention" link (if shown) → Sync now → Pair a server → for each card: name link → card action button (if any) → "Speed shown below" link (if selected) → Server select → 24 h → 7 days → "Test now" (if shown) → "Open full diagnostics" → "Full report" → "See all" → posters (each one stop) → "Share with a friend" → friend rows.

**8.1.7 Copy deck (Overview)**
| Element | Text |
|---|---|
| h1 | Shoal |
| Status | "{online} of {total} servers online" · "{n} need attention" · "{n} titles shared with you" · "Last sync {relative}" / "Syncing now…" / "Not synced yet" |
| Buttons | "Sync now", "Pair a server" |
| Section | "Friends' servers"; note "Select a server to see its speed below" |
| Card | "Latest from {Server}: {Title}", "No pictures yet", "Offline", "via Tailscale", "via home network", "via internet (HTTPS)", "through proxy", metrics "Connection" / "Latency" / "Speed", values "Direct" / "Relayed" / "Proxy" / "None", "{n} titles shared", "Next check in {m} min", "Speed shown below", "Test link again", "Retry now", "Pair again", "Resume", "Continue pairing", "Check certificate", "See what to do", "Pairing stopped", "Not paired", "{n} removals on hold", "{n} new titles on hold", "{n} things to check", "· {k} more", "Not in use until Shoal can unlock its keys"; chip and hint texts in section 7 |
| Speed panel | "Link speed", "Last 24 hours · {t} tests and {p} plays, measured from your server" ("Last 7 days · …"), "24 h", "7 days", "Speed now", "Latency", "Lowest in 24 h", "Lowest in 7 days", "4K needs about 30 Mbps", "1080p needs about 15 Mbps", "What plays from {Server}", "4K remux", "4K", "1080p", "720p", "Open full diagnostics", "Not enough tests yet. Shoal tests every 6 hours.", "Test now" |
| Overlap | "Library overlap with {Server}", sentence above, "In both libraries", "New from {Server}", "Full report" |
| Posters | "Recently shared with you", "See all", caption "{year} · {runtime} min · from {Server}", your copy caption "Also on {Server} · your copy plays", pill "Your copy" |
| Friends | "Friends watching from you", "Share with a friend", "{k} libraries · {d} devices · last seen {relative}", "Playing now: {n}", "({t} transcoding)", "{n} unexpected devices signed out" / "1 unexpected device signed out", empty "No one watches from your server yet. Share a library and it shows up on their server, next to theirs.", v2.1 notice "Someone tried {n} wrong invites on your server in the last hour. Your invites still work, and Shoal is slowing those attempts down. Keep sending invites privately." |

### 8.2 Pair a server (`view=pair`)
Purpose: connect two servers safely, in either direction, with no passwords exchanged. Back link "Overview". `h1` "Pair a server". Single column, max-width 720.

Data (DESIGN 9.6; field names in 13). Watch direction: `POST Shoal/Admin/Peers/Probe`, then the pairing session `POST` / `GET` / `DELETE Shoal/Admin/Pairing` and, on the Quick Connect path only, `POST Shoal/Admin/Pairing/Confirm` (legal only in AwaitingConfirm, DESIGN 4.2a), then `GET .../Peers/{h}/RemoteLibraries` and `POST .../Peers/{h}/Libraries`. Share direction: `POST Shoal/Admin/Shares`, `POST .../Shares/{id}/Invites`, `DELETE .../Shares/{id}/Invites/current`, `GET` / `POST .../Shares/{id}/Approval`.

**8.2.1 Step indicator**: an ordered list `<ol class="shoal-steps" aria-label="Pairing steps">`; current step `aria-current="step"`, 700 weight, accent underline; done steps show the check icon + muted text; future steps muted. Steps:
- Watch a friend's library: "1 Connect", "2 Check the server", "3 Pair", "4 Choose what to add".
- Share your library: "1 Who and what", "2 Invite", "3 Approve".
On S, only "Step 2 of 4 · Check the server" is shown as text.

**8.2.2 Step 0: direction** (skipped when opened from "Share with a friend", which passes `dir=share`)
- `h2` "What do you want to do?"
- Two radio choice cards: **"Watch a friend's library"** / "Their movies show up in your library. They keep the files, and they don't need Shoal." and **"Share your library with a friend"** / "Your movies show up in their library. Your friend needs Shoal too. You choose which libraries and can stop anytime."
- Note (`meta`, muted): "To do both, pair once in each direction."
- Primary "Continue" (disabled until a choice is made; reason text "Choose one to continue").

**8.2.3 Watch a friend's library** (Home role; DESIGN 4.2, 9.3)

*Step 1 "Connect".* `h2` "How will you connect?"
- Choice cards (`method`): **"My friend sent me an invite"** / "They have Shoal and made an invite for you." (`invite`) and **"I have their server address"** / "Your friend doesn't have Shoal. You'll send them a code to approve." (`qc`, Quick Connect).
- Fields, in this order:
  - Invite path only: "Invite" (invite input, 6.10), help "Starts with shoal1-. Your friend makes it in Shoal under Share with a friend. It works once, for 15 minutes."
  - "Name for this server" (≤ 64 chars, counter shown from 50 on: "12 left"), help "Your users see it in library names, like Hops Movies." (`displayName`)
  - "Server address", `type="url"`, `inputmode="url"`, help "For example http://hops-jellyfin.tail1234.ts.net:8096". Invite path help adds: "Your friend's server address. The invite only works there." (`url`)
  - "How you reach it" select (`network`): "Tailscale (private)" (`Tailnet`) / "Home network (same house)" (`LAN`) / "Internet (https only)" (`Public`). Help changes with the choice: Tailscale "Encrypted by Tailscale. Plain http is fine here." · Home network "Not encrypted. Use Tailscale if you can." · Internet "Needs an https address with a valid certificate."
  - Home network with an `http://` address only: a required checkbox row (`lanAck`) "I understand Shoal's key travels unencrypted on my home network" / note "Anyone on your network could copy it. Tailscale avoids this."
  - `details` "Advanced" → "Proxy (optional)" (`proxyUrl`), help "Only if Tailscale on this server runs in userspace mode, for example socks5://127.0.0.1:1055". Tailscale only; hidden for the other two choices.
- Primary "Check server" → busy "Checking…" (`POST Shoal/Admin/Peers/Probe {url, network, proxyUrl?, invite?, lanAck?}`).
- Errors (inline on the field unless noted):
  | Case | Text |
  |---|---|
  | empty name / address / invite | "Enter a name." / "Enter the server address." / "Paste the invite your friend sent." |
  | invite malformed (client check, 6.10) | "That doesn't look like a Shoal invite. It starts with shoal1-." |
  | bad scheme | "Use an address that starts with http:// or https://." |
  | Internet + http | "Internet servers need an https:// address." |
  | Home network + http, box not ticked | "Tick the box to continue, or use Tailscale." |
  | blocked address (SSRF policy) | "Shoal can't connect to that address. It points to this server itself or to a private service." |
  | wrong network class | "That address isn't a Tailscale address. Change How you reach it, or check the address." (adapt per class) |
  | proxy not allowed | "A proxy only works with a Tailscale address (a .ts.net name or a 100.x address)." |
  | unreachable (form-level) | "Couldn't reach that server. Check the address, and that Tailscale is running on this server." |
  | certificate | "That server's certificate isn't trusted, so Shoal won't connect. Your friend needs a valid certificate." |
  | not Jellyfin | "That address answered, but it isn't a Jellyfin server." |
  | invite for a different server (the invite's server prefix doesn't match the probe; Shoal never sends the invite there) | "That invite is for a different server. Check the address, or ask your friend for the right invite." (on the invite field) |
  | v2.1: invite path + Home network + `http://` (DESIGN 4.2 step 1: an invite is never sent unencrypted outside Tailscale). Checked in the page as soon as all three are set, and again by the server | "Invites need Tailscale or an https address, so nobody on the network can copy them. On a home network over http, choose I have their server address instead." (on the address field; the LAN acknowledgement row is hidden on the invite path) |
  | rate limited | "Too many tries. You can try again in {m} min." |

*Step 2 "Check the server".* `h2` "Is this the right server?"
- Server facts (`dl`, two columns on L): "Server name" {serverName}, "Jellyfin version" {version}, "Address" {url}, "Latency" {rttMs} ms, "Server ID" {serverIdPrefix} (`code-sm`). Under the list (`meta`, muted): "The Server ID lets Shoal notice later if this address leads to a different or reset server. It isn't a safety code."
- **No safety code at this step** (DESIGN 9.3: nothing exists yet that the friend could compare).
- Warnings (warn hints, from `warnings[]` and the chosen path):
  - `versionTested` false: "Shoal hasn't been tested with Jellyfin {version} yet. It will probably work."
  - Home network: "Home network connections aren't encrypted."
  - Internet + Quick Connect (DESIGN 4.3 residual risk, 12 question 2 default): "Strangers on the internet can try to grab Quick Connect codes. If your friend installs Shoal and sends you an invite, pairing is safer."
- Quick Connect path note (`body`): "Start when your friend is ready. They'll have 10 minutes to enter the code."
- v2.1, Quick Connect re-pair note (when this server was paired before, i.e. "Pair again" or "Start again" on a known server): info hint "Pairing again? Ask your friend to set the sharing account's session limit to 0 first, and back to 1 once you're paired. If your friend sees an error when they approve, the session limit is probably still at 1. Ask them to set it to 0, then try again." (DESIGN v2.2 corrected this: deleting the old Shoal device isn't enough. v2.2, Pilot-02: Jellyfin shows the friend only a generic error, so the copy names the likely cause instead of quoting a message.)
- v2.1, **new sharing account required** (when the last outcome for this server was `requestReplaced` or `codesDidNotMatch`; DESIGN 4.2a "the next attempt must target a NEW sharing account"): a warn hint "Last time, someone else may have got in with the old sharing account. Only a new account is safe." and a required checkbox row "My friend deleted the old sharing account and made a new one" / note "Steps 2 and 3 of the steps for your friend." with a link-button "Show the steps" that opens 8.2.8 in a dialog with "Copy steps". "Start pairing" stays `disabled` until ticked; reason text "Tick the box once your friend has made a new account."
- Buttons: primary "Start pairing" (`POST Shoal/Admin/Pairing {probeHandle, displayName, method}`; busy "Starting…"), quiet "Back". This click is the only thing that ever asks the friend's server for a code (DESIGN 4.2a); arriving on this step, reloading, or coming back after an error never does.
- v2.1 errors on "Start pairing" (inline under the buttons, `role="alert"`): `409` session already live: "Another pairing is already in progress." + link "Go to it" (8.2.6). Check expired (`probeExpired`, the check is single-use and lasts 10 minutes): "That check is more than 10 minutes old. Checking again…", then the page re-runs the Probe (not pairing) and stays on this step with fresh facts. Rate limited: "Too many tries. Wait {m} min, then try again." with the 6.2 cooldown on the button.

*Step 3 "Pair", invite path.* (v2.1: the friend types this code; there is no "The codes match" here, DESIGN 4.2 step 3.)
- `h2` "Read this code to your friend"
- Body: "Call or text your friend and give them this code. They type it into Shoal on {Server} to approve. If what they type doesn't match, their server won't approve."
- Safety code box (6.10, 8 characters, `safetyCode`) + quiet small "Copy code".
- Status row: clock icon + "Waiting for your friend to approve on {Server}…" + countdown (`expiresUtc`, 15 min).
- `meta` muted: "If your friend says they weren't asked for a code, or don't see a request from {Home}, cancel and check the address with them."
- Link "Cancel pairing" (`DELETE Shoal/Admin/Pairing`). No other buttons: a wrong code simply never approves.
- On approval: status row ok "Approved. Connecting…", then step 4. v2.2 (DESIGN 4.2 step 3): if the friend chose Deny, this side shows 8.2.5 `denied`; if they typed a wrong code 3 times, their server ends the request and the invite, and this side shows 8.2.5 `codeMismatch`. Both need a new invite.

*Step 3 "Pair", Quick Connect path.* Two phases on one step.
- **Before phase A, getting a code** (v2.1, DESIGN 4.2a Preflight, Initiating, Baseline; normally under a second): `h2` "Send this code to your friend" with the code box area showing a 6.12 skeleton block and the status row `sync` + "Getting a code from {Server}…" (`aria-busy` on the step body). The code is **not shown** until Shoal has confirmed it with the friend's server, so nobody relays a code that was never real. Failures here are `quickConnectOff` or `quickConnectUnreachable` (8.2.5), never `requestReplaced`.
- **Phase A, waiting for approval:**
  - `h2` "Send this code to your friend"
  - Quick Connect code box (6.10; the session's `code` field, 6 digits) + "Copy code"; countdown (`expiresUtc`, 10 min).
  - Next to it (under it on S): the safety code box (6.10, 4 characters, `safetyCode`) with `meta` "After your friend approves, they'll see Shoal: {Home} ({safetyCode}) in their Devices list. You'll compare it then." `{Home}` is this server's name (see 14).
  - `details` "Steps for your friend" (open the first time a browser sees this step; remembered in `localStorage['shoal.qcStepsSeen']`, try/catch): the checklist in 8.2.8 + quiet small "Copy steps" (plain text to the clipboard).
  - Status row: clock icon + "Waiting for {Server} to approve…". v2.1: after 3 missed polls in a row (session flag `peerSilent`, DESIGN 9.5) the row turns warn: warn icon + "Waiting for {Server} to answer…" and `meta` "Shoal keeps trying for 30 seconds." It returns to the clock row by itself when the server answers; after 30 s the outcome is `network` (8.2.5). Not announced unless it becomes final.
  - v2.2, re-pairs only (session `paired` true): a `meta` line under the status row: "If your friend sees an error when they approve, the session limit is probably still at 1. Ask them to set it to 0, then try again." Static text, not a state: Shoal can't see the friend's error while the code is live.
  - Link "Cancel pairing". There is **no** "Get a new code" while a code is live: Shoal never swaps a code silently (DESIGN 4.3). A new attempt is offered only after the session ends (8.2.5).
- **Phase B, after approval** (session `state` AwaitingConfirm, waiting for `Confirm`; DESIGN 4.2a: Shoal holds the key in memory only and saves nothing until the admin confirms):
  - Status row ok: "Approved. Shoal is connected to {Server}." + countdown "Confirm within {m:ss}" (`expiresUtc`, 15 min; at 0 the outcome is `expired` and Shoal signs out of {Server}).
  - `h2` "Compare the safety code"
  - Body: "Ask your friend to open Dashboard > Devices on {Server} and find **Shoal: {Home} ({safetyCode})**. The code in brackets must match this one, and it must be the only Shoal device for that account."
  - Safety code box (4 characters).
  - Body: "Then your friend locks the sharing account to that device: in the account's settings, untick Enable access from all devices, tick only the Shoal device, and save."
  - Buttons: primary "The codes match" (`{match: true}` → step 4; busy "Saving…"), quiet "They don't match" (`{match: false}` → 8.2.5 `codesDidNotMatch`). These two buttons exist **only** on the Quick Connect path, and only in this phase. A `409` from Confirm (the session is no longer waiting) reloads the session and shows whatever state the server reports.

*Step 4 "Choose what to add".*
- `h2` "Choose what to add"; body "Pick libraries from {Server}. At first only you can see them, so you can try them. When they work, open them to everyone here in Libraries & access."
- One row per remote library (`.shoal-check`, 56 px): checkbox, name (`body` 700), meta "{type} · {count} titles". `selectable` false: disabled, note "TV shows are coming in a later version."
- For each ticked library, an indented block appears:
  - Field "Name in your library", default "{Server} Movies".
  - "Who can see this": summary line (eye icon) "Only you" (the admin running the wizard is pre-selected, owner D3) or "{n} people: You, Sam and 1 more"; quiet small "Choose people" → picker (8.4.3), where the current admin's row carries the note "You". "Everyone on this server" is **not** offered here (owner D4: the switch lives on the library, after you've tested it).
  - When `count` > 5,000: a required checkbox row "Add all {count} titles" / note "The first sync takes a while and saves about {GB} GB of pictures." ({GB} = count × 0.2 MB at Standard picture quality, one decimal.)
- Primary "Add and start syncing" (`POST .../Peers/{h}/Libraries`; disabled until at least one library is ticked and every required box is ticked; reason text "Pick at least one library"). Allowed with nobody chosen; then the library is created hidden and the success screen says so.

*Success.* Shoal mark; `h2` "{Server} is paired"; progress line "Adding titles: {done} of {total}" with a 4 px progress bar (`role="progressbar"`, `aria-valuenow`, `aria-valuemax`, label "Adding titles"); body "You can leave this page. Syncing keeps going in the background." If nobody can see a new library: warn hint "No one can see {Library} yet. Choose people in Libraries & access." Quick Connect pairings add an info hint (v2.1, steps 7 to 9 of 8.2.8): "Remind your friend to finish: sign out of the private window, lock the sharing account to Shoal's device, then set its session limit to 1. {Server}'s page shows Lock confirmed and Session limit 1 once they have." + quiet small "Copy steps" (copies steps 7 to 9 only). Buttons: primary "Go to Overview", quiet "Open {Server}".

**8.2.4 Share your library with a friend** (Peer role with Shoal; DESIGN 4.1b, 9.3). Invite-only: there is no code to enter on this side.
- *Step 1 "Who and what".* Fields: "Friend's name" (≤ 64; `friendName`), help "Shown only to you."; "Libraries to share" checkbox rows (`libraryIds`; your libraries; movies only, others disabled with the same note; libraries Shoal made from other friends' servers disabled with note "Libraries from friends' servers can't be shared on."); "Highest quality per stream" select (`maxBitrateMbps`): "No limit", "40 Mbps (4K)", "20 Mbps (1080p, high)", "10 Mbps (1080p)", "4 Mbps (720p)"; "Transcodes at once" number 1-10, default 4 (D15) (`maxConcurrentTranscodes`), help "How many videos {Friend}'s server can ask yours to convert at the same time. Converting uses your CPU. This is a limit, not a hard cap: audio and subtitle conversions aren't counted." (v2.1, DESIGN 8.4 (c) best-effort) Note box (info): "Shoal creates a hidden sharing account for {Friend} with a random password nobody ever sees. It can only watch these libraries, can't change anything, and you can remove it anytime." Primary "Continue" (`POST Shoal/Admin/Shares`).
- *Step 2 "Invite".* `h2` "Invite {Friend}"
  - Required radio pair (`fieldset`, legend "How will {Friend}'s server reach yours?"): "I shared this server with {Friend} in Tailscale" / "My server has a public https address".
  - Primary "Create invite" (`POST .../Shares/{id}/Invites`) → the one-time invite display (6.10) with "Copy invite", countdown (15 min) and the shown-once warning.
  - Body: "Send it to {Friend} privately. In Shoal they choose Pair a server, then My friend sent me an invite, and enter it with your server's address. Their screen then shows a safety code, and you type it here to approve."
  - Status row: clock icon + "Waiting for {Friend} to use the invite…"
  - Buttons: quiet "Create a new invite" (the old one stops working), link "Cancel invite" (`DELETE .../Invites/current`).
  - When the friend's server redeems the invite (poll, 2.3), the wizard moves to step 3 by itself.
- *Step 3 "Approve".* The approval card (6.18), with the safety code input. Approve with the right code → "Approved. Waiting for {homeName} to connect…" → Done. Wrong code: inline on the card (6.18). Deny, three wrong codes and failures: 8.2.5 share side.
- *Done.* Shoal mark; `h2` "{Friend} can now watch from your server"; body "They'll see {k} libraries. You can pause or stop sharing on {Friend}'s page." Buttons primary "Go to Overview", quiet "Open {Friend}".

**8.2.5 Pairing outcome states** (shown in place of the step body; `h2` + body + actions; the message container gets `role="alert"` for failures). Watch side, keyed by the session's `state` and `error` (DESIGN 9.5 data shape and 4.2a; since v2.2 the two agree, and the error list below is DESIGN's complete list). **Kind** says how section 7 shows a stored outcome on the Overview card: *safety* = shield, bad, "Stopped for safety"; *stopped* = pause, warn, "Pairing stopped".
| State | `state` / `error` | Kind | Icon / token | Title | Body | Actions |
|---|---|---|---|---|---|---|
| Waiting | waiting | | clock / info | "Waiting for {Server} to approve" (invite: "Waiting for your friend to approve on {Server}") | countdown | "Cancel pairing" |
| Not answering, still trying (v2.1) | waiting + `peerSilent` | | warn / warn | "Waiting for {Server} to answer…" | "Shoal keeps trying for 30 seconds." | "Cancel pairing" |
| Approved | approved | | ok / ok | "Approved" | "Connecting to {Server}…" | none |
| Success | done | | mark | "{Server} is paired" | 8.2.3 success | as above |
| Expired | `expired` | stopped | clock / warn | "The code expired" (invite: "The invite expired") | Quick Connect: "Codes last 10 minutes. Start again when your friend is ready." Invite: "Invites last 15 minutes. Ask your friend for a new one, then start again." After approval (no Confirm in 15 min): "You didn't confirm within 15 minutes, so Shoal signed out of {Server} and saved nothing. Start again when you're both ready." (v2.2: a restart of this server is no longer shown here; see `interrupted`.) | primary "Start again", quiet "Dismiss" |
| Friend chose Deny (v2.2 wording) | `denied` | stopped | x / off | "Your friend chose Deny" | "Your friend turned down the request on {Server}, so nothing was saved. If that was a mistake, ask them for a new invite, then start again." | primary "Start again", quiet "Dismiss" |
| **Code didn't match** (v2.2, invite path only) | `codeMismatch` | stopped | x / warn | "The safety code didn't match" | "Your friend typed a code that didn't match yours 3 times, so their server cancelled the request and the invite. Nothing was saved. Call them and read the code out slowly, one group at a time. Then ask for a new invite and start again. If it still doesn't match, stop and check the server address with them first." | primary "Start again", quiet "Dismiss" |
| Invite didn't work | `inviteInvalid` | stopped | x / bad | "That invite didn't work" | "It may have expired, been used already, or been cancelled. Ask your friend for a new one." | primary "Start again" |
| Refused: admin account | `adminAccount` | safety | shield / bad | "That account is an admin on {Server}" | "Shoal only accepts a limited sharing account, so it signed that account out and saved nothing. Ask your friend to approve the code from the sharing account they made for you." | primary "Start again", quiet "Dismiss" |
| Refused: server changed | `serverChanged` | safety | shield / bad | "The server changed while pairing" | "The server at this address isn't the one you checked. Shoal saved nothing. Check the address with your friend, then start again." | primary "Start again", quiet "Dismiss" |
| **Codes didn't match** (Quick Connect only in v2.1; the invite path has no compare button) | `codesDidNotMatch` | safety | shield / bad | "Pairing stopped for your safety" | "The safety codes didn't match, so another device may have taken your place. Shoal signed out of {Server} and saved nothing. Ask your friend to delete the sharing account in Dashboard > Users and make a new one. Then start again." | primary "Start again" (step 2 then asks for the new account, 8.2.3), quiet "Dismiss" |
| **Request replaced** (hijack stopped) | `requestReplaced` | safety | shield / bad | "Pairing stopped for your safety" | "Your pairing request was replaced, or {Server} restarted. Tell your friend not to enter code {code}. If they already did, they should delete the sharing account and make a new one. Shoal saved nothing." | primary "Start again" (new account required, 8.2.3), quiet "Dismiss" |
| Unexpected answer (v2.1) | `badPeerResponse` | safety | shield / bad | "Pairing stopped for your safety" | "{Server} answered in a way Jellyfin never does, so Shoal stopped and saved nothing. Check the address with your friend, then start again." | primary "Start again", quiet "Dismiss" |
| Quick Connect off | `quickConnectOff` | stopped | warn / warn | "Quick Connect is off on {Server}" | "Ask your friend to turn on Quick Connect in Dashboard > General on {Server}. Then start again." | primary "Start again", quiet "Dismiss" |
| Session limit blocked it (v2.2, Quick Connect only; Pilot-02) | `sessionLimitBlocked` *(proposed name; Kiro is adding this kind, use Kiro's name)* | stopped | lock / warn | "Your friend's server didn't accept the code" | "If your friend sees an error when they approve, the session limit is probably still at 1. Ask them to set it to 0, then try again. Nothing was saved. Once you're paired, they set it back to 1." | primary "Start again", quiet "Dismiss" |
| Quick Connect not found (v2.1) | `quickConnectUnreachable` | stopped | offline / off | "Couldn't find Quick Connect on {Server}" | "Shoal couldn't reach Quick Connect at this address. Check the address with your friend, including any /jellyfin at the end. Then start again." | primary "Change the address" (step 1 with the fields kept), quiet "Dismiss" |
| Restarted before approval (v2.2) | `interrupted`, `phase: beforeApproval` | stopped | clock / warn | "The code expired" (invite: "The invite expired") | Quick Connect: "Your server restarted before your friend approved, so Shoal saved nothing. The code may still work on {Server} for a few minutes, so if your friend entered it anyway, ask them to delete the device named Shoal: {Home} in Dashboard > Devices. Nobody can use it, but it shouldn't stay there. Then start again." Invite: "Your server restarted while waiting for your friend, so this invite can't be finished and Shoal saved nothing. If your friend approves it anyway, their Shoal signs that connection out within 2 minutes. Ask them for a new invite, then start again." | primary "Start again", quiet "Dismiss" |
| Restarted before you confirmed (v2.1; v2.2 adds `phase`) | `interrupted`, `phase: awaitingConfirm` (Quick Connect only) | stopped | clock / warn | "Shoal restarted before you confirmed" | "Your server restarted while Shoal waited for you to compare the codes, so it saved nothing. Ask your friend to delete the device named Shoal: {Home} in Dashboard > Devices on {Server}. They can keep the same sharing account. Then start again." | primary "Start again", quiet "Dismiss" |
| Too many tries | `rateLimited` | stopped | clock / warn | "Too many tries" | "Wait {m} min, then try again. Nothing was saved." (from `Retry-After`) | primary "Try again" with the cooldown state (6.2) |
| Network error | `network` | stopped | offline / off | "Lost contact with {Server}" | "Shoal couldn't reach {Server} for 30 seconds while pairing, so it stopped and saved nothing. Check the connection, then start again." | primary "Start again", quiet "Dismiss" |
| Cancelled | (404 after `DELETE`) | | none | (returns to Overview with polite announcement "Pairing cancelled") | | |
| Server error | `server` | stopped | x / bad | "Pairing didn't work" | "Something went wrong on your server. Try again in a moment." (never a peer error body) | primary "Start again" |

- **"Start again"** returns to step 2 "Check the server" with the earlier answers kept, re-runs the check (a Probe, never a pairing request), and waits for the admin to press "Start pairing". That keeps DESIGN 4.2a's rule: one click, one new session, and Shoal never restarts pairing on its own. After `requestReplaced` and `codesDidNotMatch`, step 2 shows the new-account checkbox (8.2.3). On the invite path "Start again" opens step 1 instead, with the address kept and the invite field empty, because every attempt needs a new invite.
- **"Dismiss"** appears only when the outcome is stored on a card (`pairingError` set, 8.2.9). It clears the card (`POST Shoal/Admin/Peers/{h}/DismissPairing`, DESIGN 9.6 v2.2; it answers `{removed: true}` or `{restored: true, status}`) and returns to Overview with polite "Removed from your list" for a server that was never paired, or "{Server} is back to how it was" for a re-pair.
- v2.2, **`denied` versus `codeMismatch`**: both are ordinary stops (pause, warn on the card, no pop-up). They are kept apart so the admin knows what to do next: after Deny, talk to your friend; after a mismatch, read the code again more carefully. The mismatch copy ends with a calm "check the address" line because a code that differs twice can mean the request reached the wrong server; it never says "attack".
- v2.2, **`interrupted` before approval** picks its copy by pairing method: the live session's `method`, or for a stored outcome the peer's `pairingMethod` (13). If neither is known, use the Quick Connect copy, the one that gives the friend something to check.
- `requestReplaced` copy follows DESIGN 4.2a ("delete", not 9.5's older "disable"), with "your friend" because the watch side only knows the server's name, and with the code number added so the friend knows which code to ignore.

Share side (wizard step 2-3 and Friend detail; data from `GET .../Shares/{id}/Approval` and the share's invite state, 13). v2.1: "Invites cancelled after failed attempts" is gone. Wrong invites from strangers never cancel anything; the calm notice lives on Overview and Settings (8.1.1 item 7, 8.5 item 7).
| State | Icon / token | Title | Body | Actions |
|---|---|---|---|---|
| Waiting for the invite to be used | clock / info | "Waiting for {Friend} to use the invite" | countdown | "Create a new invite", "Cancel invite" |
| Invite expired | clock / warn | "The invite expired" | "Invites last 15 minutes. Create a new one when {Friend} is ready." | primary "Create a new invite" |
| Approval waiting | shield / info | approval card (6.18) | | "Approve" (needs the typed code), "Deny" |
| Wrong code, tries left (v2.1) | inline on the card (6.18) | | "That code doesn't match. Check it with {Friend} and type it again. {n} tries left." | the card stays |
| Request cancelled after 3 wrong codes (v2.1; approval `state` `cancelledWrongCode`, confirmed in DESIGN 9.5 v2.2) | x / off | "Request cancelled" | "The safety code didn't match 3 times, so Shoal cancelled this request and its invite to be safe. Nothing was shared. Call {Friend}, check the code together, then create a new invite." | primary "Create a new invite" |
| Approved, connecting | clock / info | "Approved" | "Waiting for {homeName} to connect…" | none |
| Connected | ok / ok | "{Friend} can now watch from your server" | Done screen | as Done |
| Not collected in 2 minutes | x / bad | "{homeName} didn't finish connecting" | "Shoal signed that device out to be safe. Create a new invite and try again." | primary "Create a new invite" |
| You denied it | x / off | "You denied the request" | "Nothing was shared." | primary "Create a new invite" |

**8.2.6 Leaving and resuming** (DESIGN 9.5 #5): there is exactly one pairing session per direction and target: one watch-side session on this server, one share-side approval per friend. It lives in server memory, survives page reloads and navigation, keeps polling the friend without the page, and ends with its code or invite (10 or 15 min). `GET Shoal/Admin/Pairing` returns the current session or 404; `DELETE` cancels it. Opening `view=pair` while a session exists skips step 0 and shows an info banner above the step body: "Pairing with {Server} is still in progress." with primary "Continue" (jumps to the server-reported step) and link "Cancel pairing". A restart of this server loses the session. v2.2 (DESIGN 4.2a "Abort is durable"): Shoal remembers how far the pairing got, so after the restart the server shows `interrupted` with its `phase`, stored on the card (8.2.9): before approval it reads "The code expired" (invite: "The invite expired") with the stray-device hint on the Quick Connect path; after approval, while waiting for "The codes match", "Shoal restarted before you confirmed" (8.2.5). While a session lives, that server's address, network and proxy can't be edited (8.3.7). Share side: invites and approvals are memory-only too; a restart cancels them, and Friend detail shows "Invite expired".

**8.2.7 Focus**: on each step change, focus the step's `h2`. Countdown announcements per 6.11. Pairing status changes are announced politely ("Approved. Connecting to Hops."; "{homeName} wants to connect. Ask {Friend} for the safety code."); failures use the alert region. A wrong typed code uses the alert region and keeps focus in the code input (6.18). Polling never moves focus.

**8.2.8 Steps for your friend** (v2, rewritten in v2.1; Quick Connect path; DESIGN 4.1 and 4.5 in plain words). Written for a friend who has never opened a Jellyfin policy page: one action per sentence, no field names from code. Shown with the step checklist (6.19, all "to do") and copied as plain text by "Copy steps". Dashboard labels vary between Jellyfin versions; the README carries screenshots. Used in: Quick Connect phase A ("Steps for your friend"), the new-account dialog of step 2 (8.2.3), Server detail "How" (8.3.1, steps 8 and 9), and the success hint (steps 7 to 9).

**Ship gate.** Step 9, the session-limit sentence in step 5 and the "To pause" paragraph ship only after Pilot's lab check T-SEC-22 passes (DESIGN "Open after v2.1" #1). If the lab finds the limit behaves differently, Lumen rewrites them before release.

1. "Give me access to your Jellyfin server: share it with me in Tailscale (give the Jellyfin computer its own Tailscale machine), or send me its https address."
2. "In Dashboard > Users, add a new user for me, for example shoal-friend, with a long random password. You'll type it only once. Under library access, untick All libraries and tick only the movie libraries you want to share."
3. "Open that user's settings, set these, and save." The row shows three short groups (bold lead-in, then plain text), matching DESIGN 4.1 step 3 *Set these*:
   - **Turn on:** "Hide this user from the sign-in screen. Allow remote connections. Allow media playback. Allow video and audio playback that needs transcoding, and video that only needs remuxing."
   - **Turn off:** "Administrator. Access to all libraries (tick only the ones you share) and to all channels. Deleting media. Downloading media. Live TV and recording management. Managing collections and subtitles. Remote control of other users and of shared devices. Set SyncPlay to no access."
   - **Leave for now:** "Maximum number of simultaneous user sessions stays at 0. You'll change it in step 9."
   - Then an info note inside the same row (`role="note"`, `--shoal-info-bg`, info icon, not a to-do, no checkbox), title **"Jellyfin has no switch for these"** (DESIGN 4.1 step 3 *Cannot be set*): "You can't turn off this user's ability to change its own password, because Jellyfin has no setting for it. That's fine: step 9 stops a second sign-in while my server is connected, and deleting the user always ends sharing completely. Three other options, media conversion, sync transcoding and public sharing, have no switch either. Jellyfin doesn't use them."
4. "In Dashboard > General, turn on Quick Connect."
5. "When I send you a 6-digit code: open a private browser window, sign in as that user, go to Settings > Quick Connect, and enter the code. If you see an error when you enter it, the session limit is probably still at 1. Set it to 0, then try again." (v2.2, Pilot-02: Jellyfin shows only a generic error here; the words "maximum number of sessions" appear only in the friend's server log, so no copy may tell anyone to look for them.)
6. "Open Dashboard > Devices. There must be exactly one device for that user called Shoal: {Home} ({safetyCode}), besides your browser. Read me the code in brackets. If it's different, or there are two Shoal devices, delete that user and tell me. We'll start again with a new one."
7. "Sign out of the private window."
8. "In that user's settings, untick Enable access from all devices, tick only the Shoal device, and save. Check Dashboard > Devices again: one device should be left."
9. "When I tell you the codes matched: in that user's settings, set Maximum number of simultaneous user sessions to 1, and save. While my server is connected, nobody else can sign in as that user."

After the list, three short paragraphs (`meta`, `--shoal-text`, each with a bold lead-in; copied with the steps):
- **"To stop sharing:** delete the user (Dashboard > Users > the user > Delete). That ends it completely, including any device you can't see. Then remove me from Tailscale sharing, or block my address. Turning the user off isn't enough on its own." (DESIGN 4.5: deleting the account is the only clean revoke.)
- **"To pause for a while:** turn the user off, and while it's off, give it a new long password. That signs out every device. When you want to share again, turn it back on and we'll pair again from step 5." (DESIGN 4.5 Pausing.)
- **"To pair again later:** set the user's session limit back to 0, pair again from step 5, then set it to 1 again." (DESIGN 4.1 step 9 and 4.4 Rotation, corrected in v2.2.)

Plain-text format for "Copy steps": numbered lines; the step 3 groups as "   - Turn on: …" lines; the note as "   Note: Jellyfin has no switch for …"; then a blank line and the three paragraphs. `{Home}` and `{safetyCode}` are filled in; before a code exists, step 6 reads "called Shoal: {Home} (and a 4-character code I'll give you)".

**8.2.9 A stopped pairing stays visible** (v2.1, pop-up rule changed in v2.2; DESIGN 4.2a "Abort is durable" and "Two kinds"). A stopped pairing (either path) is stored with the server (`pairingError`, `pairingStoppedUtc`) and survives page reloads and restarts, so the admin learns about it even if they closed the pairing page.
- **Overview card**: section 7 rows for `PairingAborted` (safety or stopped kind), footer "See what to do". Counts toward "{n} need attention".
- **`view=pair&id=<handle>`**: renders the 8.2.5 outcome for that kind from `GET Shoal/Admin/Peers/{h}` (not from the live session, which is gone), with a `meta` line "Stopped {relative}". The stored outcome has no code number, so `requestReplaced` reads "Tell your friend not to enter the code you sent." Actions per 8.2.5, always including "Dismiss".
- **Jellyfin pop-up, safety stops only** (v2.2). A `DisplayMessage` goes to the admin who paired and to the other admins (header "Shoal", 8 s; DESIGN 4.2a) **only** for the five safety kinds: `requestReplaced`, `codesDidNotMatch`, `serverChanged`, `adminAccount`, `badPeerResponse`. Text: "Shoal stopped pairing with {Server} for your safety. Open Shoal in the dashboard to see what to do." `{Server}` is the admin's own display name for it. No codes, addresses or friend-supplied text in the pop-up.
- **Every other kind sends no pop-up** (`expired`, `denied`, `codeMismatch`, `inviteInvalid`, `quickConnectOff`, `quickConnectUnreachable`, `sessionLimitBlocked`, `interrupted`, `network`, `rateLimited`, `server`). They are ordinary outcomes: the card says "Pairing didn't finish" (chip "Pairing stopped") and waits for the admin. A pop-up for a network blip would teach admins to ignore the ones that matter. *(v2.1 also sent "Pairing with {Server} didn't finish…" as a pop-up; that string is removed.)*
- Clears only on **Dismiss** or a successful pairing with that server.

### 8.3 Server detail (`view=server&id=…`)
Purpose: everything about one friend's server: health, speed and diagnostics (DESIGN 9.3 Server detail), libraries and who sees them, overlap and storage, active streams, settings, removal. Back link "Overview". Data: `GET Shoal/Admin/Peers/{h}` (13); writes per 9.6.

**8.3.1 Header (inside a panel, so chips sit on surface)**
- Banner: latest backdrop, 160 px (S 120), full panel width, top radius 10, same pill and offline treatment as cards. Fallback: hatched banner.
- Row: `h1` "{Server}" (one line, ellipsis) + status chip.
- Meta lines (`meta`, muted):
  1. via line (as cards).
  2. "Server ID" + `{serverIdPrefix}` (13 px mono, `--shoal-text`) · "Paired {date} by invite" or "Paired {date} by Quick Connect" (`pairedUtc`, `pairingMethod`). **No safety code here** (DESIGN 9.5 #1): the safety code is only for the moment of pairing.
  3. Quick Connect pairings only, the device lock (`deviceLock`): ok lock icon + "Lock confirmed" or warn icon + "Ask your friend to lock the sharing account to Shoal's device"; then (v2.1) " · " and the session limit (`sessionLimit`): ok lock icon + "Session limit 1" or warn icon + "Ask your friend to set the session limit to 1". When either is warn, a `details` "How" follows, containing steps 8 and 9 of 8.2.8 with `{Home}` and the check filled in, and "Copy steps".
  4. v2.1, Quick Connect pairings only: `details` "Notes about this sharing account" (closed by default, `meta`; from `accountNotes`, DESIGN 4.1 "Informational notes"). Never counted as alerts or "need attention", because they can't change on Jellyfin 12.1. Text: "Jellyfin 12.1 lets this sharing account change its own password and has no setting to stop it. The session limit protects your friend while you're connected, and deleting the account ends sharing completely." and "Jellyfin turns on three options it doesn't use for this account (media conversion, sync transcoding, public sharing). They have no effect."
- Actions (wrap; S: 2-up grid): quiet "Sync now" (cooldown 5 min), quiet "Test link" (cooldown 1 min), quiet "Pause" / "Resume" (8.3.11), quiet "Pair again", quiet "Remove server…". Problem states put their fix first as **primary**: AuthFailed and IdentityMismatch → primary "Pair again"; TlsKeyChanged → primary "Check certificate" (moves focus to that alert's title); Disabled → primary "Resume"; Offline → primary "Retry now".

**8.3.2 Alerts** (`h2` "Needs attention", `id="alerts"`, only when `alerts[]` is non-empty). Each alert is a hint box (warn or bad) with an `h3` title (`label` 700), body, actions. Order: bad before warn, then the order below. The `kind` values are proposed (14).
| `kind` | Severity | Title | Body | Actions (API) |
|---|---|---|---|---|
| `heldRemovals` | warn | "{n} removals on hold" | "{Server} stopped listing {n} titles at once. That can happen when a drive disconnects, so Shoal kept them for now. For example: {t1}, {t2}, {t3}. If they're still gone after 3 syncs over 24 hours, Shoal removes them." | primary "Remove them now" (`POST …/{h}/Holds/removals/Release`), quiet "Keep them for now" (`…/Holds/removals/Keep`) |
| `heldAdditions` | warn | "{n} new titles on hold" | "{Server} added {n} titles at once, so Shoal is holding them until you check. That protects your server if something goes wrong on theirs. For example: {t1}, {t2}, {t3}. Shoal never adds them on its own." | primary "Add them" (`…/Holds/additions/Release`), quiet "Not now" (`…/Holds/additions/Keep`) |
| `authFailed` | bad | "Access removed" | card hint text (section 7) | primary "Pair again" |
| `identityMismatch` | bad | "Different server at this address" | card hint text | primary "Pair again", quiet "Remove server…" |
| `tlsKeyChanged` | bad | "{Server}'s certificate changed" | "Shoal stopped talking to {Server} because its certificate key is different from the one you pinned. That's normal when your friend renews or replaces the certificate, and a warning sign if they didn't. Ask them before you trust it." Then a `dl` in 13 px mono, wrapping: "Pinned key" {old fingerprint}, "Key now" {new fingerprint} | primary "Trust new certificate" → confirm dialog: title "Trust {Server}'s new certificate?", body "Only do this if your friend told you they changed it.", "Cancel" (initial focus) / primary "Trust it" (`POST …/{h}/TrustCertificate`); quiet "Remove server…" |
| `foreignDevice` | bad | "Another device is using your account on {Server}" | "Shoal saw a device called {deviceName} using the sharing account {Server} gave you. If your friend doesn't recognize it, they should delete that sharing account and make a new one. Then pair again." (`deviceName` is text from the friend's server; v2.1: "delete", never "disable", DESIGN 4.5) | quiet "Pair again" |
| `policy` | warn | "The sharing account can do too much" | "{Server}'s sharing account is allowed to {list}. Ask your friend to turn that off in the account's settings." with {list} from: "see all libraries", "delete media", "download media", (v2.1) "use Live TV", "control other people's devices", "sign in from any device" (the device lock, when the header doesn't already show it) | none |
| `sessionLimit` (v2.1) | warn | "Ask {Server}'s admin to set the session limit to 1" | "Without a session limit, the sharing account on {Server} could also be signed into from somewhere else. A limit of 1 stops that while Shoal is connected. It's step 9 of the steps for your friend." Shown only after the pairing is confirmed and while `MaxActiveSessions` isn't 1 (DESIGN 4.1 "What Home checks") | quiet small "Copy steps" (step 9 only) |
| `mergedVersion` | warn | "Unmerge {title}" | "Jellyfin grouped {title} from {Server} with one of your own movies as versions of one title. Titles from friends can't be merged with your copies, so it won't play from {Server}. Open it in Jellyfin and split the versions." | link "Open {title}" (`#/details?id={localItemId}&serverId=…`) |
| `diskQuota` | warn | "Storage limit reached for {Server}" | "Shoal stopped saving pictures for {Server} at {quota} GB. New titles still appear, with plain placeholder art." | link "Open Settings" (Syncing section) |
| `entityCap` | warn | "Some names from {Server} were skipped" | "{Server}'s library lists more than 20,000 cast, genre or studio names, so Shoal skipped the extra ones. Titles still appear." | none |
| `entryCap` | bad | "{Server}'s library is too big to sync" | "Shoal syncs up to 50,000 titles per server, so syncing stopped. Titles already added still play." | none |
| `stubFolder` | bad | "Shoal can't write its library files" | "The folder for {Server}'s library can't be written. Check the folder in Settings." | link "Open Settings" |

**8.3.3 Link speed** (`h2` "Link speed", `id="speed"`) [DESIGN phase 4]
- Range segmented "24 h" / "7 days" / "30 days".
- KPIs: "Speed now", "Latency" (median), "Stability" ("Good", or "{jitter} ms jitter · {loss}% loss" in warn when unstable), "Lowest in {range}".
- Throughput chart (section 9), then a latency chart (96 px, same rules, y in ms, no thresholds, line `--shoal-link`, labelled "Latency, median per test").
- "What plays from {Server}" ladder (all six DESIGN 7.2 classes on this screen: 4K remux, 1080p remux, 4K, 1080p, 1080p transcode, 720p).
- Route panel (surface-2 box): "Route" + chip: "Direct", "Relayed through Tailscale ({region})", "Maybe relayed", "Through proxy", "Unknown". Line: "To check yourself, run" + code `tailscale ping {host}` + icon button "Copy command". Line (`meta`): "Shoal can read Tailscale's status on this server." / "Shoal can't read Tailscale's status here, so relay detection is a guess."
- Hints list (DESIGN 7.3), each a hint box with severity icon, first match first.
- Buttons: quiet "Test now" (1 min cooldown), quiet "Deep test…" → dialog: title "Run a deep test?" body "It downloads about 32 MB and asks {Server} to transcode for about 20 seconds, which uses your friend's CPU." Buttons "Cancel" / primary "Run deep test". 10 min cooldown.
- `details` "Recent tests" → table: Time, Type (Scheduled / Manual / Deep / Playback), Speed, Latency, Jitter, Loss, Route, Result ("OK" or "Failed: timed out" style short reasons, never peer bodies).
- `details` "Recent plays" → table: Time, Person, Method (Direct / Transcode), Average speed, Segment wait, Pauses, Transcode speed.
- `details` "Check a title" → search input "Find a title from {Server}", results list rows: title, "{bitrate} Mbps file", verdict text with icon.

**8.3.4 Libraries from {Server}** (`h2`). Rows: library name (`body` 700), meta "From {remote name} · {count} titles · last sync {relative} ({result})", access summary (eye + "Everyone on this server" / "{n} people: …" / "Only you", or eyeoff warn "Hidden from everyone"; section 7) + link "Change who can see this" (to `view=libraries`, scrolled to `#lib-{mappingKey}`; the switch and picker live in one place, 8.4), held removals and held additions lines when any, quiet small "Remove…" → dialog: title "Remove {Library}?", body "People here stop seeing its {n} titles. Shoal deletes the library and its entries; {Server} keeps its files.", "Cancel" (initial focus) / danger "Remove library" (`DELETE …/{h}/Libraries/{m}`). Quiet "Add another library" opens the step-4 picker in a dialog (`GET …/{h}/RemoteLibraries`, `POST …/{h}/Libraries`), with the same "Only you" default (owner D3). Empty: "No libraries from {Server} yet." + "Add a library".

**8.3.5 Storage overlap and limits** (`h2` "Storage"): the overview overlap panel content, plus link "Full report". Then `h3` "Shoal's storage for {Server}" (DESIGN 5.8, 5.10; from `usage`):
- A meter: "Pictures and library files: {used} of {quota} GB" with a 8 px bar (`role="meter"`, `aria-valuenow`, `aria-valuemin="0"`, `aria-valuemax`, `aria-label` = the visible text). Fill `--shoal-accent`; at 90% or more, the text gets a warn icon and "Almost full".
- `dl` lines (`meta`): "Titles" {stubs} of 50,000 · "Cast names" {people} of 20,000 · "Genres" {genres} of 20,000 · "Studios" {studios} of 20,000. A value at its cap shows warn icon + "limit reached".

**8.3.6 Playing now** (`h2`): rows: person, title, "Direct" or "Transcode", started {relative}, {Mbps}. Empty: "Nothing is playing from {Server} right now."
- v2.2: directly under the rows (or the empty line), the `details` "Which apps can play titles from {Server}" from 8.3.12, closed by default. This is where an admin lands when someone here says a title won't play.

**8.3.7 Settings for this server** (`details`, closed by default; `PUT Shoal/Admin/Peers/{h}`): "Streams at once" number (1-10, default 4; `maxConcurrentStreams`), help "How many people here can watch from {Server} at the same time."; "Priority" number (`priority`), help "When two servers have the same title, the lower number wins."; (v2.1) "Server address" (`url`), help "If only the http/https part or the port changes, Shoal stays connected. A new name or IP address signs Shoal out, and you pair again."; "Proxy" (as pairing; `proxyUrl`); "How you reach it" read-only text + "To change this, pair again."; Internet servers only: checkbox row "Pin this server's certificate" (`tlsPinned`, off by default, DESIGN 12 question 4) / note "Shoal stops if the certificate's key ever changes. Most certificates get a new key when they renew, every 2 to 3 months, and you'd have to trust it again each time." Primary "Save" (inline "Saved" status, polite).
- v2.1, **address change** (DESIGN 4.4 "Peer address change", W2§N-8): when Save would change the host (the page compares the hostname part; the server decides for sure), Save opens a dialog first: title "Change {Server}'s address?", body "Shoal signs out of {Server} and deletes its saved key before it uses the new address, so the key never reaches a server you haven't checked. Titles stay listed but won't play until you pair again.", field "Type {Server} to confirm" (sent as `confirmName`), "Cancel" (initial focus) / primary "Change address". Not the danger button: nothing is deleted from the library. After saving, the server shows "Access removed" with primary "Pair again".
- v2.1, **while a pairing is in progress** with this server, "Server address" and "Proxy" are `disabled`, with the note "You can change this after pairing finishes." (the server answers 409 otherwise, DESIGN 4.2a).

**8.3.8 Remove** (`h2` "Remove {Server}", danger zone styling: 1 px `--shoal-hint-bad-border` box): body "Stops all sharing from {Server}." Quiet "Remove server…" → dialog:
- Title "Remove {Server}?"
- List: "Shoal signs out of {Server} and deletes its key." / "People here stop seeing {Server}'s titles." / "Shoal undoes the library access changes it made for {Server}'s libraries."
- v2.1, Quick Connect pairings only (the friend runs plain Jellyfin), an info note under the list: "Your friend still has the sharing account they made for you. Ask them to delete it in Dashboard > Users. Shoal can't do that from here." (DESIGN 4.5.) Invite pairings: "Your friend can stop sharing with you in their Shoal."
- Checkbox (checked): "Also delete the {Library} library and its {n} entries" (`deleteLibrary=true`).
- Field "Type {Server} to confirm" (v2.1: the typed text is sent as `confirmName`, so the server checks it too, DESIGN 9.6 and T47).
- Buttons "Cancel" (initial focus) / danger "Remove server" (`DELETE Shoal/Admin/Peers/{h}?deleteLibrary=true|false`).

**8.3.9 States**: loading skeleton (banner + header + chart blocks); error as Overview; offline (header chip Offline, speed shows last data + band, primary "Retry now"); paused (primary "Resume"); access removed; different server; certificate changed (`TlsKeyChanged`: chip "Different server", the certificate alert first, primary "Check certificate"). In every problem state all sections stay readable and data is marked "Last updated {relative}". Keys can't be unlocked: banner (8.8), primary "Pair again". Recovery mode: banner, `h1` and "Shoal is paused until its settings are fixed." only.

**8.3.10 Focus order**: back link → action buttons → device-lock and session-limit "How" summary (if shown) → "Notes about this sharing account" summary (if shown) → alert actions → range segmented → Test now → Deep test → Copy command → details summaries in order → library rows' "Change who can see this" and "Remove…" → "Add another library" → Full report → (v2.2) "Which apps can play titles from {Server}" summary → settings summary → Remove server.

**8.3.11 Pause and resume** (v2; `POST …/{h}/Pause`, `POST …/{h}/Resume`). No confirm dialog (reversible). Under the button, `meta` muted help: Pause "Titles stay listed here but won't play until you resume." Busy "Pausing…" / "Resuming…". Results: chip changes, polite "{Server} paused. Its titles stay listed but won't play." / "{Server} resumed."

**8.3.12 Which apps work** (v2.2; rewritten in v2.2.1 for DESIGN v2.3 6.12, Warden W4-15, owner D12 and D14). One block, written once, rendered in two places: Server detail under Playing now (8.3.6, summary "Which apps can play titles from {Server}") and Settings > Playback (8.5 item 3, summary "Which apps can play friends' titles"). Both are 6.8 `details`, closed by default. It explains, it doesn't warn: no hint box, no alert count, no "need attention". Nothing is broken on the admin's side.

Three honesty rules (W4-15, DESIGN 6.12 v2.3): (1) an app is listed under **Works** only after a real-device pass on the build that ships is on record (T-CLI-13 for apps; the lab browser run for Jellyfin's web app); every other supported app sits under **Should work** until then. (2) No "yet" anywhere: Option A may never ship, so nothing may read as a promise. (3) The proxy warning lives in the README only, never in this block.

Content, top to bottom (`{Server}` reads "a friend's server" in the Settings copy):
- Lead (`body`): "Titles from {Server} play in most Jellyfin apps, but not all of them."
- `h3` "Works" (`label` 700), then a plain list, each row a 16 px `check` icon in `--shoal-ok` + text. Rows are the `verified: true` rows of the table below. If no row is verified, the heading and list are not rendered.
- `h3` "Should work", then the same list style with a 16 px `info` icon in `--shoal-muted`, then one `meta` line: "These are built the same way, but nobody has tried them with Shoal on a real device." Rows are the `verified: false` rows of the table below.
- `h3` "Doesn't work" (was "Not yet"), then the same list style with a 16 px `x` icon in `--shoal-off` (neutral grey, not bad: this isn't an error):
  - "The Jellyfin Android TV app"
  - "Swiftfin"
  - "Findroid"
  - "The Jellyfin Android app when it hands a video to another player, such as VLC or MX Player"
- Why (`meta`, `--shoal-text`): "Those apps ask for videos without signing in, and Shoal never sends a friend's video to a request that isn't signed in. A few titles may still play in them when {Server} converts the video, but that isn't something to rely on."
- `meta`: "Downloading a friend's title to watch offline isn't available in any app."
- What to do (`meta`, bold lead-in **"If a title won't play:"**): "ask which app they're using. If it's on the Doesn't work list, try Moonfin or Jellyfin's web app instead."

**Supported apps, one row each** (fixed text; `verified` is a constant in `shoal.js` next to `STRINGS`, flipped by Kiro when Pilot or the owner records a pass; each flip names its test record in the commit message):
| Key | Text | `verified` at first release | Evidence needed |
|---|---|---|---|
| `appsRowWeb` | "Jellyfin's web app, in a browser" | true once Pilot's lab browser run on the release build is on record (the owner's spike test 01 alone isn't enough) | T-CLI web cells on the release build |
| `appsRowMoonfinIos` | "Moonfin on iPhone" | true once the owner's iPhone pass is on record (D14) | T-CLI-13, iOS row |
| `appsRowMoonfinOther` | "Moonfin on Android, Android TV, Fire TV, Apple TV, iPad, Samsung and LG TVs, Xbox, Roku and computers" | false | T-CLI-13 per platform; when some platforms pass and others don't, split the row (passed platforms in one row under Works, the rest under Should work) |
| `appsRowJmp` | "Jellyfin Media Player" | false | T-CLI-13, JMP row |
| `appsRowAndroid` | "The Jellyfin Android app, with its own player" | false | T-CLI-13, official Android app row |

D12 sets the MVP target as Moonfin plus Jellyfin's web app; the other two Should work rows are supported by source reading only (DESIGN 6.12) and may stay there.

Rules: the three `h3` labels carry the meaning, so the icons stay `aria-hidden` (12.4). App names are product names and stay untranslated. The lists are fixed text in `STRINGS`, never built from client data or from anything a friend's server sends. In Server detail the block sits at the `h3` level under the `h2` "Playing now"; in Settings, under the "Playback" `h2`.

**Ship gate.** The block ships only when the owner has confirmed G1 (done: D12), Warden has read the help text (done: W4-15, these edits applied), and **T-CLI-13 is on record for every row marked `verified: true`**. If the stance changes (for example Option A is ever built), Lumen rewrites the Doesn't work list and the why line. The README carries the same facts in DESIGN 6.12's wording plus the proxy warning; the UI copy above is the shorter, plainer version of it.

### 8.4 Libraries & access (`view=libraries`)
Purpose: one place to see and change who can see what, in both directions. Default-deny is the rule (DESIGN 5.6, owner D3/D4): a friend's library starts as "Only selected users" with only the admin who added it selected, and one switch opens it to "Everyone on this server". Data: `GET Shoal/Admin/Libraries` (mappings + users, 13); `PUT Shoal/Admin/Libraries/{m}/Access`; `POST Shoal/Admin/Libraries/{m}/Reconcile`.

**8.4.1 "Shared with you"** (`h2`; intro `body`: "At first only the person who added a library can see it. Try it, then open it to everyone here with one switch.")
- Grouped by server (`h3` server name + status chip). Each library is a **block** (`<article class="shoal-libblock" id="lib-{mappingKey}" aria-labelledby>`, divider between blocks, padding 16 0), laid out per 3.3:
  1. Library name (`localLibraryName`, `body` 700, one line) · meta "{count} titles · last sync {relative}" · held lines when any (warn: "{heldRemovals} removals on hold", "{heldAdditions} new titles on hold", each a link to the server's alerts).
  2. **Access switch** (6.17) bound to `access.mode`, label "Who can see {Library}".
  3. `Selected` mode: summary (section 7: "{n} people: {name}, {name} and {k} more", "Only you", or eyeoff warn "Hidden from everyone") + quiet small "Choose people" → picker (8.4.3).
     `Everyone` mode: summary eye ok "Everyone on this server, including people you add later" + `meta` muted "Your earlier selection of {n} people is saved. It comes back if you switch back." + link "See saved selection" → picker in read-only mode (8.4.3).
  4. `Everyone` mode only: checkbox row "Include profiles with parental limits" (`access.includeRestricted`) / note "Ratings come from {Server} and aren't checked." Ticking it opens the acknowledgement dialog (8.4.4); unticking saves at once.
  5. Drift (8.4.5), only when `drift[]` is non-empty.
- Empty: "No libraries from friends yet." + primary "Pair a server".

**8.4.2 "You share"** (`h2`; intro "What each friend's server can see from yours.")
- Rows per friend: friend name (700) · meta "{k} libraries: {names} · highest quality {limit} · {t} transcodes at once" · chip · warn line for unexpected devices (as Overview) · quiet small "Edit" (Friend detail, libraries section).
- Empty: "You aren't sharing any libraries." + quiet "Share with a friend".

**8.4.3 "Who can see this" picker** (dialog)
- Used in `Selected` mode (editable), in `Everyone` mode (read-only), and in the pairing wizard's step 4 (editable, before the library exists).
- Title "Who can see {Library}?"; body (`meta`): "Only the people you tick can see and play this library. Everyone else won't know it's there."
- Search field "Find a person" when there are more than 8 users.
- **"Select all"** checkbox row at the top (renamed from "Everyone" so it can't be mistaken for the "Everyone on this server" switch): checked when all selectable rows are, `indeterminate` when some are; toggles all visible selectable users.
- When any user has `restricted` true: an acknowledgement checkbox row under "Select all": "Ratings come from {Server} and aren't checked. Let me choose profiles with parental limits." (sent as `ratingsAcknowledged`). Until it's ticked, restricted users' checkboxes are disabled.
- v2.1: sharing accounts that Shoal made for friends (`isShareUser`) are **never listed** and never counted in "{n} of {total}", in either mode (DESIGN 5.6, W2§N-2). They can't see friends' libraries at all.
- One row per local user (48 px): avatar 32 px (user image from `ApiClient.getUserImageUrl`, fallback initial on `--shoal-surface-2`), name (`body`), notes (`meta`, muted), several allowed, in this order:
  - current admin (`ApiClient.getCurrentUserId()`): "You"
  - `isAdmin`: "Admin" (other admins start unticked too, DESIGN 5.6)
  - `isDisabled`: "Disabled account"
  - `allLibraries`: "Has access to all libraries. If unticked, Shoal hides this library from them and keeps the rest as it is." (DESIGN 9.5 #3)
  - `restricted`: "Parental limits: ratings come from {Server} and aren't checked"
  - `managedByShoal`: "Shoal keeps this account's library list so it can hide some friends' libraries."
- Live count in the footer, left: "{n} of {total} selected" (polite update on change, debounced 500 ms). When the current admin is unticked: warn line "You won't see this library either."
- Footer: "Cancel" / primary "Save" (`PUT …/Libraries/{m}/Access {mode: "Selected", selectedUserIds, includeRestricted, ratingsAcknowledged}`; in the wizard, Save only updates the step-4 summary). Save errors: inline in the dialog with `role="alert"`.
- **Read-only variant** (`Everyone` mode): title "Saved selection for {Library}", body "These people get it back if you switch to Only selected users.", checkboxes `disabled`, no search for 8 or fewer users, footer has only "Close".
- Focus: dialog title is not focusable; initial focus on the search field (or the "Select all" row when no search; "Close" in read-only); Tab order rows top to bottom; Esc cancels.

**8.4.4 Access switch confirm dialogs** (v2; owner D4; DESIGN 9.3). Who gains or loses access is computed in the page from `users[]` and `access`: Everyone admits every user except `restricted` ones unless `includeRestricted`; the list is everyone admitted under Everyone who is not in `selectedUserIds`. Names: first 5 as text, then "and {k} more" (a `details` expands the full list).
| Change | Title | Body | Buttons (API) |
|---|---|---|---|
| Only selected users → Everyone | "Open {Library} to everyone?" | "{n} more people can see and play it now: {names}. People you add to this server later get it too." When restricted profiles stay out: "{r} profiles with parental limits stay out. You can include them afterwards." | "Cancel" (initial focus) / primary "Open to everyone" (`PUT …/Access {mode: "Everyone", selectedUserIds (unchanged), includeRestricted (unchanged), ratingsAcknowledged}`) |
| Everyone → Only selected users | "Limit {Library} to selected people?" | "{n} people lose access: {names}. Your saved selection of {m} people comes back." (When the saved selection is empty: "Nobody will be able to see it until you choose people.") v2.1 second line (`meta`, DESIGN 5.6 guarantee): "They can't play it from now on. It can stay in their lists for up to an hour." | "Cancel" (initial focus) / primary "Limit access" (`PUT …/Access {mode: "Selected", …}`) |
| Include profiles with parental limits | "Include profiles with parental limits?" | "Ratings for {Library} come from {Server}, and Shoal can't check them. A child's profile could see a movie that's rated wrong." Required checkbox: "I understand ratings from {Server} aren't checked." | "Cancel" / primary "Include them" (disabled until the box is ticked; `PUT …/Access {…, includeRestricted: true, ratingsAcknowledged: true}`) |
- After success: the switch and summary update; polite "{Library} is open to everyone on this server" / "{Library} is limited to {m} people" / "Profiles with parental limits can now see {Library}". Failure: inline bad line under the switch (`role="alert"`) "Couldn't change who can see {Library}. Nothing changed." and the switch shows the saved mode.
- Neither direction uses the danger button: both are reversible.

**8.4.5 Drift list** (v2; DESIGN 5.6). Shown inside a library block when `drift[]` is non-empty: `details` with summary warn icon + "Access changed outside Shoal for {n} people". Content:
- Intro (`meta`): "Someone changed library access in Jellyfin's dashboard. Shoal's choice here wins: playback is checked every time, and lists catch up within about an hour, unless you accept the change."
- One row per entry (`userId` → user name from `users[]`), text by `kind` (values proposed, 14), and the action that **accepts** the dashboard's change:
  | `kind` | Text | Row action |
  |---|---|---|
  | `extraAccess` | "Can see it in Jellyfin, but isn't selected here." | quiet small "Select {name}" (adds them to `selectedUserIds`) |
  | `missingAccess` | "Selected here, but Jellyfin hides it from them." | quiet small "Remove {name}" (removes them from `selectedUserIds`) |
  | `managed` | "Shoal keeps this account's library list so it can hide {Library}." | none (info) |
  | `unmanaged` | "Shoal stopped managing this account's library list because someone edited it in the dashboard." | none (info) |
- Under the rows: quiet "Re-apply now" (`POST …/Libraries/{m}/Reconcile`; busy "Re-applying…"; polite "Access to {Library} re-applied").

**8.4.6 States** (v2): loading skeleton (two library blocks); load failed as Overview; saving (switch busy, 6.17); recovery mode: banner, `h1` and "Shoal is paused until its settings are fixed." only; a server in a problem state keeps its libraries editable (access is local) with its chip in the group heading.

### 8.5 Settings (`view=settings`)
Purpose: DESIGN 9.3 Settings. Data: `GET`/`PUT Shoal/Admin/Settings` for the form, `GET`/`PUT Shoal/Admin/Hardening` for "Protect your shared videos", `GET Shoal/Admin/State` for backups and checks, `DELETE Shoal/Admin/All` for the danger zone (field names in 8.5.1). Single column, sections as panels with `h2`. One "Save" for the settings form at the bottom (sticky on S: a bottom bar with surface background and top border; not sticky on L/M). Unsaved changes: text "Unsaved changes" next to Save (`meta`, warn icon). Leaving with unsaved changes: no blocking dialog (dashboard routing can't be intercepted reliably); changes are lost, which the text warns about.

Sections and fields (labels / help / validation):
1. **"Library files"**: "Folder for Shoal's library files" (path input) with validation line below, icon + text: ok "Local disk. Good." · bad "That folder is on a network share. Shoal needs a local disk so a network hiccup can't freeze your server." (with checkbox "I understand the risk, use it anyway") · bad "That folder is inside one of your libraries. Pick a folder outside them." · bad "Shoal can't write to that folder."
2. **"Syncing"**: "Check friends' libraries every" select (1, 3, 6, 12, 24 hours); "Safety net for big deletions": "Hold removals when more than {n} titles" (number) "or {p}% of a library disappear at once" (number), help "Protects you when a friend's drive disconnects."; "Safety net for big additions" (v2): "Hold new titles when more than {n} titles" (number) "or {p}% arrive at once" (number), help "Protects your server if a friend's server suddenly lists thousands of titles. You add them yourself from the alert; Shoal never does it on its own."; "Storage limit per server" number 1-50 + "GB", help "Pictures and library files Shoal saves for each friend's server. When it's full, new titles still appear with plain art."; "Picture quality" select "Standard (about 0.2 MB per title)" / "High (sharper on big screens)".
3. **"Playback"**: checkbox "Fit quality to each link" / note "Asks your friend's server to transcode when the link is too slow."; "Streams at once per person" number 1-4 (v2.2.1, owner D13), help "How many friends' titles one person here can watch at the same time, across all friends' servers. Up to 4."; checkbox "Send subtitles inside the video stream" / note "Turn off if subtitles misbehave in an app. Subtitles then come as separate files or are burned in." v2.2: last in the section, the `details` "Which apps can play friends' titles" (8.3.12; read-only, no field).
4. **"Speed tests"**: "Test each link every" select (3, 6, 12, 24 hours).
5. **"Offline servers"**: checkbox "Tell people when a server is offline" (on by default) / note "When someone presses Play on a title from an offline server, their app shows a short message, if the app supports it."; checkbox "Hide a server's titles after it's been offline for" + number + "hours" (off by default, owner D5) / warn note "Experimental. Hiding and showing titles again might lose watch history."
6. **"Tailscale"**: select "Read Tailscale status" ("Automatically", "Off", "Custom path") + path field when custom + status line "Found Tailscale on this server." / "Tailscale status isn't available here."
7. **"Protect your shared videos"** (Peer role; DESIGN 8.4, 9.5 #4, owner D9; only when at least one friend exists, else a one-line note "This appears once you share a library.") Data: `GET Shoal/Admin/Hardening` → `{mode, modes, sinceUtc, counters[], guardErrors, shieldHits, transcodeCapHits}`; changes `PUT Shoal/Admin/Hardening {mode}`.
   - Intro (`body`): "Jellyfin lets anyone who knows a video's internal ID stream it without signing in. Shoal can require sign-in for the libraries you share. Your other libraries aren't affected."
   - Current mode chip (section 7) + "since {sinceUtc, date}".
   - **Two** mode cards side by side (stacked on S), the only peers of each other: **"Log only (test first)"** (`LogOnly`, the default) / "Nothing is blocked. Shoal counts requests that would have been blocked, so you can check nothing breaks." · **"Require sign-in"** (`Enforce`) / "Requests without sign-in to your shared videos are blocked. Posters and pictures stay open so apps can still show them." They look like 6.7 choice cards but are **not radios**, because a change saves at once: the current mode's card has the checked outline, a check icon and the label "Current"; the other card carries its own button, primary "Require sign-in…" or quiet "Switch to log only". So moving focus or pressing arrow keys never changes the mode.
   - Panel "Would have been blocked, last 7 days" (from `counters[]`): table (App, Network, Kind, Count) with network values "This server", "Home network", "Tailscale", "Internet". Empty: "Nothing yet. Shoal has been watching for {d} days."
   - Recommendation hint after 7 days in Log only: ok "Nothing unexpected in 7 days. It looks safe to require sign-in." or warn "{n} requests came from {app}. Requiring sign-in would stop that app from playing shared titles."
   - "Always on while you share" (`h3`, `meta` lines): "Pairing requests slowed down: {shieldHits}" · (v2.1) "Wrong invites tried in the last hour: {inviteFailures.lastHour}" · "Transcodes refused over a friend's limit: {transcodeCapHits}" · (v2.1, static) "Sharing accounts can't sign in again elsewhere or change their password." When `inviteFailures.notice` is true, the same calm info hint as Overview (8.1.1 item 7) sits under these lines. When `guardErrors` > 0, a warn hint: "Shoal hit {guardErrors} errors while checking requests and let those requests through, so your server kept working. If this keeps happening, check the server log."
   - v2.1, reverse proxy (DESIGN 4.1b Limits, 8.4 (b); `proxySuspected`, DESIGN 9.5): warn hint "Most requests reach Jellyfin from one address on your own network, so Shoal can't tell visitors apart and its limits apply to everyone at once. If Jellyfin sits behind a reverse proxy, add the proxy under Known proxies in Dashboard > Networking."
   - `details` "What might stop working": "Smart TVs that play over DLNA", "Older apps that sign in the old way", "Stream links copied into VLC or other players".
   - `details` **"Troubleshooting"** (closed by default; rendered only when `modes` contains `Off`): body "If you think Shoal is breaking playback on this server, you can turn this protection off completely. Shoal then stops checking and counting. Pairing limits and friends' transcode limits stay on." + quiet "Turn off protection…". `Off` is never shown as a third choice card.
   - When `mode` is `Off`: neither card is "Current", both carry their button, the chip reads "Off", and a warn hint sits at the top of the section: "Protection is off. Anyone who knows a shared video's ID can stream it." The Troubleshooting button is hidden.
   - Changing mode (saves at once via `PUT`, never with the form Save):
     - to Require sign-in: confirm dialog "Require sign-in for shared videos?" / "Apps in the list above will stop playing shared titles until they sign in." / "Cancel", primary "Require sign-in". This is the one-click enforce of owner D9.
     - back to Log only: no dialog; polite "Protection set to log only".
     - to Off: confirm dialog "Turn off protection?" / "Shoal stops checking and counting requests for shared videos until you turn it back on." / "Cancel" (initial focus), primary "Turn off".
8. **"Key storage"** (read-only; `keySource`): "Shoal's keys are encrypted with {an environment variable (`env`) | a key file you chose (`envFile`, v2.1) | Windows protection for the Jellyfin account (`dpapi`) | a key file in Shoal's data folder (`file`)}." help (v2.1, DESIGN 8.2) "To keep the key out of backups, set JELLYFIN_SHOAL_KEY_FILE to a file holding 32 random bytes as 64 hex characters, for example from openssl rand -hex 32. Changing the key means pairing every server again." Hidden for `env` and `envFile`. When Shoal's data folder can't protect secret files (`secretsVolumeUnsafe`, proposed; DESIGN 3.1): warn hint "This drive can't protect Shoal's secret files. Treat secrets.enc and master.key as readable by anyone who can read the folder."
8a. **"Parental limits"** (v2; DESIGN 5.6): body "Every title from a friend carries the tag Shoal. Blocking that tag hides all friends' titles from profiles with parental limits, whatever the ratings say." Quiet "Block Shoal titles for profiles with parental limits" → result line ok "Added to {n} profiles." (endpoint not in DESIGN 9.6 yet, see 14; hide the button until it exists).
8b. **"Backups and checks"** (v2; DESIGN 3.7; `GET Shoal/Admin/State`): line "Last settings backup: {lastBackupUtc, relative}". v2.1, when `backupByCopy` (DESIGN 9.5) is true, a second `meta` line: "This folder can't make backups the usual way, so Shoal copies the file instead. That's fine." Startup check results, when any, as warn rows with text from `findings[].kind` (proposed): "{Server} has no saved key. Pair it again." · "{Server}'s library folder is missing." · "The library {Library} has no paired server. This happens after a restore: pair the server again, or remove the library." Each row offers "Relink" or "Delete" where DESIGN 3.7 allows (endpoint not in 9.6 yet, see 14). No findings: ok "Everything checks out."
9. **"Delete all Shoal data"** (danger zone box): body "Removes every paired server, every friend's access, Shoal's keys and the libraries Shoal created. People whose library list Shoal kept get All libraries back." (v2.1, DESIGN 5.6 N-13.) Quiet "Delete all Shoal data…" → dialog with typed confirmation "Type delete everything to confirm" (sent as `confirmName`) and danger "Delete all".

Focus order: local nav → fields in visual order → Protect section controls (cards, Troubleshooting summary) → parental limits button → backups and checks actions → Save → danger button. Save results: polite "Settings saved"; validation per 6.7. Recovery mode: banner, `h1` and "Shoal is paused until its settings are fixed." only (the restore actions live in the banner, 8.8).

**8.5.1 Field to API mapping** (v2; names from DESIGN 3.2 and 9.3; JSON casing follows Kiro's serializer)
| UI field | API field | Range / default |
|---|---|---|
| Folder for Shoal's library files | `stubRoot`; check result `stubRootCheck: ok\|network\|insideLibrary\|notWritable`; "I understand the risk" → `stubRootNetworkAck` (proposed) | |
| Check friends' libraries every | `SyncIntervalHours` | 1-24, default 6 |
| Hold removals when more than / or % | `MassDeletionMinCount` / `MassDeletionPercent` | 25 / 10 |
| Hold new titles when more than / or % | `NewItemsMinCount` / `NewItemsPercent` | 500 / 20 |
| Storage limit per server | `DiskQuotaGbPerPeer` | 1-50, default 2 |
| Picture quality | `ArtQuality` (`Standard` / `High`) | Standard |
| Fit quality to each link | `AutoFitQualityToLink` | on |
| Streams at once per person | `MaxStreamsPerUser` | 1-4, default 4 (owner D13) |
| Send subtitles inside the video stream | `HlsSubtitlesEnabled` | on |
| Test each link every | `SpeedTestIntervalHours` | 6 |
| Tell people when a server is offline | `NotifyClientsWhenOffline` | on |
| Hide a server's titles after … hours | `ParkOfflinePeers` / `ParkAfterHours` | off / 72 |
| Read Tailscale status | `TailscaleSocket` (`auto`, `off`, or a path); status `tailscaleDetected` | auto |
| Protect your shared videos | `Hardening.mode` (`LogOnly` / `Enforce` / `Off`), `modes` | LogOnly |
| Key storage | `keySource: env\|envFile\|dpapi\|file` (v2.1 adds `envFile`) | |

### 8.6 Overlap report (`view=overlap&id=…`)
Purpose: DESIGN 5.8 detail. Back link "{Server}" (to Server detail) or "Overview" when opened from Overview.
- `h1` "Library overlap"; Server select (`aria-label="Server"`).
- Sentence + bar + legend (as 8.1). Line `meta`: "Your copy always plays when you both have a title."
- `h2` "By quality": table (caption visually hidden "Overlap by quality"): rows 4K, 1080p, 720p, SD, Total; columns "In both (titles)", "Your storage", "New from {Server} (titles)", "Their storage". Numbers right-aligned, tabular.
- When the API provides it (phase 6): "{Server}'s copy is better than yours for {n} titles." with a muted note "Alternate versions are coming in a later version."
- `meta`: "Updated after the last full sync, {relative}."
- Empty: "No overlap report yet. It appears after the first full sync." Loading: skeleton bar and table rows.

### 8.7 Friend detail (`view=friend&id=…`, sharing side)
Purpose: DESIGN 9.3 Friend detail (Peer role with Shoal). Back link "Overview". Data: `GET Shoal/Admin/Shares/{id}` (13); writes per 9.6. There is **no "Approve a code" box**: Shoal on this side never approves Quick Connect codes; pairing is invite-only (DESIGN 4.1b, 9.5 #2).

Sections, top to bottom:
1. **Header panel**: `h1` "{Friend}" (`friendName`) + chip (`status`: Active / Paused / Access removed); meta "Sharing account {shareUserName} · since {sinceUtc, date}". Actions: quiet "Pause" / "Resume" (`POST …/Shares/{id}/Pause` / `Resume`; no confirm, reversible). Help under the button (`meta`, muted): Pause "Signs {Friend}'s server out at once. Its titles stay listed on their side but won't play." · Resume "Shoal first signs out any device that isn't {Friend}'s server, then lets it back in." Results announced politely ("{Friend} paused" / "{Friend} resumed").
2. **Approval card** (6.18, with the safety code input), only while an approval is pending, directly under the header. After 3 wrong codes it becomes the "Request cancelled" state (8.2.5 share side) until the admin creates a new invite or leaves.
3. **"Invite"** (`h2`; DESIGN 4.1b step 2): state from `invite.state`:
   - `none`: body "To connect {Friend}'s server, or to pair it again, create an invite." + "Create invite" (primary when the friend has never connected, else quiet; `POST …/Shares/{id}/Invites`).
   - just created: the one-time invite display (6.10) with "Copy invite", countdown and the shown-once warning, + quiet "Create a new invite" + link "Cancel invite" (`DELETE …/Invites/current`).
   - `active` (page reopened, secret no longer available): "An invite is active · expires in {m:ss}" + quiet "Create a new invite" + link "Cancel invite".
   - `expired`: off "The invite expired." + "Create a new invite".
   - v2.1: there is no "cancelled after failed attempts" state. Wrong invites from strangers never cancel this invite; the calm notice is on Overview and Settings (8.1.1 item 7, 8.5 item 7).
4. **"Devices"** (`h2`): intro `meta` "Only {Friend}'s server should be here. Shoal signs out anything else within a minute." Rows: device name (text from the device, `textContent`), meta "{app} {version} · first seen {date} · last seen {relative}", chip: ok "{Friend}'s server" (expected) or warn "Unexpected device signed out" (row background `--shoal-hint-warn-bg`); quiet small "Remove" → confirm dialog title "Remove this device?", body "It stops working right away. If it's {Friend}'s server, they'll need a new invite.", "Cancel" / danger "Remove device" (`DELETE …/Shares/{id}/Devices/{deviceHandle}`). Empty: "No devices yet. {Friend}'s server appears here after it connects."
5. **"Libraries and quality"** (`h2`): library checkboxes (`libraryIds`, same rules as 8.2.4 step 1) + "Highest quality per stream" (`maxBitrateMbps`) + "Transcodes at once" (`maxConcurrentTranscodes`, 1-10) + primary "Save" (`PUT …/Shares/{id}`; inline "Saved", polite).
6. **"Stop sharing with {Friend}"** (`h2`, danger zone box: 1 px `--shoal-hint-bad-border`; DESIGN 4.5): body "Shoal disables {Friend}'s sharing account, signs out every device, checks that none are left, and deletes the account. Their server stops playing your titles within a minute." Quiet "Stop sharing…" → dialog: title "Stop sharing with {Friend}?", the four effects as a list, field "Type {Friend} to confirm" (sent as `confirmName`), "Cancel" (initial focus) / danger "Delete {Friend}'s access" (`DELETE Shoal/Admin/Shares/{id}`; v2.1 label per DESIGN 4.5, so the button says the account goes away). Under the dialog's list, `meta` muted: "To share again later, create a new invite. Shoal makes a new sharing account."
   - **Result** replaces the section with a step checklist (6.19), from the revoke result (shape proposed, 14): 1 "Sharing account disabled" · 2 "{n} devices signed out" · 3 "No devices left" (failed variant, bad: "{n} devices are still signed in. Shoal tried twice. Disable the account in Dashboard > Users, then delete its devices.") · 4 "Sharing account deleted". Then `h3` "Two more steps" (to-do rows): 5 "Remove {Friend} from this server's Tailscale sharing: Tailscale admin console > Machines > this server > Share." (public servers: "Block {Friend}'s address on your router or firewall.") with a "Done" checkbox (local only, not saved) · 6 "Protect your shared videos: {mode}." + link "Open settings"; when the mode isn't Require sign-in, a warn hint "Without it, a server that kept old links can still play your shared videos until you remove its network access."
   - Then `{Friend}` no longer exists: the page keeps the result until the admin leaves; Back goes to Overview.
- **States**: loading skeleton (header + three panels); load failed as Overview; paused (chip, primary "Resume"); `AccessRemoved` (bad hint under the header: "{Friend}'s sharing account was turned off or deleted outside Shoal. Create an invite to share again, or stop sharing to clean up."); recovery mode (banner, `h1`, "Shoal is paused until its settings are fixed." only).
- **Focus order**: back link → Pause/Resume → approval card code input, Approve, Deny (if shown) → invite actions → device "Remove" buttons → library checkboxes → quality → transcodes → Save → "Stop sharing…".

### 8.8 Banners on every screen (v2; DESIGN 9.1, 3.7, 8.2)
Rendered by the page shell into `.shoal-banners` (1.2) from the Status response (`state`, `secretsUnreadable`, `keySource`; shape proposed, 13). At most one bad banner shows: recovery mode outranks keys can't be unlocked; the info banner shows only when no bad banner does. Component: 6.16.

| Banner | When | Title | Body | Actions |
|---|---|---|---|---|
| **Recovery mode** (bad, blocking) | `state.mode` = `Recovery` | "Shoal couldn't read its settings" | Damaged files: "Shoal's settings file and its backups are damaged, so Shoal paused sharing and won't change anything until you choose what to do. Titles from friends' servers won't play meanwhile." · Newer version (`state.reason` = `newerSchema`): "Shoal's settings were saved by a newer version of Shoal. Update Shoal, or restore an older backup. Until then Shoal paused sharing and won't change anything." · v2.1, appended to either body (DESIGN 3.7 step 2): when the last-known share list was read (`state.guardLastGood` = `ok`, DESIGN 9.5): "Your friends' sharing accounts keep their usual limits meanwhile." When it couldn't be read (`unreadable`): "Protection for the libraries you share is also off until you fix this." | primary "Restore a backup…" → dialog "Restore Shoal's settings" with one radio choice card per file from `GET Shoal/Admin/State` ("{file} · saved {date}", newest first, "Newest" note on the first), "Cancel" / primary "Restore" (`POST Shoal/Admin/State/Restore`). Quiet "Start over…" → dialog "Start over with empty settings?", body "Shoal moves the damaged files aside (it deletes nothing) and starts with no paired servers and no friends. You'll need to pair again.", field "Type start over to confirm", "Cancel" (initial focus) / danger "Start over" (`POST Shoal/Admin/State/StartOver`) |
| **Keys can't be unlocked** (bad; blocks friends' servers only) | `secretsUnreadable` | "Shoal can't unlock its saved keys" | "Shoal stopped using your friends' servers because it couldn't decrypt the keys it saved. That happens when the encryption key changed or the file was damaged. Shoal didn't make a new key, so nothing is lost yet." + `details` "How to fix it": by `keySource`: env "Set JELLYFIN_SHOAL_KEY back to its old value and restart Jellyfin." · envFile (v2.1) "Put the original key back in the file JELLYFIN_SHOAL_KEY_FILE points to, and restart Jellyfin." · file "Put the original master.key back in Shoal's data folder and restart Jellyfin." · dpapi "Run Jellyfin under the Windows account it used before, then restart it." Always followed by: "Or pair each server again. That replaces the saved keys." v2.1 variants by `secretsReason` (DESIGN 9.5; DESIGN 8.2 key format): `badKey` body "The key Shoal was given isn't a valid key. It must be 32 random bytes, written as 64 hex characters." How to fix: "Put back the key you used before. Or make a new one with openssl rand -hex 32, restart Jellyfin, and pair each server again." · `bothKeys` body "Both JELLYFIN_SHOAL_KEY and JELLYFIN_SHOAL_KEY_FILE are set, so Shoal doesn't know which key to use." How to fix: "Remove one of them and restart Jellyfin." | none in the banner (the fix is outside the page); every server card shows "Pair again" (section 7) |
| **Restored from a backup** (info; v2.1: stays until confirmed) | `state.restoredFrom` present and `needsConfirm` | "Shoal restored its settings from a backup" | "The main settings file was damaged, so Shoal used {file} from {date}. Changes made after that are missing. To be safe, every friend's library is now limited to its selected people. Check who can see each library and which friends can connect, then confirm." (DESIGN 3.7: every access mode loads as Only selected users, and protection keeps its stricter setting.) | quiet "Check libraries" (to Libraries & access), quiet "Check friends" (to Overview, friends section), primary "Confirm" (`POST Shoal/Admin/State/ConfirmRestore`, DESIGN 9.6; busy "Confirming…"; polite "Thanks. Shoal is back to normal."). Not dismissible per browser any more: the state is on the server, so every admin sees it until one confirms. Libraries & access also shows each library's switch on "Only selected users"; the admin opens any of them again with the switch (8.4.4) |

- **Blocking** means: in recovery mode every view renders only the banner, its `h1` and the line "Shoal is paused until its settings are fixed." (`meta`, muted); the local nav still works. With keys locked, screens render normally, but pairing, sync and test actions for existing servers are replaced by "Pair again".
- After Restore or Start over succeeds: the page reloads the current view; polite "Settings restored" / "Shoal started over".

---

## 9. Chart spec: link speed (24 h default)

**Structure (exact)**
```html
<figure class="shoal-chart" aria-labelledby="shoal-chart-title">
  <figcaption class="shoal-sr" id="shoal-chart-title">Speed from Hops, last 24 hours</figcaption>
  <div class="shoal-chart__plot">
    <svg role="img" aria-labelledby="shoal-chart-title shoal-chart-summary" focusable="false"> … </svg>
    <span class="shoal-chart__thr" data-q="4k">4K needs about 30 Mbps</span>
    <span class="shoal-chart__thr" data-q="1080p">1080p needs about 15 Mbps</span>
  </div>
  <div class="shoal-chart__x" aria-hidden="true"><span>24 h ago</span><span>18 h</span><span>12 h</span><span>6 h</span><span>Now</span></div>
  <p class="shoal-sr" id="shoal-chart-summary">…generated sentence…</p>
  <details class="shoal-collapse"><summary>Show as a table</summary><div class="shoal-tablewrap" tabindex="0" role="region" aria-label="Speed tests table"><table>…</table></div></details>
</figure>
```

**Geometry**
- Plot height 184 px (S 160). Draw in **pixel space**: on render and on `ResizeObserver` (debounced 100 ms), set `viewBox="0 0 W H"` from the plot's `clientWidth`/`clientHeight` and rebuild paths. No `preserveAspectRatio="none"` (it distorts point markers).
- Insets: top 12, right 8, bottom 0 (the plot's bottom border is the x axis: 1 px `--shoal-control-border`), left 0.
- X: linear time from `now - range` to `now`, local time. Labels: 24 h → "24 h ago, 18 h, 12 h, 6 h, Now"; 7 days → short weekday names at local midnights + "Now"; 30 days → dates every 7 days ("Oct 7"). Label row `micro` 12 px muted, padding-top 6.
- Y: 0 to `yMax`, `yMax = niceCeil(max(maxSample, 30 if any sample ≥ 20 else 15) × 1.15)` on a 1-2-5 sequence (10, 20, 50, 100…). Draw a threshold only when it is ≤ `yMax`.
- Gridlines: 3 horizontal lines at 25/50/75% of `yMax`, 1 px `--shoal-line`, `shape-rendering: crispEdges`. No y labels (thresholds and KPIs carry the numbers).

**Marks**
| Mark | Spec |
|---|---|
| Line | throughput samples (all kinds) in time order; `stroke: var(--shoal-accent)`, 2.25 px, round joins; straight segments (no smoothing: values are discrete tests) |
| Area | under the line, `fill: var(--shoal-chart-area)` |
| Points | every sample: circle r 3.5, fill accent, stroke `--shoal-surface` 1.5 px; manual and deep tests: r 4.5 hollow (surface fill, accent stroke 2 px); playback samples: r 3 |
| Latest point | r 5 + halo r 9 at accent 25% opacity (the mockup's "now dot") |
| Thresholds | 1 px dashed `5 5`, `--shoal-chart-threshold`; HTML labels on top of the line at the left, `micro` 12 px, `--shoal-text` on `--shoal-surface` background, padding 0 6, radius 3, positioned with `style.setProperty('--y', pct)` |
| Offline periods | no line across the gap (separate path segments); band `fill: var(--shoal-hatch-b)` at 35% opacity with a 12 px `micro` muted label "Offline" when the band is ≥ 48 px wide |
| Route change (direct ↔ relayed) | 1 px dotted vertical line in `--shoal-warn` at that time; explained in the table, not with a legend on the chart |

**Accessibility**
- The SVG is `role="img"` with an accessible name (title) and description (summary sentence). Generate the summary from data: "Speed from Hops over the last 24 hours: 6 tests, between 24 and 48 Mbps, latest 42 Mbps at 21:00. Every test was above the 15 Mbps needed for 1080p. 2 tests were below the 30 Mbps needed for 4K. Offline from 03:10 to 04:40."
- "Show as a table" gives the same data to everyone: columns Time, Type, Speed (Mbps), Latency (ms), Route. Newest first. `<caption>` "Speed tests, last 24 hours".
- Non-text contrast: line vs surface ≥ 3:1 on all built-in themes (12.1); thresholds ≥ 3:1; the latest value is also text in the KPI.
- Not keyboard-interactive. Optional pointer tooltip (P2): nearest sample on hover/tap shows time, speed, latency, route in a small surface tooltip; the same data is in the table, so it adds no new information.

**States**: loading = surface-2 block at plot height; 0 samples = no marks, centered `meta` "No speed tests yet." + "Test now"; 1 sample = point only + "Not enough tests yet. Shoal tests every 6 hours."; error = "Couldn't load speed history." + "Try again".

---

## 10. Imagery at runtime

| Image | Source (always the Home server) | Request | Missing or failed |
|---|---|---|---|
| Card art and detail banner | backdrop of the newest title from that server, stored next to its stub at sync (DESIGN 5.4), served by core | `ApiClient.getImageUrl(localItemId, { type: 'Backdrop', tag, maxWidth: cssWidth × min(devicePixelRatio, 2), quality: 80 })` (card ≈ 400 css px, banner ≈ 1240) | try the same item's Primary (poster) cropped `object-position: 50% 20%`; then the hatched placeholder + "No pictures yet" |
| Recently shared posters | Primary image of the local stub item (or of **your** local item for "Your copy") | `type: 'Primary', tag, maxWidth: 180 × dpr (max 360), quality: 90` | hatched 2:3 box + film icon + title text |
| Picker avatars | local user image | `ApiClient.getUserImageUrl(userId, { type: 'Primary', tag, maxWidth: 64 })` | initial letter on surface-2 |

Rules:
- The admin API returns **local item ids and image tags only**. It never returns an image URL, and the page never builds one from peer data. No peer host, peer item id or token can appear in any `src`.
- `<img>` elements only (no CSS `background-image` with data, which would need inline styles). `alt=""` because the adjacent text names the item. `loading="lazy"`, `decoding="async"`, explicit `width`/`height` attributes to prevent layout shift.
- Failure handling with `img.addEventListener('error', …)` (no inline `onerror`): swap to the next fallback once, then the placeholder.
- Offline servers: stored art still loads from Home; show it desaturated (6.4). Parked servers (stubs moved): items are gone, so use the placeholder.
- Mockup images are Blender Foundation open-movie art (CC BY). Credit line only in demo or lab screenshots.

---

## 11. Copy rules (applies to every string)
- Sentence case. Plain words. Name the server or friend instead of "peer" or "remote".
- Buttons are verbs that say the outcome: "Pair a server", "Check server", "Remove server", never "OK" or "Submit".
- Errors say what happened and what to do next, in that order. Never show HTTP codes, exception text, or anything a friend's server sent.
- Numbers: `Intl.NumberFormat` grouping ("1,204"); sizes in GB/TB, base 10, one decimal; speed in Mbps (integer from 10 up, one decimal below 10); latency in whole ms; times in the viewer's locale; relative times "just now", "4 min ago", "2 hours ago", "yesterday at 21:40", then a date.
- Durations: "for 2 hours", "for 3 days" (never "since 2 h").
- Truncation: server and library names one line with ellipsis (full text stays in the DOM); titles two lines; hosts one line. Plan for strings 30% longer (translation).
- Peer-supplied text: `textContent` only; strip nothing visually, but never interpret it. In v2 that includes the friend's Home name on the approval card, device names in Devices and in the "another device" alert, and certificate fingerprints.
- Codes, invites, Server IDs, file names and commands are monospace, kept out of translation, and never split by a line break inside a group.
- Never use these words in the UI: peer, federated, SAS, check string, share user, `LogOnly`, `Enforce`, access mode, drift, recovery mode, secrets (DESIGN terms; 0.2 has the plain words).

---

## 12. Accessibility

### 12.1 Contrast (computed, WCAG 2.1 relative luminance; script kept in Lumen's scratchpad)
**Text (needs 4.5:1)**
| Pair | Dark | Light | Apple TV | Blue Radiance | WMC | Purple Haze |
|---|---|---|---|---|---|---|
| text on surface | 16.29 | 17.14 | 13.86 | 15.77 | 13.08 | 16.94 |
| muted on surface | 8.58 | 7.23 | 7.25 | 14.91 | 12.49 | 16.08 |
| link on surface | 6.94 | 5.81 | 5.92 | 7.79 | 6.46 | 7.63 |
| link on page bg | 8.10 | 6.36 | 5.69 | 5.42 | 5.24 | 7.63 |
| ok on surface / on its chip | 7.92 / 6.32 | 5.89 / 4.95 | 6.00 / 5.05 | 8.90 / 7.22 | 7.38 / 5.86 | 8.71 / 7.25 |
| warn on surface / chip | 9.10 / 7.08 | 6.55 / 5.49 | 6.67 / 5.59 | 10.23 / 8.32 | 8.48 / 6.78 | 10.01 / 8.02 |
| bad on surface / chip | 7.14 / 5.76 | 6.39 / 5.24 | 6.51 / 5.34 | 8.02 / 6.81 | 6.65 / 5.57 | 7.85 / 6.48 |
| off on surface / chip | 6.85 / 5.55 | 6.08 / 5.13 | 6.20 / 5.23 | 7.70 / 6.38 | 6.38 / 5.19 | 7.54 / 6.25 |

**Buttons and fixed pairs**
| Pair | Ratio | Result |
|---|---|---|
| white on `#0a7cab` (primary) | 4.68 | pass |
| white on `#086c95` (hover) / `#075a7d` (pressed) | 5.85 / 7.59 | pass |
| white on `#00a4dc` (the old primary) | 2.86 | **fail, replaced** |
| 87% black on `#00a4dc` (Jellyfin's own submit button, for reference) | 6.53 | pass |
| 87% black on `#48c3c8` (Purple Haze primary) | 8.51 | pass |
| white on `#424242` (quiet) / black on `#d8d8d8` | 10.05 / 14.73 | pass |
| white on `#c62828` (danger) | 5.62 | pass |
| `#f2f2f2` on scrim over a pure white image | 11.29 | pass |
| text on warn hint (dark / light) | 13.26 / 15.09 | pass |

**Non-text (needs 3:1)**
| Pair | Ratio |
|---|---|
| accent `#00a4dc` vs Dark surface / bg | 5.70 / 6.65 (Blue Radiance 6.40, WMC 5.31 on surface) |
| accent `#086c95` vs Light surface / Apple TV bg | 4.77 / 4.67 |
| primary fill `#0a7cab` vs Dark surface / Light surface | 3.48 / 3.82 |
| control border (dark 38% / light 45%) vs surface | 3.54 / 3.22 (Apple TV bg 3.24) |
| chart threshold `#8a8a8a` / `#6b6b6b` vs surface | 4.72 / 4.35 |
| hatch "in both" `#8a8a8a` vs Dark surface | 4.72 |
| Failing mockup values, now fixed: segmented border `#3a3a3a` 1.50, quiet border `#363636` 1.57, quiet fill `#262626` 1.26, seg selected fill vs unselected about 1.2 | replaced (0.3 #5, #6) |

### 12.2 Focus
- Every interactive element shows `outline: 2px solid var(--shoal-accent); outline-offset: 2px` on `:focus-visible`. Never `outline: none` without this replacement. Inside dialogs and on the primary button the ring sits on the surrounding surface (offset), so it keeps ≥ 3:1.
- Focus is never moved by polling updates. Focus moves only on: view change (to `h1`), wizard step change (to step `h2`), dialog open (first field or safe button), dialog close (back to opener), form error (to the error summary).

### 12.3 Keyboard map
| Element | Tab | Enter / Space | Esc | Arrows |
|---|---|---|---|---|
| Links, buttons | one stop each | activate | | |
| Segmented control | one stop per button | toggle (`aria-pressed`) | | not used (simple toggle buttons) |
| Access switch (6.17) | one stop per segment | on the unselected segment: opens the confirm dialog; on the selected one: nothing | | not used, so moving focus never opens a dialog |
| Protection mode cards (8.5 item 7) | the action button in the non-current card | activate (confirm first for Require sign-in) | | not used |
| Server select (native) | one stop | opens | closes | change value; panels update on `change` |
| Radio choice cards | one stop per group | Space selects | | Up/Down/Left/Right move selection (native radios) |
| Checkbox rows | one stop each | Space toggles | | |
| `details` summaries | one stop | toggle | | |
| Dialog | trapped (native `showModal`) | | closes (same as Cancel) | |
| Table wrappers that scroll | one stop (`tabindex="0"`, `role="region"`, label) | | | scroll |
| Cards | no stop of their own; name link and buttons inside | | | |

### 12.4 Screen readers
- Headings: one `h1` per view, `h2` per section, `h3` for card names and grouped headings. No skipped levels.
- Cards: `<article aria-labelledby="name-id chip-id">` so "Hops, Online" is the card's name.
- Announcements (polite region, 6.15): selection change "Showing link speed for {Server}"; state changes from polling "{Server} is now {state}" (only on change, max one message per 10 s, coalesced: "2 servers changed status"); "Sync started", "Sync finished: {added} added, {removed} removed"; pairing status; "Code copied", "Invite copied", "Steps copied"; "Settings saved"; v2: "{homeName} wants to connect. Ask {Friend} for the safety code." (v2.1 wording); "{Library} is open to everyone on this server" / "{Library} is limited to {m} people"; "Protection set to {mode}"; "{Server} paused" / "{Server} resumed"; "{Friend} paused" / "{Friend} resumed"; "Shoal signed out an unexpected device on {Friend}'s account" (on change only); "Access to {Library} re-applied"; v2.1: "Thanks. Shoal is back to normal.", "Removed from your list", "{Server} is back to how it was". Alert region: load failures, pairing failures, a wrong typed safety code ("That code doesn't match… {n} tries left"), form error summaries, and the title of a bad page banner once per page load (6.16).
- Hidden text where visuals carry meaning: "—" values ("unknown"), "·" separators are `aria-hidden`, the overlap bar and chart have full-sentence names.
- Icons are `aria-hidden`; every icon-only button has `aria-label` ("Copy command", "Copy code").
- v2.1 code input (6.10, 6.18): a real `<label>`; help and error tied with `aria-describedby`; the "{n} tries left" count is in the error text, not only in a color or icon; Approve's disabled reason is visible text, not a tooltip.

### 12.5 Touch targets and pointer
- All interactive targets ≥ 44 × 44 CSS px (buttons, standalone links, segmented buttons, icon buttons, summary rows 48, checkbox rows 48, list rows 64). Inline links inside sentences are exempt but keep 44 px line boxes where they stand alone.
- Minimum 8 px between adjacent targets.
- Hover-only information: none.

### 12.6 Zoom, reflow, motion
- Works at 320 CSS px wide and at 200% text zoom with no horizontal page scroll. Tables reflow on S: each row becomes a block with `label: value` pairs (`th` text repeated via `data-label` + CSS, or a `dl` per row). Wide tables that cannot reflow (recent tests on M) scroll inside their own focusable region.
- `prefers-reduced-motion: reduce`: no pulse, no spinner rotation (static icon + text), no transitions.
- No content flashes. Countdown changes text only.

### 12.7 Audit of the approved mockup (WCAG 2.1 AA)
Issues found: 9. Critical 0, Major 5, Minor 4. All fixed in this spec.

| # | Issue | Criterion | Severity | Fix in spec |
|---|---|---|---|---|
| 1 | Primary button white on `#00a4dc` would be 2.86:1 (owner's requested fix) | 1.4.3 | Major | `#0a7cab` 4.68:1 (5.2) |
| 2 | Server cards selectable only by pointer | 2.1.1 | Major | Server select + name link (0.3 #11) |
| 3 | Chart only an image with a static label | 1.1.1, 1.3.1 | Major | generated summary + data table (9) |
| 4 | Light themes: `#00a4dc` text 2.4:1, status colors untested | 1.4.3, 1.4.11 | Major | light signal set (4.1) |
| 5 | Blue Radiance / WMC: `#00a4dc` link text 4.45 / 4.30 on page bg | 1.4.3 | Major | `--jf-palette-primary-light` (`#33b6e3`) |
| 6 | Segmented selected state about 1.2:1 vs unselected | 1.4.11 | Minor | underline + bold (6.6) |
| 7 | Quiet button and segmented borders under 3:1 | 1.4.11 | Minor | native `.raised` fill; control border 3.54:1 |
| 8 | `aria-label` on `<dd>` for "—" is unreliable | 4.1.2 | Minor | visually hidden "unknown" text |
| 9 | Poster `alt` repeated the caption | 1.1.1 (noise) | Minor | `alt=""`, caption names the item |

Passing already in the mockup: status as icon + text everywhere, 44 px targets, heading structure, `:focus-visible` rings, image pills on a scrim.

---

## 13. Data the UI needs (for Atlas and Kiro)
Routes are DESIGN 9.6's `Shoal/Admin/*` (all `RequiresElevation`, JSON, handles only). No response contains a token, an invite after its one display, or a peer GUID; the only URL is the admin-entered server URL. Names below are DESIGN's (9.3, 9.5, 3.2, 3.2a). Anything DESIGN doesn't define yet is marked *(proposed)*: Kiro 2 mocks it as written and section 14 asks Atlas and Kiro to confirm or rename it. Mock JSON for U1 follows this table.

| Screen | Route(s) (DESIGN 9.6) | Fields the UI reads or sends |
|---|---|---|
| Overview | `GET Shoal/Admin/Status`; `POST …/Peers/{h}/Sync` (and sync all, *proposed* `POST Shoal/Admin/Sync`) | `servers[]: { handle, displayName, url, network: Tailnet\|LAN\|Public, proxy: bool, status: Online\|Degraded\|Offline\|AuthFailed\|IdentityMismatch\|TlsKeyChanged\|Disabled\|Pairing\|PairingAborted, pairingError?: <8.2.5 error kind>, pairingPhase?: beforeApproval\|awaitingConfirm (v2.2, for `interrupted`; DESIGN 4.2a `PairingAborted{kind, phase?, atUtc}`), pairingMethod?: invite\|qc *(proposed for a stopped first pairing, 8.2.5 `interrupted` copy)*, pairingStoppedUtc? (v2.1), paired: bool (false for a server whose first pairing stopped; confirmed v2.2), path: direct\|derp\|derp?\|proxy\|unknown, derpRegion?, rttMs?, mbps?, jitterMs?, lossPct?, offlineSinceUtc?, nextCheckUtc?, lastSync: { atUtc, result: ok\|partial\|failed\|held\|skipped, added, removed }, syncing: bool, titleCount, alertCount, heldRemovals, heldAdditions, activeStreams: { n, max }, verdictSummary: [{ cls, verdict }], hints: [{ kind, severity }], latest?: { localItemId, title, year, backdropTag?, primaryTag? } }`; `recentlyShared[]: { localItemId, title, year, runtimeMin, serverHandle, serverName, primaryTag?, kind: shared\|yourCopy }`; `friends[]: { handle, name, status: Active\|Paused\|AccessRemoved, libraryCount, deviceCount, unexpectedDeviceCount, lastSeenUtc?, playingNow, transcodingNow }` (DESIGN 9.3); `cooldowns: { syncAllUntilUtc? }`; banners (shape *proposed*; v2.2 confirmed the fields in DESIGN 9.5 and renamed `writtenUtc` to `atUtc`): `state: { mode: Normal\|Recovery, reason?: unreadable\|newerSchema, guardLastGood?: ok\|unreadable, restoredFrom?: { file, atUtc, needsConfirm } }, secretsUnreadable: bool, secretsReason?: wrongKey\|badKey\|bothKeys\|damaged, keySource: env\|envFile\|dpapi\|file` (v2.1 additions: `guardLastGood`, `needsConfirm`, `secretsReason`, `envFile`); v2.1 `inviteFailures: { lastHour, notice: bool }` (Peer role, DESIGN 4.1b Limits; confirmed v2.2) |
| Pair a server, watch | `POST Shoal/Admin/Peers/Probe`; `POST`/`GET`/`DELETE Shoal/Admin/Pairing`; `POST Shoal/Admin/Pairing/Confirm`; `GET …/Peers/{h}/RemoteLibraries`; `POST …/Peers/{h}/Libraries` | probe sends `{ url, network, proxyUrl?, invite?, lanAck? }`, returns `{ probeHandle, serverName, version, versionTested: bool, rttMs, serverIdPrefix, warnings[] }` (**no** `safetyCode`); start sends `{ probeHandle, displayName, method: invite\|qc }`; session `{ direction: watch\|share, method: invite\|qc, step, state, code?, safetyCode?, expiresUtc, error?, phase?, peerSilent, paired }` (DESIGN 9.5 v2.2), `error` ∈ safety `requestReplaced, codesDidNotMatch, serverChanged, adminAccount, badPeerResponse`; stopped `expired, denied, codeMismatch, inviteInvalid, quickConnectOff, quickConnectUnreachable, sessionLimitBlocked, interrupted, network, rateLimited, server` (v2.1 added three from DESIGN 4.2a; v2.2 adds `codeMismatch` and `phase: beforeApproval\|awaitingConfirm` with `interrupted`; `sessionLimitBlocked` is *proposed*, the kind Kiro is adding after Pilot-02, not yet in DESIGN); `code` and `safetyCode` absent until the baseline poll succeeded (Quick Connect); `peerSilent: bool` (3 missed polls, not final), `paired: bool` (a re-pair), `expiresUtc` in AwaitingConfirm = the 15-minute confirm deadline; start answers `409 { error: "sessionActive" }` while a session lives and `409 { error: "probeExpired" }` for a used or old probe (confirmed v2.2); confirm sends `{ match: bool }`, Quick Connect only, `409` outside AwaitingConfirm; remote libraries `{ mappingKey, name, type, count, selectable }`; add sends *(proposed)* `[{ mappingKey, localLibraryName, selectedUserIds }]`. *(proposed)* session `peerHandle` once the server exists, and `deviceName` (the exact `Shoal: {Home} ({check})` text) |
| Pair a server, share; Friend detail | `GET`/`POST Shoal/Admin/Shares`; `GET`/`PUT`/`DELETE Shoal/Admin/Shares/{id}` (`DELETE` = stop sharing); `POST …/{id}/Pause`, `…/Resume`; `POST …/{id}/Invites`, `DELETE …/{id}/Invites/current`; `GET`/`POST …/{id}/Approval`; `DELETE …/{id}/Devices/{deviceHandle}` | create and update send `{ friendName, libraryIds, maxBitrateMbps?, maxConcurrentTranscodes }` (names from `state.json`, 3.2a); invite create returns *(proposed)* `{ invite, expiresUtc }` once; approval `{ state: pending\|approved\|denied\|cancelledWrongCode\|collected\|uncollected, homeName, homeServerIdPrefix, addressClass: tailnet\|lan\|public, triesLeft }` (DESIGN 9.5 v2.2; v2.2 renames `serverIdPrefix` → `homeServerIdPrefix` and `notCollected` → `uncollected`; with nothing pending there is no approval and the card isn't shown) (v2.1: **no `safetyCode`**; the sharing side never receives it, DESIGN 4.1b step 3); approve sends `{ approve: true, sas: "<as typed>" }` or `{ approve: false }`; answers `{ match: true, state: "approved" }`, `{ match: false, triesLeft }`, on the third miss `{ match: false, triesLeft: 0, state: "cancelledWrongCode" }`, and `{ state: "denied" }` for Deny; share detail *(proposed)* `{ id, friendName, status, shareUserName, sinceUtc, libraryIds, maxBitrateMbps, maxConcurrentTranscodes, deviceCount, invite: { state: none\|active\|expired, expiresUtc? }` (v2.1: `cancelledAfterFailures` removed), devices: [{ deviceHandle, name, app, version, firstSeenUtc, lastSeenUtc, expected: bool, signedOut: bool }] }`; stop-sharing result *(proposed)* `{ disabled: bool, signedOut: n, remaining: n, deleted: bool }`; v2.1 `DELETE Shoal/Admin/Shares/{id}` carries `{ confirmName }` (DESIGN 9.5, 9.6; confirmed v2.2) |
| Server detail | `GET`/`PUT`/`DELETE Shoal/Admin/Peers/{h}` (`?deleteLibrary=true`); `POST …/{h}/Sync`, `…/Pause`, `…/Resume`, `…/TrustCertificate`; `POST …/{h}/Holds/{kind}/Release`, `…/Keep` (`kind` = `removals`\|`additions`); `DELETE …/{h}/Libraries/{m}` | Overview server fields plus `serverIdPrefix, pairedUtc, pairingMethod: invite\|qc, deviceLock?: confirmed\|missing` (Quick Connect only), v2.1 `sessionLimit?: ok\|notSet` and `accountNotes?: [passwordSelfChange\|unusedOptions]` (Quick Connect only, DESIGN 4.1 "What Home checks"; fields confirmed in 9.5 v2.2, value names still *proposed*), `pairingSession?` (v2.2: the handle of a live pairing session, absent when none; address fields locked while present), `pairingError?`, `pairingStoppedUtc?`, `alerts[]` *(proposed shape)* `{ kind, severity, count?, examples?: [title], deviceName?, policy?: [allLibraries\|deletion\|download\|liveTv\|remoteControl\|allDevices], localItemId?, title?, pinnedKey?, presentedKey? }` with the kinds of 8.3.2 (v2.1 adds `sessionLimit`); `PUT` with a new host and `DELETE` carry `confirmName` (DESIGN 9.5, 9.6, 4.4; confirmed v2.2); v2.2 `POST …/{h}/DismissPairing {}` → `{ removed: true }` or `{ restored: true, status }`, `409` when nothing is stored or a session is live, `usage: { diskBytes, quotaBytes, stubs, people, genres, studios }`, `libraries[]` (as Libraries), settings `{ maxConcurrentStreams, priority, proxyUrl, tlsPinned }`, `streams[]` for Playing now |
| Speed panel / diagnostics (phase 4) | `GET …/{h}/Speed?range=24h\|7d\|30d`, `GET …/{h}/Telemetry`, `POST …/{h}/SpeedTest` | `samples[]: { t, kind, mbps?, rttMedianMs?, rttP90Ms?, jitterMs?, lossPct?, path, error? }`, `offline[]: { fromUtc, toUtc }`, `routeChanges[]: { t, from, to }`, `kpis`, `ladder[]: { cls, verdict, neededMbps }`, `bottleneck: { kind }`, `localApiAvailable: bool`, `cooldowns: { testUntilUtc?, deepUntilUtc? }` |
| Overlap | `GET …/{h}/Overlap` | `{ computedUtc?, both: { count, localBytes }, gain: { count, peerBytes }, byQuality: [{ q, bothCount, localBytes, gainCount, peerBytes }], betterCopyCount? }` |
| Libraries & access | `GET Shoal/Admin/Libraries`; `PUT Shoal/Admin/Libraries/{m}/Access`; `POST Shoal/Admin/Libraries/{m}/Reconcile` | mappings `{ mappingKey, localLibraryName, count, lastSync, heldRemovals, heldAdditions, access: { mode: Selected\|Everyone, selectedUserIds, includeRestricted }, drift: [{ userId, kind }] }` plus *(proposed)* `serverHandle, serverName` for grouping; users `{ id, name, imageTag?, isAdmin, isDisabled, allLibraries, managedByShoal, restricted, isShareUser }` (v2.1 `isShareUser`, DESIGN 5.6; the page drops those rows, and `PUT` answers 400 if one is sent); access sends `{ mode, selectedUserIds, includeRestricted, ratingsAcknowledged }`; `drift.kind` *(proposed)* `extraAccess\|missingAccess\|managed\|unmanaged`; "You share" reads `friends[]` from Status plus `GET Shoal/Admin/Shares` |
| Settings | `GET`/`PUT Shoal/Admin/Settings`; `GET`/`PUT Shoal/Admin/Hardening`; `GET Shoal/Admin/State`, `POST …/State/Restore`, `POST …/State/StartOver`; `DELETE Shoal/Admin/All` | settings per 8.5.1 (DESIGN 3.2 names + `stubRoot`, `stubRootCheck: ok\|network\|insideLibrary\|notWritable`, `tailscaleDetected`, `keySource`, *(proposed)* `secretsVolumeUnsafe`); hardening `{ mode: LogOnly\|Enforce\|Off, modes: [..], sinceUtc, counters[], guardErrors, shieldHits, transcodeCapHits }` plus v2.1 `inviteFailures: { lastHour, notice }`, `proxySuspected: bool` (confirmed v2.2), sends `{ mode }`; state *(proposed)* `{ mode, files: [{ file, writtenUtc, kind: bak\|daily }], lastBackupUtc, backupByCopy, findings: [{ kind, peerHandle?, library? }] }`; restore sends *(proposed)* `{ file }`; v2.1 `POST Shoal/Admin/State/ConfirmRestore {}` clears `restoredFrom.needsConfirm` (DESIGN 9.6 v2.2; `409` when nothing awaits confirmation); Start over and `DELETE Shoal/Admin/All` carry `confirmName` |

The current admin's id comes from `ApiClient.getCurrentUserId()` ("You" in the picker). Rate-limited actions return 429 with `Retry-After`; the UI turns it into the cooldown text (6.2).

---

## 14. Dependencies on DESIGN v2, v2.1 and v2.2 (Atlas)

**Resolved by DESIGN 9.5 (2026-10-14).** Every former **[v2]** marker in this spec is closed.
| # | Question | DESIGN answer | Where it landed |
|---|---|---|---|
| 1 | Safety code format, length, timing, the stock friend's side | Invite path: 8 Crockford base32 characters, `K7QM 2XA4`, ~~on both servers~~ **v2.1: shown on Home only; the friend types it** (DESIGN 4.1b step 3). Quick Connect path: 4 characters, `K7QM`, shown with the 6-digit code; the stock friend sees it as the device name `Shoal: {Home} (K7QM)`. Never at the check step. Server detail shows **Server ID**. | 6.10, 8.2.3 steps 2-3, 8.2.8, 8.3.1, 6.18 |
| 2 | Invite format, lifetime, creator | Sharing side creates it; `shoal1-<8 hex>-<26 Crockford in groups of 4>`; input ignores case, spaces, hyphens; 15 min, single use; ~~3 failed attempts cancel all invites~~ **v2.1: withdrawn; failures are only slowed down, invites survive** (DESIGN "Accepted non-adoptions", 4.1b Limits). Remove "Enter the code {Friend} sent", "Approve a code" and the "If they have Shoal" Quick Connect instruction. | 6.10, 8.2.3 step 1, 8.2.4, 8.2.5 share side, 8.7 (all three removed) |
| 3 | Default-deny mechanics | Ticking an "all libraries" account changes nothing in Jellyfin; leaving it unticked makes Shoal keep a list for it. New libraries are hidden from other admins too; only the pairing admin is pre-selected. Plus the D4 switch. | 6.17, 8.2.3 step 4, 8.4.1, 8.4.3 notes, 8.4.4 |
| 4 | Does "Off" survive? | Yes, as a troubleshooting escape hatch; `modes: ["LogOnly","Enforce","Off"]`; `Off` under "Troubleshooting". | 8.5 item 7 |
| 5 | Renames and pairing-session contract | `Shoal/Admin/`; one session per direction and target, in memory, survives reloads, ends with its code or invite; `GET` or 404, `DELETE` cancels. | 2.3, 8.2.6, 13 |

### 14.1 Still open from v2
The UI is designed; the shapes are mocked as written in 13 until Atlas or Kiro confirms.
1. **Banner data**: where recovery mode, the newer-schema reason, "restored from" and `secretsUnreadable` appear (proposed: on `GET Shoal/Admin/Status`), and the `GET Shoal/Admin/State` shape (files, last backup, findings) plus the `Restore` body.
2. **Server alerts**: the `alerts[]` shape and `kind` values (8.3.2), including the certificate fingerprints for `tlsKeyChanged` and the device name for `foreignDevice`; the `pairingError` field on a `PairingAborted` card (v2.2: confirmed, DESIGN 9.5 "Peer detail"); `deviceLock` and `usage` on `GET Peers/{h}`.
3. **Holds `Keep`**: confirm that `Holds/removals/Keep` keeps the titles and the hold (auto-release still after 3 syncs over 24 h), and that `Holds/additions/Keep` keeps holding without re-alerting until the next sync adds more.
4. **Drift `kind` values**: proposed `extraAccess`, `missingAccess`, `managed`, `unmanaged` (8.4.5).
5. **Share side shapes**: invite create response, approval `GET` (including `homeName`, `addressClass`, `state`), share detail with `invite.state` and `devices[]`, and the stop-sharing result (8.7).
6. **Missing routes**: Overview "Sync now" for all servers (proposed `POST Shoal/Admin/Sync`), the one-click "Block Shoal titles for profiles with parental limits" (DESIGN 5.6), and "Relink" / "Delete" for startup check findings (DESIGN 3.7). Until they exist, those buttons are hidden (Sync now falls back to one `POST …/Peers/{h}/Sync` per server).
7. **Quick Connect device name**: the page needs `{Home}` (this server's name) or the full device name; proposed session field `deviceName`, fallback `ApiClient.serverInfo().Name`.
8. **Libraries grouping**: `serverHandle` and `serverName` on each mapping; the body of `POST …/Peers/{h}/Libraries` (proposed `[{ mappingKey, localLibraryName, selectedUserIds }]`).
9. **Settings extras**: the field for "I understand the risk" on a network-share library folder (proposed `stubRootNetworkAck`) and the "this drive can't protect secret files" flag (proposed `secretsVolumeUnsafe`).

### 14.2 Open after v2.1: all resolved in DESIGN v2.2
Every item below was answered in DESIGN v2.2 ("Changes in v2.2", Lumen block L1-L8). The original question is kept for history; **Resolved** names the DESIGN section and what this spec changed.

**DESIGN text that still said v2.** This spec followed the patched 4.x sections; Atlas aligned these lines:
1. 9.3 "Pair a server, watch direction" step 3 still lists **The codes match (informational)** and **They don't match** for the invite path. 4.2 step 3 removed them. This spec has neither on the invite path.
   **Resolved** (L3): DESIGN 9.3 "Pair a server, watch direction" step 3 now has no compare buttons on the invite path, and lists `denied` and `codeMismatch` separately. No change here beyond the `codeMismatch` row (8.2.5).
2. 9.3 "Pair a server, share direction" lists "safety code" on the approval card. 4.1b step 3: the card has a code **input**, never the code. Followed 4.1b.
   **Resolved** (L3): DESIGN 9.3 "Pair a server, share direction" and 9.5 #1. Matches 6.18.
3. 9.5 #2 still says 3 failed redemptions cancel every invite and asks for "Invites cancelled after failed attempts". Withdrawn in the v2.1 changelog; removed here.
   **Resolved** (L3): DESIGN 9.5 #2 (struck through, withdrawn in v2.1).
4. 9.5 `requestReplaced` copy says "disable the sharing account"; 4.2a and 4.5 say delete it and make a new one. Followed 4.2a.
   **Resolved** (L3): DESIGN 9.5 "Data shape changes", last sentence of the session bullet. Matches 8.2.5.
5. 9.5 error list lacks `quickConnectUnreachable`, `badPeerResponse`, `interrupted` (4.2a). Added here.
   **Resolved** (L3): DESIGN 9.5 "Data shape changes" now has the complete list, split into safety and stopped kinds, plus the new `codeMismatch`. Section 13 copies it.
6. 9.5 #5 says a Home restart shows "The code expired"; 4.2a says a restart in AwaitingConfirm is `interrupted`. This spec uses Expired before approval and `interrupted` after (8.2.6). Please confirm.
   **Resolved** (L3): DESIGN 9.5 #5 and 4.2a "Abort is durable". Confirmed, with one change: the stored kind is `interrupted` in **both** cases, with `phase`, so the before-approval outcome can carry the stray-device hint. 8.2.5 now has two `interrupted` rows; Expired no longer covers restarts.
7. 9.6 approval body is `{approve}`; 4.1b needs `{approve, sas}`. 9.6 doesn't name the typed-name field for destructive calls (proposed `confirmName`).
   **Resolved** (L2, L8): DESIGN 9.6 (`POST …/Approval {approve:true, sas}` or `{approve:false}`), 4.1b step 3 (the server judges; Approve enables at 8 valid characters, as 6.18 already said), 9.5 "Destructive calls carry the typed name as `confirmName`".

**New shapes and routes this spec proposed:**
8. **Dismiss a stopped pairing**: no route in 9.6. Proposed `POST Shoal/Admin/Peers/{h}/DismissPairing`: for a never-paired server it removes the record; for a re-pair it restores the server's previous state.
   **Resolved** (L7): DESIGN 9.6 and 4.2a. Answers `{removed:true}` or `{restored:true, status}`, `409` otherwise (13).
9. **Approval answers**: wrong code returns `{ match: false, triesLeft }`; the third miss sets approval `state` to `cancelledWrongCode`. Does the invite survive that cancel (it is already bound to Home's commitment)? This spec assumes it doesn't and offers "Create a new invite". Home sees `denied` (copy covers both causes).
   **Resolved** (L4): DESIGN 4.1b step 3, 4.2 step 3, 9.5 "Approval". The invite ends with the request (as assumed). Home does **not** see `denied`: it gets the new stopped kind `codeMismatch`, which now has its own row (8.2.5). Three tries kept.
10. **Session flags**: `peerSilent` (3 missed polls) and the 15-minute AwaitingConfirm deadline in `expiresUtc`; `probeExpired` on a reused or old probe; `paired` on a server whose first pairing stopped.
    **Resolved** (L8): DESIGN 9.5 "Data shape changes" (session bullet). Note: DESIGN's `paired` is a **session** flag meaning "a re-pair"; the server-card `paired` in 13 (false after a stopped first pairing) is the same fact seen from the record, kept as is.
11. **Peer fields**: `sessionLimit`, `accountNotes`, `pairingSession`, `pairingStoppedUtc`, the new `policy` values (`liveTv`, `remoteControl`, `allDevices`) and the `sessionLimit` alert kind.
    **Resolved** (L8): DESIGN 9.5 "Peer detail". `pairingSession` is a handle, not a boolean (13 updated).
12. **Status and Settings**: `inviteFailures { lastHour, notice }` (the "N wrong invite attempts" notice of 4.1b), `proxySuspected` (8.4 (b) warning), `backupByCopy` (3.7 step 3), `state.guardLastGood`, `restoredFrom.needsConfirm` plus `POST Shoal/Admin/State/ConfirmRestore` (3.7 "until the admin clicks Confirm"), `secretsReason` (`badKey`, `bothKeys`), `keySource: envFile`.
    **Resolved** (L6, L8): DESIGN 9.5 "Status and Settings", 9.6 `ConfirmRestore`, 3.7. `restoredFrom` is `{file, atUtc, needsConfirm}` (13 renamed `writtenUtc`).
13. **Users**: `isShareUser` on user rows (5.6 mentions it "in 9.5", but 9.5's user shape doesn't list it).
    **Resolved** (L8): DESIGN 9.5 "Users".

Also resolved in v2.2 without a 14.2 number: the pop-up goes to safety kinds only (L1, DESIGN 4.2a "Two kinds"; 8.2.9), and the Quick Connect re-pair needs the session limit at 0, not a deleted device (L5, DESIGN 4.1 step 9, 4.4; 8.2.3 and 8.2.8 were updated by Atlas).

### 14.3 Open after v2.2 (for Atlas, Kiro, Warden and the owner)
1. **Owner and Warden: "Which apps work" (8.3.12).** Ships after the owner confirms G1 Option C and Warden reads the text (DESIGN "Open after v2.2" #1, #2).
2. **Atlas: `pairingMethod` on a stopped first pairing** (13, *proposed*). The `interrupted` before-approval copy differs by path (8.2.5). If the record can't carry it, the UI falls back to the Quick Connect copy.
3. **Atlas, small DESIGN cleanup:** 4.1b step 3 still contains the v2.1 sentence "Approve stays disabled until the typed code equals the one the Peer computed" right before the v2.2 correction. The correction wins; this spec follows it (6.18).
4. **Atlas: titles for `interrupted` on the invite path.** DESIGN 4.2a names only "The code expired"; this spec uses "The invite expired" on the invite path, matching the Expired row. Confirm or overrule.
5. **Kiro and Atlas: `sessionLimitBlocked`** (8.2.5, 13). Name proposed; Kiro is adding the kind. Pilot-02 shows Home itself only sees the code never being approved (a 404 on redeem), so tell Lumen how Home decides it's this case; the copy says "probably" on purpose. Atlas: DESIGN 4.1 step 5 still says "If Jellyfin says 'maximum number of sessions'", which the friend never sees (Pilot-02 finding 2), and the new kind belongs in DESIGN 4.2a and 9.5.
6. **Value names still proposed** in 13 for `sessionLimit` (`ok|notSet`) and `accountNotes` (`passwordSelfChange|unusedOptions`); DESIGN 9.5 confirms the fields, not their values.

---

## 15. Acceptance checklist (Kiro self-check, then Lumen design review)
Mark each item with the evidence used (screenshot at the stated width and theme, or a test).

**A. Layout and owner edits**
- [ ] A1 Content column caps at 1240 px at wide widths; nothing is cut off at the bottom of any screen; no inner scroll areas except data tables.
- [ ] A2 Page height follows content on every screen (no fixed heights anywhere in `shoal.css`).
- [ ] A3 L/M/S grids match 3.3 at container widths 1240, 1080, 900, 640, 375, 320.
- [ ] A4 Phone (375 px): 16 px side gutter, header actions 2-up, cards single column, posters 2-up, no horizontal scroll.

**B. Fidelity to the approved mockup**
- [ ] B1 Overview matches the mockup's composition and spacing (header, 3 cards, speed panel, overlap, posters) at 1440 px, Dark theme, with the 0.3 changes applied.
- [ ] B2 Only Noto Sans 400/700 from the dashboard; no font requests in the network log.
- [ ] B3 Real images on cards, banner and posters, loaded from the Home server only.
- [ ] B4 No gradient washes, no left-border accent cards, no emoji.

**C. Theming**
- [ ] C1 Screenshots in all six built-in themes (Dark, Light, Apple TV, Blue Radiance, Purple Haze, WMC): surfaces follow the theme; text and chips readable.
- [ ] C2 Primary button `#0a7cab` + white in Dark/Light/Apple TV/Blue Radiance/WMC; theme primary + contrast text in Purple Haze.
- [ ] C3 A custom CSS rule setting `--shoal-fill` changes the button (override works).
- [ ] C4 Jellyfin's `.raised:focus` and `.button-submit:focus` color changes do not appear inside Shoal.

**D. States and copy**
- [ ] D1 Every state in 8.1.5, 7, 8.2.5 (both tables), 8.3.9, 8.4.6, 8.7, 8.8 and 9 reachable in the lab (fakepeer or stopped jf-peer) and screenshotted.
- [ ] D2 All copy matches section 8 (diff `STRINGS` against this file); none of the words banned in 11 (peer, federated, SAS, `LogOnly`, `Enforce`…), HTTP codes or raw errors in the UI.
- [ ] D3 Offline server: stored backdrop greyed out (owner D5), "Offline for …" copy, titles described as still listed.
- [ ] D4 (v2.1) Both "Pairing stopped for your safety" paths save nothing (API check): `codesDidNotMatch` from "They don't match" on the Quick Connect path, and `requestReplaced` from fakepeer's vanished request; each shows its 8.2.5 copy, stays on its Overview card after a reload **and after a Home restart** (T-PAIR-16), clears only on Dismiss or a successful pairing, and "Start again" then requires the new-account checkbox. A network blip, an expired code and `interrupted` show "Pairing stopped" (warn), never "Stopped for safety". (v2.2) Only the five safety kinds send the Jellyfin pop-up; `expired`, `network`, `interrupted`, `denied` and `codeMismatch` send none (check the dashboard of a second admin).
- [ ] D5 (v2) Safety code shows 8 characters in two groups on the invite path and 4 on the Quick Connect path; it never appears at "Check the server"; Server detail shows "Server ID", never "Safety code".
- [ ] D6 (v2) No "Approve a code" box, no "Enter the code {Friend} sent" option, no "If they have Shoal" instruction anywhere.
- [ ] D7 (v2) Access switch: a new library is "Only selected users" with only the pairing admin; switching to Everyone and back restores the same selection (T-VIS-4 through the UI); both confirm dialogs list the right people; the picker's top row says "Select all".
- [ ] D8 (v2) The invite text is visible once, is gone after leaving and returning ("An invite is active" instead), and never appears in `localStorage`, the URL or the console.
- [ ] D9 (v2) "Protect your shared videos" shows two choice cards; `Off` only under Troubleshooting; to Require sign-in asks once, back to Log only doesn't ask.
- [ ] D10 (v2) Recovery mode (corrupt state in the lab) shows the blocking banner on every screen with Restore and Start over; a flipped byte in `secrets.enc` shows the keys banner and "Pair again" on every card. (v2.1) A restore from `.bak` shows the restored banner on every screen and for every admin until Confirm, with every friend's library switched to "Only selected users"; a 31-byte `JELLYFIN_SHOAL_KEY` shows the `badKey` variant.
- [ ] D11 (v2.1) Invite path: Home shows the 8-character code with "Copy code" and **no** "The codes match" / "They don't match"; the sharing side never shows the code (DOM, console and network); Approve is disabled until 8 valid characters; a wrong code shows "{n} tries left" and keeps the typed value; the third wrong code shows "Request cancelled" and Home shows "The safety code didn't match" (v2.2 `codeMismatch`; was "turned down"), while Deny shows "Your friend chose Deny" (T-PAIR-8 through the UI).
- [ ] D12 (v2.1) No string "Invites cancelled after failed attempts" anywhere; 100 wrong invites from a script leave a live invite working and show only the calm notice on Overview and Settings.
- [ ] D13 (v2.1) Steps for your friend: step 3 shows Turn on / Turn off / Leave for now and the "Jellyfin has no switch for these" note; step 9 is present; the footer says delete to stop and new password to pause; no "disable the user" anywhere in friend-facing copy. "Copy steps" output matches the plain-text format in 8.2.8. Step 9 and the pause text are released only with a T-SEC-22 pass on record.
- [ ] D14 (v2.1) Quick Connect: no code is visible before the baseline poll ("Getting a code…"); the warn "Waiting for {Server} to answer…" row appears after 3 missed polls and recovers on its own; `quickConnectUnreachable`, `badPeerResponse` and `interrupted` are reachable with fakepeer; Confirm shows a 15-minute countdown.
- [ ] D15 (v2.1) Invite path with "Home network" and an `http://` address can't be submitted and shows the "Invites need Tailscale or an https address" message; changing a paired server's host asks for the typed name and then shows "Access removed".
- [ ] D16 (v2.2) Restart Home during a Quick Connect pairing before approval: the card shows "The code expired" with the delete-the-Shoal-device hint, warn, no pop-up; restart it in AwaitingConfirm: "Shoal restarted before you confirmed". The same restart on the invite path shows "The invite expired". The invite cancelled after 3 wrong codes can't be redeemed again (Home sees `codeMismatch`, then the Peer answers 404). A Quick Connect re-pair with the friend's session limit still at 1 ends in "Your friend's server didn't accept the code" (warn, no pop-up), and no UI string anywhere mentions "too many sessions" or "maximum number of sessions". "Which apps can play…" appears closed in Server detail and Settings > Playback, with icon + text in both lists; it ships only with the 8.3.12 gate on record.

**E. Accessibility**
- [ ] E1 Contrast spot-checks in DevTools match 12.1 for text, chips, links, buttons, focus ring, chart line.
- [ ] E2 Keyboard-only run of every screen: order matches 8.1.6 / 8.3.10, every action reachable, visible focus everywhere, Esc closes dialogs and returns focus.
- [ ] E3 NVDA (Windows) or VoiceOver pass: card names include status; chart summary and table read correctly; announcements from 12.4 occur once, not on every poll.
- [ ] E4 All targets ≥ 44 px (DevTools overlay on S).
- [ ] E5 200% zoom and 320 px width: no loss of content or function.
- [ ] E6 Reduced motion: no pulse, no spin.
- [ ] E7 axe DevTools: zero violations on each screen (Dashboard chrome excluded).
- [ ] E8 (v2) Access switch by keyboard: Tab reaches each segment, Enter or Space on the other segment opens the confirm dialog, Cancel restores the old state and returns focus; Tab alone never opens a dialog.
- [ ] E9 (v2) A bad page banner is announced once per page load, not on every poll; the view's `h1` is described by it.

**F. Security and CSP**
- [ ] F1 `shoal.html` contains no `<script>`, no `on*=` attribute, no `style=` attribute, no `${`.
- [ ] F2 With a test CSP `script-src 'self'; style-src 'self' 'unsafe-inline'` injected by a proxy, every Shoal screen works (core needs inline styles; Shoal must not).
- [ ] F3 T-SEC-11: server, library, title and device names set to `<img src=x onerror=alert(1)>` render as text on every screen.
- [ ] F4 HAR of a full session: every image `src` is `/Items/{localId}/Images/…` or `/Users/{id}/Images/…` on the Home origin; no peer host, peer id or token anywhere in the DOM or requests.
- [ ] F5 Admin responses contain handles only (no GUIDs other than local item and user ids).
- [ ] F6 (v2) T-PAIR-7 and T-SEC-11 with v2 strings: a friend's Home name, device names and the "another device" name set to `<img src=x onerror=alert(1)>` render as text on the approval card, Devices list and alerts.
- [ ] F7 (v2.1) An automated scan of every share-side admin JSON response (`Shares/*`, `Approval`) finds no safety code; the picker's user list never contains a sharing account; destructive calls send `confirmName`.

**G. Behavior**
- [ ] G1 Browser Back moves between screens; reload keeps the screen (`view` param).
- [ ] G2 Polling pauses when the tab is hidden and on leaving the page (network log).
- [ ] G3 Cooldowns show "Available again in …" from `Retry-After`.
- [ ] G4 Chart re-renders crisply on resize (circles stay round), handles 0, 1 and many samples and offline gaps.

---

## 16. Open UI questions for the owner
All three are answered (`docs/DECISIONS.md`, 2026-10-14):
1. ~~Offline servers' pictures~~ **D5:** the stored backdrop greyed out with an "Offline" tag (6.4, 7).
2. ~~"Who can see this" default~~ **D3/D4:** the admin who adds the library is pre-ticked, nobody else; one switch opens it to "Everyone on this server", including later users, and switching back restores the selection (6.17, 8.4).
3. ~~Sharing side placement~~ **D6:** "Friends watching from you" stays at the bottom of Overview (8.1.1).

No new owner questions from this spec. Where DESIGN 12 lists open owner questions, the UI follows Atlas's defaults: "Everyone" leaves out profiles with parental limits unless included (8.4.4); Quick Connect to an internet address is allowed with a warning (8.2.3 step 2); large additions wait for the admin with no automatic release (8.3.2); certificate pinning is off by default (8.3.7).

---

## Appendix A: icon sprite (put once at the end of `.shoal-root`)
```html
<svg class="shoal-sprite" aria-hidden="true" focusable="false" width="0" height="0">
  <defs>
  <symbol id="shoal-i-ok" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M8 12.5l2.7 2.7L16 9.5"/></symbol>
  <symbol id="shoal-i-check" viewBox="0 0 24 24"><path d="M5 12.5l4.5 4.5L19 7.5"/></symbol>
  <symbol id="shoal-i-x" viewBox="0 0 24 24"><path d="M6.5 6.5l11 11M17.5 6.5l-11 11"/></symbol>
  <symbol id="shoal-i-warn" viewBox="0 0 24 24"><path d="M12 3.8L2.8 19.6h18.4z"/><path d="M12 10v4.2M12 16.9v.2"/></symbol>
  <symbol id="shoal-i-relay" viewBox="0 0 24 24"><path d="M3 12h4.5M16.5 12H21"/><path d="M12 7.5l4.5 4.5-4.5 4.5-4.5-4.5z"/></symbol>
  <symbol id="shoal-i-direct" viewBox="0 0 24 24"><path d="M3 12h17M15 7l5 5-5 5"/></symbol>
  <symbol id="shoal-i-offline" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M5.7 5.7l12.6 12.6"/></symbol>
  <symbol id="shoal-i-pause" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M10 8.5v7M14 8.5v7"/></symbol>
  <symbol id="shoal-i-keyoff" viewBox="0 0 24 24"><rect x="5" y="10.5" width="14" height="10" rx="2"/><path d="M8.5 10.5V7.5a3.5 3.5 0 0 1 7 0v3M10 13.5l4 4M14 13.5l-4 4"/></symbol>
  <symbol id="shoal-i-shield" viewBox="0 0 24 24"><path d="M12 3l7.5 3v5.5c0 4.5-3.2 8.2-7.5 9.5-4.3-1.3-7.5-5-7.5-9.5V6z"/><path d="M12 8.5v4.5M12 16v.2"/></symbol>
  <symbol id="shoal-i-clock" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></symbol>
  <symbol id="shoal-i-sync" viewBox="0 0 24 24"><path d="M20 11a8 8 0 0 0-14.6-4.5M4 13a8 8 0 0 0 14.6 4.5"/><path d="M5 3v4h4M19 21v-4h-4"/></symbol>
  <symbol id="shoal-i-plus" viewBox="0 0 24 24"><path d="M12 5v14M5 12h14"/></symbol>
  <symbol id="shoal-i-lock" viewBox="0 0 24 24"><rect x="5" y="10.5" width="14" height="10" rx="2"/><path d="M8.5 10.5V7.5a3.5 3.5 0 0 1 7 0v3"/></symbol>
  <symbol id="shoal-i-globe" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.5 2.6 3.8 5.6 3.8 9s-1.3 6.4-3.8 9c-2.5-2.6-3.8-5.6-3.8-9S9.5 5.6 12 3z"/></symbol>
  <symbol id="shoal-i-home" viewBox="0 0 24 24"><path d="M4 11l8-6.5 8 6.5M6 9.5V20h12V9.5"/></symbol>
  <symbol id="shoal-i-play" viewBox="0 0 24 24"><path d="M8 5.5v13l10.5-6.5z"/></symbol>
  <symbol id="shoal-i-up" viewBox="0 0 24 24"><path d="M12 19V5M6 11l6-6 6 6"/></symbol>
  <symbol id="shoal-i-down" viewBox="0 0 24 24"><path d="M6 9l6 6 6-6"/></symbol>
  <symbol id="shoal-i-left" viewBox="0 0 24 24"><path d="M15 6l-6 6 6 6"/></symbol>
  <symbol id="shoal-i-right" viewBox="0 0 24 24"><path d="M9 6l6 6-6 6"/></symbol>
  <symbol id="shoal-i-share" viewBox="0 0 24 24"><circle cx="6" cy="12" r="2.5"/><circle cx="18" cy="6" r="2.5"/><circle cx="18" cy="18" r="2.5"/><path d="M8.3 11l7.4-3.8M8.3 13l7.4 3.8"/></symbol>
  <symbol id="shoal-i-copy" viewBox="0 0 24 24"><rect x="8.5" y="8.5" width="11" height="11" rx="2"/><path d="M15.5 8.5V6A1.5 1.5 0 0 0 14 4.5H6A1.5 1.5 0 0 0 4.5 6v8A1.5 1.5 0 0 0 6 15.5h2.5"/></symbol>
  <symbol id="shoal-i-eye" viewBox="0 0 24 24"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="3"/></symbol>
  <symbol id="shoal-i-eyeoff" viewBox="0 0 24 24"><path d="M2.5 12S6 5.5 12 5.5c2 0 3.7.7 5.1 1.7M21.5 12S18 18.5 12 18.5c-2 0-3.7-.7-5.1-1.7"/><path d="M4 20L20 4"/></symbol>
  <symbol id="shoal-i-film" viewBox="0 0 24 24"><rect x="4" y="3.5" width="16" height="17" rx="2"/><path d="M8 3.5v17M16 3.5v17M4 8h4M4 12h4M4 16h4M16 8h4M16 12h4M16 16h4"/></symbol>
  <symbol id="shoal-i-server" viewBox="0 0 24 24"><rect x="4" y="4" width="16" height="7" rx="1.5"/><rect x="4" y="13" width="16" height="7" rx="1.5"/><path d="M7.5 7.5h.1M7.5 16.5h.1"/></symbol>
  <symbol id="shoal-i-info" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 11v5.5M12 7.8v.2"/></symbol>
  <!-- filled shape used only by the Shoal mark (appendix B); 40 x 20 units, facing right -->
  <path id="shoal-fish" d="M40 10C34 2 24 0 17 2 13 3 10 6 8 8L1 3l2 7-2 7 7-5c2 2 5 5 9 6 7 2 17 0 23-8z"/>
  </defs>
</svg>
```
Use: `<svg class="shoal-icon" aria-hidden="true" focusable="false"><use href="#shoal-i-ok"/></svg>`. CSS: `.shoal-icon { width: 20px; height: 20px; flex: none; fill: none; stroke: currentColor; stroke-width: 1.75; stroke-linecap: round; stroke-linejoin: round; } .shoal-icon--s { width: 16px; height: 16px; }`. The sprite element is hidden with `.shoal-sprite { position: absolute; width: 0; height: 0; overflow: hidden; }` (from the stylesheet, not inline).

## Appendix B: Shoal mark (empty state, pairing success)
Three fish moving together, flat, single color (`currentColor` = `--shoal-accent`), eyes cut out in `--shoal-surface`. Decorative: `aria-hidden="true"`. The fish shape is defined once in the sprite (appendix A), so several marks on one page never duplicate an id.
```html
<svg class="shoal-mark" viewBox="0 0 120 48" aria-hidden="true" focusable="false">
  <g fill="currentColor">
    <use href="#shoal-fish" transform="translate(6 6)"/>
    <use href="#shoal-fish" transform="translate(54 22) scale(.8)" opacity=".7"/>
    <use href="#shoal-fish" transform="translate(86 6) scale(.62)" opacity=".45"/>
  </g>
  <g class="shoal-mark__eyes">
    <circle cx="39" cy="14.5" r="1.6"/><circle cx="80.4" cy="28.8" r="1.3"/><circle cx="106.5" cy="11.3" r="1"/>
  </g>
</svg>
```
CSS: `.shoal-mark { width: 120px; height: 48px; color: var(--shoal-accent); } .shoal-mark__eyes { fill: var(--shoal-surface); }`. Lumen checks the rendered mark at design review and may refine the path; the size and placement are fixed.
