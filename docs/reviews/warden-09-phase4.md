# Warden 09: phase 4 (U8, U9) review, main eb768d1 (2026-10-15)

**Critical 0, High 0, Medium 3, Low 5. GO.** Fix the Mediums before release. M1 breaks the U8 acceptance scenario. Auth, SSRF, XSS, tokens and the LocalAPI all came out clean. (Saved by the manager from Warden's report.)

Paths are under `src/Jellyfin.Plugin.Shoal/Diagnostics/` unless stated.

## Medium
**M1 Test now and the deep test time out on links under about 8.5 Mbps, and the result is discarded.** `SpeedTestService.cs:221` (2 x 16 MB parallel), `:236` (catch), `Peers/PeerCallProfile.cs:82` (BitrateTest Total 30 s). At `rate 8mbit` each stream needs about 32 s and fails; the catch nulls `Mbps`, discarding the throughput already measured at `:209`. Auto-fit then goes stale and expires after 48 h. **Fix:** size the parallel runs as `min(16 MB, bestMbps*1e6/8*8 s)` and skip them below about 4 Mbps; give steps 219-230 their own try/catch so pings and throughput are kept; blank Mbps only if the throughput step failed.

**M2 Playback samples can flush the speed history.** `PassiveTelemetry.cs:138` with `SpeedHistoryStore.cs:124, 189` (shared 2,000-line cap). About 2,000 short HLS plays evict every test sample, so `VerdictEngine.Throughput` returns null and auto-fit and the hints vanish. **Fix:** a separate cap for `playback` lines (about 300), or a separate file; optionally cap samples per grant or user per hour.

**M3 A flapping peer bypasses the test interval** (`SpeedTestHost.cs:37, 44`, `cameBack`): about 41 MB a minute and history churn. **Fix:** no light test within 10 minutes of the last sample of any kind; same rule for the after-pairing trigger.

## Low
- **L1** The auto-fit floor is 100 kbps (`SpeedTestHost.cs:145, 187`). It can only lower the cap (`PlaybackInfoBuilder.cs:88` uses min), but a peer that throttles only BitrateTest, or a test run during a Home stream, can pin streams low for 48 h. Skip manual and deep runs while `IsStreaming(peer)` (or exclude those samples from T), and raise the floor to about `VerdictEngine.MinUsableMbps` (4 Mbps).
- **L2** Returned bytes aren't checked against the requested size (`SpeedTestService.cs:311-345`); up to 64 MiB per run. Abort if `total > size + 64 KiB`, or set MaxBytes = size + slack.
- **L3** `DerpRegion`/`Relay` is unvalidated (`TailscaleLocalApi.cs:89-91`); over 2,048 chars, `SpeedHistoryStore.ReadAll:251` drops the sample. Accept only `^[A-Za-z0-9._-]{1,32}$`. The short-name match (`:177`) can pick a shared-in node; prefer an exact IP or a unique match. (The admin-set socket path is fine.)
- **L4** `AdminSpeedController.cs:154-166`: the cooldown is consumed before RunAsync knows the peer is usable (202 with nothing run); a malformed body is treated as manual. Start the cooldown when the run begins; return 400 on a null body.
- **L5** The deep probe is invisible to StreamSlots (`SpeedTestService.cs:243`, `TranscodeProbe.cs:88`); a stream started mid-probe exceeds MaxConcurrentTranscodes by one. Take a real slot, or have TryTakeHls refuse while a probe is active.

## Checked OK
Admin-only RequiresElevation and a known PeerKey; cooldowns 1/min and deep 1/10 min with a per-peer semaphore; speed calls use a throwaway breaker; all Peer calls bounded (MaxBytes, Idle, Total, BoundedReadStream, no redirects, address policy per call); LocalAPI capped at 1 MB, depth 16, three fields read; passive samples hold no user, token, IP, device or title; PeerDir hex-validated, history file private and capped; shoal.js uses textContent and setAttr bans; logs redacted.
