# Warden 08: K21-K23 review (2026-10-15, main 555aeed)

Scope: K21 ShareService (fc860e5), K22 invite pairing (fa92625, a168bda), K23 PeerGuardFilter (77c46c1). Code read only; nothing run; 12.1 API behavior inferred from XML docs. (Saved by the manager from Warden's report.)

**Verdict: Critical 0, High 2, Medium 9, Low 8. Lab GO. Public or the production server exposure NO-GO until both Highs are fixed.** One anonymous action only (`PairController.Pair`, pinned by the reflection test); every admin controller has RequiresElevation; no token or invite secret in logs, state.json, `guard-lastgood.json` or admin responses.

Paths are under `src/Jellyfin.Plugin.Shoal/`.

## High
**H1 Anonymous `POST /Shoal/Pair` has an unbounded, quadratic failure log (CPU/memory DoS).** `Sharing/InviteBook.cs:62` (`_failures` list), `:405-410` (Fail), `:370-377` (Throttled), `:237-238` (all under `_lock`). Every failure appends (kept 1 h) and runs RemoveAll plus two O(n) Counts; a 429 still grows it. Thousands of garbage POSTs/s grow it to millions of entries under the lock that also serves valid redeems and Approval; thread-pool starvation for all of Jellyfin. **Fix:** fixed counters (global per-60 s bucket; per-IP dict cleared each minute, capped ~1,024 keys, overflow to global; 60-slot minute array for FailuresLastHour); check Throttled before hashing and 429 at once; move failure counting off the invite-dictionary lock.

**H2 Guard counters create unbounded buckets keyed on attacker-controlled User-Agent.** `Guard/GuardSupport.cs:261-280, 310-315`, called from `Guard/PeerGuardFilter.cs:291, 301`. Each distinct 32-char UA token makes a bucket kept 7 days; every call does FindIndex/RemoveAll under a global lock. Anonymous GETs to `Videos/{id}/stream` with random UAs (still served in LogOnly). **Fix:** cap distinct apps (~64, rest "other"); Dictionary keyed by (hour, kind, app, network), prune hourly.

## Medium
**M1 Approval state machine can stick and orphan a minted token.** `Sharing/InviteBook.cs:209` sets Approved before the token exists (`:423, :426`, after awaits); Redeem's Approved case (`:306-311`) can hand out a null token and flip to Delivered; catch at `:216` is only InvalidOperation/Argument. Home aborts `BadPeerResponse` (`Pairing/PairingService.Invite.cs:106, 115`) with a live orphan device; a SecurityException from AuthenticateDirect gives 500 and a permanent Approved. `Sharing/ShareUserSource.cs:205, 208` likely don't await `ISessionManager.Logout` / `IDeviceManager.DeleteDevice` (Tasks in 12.1 docs), so MaxActiveSessions=1 may refuse the mint and the "zero devices" checks are unreliable. **Fix:** add Phase.Approving (Redeem answers pending); set Token and Approved together under one lock after mint; catch Exception in DecideAsync and reset to Bound; make RemoveDeviceAsync async and await both; refuse invite/approve for a paused share (`ShareService.cs:340`).

**M2 Home redeems invites over LAN http.** `Pairing/PairingService.cs:232-238`, `PairingService.Invite.cs:152`. DESIGN 4.2 step 1 forbids it outside Tailnet. **Fix:** in ProbeAsync, `inviteSecret != null && address.IsCleartext && network != Tailnet` → `Failure="inviteLanHttp"`; re-check at Begin.

**M3 Sharing account can be orphaned with an open default policy.** `Sharing/ShareService.cs:204-218`, `Sharing/ShareUserSource.cs:134-140`. User created with EnableAllFolders/AllDevices and empty password before ChangePassword/ApplyPolicy; only two exception types caught; a client abort leaves `shoal-<friend>-<4hex>` with all-library access. **Fix:** run create with CancellationToken.None; catch Exception and delete the user; create disabled or lock first; record ShareUserId as soon as it exists.

**M4 Failed Revoke gets re-enabled by drift repair; invites outlive the share.** `Sharing/ShareService.cs:275-337, 489-502`, PolicyOf `:421`. **Fix:** persist Enabled=false before the sign-out loop; clear the share's invite at revoke; surface `deleted:false` loudly.

**M5 StubRoot refusal is lexical only, checked only at create/update.** `Sharing/ShareService.cs:458-472` (called `:441, :449`); no GetFullPath, separator normalization or link resolution (unlike `Authorization/ShoalIndex.cs:347`). Enables W§A-20.1 chaining. **Fix:** GetFullPath both sides, ResolveLinkTarget(returnFinalTarget:true), compare on separator boundaries; re-check in RepairAsync.

**M6 Guard hot path takes a global lock per request; snapshot failures disable job (d).** `Guard/GuardSupport.cs:104-127, 129-148`, `Guard/PeerGuardFilter.cs:88-100`. Every request rebuilds collections, serializes and compares; a read-only data dir retries fsync'd writes under the lock; other exceptions fall to `next()`, failing job (d) open. **Fix:** rebuild only on the state-changed event into a volatile field; persist lastgood off the request path with full try/catch; apply bans from the last snapshot when state is unreadable.

**M7 Recovery mode silently drops Enforce to LogOnly.** `Guard/PeerGuardFilter.cs:254`, `Guard/GuardSupport.cs:129-132, 168-178`. lastgood lacks the mode (DESIGN 3.7, N-9). StartOver/restore leaves orphan share accounts and Persist writes an empty list. **Fix:** store HardeningMode in lastgood and use it when state is not Ready; on StartOver/restore warn about or delete orphan `shoal-*` accounts.

**M8 Per-share transcode limit is easy to bypass; DESIGN 4.3 overstates it.** `Guard/PeerGuardFilter.cs:203-206, 216-220, 222-229`. Missing PlaySessionId → not counted; reused id passes; parallel fresh ids pass before the job exists; O(n) dict fillable. **Fix:** 400 for a share user's transcode request without PlaySessionId; reserve at acceptance; cap the dict ~64; soften DESIGN text or key on the share user.

**M9 Job (a) route detection ignores IAllowAnonymous; route table unverified.** `Guard/PeerGuardFilter.cs:236`, `Guard/GuardSupport.cs:349`. **Fix:** anonymous when IAllowAnonymous present or IAuthorizeData absent; make the startup diff a failing lab check.

## Low
- **L1** Raw `RemoteIpAddress.ToString()` for limiter and shield (`Api/PairController.cs:78`, `Guard/PeerGuardFilter.cs:134`); one IPv6 /64 gets unlimited buckets and can lock the friend's Quick Connect (`Guard/GuardSupport.cs:398-411`). Key IPv6 on /64; use core's normalized address.
- **L2** PairController regexes use `$` (`Api/PairController.cs:135-136, 157-161`): `\n`-suffixed homeDeviceId passes then 400s after devices are signed out (`InviteBook.cs:420`). Use `\A…\z`; clean homeName with TextClean.Display; require homeDeviceId[..12] == homeServerId[..12].
- **L3** Creating/cancelling an invite (`InviteBook.cs:133-149`) drops one mid-approval, leaving an unswept token; Approval() ignores expiry (`:152-173`). Refuse replacement mid-approval.
- **L4** No OnUserUpdated sweep (DESIGN 4.1b step 4), only the 60 s timer (`ShareService.cs:78, 522-567`); uncollected-token sign-out takes 2-3 min.
- **L5** `SharedItemCache.Clear()` never called (`Guard/GuardSupport.cs:488`); new shares unprotected up to 60 s. Clear on share change.
- **L6** `LoginAttemptsBeforeLockout` unset (`Sharing/ShareUserSource.cs:239-270`); 3 bad logins by name disable the account. Set -1 in Fill.
- **L7** Job (c) covers share users only; anonymous transcodes in LogOnly/Off uncounted. Document.
- **L8** Pair token delivery can't enforce https on the Peer; README must say a proxied Peer serves pairing over TLS.

## Checked OK
Commit-reveal binding and per-poll re-check; 128-bit invite, hash only, 15 min, single use; FixedTimeEquals lookup with uniform `404 {}`; SAS never shown or returned, judged server side, 3 tries; token delivered once, swept after 2 min, never logged or stored, `no-store`; 256-bit unseen password and full policy with drift repair; job (d) bans on route templates fail closed; share user cannot reach RequiresElevation or QuickConnect/Authorize; job (a) bounded LRU, API keys pass, images open; invite only in the 201 body; device ids hashed; Revoke checks confirmName first.

## Fix order
1. H1, H2. 2. M1 with the awaits; M3. 3. M2. 4. M6, M7 before Enforce anywhere. 5. Pilot lab checks: Logout/DeleteDevice return types in 12.1; T-SEC-16; real endpoint-table diff; approve while paused; approve with a slow mint (M1 race).
