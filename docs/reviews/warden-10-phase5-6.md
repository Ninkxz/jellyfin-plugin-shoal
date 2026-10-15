# Warden 10: phases 5 and 6 (K24-K32, K27 backend), main b1f4761 (2026-10-15)

**Critical 0, High 0, Medium 2, Low 7. GO for the lab and the merge.** Fix both Mediums before release. The only `[AllowAnonymous]` is PairController.Pair. Code was read directly; nothing was built or run. (Saved by the manager from Warden's report.)

Paths are under `src/Jellyfin.Plugin.Shoal/`.

## Medium
**M1 The rotation route limiter is charged before the caller is identified.** `Sharing/ShareRotation.cs:101-104` (30/min, `Guid.Empty` key). Any signed-in Peer account (even a non-share kid account) posting 30 times a minute to `/Shoal/Share/Rotate` starves Home's rotation with 429, and non-share callers see 429 instead of the uniform 404. **Fix:** charge the route limiter only after step 1 (known share user plus live token); pre-auth, use a per-user or per-IP bucket.

**M2 The rotation lock is not shared with Pause, Revoke or Approve.** `Sharing/ShareRotation.cs:113-148`, `ShareTokenIssuer.cs:52-58`, `ShareService.SetEnabledAsync:265-285`, RevokeAsync, Approve. A Pause landing between rotation's re-read and ApplyPolicyAsync re-applies a stale `Disabled=false` and mints a token for a paused share (it lives until the 60 s sweep). **Fix:** take the same `_locks[shareId]` in Pause, Revoke and Approve; re-check `share.Enabled` after ApplyPolicyAsync and before MintTokenAsync; if disabled, remove the devices and answer 404.

## Low
- **L1** `Playback/AttachmentHandler.cs:167`: the grant lookup compares `g.LocalItemId == version.VersionItemId`, so fonts 404 for every alternate version (fail-closed, but breaks S12 with S10). Match `g.LocalItemId == route.ItemId && g.EffectiveVersionItemId == version.VersionItemId`.
- **L2** `Sync/ChildImageSanitizer.cs:276-279, 286-288, 432-437` fails open: one transient self-test failure (8 s under load) or CouldNotStart disables the sandbox for 10 min and images go unsanitized. Require 2 failures or retest after 60 s; keep the alert.
- **L3** `ChildImageSanitizer.cs:273`: one global `_serial` is shared by background sync and user tile requests, and the retest runs inline on a user request; tile scrubbing can starve (503). Separate lanes or a priority, and no inline retest on the request path.
- **L4** `Peers/PeerDirectory.cs:186, 198` with `Peers/TokenRotation.cs:318, 446`: after `storeFailed`, the in-memory active token overrides a re-pair's fresh credential (AuthFailed until restart). Call `ClearActiveToken` on pair, re-pair and peer removal.
- **L5** `Playback/ShoalInterceptFilter.cs:164-168, 198-199`: the foreign-mediaSourceId 404 rule covers static and subtitle only; `Videos/{localId}/{federatedStubId}/Attachments/n` and Trickplay `?mediaSourceId=` still reach core when the route id is local (merged-version hazard only; not a regression). Apply `IsFederated(named) && !InSet` there too.
- **L6** `StaticStreamHandler.cs:218`, `StreamSlots.cs:132`: slots keyed by (user, set) let one user run up to 4 distinct-version streams on one slot (at most a 4x bandwidth multiplier, accepted in the design). Document it or count connections per peer.
- **L7** `Sharing/ShareRotation.cs:40`: `RotationResult` is a record holding `Token`, so an auto ToString would print it if ever logged (nothing does now). Override ToString or use a non-record type. Also add class-level `[Authorize]` on `Api/PairController.cs:53` so future actions fail closed.

## Checked OK
- **Peer rotation:** the token appears only in the no-store body (never in logs, state, health.json or URLs); identity comes from claims; API-key and non-share callers get a uniform 404; the old token dies before the mint; replays are refused; the 10-min and 6/day limits work.
- **Home rotation:** atomic Rotating mark with the slot check; never retried; token regex, DeviceId equality, non-admin and pin checks; a 401 inside the window is not a verdict.
- **Trickplay:** only validated JPEGs stored; width and index bounded against the catalog; cache path from random keys plus a regex; temp-then-rename with LRU; the Peer URL built from GUIDs and ints.
- **Fonts:** magic check; 16 MB per file and 128 MB per grant; index allow-list; closed `font/*` table; nosniff, sandbox, attachment disposition.
- **Version labels:** heights plus a `[a-z0-9]` codec; TryConfine, HasLinkBelow, length cap, IsSafeStubPath; 4 sources per title, 64 examined, counted against the 50k cap.
- **ffmpeg child:** argument list only; `-protocol_whitelist pipe`; cleared environment; temp directory removed in `finally`; 8 s tree kill; 4 MB output cap; stderr never logged; output revalidated.
- **Slots:** SetId is always the primary stub key.
- **FriendLinks:** display only.
- **Admin DTOs and logs:** handles only in DTOs; logs clean.
