# WARDEN review 02: verification of DESIGN v2

Reviewer: WARDEN (security). Date: 2026-10-14. Mode: read-only. Nothing was built, run or deployed. The only file I wrote is this one.
Inputs read in full: `docs/reviews/warden-01.md`, `docs/DESIGN.md` v2 (1934 lines), `docs/DECISIONS.md` (D1-D11), the K1 working-tree code only as far as `AtomicFile.cs`, and a skim of `HlsUriPolicy.cs`. I did not re-review the spike code since Kiro's hardening commit `ae6f48b`; K16/K17 replace it.
Jellyfin 12.1 and jellyfin-web 12.1 source were used to check claims. `path:line` is relative to `jellyfin/` (server) or `jellyfin-web/`.

Evidence labels as in review 01: **Verified (source)** = I read the line; **Derived** = follows from source plus a spec or runtime behavior I did not execute; **Not executed** = every exploit below is a reading of the code, not a run.

## Summary

| | Count |
|---|---|
| warden-01 findings (34 headline) resolved by the cited section | 22 |
| resolved by design, accepted non-adoption | 1 (B-13, lab tooling) |
| resolved with small follow-ups | 6 |
| only partially resolved | 5 (A-1, A-6, A-9, A-18, B-14) |
| not resolved | 0 |
| New findings | Critical 0, High 2, Medium 8, Low 9 |

Nothing is Critical. The two High items are both gaps between what v2 promises and what Jellyfin 12.1 really allows, and both have cheap fixes:

1. **N-1.** The stock-Peer story (owner D7: the friend installs nothing) relies on a hardening checklist that the 12.1 dashboard cannot apply, and on a revoke/pause model that misses a second self-minting path (password reset). Verified in source.
2. **N-2.** "Everyone on this server" admits Shoal's own share users, so two-way sharing can chain a third friend's library to the wrong person.

Atlas's own new finding (the device lock ignores non-persistent devices, so a share-user token can mint more tokens) is **confirmed in source**, and I found a second path with the same effect that his two Shoal-Peer controls do not close (section 3).

**Verdict on v2.** A real improvement and close to buildable. The closed HLS grammar held against every line I threw at it (section 4.6). K5 may start after seven spec edits (section 5).

---

# 1. Resolution table for warden-01

Status: **R** resolved; **R+** resolved, with a follow-up listed in the note; **P** partial; **A** accepted by design.
For Part B the status is for the *design*. The spike code is replaced by the cited K-task; I did not re-verify code.

## Part A

| ID | Sev | Status | v2 section | Note |
|---|---|---|---|---|
| A-1 | High | **P** | 1.1, 4.1-4.4, 8.4 | The Quick Connect (QC) design is sound: 1 s poll, vanished = hijack, one redeem attempt (correct: `_authorizedSecrets` is independent of `_currentRequests`, `QuickConnectManager.cs:136-170`, `GetAuthorizedRequest` at `:173`). The invite path has three flaws (N-4 failure counters as an anonymous DoS lever, N-5 optional SAS compare, no commitment). K5 needs seven spec edits (N-10). Residual for public stock Peers is stated honestly. |
| A-2 | High | R | 6.4 | Closed grammar holds (section 4.6). Small structural caps in N-11. |
| A-3 | High | R | 8.6 | Own Content-Type table, nosniff, CSP sandbox, `attachment`. CORP non-adoption accepted (section 2a). |
| A-4 | High | R | 8.5, 8.7, 8.8 | The 8-step CallerContext matches the source (re-checked `AuthService.cs:23-40`, `CustomAuthenticationHandler.cs:46-90`, `DefaultAuthorizationHandler.cs:41-94`). A deleted user's token reaches the handler with an empty user id and the handler does not succeed, so step 3 denies it. Good. |
| A-5 | Med | R | 6.9 | Slots at first byte; per-user half of the peer slots. |
| A-6 | Med | **P** | 5.6, 8.5 step 6 | Per-request mode check is right and playback is authoritative. Gaps: N-2 (share users are "Everyone"), N-3 (browse layer is eventually consistent, with a window of up to an hour). |
| A-7 | Med | R+ | 4.4, 8.3 T13 | Accepted as partly adopted. Follow-up: N-8 (changing a peer URL keeps the token). |
| A-8 | Med | R | 2, 8.3 T11 | Proxy mode limited to Tailnet literals. Block list extended. |
| A-9 | Med | **P** | 4.5, 8.4 | Disable-first runbook is correct. The stock-Peer residual is understated (N-1), "Pause" is unsafe on stock Peers, and the Shoal-Peer bans are incomplete (N-6). |
| A-10 | Med | R | 5.2, 5.4, 5.10 | Quota, caps, new-items guard. |
| A-11 | Med | R+ | 5.4 | JPEG/PNG only by magic. Follow-up N-12: walk the JPEG markers; do not scan for SOFn. |
| A-12 | Med | R | 5.5, 6.2.3 | Per-field table is strict; `DisplayTitle` recomputed locally. Re-checked: the 12.1 web item page still has the unescaped sinks, so the controls are load-bearing. |
| A-13 | Med | R+ | 8.4 | Route set from `EndpointDataSource` plus CI diff is right. Follow-ups in N-6: bounded cache, fail closed for the security bans. |
| A-14 | Med | R+ | 3.2a, 3.7 | `state.json` store is right. Follow-ups: N-9 (restore can widen access; hard-link portability of `File.Replace`). |
| A-15 | Low | R | 6.2.2, 8.3 T26 | Rebuilt profile with fixed name. |
| A-16 | Low | R+ | 8.2 | HKDF, nonce, AAD, DPAPI `CurrentUser` all specified. Follow-ups: N-7 (env key format, service-account caveat, AAD binding). |
| A-17 | Low | R | 8.3 T30, 11.5 | Checklist. |
| A-18 | Low | **P** | 4.1, 4.1b | The full policy list is correct for Shoal Peers (set by code). For stock Peers four of the listed flags cannot be set in the dashboard, and one of them is security-relevant (N-1). |
| A-19 | Low | R | 3.5, 6.4 | 12 h absolute, 5 min re-check (could be per request, N-17). |
| A-20.1 | Low | R+ | 4.1b | The Shares screen refuses StubRoot libraries, but Everyone-mode Reconcile writes them into share users' lists (N-2). |
| A-20.2 | Low | R | 6.1 | Merged-version detection and 400 on foreign `MediaSourceId`. |
| A-20.3 | Low | R | 6.1 | `File` 403, `LiveStreams/Open` 400. |
| A-20.4 | Low | R | 8.3 T4 | |
| A-20.5 | Low | R | 8.3 T25 | |
| A-20.6 | Low | R | 6.10 | |
| A-20.7 | Low | R | 6.9 | |
| A-20.8 | Low | R | 5.5 | |
| A-20.9 | Low | R (moot) | 4.1b | Shoal Peers do not approve QC codes. |

## Part B (design level)

| ID | Sev | Status | Replaced by | Note |
|---|---|---|---|---|
| B-1 | Crit | R | 6.4, K16 | The patched spike (`HlsUriPolicy.cs`, "minimal version") is an interim seam; K16 must delete `PlaylistRewriter.cs`. |
| B-2 | High | R | 6.10, K3 | |
| B-3 | High | R | 8.6, K7 | |
| B-4 | High | R | 6.2.1, 6.11, K14, K17 | |
| B-5 | Med | R | 3.5, K12 | Shoal never reads `.strm` at runtime; empty StubRoot means nothing is federated. |
| B-6 | Med | R | 8.5, K6 | |
| B-7 | Med | R | 3.2, 9.1, K1, K2 | |
| B-8 | Med | R | 6.1, K13 | Template table. |
| B-9 | Med | R | 6.9, 6.10 | |
| B-10 | Low | R | 6.2.3, 8.6 | |
| B-11 | Low | R | 6.1 | |
| B-12 | Low | R | 2, 4.4 | |
| B-13 | Low | A | 11.2 note | Lab-only, `lab/` excluded from the public repo. |
| B-14 | Low | **P** | 11.5 | The author email is already rewritten (27 commits, all noreply). `docs/` still names private IPs and CT ids (`docs/spike-01-playbackinfo.md`, `docs/tests/owner-browser-test-01.md`, `docs/MORNING-REPORT.md`, `docs/ROADMAP.md`). The checklist covers it; `.claude/worktrees/` is untracked and not ignored. |

---

# 2. Atlas's four non-adoptions

## (a) No `Cross-Origin-Resource-Policy: same-origin` -- ACCEPT, with an optional refinement

- **Correct premise.** `<video src>` is a no-cors request; CORP same-origin would block a Chromecast receiver or any separately hosted web client from loading direct-play bytes. The token (`ApiKey`) is the gate, and CORP is not an authorization control.
- **Why it costs little to skip.** What stopped the XSS-in-Home's-origin scenario of A-3 was never CORP. It was the own-Content-Type table, `nosniff`, `CSP: sandbox` and `attachment`. Those are adopted.
- **Refinement (optional, Low).** CORP is enforced only for no-cors requests. Fetches in cors mode (hls.js playlist and segment XHR, `<track>`, the admin page's `fetch`) are governed by CORS headers, not CORP. So `Cross-Origin-Resource-Policy: same-origin` can safely go on JSON, admin and playlist responses, and be left off raw media and segments. It adds little (nosniff plus correct types already enable ORB), so I do not require it.

## (b) TLS SPKI pinning opt-in only -- ACCEPT

- Leaf-key pins would flap on renewal for ACME clients that rotate keys (certbot generates a new key by default), and sharing would break every 60-90 days. A pin that users learn to click through is worse than no pin.
- The real defences stay: CA validation, hostname verification, TLS 1.2+, HTTPS required for Public peers, WireGuard identity on tailnets.
- **Optional middle path** (not required): record the issuer name (or intermediate SPKI) at pairing and show an alert-only badge when the issuer changes. It survives key rotation, and it still catches a captive portal or a MITM with a different CA.

## (c) Transcode cap only when the friend runs Shoal -- ACCEPT

A stock Peer has no code of ours, and core has no count limit. Two precisions for the README:

1. `MaxActiveSessions` does not bound transcodes (jobs are keyed by device and `PlaySessionId`, not by session; a session is one device).
2. On a stock Peer a hostile token holder can also write to the share user's own rows (user data, display preferences, playlists). Unbounded in size per request up to Kestrel's body limit. Accepted for a "semi-trusted friend", but say so.

## (d) "3 failed attempts cancel ALL active invites" instead of burning one invite -- PUSH BACK

Atlas's premise is right (a wrong secret cannot be attributed to one invite). The conclusion is wrong, for two reasons:

- **It buys no security.** The invite has 130 bits. No failure counter changes brute-force feasibility; the per-IP and global rate limits already make it hopeless.
- **It hands an anonymous attacker a griefing lever.** `POST /Shoal/Pair` is reachable by anyone who can reach the Peer. Three bad requests while an invite is active destroy every pending invite and raise an admin alert. The attacker can repeat it forever, so invite pairing becomes unusable on any internet-reachable Peer. The leak that tells the attacker an invite is open (404 without reading the body versus hashing) is itself a timing signal.

**Replace with** (N-4):
- Compare the presented secret in constant time against active invites **first**. A valid secret is never subject to the failure counter.
- Count and rate-limit only the failures (per remote IP, plus a looser global bucket); a tripped bucket answers failures with 429 or a delay, never destroys invites and never rejects a valid secret.
- Keep "no active invite: 404 without reading the body" but make the response time constant for the same-size body.

---

# 3. Atlas's new finding, verified, and the two Shoal-Peer controls

## 3.1 Verified: the device lock does not stop a token holder from minting tokens

The chain, each link read in source (**Verified (source)**, **Not executed**):

1. `DeviceManager.CanAccessDevice` (`Jellyfin.Server.Implementations/Devices/DeviceManager.cs:237-249`) returns true if the user has `EnableAllDevices` or is admin, else if `EnabledDevices` contains the id **or** `!GetCapabilities(deviceId).SupportsPersistentIdentifier`.
2. `GetCapabilities` reads an **in-memory** `ConcurrentDictionary` (`:33,123-132`) and returns `new()` for an unknown id; `ClientCapabilities` defaults `SupportsPersistentIdentifier = true` (`MediaBrowser.Model/Session/ClientCapabilities.cs:17`).
3. Anyone with a valid token can write that map. `POST /Sessions/Capabilities` and `/Sessions/Capabilities/Full` are `[Authorize]` only (`SessionController.cs:351-400`) and take `supportsPersistentIdentifier` from the caller; `ReportCapabilities` ends in `_deviceManager.SaveCapabilities(session.DeviceId, ...)` (`SessionManager.cs:1901`).
4. The session's `DeviceId` comes from the request header whenever a valid token is presented (`AuthorizationContext.cs:146-148`: a non-empty header DeviceId is kept), and `RequestHelpers.GetSession` creates the session from `User.GetDeviceId()` (`RequestHelpers.cs:123-139`). So the holder can register **any** DeviceId X as non-persistent without a token for X.
5. `POST /QuickConnect/Initiate` is anonymous and takes DeviceId, name and client from the caller's headers (`QuickConnectManager.cs:72-96`). `AuthorizeQuickConnect` accepts the caller's own user id (`QuickConnectController.cs:104-120`, `RequestHelpers.cs:62-83`). `AuthorizeRequest` calls `AuthenticateDirect` with the Initiate device fields (`QuickConnectManager.cs:151-161`), and `AuthenticateNewSessionInternal` only asks `CanAccessDevice` (`SessionManager.cs:1732-1736`). With X registered as non-persistent, a second independent token for X is minted.
6. Disabling a user does not delete devices, and `DeviceAccessHost` acts only on policy updates for persistent devices (`DeviceAccessHost.cs:62-94`). The extra device and token survive "delete the device you see".
7. The web client is not special: `SupportsPersistentIdentifier` is `false` for browsers (`web:utils/dashboard.js:191`), which is why the dashboard's own help text says the filter "will not prevent browser access".

Atlas's finding stands as written. Two clarifications that matter for Pilot:

- The caps map is in memory, so a Peer restart resets all registrations; the attacker must re-register.
- A token holder can also mint a token for Home's **own** DeviceId D1 (it is in `EnabledDevices`, so no capability trick is needed). `GetAuthorizationToken` logs out Home's token (`SessionManager.cs:1768-1797`), so Home sees a 401. That is self-limiting: re-pair on the same DeviceId evicts the thief's copy.

## 3.2 A second self-minting path the design does not know about (N-1)

`POST /Users/Password` with `ResetPassword: true`:

- It is `[Authorize]` only. `AssertCanUpdateUser(User, user, restrictUserPreferences: true)` lets a non-admin act on itself when `user.EnableUserPreferenceAccess` is true (`RequestHelpers.cs:94-111`; `UserController.cs:270-327`).
- With `ResetPassword` the branch does not ask for the current password and does not revoke tokens: `ResetPassword` calls `ChangePassword(userId, "")` and the default provider stores `Password = null` (`UserManager.cs:419-449`, `DefaultAuthenticationProvider.cs:97-103`). A user with a null password logs in with an empty password (`:61-68`).
- The thief then runs `POST /Users/AuthenticateByName` with a registered non-persistent DeviceId (step 3 above) and holds a token the sweep and the Authorize ban never see.

**`EnableUserPreferenceAccess` defaults to `true` (`UserPolicy.cs:46`, `Entities/User.cs:44`) and the 12.1 dashboard cannot change it.** I read the dashboard's user profile and access pages: the policy flags they write are IsAdministrator, IsHidden, IsDisabled, EnableRemoteControlOfOtherUsers, LiveTv, SharedDeviceControl, MediaPlayback, the three transcoding toggles, CollectionManagement, SubtitleManagement, ForceRemoteSourceTranscoding, ContentDownloading, RemoteAccess, bitrate limit, LoginAttemptsBeforeLockout, **MaxActiveSessions**, ContentDeletion, SyncPlayAccess, plus folders, channels and devices on the Access page (`web:apps/dashboard/features/users/components/Profile.tsx:182-245`, `Access.tsx`). `EnableUserPreferenceAccess`, `EnableMediaConversion`, `EnableSyncTranscoding` and `EnablePublicSharing` are not among them, and all four default to true.

## 3.3 What helps on a stock Peer: `MaxActiveSessions = 1` (dashboard-reachable, Verified (source), Not executed)

`AuthenticateNewSessionInternal` is the single funnel for QC authorize and for password login. It refuses when `MaxActiveSessions >= 1` and `Sessions.Count(i => i.UserId == user.Id) >= max` (`SessionManager.cs:1738-1744`). Sessions live in `_activeConnections` and are removed only by logout, device deletion or a websocket controller disconnecting (`SessionManager.cs:308-322,368-377`, the only caller of `CloseIfNeededAsync` is `WebSocketController.cs:94`). There is no inactivity reaping of HTTP-only sessions.

Consequences:

- To register a non-persistent device the thief must first create a session (`GetSession` runs `LogSessionActivity`), which makes the count at least 1, so the mint is then refused. Both the QC route and the password route are closed while any session of the share user exists. Home's `Sessions/Playing` reports keep one alive.
- The residual is only the "mint for Home's own DeviceId" case, and only right after a Peer restart (count 0). It is self-healing.
- **Costs.** (i) The friend's private-window browser session counts, so Max must be 0 during pairing and set to 1 afterwards, in checklist step 8, after sign-out. (ii) A re-pair while Home's session is live is refused, so the re-pair instruction becomes "delete the old Shoal device first" (which ends its session). (iii) Shoal's own `AuthenticateDirect` call on a Shoal Peer hits the same check (K21/K22 must handle it).
- Pilot must run this in the lab before the checklist ships (T-SEC-22).

## 3.4 Controls on Shoal Peers: gaps

**(d) Ban on share users calling `QuickConnect/Authorize`.** Correct as far as it goes; the only remaining QC minting path is blocked. Gaps:

1. The password-reset path above is not covered. The plugin creates the user by code, so it **does** set `EnableUserPreferenceAccess=false` (it is in the 4.1 list) and re-applies it each tick. Add a guard ban as defence in depth: share users get 403 on `POST Users/Password`, `POST Users/{id}/Password` and `POST Sessions/Capabilities[/Full]`. Banning the capability routes alone closes steps 3-5 of 3.1 without relying on the Authorize ban, and Home never calls them.
2. "Any exception inside the filter calls `next()`" applies to the whole filter. For job (a) fail-open is a defensible trade for a global filter. For (d), and for the bans above, fail **closed**: they only touch a few share-user routes, so a bug there cannot break the friend's own playback.
3. The filter is "inert unless at least one Share exists", and shares live in `state.json`. In recovery mode (N-9) the bans silently turn off while the share users and their tokens still exist.

**(b) Unexpected devices signed out within 60 s.** The mechanism is fine. Gaps:

1. 60 s is long enough for the password-reset step, which leaves nothing for the sweep to see. With the bans and the flag in place that path is closed.
2. Ordering: `expectedDeviceIds` must be updated **before** `AuthenticateDirect` returns (4.1b step 3 records it afterwards). A sweep triggered by the plugin's own `OnUserUpdated` for the `EnabledDevices` change can otherwise log out the freshly minted Home device.
3. The sweep removes devices; it cannot see a thief using Home's own token T1 directly (same DeviceId header). Nothing in v2 detects that. A cheap detection for Shoal Peers: remember the remote address class (and, for tailnet or LAN, the address) of authenticated share-user requests, learned at approval, and raise an alert when a new one appears. Optional; I would not block on it.

## 3.5 Honest residual risk

**Friend runs Shoal (invite path, after the changes above).** A thief holding T1 can read the shared libraries, run transcodes up to the per-share cap, and nothing else: no second token, no password change, no lasting foothold after Pause or Revoke. Home's token cannot be stolen from the Peer. Remaining: anonymous raw streams for anyone who holds peer GUIDs until `Enforce` is on (T29).

**Friend does not run Shoal (the D7 MVP default).** The shipped design says "a stolen token can mint more tokens; disable the user first". The accurate statement is stronger:

- A holder of one share-user token can use the shared libraries, start unlimited ffmpeg jobs, mint further devices (via QC or via a password reset) and keep access after the friend deletes every device they can see.
- With the 4.1 checklist as written, the friend cannot turn off the password reset (the flag is not in the dashboard). With `MaxActiveSessions=1` set after pairing, new logins and mints are refused while any session of the share user exists.
- **Only deleting the account is a complete revocation.** "Pause" (disable, later re-enable) is only safe for a trusted Home. If a token was ever stolen, a paused account can be revived with an attacker-set password. 4.5's "Pausing on a stock Peer" and T42 must say so.
- Raw static and subtitle bytes remain anonymous to anyone with the GUIDs while the Peer is reachable (T29, unchanged).
- The data at stake is what the friend deliberately shared with Home, plus Peer CPU. That bounds the damage, and it is what the owner accepted, but it should be written down exactly this way.

---

# 4. New findings

Ranked within severity. "Blocks MVP" means the named task should not merge (or start, for spec items) until it is addressed.

## 4.1 High

### N-1 [High] The stock-Peer hardening checklist cannot be applied in the 12.1 dashboard, and the revoke/pause text misses the password-reset path

- **Where:** 4.1 steps 3 and 8 (the "copyable checklist" K5 and U3 render), 4.5 ("Pausing on a stock Peer"), 8.3 T42, T-PAIR-12, A-18's resolution.
- **Evidence (Verified (source)):** section 3.2 and 3.3. Unreachable defaults that the checklist tells the friend to set false: `EnableUserPreferenceAccess` (security-relevant: password reset), `EnableMediaConversion`, `EnableSyncTranscoding`, `EnablePublicSharing` (not security-relevant on 12.1 servers).
- **Exploit:** a thief with one share-user token resets the sharing account's password to empty, registers a non-persistent DeviceId, logs in again, and survives "disable, delete devices, re-enable". The friend followed the checklist and has no way to know.
- **Fix:**
  1. Rewrite the checklist to split two lists: *Set these* (dashboard-reachable, including `MaxActiveSessions=1` as step 9 after sign-out, with the re-pair note) and *Cannot be changed from the dashboard* (inherent, explained in one sentence each).
  2. Home's 60 s `/Users/Me` check must treat the second list as an informational note, not a warning that can never clear. It should warn on `MaxActiveSessions` other than 1 after pairing.
  3. 4.5 and T42: Pause on a stock Peer is safe only for a trusted Home; delete-and-recreate is the safe "stop".
  4. Shoal Peer: guard bans on `Users/Password` and `Sessions/Capabilities*` for share users (N-6); `EnableUserPreferenceAccess=false` verified each tick.
  5. Lab tests T-SEC-22 (Appendix).
- **Blocks MVP:** yes. K5 checklist text, U3, K21 policy set, section 4.5.

### N-2 [High] "Everyone on this server" admits Shoal's own share users (chain sharing)

- **Where:** 5.6 (`Everyone` = "every account", Reconcile rules), 8.5 step 6, 4.1b Shares ("refuse libraries under StubRoot").
- **Issue:** On a server running both roles, the hidden share users (`shoal-<friend>-xxxx`) are ordinary Jellyfin accounts. In `Everyone` mode `Admitted(share user, L)` is true, and Reconcile's rule for an explicit-list account is `EnabledFolders := (current minus every Shoal library) ∪ (Shoal libraries U is admitted to)`. So Reconcile **adds** the federated libraries to the share user's list. A-20.1's "Shares refuse StubRoot libraries" only covers the manual selection.
- **Exploit:** Z shares with Home H. H's admin flips Z's library to "Everyone" (D4). H also shares its own libraries with X, via share user `shoal-X` on H. X's Home pairs with H and lists `UserViews`; Z's library now appears among H's libraries. X's Home imports it. When X plays a title, H's intercept filter runs CallerContext for `shoal-X`: step 5 passes (Reconcile gave it library access) and step 6 passes (`Everyone`). H proxies Z's file to X. Z agreed to share with H's admin only.
- **Fix:**
  1. `Admitted(user, L)` is false for any user listed in `peerRole.shares[].shareUserId`, in both modes, ahead of everything else.
  2. Reconcile never adds Shoal libraries to a share user and removes them if present.
  3. `PUT Libraries/{m}/Access` rejects share users in `selectedUserIds`.
  4. T-SEC-23 (two-way lab).
- **Blocks MVP:** yes. K10 acceptance (the rule is generic and cheap now); matters in practice at K21.

## 4.2 Medium

### N-3 [Medium] The browse layer is eventually consistent: default-deny holds for playback, not for metadata and art

- **Where:** 5.6 (Layer 2), triggers list ("`OnUserUpdated` outside the creation window only refreshes the drift list; the next scheduled reconcile re-applies"), D3 wording "nobody else can see".
- **Evidence:** In 12.1 every browse path I checked filters by the user's library access: item queries (`BaseItemRepository.QueryBuilding.cs:459-541`), People, Genres and Studios (`PersonsController.cs:103-139`, `ApplyItemByNameAccessFiltering`), playlist and collection children (`Folder.GetLinkedChildren(user)`, `Folder.cs:1736-1800`, `FilterLinkedChildrenPerUser` true for `BoxSet` and `Playlist`), and SyncPlay joins (`Group.cs:247-249`). So core library access *is* the browse control, and the reconcile windows are the exposure.
- **Windows:** a new account (30-60 s; dashboard-created users default to `EnableAllFolders=true`); a dashboard edit that ticks "all libraries" or adds a Shoal library (**up to the next scheduled reconcile, hourly**); a restored state; a reconcile that failed.
- **Impact:** an unadmitted user sees titles, posters and overviews for up to an hour. They cannot play (step 6 returns 404). It contradicts D3's promise, not the bytes.
- **Fix:**
  1. On `OnUserUpdated` for a non-share user, re-apply **only the Shoal-library portion** of that account's access within 10 s (debounced; ignore events Shoal caused itself). This does not fight the admin for the other libraries.
  2. As a backstop, add the `Shoal: <Peer>` tag to `BlockedTags` for accounts that are not admitted. It hides the items in every browse path regardless of library lists (new accounts get it with the creation hook). Tag blocking is already how 12.1 filters `IsVisible`.
  3. State the guarantee in the UI and README: "playback is enforced on every request; lists catch up within about a minute."
- **Blocks MVP:** no (K10 improvement).

### N-4 [Medium] `POST /Shoal/Pair` and the Initiate shield are anonymous DoS levers

- **Where:** 4.1b limits row, 8.4(b), T-PAIR-9, T-PAIR-11, DESIGN "Not adopted" bullet 4.
- **Issues:**
  1. "3 failures cancel all invites" and "global 5 failures per minute, then 429" let any caller who can reach the Peer permanently disable invite pairing, including for a valid secret (section 2d).
  2. "Per remote IP" must use core's resolved client address (`HttpContext.GetNormalizedRemoteIP()`); behind a reverse proxy without `KnownProxies` every visitor is the proxy, so a per-IP limit becomes a global one. Same for the Initiate shield.
  3. The Initiate shield's global bucket (60 per 10 minutes) can be kept full by anyone at 6 requests per minute, which turns QC off for **all of the friend's own users** while it lasts. The collision probability the cap protects against is `R/900,000`; a cap of 300 per 10 minutes still gives about 3e-4.
- **Fix:** constant-time compare first and never limit a valid secret; limit failures only; per-IP bucket via `GetNormalizedRemoteIP`; Initiate shield global cap raised to 300 per 10 minutes and per-IP 10 per minute; document the proxy requirement.
- **Blocks MVP:** MVP-B (K22, K23).

### N-5 [Medium] The invite SAS: optional visual comparison, no commitment, cleartext redemption

- **Where:** 4.1b step 3 and the SAS row, 9.5 #1, `docs/ui-spec.md` line 729.
- **Entropy answer.** The QC **check** (4 Crockford characters, 20 bits) is enough for its role: it travels only in Home's request header inside WireGuard or TLS, the attacker cannot see it, gets about one guess per hijack, and Home's own detection fires first. The invite **SAS** (8 characters, 40 bits) is enough against an attacker without a network position. Two defects remain:
  1. **Click-through.** "Compare by eye" is skipped in practice. If the invite leaks, the attacker redeems first and the friend sees a plausible "Bob's Home" card. Server id and name are attacker-supplied and public. The real Bob's redeem returns 404, so Bob has no SAS to read; a friend who clicks Approve without comparing still hands over the token. **Require the approving admin to type the SAS** that Home displays (Approve disabled until it matches). With the legitimate flow it costs eight keystrokes; with a hijack Bob cannot dictate a code.
  2. **No commitment.** An on-path attacker who also holds the invite can grind a nonce for one leg until the two SAS values collide: 2^40 hashes is on the order of a minute on one modern GPU. Fix by commit-reveal: the first request carries `SHA-256(homeNonce)`, the poll reveals `homeNonce`. It is one extra field.
  3. Redeeming over plain-http LAN exposes invite, nonce and the returned token to a sniffer (the "delivered once" rule does not help, the sniffer polls first). Refuse invite redemption over plain http outside the tailnet class.
- **Blocks MVP:** K22 / U7 (MVP-B).

### N-6 [Medium] `PeerGuardFilter` gaps

- **Where:** 8.4, K23.
- **Issues (besides 3.4):**
  1. The "inside a shared library" cache is keyed by item id with a 60 s TTL. An anonymous caller can spray random GUIDs at the anonymous routes: do not cache misses and bound the cache (for example 10,000 entries, LRU). Otherwise it is a memory lever.
  2. Cache staleness: an item added to a shared library is anonymous-readable for up to 60 s. Acceptable; note it.
  3. Order of operations for the sweep (3.4).
  4. Transcode cap coverage (`master`, `main`, `hls1`, non-static `stream`) is best-effort: audio HLS and subtitle conversion are other ffmpeg starts. Say "best-effort", not "cap".
- **Blocks MVP:** K23.

### N-7 [Medium] `SecretStore`: unspecified key format and weak spots

- **Where:** 8.2, K2.
- **Issues:**
  1. `JELLYFIN_SHOAL_KEY` has no format. HKDF is not a password hash: `JELLYFIN_SHOAL_KEY=hunter2` is a brute-forceable key. Require 32 random bytes as base64url or hex (reject anything else, and refuse to fall back to `master.key`). Add `JELLYFIN_SHOAL_KEY_FILE` for Docker/systemd secrets, since environment variables show up in `docker inspect` and `/proc/<pid>/environ`.
  2. DPAPI `CurrentUser` protects against other accounts, not against the service account. If the admin switches Jellyfin between a tray app and a service, or between service accounts, the blob becomes unreadable: that is `SecretsUnreadable`, correct, but say so. The "application entropy" is a constant in an open-source plugin; it is not a secret, so do not describe it as one.
  3. AAD = schema only. Bind each entry to its peer: AAD includes `peerKey` and the canonical base URL hash. Then an attacker who can edit `state.json` (write but not read access) cannot redirect a token to another host without the key. See N-8.
  4. Key rotation and "re-key" are not described. Low priority.
- **Blocks MVP:** K2 (spec).

### N-8 [Medium] Admin API: changing a peer URL keeps the token

- **Where:** 9.6 `PUT Shoal/Admin/Peers/{h}`, 4.4 trust window.
- **Issue:** the token is sent after an **unauthenticated** pin check, and the pin is a server Id any host can replay (the doc says so itself). So `PUT` with a new `baseUrl` (an admin mis-click, a phished admin, an XSS in the admin page, a restored `state.json`) sends the token to whatever host answers with the old Id.
- **Fix:** a change of host (not scheme or port) discards the token and requires re-pair. Related hygiene for the same API: `POST State/Restore` takes a server-listed handle, never a path; `DELETE Admin/All` and "Remove peer and delete library" require the typed name in the request body, not only in the UI; `PUT Libraries/{m}/Access` validates `ratingsAcknowledged` and the user ids server-side (also N-2).
- **Blocks MVP:** MVP-A (cheap).

### N-9 [Medium] `state.json` recovery and restore can widen access, and disable the Peer guard

- **Where:** 3.7, 8.4 "Recovery mode".
- **Issues:**
  1. Restoring from `.bak` or a daily copy silently returns older settings. A library tightened from `Everyone` to `Selected` after the backup comes back as `Everyone`; a revoked share comes back enabled; `Enforce` comes back as `LogOnly`. The banner only says where it restored from.
  2. In recovery mode (nothing parses) the guard filter has no share list, so jobs (b)-(d) are off while share users and tokens exist. Keeping "the last mode in memory" does not help after a restart.
  3. (Derived, not verified) on Unix, .NET implements `File.Replace(..., backup)` with a hard link for the backup. On a filesystem without hard links (some CIFS/NFS mounts that are common for `/config` in containers) it throws. `AtomicFile.Write` in the K1 draft calls it with the backup path. It must degrade to copy-then-move and report, not fail the save.
- **Fix:** after any restore other than from `state.json` itself, load every access mode as `Selected` and require the admin to confirm "settings restored from <time>, review access"; persist a tiny second file (`peer-role/guard-lastgood.json`: share user ids, shared collection-folder ids, mode) outside the main state so recovery mode keeps the bans and `Enforce`; handle the hard-link failure.
- **Blocks MVP:** no.

### N-10 [Medium] Quick Connect pairing (K5): spec gaps that must be closed before coding

See section 5 for the list; all seven are spec edits, none changes the architecture. **Blocks K5: yes.**

## 4.3 Low

### N-11 [Low] HLS grammar: small structural holes (no injection found)

- **Total size.** Nothing caps the **sum** of `EXTINF` durations or the segment count beyond line limits: 100,000 segments of 86,400 s each makes hls.js build a multi-day timeline. Require `sum(EXTINF) <= runtime * 1.1 + 1 h` and at most 20,000 segments.
- **NonBacktracking captures.** .NET's `NonBacktracking` engine finds the same *match* but may report capture groups differently for ambiguous patterns (the `.*` in rule MD). The code validates every captured value afterwards, so this is not exploitable, but F-PL fixtures should pin the captured group for a crafted `NAME`.
- **Anchors.** Use `\z` (or split lines first and never match unsplit text), because .NET `$` also matches before a trailing `\n`.
- **Numbers.** `BANDWIDTH` allows 10 digits; parse as `long`. `#EXT-X-VERSION`, `MEDIA-SEQUENCE` repeated: emit once.
- **6.3 headers.** "Copied after validation" for `Last-Modified` and `Content-Range` should mean *parsed and re-serialized*, not copied raw.

### N-12 [Low] Image header parsing (K9)

Walk the JPEG marker segments by their declared lengths; do not search for `FFC0..FFCF`. An EXIF thumbnail in APP1 contains its own SOF marker, so a scan reads the thumbnail's small dimensions and misses the real image (an 8000 x 8000 file passes the 16 MP check). Same for PNG: use the first `IHDR` chunk only.

### N-13 [Low] Uninstalling the plugin leaves converted accounts

`VisibilityService` converts "all libraries" accounts to explicit lists. If the plugin is uninstalled, those accounts never see new libraries again and nothing tells anyone. Use `BasePlugin.OnUninstalling()` (and `DELETE Admin/All`) to restore `EnableAllFolders=true` for managed accounts, and say so in the README.

### N-14 [Low] Peer-controlled media bytes reach client decoders (document as residual)

The grammar controls URIs; it cannot sanitize segment or subtitle *payloads*. A hostile Peer's TS/MP4/MKV, ASS (fonts) or VTT reach the user's player. jellyfin-web sanitizes VTT cue HTML with DOMPurify (`web:plugins/htmlVideoPlayer/plugin.js:1728`) and runs ASS in a WASM renderer. Native Moonfin/Swiftfin/ExoPlayer demuxers are unknown (D11 makes them a blocker for functionality, not for this). Add one sentence to T26/§8.1: "a friend's server can send malformed media to your players, as any Jellyfin server can".

### N-15 [Low] Metadata paths the access mode does not cover (documented, not fixable by us)

- Item images are anonymous in core by item id (`ImageController.cs:552`): stub posters are as exposed as any local item's.
- User-data and playback-report APIs accept a federated item id from an unadmitted user and write only to that user's own rows (a 404-versus-200 oracle for a GUID nobody can guess).
- DLNA lists items for its configured user without per-request authentication (the DLNA plugin is separate; I did not read it). Playback fails with 401 on federated items.
- SyncPlay checks `IsVisibleStandalone` on join (core library access), not Shoal's mode, so a user in the browse window (N-3) can join a group and see queue titles.

### N-16 [Low] QC `Secret` in the poll URL

`GET /QuickConnect/Connect?secret=...` puts the secret in a query string: it appears in Peer-side Debug request logs and in reverse-proxy logs. After approval the secret stays redeemable for about 11 minutes, and redemption is not exclusive (`_authorizedSecrets` is not cleared on redeem, `QuickConnectManager.cs:163,173-185`). Note: Atlas's Appendix A says approval "sets expiry to now + 1 min"; the code sets `DateAdded` to now plus 1 minute and expiry is `DateAdded + 10 min`, so about 11 minutes. Home should never put the URL with query into logs, exceptions or the admin API; K5 acceptance below.

### N-17 [Low] HLS access re-check every 5 minutes

Steps 5-7 are in-memory work plus a cached lookup. Doing them per request would make "switch to Selected" take effect for HLS immediately, at negligible cost. Optional.

### N-18 [Low] Release hygiene follow-up

`docs/` still contains `192.0.2.10`, CT ids and "production the production server" (listed under B-14). `.claude/worktrees/` shows as untracked: add to `.gitignore`.

### N-19 [Low] Session-report events and grant ownership

K19: `PeerSessionReporter` must check `grant.localUserId == session.UserId` before forwarding a `PlaybackStopped` to the Peer, not only match a `PlaySessionId`.

---

## 4.6 HLS grammar break attempts (6.4, Appendix C)

All 22 F-PL fixtures behave as the doc says when walked through the rules. I also tried the following; each is **rejected (502) or dropped, none gets a token or a connection**:

| Input | Where | Outcome |
|---|---|---|
| `#EXT-X-DEFINE:NAME="x",VALUE="https://h"` then `{$x}/seg.ts` | V, after EXTINF | DEFINE dropped; `{$x}...` fails U-V (`{`, `$` not in the grammar) -> 502 |
| `#EXTINF:10,` / `#EXT-X-BYTERANGE:100@0` / `hls1/main/1.ts?...` | V | BYTERANGE dropped, URI accepted (positive control) |
| `#EXTINF:10,` / `#EXT-X-DATERANGE:...X-ASSET-URI="..."` / `hls1/main/1.ts?...` | V | DATERANGE dropped, URI accepted |
| `#EXT-X-ENDLIST` followed by `#EXTINF` + a URI | V | ENDLIST must be the last tag -> 502 |
| `hls1/main/1.ts?runtime%54icks=1&runtimeTicks=2&actualSegmentLengthTicks=3` | U-V | keys are percent-decoded and compared case-insensitively; duplicate -> 502 |
| `hls1/main/1.ts?runtimeTicks=%D9%A3&actualSegmentLengthTicks=1` (Arabic-indic digit) | U-V | decoded value fails `^[0-9]{1,19}$` -> 502 |
| `hls1/main/01.ts?...`, `hls1/main/1.ts.exe?...`, `HLS1/main/1.ts?...` | U-V | pattern anchors and no `IgnoreCase` -> 502 |
| `#EXT-X-MAP:URI="hls1/main/-01.mp4?..."` and the same with a leading space | V | MAP non-match -> 502 |
| `#EXT-X-MEDIA:...NAME="x",DEFAULT=NO,FORCED=NO,AUTOSELECT=YES,URI="<guid>/Subtitles/9/subtitles.m3u8?a=b",LANGUAGE="y",URI="https:/h/x",LANGUAGE=".*"` | M | NAME crafted to contain a second valid-looking URI: index must be a text subtitle stream in the grant; output is built from grant data only -> worst case a wrong label |
| `#EXT-X-STREAM-INF:BANDWIDTH=1,CODECS="a,BANDWIDTH=2"` | M | quote-aware attribute parser; duplicate or malformed -> 502 |
| `#EXT-X-STREAM-INF:BANDWIDTH=99999999999999999999` | M | `[0-9]{1,10}` -> 502 |
| BOM + CRLF line endings | all | accepted (positive control) |
| `#EXTINF:1E-1000,` and `1E999,` | V | parsed finite and in 0..86,400, else 502 |
| URI line `main.m3u8?next=http%3A%2F%2Fevil` | U-M | passes `Q` (percent-encoded) but the value is only stored in the grant's peer-bound query; it never reaches a client, and Home re-encodes when sending to the same Peer |
| `#EXT-X-I-FRAME-STREAM-INF`, `#EXT-X-PRELOAD-HINT`, `#EXT-X-RENDITION-REPORT`, `#EXT-X-SKIP`, `#EXT-X-PART:URI=...` | V | dropped (named in the table) |

No injection path found. The remaining items are structural (N-11). Add the rows above that are not in Appendix C as F-PL-23..F-PL-33.

---

# 5. K5 go / no-go

**GO, with seven spec edits made first.** K5 can start on the transport and state machine. Do not merge until these are in the design text and K5's acceptance criteria.

Required before coding:

1. **Checklist truth (N-1).** Rewrite 4.1 steps 3 and 8 to what the dashboard can set; add step 9, `MaxActiveSessions=1` after sign-out (and "delete the old Shoal device before re-pairing"). Home's `/Users/Me` checks classify the unreachable flags as informational notes, and warn on `MaxActiveSessions` other than 1 after pairing and on `EnableAllDevices=true`.
2. **Server-side confirm gate.** For the QC path the pairing state machine must have an explicit `AwaitingConfirm` state: a token is held but no sync, no library creation and no Reconcile happen until `POST Pairing/Confirm {match:true}`. The UI makes it a primary button (`ui-spec.md:746`); the server must enforce it too. `Confirm` is legal only from that state. `{match:false}` logs out, discards the token and moves to `PairingAborted(codesDidNotMatch)`.
3. **Abort durability and retry.** `PairingAborted` is persisted (`health.json`), shown on the Overview card, and also sent as `DisplayMessage` to the admin's sessions. It does not depend on the pairing screen being open. After `requestReplaced`, the next attempt must target a **new** sharing account (or follow 4.5 first); the UI says so. The attacker's token, if the friend approved the wrong request, lives on the old account.
4. **Poll classification.** The first poll after Initiate must return 200 with the same `Code` and `Secret`; only then is a later 404 treated as "request vanished". A 404 from a reverse proxy with a wrong base path, a Peer restart, a 401 (Quick Connect disabled mid-flight) and timeouts are separate states with their own messages. Call `GET /QuickConnect/Enabled` before Initiate. Make the 3 s per-poll timeout and one redeem attempt after a 404 explicit.
5. **Validate everything the Peer returns.** `Code` must match `^[0-9]{6}$` before it is shown or relayed; `Secret` `^[0-9a-f]{64}$`; the access token `^[0-9a-f]{32}$` before it is put in an `Authorization` header or stored; `AuthenticationResult.User.Id` must equal the `/Users/Me` id. Never log the poll URL (it carries the secret), the secret, the code or the token at any level; add them to the log-grep test.
6. **Header encoding.** The `Device` value `Shoal: <Home name> (<check>)` is percent-encoded, including `%` and `+` (core URL-decodes header values, and a bare `+` decodes to a space, `AuthorizationContext.cs:276-313`).
7. **One session per Home, one Initiate per click.** Initiate only when the admin clicks "Start pairing"; at most one live session; `baseUrl` is immutable while it lives; `Probe` handles expire; the 5 per 10 minutes limit (6.9) is enforced server-side.

Add to K5 acceptance:
- **T-PAIR-13:** fakepeer replaces the entry 20 s in while the test also floods Initiate at 1,000 per second: Home reaches `PairingAborted(requestReplaced)` within 2 s, stores nothing, and never auto-restarts.
- **T-PAIR-14:** a 404 HTML page from the Connect URL (wrong base path) gives "cannot reach Quick Connect", not `requestReplaced`.
- **T-PAIR-15:** hostile Peer returns `Code="<img src=x onerror=...>"`, a non-hex secret, a token containing `"` and `,`: all refused, nothing stored, nothing rendered as HTML.
- **T-PAIR-16:** abort survives a Home restart and a page reload.
- **T-PAIR-17 (K22):** while a bad-request flood trips the failure limiter, a valid secret still redeems.
- **T-PAIR-18 (K22):** Approve is disabled until the typed SAS matches.

Also, for the record: the poll, the check string and the vanished-request logic in v2 are correct as designed; I could find no way to bring Home to hold a token that was approved for someone else. If Home holds a token, the friend approved Home's own secret. The check is a belt-and-braces signal for the case where Home did not see the replacement, such as a Home restart mid-pairing.

---

# Appendix: facts verified in source for this review

| Fact | Where |
|---|---|
| `CanAccessDevice`; capabilities map is in-memory; default persistent = true; only `ReportCapabilities` writes it | `DeviceManager.cs:33,69-73,123-132,237-249`; `ClientCapabilities.cs:17`; `SessionManager.cs:1901`; `SessionController.cs:351-400` |
| Valid token + header DeviceId creates a session for that DeviceId | `AuthorizationContext.cs:146-148`; `RequestHelpers.cs:123-139`; `SessionManager.cs:244-292,494-533` |
| QC Authorize mints via `AuthenticateDirect` with Initiate-supplied device fields; entry lives about 11 min after approval; secret redeem is repeatable | `QuickConnectManager.cs:72-96,136-185,198-230` |
| `AuthenticateDirect` and password login both check `MaxActiveSessions` against in-memory sessions; sessions are removed only on logout/delete/websocket disconnect | `SessionManager.cs:1738-1744,308-322,368-377`; `WebSocketController.cs:94` |
| Password reset by the account itself when `EnableUserPreferenceAccess` is true; no token revocation; null password logs in with empty | `UserController.cs:270-327`; `RequestHelpers.cs:101-111`; `UserManager.cs:419-449`; `DefaultAuthenticationProvider.cs:61-68,97-103` |
| `EnableUserPreferenceAccess`, `EnableMediaConversion`, `EnableSyncTranscoding`, `EnablePublicSharing` default true; dashboard writes none of them | `UserPolicy.cs:25-26,46,65`; `web:Profile.tsx:182-245`, `Access.tsx` |
| `UserDto.HasPassword` is never populated by the server in 12.1 (always the default `true`), so Home cannot detect a password reset from `/Users/Me` | `UserDto.cs:59,66` (no assignment anywhere in the server) |
| 12.1 browse paths filter by library access: item queries, People/Genres/Studios, BoxSet and Playlist children, SyncPlay join | `BaseItemRepository.QueryBuilding.cs:459-560`; `PersonsController.cs:103-139`; `Folder.cs:1736-1800`; `BoxSet.cs:31`; `Playlist.cs:67`; `Group.cs:247-249` |
| `CustomAuthenticationHandler` claims: UserId, DeviceId (header-overridable), Token (header or `ApiKey`), IsApiKey | `CustomAuthenticationHandler.cs:46-90` |
| `DefaultAuthorizationHandler` does not succeed for a principal with an empty user id | `DefaultAuthorizationHandler.cs:41-94` |
| VTT cue HTML in jellyfin-web is DOMPurify-sanitized | `web:plugins/htmlVideoPlayer/plugin.js:1728` |

**Not verified (no execution):** hls.js behaviour on any tag; `File.Replace` internals on Unix (derived from memory of the runtime source); `EndpointDataSource` enumeration at plugin startup; the DLNA plugin; native Moonfin/Swiftfin clients; whether `LogSessionActivity` is invoked by every session-creating controller I listed; that `MaxActiveSessions=1` behaves as read (T-SEC-22 settles it).

# Appendix: tests to add to Pilot's plan

- **T-SEC-22 (stock Peer, default policy):** a share-user token runs `Sessions/Capabilities/Full` with a fresh DeviceId then (a) Initiate + Authorize(self), (b) `Users/Password {ResetPassword:true}` + `AuthenticateByName` with empty password. Expected without `MaxActiveSessions`: both succeed (documented). With `MaxActiveSessions=1` and a live session: both refused with "maximum number of sessions". With a Shoal Peer: `Users/Password` and `Sessions/Capabilities*` return 403, and `EnableUserPreferenceAccess` is false.
- **T-SEC-23 (two-way lab):** `Everyone` mode on a federated library: the share user is not admitted (404 on PlaybackInfo and static), and Reconcile never wrote the Shoal library into its list; `PUT Access` with a share user id in the selection is rejected.
- **T-SEC-24:** 1,000,000 random GUIDs at the Peer's anonymous stream routes: guard cache stays bounded and memory is flat.
- **T-VIS-7:** an unadmitted user with "all libraries" ticked in the dashboard: time until the Shoal library disappears (target 10 s); Search, Latest, NextUp, Resume, Persons, Genres, Studios, a public playlist and a collection owned by an admitted user show no stub-derived names.
- **T-REG-5:** restore from `.bak`: every library comes back as `Selected`, and the banner asks for confirmation.
- **F-PL-23..33:** the new break attempts of section 4.6.
- **T-PAIR-13..18:** section 5.
