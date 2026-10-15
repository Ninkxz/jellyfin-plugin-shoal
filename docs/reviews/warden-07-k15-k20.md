# Warden 07: K15-K20 milestone review (2026-10-15)

Scope: K15 3a77436, K16 6366b85, K17 0398cb5, K18 114c99b, K19 c311acf, K20 0d32ac2 and the W6 fix commits. Reviewed on the working tree, no tests run. K21-K23 are NOT covered here (separate pass).

**Verdict: Critical 0, High 1, Medium 2, Low 6, Info 7. GO for the lab.** Fix H-1, M-1, M-2 before real friend traffic or a public push.

(Warden is read-only; the manager saved this file from Warden's report.)

## High

**H-1 HLS segment route is unbounded, has no concurrency cap, and segment timeouts feed the shared breaker.**
Where: `Playback/HlsProxy.cs:499-523` (SegmentAsync; id only checked -1..999999), `:337` (per-grant limiter 600/min burst 40, wired `PluginServiceRegistrator.cs:176`), `:422-423` (MainAsync discards `PlaylistResult.Segments`), `Peers/PeerCallProfile.cs:55` (HlsSegment counts toward breaker), `Peers/PeerClient.cs:163-166, 196-198, 396` (16 pooled connections per peer).
Exploit: an admitted Home user requests random far segment ids at 10/s on up to 4 grants. Ids are not tied to the approved playlist; each far id makes the Peer restart ffmpeg and may hold up to 60 s (DESIGN 6.10). The 16 connections pin, PlaybackInfo and health pings queue, timeouts open the peer-wide circuit, and the peer shows Offline for every Home user (W4-1 class, closed for subtitles but not segments). Pilot to confirm stock 12.1 past-the-end behavior.
Fix: (1) store the segment count from the accepted main playlist on the grant; main-before-master stays 404; reject `segmentId >= count`; allow -1 only if EXT-X-MAP was emitted. (2) in-flight cap per grant (3, then 429) plus a per-peer HLS segment gate like the subtitle gate, so control calls keep 8+ connections. (3) `CountsTowardBreaker=false` for segments (connect failures still count); HealthMonitor's ping decides up/down.

## Medium

**M-1 Per-user stream limit bypass via the "same static slot" exemption.**
Where: `Playback/StreamSlots.cs:163-171` (TryTakeHls skips CheckLimits when a static slot for (user,item) exists), `:174`.
Exploit: one `Static=true` GET/HEAD on item X holds a static slot (2 min, refreshable); then PlaybackInfo with a transcode-only profile under up to 4 client-chosen DeviceIds gives 4 grants, each with an HLS slot and no user or half-per-peer check. One account takes all 4 conversion slots.
Fix: exempt only the first HLS slot of a (user,item) when a static slot exists; run CheckLimits if an HLS slot for that (user,item) already exists (or cap HLS slots per (user,item) at 1).

**M-2 Client-sent PlaybackStopped releases the static slot while the body is still streaming.**
Where: `Playback/PeerSessionReporter.cs:189` (ReleaseStatic), `Playback/StreamSlots.cs:220-226, 300-309` (TouchStatic is a no-op once the slot is gone).
Exploit: start 4 static streams, POST `/Sessions/Playing/Stopped` for each, slots vanish while bodies keep streaming, start more. Only the 6-connection per-user cap (`StreamSlots.cs:76`) remains. Bypasses MaxStreamsPerUser and the per-peer cap; eats the friend's upload.
Fix: ReleaseStatic must not remove a slot with `Connections > 0`; mark release-on-last-close and let CloseStatic remove it after the 2 min grace.

## Low

**L-1 K15 acceptance gap: SR§4.2 `mediaSourceId` rule missing.** `Playback/ShoalInterceptFilter.cs:147-150, 191-215`; `StaticStreamHandler.cs:169-175` answers 400 after CallerContext instead of 404 before it (DESIGN 6.1). A local item merged with a stub, requested `route=L&mediaSourceId=S`, reaches core's anonymous stream route with the stub source and leaks only the sentinel; needs an admin merge (StubEnricher alerts). Fix: for StaticStream and Subtitle rows, 404 when a query `mediaSourceId` or route `{routeMediaSourceId}` is federated and differs from the route id, before the "not federated, next()" return.

**L-2 One user can starve subtitles for a whole peer.** `Playback/SubtitleHandler.cs:125-131` (16-waiter cap per peer), `:135` (HLS subtitle segments skip the user-lane rule), `:412-424`. Fix: cap waiters per user at 3.

**L-3 Slot release and grant removal ride the droppable report queue; OnStart is unthrottled.** `Playback/PeerSessionReporter.cs:136-147, 180-191, 274-277` (queue 256, DropOldest). Spamming `POST /Sessions/Playing` pushes other users' stops out. Fix: release slots and remove the grant synchronously in OnStop; queue only Peer calls; throttle OnStart per grant (1 per 20 s).

**L-4 Peer PlaySessionId not shape-checked.** `Playback/PlaybackInfoBuilder.cs:239`. Whitespace passes `IsNullOrEmpty` (`HlsProxy.cs:107`, `PeerSessionReporter.cs:258`) but ScopedKill/Report throw; OnStop aborts before releasing slots; ActiveEncodings 500. The empty-PlaySessionId hard rule otherwise holds (`HlsProxy.cs:67-73`). Fix: accept only `^[A-Za-z0-9-]{8,64}$`, else store empty and treat as unstoppable (idle kill).

**L-5 W6 L-8 residual: NFO growth on update is outside the quota.** `Sync/StubWriter.cs:252` (`quota.Add(delta)`, no TryReserve; Kiro Q27). Overshoot about 1.5-2.5 GB. Fix: TryReserve the delta; on refusal keep the old NFO and raise QuotaReached.

**L-6 RateLimiter buckets never pruned.** `Playback/Grants.cs:370-413`; about 4 MB/day/user. Fix: drop buckets full and idle for over 2 min.

## Info
- Duplicate DI registrations, `PluginServiceRegistrator.cs:76-89` (CatalogStore through SyncScheduler).
- Grammar Decode/QueryOf turns a literal `+` in a Peer variant query into `%2B` (functional only).
- RuntimeTicks 0 caps playlist duration at 3600 s, so a long title with unknown runtime gets 502.
- A second `#EXT-X-ENDLIST` in a V/S playlist is emitted twice (harmless).
- `.claude/worktrees/agent-a639...` holds old spike code; keep it out of any commit or package.
- Flaky test `F_RS_06` (short body vs Content-Length); make deterministic before ship. (Manager: `T_PLAY_8_Stop_ForAStartedTranscode...` also failed once under the full parallel run after the 555aeed merge and passed 3/3 alone.)
- `CallerContext.IsLocalNetwork` (`CallerContext.cs:257-264`) treats all RFC1918 as LAN and ignores the admin's LAN subnets; bitrate classification only, not auth.

## Verified clean
- **Auth:** Hls and Subtitles controllers class-level `[Authorize]` + CallerContext; API keys 403 (step 2); another user's or unknown grant 404 at step 4 without removing the victim's grant (`HlsProxy.cs:317`); admin controllers RequiresElevation; no `[AllowAnonymous]` in K15-K20.
- **Grammar:** closed tag set, rebuilt URIs, no peer host emitted; token `^[0-9a-f]{32}\z`; ASCII classes, `\z`, NonBacktracking, no IgnoreCase; control/NEL/LS/PS/lone CR → 502; KEY and non-VOD → 502; ENDLIST required; segment, duration and line caps; MD rebuilt from grant data; canary on output; query via `Uri.EscapeDataString` + `IsQ`.
- **Subtitles:** cue-text invariant holds (only six tags survive, `-->` impossible); VTT drops NOTE/STYLE/REGION and re-serializes timings and settings; JSON Utf8JsonReader walk, depth 8, 502 at 20,001 events; ASS/SSA residual accepted (W2§N-14); format and codec allowlists; sup/pgs 404; coalescing key includes peer and media source.
- **Scoped kill:** client ActiveEncodings matched only to the caller's grants (10/min, 204); direct-play grants never release a slot; empty client PlaySessionId falls to core, nothing sent to the Peer.
- **Static stream:** single range only; headers re-serialized; type from catalog container; CallerContext every request; trust window PinAndAuthFresh.
- **Logging:** no URL, query or token in any K15-K20 log line.

## W6 fixes landed
a987226 (M-1: `MetadataSanitizer.cs:271, 373`, `CatalogSyncService.cs:192`), 83cdf3f (M-2/L-7: `BannedSymbols.txt:49-99`, spike controller and resolver gone), ca1432a (K14-L1/L2), ca49676 (L-4: `CallerContext.cs:277`), 903207b (L-8; residual L-5), 95c0fb4 (L-6: `VisibilityService.cs:322-325`), 8aab800 (L-1/2/3: `PairingService.cs:230, 258, 324-328, 995`).

## Rulings on Kiro Q23-31 (security-relevant)
- **23 ACCEPT.** `Q = [...]*` plus captured length ≤ 4096 at all five sites is equivalent; update DESIGN 6.4 wording.
- **24 ACCEPT.** Dropping a malformed known singleton tag is fine; EXTINF, MAP, STREAM-INF errors, KEY, non-VOD and zero segments stay 502.
- **25 ACCEPT for now.** Descriptor walk against the pinned, hash-checked 12.1 Jellyfin.Api.dll becomes a mandatory release-checklist step.
- **26 APPROVE** the spike deletion; no references remain. Pilot owns F-PL-23..36.
- **27 ACCEPT** except the update-path residual (L-5).
- **28 ACCEPT.** The old-token `/Users/Me` call must stay after the pin check.
- **29 ACCEPT** (playlists PinFresh, segments PinAndAuthFresh, main-before-master 404, hashed limiter key + pruning L-6); slot arithmetic is not fine as is (M-1).
- **30 ACCEPT.** mov_text safe via the VTT/SRT rebuild; add L-2.
- **31 ACCEPT.** Not parking offline peers is fine; alerts throttled and device names sanitized.
