# Shoal for Jellyfin: Design v2.6

*Your friends' libraries, next to yours. Nothing copied, nothing exposed.*

Author: ATLAS (architect). Date: 2026-10-15. Status: **v2.6** (phases 5 and 6, see "Changes in v2.6"), on top of **v2.4**, the patch for Warden-06 (code review of K3-K14), Pilot-03 (concurrency), Scout-03 (route table), the owner's Moonfin iOS test and the coders' open questions, on top of v2.3 (the Warden-04 patch: review of v2.2, plus Pilot-02 and the ui-spec 14.3 answers), v2.2 (the client-compatibility patch: Scout-02, owner D11), v2.1 (the Warden-02 patch) and v2 (revised after WARDEN review 01). Changelogs newest first: v2.6, v2.4, v2.3, v2.2, v2.1, v2. This is the build spec for the MVP (roadmap phase 3). v1 was titled "Jellyfin Federation: Design v1".

Inputs:
- `docs/reviews/warden-01.md`, cited as **W§A-n** and **W§B-n**. Its Appendix B fixtures are renumbered in Appendix C here so tasks can cite them.
- `docs/reviews/warden-02.md`, cited as **W2§N-n** for its new findings and **W2§n** for its sections (for example W2§2d, W2§5).
- `docs/spike-01-playbackinfo.md`, cited as **K§**. The MVC-filter mechanism is proven on 12.1.
- `docs/tests/owner-browser-test-01.md`. The owner played the spike build in jellyfin-web: play, seek, forced transcode on the **Peer** (no ffmpeg on Home) and subtitles all PASS. Native apps are untested.
- `docs/research/scout-01.md` (**S§**), `docs/ROADMAP.md`, `docs/naming.md`.
- `docs/research/scout-02-clients.md` (**SC§**; its gaps are **G1-G8**, its replay tests **T-C1..13**): how Moonfin (all platforms), Moonbase and the stock Jellyfin apps start playback, read in their public source.
- `docs/ui-spec.md` v2.1, section 14.2 (**UI§14.2 #n**): Lumen's list of DESIGN lines that still said v2; section 14.3 (**UI§14.3 #n**): Lumen's questions after v2.2.
- `docs/reviews/warden-04-v22.md`, cited as **W4-n**: Warden's review of v2.2 (no Critical or High, four Medium, eleven Low; K18 NO-GO until W4-1, W4-3, W4-7 are in the spec).
- `docs/tests/pilot-02-tsec22-tcli-baseline.md`, cited as **P2§n**: T-SEC-22 on a stock Peer (passes) and the T-CLI-1..6 baseline on the spike build.
- `docs/reviews/warden-06-k3-k13.md`, cited as **W6 M-n / L-n**: Warden's review of the committed K3-K14 code (no Critical or High; two Medium and ten Low, all code fixes except L-5, which is a design rule).
- `docs/tests/pilot-03-concurrency.md`, cited as **P3§n**: T-CONC-1, several users streaming through one share token (PASS on the spike).
- `docs/research/scout-03-routes.md`, cited as **SR§n**: the 12.1 route table against Kiro's `InterceptTable` (0 misclassified, 0 leaks) and the `MetadataFetchers=[]` question.
- `docs/tests/owner-moonfin-ios-01.md`: Moonfin for iOS on the spike build, PASS.
- `docs/kiro-03-backend.md` (Kiro's questions, cited as **KQ n**) and `docs/kiro2-02-ui.md` round 2 (Kiro 2's unsettled names).
- Jellyfin `v12.1` source, cited as `path:line` relative to `jellyfin/`, and jellyfin-web `v12.1` (`web:`).

**Naming.** The plugin is **Shoal** ("Shoal for Jellyfin"). Assembly and namespace `Jellyfin.Plugin.Shoal`, plugin routes `/Shoal/...`, data folder `<DataPath>/shoal/`, user-visible tag `Shoal`. "Federated item" stays the generic term for a library item backed by a stub.

| Term | Meaning |
|---|---|
| **Home** | The server whose users watch. It imports a friend's catalog. |
| **Peer** | The friend's server that shares libraries. It stores the files and does any transcoding. It may run stock Jellyfin (MVP default) or Shoal. |
| **Share user** | Dedicated hidden non-admin Jellyfin user on the Peer, limited to the shared libraries, locked to Home's device id and capped at one session (`MaxActiveSessions=1`). Home holds one token for it. On a server that also runs Shoal, share users are never admitted to any Shoal library (5.6). |
| **Stub** | The `.strm` + `.nfo` + images that Home writes so a peer title becomes a normal library item. |
| **stubKey** | Random local id per stub (96 bits, base32). It has no meaning outside Home. |
| **Grant** | Short-lived, in-memory, per-playback record on Home binding (local user, local item, peer play session). |
| **Shoal index** | Home's in-memory map: local item id to stub record to peer ids. |
| **Access mode / selection** | Per peer library: `Selected` ("Only selected users", default) or `Everyone` ("Everyone on this server"). The **selection** is the list of local users allowed in `Selected` mode; it is kept while the mode is `Everyone`. Default selection: only the admin who paired. |
| **Safety code** (UI word) | What two admins compare during pairing (9.5). Invite path: the **SAS**, 8 characters derived on both servers; **Home displays it and the Peer admin types it** to approve, the Peer never shows it (4.1b). Quick Connect path: the **check**, 4 random characters Home puts in its device name, which the friend sees in their dashboard's Devices list. |
| **Invite** | Plugin-to-plugin pairing: a one-time 128-bit secret created by the Peer admin and redeemed by Home. |

---

## Changes in v2.8 (D17, D18, offline card, read-ahead)

Owner decisions of 2026-10-16 (`docs/DECISIONS.md` D17 and D18; the work plan and its owner update are in `docs/plans/d17-remove-quick-connect.md`). They change pairing and one default. Playback, sync and the TV work of v2.7 are untouched. The older "Changes in" sections below stay as history: where they describe Quick Connect (QC) pairing, read them as the design that was in force until v2.7.

### v2.8.0 Decisions in one table

| # | Decision | Consequence |
|---|---|---|
| D17 | **Both servers need Shoal.** Pairing is by invite only (an invite code, then a safety code typed on the sharing side). Quick Connect pairing, the sharing checklist for stock Jellyfin and every "create an account on your Jellyfin" instruction are removed. | D7 ("the friend needs no plugin") is superseded. The pairing wizard has five steps: Address, Check, Invite, Code, Choose. The route question is gone. A server without Shoal gets a blocking "Install Shoal first" screen (section 12). LAN over plain http outside a Tailnet has no route (LAN only is for testing; friends use Tailscale or https). |
| D17 | **No legacy QC support.** A peer that was paired by QC loads, keeps its libraries, stubs and catalog, and is **paused** (`Enabled=false`) with the status "needs pairing again". | Its titles stop playing, like any paused server, until the admin re-pairs it with an invite (the same peer record, libraries and mappings are kept; the old token is signed out on a best effort basis). No new code writes the pairing method `qc`; old `state.json` and `health.json` files that contain it still load. |
| D18 | **Protect your shared videos starts in `Enforce`** ("Require sign-in") on new installs. | Servers that already have Shoal keep their current mode and see a one click "Require sign-in" card with the log result. The log only mode and the 7 day result of 8.4 stay. This changes D9. |

### v2.8.1 What changed in this document

| Section | Change |
|---|---|
| 1.1 | The QC flow diagram is marked retired. |
| 4.1 | Collapsed to "Legacy QC peers (paused, re-pair by invite)". The 18 step stock Jellyfin checklist, the session limit and device lock explanation, the session anchor and the lab findings behind them are removed from the design (they live in git history and in the v2.1 to v2.4 sections). |
| 4.1b | The only pairing path. The share policy list moved here. Home can now detect Shoal in advance (the anonymous ping). |
| 4.2 | Steps for the five step wizard; the QC path and the "LAN http uses Quick Connect" line are gone. |
| 4.2a | Reduced to: one session and one click, response validation, the durable abort record and header encoding. The QC state machine is removed. |
| 4.3 | Only the defenses that still exist. The QC hijack attack no longer exists on Home. |
| 4.4, 4.5 | Mint and rotation rows updated. 4.5 keeps the stock Jellyfin revoke runbook for the old sharing accounts that legacy peers left behind. |
| 6.11 | The session anchor and the QC calls are removed from the list of what Home sends. |
| 8.3, 8.4 | T34 marked removed. The Initiate shield (job b) is removed. The default mode for new installs is `Enforce`. The ban on `QuickConnect/Authorize` for share users stays. |
| 9.3, 9.5 | Wizard, server detail, settings and friend detail updated. New fields: `hasShoal`, `shoalPingThrottled`, the legacy banner, no `code` in the pairing session. |
| 11.2, 11.3 | K5 and part of U3 retired. T-PAIR-1, 2, 5, 6, 11, 12, 13 and T-SEC-22 retired; T-PAIR-3, 7, 14, 15 and 16 ported or trimmed. New T-D17-1 to T-D17-6. |
| 12 | D7 superseded, D9 changed by D18, the "install Shoal first" copy and the install address. |
| New | v2.8.4 offline card and v2.8.5 read-ahead (`HlsBufferAhead`). |

### v2.8.2 What stays

- The anonymous Shoal ping (`GET /Shoal/Ping`, `Pairing/ShoalPing.cs`) is a hint for the admin and drives the "install Shoal first" check. The `[AllowAnonymous]` count stays at 2 (`Pair` and `Ping`).
- Invite pairing exactly as in 4.1b: single use secret, commit and reveal nonces, a safety code that Home shows and the friend types, a 15 minute life.
- The enum members and the state and health file readers for QC values, so old files load. Redaction patterns for QC secrets stay (cheap). The guard ban on `QuickConnect/Authorize`, `Users/Password` and `Sessions/Capabilities*` for share users stays.
- Token rotation through `POST /Shoal/Share/Rotate` (v2.6.1) for invite peers. A legacy peer is not rotated; it is re-paired.

### v2.8.3 Install Shoal first, and the legacy banner

The Check step asks the anonymous ping whether the server has Shoal. `hasShoal: true` goes on to the invite step. `hasShoal: false` stops with the "Install Shoal first" screen (copy in section 12; the install address is https://github.com/Ninkxz/jellyfin-plugin-shoal) and offers **Check again**. If the ping was throttled (`shoalPingThrottled`) or could not be read, the wizard continues with a soft hint and never blocks: an older Shoal, a proxy that blocks `/Shoal/*` or a rate limit must not lock out a real friend. While any peer is a legacy QC peer, Overview shows an info banner "N servers still use the old pairing. They are paused until you re-pair them with an invite." and each such server has a **Pair again with an invite** button. Screens: `docs/ui-spec-pairing-v2.md`.

### v2.8.5 Read-ahead for slow friend connections (owner request, same release)

For a converted (HLS) stream, Home fetches the next segments from the friend's server a little before the client asks for them and holds them in memory (`Playback/ReadAhead.cs`), so a slow or uneven link is smoothed out. The window is `HlsBufferAhead` segments (2 to 20, default 6) after the segment the client last asked for. Rules from Warden 18: a segment's bytes are reserved before they are read (held plus in flight count against the stream and against one global budget for the whole of Home, using the exact size when the friend declares it); it never takes or touches a stream slot and creates nothing for a stream that was released; it drops a server's streams when the server is stopped, removed or its key replaced; it serves from memory only while the server's trust holds; it fills only after the second request in order and not right after a seek. `AutoFitQualityToLink` (default on, phase 4) still caps the requested bitrate at the measured link speed divided by 1.5 (6.2). Direct play is not buffered.

### v2.8.4 Offline friends (owner request, same release)

When a friend's server is offline, PlaybackInfo no longer ends in a bare "Playback error": with `OfflineCardEnabled` (default on) it offers a short video titled "Server offline" that says the friend is down, that this is not a problem with this server, and that the title can be requested on the request service (`RequestServiceName`, default Seerr; optional `RequestServiceUrl` in the message). It is served by Shoal's own static route with no peer call, no stream slot and no grant, and playback reports for it are swallowed so the real item gets no resume point or watched mark. Hiding an offline server's libraries after some hours (`ParkOfflinePeers`) stays optional and off by default (5.7). Details: `docs/kiro3-01.md`.

## Changes in v2.7 (TV shows; owner feedback 01 item 2)

Nothing in sections 1 to 11 is reopened. TV reuses the movie pipeline: same pairing, same share-user token, same PlaybackInfo, static, HLS, subtitle and trickplay path. What is new is the stub layout, a three-level catalog, a TV sync planner, and guards that count episodes. Feature switch `TvShowsEnabled` (XML) defaults **off until T-TV-0 passes**, as alternate versions did. Off means v1 behavior: the picker keeps showing tvshows as "later".

### v2.7.0 Decisions in one table

| # | Decision | Why |
|---|---|---|
| 1 | Episode file stems are built from integers only: `S01E01.strm`, or `S01E01-E02.strm` for multi-episode files. No peer text appears in any episode or season name. | An episode title such as "S09E09", or a series called "24", cannot confuse Jellyfin's file-name parser. It also removes the traversal vector. NFO fields carry the real title. |
| 2 | Seasons and the show folder are **derived** from episodes. A container exists only while at least one active episode exists. | The mass-deletion guard then needs no container logic. A hostile peer cannot create an empty or mismatched season. |
| 3 | Dedup is **series-level**: a local series matching by provider id hides the whole peer series (`duplicate-local`). The overlap report adds "friend has K more episodes of N shows". **Owner decision.** | Merging episodes into a local series is the merged-version hazard (6.1). Episode-level dedup shows two "Show" rows. |
| 4 | Season and episode numbers are exactly what the friend's server shows. Home never renumbers. Out of scope: anime absolute numbering, date-based episodes with no number, "Season Unknown", alternate versions per episode (primary source only), episode guest stars and directors. | Home runs no metadata providers, so it cannot remap numbers. |
| 5 | Skip intro and outro are **locked off** in v2.7. `DisabledMediaSegmentProviders=<all>` already applies (5.6). Fetching the peer's `/MediaSegments` is a v2.8 candidate. | It adds a peer route and a DB write path. |
| 6 | A mapping has one type: `movies` or `tvshows`. Mixed or untyped peer views stay unselectable. | A mixed library cannot be locked down per type. |

### v2.7.1 Stub layout (`TvLayout`, a pure class)

```
<StubRoot>/<peerKey>/<Subfolder>/                        <- library type tvshows
  Breaking Bad (2008) [tvdbid-81189]/                    PathSanitizer 5.5; id order tvdb, tmdb, imdb, else [shoal-<key8>]
    tvshow.nfo
    poster.jpg  backdrop.jpg  banner.jpg  landscape.jpg   (extension from magic; landscape = peer "Thumb")
    Season 00/                                           fixed "Season " + 2-digit-minimum integer
      season.nfo   poster.jpg
      S00E01.strm   S00E01.nfo   S00E01-thumb.jpg
    Season 01/
      season.nfo   poster.jpg
      S01E01.strm  S01E01.nfo  S01E01-thumb.jpg
      S01E02-E03.strm  S01E02-E03.nfo  S01E02-E03-thumb.jpg      <- multi-episode file
```

- **Write order** per series: folders, `tvshow.nfo`, series images, season folders and `season.nfo`, then per episode NFO, thumb, and `.strm` last (temp file plus rename). A crash leaves no half-item. A series folder with no `.strm` is not an item.
- **Series NFO** (`tvshow.nfo`): `title`, `originaltitle`, `sorttitle`, `year`, `premiered`, `enddate`, `status` (closed enum `Continuing` or `Ended`), `plot`, `mpaa` (only if in the rating table), `rating`, `genre` (up to 32), `studio` (the network, up to 16), `uniqueid` (canonical tvdb, tmdb, imdb only), `actor` (up to 5, no thumb), and tags `Shoal` and `Shoal: <Peer>`.
- **Season NFO**: `title` (only if it differs from "Season N" and is at most 100 characters), `seasonnumber`, `plot` (up to 2,000), and the tags.
- **Episode NFO** (`episodedetails`): `title`, `season`, `episode`, `episodenumberend` (multi-episode), `aired`, `plot`, `rating`, and the tags. Never written: `uniqueid`, `actor`, `director`, `credits`, `runtime`, `thumb`, `fileinfo`, `lockdata`, or anything containing a URL. Tags are written at all three levels so stock `BlockedTags` works whatever inheritance does.
- **Numbers.** Season 0 to 2100 (year-style seasons exist). Episode 0 to 9999. `IndexNumberEnd` is at least the start and at most start+99. An episode with a missing or out-of-range season or episode number is skipped and counted (`skippedNoNumber`, shown in the UI).
- **Overlaps and duplicates.** Sort by (season, episode, end, peer Id). Accept an episode only if its range does not overlap an accepted one. The others are skipped as `duplicate-peer` (counted).
- **Orphans.** An episode whose `SeriesId` is not in this run's series listing is dropped and counted.
- **Renumbering.** If the peer moves an episode to another number, Shoal removes the old stub and adds the new one (one gone, one new). The local item id changes. Watch state follows the series provider id plus S/E through core's user-data keys when the series has provider ids (T-TV-9 verifies it).
- **Never renamed:** the series folder after creation (the NFO title wins), and a season folder.

### v2.7.2 Catalog sync

**Peer calls** (stock Jellyfin, share-user token; the same `PeerCallProfile`s as movies). All use `EnableImages=false&EnableUserData=false&SortBy=SortName,Id`. Add `isMissing=false`, and drop any DTO whose `LocationType` is `Virtual` in code (the missing/virtual-episode rule).

| Listing | Request |
|---|---|
| Series | `/Items?ParentId=<lib>&Recursive=true&IncludeItemTypes=Series&Fields=ProviderIds,Etag&StartIndex&Limit=200` |
| Seasons | same with `IncludeItemTypes=Season` (metadata and poster only) |
| Episodes | same with `IncludeItemTypes=Episode`; the base DTO already carries `SeriesId`, `ParentIndexNumber`, `IndexNumber`, `IndexNumberEnd` |
| Details | `/Items?Ids=<=50>&Fields=ProviderIds,Etag,Overview,Genres,Studios,People,OriginalTitle,OfficialRating,CommunityRating,PremiereDate,EndDate,Status,MediaSources,Trickplay,SortName` |

- **Page size 200.** The listing byte cap must scale. Kiro checks the cheap-listing cap in `PeerCatalogReader.cs` and sets it from a measured 200-episode page times 4.
- **Scale.** 5,000 episodes is 25 listing pages plus 100 detail batches on first sync, about 2 to 4 minutes over a 150 ms link. 50,000 episodes is 250 pages, about 2 minutes. The full cheap listing runs every sync because deletion detection needs it.
- **Complete run.** All three listings pass the 5.2 tolerance, `|collected - Total| <= max(5, 1%)`, **each**. If one listing is partial, no removals happen anywhere in that mapping.
- **Etag storm.** A peer-side "refresh all" can bump every Etag. After the details fetch, compare a `contentHash` of the **sanitized** fields. Rewrite NFO, stub or images only when it differs, and report a filesystem change only for those paths. Details fetching runs in the structure budget (15 minutes). The rest queues in `pendingDetail` and resumes next run.
- **In-place move.** A gone series whose canonical provider ids match a new series is a move (the peer re-imported its library): rebind `peerItemId`, and keep the stub, the local id and the watch state. Without provider ids, require an exact normalized title+year match that is unique on both sides. Inside a moved or same series, a gone episode whose (season, episode, end) equals a new episode's is also rebound in place. A peer library re-import therefore trips no guard.
- **Mass-deletion guard** (per mapping, counting **episodes**; series and seasons never count). Hold if either:
  - `|gone episodes| > max(TvFactor * MassDeletionMinCount, MassDeletionPercent% of active episodes)`, with `TvFactor = 4` (default 100 episodes), or
  - `|gone series| > max(3, MassDeletionPercent% of active series)`.

  A mapping that returns 0 episodes while the catalog has more also holds. Auto-release after 3 complete syncs over 24 hours and manual Release work as for movies. Release works by series group.
- **New-items guard** (per mapping, after the first sync of that mapping; v2.4 only exempted the peer's first sync, so K36 makes "first" per mapping). Hold if `|new episodes| > max(4 * NewItemsMinCount, NewItemsPercent%)` (default 2,000) **or** `|new series| > max(10, NewItemsPercent%)`. There is no auto-release. The alert reads "Hops added 3 series, 1,240 episodes".
- **Entity caps** (additions to 5.10).

| Cap | Default | On breach |
|---|---|---|
| Playable stubs per peer (movie sources plus episodes) | 50,000 (existing `CatalogDocument.MaxEntries`) | Sync stops with an alert |
| Series per peer | 5,000 | Extra series dropped, counted, alert |
| Seasons per series, and per peer | 200, and 25,000 | same |
| Episodes per season, per series | 2,000, 10,000 | same |
| Actors per series | 5 | Extra dropped |

  Containers do not count toward the 50,000. The existing `Take(MaxEntries)` in `CatalogStore.cs:230` would silently truncate the document, which orphans stubs. Container entries are therefore stored outside that count, and the planner enforces the cap before the store.
- **Catalog schema 4** (K34 owns it). `entries[].kind` is `movie`, `series`, `season` or `episode`; a schema 3 file reads as all `movie`. Episode entries add `parentKey` (series stubKey), `season`, `episode`, `episodeEnd`, `peerSeriesId`. Container entries have `StubRelPath=null` and a `folderRel`. Newer schema goes to recovery as always.
- **catalog.json size.** 50,000 entries of about 600 bytes is about 30 MB per atomic rewrite. **Risk:** K36 measures at 20,000 episodes. If a save takes over 1 s or the file exceeds 15 MB, shard into `catalog-<mappingKey>.json`. This is a stated risk, not a decision.
- **Phase E refresh.** One `ReportFileSystemChanged` per changed **series folder**, or one for the library root if more than 50 series changed (the root path is also used for a first sync). Never `QueueLibraryScan()`. If the stream count of the peer is above 0, defer the refresh up to 10 minutes. Owner feedback 01 showed HLS stalls during scans, and a 5,000-stub scan is heavier than a movie sync.
- **Dedup index** (`JellyfinSyncSources.cs`): local `Series` items outside StubRoot keyed by canonical tvdb, tmdb, imdb, with `title|year` as the weak key. Cross-peer: lower priority wins, as in 5.3.

### v2.7.3 Images, quotas and time budgets

- Series: poster (maxWidth 600), backdrop (1280), banner (1000), landscape/Thumb (1000). Season: poster (400). Episode: Primary as `-thumb.jpg` (maxWidth 480). Same JPEG/PNG magic and header-walk rules (5.4). Caps: banner 1.5 MB, thumb 2 MB, others as 5.4.
- **Order:** series posters, season posters, then series backdrops, banners and landscapes, then episode thumbs (newest series first). Fetching stays serial.
- **Budgets.** Structure (listings, details, stubs, NFO) 15 minutes, as before. The **image stage has its own 10 minute budget per run** and resumes next run through `imagesPending` (a list of keys). If images are left, `SyncScheduler` schedules a follow-up in 10 minutes, at most 12 in a row.
- **Cost.** 5,000 thumbs is about 17 minutes serial (estimate: 0.2 s each), plus about 6 minutes with the image child (K32). Expect 3 runs for a big first sync. Stubs and series art show up first. `TvEpisodeThumbs` (XML, default true) can turn the thumbs off.
- Disk quota (2 GB per peer by default) counts bytes Shoal wrote. 5,000 episodes cost about 125 MB of thumbs plus tiny NFOs. Quota breach still imports titles with placeholder art.

### v2.7.4 Metadata sanitizing for the new fields (additions to 5.5)

All text uses the 5.5 common rules. All numbers are parsed as integers and re-emitted. `Id`, `SeriesId` and `SeasonId` are GUIDs. Provider ids follow 5.5 (tvdb and tmdb digits, imdb `tt`).

| Field | Rule |
|---|---|
| Series Title, OriginalTitle, SortName | up to 300 |
| Series Overview | up to 10,000, keeps `\n` (v2.4) |
| Series Status | closed enum `Continuing` or `Ended`, else dropped |
| EndDate, PremiereDate, AirDate | valid date 1870 to 2100, else dropped |
| Studios (network) | up to 16 values by 128 |
| Season IndexNumber | 0 to 2100 |
| Season Name | up to 100, dropped if it equals the default pattern |
| Season Overview | up to 2,000 |
| Episode Name | up to 300 |
| Episode Overview | up to 10,000, keeps `\n` |
| Episode IndexNumber | 0 to 9999 |
| Episode IndexNumberEnd | start to start+99 |
| Episode ParentIndexNumber | 0 to 2100 |
| Episode RunTimeTicks | 0 to 48 h |
| Episode CommunityRating | 0 to 10 |
| Episode OfficialRating | not written (inherits from the series) |
| Episode MediaSources | primary source only, same rules as movies (container, codec, size) |
| Image tags (Primary, Backdrop, Banner, Thumb) | `^[A-Za-z0-9]{1,64}$`, else no image |

Fixtures F-TV-01 to F-TV-12: title `S09E09`, series "24", `..`, NUL and bidi controls, a 300-character title, tt-injection in provider ids, IndexNumber -1 and 99999, End less than start, 250 seasons, 3,000 episodes in one season, an episode with a wrong `SeriesId`, a `Virtual` episode, two episodes with equal numbers, overlapping ranges, and a 5.5 hostile `Overview` with `<script>`.

### v2.7.5 Playback

- **The same path as movies.** `POST Items/{episodeId}/PlaybackInfo`, the interception table rows, the grant, the Peer PlaybackInfo with `peerItemId` and `peerMediaSourceId`, the HLS proxy, subtitles, trickplay and session reporting are unchanged. Episode stubs are `Video` items. `ShoalIndex` resolves them to a playable record exactly as for movies.
- **ShoalIndex kinds.** An id in `ShoalIndex` is now `Playable` (movie or episode, has a stub record) or `Container` (series or season, path under StubRoot). Container ids: PlaybackInfo answers 404 (the user is told "Pick an episode"), every byte row in 6.1 answers as for a federated id with no stub (403 or 404, zero Peer calls), and an API key stays 403. The path-rule fallback of 3.5 already treats them as federated. The route-coverage test (K13) adds container-id cases.
- **Next episode and resume.** Both are computed by Home from the local stub items. Resume uses the local `PlaybackPositionTicks` and `StartTimeTicks` in PlaybackInfo, exactly as movies (6.5). Next Up, Continue Watching, "Up next" and the autoplay queue call Home's own `/Shows/...` and `/UserItems/Resume` and then do a normal PlaybackInfo for the next episode. **Acceptance depends on `RunTimeTicks`** on every episode stub (from `StubEnricher` using catalog `runtimeTicks`), or the 90% played mark and the resume row misbehave.
- **Binge handover.** The next episode often asks for PlaybackInfo before the previous stream's slot is released. With the half-of-peer rule at `maxConcurrentStreams` of 2 that would answer `RateLimitExceeded` mid-binge. The rule becomes: the pre-check allows **one** extra stream slot per (user, peer) when the new item shares the series and `DeviceId` of a held slot. The peer total and `MaxStreamsPerUser` stay hard limits, and the allowance never reaches the first-byte check. This is a bounded relaxation, like the version-set multiplier Warden accepted (v2.6.4); Warden batch C reviews it.
- **Skip intro and outro: off.** No segments exist for stubs. jellyfin-web's and Moonfin's "Skip Intro" button therefore never shows. Moonfin's "Next episode" overlay is timed from the end of the file in its own code; Pilot confirms on iOS (S13).
- **Trickplay for episodes.** `TrickplayInfoWriter` and `TrickplayHandler` already work per stub source. Extend the writer to iterate `Episode` items, and make `TrickplayCheckedUtc` rechecks rotate: at most 200 episodes per sync and once a week per entry, so a peer without trickplay does not cost 5,000 detail fetches per run. `TrickplayCacheGb` default stays 1; the Settings help says TV can use more.
- **Peer sees:** the share user playing the episode (D8). It never sees Home usernames.

### v2.7.6 Visibility and the library picker

- `AdminPeerLibrariesController.cs`: `selectable` is true for `movies` and `tvshows` (when `TvShowsEnabled`). The count for tvshows is `{seriesCount, episodeCount}` from two cheap `Limit=0` calls. `RemoteLibraryDto` adds those fields. The mapping stores `CollectionType = "tvshows"`; `state.json` stays schema 2.
- `Visibility/JellyfinSources.cs:64` creates `CollectionTypeOptions.tvshows`. Locked options (5.6) for a TV library add `TypeOptions` entries for **Series, Season and Episode** (and keep Video), each with empty `MetadataFetchers` and `ImageFetchers`, so no type falls back to server-wide fetchers. Also set `EnableAutomaticSeriesGrouping=true` (K43: one friend's series and another friend's extra episodes of it, in one library, show as one series; 2.9.4: in an admin's own library with it off, those extra episodes stay hidden sources instead), `SaveTrickplayWithMedia=false`, and keep every 5.6 lock. The "every provider" lists are re-applied as for movies.
- `AccessService`, Reconcile, share-user rules and the drift list work per **library id**, so they are unchanged. Acceptance (T-TV-7): a TV library behaves exactly like a movie library for D3, D4, restricted profiles and share users. A series' `mpaa` makes restricted profiles block episodes (inherited rating).
- `ShareService.cs:502` hard-codes `type = "movies"` for the Peer role's shareable list; it reports the real collection type. A Shoal Peer can share a TV library, but a StubRoot library stays refused (as for movies).
- A TV library is named "<Peer> TV shows". Remote views of other types keep their "later" label.
- **Peer guard.** Check `PeerGuardFilter` route enumeration (8.4): `Items?IncludeItemTypes=Series,Season,Episode`, and `Images/Banner` and `Images/Thumb`, must pass in Enforce mode (T-TV-11).

### v2.7.7 Security rules for Warden

1. **Names.** Every folder or file name comes from integers (Season/episode), from PathSanitizer (series folder), or from a fixed table. No peer string is in an episode, season or thumb file name. Canonical-path confinement and the symlink check apply at **every** created component.
2. **Counts.** Hard caps in v2.7.2; the planner stops before the store; `TotalRecordCount` is never trusted for allocation; duplicates are skipped, not overwritten; relationship fields are re-derived from validated ids.
3. **Folders.** Seasons per series and per peer are capped, so a peer cannot make 25,000 one-episode seasons; total directories are counted into the quota.
4. **Deletion.** Removal deletes only paths recorded in the catalog plus fixed names; folders only if empty; guard and confirm-by-`Ids=` first.
5. **Auth and routes.** No new endpoint, no new anonymous action. Container ids and episode ids pass through CallerContext. T-SEC-15 gains rows for Series, Season and Episode ids on each 6.1 route.
6. **No secrets.** No token, no peer id and no host appears in any NFO, `.strm` (sentinel only), file name, log or admin JSON. NFO fields carry no URL.
7. **Hostile `Overview`/`Title` content** is stripped per 5.5. XML uses `XmlWriter` with `CheckCharacters=true`.
8. **Residual** (same class as 5.6): cast names reach TMDb from the series NFO (5 actors per series, none per episode).

### v2.7.8 Failure modes

| Failure | Behavior |
|---|---|
| Core does not group `S01E01.strm` correctly (T-TV-0) | Feature stays off; adjust layout (e.g. stem `Show - S01E01`) and re-run the gate. |
| Core makes virtual seasons or missing episodes | T-TV-0 detects; a post-scan check removes nothing, and the doc records the oddity. |
| Peer re-imports its library | Moves rebind in place; guards stay quiet. |
| Peer refresh bumps every Etag | `contentHash` stops rewrites; `pendingDetail` spreads the fetches. |
| Peer scanning shows few episodes | Partial or zero-listing holds removals (guard). |
| Image stage runs out of time | `imagesPending` resumes; stubs and posters already usable. |
| Peer offline | As movies (5.7): titles stay listed, play fails in under 100 ms. |
| Binge on a peer at its stream limit | Handover allowance; beyond it `RateLimitExceeded`. |
| `catalog.json` too big | Sharding trigger above. |
| Home scan of thousands of stubs slows playback | Refresh deferred while the peer has streams; Pilot measures (T-TV-12). |

### v2.7.9 Tasks (K34 to K40), lanes, and shared files

Lanes: A = Kiro (security and sync core). B = Kiro 2 (self-contained libraries, images, admin). C = Kiro 3 (library, index, playback; with only two coders, Kiro takes K38 and K39 after K36). Rules: each task's first commit publishes its interfaces; K34 owns every shared file below; Warden batch C is K36, K38 and K39; Lumen reviews K40 before merge.

| ID | Lane | Task | Depends on | Shared files touched | Acceptance criteria |
|---|---|---|---|---|---|
| T-TV-0 | Pilot | **Lab gate, no Shoal code.** On lab-server build by hand: series folder with `tvshow.nfo`, `Season 00` and `Season 01`, `S01E01.strm`, `S01E02-E03.strm`, `S00E01.strm`, NFOs, posters, banner, landscape, `-thumb.jpg`. | none | none | (1) one Series, Seasons "Specials" and 1, correct numbers, and multi-episode shows `IndexNumberEnd`; (2) NFO titles win over the file name; (3) images are picked up; (4) no Virtual episodes or extra seasons appear; (5) Continue Watching, Next Up and Series "Play" find the stubs; (6) a library with only Series/Season/Episode TypeOptions makes no outbound metadata calls; (7) provider-id user-data key carries watch state between two series folders with the same tvdb id. If it fails, adjust the layout and re-run before K36. |
| K34 | A | **Foundation, no behavior change.** Catalog schema 4 (`kind`, parent, numbers, container entries, caps in `TvLimits`), `TvShowsEnabled` and `TvEpisodeThumbs` with clamps, interfaces `ITvSanitizer`, `ITvLayout`, `ITvNfoWriter`, `IPeerTvReader`, `ITvPlanner`, `ITvImageWork`, and `ShoalIndex` kind API (`Playable` or `Container`); FakeJellyfinPeer TV endpoints and a generator for N series by M seasons by E episodes in a new `FakePeerTv.cs`; fixtures F-TV-01 to F-TV-12. | none | `Sync/CatalogTypes.cs`, `CatalogStore.cs`, `SanitizedTypes.cs`, `SourceHooks.cs`, `Configuration/PluginConfiguration.cs`, `SettingsValidator.cs`, `Authorization/ShoalIndex.cs` (API only), `Registrations/Tv.cs` | All existing tests pass unchanged. T-TV-1: schema 3 to 4 round trip, newer schema goes to recovery, nothing renamed. Out-of-range settings clamp (T-REG-2 extended). Container entries are outside the 50,000 cap and the store does not truncate (test at 50,001 playable). Fakepeer serves a 5,000-episode tree. |
| K35 | B | **Pure TV libraries:** `TvSanitizer` (v2.7.4), `TvLayout` (names from integers, overlap and duplicate resolution, collision handling, in-place move matching), `TvNfoWriter` (tvshow, season, episode), series dedup keys. | K34 | new `Sync/TvSanitizer.cs`, `TvLayout.cs`, `TvNfoWriter.cs`, `TvDedup.cs` | T-TV-2: fuzz of 10,000 hostile titles stays inside the root and yields valid XML; F-TV-01 to F-TV-12 each produce the specified outcome; numbers re-emitted from integers; overlap rule deterministic; no URL in NFO; `S01E01-E02` naming; case/normalization-insensitive collisions. |
| K36 | A | **TV sync engine:** `PeerTvReader` (three listings, details, paging), `TvPlanner` (diff, moves, orphan and cap handling, guards counting episodes, partial rule, `contentHash`, `pendingDetail`), `TvStubWriter` (write order, removal confined), integration in `CatalogSyncService` and `SyncRun`, per-mapping first-sync flag, series-level dedup, per-series refresh reporting. | K34, K35 interfaces | `Sync/CatalogSyncService.cs`, `SyncRun.cs`, `PeerCatalogReader.cs`, `StubWriter.cs`, `Dedup.cs`; new `Sync/TvPlanner.cs`, `PeerTvReader.cs`, `TvStubWriter.cs` | T-TV-3 (sync: add, change, remove, move, renumber, rebind, partial listing, wrong `SeriesId`, virtual episode, caps); T-TV-4 (guards: 150 gone episodes held, 3 gone series of 20 held, a peer re-import trips nothing, additions hold at 2,001 episodes, first sync of a new mapping exempt, Release by series); T-TV-5 (5,000-episode fake sync under 4 minutes on fakepeer, memory flat, `catalog.json` save time and size recorded, shard trigger honored); a crash mid-write leaves no `.strm` without its NFO; only the TV library refreshes. Warden batch C. |
| K37 | B | **Images:** banner, landscape, season and episode thumbs in `ImageFetcher`, per-kind caps and widths, image queue with `imagesPending`, 10 minute image budget, follow-up scheduling, `TvEpisodeThumbs`. | K34 | `Sync/ImageFetcher.cs`, `SyncScheduler.cs` | T-TV-6: priority order; a 5,000-thumb fakepeer set finishes over multiple runs with the 12-run limit; WebP, oversize and EXIF-thumbnail trick rejected; quota stop imports titles with placeholders; thumbs off writes none. |
| K38 | C | **Library, index, enricher:** create `tvshows` libraries with the locked options; index `Series`, `Season`, `Episode`; `StubEnricher` and `TrickplayInfoWriter` for Episode; local Series dedup index; picker `selectable`, counts and DTO; `ShareService` real type; `PeerGuardFilter` check. | K34 | `Visibility/JellyfinSources.cs`, `Authorization/ShoalIndex.cs`, `Sync/JellyfinSyncSources.cs`, `Sync/StubEnricher.cs`, `Sync/TrickplayInfoWriter.cs`, `Api/Admin/AdminPeerLibrariesController.cs`, `Sharing/ShareService.cs` | T-TV-7 (access modes, D3/D4, restricted profiles, share users on a TV library; second Reconcile writes nothing); the created library shows Series/Season/Episode TypeOptions with empty fetchers; episode stubs have `RunTimeTicks` (forced refresh keeps it); trickplay info saved for episodes; index kinds correct after scan; picker lists tvshows selectable only when `TvShowsEnabled`; T-TV-11 guard routes pass. |
| K39 | C | **Episode playback:** container-id handling on every 6.1 route, PlaybackInfo for Episode, binge handover allowance, session reporting test with Episode, trickplay recheck rotation. | K38 | `Playback/PlaybackInfoHandler.cs`, `PlaybackInfoBuilder.cs`, `Playback/InterceptHandlers.cs`, `StreamSlots.cs`, `Grants.cs` (names per v2.6.9) | T-TV-8: PlaybackInfo for Series and Season ids is 404 with zero fakepeer requests; T-SEC-15 gains Series/Season/Episode rows on each route; T-SEC-20 body equals 6.2.1 for an episode; handover test: with `maxConcurrentStreams=2`, episode N then N+1 on the same device and series succeeds, a different series on the same user is refused; allowance never exceeds peer total or `MaxStreamsPerUser`; canary passes. Warden batch C. |
| K40 | B | **Admin UI and API:** tvshows selectable in the wizard picker with "N series, M episodes", alerts for held additions and removals worded by series and episodes, `skippedNoNumber`, `imagesPending` status, Settings rows (`TvShowsEnabled`, `TvEpisodeThumbs`), overlap report lines, "Release by series". | K36 DTOs, K37 | `Api/Admin/AdminStatusBuilder.cs`, `AdminSettingsController.cs`, `AdminPeerLibrariesController.cs` (DTO only; coordinate with K38), `Configuration/shoal.html/js/css` | Round trip of every setting; every alert reachable with fakepeer; no token or peer GUID in the HAR; peer strings as `textContent`; Lumen review. |

**Waves.** Wave 0: K34 (A) with T-TV-0 (Pilot) and a Lumen note on picker copy. Wave 1, parallel: A K36; B K35 then K37; C K38 then K39. K36 builds against K35's published interfaces and fakes until K35 lands (K35 is short, so it goes first in lane B). Wave 2: B K40, A integration fixes, Warden batch C (K36, K38, K39), Lumen on K40. Wave 3: Pilot full run, then the owner's iPhone.

**Shared-file rules.** K34 alone edits schema, settings and registration files. `ShoalIndex.cs` is edited by K34 (API) then K38 only. `ImageFetcher.cs` is K37 only. `AdminPeerLibrariesController.cs` is K38 first, then K40 changes only the DTO. `shoal.*` is K40 only.

### v2.7.10 Lab test plan for Pilot (lab only, never production container)

Fixtures: generate tiny (1 to 3 s) media with ffmpeg and hard-link them for scale. Tree A: 3 series (one with Season 00, one with a multi-episode file, one with 250 episodes in one season). Tree B (scale): 300 series, about 5,000 episodes. Real Peer is lab-server; fakepeer covers hostile cases.

| ID | Test |
|---|---|
| T-TV-0 | Gate in v2.7.9. |
| T-TV-1 to T-TV-6 | Unit and fakepeer tests from K34 to K37. |
| T-TV-7 | Library and visibility: tvshows library created; admin pre-ticked only; Everyone mode; restricted profile with a rating limit; share user never admitted; uninstall rules (T-VIS-8) on a TV library. |
| T-TV-8 | Routes: container ids on every 6.1 route, with and without an API key, no credentials. |
| T-TV-9 | Watch state: mark S1E2 played, rename the peer's episode numbering, re-sync; state follows the user-data key. Peer library re-import: ids change, stubs unchanged, local ids unchanged. |
| T-TV-10 | Real play (web, then Moonfin iOS): browse series, season, episode; play direct and forced conversion; resume mid-episode; autoplay next; Next Up and Continue Watching rows; specials; multi-episode file; subtitle and audio switch; trickplay scrub (S11); Skip Intro absent. Run two accounts; try 3 accounts against `maxConcurrentStreams=2`. |
| T-TV-11 | Shoal Peer in Enforce mode: listing and image calls for the new types pass. |
| T-TV-12 | Scale (Tree B): first sync time to stubs visible; time to all thumbs; Home CPU and memory during the scan; HLS start time during a scan; second sync with no changes makes only listing calls (count them); a doctored Etag storm (all etags changed) rewrites zero files; `catalog.json` save time and size at 5,000 and 20,000 episodes. |
| T-TV-13 | Hostile peer: all F-TV fixtures, 25,000 one-episode seasons, 100,000 episodes, `TotalRecordCount` lying, paging that repeats ids. Expected: caps hit, alerts raised, nothing outside StubRoot, memory flat. |
| T-TV-14 | Log grep: no token, peer id or host in logs, NFOs or `.strm` files. |

Pilot reports numbers (not pass/fail only) for T-TV-5, T-TV-6 and T-TV-12; the 4-minute, 23-minute and 15 MB figures above are my estimates, not measurements.

### v2.7.11 Open

Owner: (1) series-level dedup, recommended (v2.7.0 #3); (2) episode thumbnails on by default; (3) accept that Skip Intro stays absent for friends' TV shows. Risks to weigh: first-sync scan load on Home (T-TV-12), `catalog.json` size, binge allowance relaxation (Warden), unverified core behavior of virtual items and seasons (T-TV-0), and Moonfin's next-episode overlay without segments (Pilot, iOS).

---

## Changes in v2.6 (phases 5 and 6)

Author: ATLAS, 2026-10-15. v1 is built and lab-proven (Pilot 04 rounds 1 to 5; owner iPhone T-CLI-17 PASS, `docs/tests/owner-moonfin-ios-02.md`). Phase 4 (U8, U9) is Kiro 2's current work and is not touched. There was no v2.5. Nothing below changes v1 behavior unless its switch is on. Task rows K24 to K33 are in 11.2; shared files and run order are in v2.6.9. Section numbers of the older text are unchanged; where an older line is superseded it now points here.

### v2.6.0 Decisions in one table

| Item | Decision | Tasks |
|---|---|---|
| (a) Peer-plugin token rotation | Build. Invite-paired peers only (the Peer runs Shoal). Manual button plus optional schedule, default **off**. | K25 K26 K27 |
| (b) Two-way UX | Build. Friends are linked by server-id prefix, **for display only**. | K27 |
| (c) Real-device matrix | Checklist in v2.6.3. Needs one small code piece: the intercept counters become readable by Pilot. | K24 K27 + Pilot, owner |
| (d) Alternate versions | Build as several `.strm` per title folder (Jellyfin's own multi-version naming). Gated by a 30-minute lab check first. A better peer copy is **never** merged with a local item. | K28 K29 |
| (e) Trickplay | Build. JPEG tiles only, fetched lazily and cached on disk; `tiles.m3u8` stays 404. | K30 |
| (f) Attachments and fonts | Build, fonts only, default on, kill switch. | K31 |
| (g) Image re-encode child process | Build last; lowest value (honest limits in v2.6.7). Default on when the self-test passes. | K32 |
| (h) Progressive (non-static) proxy | **Dropped** (v2.6.8). | none |

### v2.6.1 (a) Token rotation: `POST /Shoal/Share/Rotate`

Why it is small: a Shoal Peer already re-issues a token on the same DeviceId at Approve (4.1b step 3, `AuthenticateDirect`; re-auth on the same DeviceId kills the old token, 4.4). Rotation is that sequence, started by Home. 4.4's caveat stands: rotation limits the life of an old leaked token; it does not stop a thief who holds the current token (he can rotate too; Home then shows `AuthFailed`, the admin re-pairs, and Approve signs the thief's device out). Quick Connect (stock Peer) pairings cannot rotate; re-pair is their rotation (4.4).

**Wire (Peer).** `POST /Shoal/Share/Rotate`, `[Authorize]` (core authentication; token in the `Authorization` header, as every Home call), body `{"v":1}` (at most 64 bytes, `application/json`). Reply `200 {"v":1,"token":"<32 hex>","deviceId":"shoal-<12 hex>-<8 hex>","rotatedUtc":"<ISO>"}` written by `ResponseWriter` (no-store). Errors: `401` (core), `404 {}` (caller is not an enabled share user, or the token is not the live token of the share's expected device; identical body for both), `409 {"error":"busy"}`, `429` plus `Retry-After`, `500 {"error":"rotationFailed"}`. No other bodies.

**Peer procedure** (K25; per-share `SemaphoreSlim(1)` taken with `Wait(0)`, else 409):
1. Caller checks, in this order: `Token` claim and user id from `HttpContext.User` (never `GetAuthorizationInfo`, 8.8); user id is in `peerRole.shares`, the share is enabled and the account is not `IsDisabled`; the token must be the access token of one of the user's devices in `expectedDeviceIds` (matched through `IDeviceManager.GetDevices`; Kiro confirms the 12.1 `Device` type exposes the access token; if it does not, require `GetDevices(user).Count == 1` and that the single device is in `expectedDeviceIds`, which the sweep makes true). **Do not trust the `DeviceId` claim**: it is client-chosen (3.5, W4-2). Any failure: `404 {}`.
2. Rate: per share 1 per 10 minutes and 6 per day (counted after step 1 passes), `429` beyond; a route-wide limiter of 30 per minute for all callers.
3. The same internal method as Approve steps 1 to 4 (K21's code is refactored into `IssueTokenAsync(share, deviceId)` and both call it): sign out the share user's devices, keep `expectedDeviceIds`, re-assert `EnableAllDevices=false` and `EnabledDevices=[deviceId]`, `AuthenticateDirect` with the **existing** DeviceId. One internal retry of `AuthenticateDirect`.
4. Return the token once. It is never logged, stored or kept after the response. State gets `lastRotatedUtc` and `rotationCount` on the share record; the audit line reads "Rotated the token of share <friend>" (name as text). Friend detail shows "Token last rotated <time>".
5. If step 3 fails twice: the share is left with zero devices (the old token is already dead), `rotationFailedUtc` is set, the admin sees the alert `rotationFailed` ("Home must pair again"), and the caller gets 500.

**Home procedure** (K26): `TokenRotation.RotateAsync(peerHandle, manual|scheduled)`.
- Preconditions, else `409` with `error`: `notSupported` (pairing method is not `invite`), `notOnline` (not `Online`, trust window lapsed, circuit open), `streamsActive` (any stream or conversion slot held for this peer), `busy` (pairing session live or a rotation running).
- Mark the peer `Rotating` for at most 15 s: PlaybackInfo and sync for that peer answer as for an open circuit (`RateLimitExceeded`, no offline notice), HealthMonitor skips its tick, and `AuthFailed` is not set by a 401 seen in this window. The precondition check and the mark are one atomic step under the slot ledger.
- `POST` with a new `PeerCallProfile.Rotate` (connect 3 s, headers 5 s, idle 5 s, total 8 s, 4 KB; does not count toward the breaker; **never retried**, a retry would carry a dead token).
- Validate: `token` matches `^[0-9a-f]{32}$`, `deviceId` equals Home's pairing DeviceId, else `badResponse`. Make the new token the active credential **in memory at once**, then `GET /Users/Me` with the 4.2 step 4 checks (administrator: log out, refuse, `AuthFailed`) and the `/System/Info` pin. Then `SecretStore.Put` (a replace; new nonce). A failed Put is retried 3 times over 30 s while the in-memory token keeps working; if it still fails the peer shows alert `rotationStoreFailed` and a Home restart loses the token (re-pair).
- Outcomes on the peer record: `lastRotatedUtc`; on error `rotationError` in `health.json` with a kind: `rejected` (404 from the Peer), `lostResponse` (timeout or transport error after the request was sent; the Peer may have rotated, Home's token is probably dead: peer set to `AuthFailed`, Re-pair), `badResponse`, `storeFailed`.
- Schedule: `rotationDays` per peer, one of 0 (off, default), 30, 60, 90. An hourly check; when due and preconditions hold it rotates; if blocked for 24 h it raises `rotationOverdue`.

**Admin API (Home, `RequiresElevation`).** `POST Shoal/Admin/Peers/{h}/Rotate {}` (1 per 10 min, then `429`): `200 {rotatedUtc}`, `409 {error}` as above, `502 {error: rejected|lostResponse|badResponse}`. `PUT Peers/{h}` accepts `rotationDays`. `GET Peers/{h}` gains `rotation {supported, days, lastRotatedUtc, nextDueUtc, error?}`. Peer side: `GET Shares/{id}` gains `lastRotatedUtc`, `rotationCount`, `rotationFailed`. Only handles, never tokens.

**Security rules (Warden).** (1) The route is `[Authorize]`; the reflection test still allows exactly one `[AllowAnonymous]` (the Pair route). (2) Non-share and wrong-token callers get one uniform 404; an admin calling it is a non-share caller. (3) The token travels only in the response body of this call, over the same channel as every Home call (so LAN-cleartext peers see no new exposure); `Redact` covers the property name `token`; a log-grep test over Debug logs finds no 32-hex token. (4) No secret is added at pairing; a shared rotation key was considered and **not adopted** (a token thief who also reads `secrets.enc` has it too, and it changes the K22 wire). (5) The Peer never pushes tokens; only Home can start rotation.

**Failure modes (accepted, in the README).** F1 response lost after the Peer minted: Home's token is dead, `AuthFailed`, re-pair by invite (the window is one 8 s request on a tailnet). F2 mint fails after sign-out: same. F3 Home crashes between receiving and storing: same, window of milliseconds. F4 a stream is cut: prevented by the `streamsActive` refusal. F5 a thief rotates first: see the opening paragraph.

**Tests.** T-ROT-1 happy path (old token 401 within 1 s, new token works, one device, DeviceId unchanged, `expectedDeviceIds` unchanged). T-ROT-2 negative matrix: no token, garbage token, an admin token, another share's token, a paused share, a leftover token of a deleted device: uniform 404 or core 401, no state change. T-ROT-3 rate limits and concurrency (two at once: one 409). T-ROT-4 fault injection: `AuthenticateDirect` fails once (internal retry succeeds), fails twice (500, zero devices, alert). T-ROT-5 Home: each precondition refusal; a stream held; fakepeer `/Rotate` returning a short token, a wrong DeviceId, a slow body, a 404. T-ROT-6 `Rotating` window: concurrent PlaybackInfo answers `RateLimitExceeded`, a 401 in the window does not set `AuthFailed`. T-ROT-7 persistence: restart Home after a rotation; peer still `Online`; `secrets.enc` has a new nonce. T-ROT-8 Debug log grep. Lab prerequisite P-HTTPS: a real invite pairing needs an https or tailnet address (Pilot 04 round 2: the plain-http refusal is correct); Pilot puts a lab CA and an https front on lab-server that lab-server trusts, so a **real** Home to Shoal-Peer pair exists for T-ROT-1 and for the whole invite path, which no round has run end to end yet (release risk).

### v2.6.2 (b) Two-way sharing: what the screens show

A friend who both shares with you and watches you appears twice today: a Home-side peer record and a Peer-side share. v2.6 links them.

**Link rule (display only; no authorization decision reads it).** Share record gets `homeServerIdPrefix` (12 hex, from the invite request at Approve; for older shares derived from `expectedDeviceIds[0]`, whose form is `shoal-<12 hex>-<8 hex>`). A peer record is linked to a share when `pinnedServerId[0..12]` (lowercase N) equals that prefix. A friend can lie about `homeServerId` in the invite; the worst case is a wrong "also watches you" panel, never extra access. No manual linking.

**`GET Shoal/Admin/Status`** gains `friends[]`: `{friendHandle, name, watch: {peerHandle, status, titles, activeStreams}|null, share: {shareHandle, status, libraryCount, deviceCount, unexpectedDeviceCount, playingNow, transcodingNow, lastSeenUtc}|null, linked}`. `friendHandle` is a stable local handle (hash of the two record keys). `name` is the peer display name when linked. Roll-up status for a linked card is the worst of the two, in this order: `AuthFailed`, `IdentityMismatch`, `TlsKeyChanged`, `PairingAborted`, `Offline`, `Degraded`, `Paused`, `Online`. Unlinked records keep the v1 shapes and screens.

**Screens (data; Lumen decides layout).**
- *Overview.* One friend card per linked friend with two rows, "You watch {name}" (status, titles, streams now) and "{name} watches you" (status, devices, playing now), and the rolled-up status chip. Unlinked friends are unchanged. "Friends watching from you" stays at the bottom (D6) and lists every share; a linked row carries a link "Also your server: {name}".
- *Server detail* (watch direction). New panel "{name} also watches your libraries" when linked: share status, library count, devices (and unexpected ones), last seen, link to Friend detail. Pause and Remove here affect **watching only**; the dialogs say "{name} will still watch your libraries".
- *Friend detail* (share direction). New panel "You also watch {name}" with the peer status and title count and a link. Revoke says "You will still watch {name}'s libraries".
- *Share wizard.* Library list rows carry `shareable` and `reason`; libraries that come from friends are `shareable:false, reason:"federated"` and read "Libraries from friends can't be shared again" (the StubRoot refusal, W2§N-2, now explained instead of failing). Step 1 offers existing watch peers as a prefill for the friend name.
- *Watch wizard.* `Peers/Probe` returns `alsoWatchesYou: true` when the probed server-id prefix matches a share; the pairing screen then says "You already share with {name}; this adds the other direction".
- Share users stay hidden in every picker (`isShareUser`). Echo is impossible by construction (stubs are never shareable, dedup is local-wins, 5.3).

**Tests.** T-TWO-1 unit: `AdminStatusBuilder` over fixtures (linked, share only, peer only, prefix collision, legacy share without the field). T-TWO-2 roll-up order table. T-TWO-3 lab: scripted Home redeems an invite on lab-server using lab-server's **real** server id while lab-server is also a QC-paired peer of lab-server: `friends[]` has one linked entry. T-TWO-4 a Shoal library offered in the share wizard is `shareable:false`; `POST Shares` with it answers 400 as today. Pages: ui-spec F-checklist, `textContent` only for every peer string.

### v2.6.3 (c) Real-device matrix (Pilot and owner checklist)

**Scope.** D14: only iOS has passed (T-CLI-17, ecf65d5). Every other platform stays "should work" in the 6.12 text until it passes S1 to S9 below on a real device (W4-15). A platform nobody can test is not a blocker for release; it just never moves to "Works" (D11 is satisfied by an honest list; owner confirms).

**Setup (Pilot).** The Pilot 04 round 3 lab state or newer: lab-server with the release build, lab-server, library "lab-server Movies" (Sintel, Tears of Steel), a lab user per tester with access, Debug logging on both servers, a clean counter baseline. **Counters:** `GET Shoal/Admin/Status` gains `intercept {sinceUtc, counters[]}` (K24 splits the old `bypass-route` into `nonstatic-stream` and `audio-route` and adds `trickplay-m3u8` and `no-credentials`; K27 shows them under Settings, Troubleshooting). Memory only, reset at restart; Pilot reads them before and after each device and records the delta. No URLs, ids or user agents in them.

**Who does what.** Owner (or a friend) holds the device and performs the step; Pilot reads Home log lines (route template and status only), Peer log, counters and the Peer's ffmpeg list. Devices away from the owner's home (a friend's TV) run the same steps against that friend's real paired install and the friend sends back the row.

| Step | Action | Expected (Home log, Peer log, counters) |
|---|---|---|
| S1 | Sign in, open "lab-server Movies" | list, posters and overviews appear; no peer call per browse |
| S2 | Play Sintel, direct | `Videos/{id}/stream` Static=true, 206, `Content-Type` video/x-matroska; no stub text |
| S3 | Seek far forward and back | Range requests, 206 each; playback resumes |
| S4 | Pause 2 minutes, resume | continues; no new grant error |
| S5 | Pick a text subtitle | shows; record whether the app used `DeliveryUrl` or built the route itself |
| S6 | Pick a bitmap (PGS) subtitle | in-band or burned in; any `.sup` request is a 404 and counted, never a crash |
| S7 | Switch audio track | continues; at most one new PlaybackInfo |
| S8 | Quality 720p at about 1 Mbps (forces conversion) | HLS through `/Shoal/Hls/...`; `libx264` in the Peer log; no ffmpeg on Home; seek inside HLS works |
| S9 | Kill the app mid-play, wait 70 s, play again | Peer job gone within about 65 s; new play works |
| S10 | After K29: pick the second version | plays that source (Peer log shows its media source) |
| S11 | After K30: drag the seek bar | thumbnails show; tile requests answer 200 |
| S12 | After K31: an ASS title with embedded fonts | uses the file's fonts (compare with the fallback look) |
| S13 | Counters after S1 to S9 | `nonstatic-stream` 0, `audio-route` 0, `core-hls-route` 0, `no-credentials` 0 |

**Pass rule.** S1 to S9 and S13 pass: the platform may be listed as Works. S10 to S12 are recorded per feature and do not gate the list. **Reopen rule for (h):** any platform with `nonstatic-stream` above 0 during S1 to S9 reopens the progressive proxy for that platform.

**Platforms and what to watch** (from SC§2 and 6.12; tiers are about who can test, not priority):

| Tier | Platform | Watch for |
|---|---|---|
| done | iOS, iPadOS (Moonfin) | repeat only on the release build (11.5 item 8) |
| B | Android phone or tablet (Moonfin) | demuxer chosen from `Container` (comma lists); header plus `ApiKey` |
| B | Android TV, Fire TV (Moonfin) | same player stack as Android; D-pad subtitle menu; Fire OS WebView limits |
| B | Apple TV, tvOS (Moonfin) | AVPlayer is strict about HLS (playlist grammar, codecs strings); S8 matters most |
| B | Windows, macOS, Linux desktop (Moonfin) | mpv or libVLC path; header-only requests |
| B | Moonfin web via Moonbase `/Moonfin/Web/` | hls.js; query-string token only |
| C | Samsung Tizen, LG webOS, Xbox, Vega (Moonfin Smart-TV) | `stream.{Container}?Static=true&ApiKey`; `Container` must be present; `js`/`vtt`/`ass` subtitle files; `upmix=true` ignored; ASS fonts (S12) |
| C | Roku (Moonfin) | header-only auth (no `ApiKey`); `Container` query |
| C | Jellyfin Media Player, official Android app (non-Moonfin, supported in 6.12) | static URL with `ApiKey`; built-in player only |

**Result record.** Pilot adds one row per platform to `docs/tests/device-matrix.md`: device, OS and app versions, server build, S1 to S13 result, counter deltas, quirks, date. Its Works rows (and only those) are copied into the 6.12 help text and the README.

### v2.6.4 (d) Alternate versions (several media sources per title)

**Mechanism.** Jellyfin groups video files in one movie folder into versions when each file name starts with the folder name character for character, followed by a label after a hyphen, dot, underscore or square brackets (Jellyfin docs, Movies, "Multiple versions"). Shoal writes one `.strm` per peer media source in the existing title folder: `<stem>.strm` (the primary, unchanged, same stubKey) and `<stem> - <label>.strm` for each extra version, each with its own stubKey and sentinel. Core then shows one movie with a version picker, and every version is a normal stub item. **Lab gate T-ALT-0 first** (Pilot, no Shoal code, 30 minutes on lab-server): make `Test (2020)/Test (2020).strm` and `Test (2020) - 720p.strm`, scan, and check that (1) `/Items` lists one movie, (2) its `MediaSources` has two entries whose `Id`s are the two files' item ids, (3) `PlaybackInfo` with the second `MediaSourceId` is not rejected by core, (4) jellyfin-web's version picker lists both. If any check fails, K28 and K29 are cancelled and the feature is dropped; report to the owner.

**Not built (owner decision, recommended): merging a better peer copy into a local item.** 5.3's "phase 6" line proposed it. It would put a stub under a local item's version list, which is exactly the merged-version hazard of 6.1 (core can serve the sentinel under the local id) and breaks "local always wins". The overlap report keeps its informational "peer copy better" count.

**Data.**
- Catalog schema 3 (K24): `entries[].sources[]` of `{stubKey, peerMediaSourceId, label, container, videoCodec, width, height, bitrate, sizeBytes, runtimeTicks, stubRelPath, trickplay[]}`. A schema-2 entry reads as one source holding its existing `stubKey`, `peerMediaSourceId` and `stubRelPath`; nothing on disk is renamed. `entries[].stubKey` stays as the primary's key. Newer schema: recovery, as always.
- Limits: at most **4** sources per title (the first 64 peer sources are examined). Selection: keep the primary; fill free slots by height, then bitrate, then size; two sources with the same width, height, codec and size collapse. The 50,000 entry cap now counts sources.
- `label`: built by Home from validated numbers only: `2160p`, `1080p`, `720p`, `480p` or `SD` (from height); on a clash append the codec (`hevc`), then ` 2`, ` 3`. Charset `[A-Za-z0-9 ]`. The file name follows `PathSanitizer` rules (5.5) with the label after ` - `.
- `ShoalIndex` (K28): local item id to `VersionSet {setId (primary stubKey), members[]}` and to the stub record. `IVersionResolver.Resolve(routeItemId, requestedMediaSourceId?)` returns `{versionItemId, stubKey, peerKey, peerItemId, peerMediaSourceId, setId}`: no request value means the route item's own stub; a request value must parse (`Guid.TryParse`, 6.1 rules), be federated and be in the **same set** as the route item, else `404` with zero Peer calls. This replaces the SR§4.2 rule "differing federated mediaSourceId: 404" by "different set: 404" and 6.2 step 5's "other than the local item id: 400" by "other than a member of the set: 400".
- Sync (K28): per title, the details call already carries `MediaSources` (5.1). Add, update or delete **one stub per source**; a changed Etag re-evaluates sources. Removing an extra version deletes only that `.strm`. If the source bound to the primary disappears while others remain, the primary stub is **rebound in place** to the best remaining source and the file of that source is deleted, so the primary's path, stubKey and watch state never change. Mass-deletion and new-item guards count **titles**, not stubs. Dedup (`duplicate-local`, `duplicate-peer`) acts on the whole title. Images and NFO exist once, for the primary.
- `StubEnricher` enriches each stub item from its own source; its merged-version warning ignores sets equal to a catalog set and still warns for any link to a non-Shoal item.
- Playback (K29): `Grants` record `versionItemId` and `localMediaSourceId`; the grant key stays (user, route item, DeviceId). **Slots are keyed by `setId`**, so alternating versions cannot take extra slots. PlaybackInfo returns exactly one source, the resolved version, with `Id` = the version's local item id (N), as 6.2.3 does today. Static, subtitle (6.6a step 2: `{ms}` is a set member), HLS, trickplay and attachment routes resolve the version through `IVersionResolver`; CallerContext (8.5) runs for the route item **and** the version item (same library, so a cheap repeat). The canary (6.2 step 7) covers the peer ids of **all** versions of the set.
- Switch: `AlternateVersionsEnabled` (XML, **default off until T-ALT-0 has passed**, owner/manager decision at K28; then the sync is exactly v1). Off: sync writes the primary only and removes extra stubs through the normal removal path. Turning it on re-evaluates every title once (the catalog flag `alternateVersionsApplied`), so extras appear without waiting for an etag change.

**Security rules.** Peer-supplied ids and numbers are validated per 5.5; labels never come from peer text; a hostile peer can only fill its own four slots; no new Peer route is called; version ids are local; a cross-set `mediaSourceId` is a 404 before any Peer call (T-SEC-15 cell added). **Accepted (Warden 10 L6):** slots are keyed by (user, version set), so one user can run up to `MaxStreamsPerUser` (4) streams of different versions of one title on a single slot, at most a fourfold bandwidth multiplier on that title; the per-user connection cap (6) and the peer limits still apply. Counting connections per peer would close it and is not built.

**Failure modes.** Core does not group (T-ALT-0 fails): feature cancelled. Peer reorders sources: rebinding rule above. A title shrinks from 3 versions to 1: extra stubs removed, the library scan removes the items (known lag, Pilot 04 4.1 finding 2). A client that sends the primary id with an alternate `MediaSourceId` (jellyfin-web does): handled by the resolver. A client that ignores `Id` and re-requests the primary: plays the primary.

**Tests.** T-ALT-0 lab gate. T-ALT-1 migration (schema 2 to 3, round trip, no file renamed). T-ALT-2 source selector: 1, 4, 5 and 64 sources, ties, collisions, a hostile label. T-ALT-3 sync: add a version, drop one, drop the primary's source (rebind in place, stubKey and path unchanged), flip the switch. T-ALT-4 guards count titles. T-ALT-5 resolver: own source, same set, foreign set, unparsable, B/P/X forms. T-ALT-6 playback: PlaybackInfo for each version forwards the right `peerMediaSourceId` (T-SEC-20), static Range per version, subtitle per version, one slot for two versions. T-ALT-7 T-SEC-15 matrix gains "cross-set mediaSourceId": 404, zero fakepeer requests. T-ALT-8 lab: web picker plays both versions of a two-version peer title; Moonfin iOS picks the second (S10).

### v2.6.5 (e) Trickplay thumbnails

**Data.** The Peer's `BaseItemDto.Trickplay` (media source id to width to `TrickplayInfo`) is read at sync from the details call (Kiro confirms the `Fields` name on 12.1 and that a stock Peer returns it to a share user; lab gate T-TRK-0 also confirms that core keeps a `TrickplayInfo` row saved by Shoal through a scan with extraction disabled). Stored per source: `trickplay[]` (at most 4 widths) of `{width, height, tileWidth, tileHeight, thumbnailCount, intervalMs, bandwidth}`. Ranges: width and height 1..1024, tile counts 1..32, `thumbnailCount` 1..100,000, `intervalMs` 1..3,600,000, `bandwidth` 0..100,000,000; one tile image (`width*tileWidth` by `height*tileHeight`) at most 16 MP and 8192 on a side; an entry that fails is dropped. A new `TrickplayInfoWriter` (`ILibraryPostScanTask`, own file) saves or deletes the local `TrickplayInfo` for each stub item so core advertises it (`BaseItemDto.Trickplay`). Locked options add `SaveTrickplayWithMedia=false` (5.6 already disables extraction).

**Serving** (K30). The 6.1 row for `Trickplay/...` splits: `GET Videos/{itemId}/Trickplay/{width}/{index}.jpg` goes to `ITrickplayHandler`; `.../tiles.m3u8` stays 404 and counts `trickplay-m3u8`.
1. CallerContext (8.5) for the route item; the `mediaSourceId` query is resolved by `IVersionResolver` (v2.6.4); credentials as for static (none: 401).
2. `width` must be one of the source's stored widths; `index` an integer in `0..ceil(thumbnailCount/(tileWidth*tileHeight))-1`; else 404 with no Peer call.
3. Cache hit: serve from disk. This works while the Peer is offline (the data is Home's own once cached).
4. Miss: peer state as static (closed circuit, trust window, else 503 with no body); coalesce identical keys (one Peer call, many waiters); per-peer gate of 4 calls with 32 waiters (429 beyond); `GET /Videos/{peerItemId}/Trickplay/{width}/{index}.jpg?mediaSourceId={peerMsId}` with a new `PeerCallProfile.Trickplay` (connect 5 s, headers 15 s, idle 10 s, total 30 s, 2 MB; connect failures count toward the breaker, timeouts and 5xx do not, like subtitles).
5. Validate: 200 only, upstream type `image/jpeg`, JPEG magic, dimensions by the 5.4 marker walk (`ImageProbe`) with width at most `tileWidth*width` and height at most `tileHeight*height`, nonzero; then the optional `IImageSanitizer` of v2.6.7 on a miss. Fail: 502, nothing cached.
6. Write temp then rename to `<DataPath>/shoal/trickplay/<peerKey>/<stubKey>/<width>_<index>.jpg` (0700/0600; names hold only Home's random ids). Respond `200 image/jpeg` through `ResponseWriter` (`nosniff`, `no-store`, CSP sandbox, `Content-Disposition: attachment` as core's own tile route).
- Rates: per user 40 per second, burst 100 (scrubbing is bursty), separate from static. Cache cap `TrickplayCacheGb` (XML, default 1, 1..20) over the whole folder; after each write, if above the cap delete least recently used files until at 90 percent; a hit refreshes the access time at most once per minute. The cache is not part of the 5.10 per-peer quota. Removing a stub, a library or a peer deletes its folder; startup walks the folder once (capped at 200,000 files, then stops and warns).
- Switch `TrickplayEnabled` (XML, default true): off, tile routes answer 404 and the writer removes `TrickplayInfo`.

**Security rules.** No token or peer id in any file name or log; the cache holds only validated JPEGs; every request runs CallerContext before the cache is touched; `index` and `width` are bounded integers, so the path cannot escape; hostile bytes are bounded by `BoundedReadStream` and rejected without decoding beyond the header walk (and re-encoded when v2.6.7 is on); a hostile peer can fill at most the global cap, and only for titles a user actually scrubs.

**Failure modes.** Peer has no trickplay: no `TrickplayInfo`, the client shows none. Peer regenerates tiles with new dimensions: the next sync updates the metadata and the stub's folder is purged when the signature changes. Core drops its saved rows on a scan: T-TRK-0 decides; the fallback is to re-save after each scan (the writer already runs post-scan).

**Tests.** T-TRK-0 lab gate. T-TRK-1 metadata sanitizer table (every range edge, 5 widths, hostile values). T-TRK-2 handler matrix: unknown width, index at N and N-1, negative, huge, repeated query keys, no credentials, API key, other user's access (R9 to R12 cells with `Trickplay`). T-TRK-3 fakepeer returns HTML, PNG, 5 MB, a JPEG with an 8000 by 8000 SOF behind an APP1 thumbnail, a slow trickle: all rejected, no cache file. T-TRK-4 coalescing and gate (50 parallel identical requests: one Peer call; 40 distinct: at most 4 in flight). T-TRK-5 cache cap and LRU (cap 5 MB, write 20 tiles), stub removal clears the folder. T-TRK-6 offline: cached tile served with the circuit open, uncached 503. T-TRK-7 scrub in jellyfin-web and Moonfin iOS shows thumbnails (S11). T-TRK-8 `tiles.m3u8` 404 and counted.

### v2.6.6 (f) Attachments and fonts (ASS/SSA)

**Current state.** `MediaAttachments=[]` (6.2.3) and `Attachments/{index}` is a 404 (6.1), so ASS styling renders with fallback fonts on Moonfin Smart-TV and jellyfin-web (6.12).

**PlaybackInfo (K31).** The Peer's list is rebuilt, never copied: keep an entry only if `Index` is 0..127 and (its `MimeType` is one of `font/ttf`, `font/otf`, `font/sfnt`, `font/collection`, `font/woff`, `font/woff2`, `application/x-truetype-font`, `application/x-font-ttf`, `application/vnd.ms-opentype`, `application/font-sfnt`, or its `FileName` extension is `ttf`, `otf`, `ttc`, `woff` or `woff2`); at most 32 kept; covers, images and everything else dropped. Output per entry: `Index`; `FileName` = base name matching `^[A-Za-z0-9 ._()-]{1,64}$` else `font<index>`; `MimeType` from the extension through Home's closed table; `Codec` null; `DeliveryUrl` = `/Videos/{routeItemId N}/{localMediaSourceId N}/Attachments/{index}?ApiKey=<caller token>` (the caller's own token, echoed like subtitle `DeliveryUrl`s, 6.6; jellyfin-web's ASS renderer fetches fonts without headers). The kept indexes go into the grant. The canary (6.2 step 7) runs over the result. `AttachmentsEnabled` (XML, default true) off gives `[]` as today and the route stays 404.

**Route (K31).** `GET Videos/{videoId}/{mediaSourceId}/Attachments/{index}` goes to `IAttachmentHandler`:
1. Credentials required (none: 401); CallerContext for the route item; `{mediaSourceId}` parsed and resolved through the version set (v2.6.4); newest live grant of (caller, item) as in 6.6a step 4 (no grant: 404); `index` must be in that grant's kept set (else 404, zero Peer calls).
2. Peer call `GET /Videos/{pid}/{pms}/Attachments/{index}` through the same **item lane and per-peer gate** as subtitle files (`PeerExtractionGate`, extracted in K24; 6.6a step 7): the Peer's attachment extractor runs ffmpeg over the whole source on first use, exactly like subtitle extraction, and a client asks for many fonts at once; they join one lane. New `PeerCallProfile.AttachmentFile` (connect 5 s, headers 120 s, idle 10 s, total 150 s including waits, 16 MB per font), not counted toward the breaker (like `SubtitleFile`).
3. Validate: 200, size at most 16 MB, magic one of `00 01 00 00`, `true`, `OTTO`, `ttcf`, `wOFF`, `wOF2`. Home emits its own type from the magic (`font/ttf`, `font/otf`, `font/collection`, `font/woff`, `font/woff2`), `nosniff`, `no-store`, CSP sandbox, `attachment`. The upstream type is never used. A grant serves at most 128 MB of fonts in total.
4. Rate: 60 per minute per user (own bucket); no caching in v2.6 (a font is fetched again on each play; accepted).
- Home never parses a font.

**Security rules and residual (Warden).** Auth on the route as on every federated route; hostile-peer input is bounded (count, index, name, size, magic, total) and emitted under Home's own type. **Residual, stated plainly:** the viewer's font engine (FreeType or the platform's) parses bytes chosen by the friend's server. That is the same class as the video and subtitle bytes a friend's server already sends (W2§N-14), but it is a new binary parser on Home's clients. It is the reason for the default-on kill switch and for S12. PGS stays refused for its own reason (6.6a step 10, which has an in-band alternative); fonts have none.

**Tests.** T-ATT-1 rebuild table (cover image dropped, 40 fonts capped at 32, hostile `FileName`, wrong MIME with a right extension, `DeliveryUrl` pointing at a peer host: never copied). T-ATT-2 route matrix: no credentials, API key, unknown index, index not in the grant, foreign set, switch off: 401, 403, 404 with zero fakepeer requests. T-ATT-3 fakepeer returns HTML, an SVG, a 17 MB file, a font with a bad magic, a trickle: rejected. T-ATT-4 lane and gate: 20 parallel font requests make one Peer call (the T-CLI-16 harness reused). T-ATT-5 breaker unaffected by slow extraction. T-ATT-6 lab: jellyfin-web plays an ASS title with embedded fonts and the font requests answer 200 (S12). T-ATT-7 log grep: no `ApiKey` value in Shoal logs.

### v2.6.7 (g) Image re-encode in a resource-limited child process

**What it buys, honestly.** Today Home accepts a peer JPEG or PNG after a header walk (5.4) and core decodes it. With the child process, the peer's bytes are decoded by a separate process under limits, and what lands on disk is a fresh baseline JPEG written by an encoder, with metadata stripped. It contains a decoder crash or a decompression bomb and keeps the decoder out of the Jellyfin process (whose memory holds peer tokens). It is **not** a privilege sandbox: the child runs as the same user and could read `master.key` if a decoder bug were exploited; privilege separation (bubblewrap, seccomp) is not portable and is out of scope. It is the lowest-value item of phase 6 and may be cut without affecting the rest.

**Design (K32).**
- `IImageSanitizer.SanitizeAsync(input, ImageSpec{Kind, MaxWidth?, MaxHeight?}, ct)` returns the new bytes or a failure. A pass-through implementation is registered by K24; `ChildImageSanitizer` replaces it when available. `ImageFetcher` calls it after the header checks of 5.4 (which stay as a cheap first filter) and before the quota check; trickplay misses call it with no resize (v2.6.5).
- Process: the host's ffmpeg (`IMediaEncoder.EncoderPath`; no new binary). Arguments built as a list, never a shell string: `-nostdin -hide_banner -loglevel error -threads 1 -protocol_whitelist pipe -f image2pipe -c:v <mjpeg|png, chosen from the magic> -i pipe:0 -frames:v 1 -an -sn -dn -map_metadata -1 -vf [scale='min(<maxW>,iw)':-2:flags=lanczos,]format=yuvj420p -c:v mjpeg -q:v 3 -f image2pipe pipe:1`. The poster and backdrop widths are the sync request sizes (5.1, art quality). Input on stdin, output on stdout, stderr drained to 4 KB and discarded (only the exit code is logged).
- Limits: empty environment, a fresh empty working directory (0700, removed after), wall clock 8 s then kill of the whole tree, output read through `BoundedReadStream` at 4 MB, one child at a time per lane (sync images and the self-test, and trickplay tiles, Warden 10 L3), below-normal priority. Linux adds `prlimit --as=805306368 --cpu=6 --fsize=0 --core=0 --nofile=32 --` in front when `prlimit` is on the PATH. **Calibrated (Pilot round 7):** peak RSS was 422 MB with a 1 GiB limit, and 512 MiB was enough for images up to 64 MP (the plugin accepts at most 16 MP); the shipped value is 768 MiB for margin. Other platforms get the wall clock, output cap and priority only, and Settings reads "Limited: time and size only". No `RLIMIT_NPROC` (it is per user and would break the host).
- Output check: JPEG magic, dimensions within the spec and 16 MP, parsed by the 5.4 walk. Failure of any kind (exit code, timeout, bad output): that image is rejected (placeholder art, counter `imageSandboxRejected`), exactly as a failed 5.4 check is today.
- Availability: at startup and when the switch turns on, a self-test re-encodes a built-in 64 by 64 JPEG. Fail: effective state `unavailable` (reason shown), images continue on the 5.4 path, an alert says so. The switch `ReencodeImages` (XML, default true); Status gains `imageSandbox {state: active|unavailable|off, limits: full|partial, rejected}`.
- Cost: about 50 to 100 ms per image; a first sync of 5,000 titles adds roughly 10 to 15 minutes of background CPU at low priority (images are already serial). Pilot measures it (T-IMG-6).

**Security rules.** Argument lists only, no peer-derived string in any argument (the codec and sizes come from Home's own magic check and settings); no network inside the child is **enforced** (only `-protocol_whitelist pipe`); nothing is written by the child (`--fsize=0` where available); the child's output is revalidated, never trusted; temp directory removed in `finally`; the code path never logs image bytes or stderr.

**Tests.** T-IMG-1 self-test pass and fail (ffmpeg missing: `unavailable`, sync unchanged). T-IMG-2 hostile inputs: a decompression bomb inside the header limits, a truncated JPEG, an APP1 thumbnail trick, a PNG with huge ancillary chunks, a polyglot HTML: rejected or re-encoded, memory of the child stays under the limit, the plugin process memory flat. T-IMG-3 limits fire: a stalled child is killed at 8 s, an output over 4 MB is cut, the tree is gone. T-IMG-4 metadata (EXIF, ICC, comment) absent from the output. T-IMG-5 a sync of the Pilot library produces images core serves unchanged in the UI. T-IMG-6 cost and serial behavior. T-IMG-7 no ffmpeg process is left after plugin shutdown.

### v2.6.8 (h) Progressive (non-static) stream proxy: dropped

Pilot 04 holds **no reading of the progressive counter**: `InterceptCounters.Bypass` (`bypass-route`) exists but is memory-only and no admin route returns it, so no round could have read it (K24 and K27 fix this for the device matrix). What Pilot 04 and the owner do show: T-CLI-5k and -5l (`Static=false` stream: 404) pass in rounds 3 to 5 with 81 of 81 replay checks; Moonfin's request shapes all follow `TranscodingUrl` for conversion (T-CLI-3); the owner's iPhone retest passed direct play, forced conversion, subtitles and audio switching with nothing on a progressive path. Scout-02 lists a non-static `stream` only for Moonfin's home-row preview, which is off by default and already unavailable for Shoal titles. A progressive proxy would add an unbounded-duration, transcode-on-demand stream path with its own slot, deadline and cut-off rules for no observed client. **Not built; struck from 11.1.** Reopen only by the device-matrix rule in v2.6.3 (a platform with `nonstatic-stream` above 0 in S1 to S9), with a fresh design round and Warden review.

### v2.6.9 Shared files, lanes and run order

K24 owns every shared file (schema, settings, route table, registration, call profiles, content-type table, counters) and lands first with no behavior change, so later tasks add new files and almost never edit one another's.

| Shared file | Who edits it | Rule |
|---|---|---|
| `State/StateTypes.cs`, `StateValidator.cs`, `ShoalState.cs` | K24 only | adds `share.homeServerIdPrefix`, `lastRotatedUtc`, `rotationCount`, `rotationFailedUtc`; `peer.rotationDays`, `lastRotatedUtc` (additive, schema stays 2) |
| `Sync/CatalogTypes.cs`, `SanitizedTypes.cs`, `CatalogStore.cs` | K24 only | catalog schema 3 (`sources[]`, `trickplay[]`), migration, hooks `ISourceSanitizer` and `ITrickplaySanitizer` called from `MetadataSanitizer`; implementations in new files |
| `Configuration/PluginConfiguration.cs`, `SettingsValidator.cs` | K24 only | `AlternateVersionsEnabled`, `TrickplayEnabled`, `TrickplayCacheGb`, `AttachmentsEnabled`, `ReencodeImages` with clamps |
| `PluginServiceRegistrator.cs` | K24 only | one call per feature to `Registrations/<Feature>.cs` (a new file per task) |
| `Playback/InterceptTable.cs`, `ShoalInterceptFilter.cs`, `InterceptHandlers.cs` | K24 only | rows and seams `ITrickplayHandler`, `IAttachmentHandler`, `IVersionResolver`; default handlers answer 404; counters split |
| `Peers/PeerCallProfile.cs`, `Http/ResponseWriter.cs` | K24 only | profiles `Rotate`, `Trickplay`, `AttachmentFile`; content types `image/jpeg` tile and the font table |
| `Playback/SubtitleHandler.cs` | K24 (extract `PeerExtractionGate`, no behavior change), then K29 (version resolution) | K31 only uses the gate |
| `Playback/PlaybackInfoBuilder.cs`, `PlaybackInfoHandler.cs` | K24 (seam `IAttachmentRebuilder`, returns `[]`), then K29 (source choice) | K31 implements the seam in a new file |
| `Playback/StaticStreamHandler.cs`, `Grants.cs`, `StreamSlots.cs` | K29 only | |
| `Sync/CatalogSyncService.cs`, `StubWriter.cs`, `PathSanitizer.cs`, `StubEnricher.cs`, `Authorization/ShoalIndex.cs` | K28 only | |
| `Sync/ImageFetcher.cs` | K32 only | |
| `Sharing/ShareService.cs` | K25 only | |
| `Api/Admin/AdminStatusBuilder.cs`, `AdminController.cs`, `AdminSharesController.cs`, `AdminSettingsController.cs`, `Configuration/shoal.html`, `shoal.js`, `shoal.css` | K27, then K33 | one lane (Kiro 2), in that order; K26 adds its endpoint in the new `Api/Admin/AdminRotateController.cs` |
| Test fakes (`FakeJellyfinPeer.cs`, `PeerServerFixture.cs`) | K24 adds extension points; each task adds its endpoints in its own new file | |

**Lanes.** A = Kiro (backend, security paths). B = Kiro 2 (admin layer, self-contained serving modules). The owner's rule is two agents at a time, so:
1. **Wave 0:** K24 (A) with Pilot: T-ALT-0, T-TRK-0 and the P-HTTPS lab setup (no Shoal code).
2. **Wave 1:** A: K25 then K26. B: K32 then K27 (K27 builds against K24's DTO shapes and mocks, then K26's endpoint). **Warden batch A** (rotation: K25, K26) when A is done.
3. **Wave 2** (only if T-ALT-0 passed): A: K28 then K29. B: K30 then K31. If T-ALT-0 failed, A takes nothing and B proceeds.
4. **Wave 3:** B: K33. **Warden batch B** (K29, K30, K31, K32). Pilot full run of the v2.6 tests, then the device matrix with the owner.

K30 and K31 do not need K28 or K29 for their own logic: until the version resolver is real, `IVersionResolver` returns the route item's own source, which is the v1 behavior.

### v2.6.10 Open after v2.6

Owner: (1) which real devices exist for the matrix (v2.6.3) and whether unreachable platforms may simply stay "should work"; (2) confirm that a better peer copy is never merged into a local item (v2.6.4). Defaults if unanswered: only iOS is listed as Works; no merge. Pilot: P-HTTPS lab front so a real invite pairing exists. Risks Warden should weigh: fonts add a viewer-side parser (v2.6.6); the image child is not a privilege sandbox (v2.6.7); rotation can strand a pairing on a lost response (v2.6.1 F1 to F3); trickplay metadata and the version grouping both depend on lab-gated core behavior (T-TRK-0, T-ALT-0).

---

## Changes in v2.4 (Warden-06, Pilot-03, Scout-03, owner iOS test)

Nothing here reopens the design. Warden-06 reviewed the committed code of K3-K14; all of its findings are code fixes Warden tracks, except **L-5** (uninstall), which needed a rule and gets one below. Pilot-03 showed that several Home users stream through one share token exactly as designed, and that the friend's **CPU**, not the token or the session limit, is what bounds simultaneous conversions; v2.4 adds a per-peer conversion limit for that. Scout-03 found the route table correct and two gaps in the 5.6 locked options. The owner's iPhone test passed on the spike, and its repeat on the MVP build becomes a release gate. Owner **D13** (4 streams per account) and **D14** (an iOS device for testing) are applied. Section numbers and K-task ids are unchanged; new material: 3.2a `maxConcurrentTranscodes` on Home's peer records, one 6.9 row, the pairing phase value `afterApproval`, the alert kind `catalogUnreadable`, and test ids **T-CONC-1..3**, **T-VIS-8**, **T-SYNC-13**, **T-CLI-17**.

### What each task now carries

| Task | v2.4 additions |
|---|---|
| K5 | W6 L-1 is still open: rule (b) of `likelySessionLimit` and the Start-time account reading, before the Pilot lab run. The abort now stores `phase: afterApproval` (rule a) or `beforeApproval` (rule b) so the UI can word the two cases differently (9.5) |
| K10 | 5.6 locked options: `TypeOptions` for `Movie`, `Video`, `Trailer`, `BoxSet`; `DisabledMediaSegmentProviders`; both "every provider" lists recomputed at startup and after each sync. Uninstall: "narrow, never widen" (W6 L-5, **before release**). A share user with "all libraries" is narrowed, not just logged (W6 L-6). **T-VIS-8**, **T-SYNC-13** |
| K13 | `ReadsStub` on every intercepted byte route (SR§4.1); a route-coverage test over the **real MVC action descriptors** of the targeted Jellyfin.Api, never swagger (SR§0, §6.2); the spike controller and resolver deleted (W6 L-7, Warden's condition) |
| K14 | Committed; no change. KQ 15 (errors as 200 + `ErrorCode`) and KQ 16 (no gate on the `Offline` display status) are confirmed as the design, below |
| K15 | Slot ledger with two kinds (stream, conversion); per-peer conversion limit, default 4 (D15); the PlaybackInfo pre-checks of 6.2; the `mediaSourceId` rule of SR§4.2; **T-CONC-3** (a fifth stream gets 429 while four keep playing). W6 M-2 (the analyzer bans) lands before this PR writes responses |
| K17 | The first forwarded segment takes a stream slot and a conversion slot together; they are released together |
| K19 | Scoped stop confirmed: `Sessions/Playing/Stopped`, then for a started conversion the scoped `ActiveEncodings` delete Pilot measured; **T-CONC-2** |
| U5 | "Conversions at once" on Server detail; the 9.5 v2.4 names (policy words, `sessionLimit` alert, `catalogUnreadable`) |
| U9 (phase 4) | Conversions running at the time on every playback sample; the slow-conversion hint names the setting; the deep test suggests a number |

### Warden-06 L-5 and L-6: uninstall and sharing accounts

| ID | v2.4 resolution | Where |
|---|---|---|
| W6 L-5 | **Decided: narrow, never widen.** On uninstall the Shoal libraries stay in Jellyfin with nothing guarding them, so handing "all libraries" back to an account that was converted *because* it is excluded from one of them would show it the friend's catalog. (1) `DELETE Shoal/Admin/All` ("Remove everything") deletes the Shoal libraries **first**, then restores "all libraries" to managed accounts whose hash still matches; nothing can be exposed once no Shoal library exists. This is the clean uninstall path, and the README and the danger zone say "Remove everything, then uninstall". (2) `OnUninstalling` deletes nothing: an account whose hash matches gets "all libraries" back **only if no Shoal library exists any more**; otherwise it keeps the explicit list Shoal last wrote, so it sees what it saw and nothing more. Accounts whose hash no longer matches are skipped (an admin owns them). One Warning names the accounts left on explicit lists and the fix. Cost, stated in the README: such an account does not see libraries created after the uninstall until an admin ticks "all libraries" | 5.6, K10, T-VIS-8 |
| W6 L-6 | **Adopted.** A share user found with "all libraries" is narrowed by Reconcile: `EnableAllFolders=false`, `EnabledFolders` = every non-Shoal library (what it could see, minus the Shoal libraries). It is not recorded as managed, the change is logged, and K21's drift repair later narrows it to the shared libraries on a Shoal Peer | 5.6 rule 2, K10 |

### Pilot-03: several users through one token

| Finding | v2.4 resolution | Where |
|---|---|---|
| P3§0.1-2: four simultaneous playbacks (1 direct + 3 conversions, and 4 conversions) through one share user, one token, one DeviceId; the Peer keeps **one** session and runs one ffmpeg job per converting stream, keyed by its own `PlaySessionId` | Recorded as **T-CONC-1, PASSED on the spike**. The MVP build repeats it inside T-CONC-3 | 11.3 |
| P3§0.3, §4: with `MaxActiveSessions=1` in force, the four streams were unaffected while a self-mint was refused | Recorded in 4.1 ("What the session limit does"): the limit counts logins, not streams. Step 9 is unchanged | 4.1 |
| P3§0.5, §2-3: one libx264 job (720p source, 1.2 Mbps) takes all 4 cores; four together ran at about 2.2x real time each; RAM is not the limit; direct streams cost the Peer no CPU | **Decided: a separate per-peer conversion limit.** `maxConcurrentTranscodes`, default **4** (owner D15 overrode the proposed 2), range 1..`maxConcurrentStreams`, Server detail "Conversions at once". Every stream through Shoal's HLS proxy holds a conversion slot in addition to its stream slot (Home cannot tell a Peer remux from an encode, so it counts both); a direct stream holds only a stream slot. Streams per peer stay 4 and per account 4 (D13). No "half" rule for conversions: the stream half-rule already caps one account at 2 per peer. Why 2 and not 4: Home cannot see the friend's core count or hardware acceleration on a stock Peer; 2 conversions on Pilot's 4 cores would still run at about 4x real time and leave room for the friend's own viewers and for sources heavier than Sintel | 3.2a, 6.9, 9.3, K15, K17, U5 |
| Where the refusal shows | **PlaybackInfo pre-checks** through K14's `ISlotLedger` seam: `RateLimitExceeded` when the user or the peer is already at its stream limit, or when the Peer's answer offers only a conversion (no direct play) and the conversion slots are full. No Peer job exists at that point. The client gets jellyfin-web's rate-limit message instead of a mid-start 429. The first byte or segment stays the authoritative check (429), because slots are not reserved at PlaybackInfo | 6.2 steps 1 and 6, 6.9, K15 |
| How the CPU ceiling is communicated | README "How many people can watch at once" (11.5 item 4). Diagnostics (phase 4): each playback sample records how many conversions Home was running on that peer; the slow-conversion hint names the setting when two or more were running; the deep transcode test is skipped while any stream from Home is active and suggests a "Conversions at once" value from its measured speed, never applied automatically | 7.1, 7.3, 11.5, U9 |
| P3§6.6: a low bitrate limit turns streams into conversions | 4.1 step 3 and the README: a `RemoteClientBitrateLimit` on the sharing account below the files' bitrates makes every such stream a conversion on the friend's CPU; set it only for the friend's upload | 4.1 step 3, 11.5 |
| P3§0.4, §5: a scoped delete ended exactly one Peer job within 3.5 s; a client that just stops leaves the job at full CPU for about 60 s | **Confirmed for K19.** On `PlaybackStopped` (owner-checked grant, 6.8) Home sends `POST /Sessions/Playing/Stopped` and then, for a `Transcode` grant whose `peerJobStarted` is still set, the scoped `DELETE /Videos/ActiveEncodings` (the mechanism Pilot measured; at most once per job, 6.11), then releases both slots. A client that vanishes without a stop event is covered by the Peer's own 60 s idle kill (accepted) and Home's 2 min slot release. **T-CONC-2**: the stopped stream's job is gone within 5 s, the others keep playing | 6.8, 6.11, K19, 11.3 |
| P3§6.3: the spike has no limit; a fifth stream was never tried | **T-CONC-3**: with defaults, a fifth stream on one peer gets `RateLimitExceeded` at PlaybackInfo (and 429 at the first byte when forced past it) while the four keep playing with zero errors; one account's third stream on one peer and a third conversion are refused the same way | 11.3, K15 |
| P3§6.7: first segment 3-5 s with two or more conversions on a small Peer | README troubleshooting line: a slow start on a busy friend's server is expected, not a fault | 11.5 |
| Owner D13 | `MaxStreamsPerUser` default **4** (was 2), still clamped 1..4; 3.2 and 6.9 corrected. Tests that need 2 (T-PLAY-10, T-SEC-25) set it explicitly | 3.2, 6.9 |

### Scout-03: route table and remote metadata

| Item | v2.4 resolution | Where |
|---|---|---|
| SR§0, §2: all 419 verb+template entries of 12.1 enumerated; the 35 byte-capable item routes are exactly the `InterceptTable` rows; 0 misclassified, 0 leaks; every Moonfin shape hits the intended handler | Recorded. No table change | 6.1 |
| SR§0 trap: swagger hides 55 real routes (every DynamicHls and legacy HLS segment route, `DELETE Videos/ActiveEncodings`) | **Required:** the K13 coverage test enumerates the **real MVC action descriptors** of the targeted Jellyfin.Api (a minimal host with Jellyfin.Api as an application part, `IActionDescriptorCollectionProvider`; if the assembly cannot be hosted in a test, its attribute routes are read with `MetadataLoadContext` and combined the way MVC combines them), never `/api-docs` or an `openapi.json`. It asserts that every template that starts `Videos/{` or `Audio/{`, plus `Items/{x}/File`, `Download`, `PlaybackInfo`, `LiveStreams/Open` and `Videos/ActiveEncodings`, is in the table unless it is on an explicit allow-list of non-byte routes (SR§2: `AdditionalParts`, `AlternateSources`, `Lyrics`, `RemoteSearch/*`, `Subtitles` POST and DELETE, `MergeVersions`). For 12.1 the covered set is the 35 entries, so a new byte route in a later server fails the build | 6.1, K13, 11.5 item 5 |
| SR§4.1 `ReadsStub` | **Adopted.** Every intercepted route except PlaybackInfo, `ActiveEncodings` and `LiveStreams/Open` reads the stub, so all of them get the path-rule fallback on an index miss (it failed closed before; now it is correct) | 3.5, 6.1, K13 |
| SR§4.2 `mediaSourceId` across merged versions | **Adopted** (Scout's optional fix; cheap, removes the case): on the StaticStream and Subtitle rows, a query `mediaSourceId` that parses to a federated id **different from the route id** answers 404 before CallerContext, the W4-3 rule extended | 6.1, K15 |
| SR§4.3 legacy anonymous segment routes | Core's own exposure of local transcodes; nothing federated is ever there. No Shoal change, nothing in Shoal's README | none |
| SR§4.5 write and delete routes on stubs | Accepted as pass-through: they need admin or a user permission and act on the stub folder only; sync is the source of truth. README: deleting a friend's title on Home lasts until the next sync | 6.1, 11.5 |
| SR§4.6 segment providers | **Adopted:** `DisabledMediaSegmentProviders` = every registered provider | 5.6 |
| SR§5 `MetadataFetchers=[]` (KQ 3) | **Settled from source:** an empty list on an **existing** `TypeOptions` entry means "no remote fetcher"; a type with no entry falls back to the server-wide options, where everything is on. **Adopted:** entries with empty `MetadataFetchers` and `ImageFetchers` for `Movie`, `Video`, `Trailer`, `BoxSet`. `EnableInternetProviders` is a dead flag in 12.1: still set, not relied on | 5.6, Appendix A |
| SR§5 Person lookups | **Accepted for MVP and documented.** The NFO's `<actor>` names become Person items, which belong to no library, so the server-wide options apply and TMDb is asked about the friend's cast names; photos and biographies come back. No peer host, id or token leaves. Shoal does not touch server-wide metadata options. A later setting to stop writing `<actor>` is a phase-5 candidate. T-SYNC-13 records it | 5.6, 11.5, T-SYNC-13 |
| SR§2 `/emby` and `/mediabrowser` prefixes | Unverified whether 12.1 still serves them; the filter keys on the action template, so it is prefix-independent if they are. **Pilot** adds the prefixed shapes to T-CLI-6 | T-CLI-6 |

### Owner test: Moonfin for iOS

| Item | v2.4 resolution | Where |
|---|---|---|
| Spike build: play, seek (Peer remux), subtitles and a quality change all PASS on an iPhone | Recorded. It does **not** verify the MVP: the spike returned an absolute `Path` that Moonfin played directly, and G7 removes that form, so on the MVP build Moonfin builds `Videos/{id}/stream?Static=true` itself (K13, K15), follows Shoal's `TranscodingUrl` (K16, K17) and fetches subtitle files (K18) | 6.12 |
| Repeat on the MVP build | **T-CLI-17, a release gate** (11.5 item 8), with the forced re-encode step: quality 720p at about 1 Mbps so lab-server must run `libx264`, and the subtitle route recorded. Its direct-play part runs as soon as K15 lands, as an early signal. "Moonfin for iOS" moves to "Works" (6.12 rule 1, ui-spec 8.3.12) only when it passes | 11.3, 11.4, 11.5 |

### Kiro's questions (`docs/kiro-03-backend.md`)

| KQ | Question or note | Answer |
|---|---|---|
| 1 | Keep `\n` in Overview only | **Yes.** Normalize `\r\n` and lone `\r` to `\n` first (a CR-only text would otherwise lose its paragraphs), collapse three or more `\n` to two, keep the other C0 rules. 5.5's `<>` removal already runs |
| 2 | TMDb/TVDb id `0` rejected; up to 10 digits as `long` | **Yes**, both |
| 3 | Does `MetadataFetchers=[]` mean none? | Settled from source by Scout (above); 5.6 gains entries for four types. T-SYNC-13 confirms in the lab |
| 4 | `DisabledSubtitleFetchers` from `GetSupportedProviders(new Movie())` | **Yes.** The real guard is `SubtitleDownloadLanguages=[]`; the list is the backstop. It and `DisabledMediaSegmentProviders` are recomputed at startup and after each sync, so a provider plugin installed later is covered (5.6). T-SYNC-13 |
| 5 | Confirm gone ids with `GET /Items?Ids=` before removing | **Adopted** into 5.2's meaning of "gone". If the confirm call fails, the run counts as partial: nothing is removed |
| 6 | Completeness tolerance `max(5, 1%)` | **Yes**, since removals are confirmed separately |
| 7 | Release runs a full sync that also imports anything new | **Yes, with one rule:** Release approves exactly the held ids; anything new since the hold goes through the new-items guard as usual |
| 8 | `HeldAddition` held as ids, the enum value unused | Fine. Delete the unused enum value so nobody writes it |
| 9 | Catalog schema 2 additions; an unreadable catalog with no usable `.bak` stops the sync (`catalogUnreadable`) | **Yes**, both. The stop is an admin alert, kind **`catalogUnreadable`** (9.5); the way out in MVP is removing the library and adding it again |
| 10 | Name caps never shrink | Accepted for MVP; a re-measure may come with K20 or later |
| 11 | Cross-peer dedup is eventually consistent | Accepted: a duplicate lives at most one sync interval of the worse-ranked peer |
| 12 | Access set before the library is created | **Yes**, that is the 5.6 intent |
| 13 | Deferred Warden-03 Lows | Tracked by Warden; no design change |
| 14 | Route templates came from DESIGN citations, not 12.1 source | Scout-03 confirmed them against the source; the descriptor-based test (K13, above) keeps them right |
| 15 | PlaybackInfo errors as HTTP 200 with `{"ErrorCode":...}`, PascalCase; step 7 is 403 | **Confirmed**, it is what core does and what clients read. The admin JSON stays camelCase |
| 16 | `PeerStatus.Offline` alone does not block PlaybackInfo | **Confirmed as the design, not a stop-gap.** The gate is the circuit and the trust window (both real signals); `Offline` is a display status derived from the circuit (HealthMonitor's failed calls open it), and before K20 it is only the initial "not measured" value. 6.2 step 1 is reworded |
| 17 | Canary host match case-sensitive; play-session ids under 8 characters not checked | **Accepted.** The canary catches Shoal building a URL with the host, which it does in lower case; a peer writing its own host into a title in capitals discloses nothing Shoal holds. Core's play-session ids are 32 hex, so a short one is already abnormal |
| 18 | Master query validation in K14 is character-level | **Yes**; K17 applies the value rules of 6.4/6.11 (Warden K14-I) |
| 19 | Stream slots are a seam (`ISlotLedger`) | **Yes**; K15 implements it with two kinds (stream, conversion) and the pre-check calls |
| 20 | Unbuilt handlers answer 501 from Shoal | Accepted until MVP-A; the last MVP-A task removes `NotYetImplementedHandlers`, and a reflection test asserts it is gone |
| 21 | `MaxStreamsPerUser` 1..4 default 4; `DiskQuotaGbPerPeer` 1..50 | **Yes** (D13); 3.2 corrected |
| 22 | The test host captures Shoal's log categories only | **Yes**; T-CLI-14 covers the real server |

### Kiro 2's unsettled names (`docs/kiro2-02-ui.md`, round 2): canonical in 9.5

| Mismatch | Canonical (both sides converge on this) |
|---|---|
| Account policy words: backend `contentDeletion`, `contentDownloading`; page `deletion`, `download` | **The backend's**, as in v2.3 for `accountNotes`: they mirror Jellyfin's `EnableContentDeletion` and `EnableContentDownloading` and already ship. The full `policy[]` set: `allLibraries`, `contentDeletion`, `contentDownloading`, `liveTv`, `remoteControl`, `allDevices`. The page renames its two string keys |
| `AccountWarning.sessionLimit` (backend list) vs the page's `sessionLimit` alert | **An alert of its own, never a policy word.** The admin API maps `AccountWarning.SessionLimit` to the alert `{kind:"sessionLimit"}` and leaves it out of `policy[]`; the alert is present exactly when the peer detail reads `sessionLimit: notSet` on a confirmed Quick Connect pairing. The enum may stay as it is internally |
| `likelySessionLimit` copy (says "set it to 0") vs the re-pair case | **Two variants, chosen by `pairingPhase`.** `afterApproval` (rule a): the friend approved, then their server refused to finish; the limit is the likely reason; set it to 0, start again, set it back to 1 afterwards. `beforeApproval` (rule b, re-pairs only): the code ran out; *if* the friend entered it, the limit is probably why, same fix; if they did not get to it, just start again. Lumen words both |
| Alert kinds | Closed list: `heldRemovals`, `heldAdditions`, `authFailed`, `identityMismatch`, `tlsKeyChanged`, `foreignDevice`, `policy`, `sessionLimit`, `mergedVersion`, `diskQuota`, `entityCap`, `entryCap`, `stubFolder`, and **`catalogUnreadable`** (v2.4, KQ 9) |
| `TailscaleSocket` `off` (Kiro 2 open question 1) | **Accepted:** `auto`, `off` or a socket path (3.2) |
| New setting | API and `state.json` `maxConcurrentTranscodes`; peer detail also returns `conversionsNow`. UI words are Lumen's (working label "Conversions at once") |

### Where I differ from the inputs (recorded, not silently changed)

- **Pilot (P3§6.5)** suggested a lower default or a cap at about half the friend's cores. Home cannot see the cores on a stock Peer, so the default is a fixed **2 conversions**, and the deep test (phase 4) suggests a number from a measurement. On a Shoal Peer the friend's own per-share cap (8.4) still applies on top.
- **Warden (L-5)** offered three fixes. I chose fail-closed explicit lists plus "Remove everything first" over deleting libraries inside `OnUninstalling`, which is synchronous, destructive, and would cost a reinstall its stubs and watch state.
- **Scout's optional items** were adopted except the README line for core's legacy segment routes (a core matter, not Shoal's).
- **K19** sends the scoped delete in addition to `Sessions/Playing/Stopped` for a started conversion: core's stop handler should kill the job by itself (6.8), but the delete is the mechanism Pilot actually measured, and it is idempotent and bounded (once per job).

### Open after v2.4

1. **Owner:** the default of **2 conversions per friend's server** (raise to 4 if you would rather let a strong friend's server be used fully by default). Everything else here follows earlier decisions.
2. **Warden:** W6's code items (M-1 and M-2 fix-first; L-1 to L-4, L-7, L-8, K14-L1, K14-L2) are tracked in the review; spot-check the uninstall code (T-VIS-8) and the slot ledger when K15 lands.
3. **Pilot:** T-CONC-2 and -3 with K15/K17/K19; T-SYNC-13; T-VIS-8; the `/emby` cells of T-CLI-6; T-CLI-17 with the owner (direct-play part after K15).
4. **Lumen:** copy for "Conversions at once", the two `likelySessionLimit` variants, the `catalogUnreadable` alert, and the README paragraph on simultaneous viewers.
5. **Kiro 2:** rename `policy_deletion`/`policy_download`; map `sessionLimit` as its own alert; add `catalogUnreadable`; read `pairingPhase: afterApproval`; the conversions setting. Kiro 2's open questions 2-7 (Speed, Shares, Probe and State shapes) are not settled in this patch.
6. Carried: N-3 fixes 1-2 and N-17 (deferred), `.gitignore`, the 11.5 scrub.

---

## Changes in v2.3 (Warden-04)

WARDEN review 04 (`docs/reviews/warden-04-v22.md`) found nothing that reopens the design: four Medium findings, all where v2.2 added waiting and bookkeeping, and eleven Low. It gave K13-K17 a GO with conditions and K18 a **NO-GO until W4-1, W4-3 and W4-7 are written into 6.6a, 6.9 and 6.10**. They now are, so **K18 is GO** on Warden's stated terms (W4-8, W4-9, W4-10 land in its code, and all three are specified below). This patch also folds in Pilot-02 (T-SEC-22 passes on a stock Peer; the session-limit refusal is invisible to Home), owner **D12** (G1 Option C confirmed: Moonfin on every platform plus jellyfin-web is the MVP; the credential-less apps get 401), and Lumen's questions in `docs/ui-spec.md` 14.3. Section numbers and K-task ids are unchanged; new material sits inside existing sections or in suffixed steps (6.6a step 0, 9a), one matrix row (8.7 R16) and new test ids (T-CLI-16, T-SEC-25).

### Verdicts after this patch

| Task | Status | What it now carries |
|---|---|---|
| K13 | **GO** | W4-3 (one id, the route id), W4-12 (query and GUID parsing), W4-14 (banned symbols), the W4-4 `ActiveEncodings` row; T-CLI-6, T-SEC-15 R16, T-SEC-25 |
| K14 | **GO** | W4-2 (DeviceId is client-chosen, 128-char cap, own-device capabilities only), W4-6 (clamp 1..4, safe eviction, check order, `hasSlot`), W4-10 (format allowlist); T-CLI-8 |
| K15 | **GO** | W4-4 (direct play never released by `ActiveEncodings`), W4-6 (`hasSlot`); T-SEC-25 |
| K16 | **GO** | unchanged by v2.2 or v2.3 |
| K17 | **GO** | W4-11 (credential keys are core's 401), W4-4 (HLS release only after the scoped delete), W4-12; T-CLI-3 |
| K18 | **GO** (was NO-GO) | W4-1 (subtitle gate, breaker exclusion, item lanes, detached abort), W4-3, W4-7 (coalescing rules), W4-8 (streaming JSON parse), W4-9 (VTT and SRT rebuilt), W4-10; T-CLI-4, T-CLI-16 |

### Where every Warden-04 finding is addressed

| ID | Sev | v2.3 resolution | Where |
|---|---|---|---|
| W4-1 | Med | **Adopted.** Subtitle-file calls get their own path: a per-peer gate of **2** concurrent Peer calls (its own `SemaphoreSlim`, so 14 of the 16 pooled connections stay out of its reach), a queue of at most 16 waiters with the wait counted inside the 150 s budget and `429 Retry-After` beyond it, **item lanes** keyed by (peerKey, peerItemId, peerMediaSourceId) so a second request for any index, format or tick value of the same source waits for the first instead of occupying a connection beside it, and at most **one open lane per user**. A new `PeerCallProfile.SubtitleFile` (the old 30 s `ExternalSubtitle` profile is not reused) has `CountsTowardBreaker=false`: neither timeouts nor 5xx open the circuit; connect failures still do. The shared Peer call is detached from the first client's abort (reference-counted; a call already running 10 s finishes inside its own budget so a retry can join it). Step 10's cost argument is corrected. **Atlas extension:** HLS subtitle segments trigger the same whole-file extraction on the Peer, so they use the same lanes and gate and are excluded from the breaker too | 6.4, 6.6a step 7, 6.6a step 10, 6.9, 6.10, K18, T-CLI-4, T-CLI-16 |
| W4-2 | Med | **Adopted.** 3.5 and 8.5 now say `DeviceId` is the header's value, client-chosen, a convenience key within one user and never an identity. Capped at 128 printable ASCII characters (longer or odd values are hashed). The capabilities fallback is used only when the device row for that id belongs to the caller. **Precision added:** that check stops a caller borrowing another device's profile; it does not stop another user overwriting the capabilities stored under a device id they know (core keys them by device id alone). The profile still goes through the rebuild, so the effect is codec choice for that device, as in core: recorded as a residual | 3.5, 6.2 step 2, 8.5 step 8, K14, T-CLI-8 |
| W4-3 | Med | **Adopted.** One rule in 6.1 and 6.6a: the id that is authorized is the id that is served, and it is the **route** id. Any `itemId` query value (every value of a repeated key) that is not equal to the route id gives **404** before CallerContext and before any Peer call, whichever of the two is federated. "(or the `itemId` query)" is deleted from 6.6a step 1 | 6.1 mechanics, 6.6a steps 0-1, 8.7 R16, K13, K18, T-SEC-15 |
| W4-4 | Med | **Adopted.** `DELETE Videos/ActiveEncodings` for a caller's grant releases a slot **only for a `Transcode` grant and only after the scoped delete was sent**, and forwards only when that grant has started a Peer job (`peerJobStarted`); otherwise Shoal answers 204 with no Peer call. Direct play is released only by connection close plus the 2 minute grace or `PlaybackStopped`. Per-user bucket 10 per minute | 3.5, 6.1, 6.9, 6.11, K13, K15, K17, T-SEC-25 |
| W4-5 | Low | **Adopted.** The anchor is sent at Home startup and on the **first successful contact after any outage** (circuit closing, trust window refreshed after a lapse, Resume), not on the next tick, and it keeps running while a server is paused on Home. The checklist says steps 8 and 9 are a required pair. The "at most 60 s" sentence is replaced by "protection resumes when Home next reaches the Peer; until then the device lock (step 8) is what covers the gap". T-SEC-22(d) gets a timed variant with K20 | 4.1 steps 8-9, 4.1 anchor bullet, 4.3, 6.11, 8.3 T42, K20, T-SEC-22 |
| W4-6 | Low | **Adopted, and the clamp is decided: 1..4.** The code clamps `MaxStreamsPerUser` to 1..10 (`SettingsValidator.cs:35`); the spec relied on 1..4. 1..4 is right: one account already holds at most half of a default peer (2 of 4), and above 4 the per-user grant cap of 8 can no longer guarantee a slot-free grant. The validator changes in K14's PR, **and** eviction is safe on its own (no slot-free grant: refuse with `RateLimitExceeded`, never evict a slotted one). `hasSlot` is defined for two devices on one item (every live grant of a (user, item) whose static slot is live holds it). Capacity order: replacement, then own eviction, then the global cap | 3.5, 6.2 steps 1 and 6, 6.9, K14, K15, T-CLI-8 |
| W4-7 | Low | **Adopted** as written rules in 6.6a step 7g (joins only after CallerContext and the caller's own grant lookup; key includes `peerKey` and `peerMediaSourceId`; immutable validated buffer, read-only per joiner; no cache after completion; failures not retained; nothing caller-specific in the key, each joiner charged separately) | 6.6a step 7g, K18 |
| W4-8 | Low | **Adopted.** The JSON body is read into one bounded buffer (10 MB) and walked with `Utf8JsonReader`: no comments, no trailing commas, depth 8, strict numbers; the walk stops with **502 at event 20,001**. Unknown members are **skipped, never stored** (the option Warden allowed; a future core adding a member must not break webOS subtitles), duplicate known members are 502. Serialized with the default encoder; `UnsafeRelaxedJsonEscaping` is banned. Fuzz property `^(?:[^<>]\|</?[ibu]>)*$` on every `Text` | 6.6a step 9, 8.8, K18 |
| W4-9 | Low | **Adopted, and extended.** VTT and SRT are **rebuilt** like JSON (6.6a step 9a): cue text through the same `<i> <b> <u>` allowlist, timing lines re-serialized, VTT `NOTE`, `STYLE` and `REGION` blocks dropped. Reason for the extension: a VTT `STYLE` block can give `::cue` a `background-image: url(...)` that makes a browser rendering the native track fetch from a host the Peer chose, which would hand the friend the **viewer's** IP that Home otherwise hides (derived from the CSS `::cue` property list, not run in a browser; T-SEC-18 checks it). Applies to every VTT Shoal emits, HLS subtitle segments included. ASS and SSA keep the header check only (override tags are styling, W2 N-14 residual) | 6.4, 6.6a steps 8-9a, 8.6, K18, T-SEC-18 |
| W4-10 | Low | **Adopted.** 6.2.2 drops every `External` subtitle profile whose format is not one Shoal serves (`srt`, `subrip`, `ass`, `ssa`, `vtt`, `webvtt`, `js`, `json`), which subsumes the bitmap rule and also stops `smi`, `ttml`, `sub` and unknown formats. 6.6a step 5 uses a **source-codec** allowlist instead of core's `IsTextFormat` blocklist: `subrip`, `srt`, `ass`, `ssa`, `webvtt`, `vtt`, plus `mov_text` (MP4 text tracks, which core converts through the same encoder; kept only if T-CLI-4 confirms lab-server serves them) | 6.2.2, 6.6a step 5, K14, K18, T-CLI-4, T-CLI-15 |
| W4-11 | Low | **Adopted.** 6.4 now reads: an unknown, extra, repeated or mutated key never causes a 4xx *from Shoal*; the credential keys are judged by core first (a repeated `ApiKey`, or `api_key` without legacy authorization, is core's 401, and Shoal must not "fix" it). The four validated tick keys answer 400 when repeated (the "exactly one" of U-V and U-S), and the compared index keys are treated as unparseable when repeated. `clientIndexMismatch` is an admin-only debug counter with no value labels. T-CLI-3 expects 401 for the duplicate `ApiKey` and the `api_key` variants | 6.4, 6.6a step 6, K17, T-CLI-3, T-CLI-14 |
| W4-12 | Low | **Adopted.** The filter reads `Request.Query` (after core's `UrlDecodeQueryFeature` re-parse, the collection the binder reads), never `QueryString.Value`; checks **every** value of `itemId`; parses GUIDs with `Guid.TryParse` (N, D, B, P, X, any case, padding), never `ParseExact`. Fixtures for every 6.1 route | 6.1 mechanics, K13, T-SEC-15 |
| W4-13 | Low | **Adopted.** 4.5 gains "restarting Jellyfin also clears hidden sessions"; 4.1 step 6 says token-less sessions do not show in Devices; the foreign-device watch also alerts when `GET /Sessions` lists **more than one** session for the share user (it already alerted on any other DeviceId; the count catches a phantom that reuses Home's DeviceId under another client name) | 4.1 step 6, 4.3, 4.5, K20 |
| W4-14 | Low | **Adopted.** `BannedSymbols.txt` gains `HttpRequest.QueryString`, `HttpRequest.Path`, `HttpRequest.PathBase`, `GetDisplayUrl`, `GetEncodedUrl`, `GetEncodedPathAndQuery`, allowed only in one helper (`ShoalRequest`: the 8 KB query-length check and route-template logging). README keeps "redact Debug logs": core's `ResponseTimeMiddleware` logs the full URL with `ApiKey` at Debug for every response slower than 500 ms. T-CLI-14 greps Shoal's lines only | 6.1 Logging, 8.8, 11.5, K13, T-CLI-14 |
| W4-15 | Low | **Adopted.** The help text lists as "Works" only clients T-CLI-13 has passed; the rest read "should work (not tested yet)" until Pilot runs them. T-CLI-13 adds Jellyfin Media Player, the official Android app and the remaining Moonfin platforms where a device exists. README gains the "do not put a token-adding proxy in front of Jellyfin" sentence. "not all of them yet" and "does not yet work" lose "yet" (Option A may never ship) | 6.12, 11.5, T-CLI-13 |
| P2 (Pilot's Shoal-relevant notes) | | `Audio/{id}/stream?Static=true`, `Items/{id}/Download` and `File` get anonymous and download-permitted-user variants in T-CLI-6 (`Download`/`File` answer core's 401 to anonymous callers, Shoal's 403 to signed-in ones). A stub item scanned after the index snapshot is caught: the index updates on `ItemAdded`, and on the five stub-reading routes an index miss falls back to the StubRoot path rule (one cached lookup) | 3.5, 6.1 mechanics, T-CLI-6 |

### Pilot-02 and the queued items

| Item | v2.3 resolution | Where |
|---|---|---|
| T-SEC-22 | **Passed** (P2§2-5): limit 1 refuses both self-minting routes while a session exists; an admin password change signs out every device; the limit-1 re-pair is refused and limit 0, pair, limit 1 works. The anchor itself is verified (204, session re-created, protection immediate); its timing waits for K20. The checklist text of 4.1 step 9 and the 4.5 pause sequence are no longer blocked | 4.1, 4.5, 8.3 T42, T-SEC-22 |
| Refusal text (P2§3) | 4.1 step 5 no longer promises "maximum number of sessions": the friend sees a generic error (403 `Error processing request.`) and the reason is only in their server's log | 4.1 step 5 |
| Session-limit error kind (UI§14.3 #5) | **`likelySessionLimit`** (Kiro's name, already in `PairingEnums.cs`; ui-spec's proposed `sessionLimitBlocked` is renamed to it), a *stopped* kind. Home never sees the friend's 403 on `Authorize`: on 12.1 the mint runs inside `Authorize` and fails before the request is marked authenticated, so Home's poll just keeps reading "waiting" (derived from `QuickConnectManager.AuthorizeRequest`; T-PAIR-13 (i) confirms). Home therefore **infers** it, and the UI says "probably": (a) a redeem after an approval answering 401, 403 or 404, or (b) a Quick Connect **re-pair** that reaches its local deadline unapproved while Home's reading of the account, refreshed when the pairing starts, shows a session limit of 1 or more. Otherwise the outcome is `expired` | 4.2a, 9.5, K5, T-PAIR-13 |
| 4.1b step 3 | The stale v2.1 sentence "Approve stays disabled until the typed code equals the one the Peer computed" is removed | 4.1b step 3 |
| T-CLI rows (P2§7 #12) | 11.3 now carries the concrete sub-cases 1a-6s from Pilot-02 section 6, each with the spike baseline and the v2.3 expectation | 11.3 |
| Owner D12 | G1 Option C is the owner's decision, no longer "pending owner review" | 0 item 18, 6.12 |

### Lumen's questions (`docs/ui-spec.md` 14.3)

| # | Question | Answer | Where |
|---|---|---|---|
| 2 | `pairingMethod` on a stopped first pairing? | **Yes.** `POST Shoal/Admin/Pairing` stores `pairing.method` next to `phase`, and the stored abort is `PairingAborted{kind, phase?, method, atUtc}`. The API exposes `pairingMethod: invite\|qc` (the working pairing's method; on a never-paired server, the stopped attempt's) **and** `pairingErrorMethod` with every `pairingError` (the stopped attempt's method, also on a re-pair whose method differs). The `interrupted` copy should prefer `pairingErrorMethod` | 4.2a, 9.5 |
| 4 | "The invite expired" as the invite-path title for `interrupted` before approval? | **Confirmed.** Quick Connect: "The code expired"; invite: "The invite expired" | 4.2a, 9.5 #5 |
| 5 | Session-limit kind | `likelySessionLimit`, rules above | 4.2a, 9.5 |
| 6 | Value names | `sessionLimit`: **`ok \| notSet`** confirmed (`ok` = exactly 1; `notSet` = 0 or more than 1; absent before Confirm and on invite pairings). `accountNotes`: **Kiro's four values win**, `userPreferenceAccess`, `mediaConversion`, `syncTranscoding`, `publicSharing` (already in `PairingEnums.cs`); the UI maps `userPreferenceAccess` to its "password self-change" note and any of the other three to its single "options Jellyfin ignores" note, so the API stays specific and the screen stays short | 9.5 |
| 3 | 4.1b cleanup | done | 4.1b step 3 |
| 1 | "Which apps work" | Owner confirmed G1 (D12); Warden's three edits (W4-15) are in 6.12. Lumen applies them to 8.3.12 | 6.12 |

### Where I differ from the inputs (recorded, not silently changed)

- **W4-2 fix, scope.** The own-device check limits *borrowing* a profile, not *planting* one: any user can `POST Sessions/Capabilities` for a device id they know, and core keys stored capabilities by device id alone (`SessionManager.cs:1901`). Not worth a Shoal-side store of its own: the rebuild bounds the effect to codec choices, exactly as core does for local items. Residual recorded in 6.2 step 2.
- **W4-10, codec list.** Warden's allowlist names output formats; step 5 needs source codecs. I added `mov_text`, conditional on the lab, because MP4 rips commonly carry it and core converts it to text.
- **W4-1, abort threshold.** Warden: "cancel only when the last waiter has gone and the call has been running less than the gate wait". I fixed it at **10 s**: long enough that a Peer that has started extracting is allowed to finish (its result is then cached on the Peer), short enough that a fast abort costs nothing.
- **W4-8, unknown members.** Skipped, not `Disallow` (Warden accepted either). The output is rebuilt from four typed values, so skipping costs no safety and keeps a newer core from breaking webOS subtitles.
- **W4-9 and W4-1** were extended (VTT `STYLE` blocks; HLS subtitle segments), as recorded in their rows.

### Open after v2.3

1. **Warden:** spot-check 6.6a step 7 (gate, lanes, detached abort) and step 9a (VTT/SRT rebuild) when K18's PR lands; Warden's verdict already makes K18 GO with these written.
2. **Pilot:** T-PAIR-13 (i) (the session-limit inference), T-CLI-16, T-SEC-25, the new T-CLI-4/-6/-8 cells, the T-SEC-15 R16 cells; T-SEC-22(d) timed once K20 lands; T-CLI-13 on whichever devices the owner has.
3. **Lumen (`docs/ui-spec.md`):** rename `sessionLimitBlocked` to `likelySessionLimit`; use `pairingErrorMethod` for the `interrupted` copy; map Kiro's four `accountNotes` values; apply W4-15 to 8.3.12 ("Works" only for tested apps, no "yet").
4. **Kiro:** `SettingsValidator` clamps `MaxStreamsPerUser` to 1..4 (in K14's PR); `PeerCallProfile` gains `CountsTowardBreaker` and a `SubtitleFile` profile (in K18's PR); K5 gains rule (b) of `likelySessionLimit` and stores `pairing.method`.
5. Carried: N-3 fixes 1-2 and N-17 (deferred), `.gitignore`, the 11.5 scrub.

---

## Changes in v2.2 (client compatibility)

Owner D11 makes the Moonfin apps on iOS, Android and TV a release blocker. Scout read every client's playback code (SC§) and found Moonfin compatible with the v2.1 design apart from small gaps, and the stock Jellyfin apps that send no credentials incompatible. This patch folds those gaps in, aligns the pairing and admin-API text with `docs/ui-spec.md` v2.1 (Lumen block), and corrects one pairing fact found while checking the session limit. Section numbers are unchanged; new material is 6.12 and suffixed rows. K-task ids are unchanged; only their acceptance criteria grew. Every behavior change below was re-read in the Jellyfin 12.1 source (citations relative to `jellyfin/` at tag `v12.1`).

### Decisions recorded (manager, 2026-10-15; not reopened here)

- **G1, credential-less clients: MVP = Scout's Option C, not supported.** The official Jellyfin Android TV app, Swiftfin, Findroid and the jellyfin-android external-player hand-off request `/Videos/{id}/stream?static=true` with no credentials at all (SC§2.5-2.8). Shoal keeps answering **401**; it never serves a federated title anonymously. This is a documented known limitation in the README and the admin UI help text (6.12). **Option A** (anonymous access only when it matches a recent grant for the same item and client IP) is recorded as a **v2.x candidate that needs a Warden review before anyone builds it**. Pending owner review (v2.3: **confirmed by the owner as D12**).
- **Production runs Jellyfin 12.1.0** (confirmed from its public `System/Info`), the same version as the lab. This settles SC§6's open question; `targetAbi` and every 12.1 citation stand.

### Where every Scout-02 gap is addressed

| ID | v2.2 resolution | Where |
|---|---|---|
| G1 | Not supported in MVP (Option C); 401, never anonymous; README and admin help text state it; Option A parked as a v2.x candidate behind a Warden review | 6.12, 6.3, 11.5, T-CLI-1 |
| G2 | Every Shoal HLS route ignores unknown or client-mutated query keys (`AudioStreamIndex`, `SubtitleStreamIndex`, `upmix`, `StartTimeTicks`, anything else) instead of answering 400. Nothing from the client query is forwarded: `AudioStreamIndex` and `SubtitleStreamIndex` are compared with the grant and a mismatch only increments a counter; the grant's values win. Only `ApiKey`/`api_key` (consumed by core's authentication) and the segment's two validated integers matter | 6.4 "Client query", T-CLI-3 |
| G3 | `js` (and core's `json`) is **supported**: Home fetches the Peer's JSON, parses it strictly and re-serializes it from typed values with sanitized `Text` (6.6a), and emits `application/json` (8.6). `sup`/`pgssub` (PGS, binary) is **not** supported: 404. Shoal also removes `External` bitmap entries from the forwarded `SubtitleProfiles`, so the Peer picks `Embed` (in-band on direct play) or `Encode` (burn-in) for PGS, as the owner already tested. Reasons in 6.6a | 6.2.2, 6.6, 6.6a, 8.6, T-CLI-4, T-CLI-15 |
| G4 | SubtitleHandler serves **embedded** text tracks as files too (the Peer extracts them through the same route), for both route forms (with and without `{ticks}/`), with the grant found by (caller, item) because clients send no `PlaySessionId`; concurrent identical extractions are coalesced and get a longer header deadline | 6.6, 6.6a, 6.10, 6.11, T-CLI-4 |
| G5 | `Audio/{id}/stream`, `stream.{container}`, `universal`, `master.m3u8`, `main.m3u8`, `hls1/...` and the legacy `hls/...` segment routes join the 404 row | 6.1, K13, T-CLI-6 |
| G6 | `Container` stays populated: the peer value if it passes 5.5, else the catalog's, never `null` while either is valid; the 5.5 rule gains the 40-character cap of core's `ContainerValidationRegexStr`. The static handler takes its Content-Type from the catalog container, never from the URL's `.{container}` | 5.5, 6.2.3, 6.3, T-CLI-5 |
| G7 | The "absolute `Http` Path" fallback is **deleted**. `Protocol=File`, `IsRemote=false`, `Path=null` is the only form; `SupportsDirectStream` is always `false` | 6.2.3 |
| G8 | Grants are keyed by (user, item, DeviceId): a new PlaybackInfo replaces that device's grant if it holds no stream slot. Per-user grant cap 4 → **8**, eviction takes the oldest grant **without a slot**, never a playing one; the global cap stays 200. PlaybackInfo per user: 20/min → a token bucket of **30/min, burst 15**. A Smart-TV start (up to 5 calls) or a Moonfin retry chain never trips either | 3.5, 6.2, 6.9, T-CLI-8 |
| PlaybackInfo merge | Confirmed in source: per field, the **query value wins** and the body fills what the query lacks; `DeviceProfile` and `AlwaysBurnInSubtitleWhenTranscoding` are body-only; defaults as core's. `UserId` (either place) is ignored. No profile at all means core's fallback, the caller device's registered capabilities, which Shoal mirrors | 6.2 step 2, T-CLI-2 |
| Credentials | Confirmed: core's authentication accepts the `Authorization` header (`Token=`), then `ApiKey` in the query (any case), and only with legacy auth on `X-Emby-Token`/`api_key`; the header wins. CallerContext reads only `HttpContext.User`, so Moonfin-Core (both), Roku (header) and Smart-TV (query) all pass. Shoal never logs a client request's URL or query string | 8.5 step 1, 6.1 mechanics, T-CLI-14 |
| Subtitle route query overrides (new) | Core's subtitle action lets obsolete query values `itemId`, `mediaSourceId`, `index`, `format` override the route (`SubtitleController.cs:216-229`). The filter treats the request as federated when either the route or the `itemId` query value is federated, and Shoal uses route values only | 6.1 mechanics, 6.6a |
| Moonbase | No conflict (Moonbase registers no filter). Lab checks for Pilot: new-media push after a bulk stub sync, peer transcodes absent from Moonbase's dashboard, Intro Skipper and Jellyfin Enhanced behavior on stubs | 11.3 T-CLI-12, 11.5 |
| Session limit and re-pair (correction) | Re-read in source: the session-limit check runs **before** the same-device logout, and the Quick Connect mint happens inside the friend's own signed-in session. So with the limit at 1 a Quick Connect pairing can never succeed, even after deleting the old device. Re-pair on a stock Peer = set the limit to 0, pair, set it back to 1. Shoal Peers are unaffected | 4.1 step 9, 4.2a, 4.4, "Verified" below, T-SEC-22 |

New tests Pilot owns: **T-CLI-1..15** (11.3), mapped to the K-tasks they gate.

### Lumen block (`docs/ui-spec.md` v2.1, section 14.2)

| ID | UI§14.2 | v2.2 resolution | Where |
|---|---|---|---|
| L1 | #4, pop-up scope | Two abort kinds. **Safety** (`requestReplaced`, `codesDidNotMatch`, `serverChanged`, `adminAccount`, `badPeerResponse`): card "Pairing stopped for your safety" and a `DisplayMessage` to admins. **Stopped** (every other kind): card "Pairing didn't finish", **no** pop-up. Both are durable until Dismiss or success | 4.2a, 9.3, 9.4 |
| L2 | #7, 4.1b | "Approve disabled until the typed code equals the Peer's" was wrong: the browser never holds the code. Approve enables at 8 valid Crockford characters; the server judges `POST .../Approval {approve:true, sas}` and answers `{match:false, triesLeft}` on a miss | 4.1b step 3, 9.6 |
| L3 | #1-#6 | Stale v2 text fixed: no "codes match" on the invite path (9.3); the approval card has a code **input**, never the code (9.3, 9.5 #1); "3 failures cancel all invites" removed (9.5 #2); `requestReplaced` copy says delete, not disable; error kinds gain `quickConnectUnreachable`, `badPeerResponse`, `interrupted`, `codeMismatch`; a Home restart reads "The code expired" before approval and `interrupted` after it (Lumen's reading confirmed, with the stored `phase`) | 4.2a, 9.3, 9.5 |
| L4 | #9 | Three wrong codes end **that request and its invite** (the invite is already bound to Home's commitment, so nobody else could use it). Home learns `codeMismatch`, distinct from `denied`. **Three tries kept**: only the Peer admin can submit a code (admin-only endpoint), so the limit is a human tripwire, not a guessing defense, and 3 vs 5 changes no security | 4.1b step 3, 4.1b table, 9.5, 9.6 |
| L5 | (re-pair friction) | Documented, and corrected by the session-limit row above: Quick Connect re-pair needs the limit at 0 during pairing. No other design change | 4.1 step 9, 4.4 |
| L6 | #12 | `POST Shoal/Admin/State/ConfirmRestore` added; `restoredFrom` on Status | 3.7, 9.5, 9.6 |
| L7 | #8 | `POST Shoal/Admin/Peers/{h}/DismissPairing` added (admin only) | 4.2a, 9.6 |
| L8 | #7, #10, #11, #12, #13 | Accepted as proposed: `confirmName` on destructive calls, session flags `peerSilent`, `probeExpired`, `paired`, peer fields `sessionLimit`, `accountNotes`, `pairingSession`, `pairingStoppedUtc`, `pairingError`, the new `policy` values, status fields `inviteFailures`, `proxySuspected`, `backupByCopy`, `guardLastGood`, `secretsReason`, `keySource`, and `isShareUser` on user rows | 9.5 |

### Verified in the 12.1 source for this patch

- **PlaybackInfo merge** (`MediaInfoController.cs:119-165`): every query parameter is `[ParameterObsolete]` and `??=`-merged with the body, so the query wins per field; defaults `AutoOpenLiveStream=false`, the five `Enable*`/`Allow*` flags `true`; `DeviceProfile` and `AlwaysBurnInSubtitleWhenTranscoding` are body-only; a missing profile falls back to `_deviceManager.GetCapabilities(User.GetDeviceId())` (`:137-147`).
- **Credential sources** (`AuthorizationContext.cs:84-111`): `Authorization` header `Token`, then (legacy only) `X-Emby-Token`, `X-MediaBrowser-Token`, then `ApiKey` from the query unconditionally, then (legacy only) `api_key`. `IQueryCollection` keys are case-insensitive, so `apikey` works too.
- **Subtitles** (`SubtitleController.cs:208-275,295`): `js` is aliased to `json` (`:231-233`), written by `JsonWriter` as `{"TrackEvents":[{"Id","Text","StartPositionTicks","EndPositionTicks"}]}` (`MediaBrowser.MediaEncoding/Subtitles/JsonWriter.cs`); obsolete query values override the route (`:216-229`).
- **PGS as a file**: for an embedded PGS stream core records the extracted format as `pgssub` (`SubtitleEncoder.cs:468-475`) and, because the requested `sup` differs, tries to parse and convert it, which has no parser or writer (`SubtitleEncoder.cs:139-152,269-323`, `SubtitleEditParser.cs:31-39`). Derived, not executed: **stock 12.1 cannot serve `Stream.sup` either**; core's own `DeliveryUrl` for PGS uses `Stream.pgssub`, the one format it returns raw. T-CLI-4 records what core does on lab-server.
- **Session limit order** (`SessionManager.cs:1700-1746`, `QuickConnectManager.cs:136-165`): the `MaxActiveSessions` check comes before `GetAuthorizationToken`'s logout of the same device, and Quick Connect `Authorize` mints Home's token synchronously inside the friend's signed-in request. With limit 1 the friend's own browser session fills the slot, so the mint is refused; deleting Home's old device does not help. v2.1's "delete the old device, or set the limit to 0" is corrected to "set the limit to 0" (4.1 step 9, 4.4). T-SEC-22 now includes this case.
- **Audio routes** (`AudioController.cs:88-89,252-253`, `UniversalAudioController.cs:92-93`, `DynamicHlsController.cs:577-578,914,1268`, `HlsSegmentController.cs:55-56`).

### Where I differ from the inputs (recorded, not silently changed)

- **G2, "map known safe params":** v2.2 maps **none**. The grant already holds the indices the client chose in PlaybackInfo, and changing `AudioStreamIndex` on a live HLS URL would let the client pick a track whose copy-versus-encode decision the Peer made for a different one. Shoal compares, counts a mismatch and uses the grant. If Pilot sees the counter move in a normal Moonfin session (T-CLI-3), mapping the two indices becomes a v2.x change.
- **G3, PGS:** Scout offered "cap size and time". Not adopted: a binary parser in a TV app is fed straight from the friend's server, extraction costs the Peer a full read of the file, and core 12.1 itself appears not to serve `Stream.sup`. In-band rendering on direct play and burn-in on transcode cover PGS.
- **UI§14.2 #6:** Lumen's mapping is confirmed, but the stored kind is `interrupted` in both cases with `phase`, so the "before approval" outcome can also tell the friend to delete a stray `Shoal: <Home>` device (the code stayed live on the Peer after Home restarted).

### Open after v2.2

1. **Owner:** confirm G1 Option C as the MVP stance (recorded as decided by the manager, pending owner review). Option A needs a Warden review before design work. *(v2.3: confirmed, D12.)*
2. **Warden (before K14, K17, K18 merge):** *(v2.3: done, Warden-04; resolved in "Changes in v2.3".)* the JSON subtitle rebuild (6.6a), grant keying and eviction (3.5, 6.9), the bitmap-profile removal (6.2.2), and the G1 help text.
3. **Pilot:** T-CLI-1..6 can run now against the spike build as a baseline (expected failures recorded, not gating); the gates apply when K13-K19 land. T-SEC-22 gains the limit-1 Quick Connect case.
4. **Lumen follow-ups in `docs/ui-spec.md`:** pop-up for safety kinds only (8.2.9); ~~the Quick Connect re-pair hint (8.2.3) and "To pair again later" (8.2.8) must say "set the session limit to 0 until we're done"~~ **done** (Atlas, at the manager's request: both now say limit 0, pair, back to 1); the new `codeMismatch` outcome on the watch side (8.2.5 "Turned down" splits in two); `interrupted` with `phase: beforeApproval` copy; a place for the 6.12 client-support text (Overview help or Pair-a-server success hint).
5. Carried from v2.1: N-3 fixes 1-2 and N-17 (deferred), `.gitignore`, the 11.5 scrub.

---

## Changes in v2.1 (Warden-02 patch)

WARDEN review 02 (`docs/reviews/warden-02.md`) found no Critical issue, two High (N-1, N-2), eight Medium and nine Low, and gave K5 a GO after seven spec edits. This patch changes only this file. Section numbers are unchanged: new material is a suffixed section (4.2a) or sits inside the existing section. Every Jellyfin citation in the patched text was re-read in the 12.1 source for this patch.

### The two High findings in short

- **N-1, stock Peer (owner D7: the friend installs nothing).** v2's checklist told the friend to set four flags false that the 12.1 dashboard cannot change. One of them, `EnableUserPreferenceAccess`, lets the sharing account's own token reset its own password and then sign in with an empty one, so a token holder outlived "delete the devices" and "pause". v2.1 splits the checklist into *set these* and *cannot be set from the dashboard*, adds `MaxActiveSessions=1` after pairing (step 9; the lab must still confirm it, T-SEC-22), says plainly that **on a stock Peer deleting the sharing account is the only clean revoke and disabling or pausing is best-effort**, and adds the guard bans for Shoal Peers. Where: 4.1, 4.3, 4.5, 8.3 T42, 8.4 job d, 9.3, 9.5, K21, K23.
- **N-2.** Share users are never admitted to any Shoal library, in either access mode, and Reconcile never writes a Shoal library into their list, so a two-way server cannot chain a third friend's library to the wrong person. Where: 5.6, 8.5 step 6, 8.7 R15, 9.6, K10, T-VIS-7.

### Where every Warden-02 item is addressed

| ID | Sev | v2.1 resolution | Where |
|---|---|---|---|
| N-1 | High | Checklist split into *set these* / *cannot be set*; step 9 `MaxActiveSessions=1`; Home's session anchor; truthful revoke and pause text; Shoal Peer bans on `Users/Password` and `Sessions/Capabilities*`; `/Users/Me` checks reclassified | 4.1, 4.3, 4.5, 6.11, 8.3 T42, 8.4 d, 9.3, 9.5, K21, K23, T-SEC-22 |
| N-2 | High | `Admitted()` is false for share users in both modes; Reconcile removes and never adds Shoal libraries for them; `PUT Access` rejects them | 5.6, 8.5 step 6, 8.7 R15, 9.6, K10, T-VIS-7 |
| N-3 | Med | **Partly.** The guarantee is now worded truthfully (playback is enforced on every request; browse lists catch up at the next reconcile, up to an hour). The 10 s debounced re-apply and the `Shoal: <Peer>` BlockedTags backstop are **deferred to v2.2** (K10 follow-up, non-blocking per Warden) | 5.6 |
| N-4 | Med | Valid secret compared first and never limited; only failures counted; no invite is ever destroyed by wrong guesses; constant-time answer; per-IP buckets use `GetNormalizedRemoteIP`; Initiate shield 10/min per IP and 300 per 10 min total; proxy requirement documented | 4.1b, 4.3, 8.4 b, 8.3 T45, K22, K23, T-PAIR-9, -11, -17 |
| N-5 | Med | Typed SAS (Home shows it, the Peer admin types it, the Peer never displays it); commit-reveal nonce; no invite redemption over plain http outside the Tailnet class | 1.1b, 4.1b, 4.2, 9.3, 9.5 #1, K22, T-PAIR-8, -18 |
| N-6 | Med | Guard cache keeps only results for items that exist, bounded LRU; staleness noted; bans fail closed; sweep ordering (`expectedDeviceIds` before `AuthenticateDirect`); transcode cap renamed best-effort | 4.1b, 8.4, K23, T-SEC-24 |
| N-7 | Med | `JELLYFIN_SHOAL_KEY_FILE` variant; service-account caveat; the DPAPI entropy is a public constant, not a secret; one AES-GCM message per peer, bound to the peer key and host (key rotation stays out of scope) | 3.1, 8.2, K2 |
| K2 prereq (N-7.1) | | `JELLYFIN_SHOAL_KEY` is exactly 32 random bytes as base64url or hex; anything else is refused, with no fallback to `master.key` and no generated key | 8.2, K2 |
| N-8 | Med | A change of peer **host** drops the stored token before the admin API can send it anywhere; typed name in the request body for destructive calls; restore by handle; server-side validation | 4.4, 8.2, 9.6, K2, U2 |
| N-9 | Med | `peer-role/guard-lastgood.json` keeps the bans and the mode in recovery mode; restore from `.bak` or a daily copy loads every access mode as `Selected` and needs confirmation; `File.Replace` degrades to copy-then-rename | 3.1, 3.7, 8.4, K1, T-REG-5 |
| N-10 | Med | The seven K5 edits, below | 4.1, 4.2a, 4.3, 6.11, 9.3, 9.5, K5, T-PAIR-13..16 |
| N-10 (1) | | Truthful checklist per N-1 | 4.1, 9.3 |
| N-10 (2) | | Server-side "codes match" confirm: explicit `AwaitingConfirm` state, token in memory only, `Confirm` legal only there | 4.2a, 1.1a, 9.6 |
| N-10 (3) | | Durable `PairingAborted` (persisted, on the Overview card, pushed as `DisplayMessage`); after `requestReplaced` the next attempt targets a NEW sharing account | 4.2a, 3.1, 9.3, 9.5 |
| N-10 (4) | | Poll classification: baseline poll first; 404, 401, wrong base path, restart and timeouts are separate states; `GET /QuickConnect/Enabled` first; explicit 3 s poll timeout and one redeem attempt | 4.2a, 4.3 |
| N-10 (5) | | Validate `Code`, `Secret` and the token (and user and server ids) from the Peer before use; never log the poll URL, secret, code or token | 4.2a, K5 |
| N-10 (6) | | Header values percent-encoded including `%` and `+` | 6.11, 4.2a |
| N-10 (7) | | One pairing session per Home; Initiate only on an explicit click; `baseUrl` immutable while it lives; Probe handles expire; the 5-per-10-minutes limit enforced on the server | 4.2a, 9.5 #5 |
| N-11 | Low | Structural caps on the HLS grammar: bytes, lines, segment count, single and summed `EXTINF`; `\z` anchors; `long` numbers; singleton tags once; re-serialized `Last-Modified` and `Content-Range` | 6.3, 6.4 (I7, I8), App. C F-PL-34..36, K16 |
| N-12 | Low | JPEG marker walk by declared lengths; first PNG `IHDR` only | 5.4, K9, F-CAP-06 |
| N-13 | Low | `OnUninstalling` restores `EnableAllFolders=true` for managed accounts | 5.6, K10 |
| N-14 | Low | Residual stated: a friend's server can send malformed media to your players | 8.1, 11.5 |
| N-15 | Low | Documented residuals (images by id, user-data oracle, DLNA, SyncPlay) | 8.1, 11.5 |
| N-16 | Low | Poll URL never logged or put in exceptions; the authorized secret's real lifetime is stated | 4.2a, App. A, K5 |
| N-17 | Low | **Deferred (optional).** Steps 5-7 on every HLS request instead of every 5 min | none |
| N-18 | Low | Release checklist names `.claude/worktrees/`; the `.gitignore` change itself is outside this file (open, below) | 11.5 |
| N-19 | Low | `PeerSessionReporter` checks `grant.localUserId == session.UserId` before forwarding a stop | 6.8, K19 |
| W§A-1, A-9, A-18 (partial in review 02) | | Closed by N-1, N-4, N-5, N-10 above | 4.1-4.5, 8.4 |
| W§A-6, A-20.1 (follow-ups) | | N-2 and the N-3 wording | 5.6 |
| W§A-7, A-14, A-16, B-14 (follow-ups) | | N-8, N-9, N-7, N-18 | as above |

New tests Pilot owns: **T-PAIR-13..18**, **T-SEC-22** (the lab confirmation of `MaxActiveSessions=1`, blocks shipping the checklist text), **T-SEC-23** (alias of T-VIS-7), **T-SEC-24**, **T-VIS-7**, **T-REG-5**, **F-PL-23..36**, **F-CAP-06** (11.3, Appendix C).

### Accepted non-adoptions (W2§2; recorded, not changed)

- **No `Cross-Origin-Resource-Policy: same-origin` header** (accepted, W2§2a). Direct play through `<video src>` is a no-cors request, and CORP is not an authorization control; the Content-Type table, `nosniff`, CSP sandbox and `attachment` are what stop script in Home's origin. Optional refinement, **not adopted**: CORP is enforced only for no-cors requests, so `same-origin` could go on JSON, admin and playlist responses and stay off raw media and segments.
- **TLS SPKI pinning stays opt-in** (accepted, W2§2b). Default Public-peer protection is CA validation, hostname verification, TLS 1.2+, HTTPS required. Optional middle path, **not adopted**: record the issuer name at pairing and show an alert-only badge when it changes.
- **Transcode cap only when the friend runs Shoal** (accepted, W2§2c). Two README precisions are adopted: `MaxActiveSessions` does not bound transcodes (jobs are keyed by device and `PlaySessionId`, not by session), and on a stock Peer a hostile token holder can also write to the share user's own rows (user data, display preferences, playlists), unbounded in size per request up to Kestrel's body limit. Accepted for a "semi-trusted friend"; stated in 8.1 and the README (11.5).
- **"3 failed attempts cancel ALL active invites"**: Warden pushed back (W2§2d) and that design is **withdrawn**, not merely softened. It bought no security (the invite has 130 bits) and handed an anonymous caller a way to disable pairing. Replaced by the failure-only limiter of N-4.

### Verified in the 12.1 source for this patch (two corrections to review 02)

- **Correction 1, Quick Connect `Secret`.** Core emits it with `Convert.ToHexString`, which is **upper-case** hex, 64 characters (`QuickConnectManager.cs`, `GenerateSecureRandom`). Review 02 suggested `^[0-9a-f]{64}$`, which would reject every real secret. The pattern is `^[0-9A-Fa-f]{64}$` (4.2a). The access token is `Guid.NewGuid().ToString("N")`, lower-case 32 hex, so `^[0-9a-f]{32}$` is right (`Device.cs:29`).
- **Correction 2, secret lifetime (N-16).** Approval sets the pending request's `DateAdded` to now + 1 minute, so that entry lives about 11 minutes, but the authorized secret is stored with the approval time and `ExpireRequests` keeps it for 10 minutes from approval. Redemption is repeatable until then (`QuickConnectManager.cs`, `AuthorizeRequest`, `GetAuthorizedRequest`).
- **New fact (affects the session limit).** `GET /Users/Me`, `GET /System/Info` and `GET /Sessions` do **not** create a session. Sessions are created by authentication and by `RequestHelpers.GetSession`, which the session and playstate controllers call. After a Peer restart the in-memory session list is empty, so Home's 60 s health call includes a **session anchor** (`POST /Sessions/Capabilities`, 4.1, 6.11) for peers paired by Quick Connect; otherwise `MaxActiveSessions=1` would not protect the account until Home next played something.
- **New fact (revocation).** An admin changing a user's password on 12.1 calls `RevokeUserTokens`, which signs out **every** device of that user (`UserController.cs:317-322`). That makes "disable, set a new password, re-enable, re-pair" a safe pause on a stock Peer (4.5). The lab confirms it (T-SEC-22).
- **Confirmed as in review 02:** `EnableUserPreferenceAccess`, `EnableMediaConversion`, `EnableSyncTranscoding`, `EnablePublicSharing` default to true and the dashboard writes none of them (`Profile.tsx`, `Access.tsx`); `AuthenticateNewSessionInternal` checks the device lock and then `MaxActiveSessions` against the in-memory session list for both password login and Quick Connect; sessions leave that list only through logout, device deletion or a websocket closing (`SessionManager.cs:308-322,365-377,1815-1840`); a deleted account's token is refused (401 if its device row went with the user, otherwise 403 via the empty-user-id branch of `DefaultAuthorizationHandler.cs:45-49`, never served).

### Open after v2.1

1. **Lab first (blocks shipping the checklist text, not K5's code):** Pilot runs T-SEC-22 on a stock Peer: a share-user token registers a fresh DeviceId as non-persistent, then tries (a) Initiate + Authorize(self) and (b) password reset + `AuthenticateByName`. Expected: both succeed without the limit; both are refused with "maximum number of sessions" while a session exists; the session anchor restores protection within 60 s of a Peer restart; an admin password change signs out every device. If the limit behaves differently, 4.1 step 9 and 4.5 change before release.
2. **Deferred to v2.2:** N-3 fixes 1 and 2, N-17.
3. **Manager:** add `.claude/worktrees/` to `.gitignore` (N-18); scrub private IPs and CT ids from `docs/` before the public push (11.5).
4. **Lumen follow-up in `docs/ui-spec.md` (not edited here):** the stock-Peer checklist (new step 9 and the *cannot be set* list), approval card with a code box instead of a displayed code (around line 729), removal of "The codes match" on the invite path, "Invites cancelled after failed attempts", revoke and pause copy, and the new error kinds in 9.5.
5. **Optional, not adopted:** alert on a new remote address for share-user requests on Shoal Peers (W2§3.4 (b)3); issuer-name badge; CORP on JSON responses.

---

## Changes in v2 (kept for history; rows marked **v2.1** were superseded)

### Summary

1. **Pairing (W§A-1).** Quick Connect stays the MVP path because the friend needs no plugin. Home now polls every 1 s and treats a vanished request as a hijack. Home's device name carries a check the friend verifies in their Devices list before locking the share user to that device. Verified limit: core's device lock ignores devices that report themselves as non-persistent, so the lock stops casual misuse but is not a revocation control (4.1). When the friend runs Shoal, pairing uses a one-time invite plus a safety code (SAS) the Peer admin confirms before approving. Shoal Peers also rate-limit `QuickConnect/Initiate`, forbid share users from authorizing Quick Connect, and sign out unexpected devices.
2. **HLS rewriting (W§A-2, W§B-1)** is a closed grammar (6.4). It has a tag allowlist and position-specific URI regexes, every emitted line is rebuilt from parsed values, and everything else is dropped or rejected.
3. **Responses (W§A-3, W§B-3).** Shoal always writes its own Content-Type from a closed table and adds `nosniff`, `no-referrer`, `no-store`, and `CSP: sandbox` plus `attachment` on non-media (8.6).
4. **CallerContext (W§A-4, W§B-6)** is specified step by step (8.5), with banned APIs (8.8) and a negative test matrix (8.7).
5. **Default-deny visibility with an access mode (W§A-6, owner D3/D4).** A peer library starts as "Only selected users", with only the pairing admin selected. One switch turns it to "Everyone on this server", which includes users created later, and switching back restores the selection. Shoal enforces the mode on every playback request (CallerContext step 6) and keeps core's library access in line for browsing (5.6). Peer ratings are untrusted, so restricted profiles are excluded unless the admin acknowledges.
6. **Shoal state lives in its own atomic store** (`state.json`), not the plugin XML that core wipes on read errors (W§A-14, 3.7).
7. **Caps (W§A-10/11/12):** per-peer disk quota, new-items guard, entity caps, JPEG/PNG-only images, per-field sanitizing of metadata and `MediaStreams`.
8. **Revocation is stated honestly (W§A-9).** Disable the share user first, remove the network share, delete its devices and the user (4.5). Shoal Peers close self-minting and add a per-share transcode cap (8.4). *(Superseded in v2.1: on a stock Peer deleting the sharing account is the only clean revoke, 4.5.)*
9. **Bounded I/O (W§B-2):** every peer body goes through one bounded reader with connect, header, idle and total deadlines (6.10). **Nothing from the client is forwarded raw (W§B-4):** 6.11 lists every byte Home sends.
10. Corrected threats T11, T13, T26 and T30 (8.3). Added a release checklist with the repo scrub (11.5). Work is re-cut into small ordered tasks with fixture-based acceptance criteria: 23 for Kiro (backend) and 9 for Kiro 2 (admin UI, plus phase 4 speed), per owner D10 (11.2).
11. Section 9 now follows Lumen's approved screens (`docs/ui-spec.md`), resolves its five open items (9.5) and lists the admin API (9.6).

### Owner decisions recorded (log: `docs/DECISIONS.md`; design consequences in section 12)

| # | Decision | Where |
|---|---|---|
| D3 | Default-deny for other users. The admin who adds a peer library is **pre-selected** so they can test at once; nobody else is. Peer-supplied ratings and metadata are untrusted. | 4.2, 5.6, 8.5 |
| D4 | Each federated library has an **access mode**: "Only selected users" (default) or "Everyone on this server", one switch. "Everyone" includes users created later. Switching back restores the previous selection. | 5.6, 8.5, 8.7 |
| D5 | Offline peer: titles stay listed and fail fast with a clear message; the server card shows the stored backdrop greyed out. Hiding is off by default. | 5.7, 9 |
| D6 | "Friends watching from you" stays at the bottom of the Overview page. | 9 |
| D7 | The friend needs **no plugin** for the MVP: a restricted share user plus Quick Connect is enough. Shoal on the Peer is optional and unlocks two-way sharing, invite pairing and Peer-side hardening. | 4.1, 11.1 |
| D8 | It is OK that the Peer sees which titles are watched, never by whom. | 6.8 |
| D9 | The Peer-side hardening filter ships in `LogOnly` with a one-click `Enforce`. **Superseded by D18 (2026-10-16): a fresh install starts in `Enforce`; a stored mode is never changed on upgrade; `LogOnly` stays one click away.** | 8.4 |
| D10 | Two coders: Kiro (backend core: pairing, sync, playback security path) and Kiro 2 (admin UI and speed diagnostics), in separate worktrees. | 11.2 |

Lumen's five open items in `docs/ui-spec.md` section 14 are resolved in 9.5.

### Warden findings: where each is addressed

| ID | Sev | Resolution | Where |
|---|---|---|---|
| A-1 | High | Fixed. 1 s poll; vanished request = hijack abort (one redeem attempt distinguishes "approved, then replaced"); check in the device name, verified in the friend's Devices list; invite + SAS for Shoal Peers; Initiate rate limit, unexpected-device sweep and Authorize ban on Shoal Peers; foreign-device watch. Residual risk for public stock Peers is stated. **v2.1:** the invite path gained a failure-only limiter, a typed SAS and a commitment, and the Quick Connect path a state machine (N-4, N-5, N-10). | 1.1, 4.1-4.4, 4.2a, 8.4 |
| A-2 | High | Fixed: closed grammar, ASCII-only `NonBacktracking` regexes, rebuilt output. | 6.4 |
| A-3 | High | Fixed: own Content-Type from a closed table, header set on every response. CORP not adopted (see below). | 8.6 |
| A-4 | High | Fixed: 8-step CallerContext, `BannedSymbols.txt`, T-SEC-15 matrix. | 8.5, 8.7, 8.8 |
| A-5 | Med | Fixed: slots taken at first byte or segment; grants are cheap and evict only the same user's oldest; per-peer default 4, one user takes at most half. | 6.9 |
| A-6 | Med | Fixed (owner D3/D4): per-library access mode enforced by CallerContext at request time; core library access reconciled for browsing; drift alerts; untrusted ratings; restricted profiles excluded by default. **v2.1:** share users are never admitted (N-2); the browse layer is eventually consistent and now says so (N-3). | 4.2, 5.5, 5.6, 8.5 |
| A-7 | Med | Partly adopted. The pin is presented as accident detection; an unauthenticated pin check gates the token (120 s trust window); Tailnet address rule; LAN cleartext acknowledgement. The TLS leaf-key pin is **opt-in**, not default. | 4.4, 8.3 T13 |
| A-8 | Med | Fixed: proxy mode restricted to Tailnet IP literals or `*.ts.net`; extended block list and address normalization; SSRF from this field is stated as admin-only. | 2 (AddressPolicy), 8.3 T11 |
| A-9 | Med | Fixed: revoke runbook (disable first; delete devices and user; clean devices before any re-enable). Shoal Peers: share users cannot authorize Quick Connect, unexpected devices are signed out, revoke verifies zero devices, per-share transcode cap. On stock Peers, self-minting and transcode floods can only be stopped by disabling the user: accepted and documented. **v2.1 corrects this (N-1):** on a stock Peer a token can also reset the account's password, so disabling is best-effort and **deleting the sharing account is the only clean revoke**; pausing needs a password change before re-enabling. Shoal Peers also ban `Users/Password` and `Sessions/Capabilities*` for share users. | 4.5, 8.4 |
| A-10 | Med | Fixed: 2 GB per-peer quota, poster 2 MB and backdrop 4 MB caps, 5 actors per title, 20,000 distinct names per peer, new-items guard. | 5.2, 5.4, 5.10 |
| A-11 | Med | Fixed: `format=Jpg`, JPEG/PNG magic only, at most 4000 × 6000 and 16 MP, serial fetch. | 5.4 |
| A-12 | Med | Fixed: canonical provider ids, per-field `MediaStream` table, StubEnricher never writes stream text. | 5.5, 6.2.3 |
| A-13 | Med | Fixed: route set enumerated from `EndpointDataSource` plus a CI diff; fail-open on exceptions; 60 s cache; images excluded from Enforce. | 8.4 |
| A-14 | Med | Fixed: `state.json` with temp file, fsync, `File.Replace`, `.bak`, 7 dailies; recovery mode; XML holds toggles only; the validator never throws. | 3.1, 3.2, 3.7 |
| A-15 | Low | Fixed: DeviceProfile rebuilt with a fixed name; T26 corrected. | 6.2.2, 8.3 |
| A-16 | Low | Fixed: DPAPI `CurrentUser` with entropy; HKDF, nonce and AAD specified; `File.Replace`; refuse to start on tamper; network-filesystem warning; backup note. | 8.2, 3.7 |
| A-17 | Low | Fixed: T30 corrected (MD5); supply-chain items in the release checklist. | 8.3, 11.5 |
| A-18 | Low | Fixed: full policy list including the device lock; Shoal Peers repair drift; Home warns from `/Users/Me`. **v2.1:** four listed flags cannot be set from the 12.1 dashboard, so stock Peers get *set these* / *cannot be set* lists and a session limit (N-1). | 4.1, 4.1b |
| A-19 | Low | Fixed: 12 h absolute grant lifetime; re-check every 5 min. | 3.5, 6.4 |
| A-20.1 | Low | Fixed: Shares refuse libraries under StubRoot. | 4.1b |
| A-20.2 | Low | Fixed: merged-version detection with an admin warning. | 6.1 |
| A-20.3 | Low | Fixed: `Items/{id}/File` returns 403; `LiveStreams/Open` with a federated `itemId` returns 400. | 6.1 |
| A-20.4 | Low | Fixed: T4 now says "peer token"; README warns that core Debug logs contain user tokens. | 8.3, 11.5 |
| A-20.5 | Low | Fixed: T25 says CSRF is not applicable; the "header-only admin" rule is dropped. | 8.3 |
| A-20.6 | Low | Fixed: JSON depth 32 everywhere. | 6.10 |
| A-20.7 | Low | Fixed: static requests limited to 20 per second, burst 100, per user. | 6.9 |
| A-20.8 | Low | Fixed: PathSanitizer details. | 5.5 |
| A-20.9 | Low | Moot: Shoal Peers no longer approve Quick Connect codes; they use invites. | 4.1b |
| B-1 | Crit | Fixed by the grammar; the spike rewriter must not ship. | 6.4, App. B |
| B-2 | High | Fixed: `BoundedReadStream`, deadlines, banned read APIs. | 6.10, 8.8 |
| B-3 | High | Fixed by 8.6. | 8.6 |
| B-4 | High | Fixed: exact forwarded fields. | 6.2.1, 6.11 |
| B-5 | Med | Fixed: Shoal index by canonical StubRoot prefix; empty or invalid StubRoot means nothing is federated; Shoal never reads `.strm` files. | 3.5 |
| B-6 | Med | Fixed by 8.5. | 8.5 |
| B-7 | Med | Fixed: no token in the XML; the admin page talks only to `/Shoal/Admin/*`. | 3.2, 9.1 |
| B-8 | Med | Fixed: route-template table. | 6.1 |
| B-9 | Med | Fixed: limits, breaker, `MaxConnectionsPerServer`. | 6.9, 6.10 |
| B-10 | Low | Fixed: whitelist construction, `no-store`, canary host match on label boundaries. | 6.2.3, 8.6 |
| B-11 | Low | Fixed: catch-all at every handler boundary. | 6.1 |
| B-12 | Low | Fixed: AddressPolicy; DeviceId derived on the server. | 2, 4.4 |
| B-13 | Low | Accepted for lab-only tooling. Chore for Pilot: secrets via stdin or env, `umask 077`, quoted heredocs. `lab/` is excluded from the public repo anyway. | 11.2 note, 11.5 |
| B-14 | Low | Release checklist: scrub or exclude `lab/` and internal names; rewrite the author email; decide on `.claude/`. | 11.5 |

### Not adopted, or adopted in part (no finding is rejected outright)

- **W§A-3, `Cross-Origin-Resource-Policy: same-origin`: not sent.** Direct play through `<video src>` is a no-cors request. CORP same-origin would block legitimate clients on another origin, such as the Chromecast receiver and separately hosted web clients. The token is the gate here. Everything else in A-3 is adopted.
- **W§A-7, TLS leaf-key pinning: opt-in, not default.** Common ACME clients generate a new key at renewal by default, so a default pin would break sharing with HTTPS friends every 60-90 days. Default protection for Public peers is CA validation plus the HTTPS requirement.
- **W§A-9, per-share transcode cap:** possible only when the Peer runs Shoal (8.4). A stock Peer has no code of ours. There, the honest Home's limits (6.9) and revocation are the controls. Accepted risk.
- **W§A-1 fix 2, "invite burned after 3 failures":** a wrong secret cannot be attributed to one invite, so v2 cancelled **all** active invites after 3 failures. **Withdrawn in v2.1 (W2§2d, N-4):** that handed an anonymous caller a way to disable pairing. The failure-only limiter of 4.1b replaces it.
- **W§A-2 tag allowlist:** narrowed further. `EXT-X-BYTERANGE` and the `AUDIO` attribute are not emitted by core for these routes, so they are dropped too.

---

## 0. Decisions in one screen

1. **Library entry = `.strm` + `.nfo` + local images. Delivery = interception.** A global MVC resource filter on Home takes over every media route for federated items: `PlaybackInfo`, `/Videos/{id}/stream`, subtitle routes, `Download` and `File`. It forwards to the Peer with a rebuilt DeviceProfile, so the **Peer transcodes**, and rewrites every URL so the client only talks to Home. Federated requests never fall through to core.
   - Proven: spike (K§Evidence 1-4) plus the owner's browser test, where play, seek, forced Peer transcode and subtitles all passed.
   - **Direct play uses a `Protocol=File` source with `Path=null`.** Every client then builds the standard `/Videos/{localId}/stream.{container}?Static=true` URL that the filter intercepts (6.2).
2. **The `.strm` holds a dead sentinel:** `http://shoal.invalid/v1/<stubKey>`. Reason: the scan copies `.strm` content into `ShortcutPath` (`ProbeProvider.cs:322-329`), and item DTOs expose it as `MediaSources[].Path` (`DtoService.cs:408-410`). If the plugin is disabled, federated items are inert and leak nothing.
3. **Peer ids live only in Home's admin-only catalog files** (`peers/<peerKey>/catalog.json`, 0600). They never appear in stubs, NFO, logs at Info or above, responses to non-admins, playlists or headers. Peer GUIDs are bearer-equivalent because the Peer's static stream route is anonymous (S§5.4, K§Surprises 1).
4. **Peer credential = one token for a non-admin share user, locked to Home's device id and capped at one session** (core's device lock has a verified gap for non-persistent devices, and on 12.1 the account can also reset its own password; a stock Peer narrows this only with `MaxActiveSessions=1`, Shoal Peers close it in code, 4.1, 8.4). Two pairing paths: Quick Connect for a stock Peer (MVP default, friend installs nothing), or invite plus SAS when the Peer runs Shoal. Home refuses admin tokens and logs them out. API keys are never accepted (S§5.1).
5. **State and secrets are Shoal's own files.** Topology goes in `state.json` (atomic, `.bak`, dailies) and tokens in `secrets.enc` (AES-256-GCM). The plugin XML holds only toggles, because core silently replaces it with defaults on any read error (`BasePluginOfT.cs:184-198`). Encryption at rest is protection against accidental disclosure, not against code running as the Jellyfin user (8.2).
6. **Peer auth goes only in the `Authorization` header.** Home forwards nothing from clients except the exact, validated fields in 6.11: no client header, query string, body, token, device name or IP.
7. **Every peer call is bounded**: connect, header, idle-read and total deadlines; a byte cap through one `BoundedReadStream`; JSON depth 32; a per-peer circuit breaker. Library scans never touch the network. An offline Peer only makes *its own* titles fail, and they fail fast.
8. **Default-deny visibility with one switch (owner D3/D4).** A peer library starts in "Only selected users" mode with only the pairing admin selected. "Everyone on this server" opens it to every user, including users created later; switching back restores the selection. CallerContext evaluates the mode on every request, so playback is correct even if core library access drifts; `VisibilityService` keeps core library access in line so browsing matches (5.6). Peer metadata, ratings included, is untrusted input.
9. **Peer output is hostile input.** Playlists pass a closed grammar (6.4). Every Shoal response carries Shoal's own Content-Type and security headers (8.6). PlaybackInfo responses are built from scratch and checked by a canary.
10. **CallerContext is the only way to authorize** a federated request on routes core leaves anonymous (8.5). `GetAuthorizationInfo` is banned.
11. **Local wins.** Dedup by TMDb, then IMDb, then TVDb, with exact title+year as a weak fallback. Guards hold mass deletions and mass additions.
12. **Progress and watch state are recorded on Home** against the local stub item. The Peer gets start, ping and stop for transcode cleanup. It sees titles, never Home usernames, IPs or device names (owner D8).
13. **Speed verdicts use 1.5x headroom** over the title's real bitrate; auto-fit clamps `MaxStreamingBitrate` to `measured / 1.5` (phase 4).
14. **Revocation, said honestly.** Core serves the Peer's raw static and subtitle routes anonymously, and a share-user token can mint more tokens (Quick Connect, or a password reset). On a stock Peer **deleting the sharing account is the only clean revoke**; disabling or pausing is best-effort, then the network share is removed (4.5). Shoal on the Peer adds a guard filter: `Enforce` for a fresh install with one-click `LogOnly` (owner D18, superseding D9; an existing state keeps its stored mode), and bans on the self-minting routes for share users.
15. **Runtime comes from the plugin, not the NFO** (`MetadataService.cs:1260-1268`). `StubEnricher` writes runtime and technical facts after scans.
16. **Exactly one media source per federated item in MVP.** Peer alternate versions are dropped. Exposing them is phase 6, designed in v2.6.4 (K28, K29).
17. **Fallback hook (contingency only):** an `IStartupFilter` path rewrite (section 10).
18. **Clients (v2.2, owner D11; G1 confirmed by owner D12).** Supported: Moonfin on every platform (iOS, iPadOS, tvOS, Android, Android TV, Fire TV, desktop, web, Tizen, webOS, Vega, Xbox, Roku), jellyfin-web, Jellyfin Media Player, and the official Jellyfin Android app's built-in player. Not supported in MVP: apps that request videos without credentials (official Jellyfin Android TV, Swiftfin, Findroid, external players). Shoal answers them 401 and never serves a friend's title anonymously (6.12).

---

## 1. Overview and architecture

```
                         HOME server (Shoal, Home role)                                     PEER server
 +----------+   local   +--------------------------------------------------------+  tailnet / +--------------------------------+
 | client   |  token    |  Jellyfin core                                         |  https     |  Jellyfin core (stock, or      |
 | web,     |---------->|   +--------------------------------------------------+ |  header    |  + Shoal in Peer role)         |
 | Moonfin, |           |   | ShoalInterceptFilter (MVC resource filter)       | |  token     |                                |
 | mobile,  |<----------|   |  route in table AND item in Shoal index?         | |----------->|  /Items/{pid}/PlaybackInfo     |
 | TV       |  only     |   |    no  -> next() (core, zero I/O)                | |            |  /Videos/{pid}/stream?static   |
 +----------+  Home     |   |    yes -> CallerContext -> handler, never next() | |            |  /Videos/{pid}/master|main.m3u8|
               URLs     |   +--------------------------------------------------+ |            |  /Videos/{pid}/hls1/main/...   |
                        |        |             |              |                  | |            |  /Items, /UserViews, Images    |
                        |  PlaybackInfo   StaticStream    Subtitle               | |            |  /System/Info[/Public], Ping   |
                        |    Handler        Handler        Handler               | |            |  /QuickConnect/*, /Users/Me    |
                        |        |             |              |                  | |            |  /Sessions, /Sessions/Playing* |
                        |  GrantStore   HlsProxyController (/Shoal/Hls/*)        | |            |                                |
                        |        |      PlaylistGrammar, ResponseWriter          | |            |  share user: hidden, non-admin,|
                        |        v                                               | |            |  EnabledFolders = shared libs, |
                        |  PeerClient (per peer: SocketsHttpHandler, no redirects,| |            |  EnabledDevices = [Home]       |
                        |   AddressPolicy, optional proxy, BoundedReadStream,    | |            |                                |
                        |   deadlines, CircuitBreaker, trust window) <-----------+-+            |  [Shoal Peer role only:]       |
                        |        ^              ^                ^               |              |  POST /Shoal/Pair (invite)     |
                        |  CatalogSync     SpeedTest        HealthMonitor        |              |  PeerGuardFilter: hardening,   |
                        |  (IScheduledTask) (phase 4)       (IHostedService)     |              |  Initiate shield, transcode cap|
                        |        |                                               |              +--------------------------------+
                        |  StubWriter --> <StubRoot>/<peerKey>/<lib>/Title (Y) [tmdbid-N]/{.strm,.nfo,poster.jpg,backdrop.jpg}
                        |  StateStore, SecretStore, CatalogStore --> <DataPath>/shoal/ (admin-only files)
                        |  VisibilityService (access modes), PeerSessionReporter
                        |  AdminController (/Shoal/Admin/*, RequiresElevation) + config page
                        +--------------------------------------------------------+
```

The plugin is one DLL with two roles: Home duty ("Watch a friend's library") and Peer duty ("Share your library with a friend"); `docs/ui-spec.md` shows both on one Overview. A friend who runs Shoal can do both, which gives two-way sharing through two independent pairings. The Home role works against a **stock** Peer (owner D7).

### 1.1 Pairing flows

> **v2.8 (D17): flow (a) is retired.** Quick Connect pairing no longer exists; a legacy peer is paused and re-paired by invite (4.1). The diagram stays as history of the v2.7 and older design. Flow (b), the invite, is the only pairing flow (4.1b).

**(a) Quick Connect, stock Peer (retired in v2.8).** Details and residual risk were in 4.1, 4.2 and 4.3 up to v2.7.

```
Peer admin (stock)               Peer server                      Home server (Shoal)               Home admin
 | share user per checklist 4.1  |                                 |  Add peer: URL, network, proxy  |
 | Quick Connect enabled         |<--- GET /System/Info/Public ----|<----------- Check --------------|
 |                               |---- Name, Version, Id --------->| Id prefix shown (accident check)|
 |                               |<--- GET /QuickConnect/Enabled --|  (admin clicks "Start pairing") |
 |                               |<--- POST /QuickConnect/Initiate | Device="Shoal: <Home> (K7QM)"   |
 |                               |---- {Code, Secret} ------------>| Code + check shown only after   |
 |                               |<--- first poll: same Code+Secret| the baseline poll (4.2a) ------>|
 |<----------------------- Code by Signal / phone / in person (out of band) -----------------------|
 |                               |<--- GET /QuickConnect/Connect?secret, every 1 s ----------------|
 |                               |     404 after baseline: one redeem attempt, else ABORT (hijack) |
 | signs in as share user once,  |                                 |                                 |
 | Settings > Quick Connect, Code->| AuthorizeRequest(user, Code)  |                                 |
 |                               |<--- POST /Users/AuthenticateWithQuickConnect {Secret} ----------|
 |                               |---- AccessToken --------------->| /Users/Me: refuse admin, policy |
 |                               |                                 | warnings; pin; token IN MEMORY  |
 |                               |                                 | (AwaitingConfirm: nothing saved)|
 | Dashboard > Devices: exactly  |                                 |                                 |
 | one "Shoal: <Home> (K7QM)" <--------------- compare check K7QM by phone ------------------------>|
 |                               |                                 | "codes match" -> Confirm gate:  |
 |                               |                                 | SecretStore.Put, then libraries |
 | lock share user to that device->| DeviceAccessHost logs out other persistent devices (4.1)      |
 | session limit = 1 (step 9)    |                                 |                                 |
 |                               |<--- GET /Users/Me + anchor (60 s)| lock and limit OK              |
```

**(b) Invite, Shoal on both sides (optional).** Details in 4.1b.

```
Peer admin (Shoal)        Peer server (Shoal)                         Home server (Shoal)               Home admin
 | Shares > New share ---->| share user (random pw, hidden, policy)   |                                 |
 | Invite --------------->| secret 128 bit, 15 min, stores SHA-256    |                                 |
 |<-- "shoal1-<peerId8>-<secret>" shown once                          |                                 |
 |--------------------------------- invite by Signal / in person ------------------------------------->|
 |                         |<--- GET /System/Info/Public -------------| Id prefix == invite prefix?     |
 |                         |<--- POST /Shoal/Pair {invite, homeNonceCommit, homeServerId, homeName, homeDeviceId}
 |                         |---- {pending, peerNonce, peerServerId} ->| (tailnet or https only)         |
 |                         |<--- polls every 2 s, now with homeNonce -| SAS = H(...) shown "XXXX XXXX"  |
 | "Bob's Home wants to    |                                          |                                 |
 |  connect. Type the code from Bob's screen: [____ ____]" <--- Home admin reads the SAS by phone ----->|
 | types it, Approve ----->| server compares (3 tries); signs out old |                                 |
 |                         | devices; EnabledDevices=[homeDeviceId]; AuthenticateDirect(shareUser, ...) |
 |                         |<--- POST /Shoal/Pair (same body, every 2 s)                                |
 |                         |---- {approved, token} (exactly once) --->| /Users/Me checks; SecretStore   |
```

### 1.2 Catalog sync flow (one-way, movies)

```
CatalogSyncTask (every 6 h, or admin "Sync now")
  for each enabled peer inside its trust window (pin + auth confirmed <= 120 s) with a closed circuit:
    A. GET /System/Info              -> Id must equal the pinned Id, else IdentityMismatch, abort
       GET /UserViews                -> mapped libraries still visible? else hold
       GET /Items?ParentId=..&Fields=ProviderIds,Etag (paged, cheap)  -> full id list
    B. diff vs catalog.json (new / changed by Etag / gone / moved by provider id)
       dedup vs local movies (local wins), cross-peer priority
       mass-deletion guard, new-items guard, entity caps
    C. GET /Items?Ids=..(batch 50)&Fields=<details>   for new + changed (sanitized per 5.5)
       GET /Items/{pid}/Images/{Primary|Backdrop}?format=Jpg&maxWidth=..   (serial, JPEG/PNG only, quota)
    D. write stubs atomically under <StubRoot>/<peerKey>/<lib>/   (local disk only)
    E. save catalog.json, ILibraryMonitor.ReportFileSystemChanged(changed folders), overlap report
```

### 1.3 Playback flow (direct play and HLS transcode)

```
client                         Home (filter + plugin)                                Peer
  | POST /Items/{L}/PlaybackInfo   |                                                    |
  |  (DeviceProfile, bitrate, ...) | L in Shoal index -> CallerContext (8.5)            |
  |------------------------------->| admitted? visible? limits? trust window?           |
  |                                | POST /Items/{P}/PlaybackInfo (body built from ---->|
  |                                |   6.2.1 only; rebuilt DeviceProfile; header token) | peer StreamBuilder decides
  |                                |<-------------- MediaSources[0], PlaySessionId PS --| DirectPlay or Transcode
  |                                | Grant g = {user, L, P, PS, peer master query}      |
  |<-------------------------------| rebuilt: Id=L, Protocol=File, Path=null,           |
  |  PlaySessionId = local ps      |  TranscodingUrl=/Shoal/Hls/g/master.m3u8?ApiKey=<caller>,
  |                                |  DeliveryUrl=/Shoal/Subtitles/L/..; canary         |
  |                                |                                                    |
  | DIRECT: GET /Videos/{L}/stream.mkv?Static=true&mediaSourceId=L&ApiKey=..  Range: bytes=x-
  |------------------------------->| CallerContext + slot -> GET /Videos/{P}/stream?static=true&mediaSourceId=..
  |<======= bytes (206) ===========|<========== single Range, header whitelist =========|
  |                                |                                                    |
  | HLS: GET /Shoal/Hls/g/master.m3u8?ApiKey=..                                         |
  |------------------------------->| [Authorize], g.user == caller -> GET /Videos/{P}/master.m3u8?<peer's own query>
  |<-- grammar output: "main.m3u8?ApiKey=<caller>", "subs/N/subtitles.m3u8?ApiKey=<caller>"; all else dropped
  | GET main.m3u8 -> GET hls1/main/7.mp4?ApiKey=..&runtimeTicks=..&actualSegmentLengthTicks=..
  |------------------------------->| -> GET /Videos/{P}/hls1/main/7.mp4?<stored variant query>&runtimeTicks=r&actual..=l
  |<======= segment ===============|<===================================================| peer ffmpeg
  | POST /Sessions/Playing{,/Progress,/Stopped} (core records progress on L for the local user)
  |                                | PeerSessionReporter: start / ping(<=1 per 20 s) / stop(PS) -> peer
```

### 1.4 Speed test flow (phase 4)

```
SpeedTestTask (every 6 h, skipped while that peer is streaming) or admin "Test now"
  1. 10 x GET /System/Ping on one keep-alive connection, 200 ms apart -> connectMs, median, p90, jitter, loss
  2. GET /Playback/BitrateTest?size=1000000 (warm-up, discarded)
     GET /Playback/BitrateTest?size=8000000 ; if < 1.5 s then size=32000000 -> Mbps (first byte to last byte)
     ("Test now / deep": also 2 parallel x 16 MB, and an optional 20 s peer transcode probe)
  3. optional: Tailscale LocalAPI /localapi/v0/status -> direct vs DERP relay
  4. append to speed.jsonl, recompute verdicts + bottleneck hints, update auto-fit bitrate cap
```

---

## 2. Components

All components live in one assembly, `Jellyfin.Plugin.Shoal`. Role: H = Home, P = Peer, both = either.

| # | Component | Role | Responsibility | Jellyfin 12.1 interfaces used (refs) |
|---|---|---|---|---|
| 1 | `Plugin` | both | Identity, config pages (Peers, Shares, Hardening, Settings) | `BasePlugin<PluginConfiguration>`, `IHasWebPages` (S§1.1) |
| 2 | `PluginServiceRegistrator` | both | Registers singletons, hosted services, `ShoalInterceptFilter` and `PeerGuardFilter` via `services.Configure<MvcOptions>(o => o.Filters.Add<...>())`. Both filters are O(1) no-ops when their role has nothing configured. | `IPluginServiceRegistrator` (S§1.3); `Program.cs:172`, `ApplicationHost.cs:462-492` |
| 3 | `SettingsValidator` | both | Validates the XML toggles (3.2) on load and on admin writes. It **never throws**: it clamps and logs once. Core resets the XML on any load exception (`BasePluginOfT.cs:184-198`). | `BasePlugin.UpdateConfiguration` override |
| 4 | `StateStore` | both | `state.json`: topology and security settings (3.2a). Atomic write, `.bak`, 7 daily copies, recovery mode, startup reconciliation (3.7). Single writer. | `IApplicationPaths.DataPath` |
| 5 | `SecretStore` | H | AES-256-GCM, one sealed message per peer (`{token, deviceId, pairedUtc, method}`), bound to that peer's key and host (8.2). Key from `JELLYFIN_SHOAL_KEY` or `JELLYFIN_SHOAL_KEY_FILE` (32 random bytes, validated), DPAPI `CurrentUser` (Windows) or a 0600 key file (8.2). | `IApplicationPaths.DataPath` |
| 6 | `PeerRegistry` | H | Runtime state per peer (`Online`, `Degraded`, `Offline`, `AuthFailed`, `IdentityMismatch`, `TlsKeyChanged`, `Disabled`, `Pairing`, `PairingAborted`), circuit breaker, trust window (4.4). | none |
| 7 | `PeerClient` | H | Typed peer calls. Exact header builder (6.11), per-peer `SocketsHttpHandler`, `BoundedReadStream` and `CallBudget` deadlines (6.10), JSON depth 32. **Not** `IHttpClientFactory`, to avoid framework URL logging. | `System.Net.Http` only |
| 8 | `AddressPolicy` | H | SSRF guard for admin-entered URLs (8.3 T11). It normalizes the host and rejects userinfo, backslashes, non-punycode hosts and decimal, octal or hex IPv4. It classifies by network type, checks every resolved IP in `ConnectCallback` (direct mode), and in proxy mode accepts only Tailnet IP literals or `*.ts.net` names. | none |
| 9 | `PairingService` | H | One pairing session per Home. Quick Connect state machine with hijack detection, response classification and the server-side confirm gate (4.2a, 4.3); invite redemption with commit-reveal SAS (4.1b); admin refusal, policy checks, identity pin, re-pair, self-logout on removal. | Peer `/QuickConnect/*` (S§5.2), `/Users/AuthenticateWithQuickConnect`, `/Users/Me` (`UserController.cs:581`), `/Sessions/Logout` (`SessionController.cs:427`), `POST /Shoal/Pair` |
| 10 | `ShareService` | P | Create, lock, repair and revoke share users; invites and approvals with the typed safety code; refuses libraries under StubRoot. Its share users are never admitted to a Shoal library (5.6). | `IUserManager.CreateUserAsync/UpdatePolicyAsync/DeleteUserAsync`, `IUserManager.OnUserUpdated`, `ISessionManager.AuthenticateDirect` (`SessionManager.cs:1695`), `ISessionManager.Logout(Device)`, `IDeviceManager.GetDevices` |
| 11 | `PairController` | P | `POST Shoal/Pair`: the **only** anonymous action in Shoal (4.1b). | `ControllerBase`, `[AllowAnonymous]`, `[RequestSizeLimit(1024)]` |
| 12 | `CatalogSyncService` + `CatalogSyncTask` | H | Section 5 | `IScheduledTask` (S§1.6), `ILibraryManager.GetItemList` (`ILibraryManager.cs:651`), `ILibraryMonitor.ReportFileSystemChanged` (`LibraryMonitor.cs:374-415`), `ILibraryManager.AddVirtualFolder` (`:724`) |
| 13 | `StubWriter` (`PathSanitizer`, `NfoWriter`, `ImageFetcher`) | H | Safe names, XML via `XmlWriter`, JPEG/PNG images, disk quota, atomic writes, confined deletes | `IFileSystem` |
| 14 | `MetadataSanitizer` | H | Per-field rules for catalog metadata (5.5) and `MediaStreams` (6.2.3). It is the only path from peer JSON to anything Home writes or returns. | none |
| 15 | `CatalogStore` | H | `peers/<peerKey>/catalog.json` load and atomic save | none |
| 16 | `ShoalIndex` | H | `HashSet<Guid>` of federated local item ids plus maps to stub records. Rebuilt in the background at startup and after scans; maintained by item events. | `ILibraryManager.ItemAdded/ItemUpdated/ItemRemoved` (`ILibraryManager.cs:36-46`), `FindByPath` |
| 17 | `StubEnricher` | H | After a scan, writes `RunTimeTicks`, `Container`, `Size`, `Width`, `Height` and `TotalBitrate` from the catalog onto stub items (`UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit)`), only when a value differs. **Never** writes stream titles or languages (W§A-12). Also detects merged versions (6.1). | `ILibraryPostScanTask`; reasons: `MetadataService.cs:1260-1268`, `FFProbeVideoInfo.cs:87,198-205` |
| 18 | `DedupService` | H | Index of local non-federated movies by provider id and size, rebuilt per sync | `InternalItemsQuery` |
| 19 | `VisibilityService` | H | Access modes and selections; reconciles core library access with them (5.6); drift alerts; new-user handling | `IUserManager.UpdatePolicyAsync`, `IUserManager.OnUserUpdated`, `IEventConsumer<UserCreatedEventArgs>` (published at `UserManager.cs:365`), `ILibraryPostScanTask` |
| 20 | `ShoalInterceptFilter` | H | `IAsyncResourceFilter`: route-template table (6.1) to handler. O(1) passthrough otherwise. Catch-all at the boundary. | ASP.NET Core MVC; templates in 6.1 |
| 21 | `CallerContext` | H | The only authorization path for federated requests (8.5) | `HttpContext.User`, `IAuthorizationService`, `IAuthorizationPolicyProvider`, `IUserManager.GetUserById`, `ILibraryManager.GetItemById<T>(Guid, User)` |
| 22 | `PlaybackInfoHandler` (+ `DeviceProfileRebuilder`) | H | 6.2: forward, rebuild, grant, canary | `MediaBrowser.Model.Dto.PlaybackInfoResponse`, `MediaSourceInfo`, `DeviceProfile` |
| 23 | `StaticStreamHandler` | H | Range and HEAD proxy (6.3) | none (pattern of `FileStreamResponseHelpers.cs:28-96`) |
| 24 | `SubtitleHandler` | H | Subtitle-file proxy for external and embedded text tracks, JSON rebuild, extraction coalescing (6.6, 6.6a) | none |
| 25 | `GrantStore` | H | In-memory grants; bounded; idle and absolute expiry (3.5) | none |
| 26 | `HlsProxyController` + `PlaylistGrammar` | H | `[Authorize]` HLS endpoints and the closed grammar (6.4) | `ControllerBase` |
| 27 | `ResponseWriter` | H, P | Writes every Shoal response: Content-Type from the closed table, security headers (8.6). Handlers cannot set Content-Type themselves. | none |
| 28 | `PeerSessionReporter` | H | Start, ping and stop to the Peer; keepalive for paused HLS; scoped kill (6.8) | `ISessionManager.PlaybackStart/PlaybackProgress/PlaybackStopped` |
| 29 | `Limits` | H | Slots and `System.Threading.RateLimiting` partitioned limiters (6.9) | none |
| 30 | `SpeedTestService`, `SpeedHistoryStore`, `VerdictEngine` | H | Section 7 (phase 4) | `IScheduledTask` |
| 31 | `TailscaleLocalApi` (optional) | H | Read-only `status` over the Unix socket when present (phase 4) | none |
| 32 | `HealthMonitor` | H | Every 60 s per peer: unauthenticated pin check, then one authenticated call (`GET /Users/Me`), plus the session anchor for peers paired by Quick Connect (4.1, 6.11), also sent at startup and on the first successful contact after any outage (v2.3, W4-5); every 10 min a foreign-device check (4.3). Drives the breaker and trust window. Sends offline notices (5.7). | `IHostedService`, `ISessionManager.SendMessageCommand` (`SessionManager.cs:1273-1291`) |
| 33 | `AdminController` | both | `/Shoal/Admin/*`, all `[Authorize(Policy = Policies.RequiresElevation)]` | `MediaBrowser.Common.Api.Policies` (S§1.5) |
| 34 | `PeerGuardFilter` | P | Hardening of core's anonymous media routes (`LogOnly`/`Enforce`/`Off`), `QuickConnect/Initiate` shield, best-effort per-share transcode limit, bans on share users calling `QuickConnect/Authorize`, `Users/Password` and `Sessions/Capabilities*` (8.4) | `EndpointDataSource`, `IAsyncResourceFilter`, `ITranscodeManager.GetTranscodingJob` |
| 35 | `Redact` | both | Logging helpers: peers by display name, ids hashed to 8 hex; never tokens, invites or URLs with queries | `ILogger` |

**Plugin HTTP surface (complete).** Every action carries an auth attribute. `[AllowAnonymous]` appears exactly once, on `PairController.Pair`. A reflection test enforces both. The reflection test cannot see intercepted core routes, which is why T-SEC-15 exists (8.7).

| Route | Auth | Role |
|---|---|---|
| `GET Shoal/Hls/{grantId}/master.m3u8`, `main.m3u8`, `hls1/main/{segmentId}.{ext}`, `subs/{index}/subtitles.m3u8`, `subs/{index}/stream.vtt` | `[Authorize]`, CallerContext steps 1-4 on every request, grant owner == caller, steps 5-7 at grant creation and every 5 min | H |
| `GET Shoal/Subtitles/{localItemId}/{index}/Stream.{format}` | `[Authorize]` + full CallerContext | H |
| `* Shoal/Admin/Status`, `Peers...`, `Pairing...`, `Libraries...`, `Settings`, `State...`, `All` (full list in 9.6) | `RequiresElevation` | H |
| `* Shoal/Admin/Shares...` (incl. invites, approval, devices), `Shoal/Admin/Hardening` | `RequiresElevation` | P |
| `POST Shoal/Pair` | **anonymous by design**: the invite secret is the credential; limits in 4.1b | P |
| `POST Shoal/Share/Rotate` (phase 5) | `[Authorize]` + caller is a share user; wire and rules in v2.6.1 (K25) | P |
| Intercepted core routes (6.1) | core's own attribute + CallerContext | H |
| `PeerGuardFilter` on core's anonymous media routes and `QuickConnect/Initiate`; bans on a few authenticated routes for share users (8.4 job d) | adds requirements, never removes one | P |

The route prefix is `Shoal/` at the top level, not `Plugins/Shoal/`, which is ambiguous with `PluginsController`'s `Plugins/{pluginId}/{version}` templates.

---

## 3. Data model

### 3.1 Files on Home and Peer

```
<DataPath>/shoal/                    DataPath = IApplicationPaths.DataPath; local volume; dirs 0700, files 0600
  state.json             topology + security settings (3.2a); the source of truth
  state.json.bak         previous good state (File.Replace backup)
  backups/state-YYYYMMDD.json   first write of each day; last 7 kept
  master.key             32 random bytes (absent with env JELLYFIN_SHOAL_KEY or JELLYFIN_SHOAL_KEY_FILE; DPAPI-wrapped on Windows)
  secrets.enc            AES-256-GCM, one sealed message per peer: {schema, peers: {peerKey: {nonce, ciphertext}}}; plaintext {token, deviceId, pairedUtc, method: qc|invite}; AAD binds peer key and host (8.2)
  peers/<peerKey>/
    catalog.json         sync state + stub mapping (contains peer GUIDs: admin-only file)
    health.json          last health/sync/auth status for the UI after restart, and a durable PairingAborted {kind, atUtc} (4.2a)
    speed.jsonl          speed + playback telemetry samples (capped; phase 4)
  peer-role/
    guard-counters.json  hourly aggregated LogOnly counters (8.4)
    guard-lastgood.json  share user ids, shared collection-folder ids and hardening mode as of the last good save; lets the guard keep its bans and mode when state.json is unreadable (3.7, W2§N-9)
  stubs/                 default StubRoot (configurable; must be local disk)
<PluginConfigurationsPath>/Jellyfin.Plugin.Shoal.xml   non-critical toggles only (3.2)
```

Files and directories are created with `UnixCreateMode` (0600/0700) on Linux. If the volume ignores Unix modes (CIFS, NFS, NTFS via FUSE), the Settings screen shows "this volume cannot protect secret files; treat `secrets.enc` and `master.key` as plaintext" (W§A-16). Jellyfin writes its own plugin XML world-readable (K§Surprises 8), which is one more reason nothing sensitive goes there.

**StubRoot validation:**
- It must not be inside, or contain, any existing library path.
- It must be on a local filesystem. On Linux, read `/proc/self/mountinfo` and refuse `cifs`, `smb3`, `nfs*`, `fuse.*` and `9p` unless the admin ticks "I understand". This is the owner's D-state lesson.
- It must not be a symlink or reparse point.
- An empty or invalid StubRoot means **no item is federated** (fail closed, W§B-5).

### 3.2 `PluginConfiguration` (XML): toggles only

```csharp
public class PluginConfiguration : BasePluginConfiguration
{
    public int SchemaVersion { get; set; } = 2;
    public bool AutoFitQualityToLink { get; set; } = true;   // phase 4
    public int MaxStreamsPerUser { get; set; } = 4;           // owner D13 (v2.4; was 2); SettingsValidator clamps to 1..4 (v2.2: keeps a slot-free grant to evict, 3.5)
    public int SyncIntervalHours { get; set; } = 6;
    public int SpeedTestIntervalHours { get; set; } = 6;     // phase 4
    public int MassDeletionMinCount { get; set; } = 25;      // hold if removals > max(MinCount, Percent% of active)
    public int MassDeletionPercent { get; set; } = 10;
    public int NewItemsMinCount { get; set; } = 500;         // hold if additions > max(MinCount, Percent% of active)
    public int NewItemsPercent { get; set; } = 20;
    public int DiskQuotaGbPerPeer { get; set; } = 2;
    public ArtQuality ArtQuality { get; set; } = ArtQuality.Standard; // Standard: poster 600 px, backdrop 1280 px; High: 1000 / 1920
    public bool ParkOfflinePeers { get; set; } = false;      // owner D5
    public int ParkAfterHours { get; set; } = 72;
    public bool NotifyClientsWhenOffline { get; set; } = true; // DisplayMessage, 5.7
    public bool HlsSubtitlesEnabled { get; set; } = true;    // kill switch, 6.2.2
    public string TailscaleSocket { get; set; } = "auto";    // phase 4; "auto", "off" (v2.4) or a socket path
}
```

Rule: the XML holds no secret, no topology, and nothing whose default is less safe than the admin's choice. If core resets the file, only these toggles return to safe defaults. The hardening mode lives in `state.json`, because a reset to the default would silently downgrade `Enforce` to `LogOnly`. The spike's `PeerConfiguration.Token` and `Peers` list are removed (W§B-7). Spike configurations are lab-only and are not migrated: re-pair.

### 3.2a `state.json` (Shoal's own store)

```json
{
  "schema": 2, "generation": 41, "writtenUtc": "2026-10-14T21:00:00Z",
  "stubRoot": "/config/data/shoal/stubs",
  "home": {
    "peers": [{
      "peerKey": "4b0c...", "displayName": "Hops", "baseUrl": "http://hops.tail1234.ts.net:8096",
      "network": "Tailnet", "proxyUrl": null, "enabled": true,
      "pinnedServerId": "<32 hex>", "tlsSpkiPin": null, "lanCleartextAck": false,
      "pairing": { "method": "qc", "checkString": "K7QM", "pairedUtc": "...", "pairedByUserId": "<local admin N>" },
      "maxConcurrentStreams": 4, "maxConcurrentTranscodes": 4, "priority": 100,
      "libraries": [{
        "mappingKey": "...", "remoteName": "Movies", "collectionType": "movies",
        "localLibraryName": "Hops Movies", "subfolder": "movies", "import": true,
        "access": { "mode": "Selected", "selectedUserIds": ["<local user N>"], "includeRestricted": false,
                    "changedUtc": "...", "changedByUserId": "<N>" }
      }]
    }],
    "visibility": {
      "managedUsers": { "<local user N>": { "since": "...", "lastWrittenFoldersHash": "<hex>" } }
    }
  },
  "peerRole": {
    "hardeningMode": "LogOnly", "hardeningSinceUtc": "...",
    "shares": [{
      "shareId": "...", "friendName": "Bob", "shareUserId": "<N>", "libraryIds": ["<N>"],
      "expectedDeviceIds": ["shoal-1a2b3c4d5e6f-9a8b7c6d"],
      "maxBitrateMbps": null, "maxConcurrentTranscodes": 4, "enabled": true
    }]
  }
}
```

`selectedUserIds` is kept unchanged while `mode` is `Everyone`, which is how switching back restores the previous selection (owner D4). `managedUsers` lists accounts whose "all libraries" setting Shoal replaced with an explicit list (5.6), so Shoal can keep them current and undo exactly its own change. Invites and pending approvals are memory-only (a restart cancels them). Peer GUIDs are not in `state.json`; they stay in `catalog.json`. A peer that is still pairing (`Pairing`) or whose pairing was aborted (`PairingAborted`) is stored with `enabled:false` and has no `secrets.enc` entry; startup reconciliation expects that (3.7). `peerRole.shares[].shareUserId` is also what keeps share users out of every access mode (5.6); `StateStore` rewrites `peer-role/guard-lastgood.json` whenever shares or the hardening mode change.

### 3.3 `catalog.json` (per peer, admin-only)

```json
{
  "schema": 2,
  "peerKey": "4b0c...",
  "libraries": [{ "mappingKey": "...", "peerLibraryId": "<peer guid>", "lastCount": 812 }],
  "lastSync": { "startedUtc": "...", "endedUtc": "...", "result": "ok|partial|failed|held|skipped",
                "added": 3, "updated": 1, "removed": 0, "heldRemovals": 0, "heldAdditions": 0, "error": null },
  "heldRemovals": ["k3m9..."],
  "heldAdditions": ["<peer guid>"],
  "usage": { "diskBytes": 0, "people": 0, "genres": 0, "studios": 0 },
  "report": { "overlapCount": 41, "overlapLocalBytes": 0, "overlapPeerBytes": 0,
              "gainCount": 771, "gainBytes": 0, "computedUtc": "..." },
  "entries": [{
    "stubKey": "k3m9q2x7v1tz8c4n",
    "peerItemId": "<peer guid N>", "peerMediaSourceId": "<peer guid N>", "mappingKey": "...",
    "peerEtag": "...", "imageTags": { "Primary": "...", "Backdrop": "..." },
    "providerIds": { "Tmdb": "603", "Imdb": "tt0133093" },
    "title": "The Matrix", "year": 1999,
    "sizeBytes": 0, "bitrate": 0, "container": "mkv", "videoCodec": "hevc", "width": 3840, "height": 2160,
    "runtimeTicks": 0,
    "state": "active|duplicate-local|duplicate-peer|held-removal|held-addition|removed",
    "duplicateOf": null,
    "stubRelPath": "The Matrix (1999) [tmdbid-603]/The Matrix (1999) [tmdbid-603].strm",
    "firstSeenUtc": "...", "lastSeenUtc": "..."
  }]
}
```

Caps: 50,000 entries per peer; beyond that, sync stops with an admin alert. All stored strings are sanitized values (5.5), never raw peer text. The local item id is **not** stored: `ShoalIndex` resolves it with `ILibraryManager.FindByPath(stubPath, false)` after each scan.

### 3.4 Stub layout on disk (client-visible through `Path` and `MediaSources[].Path`)

```
<StubRoot>/<peerKey>/<Subfolder>/                 <- the local library's only path
  The Matrix (1999) [tmdbid-603]/
    The Matrix (1999) [tmdbid-603].strm    "http://shoal.invalid/v1/k3m9q2x7v1tz8c4n\n"
    The Matrix (1999) [tmdbid-603].nfo     see below
    poster.jpg | poster.png               (extension from magic bytes; JPEG or PNG only)
    backdrop.jpg | backdrop.png
```

Everything in that path is admin-chosen (`peerKey` is random), derived from sanitized public metadata, or the random stubKey. Nothing identifies the Peer's host, ids or token. RFC 6761's `.invalid` never resolves, so any fall-through to core fails fast and harmlessly. Pilot measures how long that takes (T-PLAY-12).

**NFO** (written with `XmlWriter`; sanitizing in 5.5):

```xml
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<movie>
  <title>The Matrix</title><originaltitle>The Matrix</originaltitle><sorttitle>Matrix</sorttitle>
  <year>1999</year><premiered>1999-03-30</premiered>
  <plot>...</plot><tagline>...</tagline><mpaa>R</mpaa><rating>8.2</rating>
  <genre>Action</genre><studio>...</studio>
  <uniqueid type="tmdb" default="true">603</uniqueid><uniqueid type="imdb">tt0133093</uniqueid>
  <actor><name>Keanu Reeves</name><role>Neo</role></actor>          (max 5, no thumb)
  <tag>Shoal</tag><tag>Shoal: Hops</tag>
</movie>
```

Never written: `<thumb>`, `<fanart>`, `<trailer>`, `<fileinfo>`, `<set>`, `<lockdata>`, `<website>`, `<runtime>`, or anything containing a URL. `<mpaa>` is written only if the rating is in Jellyfin's rating table (5.5). Runtime and technical facts come from `StubEnricher`.

### 3.5 How items are marked and mapped

| Question | Answer |
|---|---|
| Is local item X federated? | `ShoalIndex.Contains(X)`: an O(1) `HashSet<Guid>` hit. Authoritative rule: `item.Path`, canonicalized, starts with the validated StubRoot plus a separator (case-sensitive on Linux, case-insensitive on Windows). Before the index is ready at startup, the filter applies that rule via `ILibraryManager.GetItemById(Guid)` (cached in core). **v2.3 (Warden-04, Pilot finding):** the index also updates on `ILibraryManager.ItemAdded`/`ItemRemoved` for paths under StubRoot, and on the five routes whose core handler would read the stub (static `stream`, `Audio/{id}/stream`, the subtitle `Stream.{fmt}` routes, `Download`, `File`) an index **miss** still applies the path rule (one cached lookup), so a stub scanned after the last snapshot never falls through to core. **v2.4 (SR§4.1):** the fallback applies on **every** intercepted route except PlaybackInfo, `Videos/ActiveEncodings` and `LiveStreams/Open`: the core HLS, trickplay, attachment and `Audio` HLS handlers also hand the stub path to ffmpeg or the extractor (it failed closed; now it is answered by Shoal). Shoal never reads `.strm` content at runtime. |
| Which peer item? | index: X to `stubRelPath` to `catalog.entries[stubKey]` to (peerKey, peerItemId, peerMediaSourceId) |
| Who may see and play it? | users admitted by the mapping's access mode (`Everyone`, or in `selectedUserIds`) who also pass core visibility (CallerContext steps 5-6) |
| Visible marker | library name `"<Peer> Movies"` plus NFO tags `Shoal` and `Shoal: <Peer>` (usable with stock `BlockedTags`) |
| What clients can see | stub path, sentinel URL with stubKey, local item id, tags. Nothing about the Peer except its display name. |

**Grants** (memory only): `{grantId (128-bit base64url), localUserId, localItemId, callerDeviceId, localPlaySessionId (random 32 hex), peerKey, peerItemId, peerMediaSourceId, peerPlaySessionId, peerMasterQuery (parsed pairs, credentials removed), peerVariantQuery, sanitizedMediaStreams, audioStreamIndex, subtitleStreamIndex, runtimeTicks, playMethod, peerJobStarted (v2.3), createdUtc, lastAccessUtc, lastAuthorizedUtc, telemetry}`, plus `hasSlot`, which is computed, not stored (below).
- **Keyed by (user, item, DeviceId)** (v2.2, G8). **`callerDeviceId` is client-chosen (v2.3, W4-2):** it is the `DeviceId` the client wrote in its `Authorization` header; core uses the token's device row only when the header omits it (`AuthorizationContext.cs:117,146-148`), and nothing compares the two. So it is a **convenience key within one user, never an identity**. Before it is stored or used as a key, a value longer than 128 characters or containing anything outside printable ASCII is replaced by `h:` plus the base64url SHA-256 of the value; an empty one keys by (user, item). Every key, lookup, eviction and replacement below includes the authenticated `localUserId`, so a spoofed DeviceId can only create more keys for the same user, which the cap of 8 bounds; the replacement rule is a courtesy for honest clients, not a control. Clients make several PlaybackInfo calls per play start (Moonfin Smart-TV up to 5, SC§2.2; Moonfin-Core retries on 400/422/500, SC§2.1) and on every audio, subtitle or bitrate switch. A new PlaybackInfo for a key whose existing grant **holds no stream slot** (6.9) replaces that grant (the client uses only the newest answer). A grant that holds a slot (the stream is playing) is kept until its slot is released (`PlaybackStopped`; for a `Transcode` grant the scoped `ActiveEncodings` delete, 6.1; or 2 min idle), so a mid-play switch never cuts the running stream before the client moves.
- **`hasSlot` (v2.3, W4-6).** HLS slots belong to one grant (taken at its first segment request, 6.4); static slots belong to a (user, item) pair (6.3). A grant holds a slot while its own HLS slot is live **or** while the static slot of its (user, item) is live. So when two devices of one user direct-play the same item, every live grant of that pair holds the slot, and none of them is replaced or evicted mid-stream; the subtitle lookup of 6.6a step 4 keeps working for each.
- **`peerJobStarted` (v2.3, W4-4)** is set when Home forwards the grant's first HLS segment request (that is when the Peer starts ffmpeg) and cleared when Home sends the scoped `ActiveEncodings` delete.
- At most **8** per user (v2.1: 4). **Order of the capacity checks (v2.3, W4-6):** (1) a slot-free grant with the same key is replaced (net zero); else (2) if the user already has 8, that user's oldest **slot-free** grant is evicted; if none of the 8 is slot-free, the new grant is **refused** with `RateLimitExceeded`, and a grant holding a slot is never evicted or replaced; else (3) the global cap below. `MaxStreamsPerUser` is clamped to **1..4** (v2.3: `SettingsValidator.cs:35` clamps it to 1..10 today and changes in K14's PR), which normally leaves at least 4 slot-free grants, but the refusal rule makes eviction safe whatever the clamp or the shared static slots do.
- At most 200 in total (unchanged), checked **last**, so a user who would only replace or evict their own grant is never refused because the server is full. When full, new grants are refused with `RateLimitExceeded`; other users' grants are never evicted (W§A-5).
- Idle expiry: 2 h for direct play (used only for session reporting), 10 min after the last request for HLS. **Absolute lifetime 12 h** (W§A-19).
- `lastAuthorizedUtc`: HLS requests re-run CallerContext steps 5-7 when it is older than 5 min (W§A-19).
- A Home restart drops grants; the player errors and the user presses Play again.

### 3.6 Speed history (`speed.jsonl`, phase 4)

One JSON object per line, capped at 2,000 lines (compacted when it exceeds 2,500):

```json
{"t":"2026-10-14T21:00:00Z","kind":"light|manual|deep|playback","rttMedianMs":23.1,"rttP90Ms":31.0,
 "jitterMs":4.2,"lossPct":0,"connectMs":41,"mbps":87.3,"mbpsParallel":null,"bytes":8000000,
 "durationMs":733,"path":"direct|derp|derp?|proxy|unknown","derpRegion":null,
 "transcodeSpeed":null,"segTtfbMs":null,"error":null}
```

### 3.7 Persistence, recovery and backup (W§A-14, W§A-16, W2§N-9)

**Write** (`StateStore`, `CatalogStore`, `SecretStore` share one helper):
1. One writer at a time (`SemaphoreSlim`), and `generation` is incremented.
2. Serialize to `<name>.tmp-<random>` in the same directory, created 0600.
3. `fsync` the file, then `File.Replace(tmp, name, name + ".bak")`, which is an atomic rename on the same volume. On Linux, `fsync` the directory as well. (Derived, not executed: on Unix .NET implements the backup argument of `File.Replace` with a hard link, which throws on filesystems without hard links, such as some CIFS or NFS mounts used for `/config` in containers.) `AtomicFile.Write` therefore degrades to: copy the current file to `.bak`, then rename the temp file over the target. It reports "backup by copy" on the Settings screen and never fails the save for this reason (W2§N-9.3).
4. On the first write of a UTC day, also copy to `backups/state-YYYYMMDD.json` and delete copies older than the newest 7 (state only; catalogs are rebuildable by sync).

**Read and recovery:**
1. Parse `state.json`. On failure, try `state.json.bak`, then the newest daily copy. The first one that parses wins, and the admin sees "Shoal restored its settings from <file>". **A restore from `.bak` or a daily copy can return older, wider settings** (a library tightened from `Everyone` to `Selected` after the backup, a revoked share, `Enforce` back to `LogOnly`). So after any restore other than from `state.json` itself, Shoal loads **every access mode as `Selected`** (the selections are kept), takes the stricter of the restored and the `guard-lastgood.json` hardening mode, and shows "Settings restored from <time>: review who can see each library and which friends can connect" until the admin clicks Confirm (T-REG-5). Confirm is `POST Shoal/Admin/State/ConfirmRestore` (v2.2, 9.6): it clears `restoredFrom.needsConfirm` and changes nothing else, so the access modes stay `Selected` until the admin switches each library back; the banner is not dismissible in the browser alone. A share whose user no longer exists is dropped at reconciliation.
2. If none parses, Shoal enters **Recovery mode**. Every peer is treated as disabled, so federated titles fail fast with `NotAllowed`. The Peer guard filter reads `peer-role/guard-lastgood.json` (3.1): the share user ids, the shared collection-folder ids and the hardening mode as of the last good save. So the bans on share users (8.4 job d) and `Enforce` stay in force while share users and their tokens still exist. If that file is unreadable too, the filter has no share list; the banner says so plainly ("Peer protection is off until you recover"). All screens show a banner. Shoal **never** writes a fresh state over unreadable files. The admin chooses "Restore from file", or "Start over", which moves the files aside as `*.corrupt-<timestamp>`.
3. A newer `schema` than this build understands is treated as unreadable (recovery mode), never downgraded.

**Startup reconciliation** (read-only, results in the admin UI, nothing auto-deleted):
- each **paired** peer in `state.json` (not one in `Pairing` or `PairingAborted`) has a `secrets.enc` entry, a `peers/<peerKey>/` folder and a library whose only path is its stub folder;
- orphans in any direction are listed with "Relink" or "Delete" actions;
- a Jellyfin library whose path lies under StubRoot but has no peer means "Restore detected: re-pair or remove this library".

**Backups.** Jellyfin's built-in 12.x backup copies only the top-level `*.xml`/`*.json` of the config directory, `users`, `ScheduledTasks`, `Root`, and `data/{collections,playlists,ScheduledTasks,subtitles,trickplay,metadata}` (`BackupService.cs:407-448`). It includes the **library definition** (`Root`) but **not** `<DataPath>/shoal/` or the plugin XML. After a restore, the library exists with no stubs and no peers, and the startup check offers re-pair. That works in Shoal's favor: tokens do not ride along in exported backups. The README tells admins to include `<DataPath>/shoal/` in their own file backups. It also warns that `secrets.enc` plus `master.key` together equal a plaintext token, and that the env-key mode avoids that.

---

## 4. Pairing, token lifecycle and revocation

### 4.1 Legacy QC peers (paused, re-pair by invite)

Up to v2.7 a friend who had only stock Jellyfin could be paired by Quick Connect (QC): the friend made a limited sharing account, entered a six digit code on their own Jellyfin, and Home held that account's token. D17 (2026-10-16) removed it: both servers need Shoal, and nobody used the QC route. The long checklist for the friend (account policy, device lock, session limit, the session anchor and the lab evidence behind them) is not part of the design any more; it is in git history and in the "Changes in v2.1" to "v2.4" sections.

What remains is the handling of **legacy QC peers**, peers whose stored pairing method is `qc`:
- **They load and are paused.** Libraries, stubs, catalog and mappings stay. The peer is set to `Enabled=false` with the status "needs pairing again". Its titles stop playing, like any paused server. Nothing contacts the friend's server for it.
- **Overview** shows the legacy banner (v2.8.3) and the server detail shows **Pair again with an invite**.
- **Re-pair by invite** is the normal invite flow (4.2) on the same peer record: libraries and mappings are kept, the credential is replaced, the pairing method becomes `invite`, the paired time is new. Just before the credential is swapped Home sends a best effort `POST /Sessions/Logout` with the **old** QC token (3 s, never fatal).
- **No QC specific code runs any more:** no session anchor, no device lock or session limit reading, no `Device` header check string for QC, no refusal of rotation with a QC explanation, no `sessionLimit` alert. The enum members and the readers for `qc` stay so that old files load; the redaction patterns and the guard ban on `QuickConnect/Authorize` stay.
- **The old sharing account is still alive on the friend's server** until they delete it. Home cannot do that for them. 4.5 keeps the stock Jellyfin revoke runbook for exactly this case.

### 4.1b Peer admin with Shoal (required since v2.8; the only pairing path)

1. **Shares > New share.** Friend name, libraries (local libraries only; anything under the server's own StubRoot is refused, so no chains, W§A-20.1), optional max bitrate per stream, max concurrent transcodes (default 4, D15). Shoal creates `shoal-<friend>-<4 hex>` with a random 32-byte password that is never shown or stored, and this policy. Allowed: `IsHidden`, `EnableRemoteAccess` (Tailscale addresses are not "local" to Jellyfin), `EnableMediaPlayback`, `EnableVideoPlaybackTranscoding`, `EnablePlaybackRemuxing`, `EnableAudioPlaybackTranscoding`. Denied: `IsAdministrator`, `EnableAllFolders` (only the chosen libraries), `EnableAllChannels`, `EnableContentDeletion`, `EnableContentDownloading`, `EnableLiveTvAccess`, `EnableLiveTvManagement`, `EnableCollectionManagement`, `EnableSubtitleManagement`, `EnableRemoteControlOfOtherUsers`, `EnableSharedDeviceControl`; `SyncPlayAccess=None`. Code can set what the dashboard cannot, so this policy also has `EnableUserPreferenceAccess=false`, `EnableMediaConversion=false`, `EnableSyncTranscoding=false`, `EnablePublicSharing=false`, `EnableLyricManagement=false`, and `MaxActiveSessions=1` as defence in depth (the guard bans of 8.4 job d are the primary control; Approve and re-pair handle the limit, step 3).
2. **Invite.** The **sharing side creates it**. Shoal makes a 128-bit secret and shows it **once** as `shoal1-<first 8 hex of this server's Id>-<secret>`, where the secret is 26 Crockford base32 characters (`0-9 A-Z` without `I L O U`) in groups of 4, for example `shoal1-1a2b3c4d-K7QM-2XA4-9FZD-R3TB-W8NE-HC5J-6P`. Parsing ignores case, spaces and hyphens, and maps `I`/`L` to `1` and `O` to `0`. It is valid for **15 minutes**, single use, and bound to that share. Only its SHA-256 is kept, in memory. The admin sends it over a private channel; "Create a new invite" is one click.
3. **Approve, with a typed safety code (W2§N-5).** When Home redeems the invite, the share shows "**<Home name>** wants to connect (server id `1a2b3c4d`, from a tailnet/LAN/public address)" and a box: "Type the safety code shown on <Home name>'s screen". **The Peer's screen never displays the code.** Home displays it, its admin reads it to the friend, and the friend types it. Reading it off a card on the Peer's own screen is what made "compare by eye" skippable: a friend who clicked Approve without comparing handed the token to whoever redeemed the invite first. **The browser never holds the code** (v2.2, UI§14.2 #7): **Approve** enables once 8 valid Crockford characters are typed, and the **server** judges `POST .../Approval {approve:true, sas}` (constant-time compare; input ignores case, spaces and hyphens and maps `I`/`L` to `1` and `O` to `0`). A miss answers `{match:false, triesLeft}` and leaves the request pending. The third miss ends **that request and its invite together**: the approval `state` becomes `cancelledWrongCode`, the invite record (already bound to Home's commitment, so unusable by anyone else) answers Home's polls with `state:"codeMismatch"` until its 15 minutes end and `404 {}` after that, and the admin must create a new invite. Other invites are never touched. Why three: only the Peer admin can submit a code (an admin-only endpoint), so the limit is a tripwire for a code that truly differs (an active attacker on the path, or the wrong Home), not a defense against guessing; 3 versus 5 changes no security, and a third miss after reading the code aloud is a reason to stop and phone. **Deny** needs no code and answers Home's polls with `state:"denied"`. On Approve, in this order, Shoal: (1) signs out every existing device of the share user (a re-pair; with `MaxActiveSessions=1` the new login would otherwise be refused while the old session lives); (2) records the new device in `expectedDeviceIds` **before** anything can trigger the sweep, so the sweep that the policy update below sets off cannot log out the freshly minted device (W2§3.4(b)2); (3) sets `EnableAllDevices=false` and `EnabledDevices=[homeDeviceId]`; (4) calls `ISessionManager.AuthenticateDirect` for the share user with Home's DeviceId; (5) holds the token in memory until Home collects it (2 min max; otherwise that device is logged out and the pairing fails).
4. **Devices and sweep.** The share lists the share user's devices with first and last seen. Every 60 s, and on `IUserManager.OnUserUpdated` for a share user, Shoal enumerates the user's devices (`IDeviceManager.GetDevices(UserId)`). Any device not in `expectedDeviceIds`, persistent or not, is **logged out at once** (`ISessionManager.Logout(device)`) and shown as "Unexpected device signed out" with its name as text (W§A-1 fix 4, W§A-9). No legitimate second device exists for a share user.
5. **Drift repair.** On the same schedule, Shoal re-applies the policy of step 1 (4.1 step 3 plus the code-only flags and the session limit) and the device lock. If an admin edited the user in the dashboard, Shoal repairs it and logs what it changed.
6. **Pause and Resume.** Pause sets `IsDisabled=true`, so every token of the share user stops working at once, and a disabled user cannot mint tokens. Resume first runs the device sweep, then clears `IsDisabled`. That is safe on a Shoal Peer, unlike a stock Peer (4.5), because the account cannot reset its own password (`EnableUserPreferenceAccess=false`, re-applied every tick, plus the guard bans on `Users/Password`, `Sessions/Capabilities*` and `QuickConnect/Authorize` for share users, 8.4 job d) and the sweep leaves only Home's device to revive.

**Pairing is invite only.** The share user's password is random and never shown, so nobody can sign in as it, and the plugin does not call `IQuickConnect.AuthorizeRequest` (W§A-20.9 is moot). Home detects Shoal in advance with the anonymous ping (`GET /Shoal/Ping`, answers `200 {"shoal":true}` or the plain `404`, rate limited; `/Shoal/Pair` itself answers 404 without an active invite and is no signal). The Check step of the Home wizard uses the ping: no Shoal means the "Install Shoal first" screen (v2.8.3, section 12).

**`POST /Shoal/Pair`, the one anonymous endpoint** (`PairController`; body ≤ 1 KB, `application/json`):

| | |
|---|---|
| Request | `{"v":1, "invite":"<26 base32>", "homeNonceCommit":"<base64url SHA-256 of homeNonce>", "homeNonce":"<16 random bytes, base64url>", "homeServerId":"<32 hex>", "homeName":"<≤ 64 chars>", "homeDeviceId":"shoal-<12 hex>-<8 hex>"}`. The first request carries the **commitment only** (`homeNonce` absent). Each poll re-sends the body **with** `homeNonce`, and the Peer checks it against the commitment. This commit-reveal means neither side can pick its nonce after seeing the other's, so an on-path attacker who also holds the invite cannot grind a nonce until the two SAS values collide (2^40 hashes is about a minute on one GPU, W2§N-5). |
| Responses | `200 {"v":1, "state":"pending"\|"approved"\|"denied"\|"codeMismatch", "peerServerId":"<32 hex>", "peerNonce":"<base64url>", "token":"<only in the first approved response>"}`. `codeMismatch` (v2.2) means the Peer admin typed a non-matching safety code three times; only the requester bound to the invite's commitment can see it, and it tells that requester nothing it could use (it cannot submit codes). Every failure (unknown, expired, already used, nonce mismatch, malformed) returns the **same** `404 {}`: there is no expired-versus-wrong oracle. |
| Lookup | The same work for every request, so neither the answer nor its timing reveals whether an invite is active: (1) always read the body (at most 1 KB); (2) compare SHA-256 of the presented secret with `CryptographicOperations.FixedTimeEquals` against every active invite, or against a fixed dummy digest when none is active; (3) only a **miss** goes on to the failure limiter below. **A valid secret is never subject to the failure limiter.** The first redemption binds the invite to its `homeNonceCommit`; later calls must present the same commitment and, from the first poll on, a `homeNonce` that hashes to it. A valid secret with a different commitment gets the same `404` and counts as a failure. |
| SAS (safety code) | First 40 bits of `SHA-256("shoal-sas-v1" ‖ secret ‖ homeNonce ‖ peerNonce ‖ homeServerId ‖ peerServerId)`, as 8 Crockford base32 characters shown in two groups: `K7QM 2XA4`. Both sides compute it, after the reveal checks out; it is not a secret. **Home displays it; the Peer never does**, and its admin types it to approve (4.1b step 3). |
| Limits | **Failures only** are counted (unknown, expired, delivered, commitment or nonce mismatch, malformed). Buckets: 10 failures per minute per remote IP, and a looser global bucket of 100 failures per minute. A tripped bucket answers **failures** with `429` plus `Retry-After` (or a 1 s delay). It never destroys an invite, never means "cancel all", and never rejects a valid secret (W2§2d: the v2 rule "3 failures cancel all invites" let any caller who could reach the Peer disable pairing for good). A burst of failures only raises a notice to the admin ("N wrong invite attempts in the last hour"). Valid requests are bounded by the invite's own 15-minute life and single binding, so no request-rate limit applies to them. "Remote IP" is `HttpContext.GetNormalizedRemoteIP()`, core's resolved client address; behind a reverse proxy without Known Proxies (Jellyfin's networking settings) every client looks like the proxy and the per-IP bucket degrades to a global one, so the Hardening screen warns when most requests come from one private or loopback address and the README documents the setting. The token is delivered once; then the record becomes `delivered` and further calls get `404`. |
| Logging | Never the invite, nonce, commitment or token. The approval screen and audit log show Home's name and id as text. |

The reflection test whitelists exactly this action, with a comment pointing here.

### 4.2 Home admin, step by step (the wizard; data requirements in 9.3)

The wizard has five steps: **Address, Check, Invite, Code, Choose** (v2.8; there is no route question). A legacy QC peer is re-paired with the same wizard, started from its card.

1. **Address.** Display name, URL, network type (`Tailnet`, `LAN`, `Public HTTPS`), optional proxy. The invite can be pasted here or at the Invite step.
   - `AddressPolicy` validates the URL (8.3 T11).
   - Tailnet requires a `*.ts.net` name or an IP in `100.64.0.0/10` or `fd7a:115c:a1e0::/48`.
   - LAN over plain http requires the per-peer acknowledgement "the token travels unencrypted on your network" (W§A-7).
   - **An invite is never redeemed over plain http outside the Tailnet class** (W2§N-5): the invite, the nonces and the returned token would be visible to a sniffer on the path, and "delivered once" does not help against one that polls first. A friend on a plain http LAN address outside a Tailnet therefore has no route: they use Tailscale or https (a LAN address is for testing only).
   - If Home's Tailscale runs in userspace mode (LXC), start `tailscaled` with `--socks5-server=localhost:1055` and enter that as the proxy. Proxy mode is Tailnet-only.
2. **Check.** The ping (`GET /Shoal/Ping` through `PeerClient`, so the address policy and size caps apply) and `GET /System/Info/Public` together. The probe answers `hasShoal` (true only for `200 {"shoal":true}`) and `shoalPingThrottled`. `hasShoal: false` stops with "Install Shoal first" and **Check again**; a throttled or unreadable ping continues with a soft hint and never blocks (v2.8.3). `GET /System/Info/Public` shows `ServerName`, `Version` and the first 8 hex of `Id`, labelled "server id (detects a wrong or reset server; not proof of identity)". With an invite, the prefix must equal the invite's prefix; otherwise stop with "this invite is for a different server", so the secret is never sent elsewhere.
3. **Pair.** Exactly one pairing session per Home, started only by the admin's click (4.2a).
   - *Invite path:* send the commitment with `POST /Shoal/Pair`, reveal the nonce on the first poll, and show the safety code (SAS): "Read this code to <friend>; they type it on their screen to approve". Poll every 2 s for up to 15 min. There is no "codes match" button on this path: the friend's typed code is the confirmation, and a code that does not match simply never approves. A poll answering `denied` ends the session as `PairingAborted(denied)`, and `codeMismatch` (three wrong codes, 4.1b step 3) as `PairingAborted(codeMismatch)` (v2.2, so the admin can tell "they chose Deny" from "the code didn't match"); both are *stopped* kinds and need a new invite. **Cancel** stops polling and discards anything received.
   - There is no other path. `POST Shoal/Admin/Pairing` takes `{probeHandle, displayName}`; a probe made without an invite cannot start a pairing (`409 methodUnavailable`), and `method: "qc"` is refused the same way. `POST Shoal/Admin/Pairing/Confirm` no longer exists.
4. **Validate the token,** all mandatory, in this order (the response-format checks of 4.2a come first):
   - `GET /Users/Me`. If `Policy.IsAdministrator` is true, call `POST /Sessions/Logout` with the token, discard it, and show "That account is an administrator on your friend's server; refusing." Warn, but continue, on the policy deviations of the share policy in 4.1b step 1.
   - `GET /System/Info`: `Id` must equal the pinned Id.
   - On the invite path `SecretStore.Put` follows these checks directly. The token is never echoed to the browser.
5. **Libraries.** `GET /UserViews` lists what the share user sees, returned to the browser as `{mappingKey, name, type, itemCount}` with no peer GUIDs. The admin ticks libraries; only movies are selectable in MVP. The first-import title count is shown, and more than 5,000 titles need an extra confirmation.
6. **Who can see it** (owner D3/D4; mechanics in 5.6). Access mode starts at **Only selected users** with only the admin running the wizard selected. The admin may select more people now or later. Restricted profiles (any parental limit, `BlockUnratedItems`, `AllowedTags` or an access schedule) are shown but cannot be selected unless the admin ticks "Ratings come from <friend>'s server and are not verified". The wizard does not offer "Everyone" yet: the admin tests first, then flips the switch on the library (9.3).
7. **Create.** `AddVirtualFolder(name, movies, options, refreshLibrary:false)` with locked options (5.6), then `VisibilityService.Reconcile` (5.6), then the first sync.

### 4.2a Pairing session: one click, response validation, durable abort, header encoding (K22; this was the Quick Connect state machine until v2.7)

This is the contract the invite pairing codes against. The Quick Connect states, the poll classification, the session limit inference and the confirm gate are gone with D17.

**One session, one click.**
- Home has at most **one live watch side pairing session**. A second `POST Shoal/Admin/Pairing` while one is live gets `409`. (A Shoal Peer's single pending approval per friend is separate, 4.1b.)
- The invite is redeemed (`POST /Shoal/Pair`) only because the admin clicked "Start pairing". A probe, a page load or reload, navigation, a Home restart, a retry after an error and every scheduler never send it. An aborted or failed session never restarts itself: the admin clicks again and a **new** session starts. The limit of 5 per 10 minutes per peer is enforced on the server, not in the page.
- While a session lives, the peer's `baseUrl`, network type and proxy are immutable (`PUT Peers/{h}` answers `409`). A probe handle is single use and expires after 10 minutes. An invite over plain http outside a Tailnet is refused.

**Response validation.** Everything the Peer returns during pairing is checked before it is shown, stored or put in a header. A failure aborts with `badPeerResponse` and a fixed message; the offending value is never rendered, logged or echoed.
- Access token: `^[0-9a-f]{32}$` (a `Guid` in "N" format) before it enters an `Authorization` header or the store.
- `peerServerId` and the ids in the answer must parse as GUIDs and equal the pinned server id; `GET /Users/Me` must return the share user, and `Policy.IsAdministrator` must be false (otherwise log out, discard, `adminAccount`).
- The invite secret, the nonces, the commitment and the token never go into a log line at any level, an exception message, the admin API or the browser. The log grep tests (T-SEC-6, T-SEC-10) cover all four.

**Abort is durable.**
- **A restart is detected from disk.** `POST Shoal/Admin/Pairing` writes `pairing:{phase:"beforeApproval", method:"invite", startedUtc}` on the peer record in `state.json` (a new peer record for a first pairing; the existing record, with its secret and libraries untouched, for a re-pair); Confirmed or an abort removes it. At startup any record still carrying `pairing` becomes `PairingAborted(interrupted)`. A record with method `qc` (an interrupted pairing from before the upgrade) is cleared silently: a never paired peer with no credential is removed as Dismiss does, a paired one resumes its status. The UI says "The invite expired".
- `PairingAborted{kind, method, atUtc}` is persisted in `peers/<peerKey>/health.json` and, for a first pairing, the peer is kept in `state.json` as `enabled:false` with no secret (3.2a, 3.7). For a re-pair the previous secret and state stay as they were (the new token replaces the secret only when the pairing succeeds).
- **Two kinds.** *Safety*: `serverChanged`, `adminAccount`, `badPeerResponse`. The Overview card says "Pairing stopped for your safety", and a `DisplayMessage` (the mechanism of 5.7) goes to the sessions of the admin who started the pairing and of the other admins. *Stopped*: every other kind produced today (`expired`, `denied`, `codeMismatch`, `inviteInvalid`, `inviteReplaced`, `inviteCancelled`, `tokenNotCollected`, `network`, `interrupted`, `rateLimited`, `server`). The card says "Pairing didn't finish" and no pop-up is sent. The QC kinds (`requestReplaced`, `codesDidNotMatch`, `quickConnectOff`, `quickConnectUnreachable`, `likelySessionLimit`) are never produced; their enum members stay so that old `health.json` files load, and a stored unknown or legacy kind shows the generic "Pairing didn't finish" card.
- Both kinds survive a Home restart and a page reload (T-PAIR-16) and clear only on **Dismiss** (`POST Shoal/Admin/Peers/{h}/DismissPairing`, 9.6) or on a successful pairing.

**Header encoding.** Every value in the `Authorization` header (`Client`, `Device`, `DeviceId`, `Version`) is percent-encoded UTF-8: every byte outside `A-Z a-z 0-9 - . _ ~` becomes `%XX`, so `%` is `%25`, `+` is `%2B` and a space is `%20`. Core decodes with `WebUtility.UrlDecode` (`AuthorizationContext.cs:276-313`), under which a bare `+` becomes a space and an unescaped `%` corrupts the value. Test: a server name such as `A+B 100% "x", y` reaches fakepeer byte exact after the same decode (T-SEC-20, run against an authenticated call).

### 4.3 Pairing hijack defenses (W§A-1)

**The Quick Connect attack no longer exists on Home.** Up to v2.7 a stock Peer's anonymous `POST /QuickConnect/Initiate` could be flooded so that a request landed on Home's six digit code and the friend approved the attacker (`QuickConnectManager.cs`). With D17 Home never starts a Quick Connect pairing, so the code collision class, the poll that detected a replaced request, the check string and the confirm gate are gone (T34, 8.3). What defends an invite pairing:

| Defense | What it does |
|---|---|
| Invite + typed SAS | The invite is a 128 bit single use secret with a commit and reveal nonce exchange. The Peer admin approves only by **typing** the safety code that Home displays (4.1b step 3), so an on path attacker who also holds the invite cannot grind a matching code. |
| Failure limiter without an oracle | `POST /Shoal/Pair` answers every failure with the same `404 {}`; only failures are counted and slowed, a valid secret is never limited (4.1b Limits, T-PAIR-17). |
| Server id prefix in the invite | Home compares the prefix with the probed server before it sends the secret (T-PAIR-10). |
| Device sweep and bans | Shoal Peer, 4.1b step 4 and 8.4 job d: unexpected devices are signed out within 60 s, and share users get 403 on `QuickConnect/Authorize`, `Users/Password` and `Sessions/Capabilities*`, so a token can neither mint another nor reset the password. |
| Foreign-device watch | Every 10 min Home calls `GET /Sessions` with its token. For non-admins core returns only the caller's own sessions plus anonymous ones (`SessionManager.cs:2065-2140`). Home keeps sessions whose `UserId` is the share user, discards the rest unlogged, and alerts "Another device is using your account on <Peer>" with the device name as text when a kept session has a `DeviceId` other than its own or when more than one is kept. Sessions live in memory, so this is a heuristic, not proof. |

**Honest residual risk after a stolen token (W2§3.5).** A thief holding Home's token for a Shoal Peer can read the shared libraries and run transcodes up to the per-share limit, and nothing else: no second token, no password change, no lasting foothold after Pause or Revoke. Raw streams stay anonymous for anyone who holds peer GUIDs until the Peer runs `Enforce` (T29, 8.4). A legacy QC peer's old sharing account is the weaker case; it is paused on Home and must be deleted by the friend (4.1, 4.5).

### 4.4 Token lifecycle and peer identity

| Stage | Rule |
|---|---|
| Mint | Invite approval via `AuthenticateDirect` on the Shoal Peer (Quick Connect minting was removed in v2.8). Phase 5 adds rotation through `POST /Shoal/Share/Rotate`. That is, by design, the same self-minting capability a token already has (W§A-9 note): rotation limits exposure of an old token, not a thief who holds the current one. (phase 5 rotation: v2.6.1) |
| Device id | `shoal-<homeServerId:N[0..12]>-<peerKey:N[0..8]>`, derived on the server, **constant per pairing** (changed from v1, which appended a generation). Re-auth on the same DeviceId logs out the previous token (`SessionManager.cs:1766-1790`), so re-pair is clean, and the invite approval of 4.1b keeps the device lock on the Peer without the friend doing anything. |
| Store | `SecretStore` only. In memory, one `PeerCredential` object. Never in XML, logs, URLs, exceptions or admin API responses. |
| Use | `Authorization: MediaBrowser ... Token="<t>"` header only (6.11). Never `ApiKey` in a query, never `X-Emby-Token` (legacy auth is off in 12.x, S§1.7). |
| Trust window | Each peer has `pinVerifiedUtc` and `authVerifiedUtc`. No authenticated request (other than the health call that refreshes them) is sent unless the pin was confirmed **unauthenticated** within the last 120 s. Static proxying also requires auth confirmed within 120 s. `HealthMonitor` refreshes both every 60 s, and on demand at most once per 10 s after a restart or an expired window. This is how "check the identity before sending the token on a new connection" is implemented (W§A-7). Connections are pooled for 5 min, so a host swap is noticed within one window, not one request. |
| Identity | The server `Id` comes from the anonymous `/System/Info/Public`, so anyone can replay it. **The pin is accident detection** (server reset, IP or name reuse), not authentication. Peer authentication comes from the network: WireGuard node identity (Tailnet), CA-validated TLS (Public), and an optional per-peer TLS public-key pin (`tlsSpkiPin`, opt-in, with "Re-trust"). LAN http has none, and the admin acknowledged that (4.2). Mismatch gives `IdentityMismatch` or `TlsKeyChanged`: zero further calls until the admin acts. |
| Expiry | None found in core (S§5.2). |
| 401/403 from the Peer | The peer goes to `AuthFailed` immediately: no retries and no re-login loop (Home never holds a password). Playback returns `ErrorCode=NotAllowed`, static proxying stops, sync is skipped, and the UI shows **Re-pair**. This is also what a deleted sharing account looks like: its token is refused with 401 (its device row went with it) or 403 (empty user id, `DefaultAuthorizationHandler.cs:45-49`), never served. |
| Rotation | **Re-pair** by invite through the same path; same DeviceId, so the old token dies at the new authentication. A Shoal Peer signs the old device out at Approve (4.1b step 3). Rotation without a new invite is `POST /Shoal/Share/Rotate` (v2.6.1). A legacy QC peer is not rotated; it is re-paired (4.1). |
| Peer address change (W2§N-8) | `PUT Shoal/Admin/Peers/{h}` with a new **host** (a scheme or port change does not count) discards the stored token **first**: the `SecretStore` entry is deleted, the peer goes to `AuthFailed` with **Re-pair**, and only then is the new `baseUrl` written to `state.json`. No request carrying the old token is ever sent to the new host. Why: the token is sent after an *unauthenticated* pin check and the pinned server Id is public, so any host that replays the Id would otherwise receive the token (an admin mis-click, a phished admin, XSS in the admin page, a restored `state.json`). The sealed entry is also bound to the host (8.2), so editing `state.json` alone cannot redirect it either. |
| Remove peer (Home) | Best-effort `POST /Sessions/Logout` with the token (3 s), then delete the secret. Optionally (ticked by default) delete the stub folder, the local library and its access settings; `VisibilityService` then undoes its own library-access changes for that library (5.6). |

### 4.5 Revoking a friend (W§A-9, W2§N-1)

> **v2.8 (D17):** new pairings are always with a Shoal Peer, so the Shoal Peer paragraph at the end is the normal revoke. The stock Jellyfin runbook before it stays for the **old sharing accounts that legacy QC peers left on their friends' servers** (4.1): Home cannot delete them, so the friend must.

**Facts.** A share-user token on a stock Peer can mint more tokens for itself in **two** ways (both read in source, neither executed here):
1. *Quick Connect:* `Initiate` is anonymous and `Authorize` accepts the caller's own user id (`QuickConnectController.cs:104-113`, `RequestHelpers.cs:62-83`).
2. *Password reset:* `POST /Users/Password` with `ResetPassword` is allowed for the account itself while `EnableUserPreferenceAccess` is true (the default, and not settable in the 12.1 dashboard), asks for no old password and revokes no tokens. The account then has no password, and `POST /Users/AuthenticateByName` with an empty password mints a token (`UserController.cs:270-327`, `UserManager.cs:419-449`, `DefaultAuthenticationProvider.cs:61-68`). Home cannot detect the reset: `UserDto.HasPassword` is never filled in 12.1.

Both need a device id that passes the device lock: Home's own, or any id the token holder first registers as non-persistent with `POST /Sessions/Capabilities[/Full]` (4.1). So deleting one device is **not** a revocation. Disabling a user does not delete its devices (`UpdatePolicyAsync`, `UserManager.cs:848-927`, has no logout; `DeviceAccessHost` only acts on the device lock), and re-enabling it revives every token it ever had, including any minted before the disable and any sign-in made with a password the thief set. Core also serves raw static and subtitle bytes anonymously (K§Surprises 1), so anyone who kept peer GUIDs can stream them until the network path is closed or the Peer runs `Enforce`.

**Stock Jellyfin Peer: revoke completely.** The Home README and the friend checklist carry this text. **Deleting the sharing account is the only clean revoke.** Disabling it is best-effort (see Pausing).
1. **Delete the sharing account now:** Dashboard > Users > `shoal-<friend>` > Delete. The account and every token it ever had stop working at once; a token for a deleted account is refused (401, or 403 through core's empty-user-id branch), never served. One click closes both minting routes together, which is why it comes first. To share again later, create a **new** account (checklist step 2) and pair again.
2. **Remove the network path:** Tailscale admin console > Machines > your Jellyfin machine > Share > remove the friend. For a public HTTPS peer, block their address. This is the only step that stops anonymous raw streams for titles whose ids their server already knows.
3. **Tidy up (optional):** Dashboard > Devices, delete any leftover devices of that account. Deleting the account also clears the hidden, token-less sessions a token holder may have created; on any suspicion short of deleting it, **restarting Jellyfin also clears hidden sessions** (they survive device deletion and a password change, P2§4, W4-13).

**Pausing on a stock Peer** (temporary, for a trusted friend). **Disabling alone is best-effort and unsafe if a token was ever stolen:** the thief may already have emptied the password and minted other devices, and re-enabling revives them. The safe sequence is:
1. Disable the account.
2. While it is disabled, set a **new long random password** on it (Dashboard > Users > the account > Password). On 12.1 an admin's password change calls `RevokeUserTokens`, which signs out **every** device of that user (`UserController.cs:317-322`), and it replaces an empty password. Home sees 401 and shows **Re-pair**.
3. Re-enable it only when sharing resumes, set the session limit to 0, then re-run checklist steps 5-9 (Home re-pairs; step 9 sets the limit back to 1). The password change also signed out Home's own token, so a re-pair is always needed (P2§5).
**Lab-confirmed by T-SEC-22** (P2§5 E1: four live tokens, including self-minted ones, all answer 401 after the admin's password change; an empty password no longer signs in). If in doubt, delete the account instead.

**Shoal Peer:** Shares > friend > **Revoke** (UI: "Delete {Friend}'s access"). Shoal disables, `ISessionManager.Logout(device)` for every device from `IDeviceManager.GetDevices(UserId)`, verifies that zero devices remain (retry once, then alert), and deletes the user. It then shows the network step as a checklist item with a link. With `Enforce` on, raw streams already need a token, so that step mainly stops further requests; it stays recommended. **Pause / Resume** are separate and reversible, and safe on a Shoal Peer because the account cannot reset its password or authorize Quick Connect and the sweep removes extra devices (4.1b step 6).

**UI copy (9.3):** "Stop sharing" on a stock Peer starts with "Delete the sharing account", never "disable it"; Pause on a stock Peer says "Disabling is not enough on its own: set a new password too, or delete the account".

**What Home does:** the next authenticated call (at most 60 s later) returns 401, so the peer becomes `AuthFailed` and static proxying stops (trust window). An honest Home stops within 60 s even though the Peer would still serve GUIDs anonymously (T-SEC-13).

---

## 5. Catalog sync

### 5.1 Peer API used (stock Jellyfin, share-user token)

| Call | Purpose |
|---|---|
| `GET /System/Info` | identity pin check (passes for the share user via `FirstTimeSetupOrIgnoreParentalControl`, S§6) |
| `GET /UserViews` | mapped libraries still visible |
| `GET /Items?ParentId=<lib>&Recursive=true&IncludeItemTypes=Movie&Fields=ProviderIds,Etag&EnableImages=false&EnableUserData=false&SortBy=SortName,Id&StartIndex=n&Limit=200&EnableTotalRecordCount=true` | cheap full listing |
| `GET /Items?Ids=<<=50 ids>&Fields=ProviderIds,Etag,Overview,Genres,Studios,People,OriginalTitle,Taglines,OfficialRating,CommunityRating,PremiereDate,ProductionYear,SortName,MediaSources&EnableImages=true&ImageTypeLimit=1&EnableImageTypes=Primary,Backdrop` | details for new and changed items |
| `GET /Items/{pid}/Images/Primary?format=Jpg&maxWidth=600&quality=85&tag=<tag>` and `Backdrop?format=Jpg&maxWidth=1280&quality=80&tag=<tag>` (High art quality: 1000 and 1920) | images; `format` is the `ImageFormat` enum (`ImageController.cs:552-570`) |

Every response is read through `BoundedReadStream` (6.10) and parsed into Jellyfin.Model DTOs (`BaseItemDto`, `QueryResult<BaseItemDto>`) with `MaxDepth=32`. An item that fails to parse (for example an unknown enum from a newer Peer) is skipped and counted; it does not fail the page. Every field then passes `MetadataSanitizer` (5.5) before it is stored or written anywhere. Supported Peer versions are 10.11.x and 12.x; only 12.1 is lab-tested.

### 5.2 Algorithm

**Preconditions:**
- The peer is enabled, inside its trust window (4.4), its circuit is closed, and no other sync is running (one global sync; `SemaphoreSlim.Wait(0)`).
- The last sync is at least 5 min old, for manual runs.
- `ILibraryManager` is not mid-scan. If it is, defer 10 min.
- Overall budget is 15 min, with linked cancellation.

**Phase A: enumerate (network only, no disk writes).**
- Identity check, then views check. If a mapped library is missing, mark it `held`; that is never treated as deletions.
- Page the cheap listing. The run is **complete** only if every page succeeded and the collected id count equals `TotalRecordCount` (± items added during paging). Otherwise it is **partial**: add and update only, no removals.

**Phase B: diff (CPU only).**
- `new` = peer ids not in the catalog; `changed` = Etag differs; `gone` = active entries whose peer id is absent (complete runs only). **v2.4 (KQ 5, 6):** a listing is a snapshot that can drop an item at a shifting page boundary, so "complete" tolerates `|collected - TotalRecordCount| <= max(5, 1%)`, and every gone id is **confirmed** with `GET /Items?Ids=` (50 per call) before it counts; ids the Peer still returns are not gone. If a confirm call fails, the run is partial and nothing is removed.
- `moved` = a gone entry whose canonical provider ids match a new entry. Update `peerItemId` in place; the stub is untouched, so the local item id and watch state are preserved.
- **Dedup** (5.3) filters `new`.
- **Mass-deletion guard:** if `|gone| > max(MassDeletionMinCount, MassDeletionPercent% of active)`, or a library returned 0 items while the catalog has more than 0, nothing is removed. Entries become `held-removal`, the admin is alerted, and **Release** applies them. If the same set is still gone after 3 consecutive complete syncs spanning at least 24 h, release automatically.
- **New-items guard (W§A-10):** on any sync after the first, if `|new| > max(NewItemsMinCount, NewItemsPercent% of active)` (defaults 500 and 20%), no new entry is imported. They become `held-addition`, the admin is alerted ("Hops added 1,240 titles"), and **Release** imports them. There is **no** auto-release for additions, because the guard exists to bound a hostile peer. The first sync is exempt; the wizard showed its count (4.2 step 5). **v2.4 (KQ 7):** Release runs a full sync that approves exactly the held ids; anything new since the hold goes through this guard as usual.
- **Entity caps:** 5.10.

**Phase C: fetch details and images (network)** for `new ∪ changed`, in batches of 50. Item validation failure (5.5) means skip that item. Images are fetched **one at a time** (W§A-11) and stop when the disk quota is reached (5.10).

**Phase D: apply (local disk only)** under `<StubRoot>/<peerKey>/<Subfolder>/`:
- *Add:* create the folder; write `.nfo`, then images, then `.strm` last (temp file plus rename). The `.strm` is what makes it an item, so a crash mid-write leaves no half-item.
- *Update:* rewrite `.nfo` if metadata changed, and images if the tag changed. **Never rename the folder** after creation; the NFO title wins for display.
- *Remove:* delete only the files Shoal creates (`*.strm`, `*.nfo`, `poster.*`, `backdrop.*`), then the folder if empty. Canonical-path confinement check before every delete.

**Phase E:** save `catalog.json` atomically (3.7). If anything changed, call `ILibraryMonitor.ReportFileSystemChanged(path)` once per changed movie folder, or once for the library root if more than 50 changed. That routes through core's debounced `FileRefresher` (`FileRefresher.cs:79`). Kiro verifies in the lab that this refreshes with realtime monitoring off; if not, call `ValidateChildren` on that library's `CollectionFolder` only (`Folder.cs:329`). Never call `QueueLibraryScan()`, which scans every library. Then compute the overlap report (5.8).

### 5.3 Dedup (local always wins)

- **Local index:** all `Movie` items not under StubRoot, keyed by canonical provider ids (TMDb numeric, IMDb `tt` + digits, TVDb numeric), with `title|year` as a weak key. Size comes from the first media source.
- **Match:** any shared provider id means duplicate. With no provider ids on either side, an exact normalized title (NFKC, casefold, punctuation stripped) plus equal year means duplicate. Without a year it never matches.
- **Duplicate of local:** state `duplicate-local`, no stub. If a stub already exists (the local copy appeared later), remove it through the normal removal path, subject to the mass-deletion guard. Watch state carries over through the provider-id user-data keys (`Video.cs:293-320`, `UserDataManager.cs:364-380`).
- **Duplicate across peers:** lower `priority` wins; ties go to the earliest paired. The others are `duplicate-peer`.
- **Not built (v2.6.4):** a better peer copy is never merged into a local item; other peers' copies stay title-level duplicates.

### 5.4 Images: download once, JPEG and PNG only (W§A-10, W§A-11)

Images are downloaded at sync time and stored next to the stub, so art works while the Peer is offline, browsing makes no peer calls, core's image cache serves them, and no peer GUID appears in an image URL. With Standard art quality, the expected size is about 60 KB per poster and 150 KB per backdrop.

Rules:
- Request `format=Jpg` (5.1). Accept HTTP 200 only, with upstream `Content-Type` `image/jpeg` or `image/png`.
- Byte caps: poster ≤ 2 MB, backdrop ≤ 4 MB, enforced by `BoundedReadStream` whatever `Content-Length` says.
- Magic bytes must be JPEG (`FF D8 FF`) or PNG (`89 50 4E 47 0D 0A 1A 0A`). **WebP, GIF, SVG, BMP and everything else are rejected.** The extension comes from the magic, never from the Peer.
- Dimensions are parsed from the header without decoding: width ≤ 4000, height ≤ 6000, width × height ≤ 16 MP. For JPEG, **walk the marker segments by their declared lengths** from the start of the file to the first SOF marker; never scan the bytes for `FFC0..FFCF`, because an EXIF thumbnail inside APP1 carries its own SOF, so a scan reads the thumbnail's small size and passes an 8000 × 8000 image (W2§N-12). For PNG, use the first `IHDR` chunk only (it must be the first chunk).
- One image at a time per sync; the disk quota is checked before each write (5.10).
- Any failure means no image, and Jellyfin shows its placeholder.

Residual risk, accepted: core still decodes these JPEG/PNG files with Skia when it serves or resizes them. Restricting formats and dimensions removes the WebP decoder and decompression bombs from reach; it does not remove decoder bugs in JPEG or PNG. v2.6.7 adds the child-process re-encode (K32).

### 5.5 Validation and sanitizing of peer metadata (W§A-12, W§A-20.8)

Every peer string is hostile input. `MetadataSanitizer` turns peer JSON into values Home may store; nothing else touches raw peer JSON. Common text rules for all strings below: NFKC; strip C0/C1 controls, characters invalid in XML 1.0, and bidi controls (U+200E, U+200F, U+061C, U+202A-U+202E, U+2066-U+2069); remove `<` and `>`; trim; truncate on a rune boundary.

| Field | Rule |
|---|---|
| `Id` (peer item, media source, library) | `^[0-9a-fA-F]{32}$` or GUID-D, parsed to `Guid`; else reject the item |
| TMDb, TVDb ids | `^[0-9]{1,10}$` (ASCII), parsed to an integer and re-emitted from the integer |
| IMDb id | `^tt[0-9]{5,10}$`, re-emitted as `"tt" + digits` |
| Any other provider id | dropped |
| **Use of provider ids** | Folder names, `<uniqueid>`, dedup keys and everything that can end up in a URL use **only the canonical re-emitted form, never the input string**. jellyfin-web builds `ExternalUrls` hrefs from provider ids without escaping (`web:itemDetails/index.js:731-737`), so these regexes are XSS controls. Test: `tt0133093"><img src=x onerror=alert(1)>` and `603 "onmouseover=...` (F-MD-04, F-MD-05). |
| Title, OriginalTitle, SortName | text rules, ≤ 300 |
| Overview | text rules, ≤ 10,000; **v2.4 (KQ 1):** the one field that keeps line feeds: `\r\n` and lone `\r` become `\n` first, three or more `\n` collapse to two, every other control is stripped as usual |
| Tagline | text rules, ≤ 500 |
| Genres | ≤ 32 values × 64 |
| Studios | ≤ 16 values × 128 |
| People | actors only, **≤ 5** (W§A-10), name and role ≤ 128 each, no images |
| OfficialRating | text rules, ≤ 32. Written to `<mpaa>` only if `ILocalizationManager.GetRatingScore(value, country)` recognizes it; otherwise dropped (unrated). **Untrusted** either way: a peer can label an adult title `G` (F-MD-06), which is why restricted profiles are excluded by default (5.6). |
| CommunityRating | 0..10 |
| ProductionYear | 1870..2100 |
| PremiereDate | a valid date between 1870 and 2100, else dropped |
| RunTimeTicks | 0..48 h |
| Size | 0..2^50 |
| Bitrate | 0..1 Gbps |
| Container | lowercase, `^[a-z0-9]{1,8}(,[a-z0-9]{1,8}){0,7}$` and **at most 40 characters in total** (v2.2: the length cap of core's `EncodingHelper.ContainerValidationRegexStr`, `EncodingHelper.cs:42`, which the official Android TV SDK also enforces, SC§G6), else null. Comma lists such as `mov,mp4,m4a,3gp,3g2,mj2` are valid and kept whole |
| Video codec | `^[a-z0-9_.-]{1,32}$`, else null |
| Width, Height | 0..16384 |
| Image tags | `^[A-Za-z0-9]{1,64}$`, else no image |
| Server name, library names (pairing, views) | text rules, ≤ 64; rendered as text only (9.1) |

**File and folder names** (`PathSanitizer`):
1. NFKC, then replace `/\:*?"<>|` and control characters with a space, then collapse whitespace.
2. Trim leading and trailing dots and spaces. Remove `..`.
3. Truncate to 120 UTF-8 bytes **on a rune boundary**, then trim dots and spaces again.
4. If the result is empty or a Windows reserved name (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`, any extension), use `Untitled`.
5. Append ` (year)` if valid, then ` [tmdbid-N]`, else ` [imdbid-ttN]`, else ` [shoal-<stubKey[0..8]>]`, always from canonical ids. A name collision within the library also gets ` [shoal-<stubKey[0..8]>]`. Collisions are detected case-insensitively and after normalization.
6. `Path.GetFullPath(Path.Combine(libRoot, name))` must start with `libRoot + separator`.
7. No path component under StubRoot may be a symlink or reparse point (`FileSystemInfo.LinkTarget == null`).

**NFO:** `XmlWriter` with `CheckCharacters=true`, `Encoding=UTF8`, no DTD, element text only, no URLs (3.4).

### 5.6 Library creation, locked options, access mode and visibility (owner D3/D4, W§A-6)

**Locked options** (set at creation; names verified in `LibraryOptions.cs` and `TypeOptions.cs`):
- `PathInfos=[<StubRoot>/<peerKey>/<Subfolder>]`
- `EnableRealtimeMonitor=false`, `SaveLocalMetadata=false`, `MetadataSavers=[]` (otherwise core rewrites our NFO), `EnableInternetProviders=false` (v2.4: declared but never read in 12.1, `LibraryOptions.cs:64`; set for readability, **not** relied on, SR§5)
- `TypeOptions` (v2.4, settled from source, SR§5; `BaseItemManager.cs:26-70`, `ProviderManager.cs:498-612`): one entry **each** for `Movie`, `Video`, `Trailer` and `BoxSet`, each with `MetadataFetchers=[]` and `ImageFetchers=[]`. An empty list on an **existing** entry means "no remote fetcher" (it also stops dynamic image providers such as the screen grabber); a type with **no** entry falls back to the server-wide options, where every fetcher is on, so every type that can sit under a Shoal path gets an entry. Local providers (the NFO reader, local images) still run.
- `EnableChapterImageExtraction=false`, `ExtractChapterImagesDuringLibraryScan=false`, `EnableTrickplayImageExtraction=false`, `ExtractTrickplayImagesDuringLibraryScan=false`, `EnableLUFSScan=false`, `EnableEmbeddedTitles=false`
- `DisabledSubtitleFetchers=<all>`, `SubtitleDownloadLanguages=[]` (the real guard; the list is the backstop), `AutomaticallyAddToCollection=false`, `AutomaticRefreshIntervalDays=0`
- `DisabledMediaSegmentProviders=<all>` (v2.4, SR§4.6; `LibraryOptions.cs:100`): the segment extraction task runs providers on any item whose file exists (`MediaSegmentExtractionTask.cs:93-96`), and a `.strm` exists, so Intro Skipper and similar plugins would otherwise analyze stubs. Kiro fills it with the exact value `MediaSegmentManager` compares (the provider name or its id).
- **Re-applied (v2.4, KQ 4):** the two "every provider" lists are recomputed at every Shoal startup and after each sync, so a subtitle or segment provider plugin installed after the library was created is covered. A difference is written back through the library options and logged once.

**Residual, accepted for MVP (v2.4, SR§5): cast names reach TMDb.** The NFO carries `<actor>` names. Core creates a `Person` item per name (`PeopleValidator.cs:60-72`), and a Person belongs to no library, so it gets default library options and the **server-wide** metadata options, where TMDb's person provider is on (`LibraryManager.cs:2884-2896`). Home's server therefore asks TMDb about the friend's cast names and downloads photos and biographies, as it does for its own library. No peer host, id or token leaves. Shoal does not change server-wide metadata options, which it does not own. The README says so; a setting that stops writing `<actor>` is a phase-5 candidate. T-SYNC-13 records the behavior in the lab.

**Access mode.** Each Shoal library (a Jellyfin library whose only path is a Shoal stub folder) has `access = {mode, selectedUserIds, includeRestricted}` in `state.json` (3.2a):
- `Selected` ("Only selected users", the default): admitted users are exactly `selectedUserIds`. At creation that is only the admin who ran the wizard (owner D3); every other account, **other admins included**, starts without access.
- `Everyone` ("Everyone on this server"): every account is admitted, including accounts created later, except **Shoal share users** (below) and **restricted profiles** (any `MaxParentalRating`, `BlockUnratedItems`, `AllowedTags` or `AccessSchedules`) unless `includeRestricted` is on. That toggle, like selecting a restricted profile in `Selected` mode, requires the acknowledgement "Ratings come from <friend>'s server and are not verified" (W§A-6: a peer can label an adult title `G`, F-MD-06).
- Switching `Selected` → `Everyone` keeps `selectedUserIds` untouched. Switching back restores that selection (owner D4); accounts created while in `Everyone` mode are not in it and lose access. The confirm dialog lists who loses access.

```
Admitted(user, L) = IsShareUser(user)           ? false
                  : L.mode == Everyone          ? (!IsRestricted(user) || L.includeRestricted)
                                                : L.selectedUserIds.Contains(user.Id)
IsShareUser(user) = user.Id is in peerRole.shares[].shareUserId     (guard-lastgood.json in recovery mode)
```

**Share users are never admitted, in either mode (W2§N-2).** On a server that runs both roles, the hidden `shoal-<friend>-xxxx` accounts are ordinary Jellyfin accounts, so without this rule `Everyone` would admit them and Reconcile would write the federated libraries into their lists. Then Z's library, which Z shared with this server's admin only, would appear in what this server shares with X, and X's Home could play it through this server: a chain Z never agreed to. So:
1. `IsShareUser` is checked **first**, ahead of the mode and ahead of `selectedUserIds` (a share user id that ends up in a selection, for example by editing `state.json`, is still refused).
2. Reconcile never adds a Shoal library to a share user's `EnabledFolders` and **removes** any it finds there (logged: "Shoal removed a federated library from a sharing account"). A share user is never converted to a managed account and never appears in `visibility.managedUsers`. **v2.4 (W6 L-6):** a share user found with "all libraries" is narrowed too: `EnableAllFolders=false`, `EnabledFolders` = every non-Shoal library, logged the same way and not recorded as managed (on a Shoal Peer, K21's drift repair then narrows it to the shared libraries).
3. `PUT Shoal/Admin/Libraries/{m}/Access` answers `400` when `selectedUserIds` contains a share user id, and the picker does not list share users (`isShareUser` in the user rows, 9.5).
4. CallerContext step 6 applies the same function (8.5, matrix row R15). Tests: T-VIS-7 (alias T-SEC-23).

**Two layers, one rule:**

| Layer | Mechanism | Why it is enough |
|---|---|---|
| 1. Playback (authoritative) | CallerContext step 6 (8.5) evaluates `Admitted` from `state.json` **on every request**, else 404 on PlaybackInfo, static, subtitles and HLS. Nothing is materialized per user, so an account created a minute ago is covered in `Everyone` mode and refused in `Selected` mode. Admitted users still pass core visibility (step 5), so an admin can still exclude one person through Jellyfin's own controls. | holds whatever happens to core library access |
| 2. Browsing (core library access) | `VisibilityService.Reconcile` sets each account's access to each Shoal library L to `Admitted(user, L)`, so people see exactly the libraries they can play. Rules below. | keeps lists, search and home rows consistent with layer 1. **Eventually consistent** (W2§N-3): see the guarantee below. |

**The guarantee, stated truthfully (W2§N-3).** Playback is enforced on **every** request. Browsing is enforced by core library access, which Reconcile writes on a schedule, so an account that gains access to a Shoal library outside Shoal (a new account whose "all libraries" default has not been reconciled yet: 30-60 s; a dashboard edit that ticks "all libraries" or adds the library: **up to the next scheduled reconcile, hourly**; a restored state) can see titles, posters and overviews for that long, but cannot play them (step 6 answers 404). The UI and README say: "Playback is checked on every request; lists catch up within about an hour." D3's "nobody else can see" therefore means *play*, and *browse after the next reconcile*. **Deferred to v2.2** (K10 follow-up, not blocking): re-apply only the Shoal-library portion of an account's access within 10 s of a non-Shoal `OnUserUpdated` (debounced, ignoring Shoal's own writes), and add the `Shoal: <Peer>` tag to `BlockedTags` of unadmitted accounts as a backstop.

**Reconcile rules.** Shoal owns per-user access to **Shoal libraries only**. It changes access to other libraries only to preserve the "all libraries" behavior of accounts it converted. For each account U:

| U's Jellyfin library access | Rule |
|---|---|
| a Shoal share user (`IsShareUser`) | Remove every Shoal library from `EnabledFolders`; add none; never manage (N-2). With "all libraries": narrow to every non-Shoal library, unmanaged (v2.4, W6 L-6). |
| explicit list (`EnableAllFolders=false`), not managed by Shoal | `EnabledFolders` := (current list minus every Shoal library) ∪ (Shoal libraries U is admitted to). Other libraries are untouched. |
| "all libraries" (`EnableAllFolders=true`) and admitted to every Shoal library | nothing to do |
| "all libraries" and **not** admitted to some Shoal library | Jellyfin cannot exclude one library from "all libraries" through the policy API, so Shoal converts U: `EnableAllFolders=false`, `EnabledFolders` = every current non-Shoal library ∪ admitted Shoal libraries, and records U in `visibility.managedUsers` with a hash of what it wrote. |
| managed by Shoal | `EnabledFolders` recomputed the same way, so **libraries added later still appear** (reconcile runs after every scan). When U is admitted to every Shoal library again, Shoal restores `EnableAllFolders=true` and forgets U. If an admin changed U's non-Shoal libraries in the dashboard (hash mismatch), Shoal stops managing U, logs it, and treats U as an explicit list from then on. |

- Rejected alternative: core still honors a legacy `BlockedMediaFolders` preference (`BaseItem.cs:1567`, `Folder.cs:256`), which would hide one library from an "all libraries" account. But the policy API never writes it (`UserManager.cs:529` only reads it), it is invisible in the dashboard, and a stale entry turns an explicit list into "everything except", which **broadens** access. Conversion is visible and only ever narrows.
- Writes go through `IUserManager.UpdatePolicyAsync` and only when something changes. One reconcile runs at a time.
- **Triggers:** library created or removed; access mode, selection or `includeRestricted` changed; account created (`IEventConsumer<UserCreatedEventArgs>`, `UserManager.cs:365`), reconciled 30 s later **and** again after the first policy update within 60 s, because the dashboard creates an account and then posts its library access (`web:apps/dashboard/routes/users/add.tsx:128-160`); after every library scan; hourly.
- `IUserManager.OnUserUpdated` outside that creation window only refreshes the **drift list**; it does not write. The next scheduled reconcile re-applies Shoal's rule to Shoal libraries and logs "Shoal re-applied access to <Library>; change who can see it in Shoal". Shoal's access mode is the source of truth for its own libraries.
- **Drift list** (Libraries & access screen): accounts whose core access to a Shoal library differs from `Admitted`, accounts Shoal manages ("Shoal keeps this account's library list so it can hide <Library>"), and accounts Shoal stopped managing. Actions: Re-apply now, Select, Remove.
- Removing a Shoal library undoes Shoal's own changes: its id leaves every list, and managed accounts that no longer need managing get "all libraries" back.
- **Uninstalling the plugin (W2§N-13; v2.4, W6 L-5): narrow, never widen.** ~~`OnUninstalling()` and `DELETE Shoal/Admin/All` restore `EnableAllFolders=true` for every managed account~~ (v2.1 rule, withdrawn: a managed account was converted *because* it is excluded from some Shoal library, and after an uninstall the Shoal libraries, with their posters and overviews, stay in Jellyfin with no Shoal to guard them; "all libraries" would show that account the friend's catalog D3 hid from it, and could overwrite an admin's later edits). The v2.4 rule:
  1. **`DELETE Shoal/Admin/All`** ("Remove everything", the clean path) deletes the Shoal libraries **first**, then restores `EnableAllFolders=true` for every managed account whose current folder list still matches `lastWrittenFoldersHash`, then clears `managedUsers`. Once no Shoal library exists, nothing can be exposed. The danger zone and the README say "to uninstall Shoal, use Remove everything first".
  2. **`BasePlugin.OnUninstalling()`** (`BasePlugin.cs:76`, called at `InstallationManager.cs:401`; synchronous, bounded to a few seconds, best-effort) **deletes nothing**. For each managed account whose hash matches: if no Shoal library exists any more, restore `EnableAllFolders=true`; otherwise **keep the explicit list Shoal last wrote** (non-Shoal libraries plus the Shoal libraries it was admitted to), so the account sees exactly what it saw. Accounts whose hash no longer matches are skipped: an admin owns them now. One Warning names the accounts left on explicit lists and the fix ("delete the Shoal libraries, then tick 'all libraries' for these accounts").
  3. **Cost, stated in the README:** an account left on an explicit list does not see libraries created after the uninstall until an admin ticks "all libraries" for it. That is the fail-closed choice; the alternative widens access silently. Libraries are never deleted by `OnUninstalling`: it is synchronous, destructive, and a reinstall (T-REG-1) must find its stubs and watch state. Test: **T-VIS-8**.

**Answers to the picker questions** (`docs/ui-spec.md` 8.4.3, 14.3):
- Ticking an "all libraries" account selects it; its Jellyfin settings do not change.
- Leaving an "all libraries" account unticked makes Shoal convert it to a Shoal-maintained list as above. Their other libraries, including future ones, stay visible. Picker note: "Has access to all libraries. If unticked, Shoal hides this library from them and keeps the rest as it is."
- A new Shoal library is hidden from other admins too. Only the pairing admin is pre-selected (owner D3); admins can always select themselves.

The Settings screen also offers a one-click "Add the `Shoal` blocked tag to every user with a parental limit". The Home README documents the ratings caveat.

### 5.7 Offline peers and availability (owner D5)

Jellyfin has no client-rendered "unavailable" state for movies, so **greyed out titles are not achievable** in clients. Greying out applies to Shoal's own admin page: the server card shows the stored backdrop desaturated with an "Offline" tag (owner D5; `docs/ui-spec.md` section 7). The design for clients:
1. **Default: titles stay listed.** While the circuit is open or the trust window is lapsed, Play fails in under 100 ms with `ErrorCode=RateLimitExceeded`. jellyfin-web shows "This media cannot be played at this time due to rate limits." (`web:strings/en-us.json:1430`), the least misleading of the three playback error codes. The library, images and search are unaffected.
2. **Clear message:** with `NotifyClientsWhenOffline` on (default), Shoal also sends the caller's session a `DisplayMessage`: header "Shoal", text "<Peer> is offline right now. Try again later.", 8 s. It is sent through `ISessionManager.SendMessageCommand` with no controlling session (`SessionManager.cs:1273-1303`), at most once per user per peer per 5 min. jellyfin-web shows it (`web:scripts/serverNotifications.js:125`); Pilot records which other clients do (T-PLAY-16).
3. **Optional park** (`ParkOfflinePeers`, default **off**): after `ParkAfterHours` offline, rename `<StubRoot>/<peerKey>/` to `<StubRoot>/.parked/<peerKey>/` (atomic on the same filesystem), then report the change so the items disappear. Unpark after 30 min of stable health. **Risk:** item removal may drop local user data. Pilot test T-SYNC-9 decides whether parking ships at all.
4. The admin's Peers screen shows the status and stale-data age; an " (offline)" library-name suffix is phase 4 (it needs a lab check that renaming a virtual folder is cheap).

### 5.8 Overlap and storage report

Computed at the end of every complete sync from `catalog.entries` and the local index. Shown per peer as:

> You both have **N** titles (**X GB** of your storage duplicates the peer's copies). You gain **M** titles (**Y GB**) without storing them.

- **N** = count of `duplicate-local`; **X** = sum of *local* sizes of those titles (what you could free).
- **M** and **Y** = count and sum of peer sizes of `active` entries.
- Also shown: stub count, Shoal disk use against the quota, people/genre/studio counts against their caps (5.10).

The detail view adds a quality breakdown (4K, 1080p, 720p, SD) and "peer copy better than yours" counts for phase 6.

### 5.9 Scan-storm and load rules

- One sync at a time globally; at most one targeted refresh per peer per 10 min.
- No startup trigger: interval only, with the first run 10 min after boot.
- Sync skips a peer that has an active stream and is in `Degraded` state.
- Refreshes are always targeted (5.2 phase E).

### 5.10 Disk quota and entity caps (W§A-10)

| Cap | Default | On breach |
|---|---|---|
| Disk per peer (stubs + images, measured as bytes Shoal wrote) | 2 GB (`DiskQuotaGbPerPeer`, 1..50) | Stop fetching images; titles still import with placeholder art; alert. Usage is tracked in `catalog.usage.diskBytes` and re-measured weekly. |
| Entries per peer | 50,000 | Sync stops; alert |
| Actors per title | 5 | Extra people dropped |
| Distinct people, genres, studios per peer | 20,000 each | New names are dropped (the title still imports without them); alert. Bounds `Person` rows and refresh work. |
| New items per sync | `max(500, 20% of active)` | Held (5.2) |

The Peer detail screen shows each against its cap.

---

## 6. Playback

### 6.1 Interception table (federated local item ids only)

The spike intercepted only the two `PlaybackInfo` routes. Without the rest, core served the 95-byte `.strm` text from `/Videos/{stubId}/stream` and an empty playlist from `master.m3u8` (K§Surprises 5). Every row below is required for MVP.

**API-key callers** get **403** on every federated route; they never fall through to core. API keys are admin-equivalent and have no user against which to check access.

| Core route (template) | Handler | Notes |
|---|---|---|
| `POST Items/{itemId}/PlaybackInfo` (`MediaInfoController.cs:116`) | PlaybackInfoHandler | core `[Authorize]` already ran |
| `GET Items/{itemId}/PlaybackInfo` (`:72`, only `userId` query) | PlaybackInfoHandler with no DeviceProfile, so the Peer uses its defaults | rare |
| `GET/HEAD Videos/{itemId}/stream`, `stream.{container}` (`VideosController.cs:314-315,552-553`) | StaticStreamHandler, only when `Static=true` (case-insensitive) | **core route is anonymous**, so CallerContext enforces everything. Without `Static=true` it is a progressive transcode or remux: MVP returns 404 and increments a counter Pilot can read. |
| `GET Videos/{routeItemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/[{routeStartPositionTicks}/]Stream.{routeFormat}` (`SubtitleController.cs:208,295`) | SubtitleHandler | anonymous in core; clients that build subtitle URLs themselves |
| `GET Items/{itemId}/Download` (`LibraryController.cs:669`) | 403 | would serve the `.strm` |
| `GET Items/{itemId}/File` (`LibraryController.cs:109`) | 403 | would serve the `.strm` (W§A-20.3) |
| `POST LiveStreams/Open` (`MediaInfoController.cs:270`) | 400 if **any** `itemId` **query** value is federated (v2.3, W4-12: every value of a repeated key); otherwise `next()` | a body-only `ItemId` falls through to core, which fails harmlessly on the sentinel (accepted, W§A-20.3) |
| `Videos/{itemId}/master.m3u8`, `main.m3u8`, `live.m3u8`, `hls1/...`, `hls/...` (incl. the anonymous legacy segment route, `HlsSegmentController.cs:126`), `{itemId}/{mediaSourceId}/Subtitles/{i}/subtitles.m3u8`, `Trickplay/...`, `{videoId}/{mediaSourceId}/Attachments/{index}` (`VideoAttachmentsController.cs:50`) | 404 | clients must use our `TranscodingUrl`. v2.6: Trickplay tile .jpg and Attachments are served (v2.6.5, v2.6.6); tiles.m3u8 and the rest stay 404. |
| **v2.2 (G5):** `GET/HEAD Audio/{itemId}/stream`, `stream.{container}` (`AudioController.cs:88-89,252-253`), `GET/HEAD Audio/{itemId}/universal` (`UniversalAudioController.cs:92-93`), `Audio/{itemId}/master.m3u8`, `main.m3u8`, `hls1/{playlistId}/{segmentId}.{container}` (`DynamicHlsController.cs:577-578,914,1268`), legacy `Audio/{itemId}/hls/{segmentId}/stream.mp3` and `stream.aac` (`HlsSegmentController.cs:55-56`) | 404 | MVP federates movies only. Moonfin-Core already computes `Audio/{id}/main.m3u8` for E-AC3 files (`hybridAudioUrl`, unused today, SC§2.1); without this row core would try to read or ffmpeg the sentinel. Same counter as non-static `stream` |
| `DELETE Videos/ActiveEncodings?deviceId&playSessionId` (`HlsSegmentController.cs:103`) | if `playSessionId` matches a grant owned by the caller, Shoal answers it (**v2.3, W4-4**): per-user bucket of **10 per minute** (429 beyond); for a **`Transcode`** grant with `peerJobStarted`, send the **scoped** delete to the Peer (6.11), clear `peerJobStarted`, and only then release that grant's HLS slot (so a track switch's new grant can take it; a later segment on the old grant must win a slot again); for any other grant (direct play, or no Peer job yet) nothing is sent and nothing is released. Answer 204 in every case. No match: `next()` | v2.2 released any grant's slot, which let a user free the slot of a direct-play stream that kept running (the static slot is never re-checked mid-connection) and start another: a bypass of `MaxStreamsPerUser` and the per-peer limit. A direct-play slot is released only by connection close plus the 2 min grace, or `PlaybackStopped` (6.3, 6.9) |

**Filter mechanics:**
- Read `ActionDescriptor.AttributeRouteInfo.Template`; if it is not in the table, `await next()`. Matching is by template, so trailing slashes and casing are routing's problem, not ours (W§B-8).
- Parse the GUID route value. If it is not in the Shoal index, `next()` (except the path-rule fallback on the stub-reading routes, 3.5; v2.4: every row except PlaybackInfo, `ActiveEncodings` and `LiveStreams/Open`). This is a dictionary lookup with no I/O and no body read.
- **`mediaSourceId` across merged versions (v2.4, SR§4.2).** Core resolves `MediaSourceId` among the route item's sources, which include linked alternates (`MediaSourceManager.cs:378-380`), so a **local** route id with a stub's id as `mediaSourceId` would reach the stub's sentinel when the two are merged. On the StaticStream and Subtitle rows, a query `mediaSourceId` (every value, `Guid.TryParse`) that is federated and differs from the route id answers **404** before CallerContext and before any Peer call, the W4-3 rule extended. The merged-version warning below stays.
- **Coverage is tested against the real server (v2.4, SR§0, §6.2).** K13's test enumerates the action descriptors of the targeted Jellyfin.Api (a minimal host with Jellyfin.Api as an application part and `IActionDescriptorCollectionProvider`; if that cannot be hosted in a unit test, the assembly's attribute routes read with `MetadataLoadContext` and combined as MVC combines them: controller `[Route]`, `[controller]` replaced, plus the action template). **Never** swagger or `openapi.json`: `DynamicHlsController` and `HlsSegmentController` are `IgnoreApi`, which hides 55 real routes. Every template that starts `Videos/{` or `Audio/{`, plus `Items/{x}/File`, `Download`, `PlaybackInfo`, `LiveStreams/Open` and `Videos/ActiveEncodings`, must be in this table or on an explicit allow-list of non-byte routes (`AdditionalParts`, `AlternateSources`, `Lyrics`, `RemoteSearch/*`, `Subtitles` POST and DELETE, `MergeVersions`). On 12.1 the covered set is 35 verb entries (26 templates); a byte route added by a later server fails the build. The assembly is the pinned, hash-checked release that `targetAbi` names.
- **Legacy prefixes (v2.4, SR§2).** Whether 12.1 still serves `/emby/...` and `/mediabrowser/...` is unverified; the filter keys on the action template, so a prefixed request gets the same decision if it is served at all. T-CLI-6 checks it.
- **Write and delete routes on stubs (v2.4, SR§4.5)** stay pass-through: `DELETE Items/{id}`, `POST Items/{id}/Refresh`, metadata edits, image and subtitle uploads need admin or a user permission and act on the stub folder only (a refresh runs the NFO reader and, per 5.6, no remote fetcher). Sync is the source of truth: a deleted stub returns at the next sync (README).
- **Parse exactly as MVC does (v2.3, W4-12).** (1) Read the query only through `Request.Query`, never `Request.QueryString.Value`: core's `UrlDecodeQueryFeature` re-parses a fully percent-encoded query (`?static%3Dtrue%26...`) into real keys, and the filter and the model binder must read the same collection. (2) GUIDs, in the route and in `itemId`, are parsed with `Guid.TryParse` (N, D, B, P and X forms, any case, surrounding whitespace), the grammar the binder accepts; never `ParseExact("N")` or `"D"`, which would let `{guid}` or `(guid)` through to core. (3) A repeated query key is evaluated on **every** value where it decides "federated or not" (`itemId`): the binder takes the first value, while `Query["itemId"].ToString()` of two values is `a,b`, which parses as nothing.
- **Subtitle route, obsolete query overrides (v2.2; v2.3 W4-3).** Core's subtitle action lets the obsolete query values `itemId`, `mediaSourceId`, `index` and `format` override the route values (`SubtitleController.cs:216-229`), and the route is anonymous, so the filter's decision is the only gate. For that template the request is federated when the route id **or any parsed `itemId` value** is federated. Then one rule: **the id that is authorized is the id that is served, and it is the route id.** If any `itemId` value is present and is not equal to the route id (an unparseable value counts as not equal), the answer is **404** before CallerContext and before any Peer call, whichever of the two is federated. Otherwise SubtitleHandler runs on the route values only. (v2.2 covered only "local route id, federated query id"; a federated route id with a *different* federated query id is now covered too.)
- **Empty container (v2.2, G6).** `Videos/{id}/stream.` with nothing after the dot matches no core route (the `{container}` segment is required), so routing answers 404 and no core action runs. T-CLI-5 pins it.
- **Logging (v2.2).** Clients put their token in the query (`ApiKey`, SC§2.1-2.2), so no Shoal log line, exception message or counter label contains a client request's URL, path-with-query or query string. Shoal logs the route template, the status and redacted ids. **v2.3 (W4-14):** the analyzer enforces it: `HttpRequest.QueryString`, `Path`, `PathBase`, `GetDisplayUrl`, `GetEncodedUrl` and `GetEncodedPathAndQuery` are banned outside one helper, `ShoalRequest`, which does the 8 KB query-length check and logs route templates only (8.8). (Core's own request logging is outside Shoal: `ResponseTimeMiddleware` logs `GetDisplayUrl()`, the full URL with `ApiKey`, at **Debug** for every response slower than 500 ms, which every cold subtitle fetch and long stream start trips; so the README keeps "redact Debug logs, they contain user tokens", 11.5.)
- Otherwise run CallerContext (8.5), set `context.Result`, and **never** call `next()`.
- The POST body is read only after the item is known to be federated, so core still binds bodies for normal items.
- **Boundary rule (W§B-11):** each handler is wrapped in `catch (Exception)`, excluding `OperationCanceledException` when `RequestAborted` fired. It logs the exception **type** and redacted ids at Warning, and returns 502 (peer-facing failure) or 500 (local), written by `ResponseWriter`. Nothing reaches core's `ExceptionMiddleware`, which would log socket messages containing the peer host.
- **Merged versions (W§A-20.2):** if a stub item has linked alternate versions, or is an alternate of a local item (`PrimaryVersionId` set), core may serve the stub's sentinel source under the local item's id. `StubEnricher` detects this after scans, and the admin sees "Unmerge <title>: Shoal titles cannot be merged with local copies". It is safe (the sentinel leaks nothing) but broken.

### 6.2 PlaybackInfo handler

1. **Caller and limits.**
   - CallerContext (8.5) for the route item: 401, 403 or 404 per its steps.
   - Rate limit: token bucket per user, 30 per minute, burst 15 (v2.2, G8; was 20 per minute). A Moonfin Smart-TV start makes up to 5 calls in a few seconds (SC§2.2) and Moonfin-Core retries twice on 400/422/500 (SC§2.1).
   - Peer state: `AuthFailed`, `IdentityMismatch`, `TlsKeyChanged`, `Disabled` or recovery mode give `NotAllowed`. An open circuit or a lapsed trust window give `RateLimitExceeded` plus the offline notice (5.7). **v2.4 (KQ 16):** the display status `Offline` is not a gate of its own: it is derived from the circuit (HealthMonitor's failed calls open it), and before K20 it is only the initial "not measured" value, so gating on it would refuse every play.
   - **Slot pre-check (v2.4, K15, through the `ISlotLedger` seam):** if the caller already holds `MaxStreamsPerUser` stream slots, or holds half (rounded up) of this peer's `maxConcurrentStreams`, or the peer's stream slots are all held, `RateLimitExceeded` before any Peer call. Nothing is reserved; the first byte or segment stays the authoritative check (6.9).
   - Grant capacity pre-check in the 3.5 order (v2.3, W4-6): a replaceable grant for this key, else an evictable slot-free grant of this user if they hold 8, else room under the global 200; otherwise `RateLimitExceeded`, before any Peer call. Step 6 repeats the same order atomically; losing a race there also answers `RateLimitExceeded`. **No stream slot is taken here** (6.9).
2. **Read the request.** Body ≤ 256 KB, JSON depth 32. Query and body are merged with the precedence of `MediaInfoController.cs:116-209` into a typed `PlaybackInfoDto`, then validated per 6.2.1. Unknown JSON members and unknown query keys are ignored, never forwarded. **The merge rule, confirmed for v2.2** (Moonfin-Core sends six values in both places, SC§2.1; the others send the body only):
   - Per field, the **query value wins** when present; otherwise the body value; otherwise core's default (`AutoOpenLiveStream=false`; `EnableDirectPlay`, `EnableDirectStream`, `EnableTranscoding`, `AllowVideoStreamCopy`, `AllowAudioStreamCopy` = `true`). Fields: `MaxStreamingBitrate`, `StartTimeTicks`, `AudioStreamIndex`, `SubtitleStreamIndex`, `MaxAudioChannels`, `MediaSourceId`, `LiveStreamId`, `AutoOpenLiveStream` and the five flags (`MediaInfoController.cs:149-165`). Query keys match case-insensitively.
   - `DeviceProfile` and `AlwaysBurnInSubtitleWhenTranscoding` are **body only**.
   - A query value that does not parse as its type (for example `audioStreamIndex=abc`) → 400, as core's model binding would answer.
   - `UserId`, in the query or the body, is **ignored**: the user is always the caller (core lets an admin act for another user through it, `RequestHelpers.GetUserId`; Shoal does not).
   - **No profile at all:** core falls back to the profile the caller's device registered through `Sessions/Capabilities` (`MediaInfoController.cs:137-147`). Shoal does the same with Home's `IDeviceManager.GetCapabilities(caller.DeviceId)`, **but only when the device row for that id belongs to the caller** (`IDeviceManager.GetDevices(new DeviceQuery { DeviceId = caller.DeviceId, UserId = caller.UserId })` returns it; v2.3, W4-2); otherwise the request counts as having no profile. That profile goes through the same rebuild (6.2.2). Only when neither exists is `DeviceProfile` omitted from the forwarded body. *Residual (recorded):* the check stops a caller borrowing another device's profile, not another user overwriting the capabilities stored under a device id they know, because core stores them by device id alone (`SessionManager.cs:1901`); the rebuild bounds the effect to codec choices for that device, as in core.
3. **Build the forwarded body** exactly per 6.2.1. **No query string** is sent to the Peer.
4. **Call the Peer:** `POST /Items/{peerItemId}/PlaybackInfo`, with the deadlines and cap of 6.10 (2 MB, depth 32). A Peer `ErrorCode` is relayed as the enum only; a peer error body is never relayed.
5. **Pick exactly one source:** the one with `Id == peerMediaSourceId` (from the catalog), else `[0]`. All other peer sources are dropped (spike gap b). If the client sent a `MediaSourceId` other than the local item id (N or D format), return 400.
6. **Conversion pre-check (v2.4, K15).** If the chosen source offers no direct play (`SupportsDirectPlay=false`) and only a `TranscodingUrl`, and all of the peer's conversion slots (`maxConcurrentTranscodes`, 6.9) are held, answer `RateLimitExceeded`: the Peer starts no ffmpeg job before the master or first segment request, so nothing runs on the friend's server. A source that also offers direct play is returned unchanged; if the client converts anyway, the first segment gets 429. **Create a grant** (3.5; keyed by user, item and the capped DeviceId; replacement, then own eviction, then the global cap) and return a `PlaybackInfoResponse` built from scratch (6.2.3), with `PlaySessionId = grant.localPlaySessionId` and `MediaSources = [s]`.
7. **Canary check before returning.** Serialize the response and assert it contains none of: the peer token, peer item, media source or play-session ids (N and D formats), or the peer host. Host matching is done **on label boundaries**: the host must not be preceded or followed by `[A-Za-z0-9.-]`, so a short host like `tv` does not match "TV" in a title (W§B-10). On a hit, return 502, log `Redacted canary hit in PlaybackInfo`, and alert the admin. This runs in release builds; the cost is microseconds.

#### 6.2.1 Exact body sent to the Peer (W§B-4)

| `PlaybackInfoDto` field | Value sent | Source and validation |
|---|---|---|
| `UserId` | omitted | |
| `MediaSourceId` | `peerMediaSourceId` | catalog |
| `LiveStreamId` | omitted | |
| `AutoOpenLiveStream` | `false` | fixed |
| `DeviceProfile` | rebuilt (6.2.2), or omitted if the client sent none | client, rebuilt |
| `MaxStreamingBitrate` | min(client value, the local user's `RemoteClientBitrateLimit` and the server-wide remote limit when the caller is remote, the link budget) | computed. The link budget is `latest median Mbps / 1.5` (phase 4, `AutoFitQualityToLink`). This replaces core's "remote sources ignore bitrate limits" exemption (`StreamBuilder.cs:1655-1660`). |
| `StartTimeTicks` | client value clamped to 0..runtime | client, `long` |
| `AudioStreamIndex` | client value if 0..127, else omitted | client |
| `SubtitleStreamIndex` | client value if -1..127, else omitted | client |
| `MaxAudioChannels` | client value if 1..32, else omitted | client |
| `EnableDirectPlay`, `EnableDirectStream` | client value AND NOT the local user's `ForceRemoteSourceTranscoding` | client, local policy |
| `EnableTranscoding` | client value AND the local user's `EnableVideoPlaybackTranscoding` | client, local policy |
| `AllowVideoStreamCopy` | client value AND `EnablePlaybackRemuxing` AND NOT `ForceRemoteSourceTranscoding` | client, local policy |
| `AllowAudioStreamCopy` | client value | client |
| `AlwaysBurnInSubtitleWhenTranscoding` | client value | client |

#### 6.2.2 DeviceProfile rebuild (W§A-15)

The client's profile is deserialized into `MediaBrowser.Model.Dlna.DeviceProfile` and a **new** object is built:
- `Name = "Shoal"`, `Id = null`. The client's name, model and manufacturer never reach the Peer.
- `MaxStreamingBitrate` = the value from 6.2.1. `MaxStaticBitrate` = min(client, that value). `MusicStreamingTranscodingBitrate` and `MaxStaticMusicBitrate` = client values clamped to 0..1 Gbps.
- List caps: `DirectPlayProfiles` ≤ 64, `TranscodingProfiles` ≤ 32, `ContainerProfiles` ≤ 32, `CodecProfiles` ≤ 64, `SubtitleProfiles` ≤ 32; `Conditions` and `ApplyConditions` ≤ 32 per entry. Excess entries are truncated.
- Every string inside the profiles (containers, codecs, condition values, subtitle formats) must match `^[A-Za-z0-9 ,._|:+/-]{0,256}$`, or that entry is dropped. Enums come from typed deserialization; an unknown enum means 400 to the client.
- **HLS-subtitle kill switch:** with `HlsSubtitlesEnabled=false`, `SubtitleProfiles` entries with `Method=Hls` are removed, so the Peer picks External or burn-in.
- **External subtitles only in formats Shoal serves (v2.3, W4-10; v2.2 removed bitmap formats only).** `SubtitleProfiles` entries with `Method=External` are kept only when `Format` (case-insensitive) is one of `srt`, `subrip`, `ass`, `ssa`, `vtt`, `webvtt`, `js`, `json`; every other `External` entry is removed. This subsumes v2.2's bitmap rule (every format `IsPgsFormat` or `IsVobSubFormat` matches, `dvbsub`, and the exact string `sub`, which core's `IsTextFormat` treats as bitmap, `MediaStream.cs:741-752`), and also stops `smi`, `ttml` and unknown formats (core's `IsTextFormat` is a blocklist, so `xsub` or `hdmv_text_subtitle` would count as text) from producing a `DeliveryUrl` nobody serves. The Peer picks `External` only when the profile's format matches (`StreamBuilder.cs:1600-1618`), so removing the entry is enough. For bitmap tracks, Shoal cannot deliver the files (6.6a), so the Peer must choose `Embed` (the track stays in the container, and Moonfin Smart-TV renders it in-band on direct play, SC§2.2) or `Encode` (the Peer burns it in, as the owner's test 01 showed). Without this, a client that lists `pgssub External` (Moonfin's TV and Xbox profiles) would get a stream with a delivery URL nobody serves.

#### 6.2.3 Response construction (whitelist; W§A-12, W§B-10)

| Field of `s` | Value |
|---|---|
| `Id` | local item id, `N` format, so session reporting resolves (`MediaSourceManager.cs:371-381`) |
| `Protocol` / `IsRemote` / `Path` | `File` / `false` / `null`. jellyfin-web then builds `Videos/{item.Id}/stream.{container}?Static=true&mediaSourceId=..&ApiKey=..` (`web:playbackmanager.js:2884-2902`), which the filter catches |
| `Container` | **kept populated** (v2.2, G6): the Peer's value if it passes 5.5, else the catalog's container (also 5.5-sanitized at sync), and `null` only when neither is valid. Moonfin Smart-TV builds `stream.{Container}` from it and Moonfin's Android player picks its demuxer from it (SC§2.1-2.2), so a `null` breaks direct play on those clients. Comma lists are kept whole |
| `Size`, `Bitrate`, `RunTimeTicks`, `VideoType`, `Video3DFormat`, `Timestamp`, `Formats` (≤ 8, `^[a-z0-9]{1,16}$`), `TranscodeReasons`, `DefaultAudioStreamIndex`, `DefaultSubtitleStreamIndex`, `SupportsDirectPlay`, `SupportsDirectStream`, `SupportsTranscoding`, `TranscodingSubProtocol`, `TranscodingContainer` (as `Container`), `AnalyzeDurationMs`, `IgnoreDts`, `IgnoreIndex`, `GenPtsInput`, `ReadAtNativeFramerate` | copied after type and range checks |
| `Name` | text rules (5.5), ≤ 200 |
| `ETag` | `hex(SHA256(stubKey + peerEtag))[0..32]` (never the Peer's) |
| `TranscodingUrl` | `/Shoal/Hls/{grantId}/master.m3u8?ApiKey=<caller token>` if the Peer returned one. The `ApiKey` echoes the caller's own token, as core does (`MediaInfoHelper.cs:323-325`), because hls.js sends no headers. The Peer's `TranscodingUrl` path must match `^/videos/<peerItemId>/master\.m3u8\?` (case-insensitive) and its query passes the query rules of 6.11, or the response is 502. |
| `MediaStreams` | rebuilt per the table below; ≤ 128 streams |
| `MediaAttachments` | `[]` by default (Peer `DeliveryUrl` embeds peer ids, `MediaInfoHelper.cs:350-358`); fonts only, rebuilt, when `AttachmentsEnabled` (v2.6.6) |
| `RequiredHttpHeaders`, `OpenToken`, `LiveStreamId`, `EncoderPath` | empty or null |
| `RequiresOpening`, `RequiresClosing`, `IsInfiniteStream`, `SupportsProbing`, `HasSegments` | `false` |
| every other field, including any added in later Jellyfin versions | default value (not copied) |

**`MediaStream` rebuild.** jellyfin-web puts `DisplayTitle` into `innerHTML` unescaped (`web:itemDetails/index.js:259,282,312`), and other clients are unknown, so every field has a rule. `DisplayTitle` is a computed property (`MediaStream.cs:265`): core recomputes it on Home from the sanitized fields below. The peer's value is never used.

| Field | Rule |
|---|---|
| `Type` | `Video`, `Audio` or `Subtitle`; any other type drops the stream |
| `Index` | 0..127, unique |
| `Codec` | lowercase `^[a-z0-9_.-]{1,32}$`, else null |
| `CodecTag` | `^[A-Za-z0-9]{1,8}$`, else null |
| `Profile` | `^[A-Za-z0-9 .:+_/-]{1,48}$`, else null |
| `Language` | `^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$`, else null |
| `Title` | text rules (5.5) plus remove `"'&` and backtick, ≤ 64, else null |
| `ChannelLayout` | `^[a-z0-9.() _-]{1,32}$`, else null |
| `ColorRange`, `ColorSpace`, `ColorTransfer`, `ColorPrimaries`, `PixelFormat` | `^[a-z0-9_-]{1,32}$`, else null |
| `AspectRatio` | `^[0-9]{1,5}:[0-9]{1,5}$`, else null |
| `Channels`, `BitRate`, `BitDepth`, `RefFrames`, `Height`, `Width`, `SampleRate`, `Level`, `AverageFrameRate`, `RealFrameRate`, `DvVersionMajor/Minor`, `DvProfile`, `DvLevel`, `RpuPresentFlag`, `ElPresentFlag`, `BlPresentFlag`, `DvBlSignalCompatibilityId`, `Rotation` | numeric range checks, else null |
| `IsDefault`, `IsForced`, `IsHearingImpaired`, `IsOriginal`, `IsInterlaced`, `IsAVC`, `IsAnamorphic`, `Hdr10PlusPresentFlag`, `SupportsExternalStream` | copied booleans |
| `IsExternal`, `DeliveryMethod` | copied; `DeliveryUrl` rebuilt per 6.6 or null |
| `Path` | null |
| `IsExternalUrl` | a stream with `IsExternalUrl=true`, or whose peer `DeliveryUrl` or `Path` is an absolute URL, is **removed** (`StreamInfo.cs:1263-1274` would hand clients a peer-chosen URL) |
| `Comment`, `TimeBase`, `CodecTimeBase`, `NalLengthSize`, `Localized*`, `Score` | not copied |

The same sanitized streams are stored in the grant: they drive subtitle names in HLS (6.4) and the subtitle index checks. `StubEnricher` never writes stream text onto stub items.

**DirectStream: always `false` (v2.2, G7; v2.1 cleared it only when `SupportsDirectPlay=false` with a `TranscodingUrl`).** jellyfin-web's DirectStream branch requests a `Static=true` URL (`web:playbackmanager.js:2884-2902`), which would serve the raw file the client just said it cannot play. No supported client needs it: Jellyfin never emits `DirectStreamUrl`, so Moonfin falls through to `TranscodingUrl` (SC§2.1-2.2), and a remux the Peer chooses arrives as an HLS `TranscodingUrl` with copied streams.

**Why `Protocol=File`, `IsRemote=false`, `Path=null`, and nothing else** (v2.2, G7: the v2.1 fallback "the spike's absolute `Http` Path" is **deleted**):
- (a) Direct play goes through the same `/Videos/{localId}/stream[.{container}]?Static=true` URL every client already builds for local files (jellyfin-web, Moonfin-Core, Moonfin Smart-TV, Roku, jellyfin-android, SC§3), and the filter intercepts that route. One code path.
- (b) **An absolute `Http` Path or `IsRemote=true` breaks or bypasses real clients** (SC§G7): the official Android TV app's new player drops every source that is not `Protocol=File && !IsRemote` (`JellyfinMediaStreamResolver.kt:104-108`); jellyfin-android plays an `Http` Path as HLS (`QueueManager.kt:361-365`); Moonfin (`media_stream_resolver.dart:176-199`) and Roku (`LoadVideoContentTask.bs:586-592`) play `Path` raw, skipping the filter.
- (c) The spike built the absolute URL from `Request.Scheme/Host/PathBase`. Behind a TLS-terminating proxy without Jellyfin's known-proxy settings, that yields `http://` on an `https://` page, or a wrong host.
- (d) No client-visible URL is needed at all, because the client prefixes its own server address and `BaseUrl`.

There is no fallback. A client that insists on `Path` for direct play is unsupported until a new design round.

### 6.3 Direct play: static stream proxy (Range)

1. **CallerContext** (8.5), all steps: authenticated user (API keys denied), enabled, default policy (remote access, schedule), visible, admitted by the access mode, `EnableMediaPlayback`. Otherwise 401, 403 or 404. A request with **no credentials at all gets 401**, including from apps that send none by design (G1, 6.12); Shoal never serves a federated title anonymously.
2. **Peer state:** inside the trust window (4.4) with a closed circuit, else 503 with no body (the client shows a network error; the offline notice of 5.7 is sent).
3. **Slot** (6.9): keyed by (user, item). Taken at the first request, shared by that stream's Range requests (and by every live grant of that user and item, 3.5 `hasSlot`), released 2 min after the last byte or on `PlaybackStopped`, **never** by `ActiveEncodings` (v2.3, W4-4). Request rate per user: 20 per second, burst 100.
4. **Request to the Peer** (exact form in 6.11): `GET|HEAD /Videos/{peerItemId}/stream?static=true&mediaSourceId={peerMsId}` plus `Range`. Only a single `bytes=a-b`, `a-` or `-n` range is forwarded; multi-range or malformed ranges get 416. `ResponseHeadersRead`; deadlines per 6.10.
5. **Validate the response:**
   - status ∈ {200, 206, 416}; any 3xx is an error
   - upstream `Content-Type` in the static-stream allowlist of 8.6
   - `Content-Length` ≤ catalog size × 1.01 when known, else ≤ 1 TiB; `Content-Range` well-formed and consistent with the request
6. **Response:** `ResponseWriter` emits our own `Content-Type` from the **catalog** container (8.6; for a comma list, its first entry), never from the URL's `.{container}` or `Container=` query, which the client chose plus `Content-Length`, `Content-Range`, `Accept-Ranges`, `Last-Modified`, and the 8.6 security headers. `Content-Length`, `Content-Range` and `Last-Modified` are **parsed to numbers and a date and re-serialized by Home**, never copied as raw text (W2§N-11); `Accept-Ranges` is Home's constant `bytes`. Nothing else from the Peer.
7. **Copy the body** with an 81 KB buffer through `BoundedReadStream`: idle read 30 s, stop at the declared length (a short body is an error), cancelled by `HttpContext.RequestAborted`. A seek is a new Range request.

### 6.4 HLS transcode proxy: a closed grammar (W§A-2, W§B-1)

**Endpoints:** `GET Shoal/Hls/{grantId}/master.m3u8`, `/main.m3u8`, `/hls1/main/{segmentId}.{ext}` (`segmentId` -1..999999, `ext` ∈ {`ts`, `mp4`}), `/subs/{index}/subtitles.m3u8`, `/subs/{index}/stream.vtt`.

- Auth: `[Authorize]`. CallerContext steps 1-4 on every request; `grant.localUserId == caller`, else **404** (never 403); grant not expired. Steps 5-7 re-run when `lastAuthorizedUtc` is older than 5 min (W§A-19); a failure kills the grant and returns 404.
- Rate limit per grant: 10 requests per second sustained, burst 40. A stream slot (6.9) is taken at the first segment request, and forwarding it sets the grant's `peerJobStarted` (3.5).
- Why grants: the Peer-side query comes **only** from the Peer's own answers, stored server-side. The client influences only validated integers (6.11). The spike's stateless proxy let any user dictate peer transcode parameters (W§B-4).
- **Client query (v2.2, G2; v2.3, W4-11).** Clients rewrite the URL they were given: Moonfin adds or replaces `AudioStreamIndex` and `SubtitleStreamIndex` and strips `StartTimeTicks` (`media_stream_resolver.dart:187-189,255-286`); Moonfin Smart-TV appends `upmix=true` (`playback.js:316-318`). On every `/Shoal/Hls/...` route the client query is read only as follows. **An unknown, extra, repeated or mutated key never causes a 4xx *from Shoal*; only the four validated tick keys can (400). The credential keys are judged by core first**, and that is intended:
  - `ApiKey` / `api_key`: consumed by core's authentication (8.5 step 1) before Shoal runs; never forwarded. Core reads `ApiKey` in any letter case; a **repeated** `ApiKey` becomes the joined string `a,b`, which matches no token, and `api_key` is read only with legacy authorization on (off by default). Both are therefore **core's 401** (P2§6 T-CLI-3 credential variants; `AuthorizationContext.cs:93-110`). Shoal never parses credentials (8.8) and must not "fix" this. The supported clients do not trigger it (Moonfin skips URLs that already carry a key; Smart-TV builds its own).
  - `AudioStreamIndex`, `SubtitleStreamIndex` (master and main only): parsed as integers and compared with the grant's values. Equal, absent, unparseable or repeated (treated as unparseable): nothing happens. Different: the grant's value is used and the per-grant counter `clientIndexMismatch` increments. That counter is an **admin-only debug aid** (Settings > Troubleshooting and the admin API, never a public metric), counted without value labels. A real track switch is a new PlaybackInfo (and a new grant), which is what Moonfin does (SC§2.1).
  - `runtimeTicks`, `actualSegmentLengthTicks` (segments): validated per rule U-V; a value that fails, or a **repeated** key (U-V says "exactly one"), → 400.
  - `StartPositionTicks`, `EndPositionTicks` (`subs/{index}/stream.vtt`): validated per rule U-S, same rule, repeated → 400.
  - Everything else (`upmix`, `StartTimeTicks`, `PlaySessionId`, `DeviceId`, `MediaSourceId`, unknown keys): ignored, never forwarded, not counted.
  - A query longer than 8 KB → 414. That is a size cap, not a key rule.
  The Peer request is still built only from the grant plus the validated integers (6.11). T-CLI-3 and T-SEC-14.

**Peer requests** (exact forms in 6.11): master from `grant.peerMasterQuery` (from the Peer's `TranscodingUrl`, `EnableAdaptiveBitrateStreaming=false` forced); main from `grant.peerVariantQuery` (from the master, rule U-M); segments from the variant query plus the two validated integers; subtitle playlist and segments built by Home. Size caps and deadlines in 6.10. Upstream Content-Type per 8.6.

**The grammar.** One parser handles three playlist kinds: master (**M**), media playlist "main" (**V**) and subtitle media playlist (**S**). It is closed. Each line is either recognized by exactly one rule below and **rebuilt by Home from parsed values**, or it is dropped (tags) or the whole playlist is rejected with 502 (URI lines, structure errors). No input text is copied to the output, except values that matched a capture pattern and were converted to integers, enums, or Home's own sanitized strings.

**Input rules (all kinds):**
- **I1** Strict UTF-8 decoding; invalid bytes → 502. A UTF-8 BOM at offset 0 is skipped.
- **I2** Lines split on LF; a CR directly before LF is removed. Any other C0 control (including TAB, lone CR and NUL), DEL, U+0085, U+2028 or U+2029 anywhere → 502.
- **I3** Line ≤ 8 KB. Structural caps per playlist kind are in **I8** (bytes, lines, segment count, segment duration, total duration). Over any of them → 502.
- **I4** Line 1 must be exactly `#EXTM3U`.
- **I5** Blank lines are dropped.
- **I6** A line starting with `#EXT` is a tag; its name is the text up to the first `:` or end of line, compared with `StringComparison.Ordinal`. RFC 8216 tag names are uppercase, so `#ext-x-key` is an unknown tag and dropped. A line starting with `#` but not `#EXT` is a comment and is dropped. Every other line is a URI line.
- **I7** Regexes: explicit ASCII classes only (`[0-9]`, `[A-Za-z]`; never `\d`, `\w`, `\s`), whole-line anchors, `RegexOptions.CultureInvariant | RegexOptions.NonBacktracking`, no `IgnoreCase`, 100 ms timeout. In .NET, `\d` matches non-ASCII digits and case-insensitive classes match U+212A KELVIN SIGN (W§A-2). **Anchors are `^` and `\z`** (the `$` written in the tables below is shorthand for `\z`), because .NET `$` also matches before a trailing `\n`; lines are split first and a pattern is never run against unsplit text. `NonBacktracking` finds the same *match* as a backtracking engine but may report capture groups differently for ambiguous patterns (the loose `.*` of rule MD), so the code re-validates every captured value after matching and the F-PL fixtures pin the captured group for a crafted `NAME` (F-PL-30). Numeric attributes (`BANDWIDTH`, `AVERAGE-BANDWIDTH`, ticks) are parsed as `long`, never `int`.
- **I8 Structural caps (W2§N-11).** A playlist that exceeds any cap is rejected with 502 as soon as the cap is crossed, before further parsing; nothing is clamped.

  | Kind | Max bytes (6.10) | Max lines | Max segments | Max single `EXTINF` | Max sum of `EXTINF` |
  |---|---|---|---|---|---|
  | M | 256 KB | 64 | n/a | n/a | n/a |
  | V | 8 MB | 100,000 | 20,000 | 600 s | `grant.runtime × 1.1 + 1 h` |
  | S | 2 MB | 20,000 | 10,000 | 600 s | `grant.runtime × 1.1 + 1 h` |

  Without the segment-count and sum caps, 100,000 segments of 86,400 s each would make hls.js build a multi-day timeline within the line limit. The single-`EXTINF` cap of 600 s matches the `actualSegmentLengthTicks` cap in U-V. Each singleton tag (`#EXT-X-VERSION`, `#EXT-X-TARGETDURATION`, `#EXT-X-MEDIA-SEQUENCE`, `#EXT-X-PLAYLIST-TYPE`, `#EXT-X-DISCONTINUITY-SEQUENCE`) is emitted **once**: the first occurrence wins and a repeat is dropped and counted. Segment **bytes** are capped separately by `BoundedReadStream` (256 MB per HLS segment, 2 MB per subtitle segment, 6.10), so no single segment can exceed its cap whatever the playlist claims.

In the patterns below, **`Q`** = `[A-Za-z0-9._~%=&,+-]{0,4096}`: no `/`, `:`, `?`, `#`, `@`, `\`, quotes or spaces, so no scheme, authority or path can hide in a query. Query pairs are split on `&` and `=`, percent-decoded, and keys compared ordinal-ignore-case.

**Tag rules** (anything not listed is **dropped** and counted per grant):

| Kinds | Input (whole line) | Output |
|---|---|---|
| M V S | `#EXTM3U` (line 1 only) | `#EXTM3U` |
| V S | `^#EXT-X-VERSION:([0-9]{1,2})$`, value 3..7 | `#EXT-X-VERSION:<n>` |
| V S | `^#EXT-X-TARGETDURATION:([0-9]{1,5})$` | `#EXT-X-TARGETDURATION:<n>` |
| V S | `^#EXT-X-MEDIA-SEQUENCE:([0-9]{1,9})$` | `#EXT-X-MEDIA-SEQUENCE:<n>` |
| V S | `^#EXT-X-PLAYLIST-TYPE:VOD$`; any other value → 502 | `#EXT-X-PLAYLIST-TYPE:VOD` |
| V | `#EXT-X-INDEPENDENT-SEGMENTS`, `#EXT-X-DISCONTINUITY` (exact) | same |
| V | `^#EXT-X-DISCONTINUITY-SEQUENCE:([0-9]{1,9})$` | rebuilt |
| V S | `^#EXTINF:([0-9]{1,6}(?:[.,][0-9]{1,17})?(?:E-[0-9]{1,3})?),(?: nodesc)?$`. The value is parsed as a double after replacing `,` with `.` (core writes S durations in the server's culture, `SubtitleController.cs:389-391`), must be finite, 0..600 (I8), and must be followed by exactly one URI line. | `#EXTINF:<value, invariant "0.000###">,` |
| V | `^#EXT-X-MAP:URI="hls1/main/-1\.mp4\?(Q)"$` (format from `DynamicHlsPlaylistGenerator.cs:70-81`) | `#EXT-X-MAP:URI="hls1/main/-1.mp4?ApiKey=<t>&runtimeTicks=0&actualSegmentLengthTicks=0"` |
| V S | `#EXT-X-ENDLIST`; **required** as the last tag of V and S, else 502 (live and event playlists are not supported) | same |
| M | the **first** `#EXT-X-STREAM-INF:<attrs>` | rebuilt per rule SI |
| M | `#EXT-X-MEDIA:...` matching rule MD | rebuilt from grant data |
| M V S | `#EXT-X-KEY`, `#EXT-X-SESSION-KEY` | **502** (encrypted output is never ours) |
| M | second and later `#EXT-X-STREAM-INF` | dropped together with its URI line (which must still match U-M, else 502) |
| all | `#EXT-X-IMAGE-STREAM-INF` (trickplay), `#EXT-X-DATERANGE` (incl. `X-ASSET-URI`, `X-ASSET-LIST`), `#EXT-X-CONTENT-STEERING`, `#EXT-X-SESSION-DATA`, `#EXT-X-DEFINE`, `#EXT-X-I-FRAME-STREAM-INF`, `#EXT-X-BYTERANGE`, `#EXT-X-START`, `#EXT-X-PROGRAM-DATE-TIME`, `#EXT-X-GAP`, `#EXT-X-PART*`, `#EXT-X-PRELOAD-HINT`, `#EXT-X-SERVER-CONTROL`, `#EXT-X-SKIP`, `#EXT-X-RENDITION-REPORT`, and every unknown tag | dropped (named only so tests enumerate them) |

**Rule SI (`EXT-X-STREAM-INF`).** The attribute list is parsed strictly per RFC 8216 §4.2: `NAME=VALUE` pairs separated by `,`, names `[A-Z0-9-]+`, values that are integers, enumerated strings `[A-Z0-9-]+`, resolutions, decimals, or quoted strings `"[^"\r\n]*"`. A syntax error or duplicate name → 502. Kept attributes, each with its own value pattern; all others are dropped:
- `BANDWIDTH` `[0-9]{1,10}` (required), `AVERAGE-BANDWIDTH` `[0-9]{1,10}`
- `VIDEO-RANGE` `SDR`, `PQ` or `HLG`
- `CODECS` `"[A-Za-z0-9.,+-]{1,256}"`, `SUPPLEMENTAL-CODECS` `"[A-Za-z0-9.,/+-]{1,256}"`
- `RESOLUTION` `[0-9]{1,5}x[0-9]{1,5}`, `FRAME-RATE` `[0-9]{1,3}(\.[0-9]{1,3})?`
- `SUBTITLES` exactly `"subs"`, kept only if at least one subtitle rendition was emitted

Output order is fixed: BANDWIDTH, AVERAGE-BANDWIDTH, VIDEO-RANGE, CODECS, SUPPLEMENTAL-CODECS, RESOLUTION, FRAME-RATE, SUBTITLES. Only the first variant is kept; core's backward-compatibility variants (DoVi first when declared, then SDR AV1/HEVC, `DynamicHlsHelper.cs:220-260`) are dropped in MVP.

**Rule MD (`EXT-X-MEDIA`, subtitle renditions).** Accepted only in the exact form core emits (`DynamicHlsHelper.cs:683-709`):

`^#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME=".*",DEFAULT=(YES|NO),FORCED=(YES|NO),AUTOSELECT=YES,URI="([0-9a-fA-F]{32}|[0-9a-fA-F-]{36})/Subtitles/([0-9]{1,3})/subtitles\.m3u8\?(Q)",LANGUAGE=".*"$`

- `NAME` and `LANGUAGE` are matched loosely because core does not escape them, and they are **never used**.
- The captured media-source id must equal `grant.peerMediaSourceId` (parsed as a GUID), and the index must be a text subtitle stream in the grant's sanitized streams.
- A peer `NAME` crafted to contain a fake `URI="..."` can at worst make the match pick a different valid index from the grant (a wrong label, not an injection), because the output is built from grant data only.
- Output: `#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="<n>",DEFAULT=<YES|NO>,FORCED=<YES|NO>,AUTOSELECT=YES,URI="subs/<i>/subtitles.m3u8?ApiKey=<t>",LANGUAGE="<l>"`, where `<n>` is the local `DisplayTitle` of that sanitized stream with `"` removed (≤ 64), and `<l>` the validated language or `und`.
- A line that fails the rule is dropped (that subtitle is unavailable) and counted.

**URI-line rules** (a URI line that does not match the rule for its position → **502**):

| Kind | Position | Pattern | Checks | Output |
|---|---|---|---|---|
| M (**U-M**) | after a STREAM-INF | `^main\.m3u8\?(Q)$` | pairs parsed; `ApiKey` and `api_key` removed; `MediaSourceId`, `PlaySessionId` and `DeviceId`, if present, must equal the grant's peer values and Home's DeviceId; stored as `grant.peerVariantQuery` (first variant only) | `main.m3u8?ApiKey=<t>` |
| V (**U-V**) | after `EXTINF` | `^hls1/main/(0\|[1-9][0-9]{0,5})\.(ts\|mp4)\?(Q)$` | exactly one `runtimeTicks` and one `actualSegmentLengthTicks` in Q, each `^[0-9]{1,19}$` parsed to `long`, `runtimeTicks` ≤ runtime + 1 h, `actualSegmentLengthTicks` ≤ 10 min | `hls1/main/<n>.<ext>?ApiKey=<t>&runtimeTicks=<r>&actualSegmentLengthTicks=<l>` |
| S (**U-S**) | after `EXTINF` | `^stream\.vtt\?(Q)$` | exactly one `StartPositionTicks` and one `EndPositionTicks`, each `^[0-9]{1,19}$`, start ≤ end ≤ runtime + 1 h | `stream.vtt?ApiKey=<t>&StartPositionTicks=<s>&EndPositionTicks=<e>` |
| any | anywhere else | none | | **502** |

**Output rules:**
- **O1** Home writes the output with `\n` line endings in UTF-8. The only non-ASCII text possible is a subtitle `NAME` from Home's own sanitized data.
- **O2** `<t>` is the caller's own token from the `Token` claim. It must match `^[0-9a-f]{32}$` (Jellyfin access tokens), else 403. It is never logged or persisted, and it is echoed only to the same caller, as core does.
- **O3** Emitted URIs are relative and resolve under `/Shoal/Hls/{grantId}/` with any `BaseUrl`: `main.m3u8`, `hls1/main/..` (relative to main), `subs/<i>/subtitles.m3u8`, and `stream.vtt` (relative to the subtitle playlist).
- **O4** Canary (6.2 step 7) over the whole output; a hit → 502.
- **O5** `ResponseWriter` headers (8.6): `Content-Type: application/vnd.apple.mpegurl`, `Content-Disposition: attachment`, CSP sandbox, `nosniff`, `no-store`.

**Segments and subtitle segments** are proxied with the deadlines and caps of 6.10. Home emits its own Content-Type (`ts` → `video/mp2t`, `mp4` → `video/mp4`, `stream.vtt` → `text/vtt; charset=utf-8`), never the Peer's. The `subs/{index}` routes require the index to be a text subtitle stream in the grant (the source-codec allowlist of 6.6a step 5). **v2.3 (W4-1 extension, W4-9):** the first subtitle segment of an embedded track makes the Peer extract the whole file, exactly as a subtitle file does, so subtitle **segment** calls go through the same item lanes and per-peer gate as subtitle files (6.6a step 7c-d; they are not subject to the one-open-lane-per-user rule, being bounded by the grant's rate and slot), their timeouts and 5xx never count toward the breaker (6.10), and their bodies go through the VTT rebuild (6.6a step 9a).

**Acceptance:** every hostile fixture F-PL-01..F-PL-36 (Appendix C) yields 502 or the line dropped, and the positive and containment controls in that range (F-PL-24, -25, -31) behave as described there. A packet capture shows no connection to the named hosts. The output contains none of the hostile input substrings.

### 6.5 Seek

- **Direct play:** Range passthrough.
- **HLS:** a far segment request makes the Peer kill and restart ffmpeg at that segment, scoped by `PlaySessionId` (`DynamicHlsController.cs:1490-1505`); this is transparent through the proxy. When jellyfin-web re-requests `PlaybackInfo` with `StartTimeTicks` for a transcode seek, it gets a new grant. The old one is cleaned up by the client's `DELETE Videos/ActiveEncodings` (intercepted, scoped) or by the Peer's 60 s idle kill.

### 6.6 Subtitles

| Case | MVP behavior |
|---|---|
| Embedded, direct play | in-container; works through Range (this includes PGS rendered in-band by the player) |
| External text on the Peer (srt, ass, ssa, vtt) | `DeliveryUrl = /Shoal/Subtitles/{localItemId}/{index}/Stream.{fmt}?ApiKey=<caller>`. Served by SubtitleHandler (6.6a), which proxies the Peer's `Stream.{fmt}`. The Peer's `?ApiKey=<peer token>` DeliveryUrl (`StreamInfo.cs:1276-1281`) is discarded. |
| **Embedded text as a file (v2.2, G4)** | No `DeliveryUrl` (the Peer gives none for `Embed` or `Hls` delivery, and Shoal adds none). Clients build the URL themselves: Moonfin-Core `/Videos/{item}/{ms}/Subtitles/{i}/0/Stream.srt\|vtt` for its secondary overlay and downloads when `SupportsExternalStream=true` (`media_stream_resolver.dart:331-372`); Moonfin Smart-TV `/Subtitles/{i}/Stream.vtt\|ass\|js` (no `/0/`) for **every** text track (`playback.js:886-973`). Intercepted (6.1) to SubtitleHandler, which asks the Peer for the same route; on the first request for any text track of a source the Peer runs **one** ffmpeg over the whole file that extracts every extractable track (bitmap ones included) and caches them, serializing later requests for that source on a per-path lock (`SubtitleEncoder.cs:515-560,647-700`), so Home gates these calls (6.6a step 7). `SupportsExternalStream` is copied from the Peer, so it stays true for text tracks. |
| **JSON subtitles `js` / `json` (v2.2, G3)** | Moonfin Smart-TV requires them on webOS ("native `<track>` does not work reliably", SC§2.2). Supported: Home fetches the Peer's `Stream.js` and **rebuilds** it (6.6a). |
| **Bitmap subtitles as files, `sup` / `pgssub` (v2.2, G3)** | **Not supported: 404.** Smart-TV builds `/Subtitles/{i}/0/Stream.sup` for embedded PGS when it cannot render in-band (`playback.js:382-384`). Reasons in 6.6a. Shoal also keeps the Peer from choosing `External` delivery for bitmap formats (6.2.2), so no client is handed a bitmap `DeliveryUrl`. |
| Image subs (PGS) or transcode with a selected sub | the Peer burns in (`Encode`), via the DeviceProfile and `SubtitleStreamIndex` |
| HLS-delivered subs (during transcode) | proxied through the grant: rule MD, rule U-S, `subs/{n}/stream.vtt` segment proxy. The spike proved this path with 0 token occurrences after rewrite (K§Evidence 4), and the owner saw subtitles work (owner test 01). Kill switch in 6.2.2. |
| Client-built core subtitle URL | intercepted (6.1) to SubtitleHandler. The Peer's subtitle `Stream.{fmt}` route is anonymous too (K§Surprises 1); the Peer guard filter covers it (8.4). |

### 6.6a SubtitleHandler (v2.2; G3, G4; v2.3: W4-1, W4-3, W4-7 to W4-10)

One handler serves `GET Shoal/Subtitles/{localItemId}/{index}/Stream.{fmt}` and the intercepted core routes `Videos/{item}/{ms}/Subtitles/{index}/Stream.{fmt}` and `.../{index}/{ticks}/Stream.{fmt}` (`SubtitleController.cs:208,295`).

0. **One id (v2.3, W4-3).** The id that is authorized is the id that is served, and it is the **route** id. On the intercepted core routes, any `itemId` query value (every value of a repeated key, parsed per 6.1) that is not equal to the route id → **404** before CallerContext and before any Peer call, whichever of the two is federated. The filter applies this already (6.1); the handler checks it again so no future route can skip it.
1. **CallerContext** (8.5), all steps, for the **route** item. 401, 403 or 404.
2. **Route values only.** Core's obsolete query overrides (`mediaSourceId`, `index`, `format`) are ignored (`itemId` is step 0). `{ms}` must be the local item id (N or D format), else 404.
3. **Format.** `fmt`, case-insensitive, must be one of `vtt`, `srt`, `ass`, `ssa`, `js`, `json` (`js` and `json` are the same, as in core, `SubtitleController.cs:231-233`). Anything else, `sup` and `pgssub` included → **404**.
4. **Grant.** The newest live grant of (caller, item), any DeviceId. Clients send no `PlaySessionId` on these URLs (SC§G4), and every client calls PlaybackInfo before playing. No grant → 404. The request refreshes the grant's `lastAccessUtc`.
5. **Stream.** `index` must be a `Subtitle` stream in `grant.sanitizedMediaStreams` whose **codec is in Shoal's text allowlist** (v2.3, W4-10): `subrip`, `srt`, `ass`, `ssa`, `webvtt`, `vtt`, and `mov_text` (MP4 text tracks, which core converts through the same encoder; kept only if T-CLI-4 shows lab-server serving them, otherwise removed). Not core's `IsTextFormat`, which is a blocklist: an unknown codec such as `xsub` would count as text. Embedded and external both qualify. Bitmap, unknown codec or unknown index → 404.
6. **Mapped client values** (the "map known safe values, drop the rest" principle of 6.11): `{ticks}` from the route, a `long` in 0..runtime, default 0; query `endPositionTicks` (`long`, ticks..runtime + 1 h), `copyTimestamps` and `addVttTimeMap` (`true`/`false`; `addVttTimeMap` only for `vtt`). Invalid or **repeated** → 400 (v2.3, W4-11). Every other query key is ignored.
7. **Peer request** (6.11): `GET /Videos/{pid}/{pms}/Subtitles/{index}/{ticks}/Stream.{fmt}` with only the mapped values. `js` and `json` are both requested as `js`. Why this needs care: the first request for any text track of a source makes the Peer run one ffmpeg over the whole file (minutes on a large remux, SC§2.2) while holding a per-path lock, so later requests for that source queue on the Peer, each holding one of Home's 16 pooled connections. v2.2 shared only *identical* requests, which a caller defeats by varying `{ticks}`; fifteen such requests could starve PlaybackInfo, segments and the health probe, and their timeouts would open the peer-wide breaker for every user of Home (W4-1). v2.3 routes every subtitle-file call (and every HLS subtitle segment call, 6.4) through the following, in this order:
   a. **Keys.** `Kitem` = (peerKey, peerItemId, peerMediaSourceId). `Kreq` = `Kitem` + (index, fmt as sent to the Peer, the mapped values of step 6).
   b. **Join.** If a Peer call with the same `Kreq` is in flight, the request waits for it and is served its result (rules in g).
   c. **Item lane.** Otherwise, if a Peer call for the same `Kitem` is in flight, the request waits for it to end and then makes its own call, which is now cheap because the Peer cached every track of that source. At most **one** Peer call per lane at a time.
   d. **Per-peer gate.** A lane needs one of **2** gate slots of its peer before it may call (its own `SemaphoreSlim` per peer, separate from the connection pool, so 14 of the 16 pooled connections stay reachable only by control and media calls). At most **16** requests may wait on one peer's gate and lanes together; beyond that → **429** `Retry-After: 30` at once. Waiting counts inside the request's 150 s budget (6.10); a request still waiting when its budget runs out → **429** `Retry-After: 30`, with no Peer call.
   e. **Per user.** A user may have at most **one open lane** (a lane their request opened and that has not ended); a request that would open a second → **429** `Retry-After: 10`. Joining (b) and waiting in someone's lane (c) are not limited by this, only by the 30 per minute rate (6.9). HLS subtitle segments are exempt from this rule (6.4).
   f. **Detached abort.** The shared Peer call is not tied to the `RequestAborted` of the request that started it. Each waiter has its own cancellation, and the call counts its waiters. When the last waiter leaves: a call running for **less than 10 s** is cancelled; an older one runs to completion inside its own 150 s budget and its result is discarded. So a TV app whose own timeout is 30 s, retrying against a 3-minute extraction, joins the running call instead of restarting ffmpeg each time; the Peer then has the tracks cached.
   g. **Coalescing rules (W4-7; no cross-user leakage or poisoning).** (1) CallerContext (step 1) and the caller's own grant lookup (steps 4-5) run **before** a request may join. (2) `Kreq` includes `peerKey` and `peerMediaSourceId` (a hostile peer chooses its own item ids). (3) The shared result is an **immutable, fully validated buffer**, after steps 8-9a and the canary; each joiner is served a read-only view of it, never a shared stream read at different offsets. (4) **No cache after completion**: the result lives only while its call has waiters (a later cache, if ever added, is keyed with the Peer's ETag and a short TTL, after a Warden review). (5) A failed call is **not retained**: the next request tries again, through the gate. (6) Nothing caller-specific is in the key or the body (Home attaches no per-user data to a subtitle), and each joiner is charged its own rate-limit token (6.9).
   h. **Breaker.** Subtitle-file and HLS-subtitle-segment outcomes **never count toward the circuit breaker** (`PeerCallProfile.CountsTowardBreaker = false`): neither header or total timeouts nor any 5xx (a title with one track the Peer cannot extract answers 500 on every retry). A hung extraction is not evidence that the Peer is down. Connect failures still count, and 401/403 still set `AuthFailed` (6.10).
   Deadlines in 6.10 (`PeerCallProfile.SubtitleFile`, new in v2.3; the old 30 s `ExternalSubtitle` profile is not reused).
8. **Validate and emit** (8.6): upstream type in the allowlist for `fmt`; size ≤ 10 MB; then the rebuild for `js`/`json` (step 9) and `vtt`/`srt` (step 9a), and the header check of 8.6 for `ass`/`ssa`. Home emits its own type by `fmt`.
9. **JSON rebuild** (`js`, `json`; v2.3 parse per W4-8). The Peer's body is read through `BoundedReadStream` into one buffer of at most 10 MB and walked with `Utf8JsonReader` (`CommentHandling = Disallow`, `AllowTrailingCommas = false`, `MaxDepth = 8`; numbers must be JSON integers that fit `long`). The accepted shape is exactly `{ TrackEvents: [ { Id: string, Text: string, StartPositionTicks: long, EndPositionTicks: long } ] }` (`JsonWriter.cs` emits exactly these four members). Unknown members at either level are **skipped and never stored** (a newer core adding a member must not break webOS subtitles); a duplicate known member, a missing one, a wrong token type or any other shape → 502. The walk keeps at most 20,000 events and **stops with 502 at event 20,001**, so no tree of 170,000 events is ever built. Then Home **serializes a new object** from the typed values with the default `System.Text.Json` encoder (`UnsafeRelaxedJsonEscaping` is banned, 8.8):
   - ticks 0..runtime + 1 h and start ≤ end, else the event is dropped;
   - `Id` re-numbered by Home (1, 2, 3, ...);
   - `Text`, **the cue-text rule** (also used by step 9a): C0 controls other than `\n` removed, plus DEL, U+0085, U+2028, U+2029 and the bidi overrides; then every `<...>` sequence removed **except** exactly `<i>`, `</i>`, `<b>`, `</b>`, `<u>`, `</u>`; then any remaining `<` or `>` removed; at most 2,000 characters on a rune boundary. The invariant, as a fuzz property over every input: the output `Text` matches `^(?:[^<>]|</?[ibu]>)*$`. Moonfin Smart-TV runs its own `sanitizeSubtitleHtml` before `innerHTML` (`TizenPlayer.js:3151`, `WebOSPlayer.js:3261`); Shoal does not rely on it, because the same JSON is a public core format other clients may render.
   The canary (6.2 step 7) runs over the output.
9a. **VTT and SRT rebuild (v2.3, W4-9).** Step 9's argument applies equally to `vtt` and `srt` from the same handler (Moonfin's TV apps run one sanitizer whatever the format; other clients render these formats differently), and embedded tracks now reach every client through them. So Home rebuilds them line by line; nothing is copied except cue text after the cue-text rule. Input: strict UTF-8 (BOM skipped), lines split on CRLF, LF or CR, NUL → 502, ≤ 20,000 cues (502 at cue 20,001).
   - **VTT** (every VTT Shoal emits, HLS subtitle segments included): line 1 must start with `WEBVTT` followed by the end of the line, a space or a tab; output `WEBVTT`. Header lines before the first blank line are dropped, except `X-TIMESTAMP-MAP=` with exactly `MPEGTS:[0-9]{1,12}` and `LOCAL:<timestamp>` (either order), re-serialized. Blocks are separated by blank lines; a block whose first line starts with `NOTE`, `STYLE` or `REGION` is **dropped whole**. Why `STYLE`: a `::cue` rule may carry `background-image: url(...)`, so a browser rendering the native track could fetch from a host the Peer chose and hand the friend the **viewer's** IP, which Home otherwise never reveals (derived from the CSS `::cue` property list, not run in a browser; T-SEC-18 checks it). In a cue block, the identifier line is dropped; the timing line must match `^(T) +--> +(T)([ \t]+.*)?\z` with `T` = `(?:[0-9]{2,3}:)?[0-5][0-9]:[0-5][0-9]\.[0-9]{3}` and is re-serialized from the parsed times; cue settings are kept only per token from a closed list (`align:(start|center|end|left|right)`, `line:` and `position:` and `size:` with numeric percentages or line numbers and their alignment suffix, `vertical:(rl|lr)`), others dropped; times outside 0..runtime + 1 h or start > end drop the cue; a block without a valid timing line is dropped. Cue text lines go through the cue-text rule.
   - **SRT**: blocks separated by blank lines; the counter line is replaced by Home's own numbering; the timing line must match `^(T) --> (T)` with `T` = `[0-9]{2,3}:[0-5][0-9]:[0-5][0-9][,.][0-9]{3}` (anything after it, such as `X1:` coordinates, is dropped) and is re-serialized with commas; the same time rules; text lines through the cue-text rule (so `<font>` loses its tag and keeps its text; `{\an8}`-style braces are plain text and stay). Line endings as core's own SRT writer.
   - **ASS/SSA** are not rebuilt: override tags are styling, and the residual "a friend's server can send malformed media to your players" (W2§N-14) covers them; the 8.6 header check stays.
   The canary runs over the output.
10. **Why no bitmap files.** PGS is a binary format that the TV app decodes with a JavaScript parser (libpgs) fed straight from the friend's server, so a malformed file is a parser attack on the viewer's TV, and Shoal has no safe way to validate it short of a PGS parser of its own. That is the reason, and the only one: **v2.2's "extraction costs the Peer a full read" was wrong as an argument** (W4-1 point 4), because any text-track request already triggers the full read and the PGS extraction on the Peer, which is why step 7 gates them. Stock 12.1 also appears unable to serve `Stream.sup` itself (Changes in v2.2, "Verified"). In-band rendering on direct play and burn-in on transcode cover PGS (6.2.2). Revisit only with a Warden review and a bounded validator.

### 6.7 Trickplay

Phase 6 design: v2.6.5 (K30).

### 6.8 Session reporting and watch state

- **Home records progress and played state** for the local user on the stub item, using core's normal mechanism. This works because `MediaSourceId` and `ItemId` are local, and core's lookup does not probe (`MediaSourceManager.cs:378` passes `allowMediaProbe:false`).
- `PeerSessionReporter` on local `PlaybackStart` for a grant sends Peer `POST /Sessions/Playing` (body in 6.11).
- On `PlaybackProgress`, at most one call per 20 s: Peer `POST /Sessions/Playing/Ping?playSessionId=PS`.
- A keepalive timer pings every 30 s while the local session still shows the item. This keeps a **paused** HLS job alive past the Peer's 60 s idle kill (`TranscodeManager.cs:144-186`).
- On `PlaybackStopped`, send Peer `POST /Sessions/Playing/Stopped`. That kills only that job (`PlaystateController.cs:245-254`). **v2.4 (P3§5, K19):** then, for a `Transcode` grant whose `peerJobStarted` is still set (no scoped delete sent yet, 6.1), also send the scoped `DELETE /Videos/ActiveEncodings?deviceId=<pairing DeviceId>&playSessionId={PS}` (6.11) and clear the flag: it is the mechanism Pilot measured (the job ended within 3.5 s, the other jobs untouched), it is idempotent, and it is sent at most once per job. Then release the stream and conversion slots and drop the grant. Why it matters: a conversion nobody stops keeps the friend's CPU at 100 % for about 60 s (the Peer's idle kill, `TranscodeManager.cs:144-186`), and zapping users stack those minutes. A client that vanishes without any stop event is left to that idle kill (accepted); Home releases its slots after 2 min without requests (6.9). T-CONC-2.
- All reporter calls are fire-and-forget on a bounded queue (256 items; drop the oldest), with the deadlines of 6.10. They never block core's event thread.
- **Grant ownership (W2§N-19).** `PeerSessionReporter` forwards a start, progress or stop to the Peer only when the grant found by `PlaySessionId` also satisfies `grant.localUserId == session.UserId`, not on a `PlaySessionId` match alone, so one local user cannot make Home stop another user's Peer transcode.
- **Hard rule:** every Peer call that can kill transcodes carries a non-empty peer `PlaySessionId`. `KillTranscodingJobs` with an empty one kills **every** job for the DeviceId (`TranscodeManager.cs:200-203`), and all Home users share one DeviceId on the Peer. A unit test asserts that the builder throws on an empty value.
- **Privacy (owner D8):** the Peer sees the share user playing a title, with times, positions and play method, and the client profile name "Shoal". It never sees Home usernames, client IPs or device names.

### 6.9 Concurrent-stream and rate limits (W§A-5, W§A-20.7)

**Slots, not grants, are the scarce resource.** PlaybackInfo creates a cheap grant (≤ 8 per user, one slot-free grant per user, item and DeviceId, 3.5). A **slot** is taken at the first static byte request or the first HLS segment request, and released on `PlaybackStopped`, on `RequestAborted` plus 2 min grace (static), after 2 min with no requests, or (HLS only, v2.3) by the scoped `ActiveEncodings` delete once it has been sent (6.1). So PlaybackInfo alone cannot starve anyone (T-SEC-17), and `ActiveEncodings` cannot free the slot of a stream that keeps running (T-SEC-25).

**Two kinds of slot (v2.4, Pilot-03).** Every stream holds a **stream slot**. A stream through Shoal's HLS proxy also holds a **conversion slot**, taken with the stream slot at its first segment and released with it. Home cannot tell a Peer remux from a full encode, so every HLS stream counts as a conversion. Why: on Pilot's 4-core Peer one libx264 job (720p source, 1.2 Mbps) took all four cores, four together ran at about 2.2x real time each, and a direct stream cost the Peer no CPU at all (P3§2-3). The friend's CPU, not the token, the session limit or RAM, bounds simultaneous conversions, and Home cannot see the friend's cores or hardware acceleration on a stock Peer. Both kinds are also pre-checked at PlaybackInfo (6.2 steps 1 and 6), so the usual refusal is the clean `RateLimitExceeded` before anything starts; the 429 at the first byte or segment remains the authoritative check for races.

| Limit | Default | Enforced at | On breach |
|---|---|---|---|
| Streams (slots) per local user | **4** (`MaxStreamsPerUser`, clamped to **1..4**, v2.3 W4-6; default 4 per owner D13, v2.4), across all peers | first byte or segment; pre-check at PlaybackInfo (v2.4) | 429 / `RateLimitExceeded` |
| Streams (slots) per peer | 4 (`maxConcurrentStreams`); one user may hold at most half, rounded up | same | 429 / `RateLimitExceeded` |
| **Conversions per peer (v2.4)** | **4** (D15; `maxConcurrentTranscodes`, 1..`maxConcurrentStreams`; Server detail "Conversions at once"); every HLS stream holds one besides its stream slot; no per-user share (the stream half-rule already caps one account at 2 per peer) | first HLS segment; pre-check at PlaybackInfo when the source can only be converted | 429 / `RateLimitExceeded` |
| Grants per user / total | 8 (v2.2; order per 3.5: replace the same key's slot-free grant, else evict the user's oldest slot-free grant, else refuse when none is slot-free; never a grant holding a slot) / 200 (refuse new; checked last, v2.3) | PlaybackInfo | `RateLimitExceeded` (user with no slot-free grant, or global cap) |
| PlaybackInfo per user | token bucket, 30 per min, burst 15 (v2.2; was 20 per min) | handler | `RateLimitExceeded` |
| HLS requests per grant | 10 per s, burst 40 | controller | 429 |
| Static requests per user | 20 per s, burst 100; 6 concurrent connections | handler | 429 |
| Subtitle requests per user | 30 per min (a coalesced request counts once per caller) | handler | 429 |
| Subtitle Peer calls per peer (v2.3, W4-1) | 2 at once (gate), 16 waiting; one lane per source (6.6a step 7) | SubtitleHandler, HLS subtitle segments | 429 + `Retry-After: 30` |
| Open subtitle lanes per user (v2.3, W4-1) | 1 | SubtitleHandler | 429 + `Retry-After: 10` |
| `ActiveEncodings` deletes per user (v2.3, W4-4) | 10 per min (matched grants only) | filter | 429 |
| Admin: Test now / Sync now / Pairing start | 1 per min / 1 per 5 min / 5 per 10 min, per peer | AdminController | 429 + `Retry-After` |

**Sizing check (v2.2, G8).** A normal Moonfin Smart-TV start is up to 5 PlaybackInfo calls in a few seconds for one (user, item, DeviceId): they replace each other, so they hold one grant and use 5 of the burst of 15. A second device of the same user starting at the same moment, each with a Moonfin-Core retry, adds one grant and at most 7 calls: still within both limits, and the first device's playing grant is never evicted. The cost of the higher numbers is bounded: at most 8 grant records per user (a few KB), the global 200 unchanged, and Peer PlaybackInfo calls per user at most 15 at once and 30 per minute sustained (v2.1: 20 per minute).

Peer-side protection: the share user's `RemoteClientBitrateLimit`, revocation, and on Shoal Peers the per-share transcode cap (8.4). A stock Peer has no count limit against a hostile Home that holds the token: accepted, documented in 4.5 and the README. Home's conversion limit is a good-neighbour default for an honest Home, not a Peer-side control. (A low `RemoteClientBitrateLimit` *raises* the Peer's CPU load: every title above it becomes a conversion, 4.1 step 3.)

### 6.10 Deadlines and bounded bodies (W§B-2, W§B-9, W§A-20.6)

Every peer request runs under a `CallBudget`: a `CancellationTokenSource` linked to the caller's cancellation (`RequestAborted`, sync budget), with `CancelAfter(total)`. Every response body is read **only** through `BoundedReadStream(inner, maxBytes, idleTimeout)`. It counts bytes and throws past `maxBytes` whatever `Content-Length` says (chunked bodies included). It throws when a single read waits longer than `idleTimeout`, and treats a body shorter than `Content-Length` as an error. JSON is parsed with `JsonSerializer.DeserializeAsync` from that stream with `MaxDepth = 32`. `ReadAsStringAsync` and friends are banned (8.8).

| Call | Connect | Headers | Idle read | Total | Max bytes |
|---|---|---|---|---|---|
| `System/Info[/Public]`, `System/Ping`, `Users/Me`, session anchor, Quick Connect (the 1 s **poll** and the **redeem** use a 3 s total, 4.2a), `Shoal/Pair` | 3 s | 5 s | 5 s | 8 s | 64 KB |
| `Sessions` (foreign-device watch) | 3 s | 5 s | 5 s | 8 s | 256 KB |
| Items listing page or details batch | 5 s | 20 s | 10 s | 30 s | 8 MB |
| Images | 5 s | 20 s | 10 s | 30 s | 2 MB poster / 4 MB backdrop |
| PlaybackInfo | 3 s | 10 s | 5 s | 12 s | 2 MB |
| Static stream | 5 s | 15 s | 30 s | none: bounded by the declared length and `RequestAborted` | declared length |
| HLS master / main | 5 s | 30 s | 10 s | 40 s | 256 KB / 8 MB |
| HLS segment | 5 s | 60 s (the Peer blocks until the segment exists) | 30 s | 180 s | 256 MB |
| Subtitle playlist / HLS subtitle segment | 5 s | 20 s | 10 s | 30 s | 2 MB / 2 MB (segments: gate, lanes and no breaker count, v2.3, 6.4) |
| Subtitle file (`Stream.{fmt}`, external or embedded; v2.2; profile `SubtitleFile`, v2.3) | 5 s | 120 s (the Peer extracts every track of the source with ffmpeg before the first byte, then caches them) | 10 s | 150 s, **including** any wait for the gate or a lane (6.6a step 7) | 10 MB |
| Session reports, `ActiveEncodings` delete | 3 s | 5 s | 5 s | 8 s | 64 KB |
| BitrateTest (phase 4) | 5 s | 15 s | 5 s | 30 s | 64 MB |

Static streams have no total deadline because a movie can stream for hours. A peer that trickles just under the idle timeout holds only that one stream's slot and connection, which the client's abort releases (accepted).

**Handler settings:** `MaxResponseHeadersLength=32 KB`, `AllowAutoRedirect=false` (3xx is an error), `UseCookies=false`, `AutomaticDecompression=None` (send `Accept-Encoding: identity`), `PooledConnectionLifetime=5 min`, `MaxConnectionsPerServer=16`, `ConnectTimeout` per the table, TLS 1.2+ with default validation plus the optional SPKI pin (`SslOptions.RemoteCertificateValidationCallback`).

**Subtitle gate (v2.3, W4-1):** a per-peer `SemaphoreSlim(2)` in front of every subtitle-file and HLS-subtitle-segment call (6.6a step 7), so those calls can hold at most 2 of the 16 pooled connections and the other 14 always stay available to PlaybackInfo, media and health calls.

**Circuit breaker:**
- 3 consecutive connect, timeout or 5xx failures open it. A 401/403 sets `AuthFailed` instead.
- **Except (v2.3, W4-1):** a call whose `PeerCallProfile.CountsTowardBreaker` is false (`SubtitleFile`, `SubtitleSegment`) leaves the consecutive count untouched on a timeout, a 5xx or a success; only its **connect** failures count. One ordinary user can otherwise mark a friend's server `Offline` for everyone on Home with a few slow extractions.
- While open, a half-open `/System/Ping` probe runs at 30 s, then 60, 120, capped at 60 s (was 300 s; Pilot 06 saw Online 5.5 min after a peer restart). While a peer is Offline the health monitor looks at it every 15 s; the breaker still decides when a probe may go out.
- While open, nothing else is sent to the peer.

### 6.11 What Home sends to the Peer, exactly (W§B-4, W§A-15, T7, T26)

**Headers on every peer request**, built by `PeerClient`; nothing is copied from the client request:

| Header | Value |
|---|---|
| `Authorization` | `MediaBrowser Client="Shoal", Device="Shoal: <Home ServerName> (<check>)", DeviceId="<pairing DeviceId>", Version="<plugin version>"`, plus `, Token="<token>"` on authenticated calls. **Every value is percent-encoded UTF-8 by one function:** each byte outside `A-Z a-z 0-9 - . _ ~` becomes `%XX`, so `%` is `%25`, `+` is `%2B` and a space is `%20` (W2§N-10 (6)). Core decodes with `WebUtility.UrlDecode` (`AuthorizationContext.cs:276-313`), which turns a bare `+` into a space and mangles an unescaped `%`. The server name is sanitized first: quotes, commas, backslashes and controls removed, ≤ 64. |
| `User-Agent` | `Shoal/<plugin version>` |
| `Accept` | `application/json` for API calls, `*/*` for media |
| `Accept-Encoding` | `identity` |
| `Content-Type` | `application/json` on POST with a body |
| `Range` | static stream only: the single validated range of 6.3 |

Never sent: `Cookie`, `Referer`, `X-Forwarded-*`, `Forwarded`, `X-Real-IP`, `X-Emby-*`, the client's `User-Agent` or `Accept-Language`, any `ApiKey`.

**Paths, queries and bodies:**

| Call | Path and query (exact) | Body |
|---|---|---|
| Identity, health | `GET /System/Info/Public`, `GET /System/Info`, `GET /System/Ping`, `GET /Users/Me`, `GET /Sessions` | none |
| Ping | `GET /Shoal/Ping` (anonymous, through `PeerClient` with `PeerCallProfile.Info`, so the address policy and size caps apply); only `200 {"shoal":true}` counts | none |
| Invite | `POST /Shoal/Pair` | 4.1b |
| Re-pair of a legacy QC peer, remove peer | `POST /Sessions/Logout` with the old token, best effort (3 s, never fatal) | none |

Removed in v2.8 (D17): the session anchor (`POST /Sessions/Capabilities`) and every Quick Connect call (`GET /QuickConnect/Enabled`, `POST /QuickConnect/Initiate`, `GET /QuickConnect/Connect?secret=`, `POST /Users/AuthenticateWithQuickConnect`). Home sends nothing unauthenticated to a friend's server except the identity and ping reads and `POST /Shoal/Pair`.
| Sync | the queries of 5.1 with `ParentId`, `Ids`, `StartIndex` and `tag` from validated catalog values | none |
| PlaybackInfo | `POST /Items/{peerItemId}/PlaybackInfo`, **no query** | 6.2.1 |
| Static | `GET\|HEAD /Videos/{peerItemId}/stream?static=true&mediaSourceId={peerMsId}` | none |
| HLS master | `GET /Videos/{peerItemId}/master.m3u8?<peerMasterQuery>`: the Peer's own `TranscodingUrl` query, parsed and re-serialized by Home, `ApiKey`/`api_key` removed, `EnableAdaptiveBitrateStreaming=false` forced. Every value must match Q (6.4), else 502. | none |
| HLS main | `GET /Videos/{peerItemId}/main.m3u8?<peerVariantQuery>` (from U-M) | none |
| HLS segment | `GET /Videos/{peerItemId}/hls1/main/{n}.{ext}?<peerVariantQuery>&runtimeTicks={r}&actualSegmentLengthTicks={l}`, where n, ext, r and l are the **only** client-supplied values, each validated as in U-V | none |
| Subtitle playlist | `GET /Videos/{peerItemId}/{peerMsId}/Subtitles/{index}/subtitles.m3u8?SegmentLength=30` (index from the grant) | none |
| Subtitle segment | `GET /Videos/{peerItemId}/{peerMsId}/Subtitles/{index}/stream.vtt?CopyTimestamps=true&AddVttTimeMap=true&StartPositionTicks={s}&EndPositionTicks={e}` (s and e validated as in U-S) | none |
| Subtitle file (external or embedded text; v2.2) | `GET /Videos/{peerItemId}/{peerMsId}/Subtitles/{index}/{ticks}/Stream.{fmt}[?endPositionTicks={e}][&copyTimestamps=true][&addVttTimeMap=true]`, where `index` is a text stream of the grant, `fmt` ∈ {vtt, srt, ass, ssa, js} (`json` is sent as `js`), and `ticks` (default 0), `e` and the two flags are the only client-supplied values, each validated as in 6.6a step 6 | none |
| Session reports | `POST /Sessions/Playing`, `POST /Sessions/Playing/Ping?playSessionId={PS}`, `POST /Sessions/Playing/Stopped` | `{"ItemId":"<pid>","MediaSourceId":"<pms>","PlaySessionId":"<PS>","PlayMethod":"DirectPlay\|Transcode","CanSeek":true,"PositionTicks":<n>,"IsPaused":<bool>}`, only these fields |
| Scoped kill | `DELETE /Videos/ActiveEncodings?deviceId=<pairing DeviceId>&playSessionId={PS}` (PS non-empty); from the client's `ActiveEncodings` delete only for a `Transcode` grant with `peerJobStarted`, at most once per started job (v2.3, W4-4, 6.1); v2.4: also after `Sessions/Playing/Stopped` when that grant's `peerJobStarted` is still set (6.8, K19) | none |

T-SEC-20 has fakepeer record every request byte and compares it with this table.

### 6.12 Client compatibility (v2.2; owner D11; source: SC§3)

**Supported in MVP** (by source reading; T-CLI-1..13 prove it in the lab, T-CLI-13 on real devices):

| Client | Direct play | HLS transcode | Subtitles | Notes |
|---|---|---|---|---|
| Moonfin-Core: iOS, iPadOS, tvOS, Android, Android TV, Fire TV, macOS, Windows, Linux, web (also served by Moonbase at `/Moonfin/Web/`) | `Videos/{id}/stream?MediaSourceId&Static=true`, header + `ApiKey` | `TranscodingUrl` with Moonfin's added indices (6.4 "Client query") | `DeliveryUrl`; embedded text as files (6.6a) | Offline download and the home-row preview (off by default) are unavailable for Shoal titles (403/404 by design) |
| Moonfin Smart-TV: Tizen, webOS, Vega, Xbox | `stream.{Container}?Static=true&ApiKey` (needs `Container`, 6.2.3) | `TranscodingUrl` (+`upmix`, ignored) | `vtt`, `ass`, `js` files; PGS in-band on direct play or burned in; **no `Stream.sup`** | ASS fonts after K31 (v2.6.6) |
| Moonfin Roku | `stream?Static=true&Container&...`, header only | `TranscodingUrl` | as core | |
| jellyfin-web, Jellyfin Media Player | `stream.{container}?Static=true&ApiKey` | `TranscodingUrl` | all text formats | the owner's browser test passed (owner test 01) |
| Jellyfin Android (official), built-in player | `stream?static=true&...`, header on every request | `TranscodingUrl` (HLS) | `DeliveryUrl` | needs `MediaSources[0].Id` to parse as the item GUID (it does) |

**Not supported in MVP (G1; owner decision D12, 2026-10-15).** The official **Jellyfin Android TV** app, **Swiftfin**, **Findroid**, and the official Android app when it hands a video to an **external player** (VLC, MX Player, mpv) request `/Videos/{id}/stream?static=true` with **no credentials** (SC§2.5-2.8). They work against plain Jellyfin only because core's stream route is anonymous. Shoal answers 401 and **never** serves a federated title anonymously. What their users see: direct play fails with a playback error. Titles the Peer must convert may still play in the Android TV app and Swiftfin, because they follow `TranscodingUrl`, which carries the caller's token (SC§2.6-2.7); that is incidental, not supported.

**Text for the README and the admin UI help** (facts for Lumen's copy; Lumen decides placement and wording, ui-spec 8.3.12). **v2.3 (W4-15), three rules for it:** (1) an app is listed as working only after **T-CLI-13** has passed on it; every other supported client reads "should work (not tested yet)" until then, and the ship gate of 8.3.12 adds "and T-CLI-13 for every row listed as working"; (2) no "yet" anywhere: Option A may never ship, so the text must not read as a promise; (3) the README (not the in-app text) carries the proxy warning below.
> Titles from {Server} play in most Jellyfin apps, but not all of them. They work in the Moonfin apps (phones, tablets, Apple TV, Android TV, Fire TV, Samsung and LG TVs, Xbox, Roku, desktop and web), in Jellyfin's web app and Jellyfin Media Player, and in the official Jellyfin Android app's built-in player. They don't work in the official Jellyfin Android TV app, Swiftfin or Findroid, or when the Jellyfin Android app sends a video to another player such as VLC. Those apps ask for videos without signing in, and Shoal never sends a friend's video to a request that isn't signed in. Downloading a friend's title for offline viewing isn't available either.

(The list in that text is the target; per rule 1, each app moves into it as T-CLI-13 passes on it.)

**README only (W4-15):**
> Don't work around the apps that don't work by putting a proxy in front of Jellyfin that adds a token or API key to requests that don't have one. That would turn Shoal's protection off for everyone who can reach the proxy.

**Option A, recorded as a v2.x candidate (not designed, not built).** Allow a credential-less static request only when it matches a grant created in the last few hours by an authenticated PlaybackInfo for the **same item** from the **same client IP** (/32 IPv4, /64 IPv6), rate-limited per grant. It would fix every client above, at the cost of the "the GUID plus your IP is the secret" exposure that core has everywhere and Shoal currently does not. **A Warden review is required before anyone designs it further**; known issues to answer: shared NAT and CGNAT addresses, IP changes mid-stream on mobile networks, proxies that hide the client IP (the per-IP problem of 4.1b), and the subtitle routes these apps also call without credentials. Scout's Option B (force `TranscodingUrl` for named clients) is not carried forward: it fixes two apps at most and relies on the client's self-reported name.

---

## 7. Speed and diagnostics (phase 4; unchanged from v1 except names)

### 7.1 Measurements

| Metric | Method |
|---|---|
| Connect time | first `/System/Ping` on a fresh connection (TCP plus TLS, or plus SOCKS handshake) |
| RTT | 10 more pings on the warm connection, 200 ms apart, 2 s timeout each. Report median, p90, jitter (mean absolute difference of consecutive samples) and loss % |
| Throughput | `BitrateTest` warm-up 1 MB (discarded), then 8 MB, then 32 MB if the 8 MB run took < 1.5 s. Rate = bytes / (last byte − first byte); report the largest run. Headers `Cache-Control: no-cache, no-store` as in web (S§6). |
| Parallel (Test now, deep) | 2 × 16 MB at once. If the aggregate is ≥ 1.6× single, the limit is per connection (latency, relay or shaping), not the link. |
| Peer transcode speed (deep, optional, admin only, 1 per 10 min) | Start a Peer HLS transcode of the smallest title at 1080p and 8 Mbps with a profile that forces transcode. Time segments 3..8: speed = segment duration / segment TTFB. Then a scoped `DELETE ActiveEncodings`. **v2.4 (P3§2-3):** skipped while any stream from Home is active on that peer (it would measure a shared CPU and slow the viewers). The result also gives a **suggested "Conversions at once"** = clamp(floor(speed / 1.5), 1, 4), shown as "about {k} conversions at once should play smoothly at 1080p; {Server}'s own viewers use the same CPU", never applied automatically. Basis: concurrent jobs split the CPU about evenly (one job 7.7x real time alone; four at once about 2.2x to 3.4x each), and 1.5x keeps a margin for seeks and heavier sources. |
| Passive (every HLS play) | per-segment TTFB, transfer Mbps, stalls (gap between segment requests > 1.5 × segment length). One `playback` sample per grant at stop. **v2.4:** the sample also records `conversionsActive`, the most conversion slots Home held on that peer while it played. |

**Schedule:** light test (pings plus 8 MB) every 6 h, on offline-to-online transitions, and after pairing. Skipped while that peer has an active stream. Budget about 10 MB per run, roughly 40 MB per day per peer.

### 7.2 Verdict thresholds

Let **T** = median Mbps of the last 3 non-error samples (manual samples count), and **H** = 1.5 headroom. H covers VBR peaks above the average bitrate and TCP throughput variance over a 1-2 s burst test. It is about jellyfin-web's ×0.7 (1/0.7 ≈ 1.43), rounded up because the test is a short burst and Tailscale paths fluctuate.

| Quality class (shown) | Reference bitrate R | Smooth if T ≥ | Why this R |
|---|---|---|---|
| 720p transcode | 4 Mbps | 6 | Jellyfin's 720p quality preset |
| 1080p transcode | 8 Mbps | 12 | Jellyfin's "1080p 8 Mbps" preset |
| 1080p file (typical web-dl or encode) | 10 Mbps | 15 | common 1080p encodes run 5-12 Mbps |
| 4K file / 4K transcode | 20 Mbps | 30 | 4K HEVC web-dl runs 15-25 Mbps |
| 1080p remux | 35 Mbps | 53 | Blu-ray video ≤ 40 Mbps plus lossless audio |
| 4K remux | 80 Mbps | 120 | UHD Blu-ray averages 50-100 Mbps with peaks above 100 |

Per-title verdict, using the actual peer `Bitrate` b: T ≥ 1.5 b "Direct play smooth"; b ≤ T < 1.5 b "Direct play may buffer on peaks"; T < b "Peer will transcode to about T/1.5 Mbps", or "Will buffer" if auto-fit is off. Summary row example: `1080p smooth · 4K needs transcode · remux will buffer`.

| Latency verdict | RTT median |
|---|---|
| Good | ≤ 50 ms |
| OK, seeks about 1 s | 50-150 ms |
| Slow start and seek | 150-300 ms |
| Poor | > 300 ms |

A peer is **unstable** if jitter > 30 ms or loss > 2%.

### 7.3 DERP and bottleneck detection

1. **Definitive (optional, Linux, same host):** if `TailscaleSocket` resolves to a readable Unix socket (default `/var/run/tailscale/tailscaled.sock`), `GET http://local-tailscaled.sock/localapi/v0/status` (≤ 1 MB, bounded reader). Find the peer by `DNSName` or `TailscaleIPs` and read `CurAddr` (empty means relayed) and `Relay` (DERP region). Kiro verifies field names and any required `Sec-Tailscale` header against the running tailscaled version.
2. **Heuristic** (labelled "possibly relayed (DERP)", never stated as fact), for Tailnet peers only, raised when either holds:
   - RTT median > 2× the best median ever seen for this peer and more than 20 ms above it, with T < 50% of the best T; or
   - RTT drops by more than 40% within one 10-ping burst (DERP-to-direct upgrade during the test).

   The UI then shows the confirm command `tailscale ping <host>`, where "via DERP(xyz)" means relayed.

**Bottleneck hints** (first match wins; several may show):

| Signal | Hint |
|---|---|
| LocalAPI says relayed | "Relayed through Tailscale DERP <region>: expect lower speed and higher latency. Allow UDP 41641 or enable UPnP/NAT-PMP on one side." |
| Heuristic relay | same, prefixed "Possibly" |
| Passive HLS: segment TTFB > 0.7 × segment duration while transfer Mbps ≥ 1.5 × stream bitrate | "Peer transcoding is too slow (about X× realtime). Use direct play or a lower quality; the peer needs hardware acceleration." **v2.4:** when the sample's `conversionsActive` ≥ 2: "…while {n} conversions from your server were running on {Server}. Lower Conversions at once for {Server}, or pick a quality that plays directly." |
| ≥ 2 peers, all with T < 50% of their best | "Your server's download or local network is the likely bottleneck." |
| This peer slow and other peers fine, with RTT normal | "The peer's upload is the likely bottleneck." |
| Parallel ≥ 1.6× single | "Per-connection limit (latency or relay); single streams get the lower number." |
| Proxy mode | "Userspace Tailscale proxy adds CPU overhead; if T is low, check that container's CPU." |

---

## 8. Security model

### 8.1 Trust boundaries

- **Home admin:** trusted. They enter peer URLs, so SSRF through that field is admin-only (W§A-8).
- **Home users:** untrusted, except for the peer libraries whose access mode admits them.
- **Peer server and its admin:** semi-trusted. A friend, but every peer response is hostile input, and a hostile or compromised peer must not be able to:
  - harvest Home user tokens or learn Home users' IPs or devices
  - make Home or its clients contact attacker-chosen hosts (SSRF, playlist redirection)
  - write outside StubRoot, fill Home's disk, or bloat its database
  - run script in Home's origin (admin page or proxied content)
  - hang Home, or show its content to Home users the admin did not choose
- **The network:** a tailnet (authenticated and encrypted by WireGuard), a LAN (unencrypted, acknowledged per peer), or the public internet (HTTPS required).
- **Shoal on the Peer** trusts Home only as far as the share user's policy and the guard filter allow.
- **Residual risks we document and do not fix** (the README repeats them, 11.5; W2§N-14, N-15, §2c, §3.5):
  - *Malformed media reaches your players.* The grammar controls URIs; it cannot sanitize segment or subtitle **payloads**. A friend's server can send malformed media to your players, as any Jellyfin server can. jellyfin-web sanitizes VTT cue HTML with DOMPurify (`web:plugins/htmlVideoPlayer/plugin.js:1728`) and runs ASS in a WASM renderer; native Moonfin, Swiftfin and ExoPlayer demuxers are unknown (D11).
  - *Metadata paths the access mode does not cover.* Item images are anonymous in core by item id (`ImageController.cs:552`), so stub posters are as exposed as any local item's. User-data and playback-report APIs accept a federated item id from an unadmitted user and write only to that user's own rows (a 404-versus-200 oracle for a GUID nobody can guess). The DLNA plugin lists items for its configured user without per-request authentication; playback of federated items fails with 401. SyncPlay checks `IsVisibleStandalone` on join (core library access, not Shoal's mode), so a user in the browse window (5.6) can join a group and see queue titles.
  - *A hostile token holder on a stock Peer* can use the shared libraries, start ffmpeg jobs without a count limit (`MaxActiveSessions` does not bound transcodes: jobs are keyed by device and `PlaySessionId`, not by session), and write to the share user's own rows (user data, display preferences, playlists), unbounded in size per request up to Kestrel's body limit. Accepted for a "semi-trusted friend" (D7); the full statement is in 4.3.

### 8.2 Encryption at rest (W§A-16, W2§N-7)

| Option | Protects against | Does not protect against | Decision |
|---|---|---|---|
| Plain XML | nothing | anything | rejected |
| `secrets.enc` (AES-256-GCM) plus `master.key` 0600 in the same DataPath | config XML pasted in forums, config-only backups, the plugin config API, casual `grep` | an attacker with read access as the Jellyfin user or root; backups that contain both files | **default on Linux** |
| Same, with the key from env `JELLYFIN_SHOAL_KEY` (systemd `LoadCredential`, Docker secret) or from a file named by `JELLYFIN_SHOAL_KEY_FILE` | additionally, full `/config` volume backups and disk images. The file form also keeps the key out of `docker inspect` and `/proc/<pid>/environ`, where environment variables show up. | code in the Jellyfin process; anyone who can read the process environment (env form) or the key file | **recommended in the README**; prefer `_FILE` |
| Windows DPAPI, scope **`CurrentUser`** (the service account) with application entropy, wrapping the key | file copies to another machine, other local accounts | code running as the Jellyfin service account; local admins. Switching Jellyfin between a tray app and a service, or between service accounts, makes the blob unreadable: that is `SecretsUnreadable`, correct, and the README says so. The "application entropy" is a constant in an open-source plugin: it separates our blobs from other DPAPI users and **is not a secret**, so no document calls it one. | **default on Windows** (v1 said LocalMachine, which any local account can decrypt) |
| ASP.NET Data Protection | its key ring is ephemeral unless configured, so secrets are lost on restart | n/a | rejected |
| TPM, OS keyring | strong | not available to a headless daemon in a container in practice | rejected |

**`JELLYFIN_SHOAL_KEY` format (K2 prerequisite; W2§N-7.1).** HKDF is not a password hash, so a human-chosen string such as `hunter2` would be a brute-forceable key. The value must be **exactly 32 random bytes**, encoded as **base64url** (43 characters, padding optional) or **hex** (64 characters, either case). `JELLYFIN_SHOAL_KEY_FILE` names a file holding the same single-line value (a trailing newline is allowed); if both variables are set, startup is refused as ambiguous. Validation is strict: decode the whole value, require exactly 32 bytes, and reject 32 identical bytes (an obvious placeholder). **Anything else is refused** with `SecretsUnreadable(badKey)` and a message that shows how to make a good key (`openssl rand -hex 32`). When either variable is set, Shoal **never** falls back to `master.key`, never generates a key and never reads or writes `master.key`. The README gives the one-line command.

**Construction:** AES key = HKDF-SHA256(master key, salt = none, info = `"shoal-secrets-v1"`). **One sealed AES-GCM message per peer entry**, each with a fresh random 96-bit nonce per save. AAD = `"shoal-secrets|schema=<n>|peer=<peerKey>|host=<hex SHA-256 of the canonical lower-case punycode host>"`. Binding the entry to its peer and host means an attacker who can edit `state.json` but not read the key cannot point a stored token at another host or peer: the entry no longer decrypts (W2§N-7.3, N-8; a legitimate host change deletes the entry first, 4.4). A scheme or port change does not change the AAD. Atomic replace via 3.7. If decryption fails (tamper, wrong key, changed host), the Home role **refuses to use that peer** and, for a wrong key, every peer (`SecretsUnreadable` banner: restore the key or re-pair). It never regenerates the key silently. Key rotation ("re-key") is not specified; changing the key means re-pairing.

**Honest summary:** at-rest encryption prevents accidental disclosure. The real controls are least privilege (a hidden non-admin user that can only read the shared libraries, locked to Home's device and limited to one session), Home refusing admin tokens, and revocation (4.5: on a stock Peer, deleting the account).

### 8.3 Threat table

| # | Threat | Impact | Mitigation |
|---|---|---|---|
| T1 | Peer token read from disk, backups or XML | attacker streams the shared libraries as the share user | secrets outside the XML, AES-GCM, env or DPAPI key (8.2); restricted user; device lock (4.1); revoke (4.5) |
| T2 | Peer token leaks to clients through HLS playlists (subtitle and trickplay `ApiKey=`) | any Home user gets a peer credential (spike: 10 occurrences in one raw master) | closed grammar: every emitted line is rebuilt, trickplay dropped, no input query is ever echoed (6.4); canary |
| T3 | Peer token leaks through PlaybackInfo `DeliveryUrl` or `TranscodingUrl` | same | response built from scratch (6.2.3); canary |
| T4 | **Peer** token in logs | anyone given a log file | own `HttpClient` (no factory logging); header-only auth, so no peer token in any URL; `Redact`; boundary catch (6.1); a unit test greps captured logs. Note: **user** tokens appear in core's Debug request log (`ResponseTimeMiddleware.cs:53-55`) because Shoal URLs carry the caller's token in the query, as core's own do. The README says to redact Debug logs (W§A-20.4). |
| T5 | Peer token in admin API or browser | XSS on the admin page exfiltrates it | admin endpoints never return secrets; the XML holds none (W§B-7) |
| T6 | Peer GUID leak, so the anonymous peer static route works (S§5.4) | Home users or tailnet members bypass the token | sentinel `.strm`; NFO with no ids; responses rebuilt; grammar drops peer paths; peer error bodies never relayed; Info-level logs carry no peer ids; canary; Pilot HAR grep (T-SEC-5) |
| T7 | Home user token or identity sent to the Peer | malicious Peer admin replays Home tokens, learns users | 6.11: nothing from the client is forwarded; no `X-Forwarded-For` |
| T8 | Unauthenticated access to federated media on Home (core stream route is anonymous) | anyone who can reach Home streams peer content | CallerContext on every intercepted route (8.5); plugin endpoints `[Authorize]`; reflection test; T-SEC-15 |
| T9 | Horizontal access: user not admitted by the access mode or without library access, or another user's grant | content exposure | CallerContext steps 5-6; grant owner check returns 404; 128-bit memory-only grant ids |
| T10 | Non-admin reaches config or pairing | takeover of Shoal | `RequiresElevation` on `Shoal/Admin/*`; core's plugin-config API is admin-only (`PluginsController.cs:25`) |
| T11 | SSRF via an admin-entered URL | Home probes internal services | admin-only field. Scheme http/https; no userinfo, query, fragment or backslash; IDN must be punycode; IPv4 in decimal, octal or hex, IPv4-mapped IPv6, NAT64 `64:ff9b::/96`, 6to4, Teredo, `::` and `0.0.0.0` normalized before classification. Classes: Tailnet (`100.64.0.0/10`, `fd7a:115c:a1e0::/48` or `*.ts.net`), LAN (RFC1918, `fc00::/7`; plain http needs acknowledgement), Public (global unicast, HTTPS required). Always blocked: `0.0.0.0/8`, `127/8`, `::1`, `169.254/16`, `fe80::/10`, multicast, broadcast, `100.100.100.100` and `100.100.100.200`, `fd00:ec2::254`, `192.0.0.192`, `168.63.129.16`. **Direct mode:** `ConnectCallback` checks every resolved IP at connect time, which defeats DNS rebinding. **Proxy mode:** the callback sees only the proxy, and SOCKS5 resolves names at the proxy, so the anti-rebinding claim does not hold there. Proxy mode is therefore limited to Tailnet IP literals or `*.ts.net` names, resolved by Tailscale. The proxy URL must be loopback. (Corrected per W§A-8.) |
| T12 | SSRF via data a peer returns: redirects, playlist URIs, `http` subtitle paths, NFO URLs, image URLs | Home or its clients fetch attacker-chosen URLs | `AllowAutoRedirect=false`; closed grammar (6.4); absolute subtitle URLs remove the stream; NFO never contains URLs; images fetched by validated id from the pinned base only; peer `Path` fields ignored |
| T13 | Peer impersonation: MagicDNS name reused, DNS hijack, wrong server | Home sends its token to an imposter | **Corrected (W§A-7):** the pinned server `Id` comes from the anonymous `/System/Info/Public`, so anyone can replay it. It is **accident detection** (reset server, reused name or IP), checked unauthenticated within 120 s before any token is sent (4.4). Authentication of the peer comes from the network layer: WireGuard node identity for Tailnet, CA-validated TLS for Public plus an optional SPKI pin. LAN http has none, which the admin acknowledged. A mismatch sets `IdentityMismatch` or `TlsKeyChanged` and stops all calls. |
| T14 | Path traversal through peer titles | writes or deletes outside StubRoot | `PathSanitizer` (5.5); canonical prefix check; no symlinks under StubRoot; deletes limited to Shoal's own file names |
| T15 | XML injection in `.nfo` | corrupted metadata, injected `<thumb>` URLs leading to SSRF | `XmlWriter` with escaping, invalid characters stripped, no DTD, no URL elements, length caps |
| T16 | Huge or malicious JSON | Home out of memory or CPU, hangs the sync | `BoundedReadStream` byte caps and deadlines (6.10); `MaxDepth` 32; element caps; parse failure skips the item |
| T17 | Malicious images (decoder exploits, decompression bombs) | crash or RCE in core's image stack (Skia) | JPEG and PNG only by magic, `format=Jpg`, ≤ 2 or 4 MB, ≤ 4000 × 6000 and 16 MP from headers, serial fetch (5.4). Residual: JPEG/PNG decoder bugs; a friend's server is a lower trust level than TMDb, so this is a stated residual risk, not "same as any provider". |
| T18 | Stored XSS in Home's admin page through peer strings | admin session hijack | config page renders peer strings with `textContent` only (9.1); T-SEC-11, T-PAIR-7 |
| T19 | Peer DoS on Home: slowloris, no response, endless body, hanging during scan | request threads or scans stall | deadlines and bounded bodies (6.10); circuit breaker; scans never call the network; StubRoot on local disk; one global sync |
| T20 | Home (or a compromised Home) floods the Peer with transcodes | Peer CPU or bandwidth exhausted | Home-side limits (6.9); share user `RemoteClientBitrateLimit`; revoke (4.5). **Shoal Peer:** best-effort per-share transcode limit (8.4; audio HLS, subtitle conversion and other ffmpeg starts are not counted). **Stock Peer:** no count limit exists in core, and `MaxActiveSessions` does not bound transcodes (jobs are keyed by device and `PlaySessionId`): accepted and documented. |
| T21 | A local user hogs the proxy | starvation for others | slots at first byte or segment; per-user share of peer slots; limiters (6.9) |
| T22 | Mass deletion: Peer NAS unmounted or library emptied | lost catalog and watch history | mass-deletion guard and empty-library hold (5.2); provider-id user-data keys |
| T23 | Cross-kill of transcodes through an empty `PlaySessionId` | one stop kills every friend's stream | builder rejects empty values (6.8); intercepted `DELETE ActiveEncodings` forwards only for owned grants |
| T24 | Pairing social engineering: Peer admin approves for an admin user | Home holds an admin token | Home refuses admin accounts and logs them out; Shoal Peers approve only for share users; see T34 for hijack |
| T25 | CSRF on admin endpoints | admin action triggered by another site | **Not applicable (W§A-20.5):** core authentication never reads cookies (`AuthorizationContext.cs:60-118`). Admin writes stay POST, PUT or DELETE for hygiene; API-key automation keeps working. |
| T26 | Privacy: the Peer learns viewing habits | friend sees what is watched and when | **Corrected (W§A-15):** the Peer sees the share user, titles, times, positions, play method, and a fixed client profile named "Shoal". It does **not** see Home usernames, client IPs or device names, **provided** the DeviceProfile is rebuilt (6.2.2) and the playlist grammar holds (6.4), so no client contacts the Peer directly. Owner decision 4 accepts the titles part. The reverse direction (W2§N-14) is a residual: a friend's server can send malformed media to your players, as any Jellyfin server can (8.1). |
| T27 | Malicious or broken config via `POST /Plugins/{id}/Configuration` (admin) | bad values | the XML holds only toggles; `SettingsValidator` clamps and never throws; topology lives in `state.json` (3.7) |
| T28 | Plugin disabled or uninstalled while stubs exist | clients raw-play stub URLs | sentinel `.invalid` URL, so nothing leaks and failure is fast; "Remove peer" deletes stubs and the library |
| T29 | Peer core's anonymous static and subtitle routes (K§Surprises 1) | anyone who can reach the Peer and knows a GUID streams it; **revoked tokens still direct-play** | Home never leaks GUIDs (T6); Home stops proxying on any 401 and outside the trust window (4.4); complete revocation = delete the sharing account (stock Peer) and remove the network share (4.5); Shoal Peer guard filter, `LogOnly` by default with one-click `Enforce` (8.4) |
| T30 | Supply chain: public plugin repo, manifest tampering | malicious update | **Corrected (W§A-17):** Jellyfin verifies an **MD5** checksum from the manifest (`InstallationManager.cs:586`), which detects corruption only. Authenticity rests on whoever controls the repository and manifest host. Mitigations in the release checklist (11.5): hardware-key 2FA, protected branches, signed tags, signed release with provenance, locked restores, `SECURITY.md`. |
| T31 | Ex-friend's Home keeps `catalog.json` after revocation | indefinite anonymous access to shared titles | same as T29 |
| T32 | Local user crafts peer transcode parameters | Peer CPU abuse; bypass of Home policy | grants; the Peer query comes only from the Peer's own answers; the client controls only validated integers (6.4, 6.11) |
| T33 | Client body fields forwarded verbatim | peer-side behavior chosen by local users | exact body (6.2.1) |
| T34 | **Removed in v2.8 (D17):** Quick Connect pairing hijack by code-collision flooding (W§A-1). Home no longer starts a QC pairing. History: | attacker gets a share-user token | Baseline poll, 1 s poll and abort with separate states for a wrong base path, restart, 401 and timeouts (4.2a), server-side confirm gate, check string, device lock, session limit, foreign-device watch, invite + typed SAS with commit-reveal, Initiate shield on Shoal Peers (4.3). Residual risk for public stock Peers is stated. |
| T35 | Hostile playlist attributes or lines steer the client elsewhere (`X-ASSET-URI`, `SERVER-URI`, scheme-prefixed "relative" lines) | end-user IP leak to the peer, content injection, caller token sent to an attacker (W§B-1) | closed grammar: only rebuilt lines leave Home (6.4); F-PL fixtures |
| T36 | Peer content executed as HTML or SVG in Home's origin (W§A-3) | admin token theft, then plugin install = code execution on Home | Shoal's own Content-Type from a closed table, `nosniff`, CSP sandbox and `attachment` on non-media, subtitle body checks (8.6); F-RS fixtures |
| T37 | Disk and database exhaustion by peer metadata (W§A-10) | Home disk full, DB bloat | quota, image caps, entity caps, new-items guard (5.10) |
| T38 | Stored XSS in clients via `MediaStreams` or provider ids (W§A-12) | script in every Home user's item page | per-field rules (5.5, 6.2.3); `DisplayTitle` recomputed locally; canonical provider ids; F-MD fixtures |
| T39 | Shoal settings lost when core resets the plugin XML (W§A-14) | peers vanish; secrets and catalogs orphaned | `state.json` store, recovery mode, reconciliation (3.7) |
| T40 | Home users see a friend's library the friend shared only with the admin (W§A-6) | privacy breach toward the friend | default-deny access mode evaluated at every playback request, core library access reconciled, drift list (5.6) |
| T41 | Peer lies about ratings (W§A-6) | restricted child profiles see adult titles | ratings untrusted; restricted profiles excluded unless the admin acknowledges; one-click `Shoal` blocked tag (5.6) |
| T42 | A share-user token mints more tokens for itself (W§A-9), by Quick Connect Authorize **or by resetting the account's own password** (`EnableUserPreferenceAccess` is true and not settable in the 12.1 dashboard), including for a device id registered as non-persistent, which core's device lock ignores (4.1; W2§N-1) | revocation by device deletion, disabling or pausing fails | **Stock Peer:** `MaxActiveSessions=1` after pairing plus Home's session anchor, sent at startup and on first contact after any outage, with the device lock as its required pair for the time Home cannot reach the Peer (4.1 steps 8-9; T-SEC-22 passed, P2§2-5; v2.3, W4-5); Devices-list check; foreign-device watch (4.3); the device lock stops only casual misuse; **revoke = delete the sharing account**, and a safe pause needs a password change (4.5). **Shoal Peer:** share users get 403 on `QuickConnect/Authorize`, `Users/Password` and `Sessions/Capabilities*` (8.4 job d, fail closed), `EnableUserPreferenceAccess=false` is re-applied every tick, and unexpected devices are signed out within 60 s (4.1b). |
| T43 | A grant keeps working after the user's permissions change (W§A-19) | streaming past a new restriction | re-check every 5 min, 12 h absolute lifetime (3.5) |
| T44 | A bug in the Peer guard filter breaks the Peer's own playback (W§A-13) | friend's server unusable | exceptions fail open with a counter for jobs a-c, while job d (the share-user bans) fails closed because it cannot affect the friend's own users (8.4); `LogOnly` default; programmatic route set with a CI diff |
| T45 | The anonymous `/Shoal/Pair` endpoint is abused | brute force, CPU DoS | 128-bit secrets; valid secret checked first and never rate-limited; only failures are counted (per-IP and global buckets, `429` or delay, no invite is ever destroyed by wrong guesses); same work and identical 404 whether or not an invite is active; 1 KB body; typed SAS with commit-reveal (4.1b) |
| T46 | **Chain sharing (W2§N-2):** a server that is both a Home and a Peer admits its own share users to a federated library (`Everyone` mode, or Reconcile) | friend X's Home plays friend Z's library through this server; Z agreed to share with this server's admin only | `Admitted()` is false for share users in both modes; Reconcile removes Shoal libraries from their lists; `PUT Access` rejects them (5.6, 8.5 step 6, R15; T-VIS-7) |
| T47 | **Token sent to another host after an address edit (W2§N-8):** `PUT Peers/{h}` with a new host, a phished admin, XSS in the admin page, a restored `state.json` | the stored token reaches whatever host replays the public server Id | a host change drops the token first (4.4); the sealed entry is bound to peer key and host (8.2); typed name in the body for destructive calls (9.6) |
| T48 | **Anonymous lever on pairing (W2§N-4):** failure counters that cancel invites or fill a global bucket | any caller who can reach the Peer disables invite pairing or Quick Connect for the friend's own users | failures-only limiter that never touches a valid secret; Initiate shield 10/min per IP and 300 per 10 min (4.1b, 8.4 b); per-IP via `GetNormalizedRemoteIP` |

### 8.4 Peer guard filter (Shoal Peer role; W§A-13, W§A-1, W§A-9, W2§N-1, N-4, N-6; owner D9)

`PeerGuardFilter` is one `IAsyncResourceFilter`, registered always, inert unless at least one Share exists. It has four jobs. (The share list comes from `state.json`, or from `peer-role/guard-lastgood.json` in recovery mode, 3.7.)

**(a) Sign-in requirement for shared media (hardening).**
- **Problem:** core 12.1 serves item media without authentication (lab-verified for static stream and subtitles, K§Surprises 1). The share user's library restriction does not protect raw bytes, revoking a token does not stop direct play, and anyone holding GUIDs keeps access while the Peer is reachable.
- **Route set, computed at startup** from `EndpointDataSource`: every `RouteEndpoint` whose metadata carries no `IAuthorizeData` and whose template has a parameter named `itemId`, `routeItemId` or `videoId`. Expected on 12.1 (verified): `Videos/{itemId}/stream[.{container}]` (`VideosController.cs:314,552`), `Audio/{itemId}/stream[.{container}]`, the two subtitle `Stream.{routeFormat}` routes (`SubtitleController.cs:208,295`), `Videos/{videoId}/{mediaSourceId}/Attachments/{index}` (`VideoAttachmentsController.cs:50`), the legacy HLS segment routes `Videos/{itemId}/hls/{playlistId}/{segmentId}.{segmentContainer}` and `Audio/{itemId}/hls/{segmentId}/stream.{mp3|aac}` (`HlsSegmentController.cs:53-56,124-126`), and `Items/{itemId}/Images/{imageType}` (`ImageController.cs:552`).
- The computed set is diffed against a checked-in table. Differences are logged at startup, and a CI test fails on any difference for each new Jellyfin release.
- **Item images are excluded from `Enforce`.** Every client loads posters with plain `<img>` requests, so requiring sign-in there would break the Peer's own users' browsing. They stay anonymous (art only) and are counted in `LogOnly`.
- **Decision per request:**
  1. Resolve the route item. If it is **not** inside a shared library, `next()` untouched. The shared-library decision is cached per item for 60 s (ancestor walk on a miss), **only for items that exist**: an id that resolves to no item is never cached, and the cache is a bounded LRU (10,000 entries), so an anonymous caller spraying random GUIDs at these anonymous routes cannot grow memory (W2§N-6; T-SEC-24). An item added to a shared library can stay anonymous-readable for up to 60 s (accepted). The set of shared collection-folder ids is recomputed when Shares change.
  2. If `HttpContext.User` is authenticated (header or `ApiKey`; disabled users are already anonymous, `AuthService.cs:35`) and it is an API key, or a user for whom `GetItemById<BaseItem>(id, user)` is non-null, `next()`.
  3. Otherwise: in `LogOnly`, `next()` and count the request in an aggregated counter keyed by (route kind, user-agent product token ≤ 32 chars, address class loopback/LAN/tailnet/public, hour), persisted hourly to `peer-role/guard-counters.json`. No URLs, ids, tokens or full IPs are stored. In `Enforce`, anonymous requests get 401 and authenticated-but-not-visible requests get 404, written by `ResponseWriter`.
- It only **adds** requirements; it never relaxes core auth.

**(b) Initiate shield (W§A-1, W2§N-4). Removed in v2.8 (D17):** Home no longer pairs by Quick Connect, so the shield, its sliding windows and its hit counts are gone; the text below is history. `POST QuickConnect/Initiate` (`QuickConnectController.cs:54`) is limited to **10 per minute per remote IP and 300 per 10 minutes in total**, then 429. Active whenever a Share exists and Quick Connect is enabled. It bounds code-collision floods and core's O(pending) `ExpireRequests` cost (`QuickConnectManager.cs:198-229`).
- *Why 300, not 60.* The code space is 900,000 (`100000..999999`), so the chance that a flood of R requests lands on a given pending code is about R/900,000: 300 per 10 minutes is about 3e-4, which still bounds the hijack. A global cap of 60 could be kept full by anyone at six requests a minute, which would turn Quick Connect off for **all of the friend's own users** while it lasts. At 10 per minute per IP, filling 300 takes at least three addresses.
- *Remote IP* is `HttpContext.GetNormalizedRemoteIP()`, core's resolved client address. Behind a reverse proxy without Known Proxies every visitor looks like the proxy, so the per-IP limit degrades to a global one; the Hardening screen warns when most requests come from one private or loopback address and the README documents the setting. The same applies to the invite limiter (4.1b).

**(c) Best-effort per-share transcode limit (W§A-9).** For requests authenticated as a share user to `Videos/{id}/master.m3u8`, `main.m3u8`, `hls1/...`, or `stream` without `static=true`, the filter tracks the `PlaySessionId` query values with a last-seen time. A session is active while seen within 60 s and `ITranscodeManager.GetTranscodingJob(ps)` is non-null. A request with a **new** `PlaySessionId` while the share already has `maxConcurrentTranscodes` (default 4, D15) active sessions gets 429; Home maps that to `RateLimitExceeded`. Direct play is not counted. This is **best-effort, not a cap** (W2§N-6): audio HLS, subtitle conversion and other ffmpeg starts are not counted, and `MaxActiveSessions` does not bound transcodes either (jobs are keyed by device and `PlaySessionId`, not by session). The Hardening screen says "limit", not "cap".

**(d) No self-minting by share users (W§A-9, W2§N-1).** A request authenticated as a **share user** gets 403, written by `ResponseWriter`, on:
- `POST QuickConnect/Authorize` (`QuickConnectController.cs:104-120`): the Quick Connect mint path. Admins approving codes for other users are unaffected.
- `POST Users/Password` and `POST Users/{userId}/Password` (`UserController.cs:270-327`): the password-reset path. The plugin also keeps `EnableUserPreferenceAccess=false` on share users and re-applies it every tick (4.1b step 5), so this ban is defence in depth, not the only control.
- `POST Sessions/Capabilities` and `POST Sessions/Capabilities/Full` (`SessionController.cs:351-400`): registering an arbitrary DeviceId as non-persistent is step 3 of the lock bypass (4.1). Banning it closes steps 3-5 of that chain by itself, even if the Authorize ban had a gap. **Home never calls these routes on a Shoal Peer**: the session anchor of 4.1 and 6.11 is sent only to peers paired by Quick Connect.

The filter looks only at the caller's user id, so admins and ordinary users are untouched. Unlike job (a), job (d) **fails closed**: it touches a handful of share-user routes, so a bug there cannot break the friend's own playback, and an exception inside it returns 403 for that request. Together with `MaxActiveSessions=1` and the device sweep (4.1b), a stolen token can no longer multiply or change the password on a Shoal Peer. The bans depend on knowing the share users, which is why recovery mode keeps `guard-lastgood.json` (3.7).

**Failure policy (W§A-13):** any exception inside jobs (a) to (c) calls `next()`, which fails open for that request, increments a counter shown on the Hardening screen, and logs one Warning line per hour with no ids. A guard bug must never break the friend's own server. The cost is that an internal error in `Enforce` lets one anonymous request through. Job (d) is the exception, as above.

**Modes and rollout (owner D9):**
- Three modes: **`Enforce`** ("Require sign-in", the default for new installs since v2.8, D18), **`LogOnly`** ("Log only"), and **`Off`**. A server that already had Shoal keeps its mode; its page shows a one click "Require sign-in" card with the 7 day log result. `Off` survives only as a troubleshooting escape hatch for a global filter running on someone else's server: it disables job (a) entirely (no checks, no counting). Jobs (b), (c) and (d) stay on whenever a Share exists. The API lists all three; the UI shows `Off` de-emphasized, under "Troubleshooting" (9.5).
- The mode lives in `state.json` (3.2a), so an XML reset cannot downgrade it.
- The Hardening screen shows "N anonymous requests for shared titles would have been blocked in the last 7 days", by user agent and address class, plus the "may break" table below. **Turn on protection** is one click with one confirmation, and **Turn off** is one click.
- After 7 days, if the only anonymous traffic is none or unknown, the screen recommends `Enforce`.

| Client or feature | How it could break under `Enforce` | Likely impact | Detection |
|---|---|---|---|
| Apps still sending legacy `api_key=` or `X-Emby-Token` (ignored in 12.x with legacy auth off, S§1.7) | treated as anonymous: 401 | medium (older third-party apps) | LogOnly counters by user agent |
| DLNA plugin and UPnP renderers (no headers) | 401 if DLNA hands out stream URLs without `ApiKey` | high for DLNA users of shared libraries | LogOnly; Pilot test if the DLNA plugin is available |
| Cast receivers, "copy stream URL", external players | 401 if the URL lacks `ApiKey` | low; jellyfin-web puts `ApiKey` in static URLs | LogOnly |
| Kodi native-path mode; server-side ffmpeg (reads files) | not affected | none | n/a |
| Home's Shoal proxy | sends the header token, so it passes; a revoked token now gets 401 on static too | none (desired) | T-SEC-9 |

### 8.5 CallerContext: the only authorization path for federated requests (W§A-4, W§B-6)

**Why it matters.** Core has no `[Authorize]` on its stream, subtitle, attachment and legacy HLS routes. For federated items, Shoal's filter is the only thing between the network and peer bytes. The "no `[AllowAnonymous]`" reflection test cannot see this, because the filter short-circuits core's anonymous actions.

`CallerContext.AuthorizeAsync(HttpContext http, Guid localItemId, CancellationToken ct)` returns `Caller` or `Deny(status, playbackErrorCode)`. Every federated request runs it: the intercept filter (every route in 6.1), `Shoal/Subtitles`, and `Shoal/Hls` (steps 1-4 per request; 5-7 at grant creation and every 5 min). Steps, in order:

1. **Identity from `HttpContext.User`, never from the token.** The default scheme `CustomAuthentication` (`ApiServiceCollectionExtensions.cs:100-101`) has already run `AuthService.Authenticate` (`AuthService.cs:23-40`). Invalid or logged-out tokens and **disabled** users fail there and leave an anonymous principal (`CustomAuthenticationHandler.cs:46-90`). If `User.Identity?.IsAuthenticated != true`: **401**.
   **Credential sources (v2.2, verified `AuthorizationContext.cs:84-111`).** Core takes the token from the `Authorization: MediaBrowser ... Token="..."` header first, then from the `ApiKey` query value (any letter case; always on), and only with legacy authorization enabled (off by default in 12.x) from `X-Emby-Token`, `X-MediaBrowser-Token` or `api_key`. The header wins when both are present. So every supported client passes the same way: Moonfin-Core sends header **and** `ApiKey` (plus an ignored `X-Emby-Token`), Roku the header only (with an extra `UserId=` field core ignores), Moonfin Smart-TV and jellyfin-web `ApiKey` only on media and subtitle URLs (SC§2.1-2.3). Shoal never parses credentials itself (8.8), and never logs the query string that carries `ApiKey` (6.1 "Logging"). The `ApiKey` that Shoal echoes into `TranscodingUrl`, `DeliveryUrl` and playlists is the `Token` claim, so a header-only client still gets working URLs.
2. **API keys:** claim `InternalClaimTypes.IsApiKey == "True"`: **403**. Never `next()`.
3. **Core's default policy:** `var policy = await policyProvider.GetDefaultPolicyAsync(); var r = await authorizationService.AuthorizeAsync(http.User, null, policy);`. This runs `DefaultAuthorizationHandler` (`DefaultAuthorizationHandler.cs:41-94`): **remote access** (`EnableRemoteAccess` versus `INetworkManager.IsInLocalNetwork`) and the **parental access schedule**. Failure: **403**. The handler throws `ResourceNotFoundException` when the user no longer exists (`:60-64`); catch it: **401**.
4. **User object:** parse `InternalClaimTypes.UserId` (N format); `IUserManager.GetUserById(id)` must be non-null (deleted user: **401**) and not `IsDisabled` (**401**, belt and braces). From here on, `user` is non-null by construction. CallerContext throws internally rather than ever passing null onward.
5. **Library visibility via the user's own view:** `ILibraryManager.GetItemById<BaseItem>(localItemId, user)` with that **non-null** user. It returns null unless `item.IsVisibleStandalone(user)` (`LibraryManager.cs:1734-1738, 4074-4087`), which covers library access (`EnableAllFolders`/`EnabledFolders`), max parental rating, `BlockUnratedItems`, blocked and allowed tags (`BaseItem.cs:1541-1585, 1865-1905, 2042`). Null: **404**. `GetItemById(id, null)` returns any item, which is why the nullable overload is banned (8.8).
6. **Shoal access mode** (owner D3/D4): `Admitted(user, L)` for the item's library L (5.6), read from `state.json` at request time. `Everyone` admits every non-restricted account, including ones created after the switch; `Selected` admits exactly `selectedUserIds`; restricted profiles need `includeRestricted` or an individual selection. Otherwise **404**, indistinguishable from "no such item". No per-user list is materialized, so there is nothing to fall out of date.
7. **Permission:** `user.HasPermission(EnableMediaPlayback)`, else **403** (`ErrorCode=NotAllowed` on PlaybackInfo). Transcode and remux permissions shape the forwarded body (6.2.1); they do not deny.
8. **Return** `Caller { UserId, PolicySnapshot (the flags 6.2.1 needs), Token (claim, used only to echo in URLs), DeviceId, IsLocalNetwork }`. **`DeviceId` is client-chosen (v2.3, W4-2):** core copies it from the request's `Authorization` header (`AuthorizationContext.cs:117`; the device row is used only when the header omits it, `:146-148`; `CustomAuthenticationHandler.cs:65`) and never compares it with the token's device. It is a convenience key within one user, never an identity; CallerContext applies the 3.5 cap (128 printable ASCII characters, else hashed) before returning it.

PlaybackInfo is also behind core's `[Authorize]`, so for that route steps 1, 3 and 4 have already been enforced by core. CallerContext runs them anyway; the cost is a claims read and a cached user lookup.

### 8.6 Response hardening (W§A-3, W§B-3, W§B-10)

Every response Shoal writes goes through `ResponseWriter`: intercepted core routes, `/Shoal/Hls/*`, `/Shoal/Subtitles/*`, `/Shoal/Admin/*`, `/Shoal/Pair`, and Peer guard denials. Handlers cannot set `Content-Type` or copy peer headers themselves; a unit test asserts every handler result type goes through it.

**On every response:**
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: no-referrer`
- `Cache-Control: no-store`
- `Content-Type` from the emitted table below, **never copied from the Peer**

**Additionally on non-media responses** (playlists, subtitles, JSON, errors):
- `Content-Security-Policy: default-src 'none'; sandbox`
- `Content-Disposition: attachment` on playlists and subtitles. This keeps a navigated URL from rendering; XHR, fetch, `<track>` and hls.js ignore it.

**Never forwarded from the Peer:** `Server`, `Set-Cookie`, `Location`, `Content-Disposition`, `Link`, `Refresh`, `Content-Security-Policy`, `Access-Control-*`, any `X-*`, or any header not in a route's whitelist (6.3).

| Response | Emitted `Content-Type` | Upstream accepted (media type, case-insensitive, parameters ignored); anything else = 502 and a counter |
|---|---|---|
| Static stream | by validated container: `mkv` video/x-matroska; `mp4`, `m4v` video/mp4; `webm` video/webm; `mov` video/quicktime; `ts`, `m2ts`, `mts` video/mp2t; `avi` video/x-msvideo; `mpg`, `mpeg` video/mpeg; `wmv` video/x-ms-wmv; `ogv` video/ogg; anything else application/octet-stream | video/x-matroska, video/mp4, video/x-m4v, video/webm, video/quicktime, video/mp2t, video/x-msvideo, video/mpeg, video/x-ms-wmv, video/ogg, video/3gpp, video/x-flv, application/octet-stream |
| HLS playlists (all three kinds) | application/vnd.apple.mpegurl | application/vnd.apple.mpegurl, application/x-mpegurl |
| HLS segments | `ts` video/mp2t; `mp4` (incl. `-1.mp4` init) video/mp4 | video/mp2t, video/mp4, application/octet-stream |
| VTT (HLS subtitle segments, external `.vtt`) | text/vtt; charset=utf-8 | text/vtt, text/plain, application/octet-stream |
| SRT | application/x-subrip | application/x-subrip, text/plain, application/octet-stream |
| ASS / SSA | text/x-ssa | text/x-ssa, text/plain, application/octet-stream |
| JSON subtitles `js` / `json` (v2.2) | application/json; charset=utf-8, rebuilt by Home (6.6a step 9) | application/json, text/plain, application/octet-stream (core's mime table maps `.json`; the body is parsed as JSON whatever the type says, so the type is not a trust decision) |
| Bitmap subtitles `sup` / `pgssub` (v2.2) | never emitted: 404 before any Peer call (6.6a) | n/a |
| PlaybackInfo, admin JSON, errors | application/json; charset=utf-8 | application/json on every Peer API call |
| Images (sync, not served by Shoal) | stored as files; core serves them | image/jpeg, image/png plus magic bytes (5.4) |

**Subtitle body checks:** valid UTF-8 (BOM allowed), no NUL. JSON subtitles are rebuilt rather than checked (6.6a step 9), and **v2.3 (W4-9): so are VTT and SRT** (6.6a step 9a: cue text through the `<i> <b> <u>` allowlist, timing lines re-serialized, VTT `NOTE`/`STYLE`/`REGION` blocks dropped), including HLS subtitle segments. ASS/SSA must contain `[Script Info]` in the first 4 KB. Failure: 502.

**Not sent: `Cross-Origin-Resource-Policy`** (see Changes in v2). Direct play through `<video src>` is a no-cors request, so CORP same-origin would break Chromecast receivers and separately hosted web clients. The token is the gate.

**Test:** fakepeer returns `text/html`, `image/svg+xml`, `application/json; charset=bogus`, a `Location` on a 200 and `Set-Cookie` for every proxied route (F-RS-01..03, 07, 08). Home's response type and headers are asserted per route (T-SEC-18).

### 8.7 Negative authorization matrix (T-SEC-15; acceptance for K6, K13-K17)

Every cell is a lab test against lab-server with fakepeer as the Peer. Each cell asserts three things: (1) the status below; (2) **fakepeer received zero requests** for that cell; (3) the response body holds no media bytes and no peer-derived string. "core" marks cells where core's own `[Authorize]` answers before Shoal; the code is core's, and the other two assertions still apply.

| Caller (rows) \ Route (columns) | PlaybackInfo POST | static GET | static HEAD | core subtitle `Stream.{fmt}` | `Shoal/Subtitles` | `Shoal/Hls` master | main | segment |
|---|---|---|---|---|---|---|---|---|
| R1 no token | 401 core | 401 | 401 | 401 | 401 core | 401 core | 401 core | 401 core |
| R2 garbage token | 401 core | 401 | 401 | 401 | 401 core | 401 core | 401 core | 401 core |
| R3 valid token, user disabled | 401 core | 401 | 401 | 401 | 401 core | 401 core | 401 core | 401 core |
| R4 token of a deleted user | 401 or 404 core | 401 | 401 | 401 | 401 or 404 core | same | same | same |
| R5 logged-out token | 401 core | 401 | 401 | 401 | 401 core | 401 core | 401 core | 401 core |
| R6 API key | 403 | 403 | 403 | 403 | 403 | 403 | 403 | 403 |
| R7 `EnableRemoteAccess=false`, non-LAN IP | 403 core | 403 | 403 | 403 | 403 core | 403 core | 403 core | 403 core |
| R8 outside `AccessSchedule` | 403 core | 403 | 403 | 403 | 403 core | 403 core | 403 core | 403 core |
| R9 no access to the Shoal library | 404 | 404 | 404 | 404 | 404 | 404 | 404 | 404 |
| R10 parental rating excludes the item | 404 | 404 | 404 | 404 | 404 | 404 | 404 | 404 |
| R11 mode `Selected`, user **not selected** (has core library access) | 404 | 404 | 404 | 404 | 404 | 404 | 404 | 404 |
| R11b mode `Everyone`, restricted profile, `includeRestricted=false` | 404 | 404 | 404 | 404 | 404 | 404 | 404 | 404 |
| R11c switched `Everyone` → `Selected`; user created while in `Everyone` mode | 404 | 404 | 404 | 404 | 404 | 404 | 404 | 404 |
| R12 `EnableMediaPlayback=false` | 403 (`NotAllowed`) | 403 | 403 | 403 | 403 | 403 | 403 | 403 |
| R13 another user's grant id (own valid token) | n/a | n/a | n/a | n/a | n/a | 404 | 404 | 404 |
| R14 grant whose user lost access within the last 5 min | n/a | 404 | 404 | 404 | 404 | 404 at the next 5-min re-check | same | same |
| R15 share user of this server (two-way), library in `Everyone` mode or the id forced into the selection (5.6, W2§N-2; v2.2 adds the row the text already cited) | 404 | 404 | 404 | 404 | 404 | 404 | 404 | 404 |
| R16 (v2.3, W4-3, W4-12) caller admitted to federated item B only; route id = federated A, `itemId=B`; variants: `itemId` repeated (`itemId=<local>&itemId=<federated>`), B/P/X GUID forms, upper case, `%20` padding, a fully percent-encoded query | n/a | n/a | n/a | 404 | n/a | n/a | n/a | n/a |

Positive control rows run alongside, so a broken fakepeer cannot make the matrix pass vacuously. Each expects 200/206 and exactly one fakepeer request: P1 the pairing admin in `Selected` mode; P2 a selected user; P3 mode `Everyone`, an account **created after** the switch through the dashboard's normal "add user" flow, tested 60 s later with no admin action in Shoal (owner D4; the creation-time reconcile of 5.6 gives it core library access, and step 6 admits it); P4 switched back to `Selected`, a user from the restored selection.

### 8.8 Banned APIs and static checks (W§A-4, W§B-2)

`Microsoft.CodeAnalysis.BannedApiAnalyzers` with a `BannedSymbols.txt`, as Jellyfin itself does (`Directory.Build.props:18`), with warnings as errors:

| Banned symbol | Allowed only in |
|---|---|
| `IAuthorizationContext.GetAuthorizationInfo(...)` (all overloads) | nowhere |
| `ILibraryManager.GetItemById<T>(Guid, User?)`, `GetItemById<T>(Guid, Guid)` | `CallerContext`, `PeerGuardFilter` (both pass a non-null user) |
| `ILibraryManager.GetItemById(Guid)` (no user) | `ShoalIndex`, `StubEnricher` (classification, never authorization) |
| `HttpContent.ReadAsStringAsync`, `ReadAsByteArrayAsync`, `ReadAsStreamAsync`; `HttpClient.GetStringAsync`, `GetByteArrayAsync`, `GetStreamAsync`; `HttpContentJsonExtensions.ReadFromJsonAsync` | `PeerClient`'s `BoundedReadStream` factory |
| `IHttpClientFactory` | nowhere (it logs request URLs) |
| `System.Random` | nowhere (ids and nonces use `RandomNumberGenerator`) |
| `HttpResponse.ContentType` setter, `HttpResponse.Headers` writes | `ResponseWriter` |
| `HttpRequest.QueryString`, `HttpRequest.Path`, `HttpRequest.PathBase`; `UriHelper.GetDisplayUrl`, `GetEncodedUrl`, `GetEncodedPathAndQuery` (v2.3, W4-14: they carry the client's `ApiKey`) | `ShoalRequest` (the 8 KB query-length check, and logging of the route **template** only; its output never contains a query) |
| `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` (v2.3, W4-8) | nowhere |

**Reflection tests:** every controller action carries `[Authorize]` (any policy) or `[AllowAnonymous]`; `[AllowAnonymous]` appears exactly once, on `PairController.Pair`. Every route in 6.1 is present in the filter table. The playlist grammar has no `RegexOptions.IgnoreCase` (a test scans the compiled patterns).

---

## 9. Admin UI: data, behavior and API (screens per `docs/ui-spec.md`)

Scope: what each screen must show and do, where the data comes from, which states exist, and the security rules. Visual design, layout, copy and components belong to **Lumen**; `docs/ui-spec.md` (Native Plus, owner D2) is the source of truth for those. This section replaces v1's S1-S8 screen list with the data and behavior those screens need from v2. Kiro 2 builds the UI (owner D10). Non-admin users get **no plugin UI** in MVP: federated titles are normal items, identified by the library name and the `Shoal` / `Shoal: <Peer>` tags.

### 9.1 Rules for every screen

- **Admin only.** Rendered inside the Jellyfin dashboard as the plugin's pages. Works with light and dark dashboard themes and at phone width.
- **Data only from `/Shoal/Admin/*` (JSON).** The page never calls `getPluginConfiguration` or `updatePluginConfiguration` (W§B-7).
- **Never displayed or sent to the browser:** tokens, invite secrets after first display, peer item, library, user or play-session GUIDs. Peers, libraries, shares and devices are referenced by local handles. The peer **URL** is shown, because the admin entered it.
- **Peer-supplied strings** (server name, library names, titles, device names, Home names on the Peer side, user agents) are text only (`textContent`, never HTML), truncated with full text on expand. Tests T-SEC-11, T-PAIR-7.
- **Confirmation:** every destructive action states its effect. Irreversible ones ("Remove peer and delete library", "Revoke friend") require typing the peer or friend name.
- **Rate-limited actions** show a cooldown from `Retry-After`.
- **Units:** local time; sizes in GB or TB (base 10, one decimal); throughput in Mbps; latency in ms.
- **Refresh:** status every 10 s while visible; pairing status every 2 s while waiting (the server polls the Peer on its own schedule, 4.3); nothing while hidden.
- **Recovery mode** (3.7) and `SecretsUnreadable` (8.2) show a blocking banner on every screen, with its actions.

### 9.2 Screen map (owner D2, D6; layout per `docs/ui-spec.md` 2.1)

```
Dashboard drawer > Plugins > Shoal
  Overview .............. view=overview    both roles at a glance; "Friends watching from you" at the bottom (D6)
  Libraries & access .... view=libraries   access mode + selection per incoming library; what each friend sees
  Settings .............. view=settings    settings, "Protect your shared videos" (8.4), danger zone
     Pair a server ...... view=pair[&dir=watch|share]   both pairing directions
     Server detail ...... view=server&id=<handle>       one friend's server (was S3 + S4)
     Overlap report ..... view=overlap&id=<handle>      5.8
     Friend detail ...... view=friend&id=<handle>       sharing side (was S5 + S6)
```

The page title is "Shoal". UI copy uses Lumen's plain words (server, friend, sharing account, safety code; `docs/ui-spec.md` 0.2); code and API keep this document's terms.

### 9.3 What each screen needs from v2

**Overview** (`GET Shoal/Admin/Status`).
- Card status values: `Online`, `Degraded`, `Offline`, `AuthFailed`, `IdentityMismatch`, `TlsKeyChanged`, `Disabled`, `Pairing`, `PairingAborted`, plus `syncing`. `TlsKeyChanged` uses the "Different server" chip with a certificate hint; `PairingAborted` uses "Pairing stopped for your safety" for the safety kinds and "Pairing didn't finish" for every other kind (v2.2, 4.2a), with **Dismiss**.
- Status also carries `restoredFrom {file, atUtc, needsConfirm}` for the restore banner (3.7), confirmed by `POST Shoal/Admin/State/ConfirmRestore` (v2.2).
- The offline card shows the stored backdrop of the newest title, desaturated, with an "Offline" tag (owner D5). The image is served by core from the stub, addressed by local item id.
- Bottom section "Friends watching from you" (owner D6): per share `{handle, name, status: Active|Paused|AccessRemoved, libraryCount, deviceCount, unexpectedDeviceCount, lastSeenUtc, playingNow, transcodingNow}`.
- Alert counts include held removals, held additions, foreign device, quota and merged-version warnings.

**Pair a server, watch direction** (Home role, 4.2):
1. *Address:* server name, address, network type, proxy, LAN acknowledgement. The invite may be pasted here or at step 3. There is no route question (v2.8, D17).
2. *Check:* server name, version, address, latency, **Server ID** (first 8 hex, labelled as detecting a wrong or reset server), and whether the server has Shoal (`hasShoal`). **No safety code at this step.** `hasShoal` false shows the blocking "Install Shoal first" screen with a link, a copyable message for the friend and **Check again**; a throttled or unreadable ping (`shoalPingThrottled`) continues with a soft hint (section 12 has the copy).
3. *Invite, then Code:*
   - The safety code (SAS), "Read this code to {Friend}; they type it on their screen to approve", "Waiting for {Friend} to approve" and **Cancel**. No "codes match" or "they don't match" buttons on this path (v2.2, UI§14.2 #1; 4.2 step 3): the friend's typed code is the confirmation. Outcomes `denied` and `codeMismatch` are shown separately.
   - A legacy QC peer is re-paired with this same flow, started by **Pair again with an invite** on its card (4.1); nothing about the old sharing account is asked of the friend except to delete it afterwards.
4. *Choose what to add:* libraries, local names, and "Who can see this" with only the pairing admin pre-selected (owner D3). "Everyone" is not offered here; the switch lives on the library (owner D4).

**Pair a server, share direction** (Peer role with Shoal, 4.1b): Who and what, then Connect (Tailscale share or https confirmation, then **Send {Friend} an invite** only), then Approve (approval card: Home name, server id, address class, a **code input** "Type the safety code shown on {Home}'s screen", Approve and Deny; v2.2, UI§14.2 #2: the card never shows the code, the browser never receives it, Approve enables at 8 valid characters and the server judges, a miss shows "{n} tries left", the third miss shows "Request cancelled" with "Create a new invite"), then Done.

**Server detail:**
- Header shows "Server ID {8 hex}" (not "Safety code") and "Paired {date} by invite". A legacy peer shows "Paired {date} the old way" with the status "Needs pairing again" and the button **Pair again with an invite** (v2.8; no device lock or session limit chips).
- Alerts: removal hold, addition hold, access removed, different server, certificate changed (old and new key fingerprints, "Trust new certificate"), another device on your account (device name as text), merged version, disk quota reached, stub folder problem; v2.4: the sharing account's policy, its session limit, and catalog unreadable (the closed kind list is in 9.5).
- Settings: streams at once (default 4), **conversions at once (default 4 (D15), at most the streams value; v2.4, 6.9)** with a help line ("Each conversion uses {Server}'s processor; direct plays don't"), priority, proxy, optional certificate pin.
- Usage: stubs, disk use against the quota, name counts against caps (5.10); v2.4: conversions running now against the limit.

**Libraries & access** (5.6):
- Per incoming library: an access switch **Only selected users / Everyone on this server**; the "Who can see this" picker (enabled in `Selected` mode; in `Everyone` mode it shows the remembered selection read-only, "restored if you switch back"); "Include profiles with parental limits" (`Everyone` mode only, behind the ratings acknowledgement); drift list.
- Confirm dialogs list who gains access (switching to Everyone) or who loses it (switching back, including accounts created meanwhile).
- User rows carry `allLibraries`, `managedByShoal`, `restricted`, `isAdmin`, `isDisabled`.

**Settings:** fields of 3.2 and StubRoot; the offline card (`OfflineCardEnabled`, `RequestServiceName`, `RequestServiceUrl`, v2.8.4); "Protect your shared videos" (8.4): `Require sign-in` (the default for new installs, D18) and `Log only` as the two choices, `Off` under a "Troubleshooting" disclosure; counters; shield and transcode-cap hit counts; guard error count. Key storage reads `JELLYFIN_SHOAL_KEY`. Reconciliation findings and the last state backup time. Danger zone.

**Friend detail** (Peer role):
- Header: Pause / Resume (4.1b step 6).
- **Invite** (create, one-time display, countdown, cancel) and the pending **approval** card. Pairing is by invite only (4.1b).
- Devices, with unexpected ones highlighted and a per-device Remove.
- Libraries, quality limit and transcode cap.
- "Stop sharing" guided revoke (4.5): Shoal disables, signs out every device, verifies none remain and deletes the sharing account, then shows the Tailscale step and the protection status.

### 9.4 States every screen must design for

loading; empty; peer `Offline` (data shown as stale with its age; card art desaturated); `AuthFailed`; `IdentityMismatch`; `TlsKeyChanged`; `PairingAborted` (safety kinds and stopped kinds, 4.2a; every kind in the 9.5 error list); held removals or additions; action in progress (sync, test, pairing); action rate-limited (cooldown); recovery mode; secrets unreadable; drift present; server error (generic message, never peer error bodies).

### 9.5 Resolutions of `docs/ui-spec.md` section 14

| # | Lumen's question | Resolution |
|---|---|---|
| 1 | Safety code format, length, when shown, and the stock friend's side | **Invite path:** SAS, 8 Crockford base32 characters (`0-9 A-Z` without `I L O U`), shown as two groups, `K7QM 2XA4` (40 bits). ~~It is shown on both servers~~ **v2.1/v2.2: Home displays it after redeeming the invite; the Peer never displays it, and its admin types it into a code input to approve** (4.1b step 3). ~~Quick Connect path: the 4 character check shown with the 6 digit code~~ **Removed in v2.8 (D17).** The safety code is not shown at the Check step. Update the 6.10 example and `aria-label` to these formats, and the 8.3.1 header to "Server ID". |
| 2 | Invite format, lifetime, which side creates it | The **sharing side** creates it in "Share with a friend" / Friend detail. Format `shoal1-<8 hex>-<26 Crockford base32 in groups of 4>`. Input ignores case, spaces and hyphens. **15 minutes**, single use. ~~After 3 failed redemption attempts every active invite is cancelled~~ **Withdrawn in v2.1 (W2§2d):** failed redemptions are only slowed down (4.1b Limits) and never cancel an invite; the admin sees a calm notice ("N wrong invite attempts in the last hour; your invites still work"). Separately, three wrong **safety codes** typed by the Peer admin end that one request and its invite (4.1b step 3, v2.2). Remove the "Enter the code {Friend} sent" option (8.2.4 step 2), the "Approve a code" section (8.7) and the "If they have Shoal" Quick Connect instruction (8.2.3 step 3): with Shoal on both sides, pairing is invite-only. |
| 3 | Default-deny mechanics | 5.6: ticking an "all libraries" account changes nothing in Jellyfin; leaving it unticked makes Shoal convert it to a Shoal-maintained list that keeps every other library, including future ones, and restore "all libraries" when nothing is hidden anymore. New Shoal libraries are hidden from other admins too; only the pairing admin is pre-selected. Plus the access switch (owner D4). Picker notes: "Has access to all libraries. If unticked, Shoal hides this library from them and keeps the rest as it is." and "Parental limits: ratings come from {Server} and aren't checked". |
| 4 | Does hardening "Off" survive? | Yes, as a troubleshooting escape hatch only (8.4). The API returns `modes: ["LogOnly","Enforce","Off"]`; the UI shows `Off` under a "Troubleshooting" disclosure, not as a peer of the two main choices. |
| 5 | Endpoint renames and the pairing-session resume contract | Prefix `Shoal/Admin/` (9.6). Exactly one pairing session per direction and target: one watch-side session per Home, one share-side approval per friend. It lives in server memory, survives page reloads and navigation, keeps polling the Peer without the page, and expires with its code or invite (10 or 15 min). A Home restart loses it; the peer record's `pairing.phase` and `pairing.method` (4.2a) turn it into `PairingAborted(interrupted, phase, method)`, which the UI titles "The code expired" (Quick Connect) or "The invite expired" (invite; v2.3, UI§14.3 #4) before approval and "Shoal restarted before you confirmed" after it (v2.2, UI§14.2 #6). `GET` returns the current session or 404; `DELETE` cancels. |

**Data shape changes for `docs/ui-spec.md` section 13:**
- The probe returns `serverIdPrefix`, not `safetyCode`.
- **v2.8 (D17) contract.** `POST Shoal/Admin/Pairing` body `{probeHandle, displayName}`; `method` is ignored and `"qc"` answers `409 methodUnavailable`; a probe without an invite cannot start a pairing; `POST Shoal/Admin/Pairing/Confirm` is deleted. The probe answer has `hasShoal` and `shoalPingThrottled`. The pairing session has no `code`; `safetyCode` is the invite safety code. `peers[].pairingMethod` stays (`invite`, or `qc` for a legacy peer, from which the UI shows the legacy banner and the paused status). The legacy outcome kinds are never produced (4.2a). The fields below that mention `code`, `method: qc`, `sessionLimit` and `accountNotes` describe the design up to v2.7.
- Pairing session (watch side, up to v2.7): `{direction: watch, method: invite|qc, step, state, code?, safetyCode?, expiresUtc, error?, phase?, peerSilent, paired}` (`code` and `safetyCode` only after the baseline poll, 4.2a; `expiresUtc` is the 15-minute confirm deadline in AwaitingConfirm; `peerSilent` after 3 failed polls in a row; `paired` when the server already had a working pairing, i.e. a re-pair). Error kinds (v2.2, complete): **safety** `requestReplaced`, `codesDidNotMatch`, `serverChanged`, `adminAccount`, `badPeerResponse`; **stopped** `expired`, `denied`, `codeMismatch` (invite path, three wrong safety codes), `inviteInvalid`, `quickConnectOff`, `quickConnectUnreachable`, `likelySessionLimit` (v2.3, Quick Connect only, inferred per 4.2a "Session limit, inferred"; v2.4: with `phase: afterApproval|beforeApproval`; ui-spec's proposed `sessionLimitBlocked` is this kind under Kiro's name), `interrupted` (with `phase: beforeApproval|awaitingConfirm`), `network`, `rateLimited`, `server`. `POST Shoal/Admin/Pairing` with a used or older-than-10-minutes probe handle answers `409 {error:"probeExpired"}`; with a live session, `409 {error:"sessionActive"}`. `requestReplaced` copy follows 4.2a ("delete the sharing account and make a new one", never "disable").
- Approval (share side): `GET .../Approval` → `{state: pending|approved|denied|cancelledWrongCode|collected|uncollected, homeName, homeServerIdPrefix, addressClass, triesLeft}`, never the code. `POST .../Approval {approve:true, sas}` → `{match:true, state:"approved"}` or `{match:false, triesLeft}` (on the third miss `triesLeft:0, state:"cancelledWrongCode"`); `{approve:false}` → `{state:"denied"}`.
- Library mappings: `{mappingKey, localLibraryName, serverHandle, serverName, count, lastSync, heldRemovals, heldAdditions, access: {mode, selectedUserIds, includeRestricted}, drift: [{userId, kind}]}`.
- Users: `{id, name, imageTag?, isAdmin, isDisabled, isShareUser, allLibraries, managedByShoal, restricted}` (`isShareUser` rows cannot be selected, 5.6).
- Server statuses gain `TlsKeyChanged` and `PairingAborted`. Peer detail (v2.2, UI§14.2 #11, accepted as proposed): `sessionLimit`, `accountNotes[]` (the informational notes of 4.1 "What Home checks"), `pairingSession` (handle of a live session, if any), `pairingError`, `pairingStoppedUtc`, `policy` values including `liveTv`, `remoteControl`, `allDevices`, and the alert kind `sessionLimit`.
- **Value names (v2.3, UI§14.3 #2, #6).** `sessionLimit: ok|notSet` (`ok` when `MaxActiveSessions` is exactly 1, `notSet` for 0 or more than 1; absent before Confirm and on invite pairings, Quick Connect only). `accountNotes[]` uses the four values Kiro already ships (`PairingEnums.cs` `AccountNote`): `userPreferenceAccess`, `mediaConversion`, `syncTranscoding`, `publicSharing`; the UI shows `userPreferenceAccess` as its "password self-change" note and any of the other three as one "options Jellyfin ignores" note. `pairingMethod: invite|qc` is the method of the working pairing, and on a never-paired server (a stopped first pairing) the method of the stopped attempt; `pairingErrorMethod: invite|qc` accompanies every `pairingError` and is always the stopped attempt's method (it differs from `pairingMethod` on a re-pair by another method). The `interrupted` copy uses `pairingErrorMethod`. `pairingPhase` carries the stored `phase`.
- **Value names (v2.4, Kiro 2 round 2; both sides converge on these).**
  - `policy[]` (the `{kind:"policy"}` alert): `allLibraries`, `contentDeletion`, `contentDownloading`, `liveTv`, `remoteControl`, `allDevices`. These are Kiro's `AccountWarning` names, mirroring Jellyfin's `EnableContentDeletion` and `EnableContentDownloading`; the page's `deletion` and `download` keys are renamed.
  - `sessionLimit` is **its own alert**, `{kind:"sessionLimit"}`, never a `policy[]` value. The admin API maps `AccountWarning.SessionLimit` to it; it is present exactly when the peer reads `sessionLimit: notSet` on a confirmed Quick Connect pairing.
  - **Alert kinds, closed list:** `heldRemovals`, `heldAdditions`, `authFailed`, `identityMismatch`, `tlsKeyChanged`, `foreignDevice`, `policy`, `sessionLimit`, `mergedVersion`, `diskQuota`, `entityCap`, `entryCap`, `stubFolder`, `catalogUnreadable` (v2.4: the catalog and its `.bak` are unreadable, so sync stopped rather than treat every stub as new; MVP way out: remove the library and add it again).
  - `likelySessionLimit` copy uses `pairingPhase`: **`afterApproval`** (rule a: the friend approved, then their server refused to finish; the session limit is the likely reason: set it to 0, start again, back to 1 when done) and **`beforeApproval`** (rule b, re-pairs only: the code ran out; if the friend entered it, the limit is probably why, same fix; if they did not get to it, just start again). 4.2a.
  - Peer `GET`/`PUT` carries `maxConcurrentTranscodes` (1..`maxConcurrentStreams`, default 4, D15) next to `maxConcurrentStreams`; peer detail adds `conversionsNow`.
  - Settings `tailscaleSocket`: `auto`, `off` or a path (Kiro 2's open question 1, accepted).
- Status and Settings (v2.2, UI§14.2 #12, accepted as proposed): `inviteFailures {lastHour, notice}`, `proxySuspected`, `backupByCopy`, `state.guardLastGood`, `restoredFrom {file, atUtc, needsConfirm}`, `secretsReason: badKey|bothKeys`, `keySource: env|envFile|dpapi|file` (ui-spec 8.5 names).
- Destructive calls carry the typed name as `confirmName` in the body (9.1, 4.4 N-8): `DELETE Peers/{h}`, `DELETE Shares/{id}`, `PUT Peers/{h}` with a new host, `DELETE Shoal/Admin/All`.
- Hardening: `{mode, modes, sinceUtc, counters[], guardErrors, shieldHits, transcodeCapHits}`.

### 9.6 Admin API (`/Shoal/Admin/*`, all `RequiresElevation`, JSON, handles only)

| Role | Method and route | Purpose |
|---|---|---|
| H | `GET Shoal/Admin/Status` | Overview data |
| H | `POST Shoal/Admin/Peers/Probe` `{url, network, proxyUrl?, invite?, lanAck?}` | step 1-2 check; returns a probe handle |
| H | `POST Shoal/Admin/Pairing` `{probeHandle, displayName, method}`; `GET Shoal/Admin/Pairing`; `POST Shoal/Admin/Pairing/Confirm` `{match}`; `DELETE Shoal/Admin/Pairing` | watch-side pairing session (9.5 #5) |
| H | `POST Shoal/Admin/Peers/{h}/DismissPairing` `{}` (v2.2, UI§14.2 #8) | clears a stored `PairingAborted` (4.2a): a never-paired peer's record is removed (`{removed:true}`); a re-paired peer returns to its previous state (`{restored:true, status}`). `409` when no abort is stored or a pairing session is live for that peer |
| H | `GET`/`PUT`/`DELETE Shoal/Admin/Peers/{h}` (`?deleteLibrary=true`; `DELETE` and a host-changing `PUT` need `confirmName`; v2.4: `PUT` accepts `maxConcurrentTranscodes`), `POST .../{h}/Sync`, `POST .../{h}/Pause`, `POST .../{h}/Resume`, `POST .../{h}/TrustCertificate` | server detail |
| H | `GET .../{h}/RemoteLibraries`, `POST .../{h}/Libraries`, `DELETE .../{h}/Libraries/{m}` | add or remove libraries |
| H | `POST .../{h}/Holds/{kind}/Release`, `POST .../{h}/Holds/{kind}/Keep` | removal and addition holds |
| H | `GET Shoal/Admin/Libraries`, `PUT Shoal/Admin/Libraries/{m}/Access` `{mode, selectedUserIds, includeRestricted, ratingsAcknowledged}`, `POST Shoal/Admin/Libraries/{m}/Reconcile` | Libraries & access |
| H | `GET .../{h}/Certificate` (v1.0.2, Warden 11 H1): `{pin, fingerprint, chainValid, pinnedFingerprint?, changed?}`. `pin` = base64 SHA-256 of the public key (what `expectedPin` carries), `fingerprint` = the same hash as 32 colon-separated upper-case hex pairs for reading aloud, `chainValid` = also passes the normal chain and host name checks. Errors `400 notHttps`, `404`, `409 recovery`, `502 unreachable`. `POST .../{h}/TrustCertificate` takes `{expectedPin, acceptUntrusted?}`: `400 expectedPin`, `409 changedAgain` (body carries the new `pin`, `fingerprint`, `chainValid`), `409 untrusted` (Public server, chain not valid, no `acceptUntrusted`), `409 notPinned`, `409 notChanged`, `502 unreachable`, else `200 {trusted:true}`. `PUT .../{h}` with `tlsPinned:true` on an unpinned server takes the same `expectedPin` and `acceptUntrusted` | pin a key the admin has compared with the friend |
| H | `GET .../{h}/Overlap`; phase 4: `GET .../{h}/Speed`, `GET .../{h}/Telemetry`, `POST .../{h}/SpeedTest` | reports |
| both | `GET`/`PUT Shoal/Admin/Settings`; `GET Shoal/Admin/State`, `POST Shoal/Admin/State/Restore`, `POST Shoal/Admin/State/StartOver`, `POST Shoal/Admin/State/ConfirmRestore` `{}` (v2.2, UI§14.2 #12: clears `restoredFrom.needsConfirm` only, 3.7; `409` when nothing awaits confirmation); `DELETE Shoal/Admin/All` (`confirmName`; v2.4: deletes the Shoal libraries **before** it restores managed accounts, 5.6) | settings, recovery, danger zone |
| P | `GET`/`POST Shoal/Admin/Shares`, `GET`/`PUT`/`DELETE Shoal/Admin/Shares/{id}` (`DELETE` = revoke, needs `confirmName`), `POST .../{id}/Pause`, `POST .../{id}/Resume` | friends |
| P | `POST .../{id}/Invites`, `DELETE .../{id}/Invites/current`, `GET .../{id}/Approval`, `POST .../{id}/Approval` `{approve:true, sas}` or `{approve:false}` (v2.2; replies in 9.5 "Approval") | invite and approval |
| P | `DELETE .../{id}/Devices/{deviceHandle}` | remove one device |
| P | `GET`/`PUT Shoal/Admin/Hardening` `{mode}` | protection mode |

Rate-limited actions return 429 with `Retry-After` (6.9). No response contains a token, an invite after its one display, or a peer GUID (T-SEC-5, `docs/ui-spec.md` F5).

---

## 10. Fallback hook (contingency; the MVC-filter mechanism is proven)

The spike and the owner's browser test proved the primary mechanism on 12.1. The fallbacks remain documented for two cases: a future Jellyfin release changes MVC filter composition, or an extended route (6.1) is invisible to the filter. A CI smoke test against each new Jellyfin release asserts that the filter still fires for `PlaybackInfo` and `Videos/{id}/stream`.

**F1 (preferred fallback): an `IStartupFilter` path-rewrite middleware.**
- The registrator adds `services.AddTransient<IStartupFilter, ShoalStartupFilter>()`. This is feasible because plugin `RegisterServices` writes into the generic host's `IServiceCollection` (`Program.cs:172`, then `ApplicationHost.Init`, `:490-492`). Unverified at runtime; a 30-minute spike.
- The middleware runs **before** `UseBaseUrlRedirection` (`Startup.cs:173`). It strips the configured `BaseUrl`, matches the same route table with case-insensitive regexes, and parses GUID N and D forms.
- If the item is federated, it **rewrites `Request.Path`** to an internal plugin route (for example `{BaseUrl}/Shoal/Intercept/PlaybackInfo/{id}`), keeping the query and body.
- The rest of the pipeline then runs normally (`Startup.cs:177-238`), then our `[Authorize]` controllers, which still call CallerContext.

| Trade-offs of F1 | |
|---|---|
| Pro | Independent of MVC filter ordering. Our endpoints get real `[Authorize]` semantics. Handlers are reused unchanged. |
| Con | Undocumented plugin plumbing that depends on Jellyfin keeping the generic host. It must handle `BaseUrl` and GUID formats itself. Path regexes duplicate core route templates (CI diff). A bug here sees every request, so it must be tiny. |

**F2 (degraded, only if neither F1 nor the MVC filter works).**
- Switch the `.strm` sentinel to a non-http form, so the static source becomes an unplayable "strm" file source.
- Add an `IMediaSourceProvider` (S§3.1) that appends a source with `Protocol=Http`, `IsRemote=true`, `SupportsTranscoding=false`, and `Path=<scheme://host/BaseUrl from the request>/Shoal/Direct/{localItemId}?ApiKey=<caller token>`, built per request and never written to disk.
- jellyfin-web then raw-plays it, and our `[Authorize]` endpoint proxies Range.

| Trade-offs of F2 | |
|---|---|
| Loses | Peer transcoding and **any** transcoding. Clients that cannot direct-play the file fail. |
| Costs | A FullRefresh plus probe on every play (`MediaSourceManager.cs:184-194`). The absolute URL depends on correct forwarded headers behind reverse proxies. **v2.2: native clients are now known to break on it** (6.2.3 "Why `Protocol=File`": the official Android TV app drops the source, jellyfin-android plays it as HLS, Moonfin and Roku play it raw), so F2 would also fail D11. |
| Verdict | Demo quality. If both hooks fail, **stop and re-scope**: open an upstream Jellyfin PR for a supported "playback info interceptor" hook rather than ship F2. |

---

## 11. MVP scope, task breakdown and test plan

### 11.1 Cut line

**MVP-A (roadmap phase 3, Home role against a stock Peer; owner D7):** Kiro K1-K20 and Kiro 2 U1-U6. This is everything the owner needs to watch a friend's library when the friend installs nothing.

**MVP-B (Peer role, optional Shoal on the friend's side):** Kiro K21-K23 and Kiro 2 U7: shares, invite pairing, the guard filter. Required before the first public release.

**Phase 4:** Kiro 2 U8-U9 (speed tests, verdicts, auto-fit, telemetry, DERP detection, diagnostics UI).

Phase 5: rotation (K25, K26), two-way UX (K27), device matrix (v2.6.3). v2.x candidate (not scheduled): credential-less clients via grant-bound access (6.12 Option A), only after a Warden review. Phase 6: alternate versions (K28, K29), trickplay (K30), attachments and fonts (K31), image child process (K32), settings (K33). The progressive stream proxy is dropped (v2.6.8).

### 11.2 Tasks (owner D10: Kiro = backend core, Kiro 2 = admin UI and speed)

Rules for every task:
- One focused session; lands with its tests; builds with `TreatWarningsAsErrors` and the banned-API analyzer (8.8) on.
- `F-*` ids are fixtures in Appendix C; `T-*` ids are tests in 11.3. "Lab" means Pilot runs it on lab-server/lab-server with fakepeer.
- The task that replaces a spike pattern deletes it (Appendix B, "must not survive").
- Each K-task's first commit publishes its service interface, so Kiro 2 can build the admin layer against it in the other worktree.
- Warden reviews K3-K7, K13-K18 and K21-K23 before merge (endpoints, auth, tokens, streaming; `CLAUDE.md`).
- **K0 (in progress, Kiro):** rename to `Jellyfin.Plugin.Shoal` and `/Shoal/...`; no behavior change.
- Lab tooling chore for Pilot (W§B-13): secrets via stdin or env, `umask 077`, quoted heredocs.

**Kiro (backend core), in order:**

| ID | Task | Acceptance criteria |
|---|---|---|
| K1 | **StateStore and settings split** (3.2, 3.2a, 3.7): `state.json` schema, write protocol, `.bak`, dailies, recovery mode, read-only reconciliation report; XML `PluginConfiguration` v2 (toggles only) and `SettingsValidator`; delete the spike's `PeerConfiguration.Token` | Unit: round-trip. Simulated crash mid-write keeps the old state. Corrupt `state.json` → `.bak` used. Both corrupt → recovery mode, and the corrupt files are byte-identical afterwards. Newer schema → recovery. **T-REG-2**: core replaces the XML with defaults → peers, access modes and hardening mode intact. Out-of-range XML is clamped without throwing. Linux modes 0600/0700. |
| K2 | **SecretStore and Redact** (8.2) | **T-REG-4**: one flipped byte → Home refuses peers, key not regenerated. Two saves use different nonces. Env key → no `master.key` on disk. DPAPI scope `CurrentUser`. Network-filesystem warning. The XML and every admin JSON response contain no token (grep). Redact unit tests. |
| K3 | **PeerClient core** (6.10, 6.11): `BoundedReadStream`, `CallBudget`, handler settings, exact header builder, circuit breaker, banned read APIs | fakepeer: **F-RS-04** cut at the cap with flat memory; **F-RS-05** idle timeout; **F-RS-06** error; **F-RS-09** and any 3xx are errors with no second request; **F-RS-07** and **F-RS-08** ignored; 100 MB and 10k-deep JSON rejected; total deadline enforced; breaker opens after 3 failures and half-opens at 30 s; request headers exactly as 6.11 (part of **T-SEC-20**). The build fails if `ReadAsStringAsync` is used. |
| K4 | **AddressPolicy, trust window, TLS options** (4.4, 8.3 T11/T13) | Table-driven tests for every address in T11, including `100.100.100.200`, `fd00:ec2::254`, decimal, octal and hex IPv4, IPv4-mapped, NAT64, userinfo, backslash and non-punycode hosts. Public over http refused. Proxy mode accepts only Tailnet literals or `*.ts.net`. **T-SEC-8** rebinding blocked in direct mode. **T-PAIR-4**: zero token-bearing requests once the pin fails or is older than 120 s. SPKI pin mismatch → `TlsKeyChanged`. |
| K5 | **Retired (D17): Quick Connect pairing, Home side** (4.2, 4.3): session contract (9.5 #5), 1 s poll, replaced-request handling, check, `/Users/Me` checks, admin refusal with logout, "They don't match", server-derived DeviceId | **T-PAIR-1, -2, -3, -5, -6, -7.** The check is 4 Crockford characters from `RandomNumberGenerator`; the poll interval is measured at 1 s; "They don't match" logs out and discards the token; no token in logs at Debug, admin responses or the browser. **v2.2 (4.2a):** `pairing.phase` is persisted at start and at AwaitingConfirm, and a restart turns it into `PairingAborted(interrupted, phase)`; aborts carry their kind, and a `DisplayMessage` goes out for the five safety kinds only (lab: restart Home in each phase, and one abort of each kind); `DismissPairing` removes a never-paired record and restores a re-paired peer; a re-pair keeps the old secret until Confirmed. **v2.3:** `pairing.method` stored with `phase` and in `PairingAborted`; `likelySessionLimit` per 4.2a rules (a) and (b), never from message text, `expired` otherwise; the account reading refreshed once at Start on a re-pair (**T-PAIR-13** (i)). **v2.4:** rule (b) and the Start-time reading are still open (W6 L-1) and land before the Pilot lab run, with tests for (i1) and (i2); the abort stores `phase: afterApproval` (rule a) or `beforeApproval` (rule b) (4.2a, 9.5); W6 L-2 (re-pair uses the stored pin) and L-3 (refuse a probe whose host changed) as Warden specified. |
| K6 | **CallerContext, BannedSymbols, reflection tests, T-SEC-15 harness** (8.5, 8.7, 8.8) | Harness rows R1-R14 and P1-P4 green against a test endpoint backed by fakepeer, asserting zero fakepeer requests on every deny. Mutation check: removing step 4 or step 6 turns rows red. The analyzer rejects `GetAuthorizationInfo`. The reflection test allows exactly one `[AllowAnonymous]`. Real-route columns turn green in K13-K18. |
| K7 | **ResponseWriter** (8.6) | Unit tests for every row of the type table and the header set, including the v2.2 JSON-subtitle row. **F-RS-01, -02, -03** against a stub handler: emitted type from the table, never the peer's; non-media rows carry CSP and `attachment`. Subtitle body checks. The analyzer rejects direct `ContentType` writes elsewhere. |
| K8 | **MetadataSanitizer, PathSanitizer, NfoWriter** (5.5) | **T-SYNC-6** fuzz (10k hostile titles stay in the root, valid XML). **F-MD-04, F-MD-05** → canonical ids only. **F-MD-02, F-MD-03** stripped and truncated on rune boundaries. Unknown ratings dropped; **F-MD-06** flagged untrusted. Reserved names; case- and normalization-insensitive collisions. |
| K9 | **ImageFetcher and disk quota** (5.4, 5.10) | **F-CAP-04** (WebP) rejected; **F-CAP-05** rejected from headers without decoding; 50 MB file and HTML-as-image rejected (**T-SYNC-7**); serial fetching; **F-CAP-01** scaled down (quota 50 MB in the test) stops images at the quota with an alert while titles still import (**T-SYNC-10**). |
| K10 | **Library creation and VisibilityService** (5.6): locked options, access modes, Reconcile, user-created consumer, drift list, undo on removal | **T-VIS-1..6.** The created library shows the 5.6 options. A second Reconcile writes nothing. `BlockedMediaFolders` is never written. **v2.4:** the 5.6 v2.4 options (`TypeOptions` entries for `Movie`, `Video`, `Trailer`, `BoxSet` with empty fetcher lists; `DisabledMediaSegmentProviders`), both "every provider" lists recomputed at startup and after each sync; uninstall and `DELETE Shoal/Admin/All` per 5.6 "narrow, never widen" (W6 L-5, **T-VIS-8**, required before release); a share user with "all libraries" narrowed (W6 L-6, unit test); **T-SYNC-13** in the lab. |
| K11 | **Catalog sync** (5.1-5.3, 5.8-5.10) | **T-SYNC-1..5, -8, -11** (**F-CAP-02**), **-12** (**F-CAP-03**: 49,000 new titles held; Release imports them); only the Shoal library refreshes; partial-run rule; one global sync. |
| K12 | **ShoalIndex, StubEnricher, sentinel** (3.4, 3.5) | **T-PLAY-14**; a forced refresh keeps the duration; no `ProbeProvider` warnings; `.strm` holds only the sentinel (**T-SEC-6**); empty or invalid StubRoot → nothing federated (W§B-5); merged-version warning; stub items never carry stream titles. |
| K13 | **Intercept filter and route table** (6.1) | Local Big Buck Bunny plays unchanged; filter p99 < 0.1 ms on non-federated requests; `Download` and `File` 403; core HLS, attachments and legacy segment routes 404; non-static `stream` 404 plus counter; `LiveStreams/Open` with a federated `itemId` 400; exceptions never reach `ExceptionMiddleware` (log has no peer host); **T-SEC-15** static and core-subtitle columns green with stub handlers. **v2.2:** the `Audio/...` row 404 (G5) and `stream.` 404 (**T-CLI-5**, **T-CLI-6**); a federated `itemId` query on the subtitle route is treated as federated; credential-less static and subtitle requests 401 (**T-CLI-1**); no Shoal log line contains a client query string (**T-CLI-14**). **v2.3:** one id, the route id: a differing `itemId` → 404 with zero fakepeer requests (W4-3, **T-SEC-15** R16); `Request.Query` only, every `itemId` value checked, `Guid.TryParse` with B/P/X, upper-case and padded fixtures on every 6.1 route (W4-12); the banned symbols of 8.8 including `QueryString`, `Path`, `GetDisplayUrl` (W4-14); `ActiveEncodings` per 6.1 (W4-4, **T-SEC-25**); index update on `ItemAdded` and the path-rule fallback on the five stub-reading routes; **T-CLI-6** anonymous and download-permitted variants. **v2.4 (Scout-03, Warden-06):** `ReadsStub` on every row except PlaybackInfo, `ActiveEncodings` and `LiveStreams/Open` (3.5, SR§4.1); the coverage test over the real action descriptors of the targeted Jellyfin.Api, never swagger, with the allow-list of 6.1 (fails on any new byte route); the spike controller, `ShoalItemResolver`, `PeerRequestAllowlist`, `PlaylistRewriter` and `PlaybackInfoRewriter` deleted (W6 L-7). |
| K14 | **PlaybackInfo handler and grants** (6.2, 3.5) | **T-SEC-20** body equals 6.2.1; DeviceProfile `Name` is "Shoal"; **F-MD-01..03** in `MediaStreams` sanitized or dropped and `DisplayTitle` recomputed; absolute `DeliveryUrl` streams removed; **T-PLAY-13**; foreign `MediaSourceId` 400; **T-SEC-21**; grant caps (global refusal); **T-SEC-15** PlaybackInfo column. **v2.2:** query/body merge per 6.2 step 2 incl. differing values (query wins), `UserId` ignored, the capabilities-profile fallback (**T-CLI-2**); `Protocol=File`, `IsRemote=false`, `Path=null`, `SupportsDirectStream=false` and a non-empty `Container` on every answer whose peer or catalog container is valid (G6, G7); grants keyed by (user, item, DeviceId), slot-free replacement, cap 8 with slot-free eviction, PlaybackInfo bucket 30/min burst 15 (**T-CLI-8**); bitmap `External` subtitle profiles removed from the forwarded profile (**T-CLI-15**); **T-CLI-9**, **-10**, **-11**. **v2.3:** `DeviceId` capped at 128 printable ASCII characters (else hashed) and never treated as identity; capabilities fallback only for the caller's own device row (W4-2); `SettingsValidator` clamps `MaxStreamsPerUser` to **1..4** (was 1..10); capacity order replace, own-evict, global; no slot-free grant → refuse, never evict a slotted one; `hasSlot` per 3.5 (W4-6); `External` profiles kept only for the eight served formats (W4-10); **T-CLI-8** extended. |
| K15 | **Static stream proxy, slots and limits** (6.3, 6.9) | 206 with byte-identical ranges; multi-range 416; **F-RS-01/02** on static → Shoal's type; **F-RS-06** error; **T-SEC-17**; static rate limit; slot released 2 min after the last byte; lapsed trust window → 503; **T-SEC-15** static columns. **v2.2:** Moonfin-Core, Roku, Smart-TV and lowercase `static=true` shapes (**T-CLI-2**, **T-CLI-5**); Content-Type from the catalog container, also for comma lists; no credentials → 401 (**T-CLI-1**). **v2.3:** a direct-play slot is never released by `ActiveEncodings` (W4-4, **T-SEC-25**); two devices of one user on one item share the static slot and both grants count as holding it (W4-6). **v2.4 (Pilot-03, Scout-03):** `ISlotLedger` with stream and conversion slots (6.9), `maxConcurrentTranscodes` per peer (default 4 per D15, clamped 1..`maxConcurrentStreams`); the PlaybackInfo pre-checks of 6.2 steps 1 and 6 through the seam; a federated `mediaSourceId` that differs from the route id → 404 with zero fakepeer requests (6.1, SR§4.2); **T-CONC-3**. Before this PR writes responses, W6 M-2's analyzer bans fire (compile-based test). |
| K16 | **PlaylistGrammar** (6.4), a pure library | One test per rule (I1-I7, tag table, SI, MD, U-M, U-V, U-S, O1-O4). **F-PL-01..22** each yield 502 or a dropped line, and no output contains a hostile substring. Real 12.1 master, main and subtitle playlists from the spike produce the expected output exactly. Comma-decimal `EXTINF`. A test scans the patterns for `IgnoreCase`, `\d`, `\w`. |
| K17 | **HLS proxy controller and grant lifecycle** (6.4, 6.11) | **T-PLAY-2, -7, -8, T-SEC-2, T-SEC-14**; a capture shows no connection to any host in F-PL lines served through a live grant; **T-SEC-15** HLS columns including R13 and R14 (access removed → 404 within 5 min); 12 h absolute expiry. **v2.2 (G2):** the client-query rules of 6.4: Moonfin's mutated master URL and Smart-TV's `upmix=true` → 200, the Peer request unchanged, `clientIndexMismatch` counted, only U-V/U-S values can yield 400 (**T-CLI-3**). **v2.3:** duplicate `ApiKey` and `api_key` stay core's 401 (W4-11); a repeated tick key → 400; `clientIndexMismatch` admin-only, no value labels; `peerJobStarted` set on the first forwarded segment, and the HLS slot released only after the scoped delete was sent (W4-4); query read per 6.1 (W4-12). **v2.4:** the first forwarded segment takes a stream slot and a conversion slot together (6.9) and both are released together; 429 when either is full. |
| K18 | **Subtitles** (6.6, 6.6a) | **T-PLAY-3**; external srt via `/Shoal/Subtitles` with Shoal's type; **F-RS-01** on subtitle routes → 502 or neutralized; body checks; HLS subtitle names come from local sanitized streams; kill switch; **T-SEC-15** subtitle columns. **v2.2 (G3, G4):** embedded text tracks served as files in both route forms; `js`/`json` rebuilt per 6.6a step 9 (fixtures: extra members, depth 9, 20,001 events, `Text` with `<script>`, `<img onerror>`, bidi overrides and NUL, ticks out of range: each dropped, stripped or 502, never echoed); `sup`/`pgssub` 404 with zero fakepeer requests; route values only (obsolete query overrides ignored); ticks and the three query values mapped, nothing else forwarded (**T-SEC-20**); concurrent identical extractions make one Peer call; the 120 s header deadline (**T-CLI-4**). **v2.3 (Warden-04; K18 GO):** 6.6a step 0 (route id only, R16); step 7 as written: `PeerCallProfile.SubtitleFile` with `CountsTowardBreaker=false` (also `SubtitleSegment`), gate of 2 per peer with 16 waiters and 429 beyond, item lanes, one open lane per user, detached abort with the 10 s rule, the six coalescing rules (unit tests for each: joiners never see a partial buffer, a failed call is retried, a different peer or media source never joins) (**T-CLI-16**, **T-CLI-4** additions); `Utf8JsonReader` walk with 502 at event 20,001 and the `Text` fuzz property (W4-8); VTT/SRT rebuild per step 9a with fixtures for `STYLE` with `url()`, `REGION`, `NOTE`, cue settings, `<script>` and `<font>` in cue text, bad timings, 20,001 cues (W4-9); source-codec allowlist (W4-10). |
| K19 | **PeerSessionReporter and scoped kill** (6.8) | **T-PLAY-7, -8**; local stop kills the peer job within 5 s; the builder throws on an empty PlaySessionId; **T-SEC-20** session bodies exact. **v2.2:** Moonfin's reporting sequence including the `?ApiKey=` exit beacon (**T-CLI-7**). **v2.4 (Pilot-03):** on `PlaybackStopped`, `Sessions/Playing/Stopped` and then, for a `Transcode` grant with `peerJobStarted` still set, the scoped `ActiveEncodings` delete (once per job; never with an empty PlaySessionId), then both slots released (6.8); **T-CONC-2**: the stopped stream's Peer job is gone within 5 s and the other streams keep playing with zero errors. |
| K20 | **HealthMonitor, offline behavior, foreign-device watch** (4.3, 4.4, 5.7) | **T-PLAY-12, -16, T-NET-3..5, T-SEC-13**; Play fails in < 1 s with the circuit open; at most one DisplayMessage per user, peer and 5 min; fakepeer `/Sessions` with another device for the share user → alert, other sessions discarded unlogged. **v2.3:** the anchor is sent at startup, on the first good contact after the circuit closes or the trust window lapses, on Resume and while paused (W4-5, **T-SEC-22** (d) timed); `/Sessions` with two share-user sessions on Home's own DeviceId under different client names → alert (W4-13). |
| *MVP-A cut* | Warden review; Pilot runs 11.3 (MVP-A rows) | |
| K21 | **ShareService (Peer)** (4.1b, 4.5): share user with full policy, lock, device sweep, drift repair, pause and resume, revoke, StubRoot libraries refused | **T-SEC-16** (Shoal Peer part: a minted device is signed out within 60 s); revoke leaves zero devices and Home gets 401; resume revives only Home's device; a dashboard edit is repaired and logged; a StubRoot library is refused. |
| K22 | **Invite pairing** (4.1b, 4.2): `PairController`, invites, approval with SAS, Home redeem path | **T-PAIR-8, -9, -10**; the token is delivered once; an uncollected token is logged out after 2 min; the reflection test pins the single anonymous action. **v2.2:** `GET Approval` never contains the code; `{approve:true, sas}` answers `{match:false, triesLeft}` on a miss; the third miss sets `cancelledWrongCode`, Home's poll then reads `codeMismatch` (distinct from `denied`) and Home ends as `PairingAborted(codeMismatch)`; other invites untouched (**T-PAIR-8** extended). |
| K23 | **PeerGuardFilter** (8.4): jobs a-d, route enumeration with a CI diff, fail-open, 60 s cache, modes including `Off` | **T-SEC-12, T-PAIR-11**, **T-SEC-16** (Authorize by a share user → 403); images untouched in `Enforce`; the third concurrent transcode → 429 → Home `RateLimitExceeded`; an injected exception → `next()` plus a counter; the CI diff test fails on a doctored route table. |

**Kiro 2 (admin UI and speed), in order** (builds per `docs/ui-spec.md`; Lumen reviews with its section 15 checklist):

| ID | Task | Depends on | Acceptance criteria |
|---|---|---|---|
| U1 | Page shell, tokens and components (`docs/ui-spec.md` 1-7) with mock JSON for every 9.5 data shape | none | ui-spec checklist A, C, F1-F2; **T-SEC-11** with mock data |
| U2 | `AdminController` Home endpoints (9.6) over Kiro's services; Overview including the desaturated offline card (owner D5) and "Friends watching from you" at the bottom (owner D6) | K1, K5, K11, K20 (stub until landed) | ui-spec F5: an automated scan of every admin JSON response finds no token, invite or peer GUID; cooldowns from `Retry-After`; ui-spec D and E items for Overview; **v2.2:** `DismissPairing` and `State/ConfirmRestore` routes (9.6), `confirmName` enforced on the server for every destructive call |
| U3 | Pair a server, watch direction (9.3; v2.8: five steps, invite only, the QC states are retired) | K5, K22 | every 8.2.5 state reachable with fakepeer, including `requestReplaced` and (v2.2) `codeMismatch` and both `interrupted` phases; safety kinds show the shield card and a pop-up, stopped kinds neither pop-up nor shield; ui-spec D4 ("They don't match" saves nothing, checked through the API); **T-PAIR-7** |
| U4 | Libraries & access: access switch, picker, restricted acknowledgement, drift list, confirm dialogs | K10 | **T-VIS-1..6** driven through the UI; picker notes per 9.5 #3 |
| U5 | Server detail (non-speed) and Overlap report | K11, K20 | every alert in 9.3 reachable with fakepeer; remove dialog effects verified through the API. **v2.4:** "Conversions at once" (1..streams) read and written through `Peers/{h}`, conversions running now shown; the 9.5 v2.4 names (`contentDeletion`, `contentDownloading`, `sessionLimit` as its own alert, `catalogUnreadable`, `likelySessionLimit` copy by `pairingPhase`) |
| U6 | Settings, "Protect your shared videos" (`Off` under Troubleshooting), recovery and secrets banners, danger zone | K1, K2, K23 (stub until landed) | recovery mode reachable by corrupting the state in the lab; mode changes are immediate and confirmed |
| *MVP-A cut* | Lumen design review, Warden review of the admin API | | |
| U7 | Share direction and Friend detail: invite display, approval card with safety code, devices, pause and resume, stop sharing | K21, K22 | **T-PAIR-8** through the UI on both servers; unexpected device shown and signed out; (v2.2) the approval card's code input enables Approve at 8 valid characters, shows `triesLeft`, and the page never receives the code (checked in the HAR) |
| U8 | Phase 4: `SpeedTestService`, history, `VerdictEngine`, Test now (7.1, 7.2) | K3 | Under netem `rate 8mbit delay 80ms` the UI shows 7-8 Mbps and about 160 ms RTT with verdicts "720p smooth / 1080p will buffer, auto-fit transcodes to about 5 Mbps / remux will buffer" |
| U9 | Phase 4: auto-fit (link budget into K14's `MaxStreamingBitrate`), passive telemetry, hints, DERP heuristic, optional LocalAPI, speed chart (ui-spec 9) | U8, K14 | At 8 Mbit a 1080p remux-class title transcodes on the Peer to ≤ 5.3 Mbps without stalls; each hint fires under its T-NET profile. **v2.4:** playback samples carry `conversionsActive`; the slow-conversion hint names the setting when it is 2 or more; the deep test is skipped while Home streams from that peer and shows its suggested "Conversions at once" (7.1, 7.3) |

**Phase 5 and 6 (v2.6).** Lane A = Kiro, lane B = Kiro 2. Each task's first commit publishes its interface. Warden batch A = K25 + K26; batch B = K29, K30, K31, K32. Lumen reviews K27 and K33 before merge.

| ID | Lane | Task | Depends on | Shared files touched | Acceptance criteria |
|---|---|---|---|---|---|
| K24 | A | **v2.6 foundation, no behavior change** (v2.6.9): catalog schema 3 and migration, additive state fields, new XML settings with clamps, route-table rows and handler seams, `PeerExtractionGate` extracted from `SubtitleHandler`, `IAttachmentRebuilder` seam, call profiles, content-type table rows, counters split plus `Status.intercept`, `IImageSanitizer` pass-through, registration partials, test-fake extension points | none | all "K24 only" files of v2.6.9 | Every existing test passes unchanged with defaults. T-ALT-1 (schema 2 to 3 round trip, newer schema goes to recovery, nothing renamed). New settings out of range are clamped without throwing (T-REG-2 extended). The 6.1 route-coverage test is green with the new rows; default handlers answer 404 and count. T-CLI-16 unit tests green on the extracted gate. `bypass-route` becomes `nonstatic-stream` and `audio-route` (unit test); `GET Status` returns `intercept {sinceUtc, counters[]}` with no URL, id or user agent. Analyzer clean. |
| K25 | A | **Rotate, Peer** (v2.6.1): `POST /Shoal/Share/Rotate`, `IssueTokenAsync` refactor shared with Approve, rate limits, `homeServerIdPrefix` stored at Approve, share record fields | K24 | `Sharing/ShareService.cs`; new `Api/ShareRotateController.cs` | T-ROT-1 (Peer part), T-ROT-2, T-ROT-3, T-ROT-4, T-ROT-8; T-PAIR-8 still green after the refactor; the reflection test still finds exactly one `[AllowAnonymous]`; response has no token in logs (grep). Warden batch A. |
| K26 | A | **Rotate, Home** (v2.6.1): `TokenRotation`, `Rotating` state, `PeerCallProfile.Rotate`, `SecretStore` replace, `rotationDays` schedule, `POST Peers/{h}/Rotate`, `rotation` on peer GET | K24, K25 contract (fakepeer until K25 lands) | new `Peers/TokenRotation.cs`, `Api/Admin/AdminRotateController.cs`; reads `PeerRegistry`, `StreamSlots` | T-ROT-1 (Home part, on the real Peer with P-HTTPS), T-ROT-5, T-ROT-6, T-ROT-7; admin JSON scan finds no token (T-SEC-5); a rotation is refused while a stream slot is held; a lost response ends as `AuthFailed` plus `rotationError: lostResponse`. Warden batch A. |
| K27 | B | **Two-way UX, rotation UI, counters panel** (v2.6.2, v2.6.1, v2.6.3): `friends[]` and roll-up, `shareable`, `alsoWatchesYou`, Overview, Server detail and Friend detail panels, Rotate button and schedule picker with cooldown and error kinds, alerts `rotationFailed`, `rotationOverdue`, `rotationStoreFailed`, counters under Settings, Troubleshooting | K24 DTOs; K26 endpoint | `AdminStatusBuilder.cs`, `AdminController.cs`, `AdminSharesController.cs`, `AdminSettingsController.cs`, `shoal.html/js/css` | T-TWO-1, T-TWO-2, T-TWO-4 unit; T-TWO-3 in the lab; ui-spec checklist (peer strings as `textContent`, HAR shows no token, code or peer GUID); Rotate shows a `Retry-After` cooldown; Lumen review. |
| K28 | A | **Alternate versions, sync side** (v2.6.4): source selector, per-source stubs and labels, rebind rule, `ShoalIndex` version sets, `IVersionResolver` implementation, `StubEnricher` per stub, guards count titles | K24, T-ALT-0 passed | `CatalogSyncService.cs`, `StubWriter.cs`, `PathSanitizer.cs`, `StubEnricher.cs`, `ShoalIndex.cs`; new `Sync/SourceSelector.cs` | T-ALT-1 to T-ALT-5 (with a fakepeer item with 1, 4, 5 and 64 sources); the primary's path and stubKey survive a rebind; switch off removes extras; no peer text in any file name. |
| K29 | A | **Alternate versions, playback side** (v2.6.4): grants carry the version, slots keyed by `setId`, PlaybackInfo, static, subtitle, HLS and the seams resolve versions; canary covers all versions | K28 | `PlaybackInfoHandler.cs`, `PlaybackInfoBuilder.cs`, `StaticStreamHandler.cs`, `SubtitleHandler.cs`, `Grants.cs`, `StreamSlots.cs` | T-ALT-6, T-ALT-7, T-ALT-8; T-SEC-20 body equals 6.2.1 for each version; a cross-set `mediaSourceId` is 404 with zero fakepeer requests on every route; two versions of one title hold one slot; S10 on web and iOS. Warden batch B. |
| K30 | B | **Trickplay** (v2.6.5): metadata sanitizer and `TrickplayInfoWriter`, `ITrickplayHandler`, disk cache with LRU, gate and coalescing, `tiles.m3u8` 404 counted | K24, T-TRK-0 passed | new `Playback/TrickplayHandler.cs`, `Playback/TrickplayCache.cs`, `Sync/TrickplaySanitizer.cs`, `Sync/TrickplayInfoWriter.cs` | T-TRK-1 to T-TRK-8; cache never exceeds the cap; no peer id or token in any file name; every hostile body rejected with no cache file; S11. Warden batch B. |
| K31 | B | **Attachments and fonts** (v2.6.6): attachment rebuild in PlaybackInfo, `IAttachmentHandler`, font magic table, shared extraction gate, kill switch | K24 (K29 only for version sets) | new `Playback/AttachmentRebuilder.cs`, `Playback/AttachmentHandler.cs` | T-ATT-1 to T-ATT-7; 20 parallel font requests make one Peer call; a non-font attachment is never listed; the served type comes from the magic, never the peer; S12. Warden batch B. |
| K32 | B | **Image re-encode child process** (v2.6.7): `ChildImageSanitizer`, limits, self-test, `ImageFetcher` hook, Status fields | K24 | `Sync/ImageFetcher.cs`; new `Sync/ChildImageSanitizer.cs` | T-IMG-1 to T-IMG-7; hostile images never raise the plugin process memory; a stalled child is killed at 8 s; ffmpeg missing gives `unavailable` and sync continues as in v1. Warden batch B. |
| K33 | B | **Settings rows and alerts for K28 to K32**: switches (`AlternateVersionsEnabled`, `TrickplayEnabled`, `TrickplayCacheGb`, `AttachmentsEnabled`, `ReencodeImages`), `imageSandbox` state and limits, trickplay cache size and "Clear" | K27, K28 to K32 | `AdminSettingsController.cs`, `shoal.html/js/css` | each setting round-trips through `Shoal/Admin/Settings` and is clamped; `unavailable` and `partial` states render; no secret in the HAR; Lumen review. |

### 11.3 Test plan for Pilot (lab only: lab-server and lab-server on the lab host, see `lab/README.md`; never the production server)

**Fixtures to build:**
- `lab/fakepeer.py`: a scripted hostile Peer serving the 5.1, 6.x, Quick Connect, `/Sessions` and `/Shoal/Pair` endpoints with chosen payloads. It **records every request byte** (headers, query, body) for T-SEC-20 and for T-SEC-15's "zero requests" assertions.
- The Appendix C fixtures as files under `lab/fixtures/`.
- A netem helper for lab-server's veth, a HAR or mitmproxy capture of client traffic, tcpdump for "no connection to the named host", and a log grep script for the token, peer GUIDs and peer host.
- The spike's `tools/spike-setup.sh` and `tools/spike-verify.sh`, extended, as the API-level regression suite (after the B-13 hygiene chore).

**Browser and native-client cases** need someone signed in to the lab Jellyfin. Agents must not type lab credentials into a non-localhost page. The owner signs in once in the browser pane or on the device and Pilot drives from there, or the owner runs those cases while Pilot records results.

| ID | Case | Expected |
|---|---|---|
| T-D17-1 | No outbound Quick Connect call in any pairing flow; starting a pairing with method `qc` | fakepeer records no request to `/QuickConnect/*`; `409 methodUnavailable` |
| T-D17-2 | An old `state.json` and `health.json` with a `qc` pairing, a pending `qc` pairing and legacy abort kinds | They load without throwing; the legacy peer is paused with "needs pairing again" and keeps its libraries and catalog; the pending record is cleared silently |
| T-D17-3 | Re-pair of a legacy QC peer by invite (also with changed libraries) | The credential and the pairing method become `invite`, libraries and mappings are kept, the old token is logged out (best effort), no session anchor runs, no device lock or session limit is read |
| T-D17-4 | The ping outcomes | `200 {"shoal":true}` gives `hasShoal` true; 404, another status, another body or a transport error gives false; 429 gives `shoalPingThrottled` and never blocks |
| T-D17-5 | Reflection over the controllers | Exactly two `[AllowAnonymous]` actions: `Pair` and `Ping` |
| T-D17-6 | Source grep gate | Fails when "QuickConnect" appears outside the allowlist (the guard ban on `Authorize`, redaction patterns, legacy readers, enum members) |
| T-D18-1 | A new install, and an existing install | New: the guard starts in `Enforce`. Existing: the mode is unchanged and the page shows the one click "Require sign-in" card with the 7 day log result |
| T-PAIR-1 (retired, D17) | Quick Connect happy path for a share user | token stored encrypted; `/Users/Me` non-admin; libraries listed |
| T-PAIR-2 (retired, D17) | Code expires (10 min) or is wrong | clean "expired" state; no token |
| T-PAIR-3 (ported to the invite path, D17) | Code approved for `labadmin` | refused, Peer device logged out, nothing stored |
| T-PAIR-4 | Peer reset (new server Id) behind the same URL | `IdentityMismatch`; zero token-bearing requests after the failed pin |
| T-PAIR-5 (retired, D17) | Quick Connect disabled on the Peer | clear error with the fix |
| T-PAIR-6 (retired, D17) | fakepeer answers `Connect` with 404 at t = 20 s (request replaced) | one redeem attempt, then `PairingAborted(requestReplaced)`; nothing stored. Variant where the redeem succeeds: paired. |
| T-PAIR-7 | Attacker device name `Shoal: <img src=x onerror=alert(1)>` and peer server name with markup | literal text everywhere; no script runs |
| T-PAIR-8 | Invite happy path; Deny; wrong safety code typed once, then three times (v2.2) | the Peer page never receives the code (HAR); the typed code matching Home's display approves; Deny → Home `denied`; one miss → `{match:false, triesLeft:2}` and the request stays pending; three misses → `cancelledWrongCode` on the Peer, Home `codeMismatch`, nothing stored, no token issued, other invites still valid |
| T-PAIR-9 | Invite abuse: wrong, expired, reused and nonce-mismatched invites; 2 KB body; no active invite | identical `404 {}`; failures only slowed (429 with `Retry-After` past 10 per minute per IP), **no invite is ever cancelled** (v2.1, W2§2d) and the admin notice counts them; 413 for the body; fast 404 with no invite; a valid invite still redeems while the failure bucket is tripped |
| T-PAIR-10 | Invite whose server-id prefix differs from the probed server | Home never sends the secret (fakepeer records nothing) |
| T-PAIR-11 (retired, D17: the shield is removed) | Shoal Peer: 5,000 `QuickConnect/Initiate` from one IP | 429 after 10 per minute; total cap 300 per 10 min (v2.1, 4.3) |
| T-PAIR-12 (retired, D17) | Stock Peer device lock: a second persistent device, then a self-minted device registered as non-persistent | the lock logs out the persistent one; the non-persistent one survives the lock (documented limit, 4.1) and dies when the user is disabled |
| T-PAIR-13 (retired, D17) | **Poll classification** (4.2a; W2§N-10 (4)). Owner: Pilot with fakepeer. Gates K5. Setup: fakepeer scripted per case, Home pairing by Quick Connect. Cases: (a) `QuickConnect/Enabled` false; (b) first poll answers 404, then a 302, then an HTML 200 (wrong base path or proxy page); (c) after a good baseline, 401 mid-flight; (d) after a good baseline, 3 timeouts in a row, then a good poll; (e) 30 s of timeouts; (f) a 200 with another `Code`; (g) a 404 after a good baseline, with the one redeem attempt answered 401, then a variant answered with a token; (h) the local deadline at 9 min 30 s; **(i) v2.3, session limit inferred (4.2a):** (i1) a re-pair where the old token's `/Users/Me` reads `MaxActiveSessions=1` and fakepeer never approves; (i2) the same on a first pairing; (i3) `Authenticated:true`, then the redeem answered 403; (i4) on lab-server (stock, limit 1, the friend signed in), the friend's `Authorize` refused: record Home's polls until the deadline | (a) `quickConnectOff`, **no Initiate sent**; (b) `quickConnectUnreachable` each time, **never** `requestReplaced`, code never shown; (c) `quickConnectOff`, no redeem; (d) `peerSilent` set after the third miss, cleared by the good poll, no abort; (e) `network`; (f) `badPeerResponse`, no redeem; (g) exactly one `AuthenticateWithQuickConnect`, then `requestReplaced`, and in the variant AwaitingConfirm; (h) `expired`; (i1) `likelySessionLimit`; (i2) `expired`; (i3) `likelySessionLimit`; (i4) every poll reads `Authenticated:false` (confirms the 4.2a derivation; if it reads otherwise, 4.2a changes). No outcome depends on any message text. fakepeer's log shows sequential polls at 1 s, never two in flight |
| T-PAIR-14 (QC parts retired, D17; token, id and header encoding parts stay) | **Validation and secrecy of pairing values** (4.2a; W2§N-10 (5), (6); W2§N-16). Owner: unit tests for the validators and header encoder, Pilot for the end-to-end and log parts. Gates K5 (header encoder also K3). Setup: fakepeer returns, one per run: `Code` `12345a` and `1234567`; `Secret` of 63 hex, of 64 with a `g`, and a real upper-case 64-hex value; a token of 33 hex, with upper case, with `"` or `,`; `User.Id` differing from `/Users/Me`; `ServerId` differing from the pin. Home server name `A+B 100% "x", y`. Debug logging on | every malformed value → `badPeerResponse` with the fixed message, and the value appears in no log line, exception, admin response or page; the upper-case secret is accepted; the header reaches fakepeer so that core's `WebUtility.UrlDecode` gives back `A+B 100% x y` exactly (quotes and comma removed by sanitizing, `+` and `%` intact); a grep of all logs at Debug finds no poll URL, `secret=` value, code or token |
| T-PAIR-15 (kept for the invite path, D17) | **One session, one click** (4.2a; W2§N-10 (7)). Owner: Pilot with fakepeer (Initiate counter). Gates K5, U3. Steps: start a pairing; then a second `POST Shoal/Admin/Pairing`; reload the page 10 times, navigate away and back, re-run Probe, restart Home; `PUT Peers/{h}` with a new URL during the session; reuse a probe handle, and use one older than 10 minutes; start 6 pairings within 10 minutes for one peer | exactly one Initiate per admin click (reloads, Probe, navigation and the restart send none); second start `409 {error:"sessionActive"}`; the `PUT` → 409; reused or old probe → `409 {error:"probeExpired"}`; the sixth start → 429 with `Retry-After`, enforced by the server (the same call from curl is refused) |
| T-PAIR-16 (durable abort kept; the confirm gate is retired, D17) | **Confirm gate and durable abort** (4.2a; W2§N-10 (2), (3); v2.2 kinds). Owner: Pilot with fakepeer; the file checks are scripted. Gates K5, U2, U3. Steps: (a) `Pairing/Confirm` in every state other than AwaitingConfirm; (b) reach AwaitingConfirm and inspect `secrets.enc`, `state.json` and the library list; (c) `Confirm {match:false}`; (d) wait 15 minutes in AwaitingConfirm; (e) restart Home before approval, then again in AwaitingConfirm; (f) after each abort, reload the page and restart Home; (g) one abort of each kind; (h) `DismissPairing` on a first pairing and on a re-pair; (i) "Start again" after `requestReplaced` and after `codesDidNotMatch` | (a) 409 each time; (b) no secret entry, no library, no sync, peer shows `Pairing`; (c) one `POST /Sessions/Logout` with the token to fakepeer, `codesDidNotMatch`, nothing stored; (d) Logout sent, `expired`; (e) `interrupted` with `phase` `beforeApproval` then `awaitingConfirm`; (f) the abort card and kind survive both; (g) a `DisplayMessage` to every admin session for the five safety kinds and **none** for the stopped kinds; (h) first pairing: the record is gone; re-pair: the peer is back to its previous state and secret; (i) the next attempt requires the new-account confirmation |
| T-PAIR-17 | **Invite limiter without an oracle** (4.1b Lookup and Limits, 8.4 (b); W2§N-4). Owner: Pilot, both servers running Shoal. Gates K22, K23. Steps: (a) 1,000 requests each with no active invite, with an active invite and a wrong secret, and with an expired one, timing each; (b) 20 wrong invites from IP A within a minute, then the valid invite from IP A; (c) 120 failures per minute from 15 addresses; (d) lab-server behind a reverse proxy without Known Proxies, failures from two clients | (a) byte-identical `404 {}` bodies and headers in all three; the medians differ by less than 1 ms and the distributions overlap (no expired-versus-wrong-versus-none oracle); (b) the wrong ones get 429 after 10, the valid one still redeems; (c) the global bucket trips at 100 per minute, valid invites unaffected, no invite destroyed; the admin notice shows the count; (d) both clients share one bucket and the Hardening screen shows the proxy warning (`proxySuspected`) |
| T-PAIR-18 | **Commit-reveal and transport rule** (4.1b, 4.2 step 1; W2§N-5). Owner: unit tests for the SAS derivation, Pilot for the rest. Gates K22, U3, U7. Steps: (a) record Home's first `POST /Shoal/Pair` and its polls; (b) a poll whose `homeNonce` does not hash to the commitment; (c) the valid secret presented with a different commitment; (d) an invite entered for a LAN peer over plain http, and for a Public peer over http; (e) the SAS derivation against a fixed unit-test vector on both sides, and end to end: type Home's displayed code on the Peer; (f) the Peer's `GET Approval` and page HAR | (a) the first request has `homeNonceCommit` and **no** `homeNonce`; polls carry it; (b) and (c) the same `404 {}`, counted as failures, the binding unchanged; (d) refused before any request is sent ("use Quick Connect instead"), fakepeer records nothing; (e) identical; (f) the code appears nowhere on the Peer side |
| T-SYNC-1 | Initial sync | 2 movies imported; BBB counted as duplicate |
| T-SYNC-2 | Metadata edit on the Peer | NFO updated; local item id unchanged |
| T-SYNC-3 | Rename or move a folder on the Peer | same stub, same local id, watch state kept |
| T-SYNC-4 | Delete one movie on the Peer | stub removed after the next sync |
| T-SYNC-5 | Unmount or empty the Peer media | removals held, alert shown, nothing deleted |
| T-SYNC-6 | Hostile titles: `../../etc/x`, `CON`, 1 MB title, RTL override, `</title><thumb>http://evil</thumb>`, NUL bytes | files stay inside the root; valid NFO; no URL elements |
| T-SYNC-7 | Hostile images: 50 MB, HTML served as image, **F-CAP-04** WebP, **F-CAP-05** 8000 × 8000 PNG | rejected; placeholder shown; the PNG is rejected without decoding |
| T-SYNC-8 | Hostile JSON: 100 MB, depth 10k, 1M items, unknown enums, **F-RS-03** | sync fails or skips gracefully; Home memory stays flat; budget respected |
| T-SYNC-9 | Park and unpark (if enabled), watch state before and after | decides whether parking ships |
| T-SYNC-10 | **F-CAP-01** (quota lowered for the test) | images stop at the quota; titles import; alert |
| T-SYNC-11 | **F-CAP-02**: 15 unique actors per title across many titles | at most 5 actors per title; the 20,000-name cap holds; alert |
| T-SYNC-12 | **F-CAP-03**: 49,000 new titles in one sync | all held; Release imports them; the first sync is exempt |
| T-SYNC-13 | **No remote metadata for stubs** (5.6 v2.4; KQ 3, 4; SR§4.6, §5). Owner: Pilot on lab-server at Debug with Intro Skipper installed (T-CLI-12 mirror). Gates K10. Steps: create a Shoal library, sync 20 titles, full scan; "Refresh metadata, replace all" on one stub; run the media segment task; read the library options back; install a subtitle or segment provider plugin, restart, read them again | options read back as 5.6 (four `TypeOptions` entries with empty lists, both provider lists complete, and after the restart including the new plugin); no request to TMDb, OMDb, TVDb or fanart for Movie items; NFO values shown; no segment provider touches a stub. **Recorded, not failing:** Person items created and TMDb person lookups (accepted residual, 5.6) |
| T-VIS-1 | New library | only the pairing admin can browse and play; another admin and an "all libraries" user get 404 and do not see it; their other libraries are unchanged |
| T-VIS-2 | Switch to Everyone | every non-restricted account browses and plays; a restricted profile is excluded unless `includeRestricted` |
| T-VIS-3 | Everyone mode; create users through the dashboard, one with an explicit list and one with "all libraries" | both browse and play within 60 s without any Shoal action |
| T-VIS-4 | Switch back to Selected | the previous selection is restored exactly; users created meanwhile lose access; managed accounts regain "all libraries" when nothing is hidden from them |
| T-VIS-5 | Managed account: add a new local library; then an admin edits that account's libraries in the dashboard | the new library appears for it after the scan; after the edit Shoal stops managing it and logs that |
| T-VIS-6 | **F-MD-06** with a restricted profile | cannot be selected without the acknowledgement; excluded from Everyone |
| T-VIS-7 | **Share users never admitted** (5.6 "Share users are never admitted"; W2§N-2; alias **T-SEC-23**). Owner: Pilot, plus a unit test of `Admitted()`. Gates K10, K6 (matrix row R15), U4. Setup: lab-server runs both roles: it has a Shoal share for a friend X (share user `shoal-x-....`) and a federated library from lab-server. Steps: (a) set the library to `Everyone`, run Reconcile; (b) with X's share-user token, call PlaybackInfo, static `stream` and `Shoal/Subtitles` for a federated item, and list `Items` under the library; (c) add the federated library to the share user's `EnabledFolders` in the dashboard, then run Reconcile; (d) `PUT Shoal/Admin/Libraries/{m}/Access` with the share user in `selectedUserIds`; (e) put the share user id into the selection by editing `state.json`, restart; (f) open the picker | (a) the share user's `EnabledFolders` never gains the library; (b) 404 on every route with zero fakepeer requests, and the library is absent from listings; (c) Reconcile removes it and logs "Shoal removed a federated library from a sharing account"; (d) 400; (e) still 404 (the share-user check runs before the selection); (f) the share user is not listed (`isShareUser`). Positive control: the pairing admin still plays |
| T-VIS-8 | **Uninstall narrows, never widens** (5.6 v2.4; W6 L-5). Owner: Pilot. Gates K10, release. Setup: library L in `Selected` (pairing admin only); account U with "all libraries" (managed after Reconcile); managed account V whose libraries an admin then edits in the dashboard. Steps: (a) uninstall Shoal, restart; (b) reinstall, Reconcile, then "Remove everything" and uninstall | (a) U keeps its explicit list without L and does not see L; V untouched; one Warning names U; L still exists. (b) L is deleted first, then U has "all libraries" again; V untouched |
| T-PLAY-1 | Web direct play, seek, resume | works; progress recorded on Home only |
| T-PLAY-2 | Forced transcode: play, seek far, change audio | Peer ffmpeg only; seek works (owner test 01 passed on the spike) |
| T-PLAY-3 | Subtitles: embedded, external srt, burn-in PGS, HLS subtitles | all visible |
| T-PLAY-4 | Moonfin client (Moonbase on Home) | direct play and transcode work; record quirks (device-level detail in T-CLI-13) |
| T-PLAY-5 | Official Jellyfin Android app (built-in player); then Swiftfin | Android: direct play and transcode work. Swiftfin: direct play fails with 401 (known limitation, 6.12); record what the user sees |
| T-PLAY-6 | Moonfin Smart-TV (Tizen or webOS emulator, if available) | `stream.{Container}` direct play works; `js` subtitles show; PGS in-band or burned in |
| T-PLAY-7 | Two users transcode at once; stop one | the other continues |
| T-PLAY-8 | Pause 3 min during HLS, then resume | resumes; peer job alive or restarted cleanly |
| T-PLAY-9 | Restart Home mid-play | client errors; Play again works |
| T-PLAY-10 | 3 streams at limit 2; 100 PlaybackInfo per minute | 429 / `RateLimitExceeded` |
| T-PLAY-11 | Local copy added later for a federated title | stub removed; watch state carries over |
| T-PLAY-12 | Plugin disabled with stubs present | fails within about 1-2 s (`.invalid` through the owner's resolver); no hang |
| T-PLAY-13 | Peer movie with 2 versions | exactly one MediaSource; no peer source id anywhere |
| T-PLAY-14 | Library listing and detail page of a federated movie | duration, poster and backdrop shown |
| T-PLAY-15 | Client-built URLs with no prior PlaybackInfo: `stream?static=true`, `master.m3u8` | static 206; core HLS 404 |
| T-PLAY-16 | Peer offline, press Play in web, Moonfin, Android | fast failure everywhere; DisplayMessage shown in web (record other clients) |
| T-CONC-1 | **Several users through one share token** (P3; owner's question). Spike build, `MaxActiveSessions` 0 and 1: 1 to 4 simultaneous streams (static and forced libx264), 60 s each | **PASSED on the spike (Pilot-03):** zero request errors at every level; one share-user session on the Peer throughout; one ffmpeg job per converting stream; limit 1 did not affect streams while a self-mint was refused; one job saturates 4 cores, four ran at about 2.2x real time each. Repeated on the MVP build inside T-CONC-3 (e) |
| T-CONC-2 | **Stop ends exactly one Peer job** (6.8 v2.4; P3§5). Owner: Pilot. Gates K19. Four streams from three users (1 static, 3 HLS); stop one HLS stream from jellyfin-web, one from Moonfin (exit beacon); kill a third client with no stop | the two stopped jobs are gone from lab-server within 5 s; the remaining streams keep playing with zero errors; no Peer request carries an empty `playSessionId`; the killed client's job ends within about 65 s (Peer idle kill) and its slots free within 2 min; one share-user session on the Peer throughout |
| T-CONC-3 | **Limits refuse cleanly while others play** (6.9 v2.4; P3§6.3). Owner: Pilot. Gates K15, K17. Defaults (4 streams and 4 conversions per peer (D15); for (c) set `maxConcurrentTranscodes=2` so the conversion refusal is exercised, 4 per account). (a) users A and B start 2 direct streams each; user C presses Play; C replays a stream URL from a grant obtained before the fourth started; (b) A starts a third stream on that peer; (c) stop two of them; A and B each start one forced conversion; C starts a title that can only be converted, then one that plays directly; (d) stop one conversion, C retries; (e) `maxConcurrentTranscodes=4`, four conversions from two users | (a) C gets `RateLimitExceeded` at PlaybackInfo and 429 at the first byte; the four keep playing with zero errors and no gap above 6 s; (b) refused the same way (half rule); (c) C's conversion `RateLimitExceeded` with no Peer job started; C's direct title plays; (d) succeeds within 5 s; (e) all four play; record Peer CPU and segment p95 against T-CONC-1 |
| T-SEC-1 | No token: static, `/Shoal/Hls/...`, `/Shoal/Subtitles/...` | 401 |
| T-SEC-2 | User B uses user A's grant URL with B's token | 404 |
| T-SEC-3 | User without library access calls PlaybackInfo or stream | 404 |
| T-SEC-4 | Non-admin calls `/Shoal/Admin/*` | 403 |
| T-SEC-5 | HAR of a full session (browse, direct, HLS, subtitles) | no peer token, GUIDs or host anywhere |
| T-SEC-6 | Grep Debug logs, `.strm`, `.nfo`, plugin XML, `state.json`; check file modes | no token anywhere; peer GUIDs only in `catalog.json` (0600) |
| T-SEC-7 | fakepeer serves **F-PL-01..22** and **F-RS-09**; subtitle `Path` with an http URL | 502 or line dropped; no connection to the named hosts; subtitle stream removed |
| T-SEC-8 | DNS rebinding: the peer name flips to 127.0.0.1 mid-run | connection refused by policy |
| T-SEC-9 | Revoke on a stock Peer per 4.5 (disable first) | next Home call 401; `AuthFailed`; Play "not allowed"; ≤ 2 calls in the Peer log |
| T-SEC-10 | Peer request logs | the token string never appears (header auth) |
| T-SEC-11 | fakepeer names everything `<img src=x onerror=alert(1)>` | literal text on every admin screen |
| T-SEC-12 | Guard filter on lab-server: anonymous static and subtitle requests for shared and non-shared items, `LogOnly` and `Enforce` | LogOnly: 206 and counted; Enforce: 401 shared, 206 non-shared; images untouched; Home proxy plays |
| T-SEC-13 | Revoke on lab-server, then the same GUID through Home's proxy | Home refuses within 60 s although the Peer would serve it |
| T-SEC-14 | Local user hand-crafts HLS segment or master queries | ignored; the peer request has only grant parameters plus validated integers |
| T-SEC-15 | The negative authorization matrix (8.7), including R16 (v2.3: federated route id A with `itemId=B`, the caller admitted to B only; also a **local** route id with `itemId=<local>&itemId=<federated>`; B, P and X GUID forms, upper case and `%20` padding on every 6.1 route) | every cell as specified; zero fakepeer requests on denies; R16 cells 404 and core never resolves the stub |
| T-SEC-16 | Self-minting: a share-user token runs Initiate + Authorize(self) | stock Peer: succeeds (documented); disabling the user kills both tokens. Shoal Peer: Authorize → 403; a device minted any other way is signed out within 60 s |
| T-SEC-17 | One user takes both peer slots with PlaybackInfo only | a second user can still play |
| T-SEC-18 | **F-RS-01, -02, -03, -07, -08** on every proxied route; v2.3: a VTT file and an HLS subtitle segment carrying a `STYLE` block with `::cue { background-image: url(http://<capture host>/x) }`, a `REGION` block and `<script>` in cue text, played in jellyfin-web with the native track | Shoal's type and headers per 8.6; nothing copied; v2.3: the emitted VTT has no `STYLE`/`REGION`/`NOTE` block and no tag other than `<i> <b> <u>`; the capture host receives no request (and, recorded once against lab-server directly, whether the browser would have fetched it) |
| T-SEC-19 | **F-MD-01..05** end to end | no script runs on jellyfin-web's item page, in Moonfin, or in the track menu; canonical ids in `ExternalUrls` |
| T-SEC-20 | fakepeer compares every recorded request with 6.11 | exact match: headers, paths, queries, bodies |
| T-SEC-21 | Peer host `tv`, a title "TV Show"; then a peer GUID injected into a stream title | no false canary hit; the GUID → 502 |
| T-SEC-22 (retired, D17) | Stock Peer session limit ("Open after v2.1" #1, plus v2.2): a share-user token registers a fresh DeviceId as non-persistent, then tries (a) Initiate + Authorize(self) and (b) password reset + `AuthenticateByName`; restart the Peer and watch the anchor; admin password change; **v2.2:** with the limit at 1, a Quick Connect re-pair (friend signs in, enters a new code) with and without deleting the old `Shoal: <Home>` device, then with the limit at 0 | (a)/(b) succeed without the limit and are refused while a session exists; the anchor restores protection within 60 s of a restart; the password change signs out every device. Re-pair: refused at limit 1 in both variants (the friend's own session fills the slot), succeeds at limit 0. If the results differ, 4.1 step 9, 4.4 and 4.5 change before release. **Status (v2.3): PASSED** on 12.1 (P2§2-5), except the 60 s bound, which needs Home's timer. **(d) timed variant, gates K20 (W4-5):** with K20 deployed, restart lab-server (1) normally, (2) while Home's circuit to it is open (block it until the breaker opens, then unblock), (3) with Home restarted at the same time, (4) with the server paused on Home; measure from the Peer being reachable to the anchor reaching it: within one probe interval of the circuit closing in (2), at Home startup in (3), and no gap in (4) |
| T-SEC-23 | Alias of **T-VIS-7** (chain sharing, W2§N-2, threat T46). Run once, report under both ids | as T-VIS-7 |
| T-SEC-24 | **Guard filter robustness** (8.4 (a) step 1, (c), (d); 4.1b step 3; W2§N-6). Owner: Pilot on lab-server with Shoal (`Enforce` and `LogOnly`), plus unit tests for the cache and the fail-closed wrapper. Gates K23 (sweep ordering also K21, K22). Steps: (a) 200,000 anonymous requests to `Videos/{random GUID}/stream?static=true` and the subtitle route; (b) a title added to a shared library, then requested anonymously at once and again after 60 s; (c) inject an exception into job (d) (test build switch), then call `QuickConnect/Authorize`, `Users/Password` and `Sessions/Capabilities/Full` with a share-user token; inject one into job (a) and request a shared title anonymously; (d) 50 invite approvals in a row, each followed at once by the policy update's device sweep; (e) a share running 2 transcodes starts a third with a new `PlaySessionId`, then starts audio HLS and a subtitle conversion | (a) guard cache size stays at or under 10,000 entries, contains no non-existent ids, and the process memory stays flat; (b) anonymous access is possible for at most 60 s (documented staleness), then 401 in `Enforce`; (c) job (d) answers 403 (fails closed), job (a) calls `next()` and increments the guard error counter (fails open), one Warning per hour with no ids; (d) the freshly minted Home device is never logged out (`expectedDeviceIds` is written before `AuthenticateDirect`); (e) the third transcode → 429 (Home `RateLimitExceeded`); audio HLS and subtitle conversion are **not** counted, and the Hardening screen says "limit", not "cap" |
| T-SEC-25 | **`ActiveEncodings` cannot free a running slot** (6.1, 6.9; W4-4). Owner: Pilot with fakepeer. Gates K13, K15. Steps: (a) `MaxStreamsPerUser=2`; user U direct-plays item X (slot 1) and keeps the connection open; `DELETE /Videos/ActiveEncodings?deviceId=a&playSessionId=<X's grant PlaySessionId>`; U starts item Y (slot 2), then item Z; (b) an HLS grant with segments served, then the delete; then a segment on the old grant; (c) an HLS grant before any segment, then the delete; (d) 30 deletes in one minute for U's own grant; (e) user V's delete with U's PlaySessionId | (a) the delete answers 204 and fakepeer receives **no** request; Z gets **429** (X's slot is still held); (b) exactly one scoped delete reaches fakepeer, the slot is released afterwards, and the later segment must win a slot again; (c) 204 and no fakepeer request; (d) 429 after 10, fakepeer receives at most one delete; (e) falls through to core (`next()`), nothing sent to fakepeer, U's stream unaffected |
| T-NET-1 | `tc netem rate 8mbit delay 80ms 20ms loss 0.5%` | phase 4 speed and verdicts; playback works |
| T-NET-2 | `delay 300ms` | "slow start and seek"; playback works |
| T-NET-3 | `loss 100%` mid-stream | client stalls; Home responsive; circuit opens within 3 failures; recovery |
| T-NET-4 | `docker pause lab-server` (TCP accepted, no response) | deadlines per 6.10; no thread growth |
| T-NET-5 | `docker stop lab-server` during a Home full library scan | scan duration unchanged; no core errors |
| T-NET-6 | SOCKS5 proxy mode | all peer traffic via the proxy; pairing, sync and play work |
| T-NET-7 | Delay jump 20 → 120 ms with rate halved | "possibly relayed" hint (phase 4) |
| T-NET-8 | HTTPS peer with a trusted internal CA, then an untrusted cert, then a changed key with the pin on | trusted: works; untrusted: refused with a clear error; pinned key change: `TlsKeyChanged` |
| T-NET-9 | Peer behind `/jellyfin`, Home with its own `BaseUrl` | pairing, sync, direct play and HLS work |
| T-REG-1 | Plugin upgrade and reinstall | state, secrets and catalogs survive; no re-pair |
| T-REG-2 | Core resets the plugin XML (corrupt it, restart) | peers, access modes and hardening mode intact |
| T-REG-3 | Corrupt `state.json`; then also `.bak` | `.bak` used; then recovery mode, nothing overwritten |
| T-REG-4 | Flip a byte in `secrets.enc` | Home refuses peers; key not regenerated |
| T-REG-5 | **Restore and recovery keep the stricter settings** (3.7, 3.1 `guard-lastgood.json`; W2§N-9). Owner: Pilot, plus unit tests for `AtomicFile.Write`. Gates K1, K23 (guard part), U6. Setup: a library in `Everyone`, a share, hardening `Enforce`; save; then tighten (library to `Selected`, revoke the share, keep `Enforce`) and save again so `.bak` holds the wider state. Steps: (a) corrupt `state.json`, restart; (b) corrupt `state.json` and `.bak`, restart so a daily copy loads; (c) corrupt all copies (recovery mode) with `guard-lastgood.json` intact, then with it corrupt too; (d) `POST Shoal/Admin/State/ConfirmRestore` (v2.2), and once more; (e) put `/config` on a filesystem without hard links (CIFS in the lab) and save | (a) and (b): every access mode loads as `Selected` with selections kept; hardening is the stricter of the restored and the last-good mode (`Enforce`); a share whose user no longer exists is dropped; the banner "Settings restored from <time>" stays until Confirm; (c) recovery mode: federated titles `NotAllowed`, share-user bans and `Enforce` still active from `guard-lastgood.json`; with it corrupt too, the banner says "Peer protection is off until you recover"; no file is overwritten (byte-identical); (d) the banner clears and access modes stay `Selected`; the second call → 409; (e) the save succeeds by copy-then-rename and Settings shows `backupByCopy` |

**Client request replays (v2.2; owner D11; from Scout's T-C1..13, SC§5).** Pilot owns them. Lab only, no phone, except T-CLI-13. Placeholders as in SC§5: `H` = lab-server, `T` = a valid Home user token, `S` = a federated local item id (N format), `U` = that user. Unless stated, requests carry `Authorization: MediaBrowser Client="Moonfin", Device="replay", DeviceId="replay-1", Version="2.7.0", Token="$T"`. Every case also records `Content-Range`/`Content-Type`, the fakepeer request log, and the token/GUID/host grep (which must stay at zero). **Baseline now:** T-CLI-1..6 may run against the current spike build to record where it stands; they gate only when the K-task listed lands. Pilot ran that baseline (P2§6); the concrete sub-cases, with the spike result and the v2.3 expectation for each, are in the table after this one (v2.3). Further URL detail is in SC§5.

| ID | Case (Scout's id) | Expected | Gates |
|---|---|---|---|
| T-CLI-1 | (T-C1) Credential-less direct play: the Android TV form `stream?container=mkv&static=true&mediaSourceId=S&tag&playSessionId&deviceId`, the Swiftfin form, the Findroid form, each with `Range: bytes=0-1023`; subtitle twins `Videos/S/S/Subtitles/2/Stream.vtt` and `.../2/0/Stream.srt` | **401** for all, zero fakepeer requests (G1, Option C) | K13, K15, K18 |
| T-CLI-2 | (T-C2) Moonfin PlaybackInfo with the six values in query **and** body, then a variant where query and body differ (query must win), a variant with `UserId` of another user (ignored), and a variant with no profile after `POST Sessions/Capabilities/Full` with a profile; then direct play `stream?MediaSourceId=S&Static=true&ApiKey=$T` with header + `ApiKey` + `X-Emby-Token`, Ranges `0-`, `300000000-`, `-4096`, `HEAD`; lowercase `static=true` | 200, exactly one source, `Id=S`, `Protocol=File`, `IsRemote=false`, `Path=null`, `SupportsDirectStream=false`, `Container` non-empty, no `DirectStreamUrl`, `PlaySessionId` = grant; forwarded body (T-SEC-20) reflects the query value and the registered profile; 206/206/206/200 with correct `Content-Range` and Home's type | K14, K15 |
| T-CLI-3 | (T-C3) HLS as Moonfin mutates it: fMP4 and `ts` profiles; `TranscodingUrl` + `&AudioStreamIndex=1&SubtitleStreamIndex=3`, + `&upmix=true`, + a different `AudioStreamIndex` than the grant; then `main.m3u8`, `hls1/main/-1.mp4`, segment 0, segment 120, `HEAD` on master and a segment, a segment with `Range: bytes=0-1`; `DELETE /Videos/ActiveEncodings?deviceId=replay-1&playSessionId=<ps>`; v2.3 credential variants: the URL as returned with no header, `ApiKey` stripped with the header only, no credentials, a **second** `ApiKey=` appended, lowercase `api_key=` with no header; a repeated `runtimeTicks` on a segment | 200 on every master variant (never 400), Peer request identical across variants, `clientIndexMismatch` = 1 only for the differing variant (read through the admin API); ffmpeg on the Peer only; the delete kills only that job; credential variants **200, 200, 401, 401, 401** (the last two are **core's** 401 and are correct, W4-11; P2§6 saw the same on the spike); repeated `runtimeTicks` → 400 | K16, K17 |
| T-CLI-4 | (T-C4) Subtitle routes as clients build them: embedded text `.../{i}/0/Stream.srt` and `.vtt`; `.../{i}/Stream.vtt`, `.ass`, `.js` (no `/0/`); `.../{i}/0/Stream.sup` and `Stream.pgssub`; an out-of-range index, a bitmap index; `?itemId=S&format=srt` overrides on a non-federated route id; two concurrent requests for one embedded track; also record what lab-server itself answers to `Stream.sup` for embedded PGS. **v2.3 additions:** federated route id with a different federated `itemId` (R16); an MP4 with a `mov_text` track, `.../{i}/0/Stream.vtt`; fakepeer answers 500 to the subtitle route three times in a row, then a PlaybackInfo; a client that aborts at 5 s and at 30 s against a fakepeer extraction of 120 s, retrying each time; a request with a repeated `endPositionTicks` | 200 with Home's type for text (`js` → `application/json`, valid rebuilt JSON; `vtt`/`srt` rebuilt per 6.6a step 9a); `sup`/`pgssub`, out-of-range and bitmap → 404 with zero fakepeer requests; the override request is 404 and never reaches core; one fakepeer request for the concurrent pair. v2.3: R16 → 404, zero fakepeer requests; `mov_text` → 200 from lab-server (else `mov_text` leaves the allowlist, 6.6a step 5); three subtitle 500s leave the **breaker closed** and the PlaybackInfo goes through; the 5 s abort cancels the Peer call, the 30 s abort leaves it running and the retry **joins** it (fakepeer sees one extraction, not one per retry); repeated `endPositionTicks` → 400 | K18 |
| T-CLI-5 | (T-C5) Roku header-only `stream?Static=true&Container=mkv&PlaySessionId&AudioStreamIndex=1&MediaSourceId=S` with the extra `UserId=` field; Smart-TV `stream.mkv?Static=true&mediaSourceId=S&deviceId=tv-1&ApiKey=$T&Tag=<ETag>`; `stream.mov,mp4,m4a,3gp,3g2,mj2`; `stream.` | 206, 206, 206 (type from the catalog container), 404 for `stream.` | K13, K15 |
| T-CLI-6 | (T-C6) Bypass routes: Moonfin preview `stream?Static=false&...`, `master.m3u8`, `main.m3u8`, `live.m3u8`, `Audio/S/main.m3u8?AudioCodec=copy&SegmentContainer=mp4`, `Audio/S/universal`, `Audio/S/stream?Static=true`, `Audio/S/stream.mp3`; `Items/S/Download`, `Items/S/File`; `MediaSegments/S`, `Users/U/Items/S/Intros`; `POST LiveStreams/Open?itemId=S`. **v2.3 (W4 tests, P2§7 #7):** each of `Audio/S/stream?Static=true`, `Items/S/Download`, `Items/S/File` three times: anonymous, as a normal user without download permission, as a download-permitted user; `LiveStreams/Open?itemId=<local>&itemId=S`. **v2.4 (SR§2):** `/emby/Videos/S/stream?Static=true`, `/emby/Items/S/File`, `/mediabrowser/Videos/S/master.m3u8` with header auth | 404 / 403 / 200 empty / 400 as listed in 6.1; none returns `.strm` text or an ffmpeg error; counter incremented for the 404 rows. v2.3: `Audio/S/stream?Static=true` 404 for all three callers; `Download` and `File`: anonymous → **core's 401** (core's `[Authorize]` runs first), signed-in → Shoal's 403; in every case the body is empty and contains no stub text; the repeated `itemId` → 400. v2.4: record whether 12.1 serves the prefixes at all; if it does, the same answers as the unprefixed routes (206 through Shoal, 403, 404), never core's stub | K13 |
| T-CLI-7 | (T-C7) Moonfin reporting with the grant `PlaySessionId`: `Playing`, 3× `Progress`, `Stopped?ApiKey=$T` beacon (`Content-Type: application/json`) | Home user data updated; the Peer sees only the scoped `Playing`/`Ping`/`Stopped` with a non-empty peer PlaySessionId | K19 |
| T-CLI-8 | (T-C8) Smart-TV storm: 5 PlaybackInfo in 2 s for one item and DeviceId (different `AudioStreamIndex`, `EnableDirectPlay:false`, `SubtitleStreamIndex:-1`, a fallback retry), while the same user plays an HLS title on `DeviceId=replay-2`; then one call per second for 2 minutes; then 9 different items on `replay-1`. **v2.3 (W4-2, W4-6):** user B presents user A's `DeviceId` (`replay-1`) on PlaybackInfo, on a subtitle request and on `ActiveEncodings` with A's PlaySessionId; B registers capabilities for a DeviceId whose device row is A's, then calls PlaybackInfo with no profile on that DeviceId; a 300-character `DeviceId`; `MaxStreamsPerUser=10` written into the XML; two devices of one user direct-play the same item, then 8 more PlaybackInfo calls on other items; with the global cap reached, a user whose call only replaces their own slot-free grant | the 5 calls → 200 each, one live grant for that key, no `RateLimitExceeded`; the replay-2 stream keeps playing (its grant is never evicted, also not by the 9 items, which evict only slot-free grants); the steady run gets `RateLimitExceeded` once the burst of 15 is spent and then about 30 successes per minute (6.9). v2.3: B can neither read, replace, evict nor kill A's grant or stream (A's grant id and Peer job unchanged; B's own grants are separate); B's no-profile call forwards **no** `DeviceProfile` (the device row is A's); the long `DeviceId` is stored hashed (`h:` prefix) and works; `MaxStreamsPerUser` reads back as 4; both devices' grants survive the 8 calls, and once no slot-free grant is left the next call gets `RateLimitExceeded` instead of evicting a playing one; the replacing call succeeds at the global cap | K14 |
| T-CLI-9 | (T-C9) Findroid profile ("Direct play all", empty lists) | no 5xx; record whether `MediaStreams` and `DeliveryUrl`s come back or the Peer's `NoCompatibleStream` (relayed as the enum only) | K14 |
| T-CLI-10 | (T-C10) `MediaSourceId` in N form, D form, unknown | 200, 200, 400; `MediaSources[0].Id` parses as the item GUID | K14 |
| T-CLI-11 | (T-C11) T-CLI-2 and -3 through a reverse proxy with `BaseUrl=/jf` | `TranscodingUrl` and `DeliveryUrl` resolve under the base path (with T-NET-9) | K14, K17 |
| T-CLI-12 | (T-C12) Moonbase on lab-server (mirror of production: Moonbase 2.2.0.0, File Transformation, JavaScript Injector, Jellyfin Enhanced, Media Bar, Plugin Pages, Intro Skipper): sync 200 stubs; open Moonbase's active-transcodes view during a peer transcode; re-run T-CLI-2/3; read the Jellyfin Enhanced and Intro Skipper logs for the stubs | record the number of "new media" pushes (expect one summary, only to users who can see the library: the pairing admin under D3); peer transcodes absent from Moonbase's dashboard (expected, documented); T-CLI-2/3 unchanged; no repeated errors or scheduled ffmpeg analysis on `shoal.invalid` stubs (if there are, file a Kiro task: exclude the stub library or document the setting) | MVP-A DoD |
| T-CLI-13 | (T-C13) Real devices: Moonfin on iOS, Android, Android TV, a Smart-TV and Roku; one stock Jellyfin Android TV app. **v2.3 (W4-15):** also Jellyfin Media Player (desktop), the official Jellyfin Android app's built-in player, and Moonfin on tvOS, Fire TV, Xbox, Vega and desktop where the owner has the device; each platform not run stays "should work (not tested yet)" in the 6.12 text | play, seek, switch audio, pick text and PGS subtitles, kill the app mid-play (peer job idles out in 60 s); the stock app fails with 401 as documented (6.12); a per-platform pass list that drives the "works" list in 6.12 | release (D11 blocker) |
| T-CLI-14 | (new) Credential hygiene: run T-CLI-2..5 with Home and fakepeer at Debug; grep **Shoal's** log lines and exceptions (v2.3, W4-14: core's own `ResponseTimeMiddleware` logs full URLs at Debug and is out of scope) | the token (from `ApiKey`, header or both) appears in no Shoal log line, exception text or counter label; core's own lines are listed separately for the README | K13, K15, K17, K18 |
| T-CLI-15 | (new) Bitmap subtitle profile: a Moonfin Smart-TV profile with `pgssub External` and a PGS track selected. **v2.3 (W4-10):** also `External` entries for `sub`, `smi`, `ttml`, `xsub` and an unknown format | forwarded profile has no bitmap `External` entry (T-SEC-20); the Peer answers `Embed` (direct play) or `Encode` (burn-in); no stream carries a bitmap `DeliveryUrl`. v2.3: only `External` entries for `srt`, `subrip`, `ass`, `ssa`, `vtt`, `webvtt`, `js`, `json` reach the Peer | K14 |
| T-CLI-16 | (new, v2.3, W4-1) **Cold-extraction storm.** fakepeer holds every subtitle-file answer for 120 s. User U sends 20 subtitle requests for one title with different `{ticks}` and `endPositionTicks`, then requests for a second and a third title; meanwhile user V (another account) calls PlaybackInfo, plays an HLS title (segments) and Home's health probe runs; a third user W opens a subtitle lane for another title | V's PlaybackInfo, segments and the health probe complete within their normal deadlines; the circuit **stays closed**; fakepeer never sees more than **2** concurrent subtitle calls from Home, and at most 1 for U's first title at a time; U's second and third titles get 429 with `Retry-After: 10` (one open lane per user) while W's lane opens; requests beyond 16 waiting get 429 `Retry-After: 30`; when the held answer is released, U's queued requests for the first title complete, one Peer call at a time | K18 |
| T-CLI-17 | (new, v2.4; owner D14) **Moonfin for iOS on the MVP build**, the owner's spike test (`docs/tests/owner-moonfin-ios-01.md`) repeated after G7. Owner on the iPhone, Pilot reads lab-server and lab-server logs at Debug. Play from the start, seek, pick a text subtitle, set quality to **720p at about 1 Mbps** so the Peer must re-encode, switch audio, background and resume, kill the app mid-play, play again | direct play arrives as `Videos/{id}/stream?Static=true` through StaticStreamHandler (no stub text, no `/Shoal/Proxy`); the quality change shows `libx264` in lab-server's log and no ffmpeg on lab-server; subtitles show, and the track type and route are recorded; after the kill the Peer job ends within about 65 s; no token in Shoal's log lines. Passing moves "Moonfin for iOS" to "Works" (6.12 rule 1, ui-spec 8.3.12) | direct-play part after K15 (early signal); full run after K18 and K19; **release** (11.5 item 8) |

T-ROT-*, T-TWO-*, T-ALT-*, T-TRK-*, T-ATT-*, T-IMG-* and the device steps S1 to S13 are defined in v2.6.

**T-CLI-1..6 sub-cases (v2.3, from P2§6).** Ids are Pilot's. Unless stated, the caller is a signed-in Home user (header token), the item is a federated movie `S`, static requests carry `Range: bytes=0-99`. "Spike" is what the spike build answered (not gating); "v2.3" is the expectation once the listed K-task lands. Every v2.3 deny also asserts zero fakepeer requests and a body free of stub text.

| Sub-case | Request shape | Spike (P2§6) | v2.3 expectation | Gates |
|---|---|---|---|---|
| 1a | Android TV / Swiftfin / Findroid: `Videos/S/stream?container=mkv&static=true&mediaSourceId=S&tag&playSessionId&deviceId`, **no credentials** | 206, stub text with the peer item id | **401** | K13, K15 |
| 1b | jellyfin-android external player: `stream?static=true&...`, no credentials | 206, stub text | **401** | K13, K15 |
| 1c | subtitle twins without credentials: `Videos/S/S/Subtitles/2/Stream.vtt`, `.../2/0/Stream.srt` | 500 | **401** | K13, K18 |
| 2a | Moonfin-Core: `POST Items/S/PlaybackInfo?UserId&StartTimeTicks=0&IsPlayback=true&AutoOpenLiveStream=true&MediaSourceId&MaxStreamingBitrate=20000000&AudioStreamIndex=1&SubtitleStreamIndex=-1` + body `{DeviceProfile, MaxStreamingBitrate:8000000, AudioStreamIndex:2}` | 200, one source, `Protocol=Http`, `IsRemote=true`, absolute `Path` with `ApiKey` | 200, one source, `Id=S`, `Protocol=File`, `IsRemote=false`, `Path=null`, `SupportsDirectStream=false`, `Container` set; forwarded body carries the **query** values (T-SEC-20) | K14 |
| 2b | Smart-TV: body only (`UserId`, `DeviceProfile`, `Enable*`, indices, `MediaSourceId`) | as 2a | as 2a, body values forwarded | K14 |
| 2c | forced transcode (`EnableDirectPlay=false`, `EnableDirectStream=false`) | `TranscodingUrl=/Shoal/Proxy/...` with `ApiKey` | `TranscodingUrl=/Shoal/Hls/{grantId}/master.m3u8?ApiKey=<caller>` | K14 |
| 2d | `POST` with `{}` (no profile anywhere), with and without a profile registered through `Sessions/Capabilities/Full` by the **caller's own** device | 200, direct play | 200; forwarded body has the registered profile (rebuilt) only for the caller's own device row (W4-2), none otherwise | K14 |
| 2e | `GET Items/S/PlaybackInfo?userId=` | 200 | 200, no `DeviceProfile` forwarded | K14 |
| 2f | hostile `UserId=0000…01`, `LiveStreamId=abc`, `AutoOpenLiveStream=true` in query and body | 200 (ignored) | 200; `UserId` ignored, `LiveStreamId` omitted, `AutoOpenLiveStream=false` forwarded | K14 |
| merge | query `AudioStreamIndex=1&MaxStreamingBitrate=2000000` vs body `2` / `9000000`, forced transcode; then body only | query wins, body fills | same, asserted on the forwarded body (use a title with two audio streams so the index is conclusive) | K14 |
| 3a | `TranscodingUrl` as returned | 200 | 200 | K17 |
| 3b | Moonfin rewrite: `AudioStreamIndex=2`, `SubtitleStreamIndex=-1` added, `StartTimeTicks` removed | 200 | 200, Peer request unchanged, `clientIndexMismatch` +1 if 2 differs from the grant | K17 |
| 3c | `SubtitleStreamIndex` dropped, `AudioStreamIndex=0` | 200 | 200, as 3b | K17 |
| 3d | Smart-TV `upmix=true` appended | 200 | 200, ignored | K17 |
| 3e | unknown `foo=bar` and `StartTimeTicks=600000000` | 200 | 200, ignored | K17 |
| 3h | `main.m3u8` from the master line | 200 | 200, rebuilt per 6.4 | K16, K17 |
| 3i | segment `hls1/main/1.ts` | 200, TS sync byte | 200, `video/mp2t` | K17 |
| 3j | segment with extra `AudioStreamIndex=2&SubtitleStreamIndex=-1&upmix=true` | 200 | 200, ignored | K17 |
| 3k | credentials on the master: as returned without header; `ApiKey` stripped with header; no credentials; a second `ApiKey=` appended; lowercase `api_key=` | 200, 200, 401, 401, 401 | **200, 200, 401, 401, 401** (the last two are core's, W4-11) | K17 |
| 4a-4e | embedded subrip track 2: `.../2/0/Stream.srt`, `.../2/0/Stream.vtt`, `.../2/Stream.vtt`, `.../2/Stream.ass`, `.../2/Stream.js` (`ApiKey`) | 500 | 200, Home's type per format, bodies rebuilt (6.6a steps 9-9a) | K18 |
| 4f | `.../2/0/Stream.sup` | 500 | 404 | K18 |
| 4g | `.srt` with the header only | 500 | 200 | K18 |
| 4h | `.srt` with no credentials | 500 | 401 | K13, K18 |
| 4i | `?itemId=S&index=2&format=srt` on a **non-federated** route id; v2.3 also a federated route id with a different federated `itemId` | 500 | 404, never reaches core | K13, K18 |
| 5a-5c | Moonfin-Core static: header; `ApiKey` only; both (`MediaSourceId&Static=true`) | 206, stub text | 206 of the real file through Home, type from the catalog | K15 |
| 5d-5e | lowercase `static=true` with header; with `ApiKey` only | 206, stub text | 206 | K13, K15 |
| 5f | Smart-TV `stream.mkv?Static=true&mediaSourceId&deviceId&ApiKey` | 206, stub text | 206 | K15 |
| 5g | `stream.?Static=true` (empty container) | 404 (routing) | 404 (routing) | K13 |
| 5h | `stream.mov,mp4,m4a?Static=true` | 206 | 206, type from the catalog container | K15 |
| 5i | Roku header with an extra `UserId=` field, `Static=true&Container=mkv&PlaySessionId&AudioStreamIndex&MediaSourceId` | 206 | 206 | K15 |
| 5j | bare `X-Emby-Token` header only (legacy auth off) | 206 | **401** (core ignores it, so the request is anonymous) | K13 |
| 5k-5l | `Static=false&videoCodec=h264&audioCodec=aac`; `stream.mp4?Static=false` | 500 | 404 + counter | K13 |
| 5m | `HEAD ...?Static=true` | 200, length 76 | 200 with the real length | K15 |
| 5n | bogus `ApiKey=dead...`, no header | 206, stub text | **401** | K13 |
| 6a | `Videos/S/master.m3u8` | 200 dead playlist | 404 | K13 |
| 6b-6d | `Videos/S/main.m3u8`, `live.m3u8`, `hls1/main/0.ts` | 500, 500, 400 | 404 | K13 |
| 6e | legacy `Videos/S/hls/seg/stream.ts`, no credentials | 404 | 404 (Shoal's) | K13 |
| 6f | `Audio/S/stream?Static=true` | 200, stub text | 404 (and anonymous, no-download and download-permitted variants, T-CLI-6) | K13 |
| 6g-6j | `Audio/S/stream.mp3`, `Audio/S/universal`, `Audio/S/main.m3u8?AudioCodec=copy&SegmentContainer=mp4`, `Audio/S/master.m3u8` | 500, 500, 500, 200 dead playlist | 404 | K13 |
| 6k-6l | `Items/S/Download`, `Items/S/File` (signed in) | 200, stub text | 403; anonymous: core's 401 | K13 |
| 6m-6n | `Videos/S/Trickplay/320/0.jpg`, `Videos/S/S/Attachments/0` | 404, 404 | 404 (Shoal's) | K13 |
| 6o | `POST LiveStreams/Open?itemId=S` | 500 | 400 | K13 |
| 6p | `DELETE Videos/ActiveEncodings?deviceId&playSessionId` (no grant match) | 204 | `next()`, core's 204; with a grant match see T-SEC-25 | K13 |
| 6q | `GET MediaSegments/S` | 200 empty | 200 empty (core; nothing to leak) | K13 |
| 6r-6s | `POST Sessions/Playing`, `Sessions/Playing/Stopped` with local ids | 204 | 204; the Peer sees only the scoped calls (T-CLI-7) | K19 |

### 11.4 Definition of done

- **MVP-A:** K1-K20 and U1-U6 merged; Warden finds no High issues; Pilot passes T-PAIR-1..7 and -12, T-SYNC-1..12, T-VIS-*, T-PLAY-1..16, T-SEC-1..22 (Home-side parts) and **-25** (v2.3), T-NET-3..6, -8, -9, T-REG-* and **T-CLI-1..12, -14, -15** (v2.2) and **-16** (v2.3), and **T-CONC-2, -3, T-SYNC-13, T-VIS-8** (v2.4), with video evidence of web plus Moonfin playback.
- **Release (owner D11 blocker):** T-CLI-13 on real devices passes for every Moonfin platform listed in 6.12, and **T-CLI-17** (Moonfin iOS on the MVP build, v2.4) passes.
- **MVP-B:** K21-K23 and U7 merged; Pilot passes T-PAIR-8..11, T-SEC-12, -16 (Shoal Peer part); Warden re-review of the Peer role.
- **Phase 5 and 6:** K24 to K33 merged; Warden batches A and B without High findings; Pilot passes the v2.6 tests; the 6.12 Works list equals the platforms that passed S1 to S9 and S13.

### 11.5 Release checklist (before the first public push)

1. **Scrub or exclude internal infrastructure (W§B-14).** `lab/` is excluded from the public repo or genericized. Grep `docs/`, `tools/` and `src/` for private IPs (`192.168.`, `10.`, `172.16-31.`), host and container names, tailnet names, the owner's handle and production-stack references, and remove them. Decide whether `.claude/` ships (default: no).
2. **History:** rewrite commit authors to a noreply address, or start the public repo from a fresh squashed history.
3. **Supply chain (W§A-17):** hardware-key 2FA on the GitHub account; protected branches with required review; signed tags; a signed release with provenance attestation or a cosign signature next to the manifest; `packages.lock.json` with `RestoreLockedMode` in CI; pinned analyzers; `SECURITY.md` with a disclosure address. The README states that Jellyfin checks only an MD5 of the zip (`InstallationManager.cs:586`).
4. **README:** Tailscale sharing guide (own node, sharee ACL `tcp:8096`); "Both servers need Shoal" with the install link https://github.com/Ninkxz/jellyfin-plugin-shoal (v2.8), the revoke runbook (4.5); the offline friends section and the "Protect your shared videos" default (D18); LAN cleartext warning; "redact Debug logs, they contain user tokens" (W§A-20.4); recommend `JELLYFIN_SHOAL_KEY`; backups and restore (3.7); ratings caveat and restricted profiles (5.6); `Enforce` breakage table (8.4). **v2.2:** the client-support text of 6.12 (supported apps; the official Android TV app, Swiftfin, Findroid and external players do not work; no offline download); PGS shows in-band or burned in, never as a separate file; re-pairing a stock friend needs the session limit at 0 until done (4.1 step 9); with Moonbase installed, a large sync sends one "new media" summary push (T-CLI-12). **v2.3 (W4-14, W4-15, W4-5, W4-13):** keep "redact Debug logs, they contain user tokens" and say why (core's `ResponseTimeMiddleware` logs the full URL, `ApiKey` included, at Debug for every response slower than 500 ms); the "Don't work around the apps that don't work with a token-adding proxy" paragraph of 6.12; the client list says "most Jellyfin apps, but not all of them", with no "yet", and lists as working only the apps T-CLI-13 passed; the friend checklist presents steps 8 and 9 as a required pair; the stock revoke runbook says restarting Jellyfin also clears hidden sessions. **v2.4:** "How many people can watch at once": several Home users stream through one sharing account without trouble (T-CONC-1); direct plays cost the friend's server almost nothing, but each conversion uses its processor, and on a 4-core server without hardware acceleration one conversion can take every core (Pilot measured about 390 % CPU for one 720p conversion); the defaults are 4 streams and 2 conversions per friend's server, raise conversions only for a friend with hardware acceleration or many cores, and the friend's own viewers share that CPU; a low bitrate limit on the sharing account turns streams into conversions (4.1 step 3); a slow start (3-5 s) on a busy friend's server is expected. Uninstall: use "Remove everything" first; otherwise accounts Shoal managed keep explicit library lists (5.6). Cast names of a friend's titles are looked up on TMDb by Home's server (5.6 residual). Deleting a friend's title on Home lasts until the next sync (6.1).
5. **CI:** route-table diff test against the targeted Jellyfin release (8.4, section 10), built from the real MVC action descriptors, never swagger (6.1, v2.4); reflection tests; banned-API analyzer; the hostile fixture suite (Appendix C).
6. **Trademark:** a live USPTO (classes 9 and 42) and EUIPO search for "Shoal" (`docs/naming.md` used aggregators only).
7. Warden final review of the release build; Pilot's full 11.3 run on a clean Jellyfin.
8. **Moonfin for iOS on the release build (v2.4, owner D14):** T-CLI-17 passes, including the forced re-encode at 720p and about 1 Mbps (`libx264` in the Peer's log). The spike PASS does not count, because G7 changed the path Moonfin takes.

---

## 12. Owner decisions (resolved) and remaining questions

**Install Shoal first (D17, v2.8; the copy the page uses).** Install address: https://github.com/Ninkxz/jellyfin-plugin-shoal

- Blocking screen title: "This server doesn't have Shoal yet".
- Body: "Shoal has to be installed on both servers. That server answered, but it has no Shoal, or an older Shoal that can't pair."
- Steps: "Ask your friend to install Shoal from https://github.com/Ninkxz/jellyfin-plugin-shoal, restart Jellyfin, then open Shoal and choose Share with a friend to make you an invite. Then press Check again."
- Message to copy for the friend: "Hi! To watch each other's libraries we both need the Shoal plugin for Jellyfin. Please install it on your server from https://github.com/Ninkxz/jellyfin-plugin-shoal, restart Jellyfin, then open Shoal, choose Share with a friend, and send me the invite. Thanks!"
- Buttons: "Copy message", "Check again". There is no way around the screen when the answer is definitive. A throttled or unreadable ping shows a soft hint instead: "Couldn't tell whether that server has Shoal. You can carry on."
- Legacy banner on Overview: "{n} servers still use the old pairing. They are paused until you re-pair them with an invite." Button on each: "Pair again with an invite". Status chip: "Needs pairing again".

| v1 question | Decision (`docs/DECISIONS.md`) | Design consequence |
|---|---|---|
| 1 Who sees federated libraries? | **D3** default-deny, pairing admin pre-selected; **D4** per-library switch to "Everyone", including later users, with the selection restored on switching back | 5.6 access modes and Reconcile; CallerContext step 6; T-VIS-*, matrix rows R11-R11c and P1-P4 |
| 2 Offline behavior | **D5** titles stay listed and fail fast; greyed backdrop on the admin card; hiding off | 5.7, 9.3 |
| 3 Stock Peers | **D7, superseded by D17 (v2.8):** the friend needs Shoal; Quick Connect pairing for stock Jellyfin is removed | 4.1 is now the legacy QC note; 4.1b is the only path; MVP-A/MVP-B split (11.1) is history |
| 4 Peer sees watched titles | **D8** OK, never who | 6.8 |
| 5 Hardening default | **D9** `LogOnly` with one-click `Enforce`; **changed by D18 (2026-10-16): new installs start in `Enforce`**, existing servers keep their mode | 8.4; `Off` kept only for troubleshooting |
| (new) Friends need Shoal | **D17** (2026-10-16, supersedes D7): both servers need Shoal; pairing is by invite only; legacy QC peers are paused and re-paired by invite | v2.8; 4.1 to 4.4, 9.3 |
| (new) Screens | **D2** Native Plus per `docs/ui-spec.md`; **D6** friends section at the bottom of Overview | section 9 |
| (new) Team | **D10** Kiro and Kiro 2 | 11.2 |

**Remaining questions for the owner** (my defaults apply until answered):
1. **"Everyone" leaves out restricted profiles** (parental limits) unless you also tick "Include profiles with parental limits", because the friend's server chooses the ratings. OK? *Default: yes.*
2. ~~Friends on the public internet without Shoal~~ **Resolved by D17 (v2.8):** every friend needs Shoal and pairs by invite.
3. **Large additions:** when a friend adds more than 500 titles (or 20%) at once, Shoal holds them until you click Release, with no automatic release. OK? *Default: yes.*
4. **Certificate pinning for https friends:** off by default, because certificate renewals usually change the key and would break sharing every few months. OK? *Default: off.*

(Decided without asking: posters and backdrops are downloaded once, about 0.2 MB per title at Standard quality, so art works offline and browsing makes no peer calls; plain HTTP is allowed over the tailnet because WireGuard encrypts it.)

---

## Appendix A: facts verified in Jellyfin 12.1 / jellyfin-web 12.1 (Atlas, in addition to scout-01 and Warden's Appendix A)

| Fact | Ref |
|---|---|
| Plugin `RegisterServices` writes into the generic host's service collection | `Jellyfin.Server/Program.cs:172`; `ApplicationHost.cs:462,490-492` |
| Middleware order: BaseUrl redirect, Map(BaseUrl), ..., Authentication, Routing, Authorization, IP access validation | `Jellyfin.Server/Startup.cs:173-250` |
| Default scheme `CustomAuthentication`; failed authentication (invalid token, **disabled user**) leaves an anonymous principal; a deleted user's token still authenticates with a null user | `ApiServiceCollectionExtensions.cs:65-68,100-101`; `AuthService.cs:23-40`; `CustomAuthenticationHandler.cs:46-90` |
| Default handler checks remote access and parental schedule; throws `ResourceNotFoundException` for a missing user | `DefaultAuthorizationHandler.cs:41-94` |
| `GetItemById<T>(id, null)` returns any item; with a user it requires `IsVisibleStandalone` | `LibraryManager.cs:1734-1738, 4074-4087` |
| `IsVisibleStandalone`: tags, parental rating, unrated blocking, library access; unrecognized ratings count as unrated | `BaseItem.cs:1541-1585, 1865-1910, 2042` |
| Legacy `BlockedMediaFolders` is honored but never written by the policy API; the web Access page nulls it | `BaseItem.cs:1567`; `Folder.cs:256`; `UserManager.cs:529`; `web:apps/dashboard/features/users/components/Access.tsx:214` |
| `UserCreatedEventArgs` is published on creation; the dashboard creates the user, then posts its policy | `UserManager.cs:365`; `web:apps/dashboard/routes/users/add.tsx:128-160` |
| Quick Connect: anonymous Initiate; pending entries keyed by code and overwritten; approval sets expiry to now + 1 min and mints via `AuthenticateDirect` with the Initiate device fields; unknown secret → 404 | `QuickConnectController.cs:54-93`; `QuickConnectManager.cs:72-170` |
| `Authorize` is `[Authorize]` and accepts the caller's own user id | `QuickConnectController.cs:104-120`; `RequestHelpers.cs:62-83` |
| `AuthenticateDirect` skips the password (and the disabled check), checks `CanAccessDevice` and `MaxActiveSessions`; re-auth on the same DeviceId logs out old tokens | `SessionManager.cs:1695-1790` |
| Device lock: `CanAccessDevice` allows devices with `SupportsPersistentIdentifier=false`; browsers report false; `DeviceAccessHost` logs out non-allowed devices on policy update | `DeviceManager.cs:237-249`; `web:utils/dashboard.js:191`; `DeviceAccessHost.cs:62-94`; `UserManager.cs:927` |
| A valid token with a `DeviceId` header keeps the header's DeviceId; capabilities are saved per session device | `AuthorizationContext.cs:146-149`; `SessionManager.cs:1871-1901` |
| Disabling a user does not log out its devices | `UserManager.cs:848-927` |
| Non-admin `GET /Sessions` returns own and anonymous sessions only | `SessionController.cs:52-68`; `SessionManager.cs:2065-2140` |
| `SendMessageCommand` works without a controlling session; jellyfin-web displays `DisplayMessage` | `SessionManager.cs:1273-1303`; `web:scripts/serverNotifications.js:125` |
| Authorization header values are URL-decoded | `AuthorizationContext.cs:276-313` |
| Core logs a device's access token at Info when logging it out (Peer side) | `SessionManager.cs:1824` |
| Plugin XML is reset to defaults on any load error and saved non-atomically | `BasePluginOfT.cs:143-151, 184-198` |
| Built-in backup contents (no `<DataPath>/shoal/`, no plugin XML; library definitions included) | `BackupService.cs:407-448` |
| HLS formats: master subtitle line, variants and DoVi ordering, main playlist with `hls1/main/<n>.<ts\|mp4>` and `EXT-X-MAP` `-1.mp4`, subtitle playlist with culture-formatted `EXTINF` | `DynamicHlsHelper.cs:148,220-260,683-709`; `DynamicHlsPlaylistGenerator.cs:55-100`; `SubtitleController.cs:366-410` |
| Image endpoint accepts `format` (`ImageFormat` enum) | `ImageController.cs:552-570` |
| `MediaStream.DisplayTitle` is computed from other fields | `MediaStream.cs:265` |
| `PlaybackInfoDto` and `DeviceProfile` field sets (no `ResponseProfiles` in 12.1) | `PlaybackInfoDto.cs:14-89`; `MediaBrowser.Model/Dlna/DeviceProfile.cs:20-70` |
| Core MIME for `.ass`/`.ssa` is `text/x-ssa`; subtitle routes return `GetMimeType("file." + format)` | `MimeTypes.cs:82-83`; `SubtitleController.cs:188,261-274` |
| Plugin install checksum is MD5 | `InstallationManager.cs:586` |
| Jellyfin uses `BannedApiAnalyzers` with a `BannedSymbols.txt` | `Directory.Build.props:18`; `Directory.Packages.props:31` |
| From v1 (unchanged): `MediaInfoController` routes; subtitle and attachment `DeliveryUrl` forms; `.strm` → `ShortcutPath`; DTO `Path` exposure; session reporting without probing; jellyfin-web static URL building and error mapping; `KillTranscodingJobs` with an empty session; NFO runtime dropped for videos; ffprobe not overwriting plugin-written runtime | v1 Appendix A references, re-checked where cited above |
| **Spike-verified (K§Evidence) and owner-verified (owner test 01):** filter fires before `MediaInfoController`; peer token 10× in a raw master, 0× after rewrite; Peer static and subtitle routes anonymous; plugin XML world-readable; web play, seek, forced Peer transcode and subtitles pass | `docs/spike-01-playbackinfo.md`; `docs/tests/owner-browser-test-01.md` |

**Unverified, to be settled in the lab:**
- `IAuthorizationService` default-policy evaluation inside a resource filter (K6)
- `EndpointDataSource` enumeration from a plugin filter at startup (K23)
- `IEventConsumer<UserCreatedEventArgs>` registered by a plugin (K10)
- whether deleting a user cascades to its devices (T-SEC-16)
- how long a session stays listed by `/Sessions` after activity (foreign-device watch, K20)
- `DisplayMessage` support in Moonfin, Android, Swiftfin and TV clients (T-PLAY-16)
- MVC filter coverage of the additional core routes in 6.1; `IStartupFilter` at runtime (contingency only)
- `ReportFileSystemChanged` with realtime monitoring off; ~~empty `TypeOptions` arrays~~ (v2.4: settled from source by SR§5, an empty list on an existing entry means none; T-SYNC-13 confirms in the lab)
- native clients with rewritten PlaybackInfo; `.invalid` resolution latency; user data retention when parking
- Tailscale LocalAPI fields; SOCKS5 remote DNS for MagicDNS names in .NET
- which real clients break under `Enforce` (DLNA, legacy `api_key` apps)
- `UpdateToRepositoryAsync` on stub items surviving later scans (K12)

## Appendix B: spike deltas

**Keep from the spike** (proven): the `IAsyncResourceFilter` registration; header-only peer auth with `AllowAutoRedirect=false` and `AutomaticDecompression=None`; the fail-closed canary (now on label boundaries); the DirectStream safety rule; `RequestAborted` cancellation; logging only ids, status codes and exception type names.

**Must not survive into the MVP** (W§B checklist; the replacing task deletes it):

| # | Spike pattern | Replaced by | Task |
|---|---|---|---|
| 1 | `PlaylistRewriter.RewriteUri` and the `URI=` regex (W§B-1) | closed grammar (6.4) | K16 |
| 2 | Every `ReadAsStringAsync` / unbounded `CopyToAsync` on peer content (W§B-2) | `BoundedReadStream`, banned APIs | K3 |
| 3 | Copying the peer's `Content-Type` (W§B-3) | `ResponseWriter` | K7 |
| 4 | Forwarding the client query, body or headers (W§B-4) | 6.2.1, 6.11 | K14, K17 |
| 5 | `Shoal/Proxy/{itemId}/{**path}` catch-all | grants and `/Shoal/Hls/...` | K17 |
| 6 | `GetAuthorizationInfo`-based identity (W§B-6) | CallerContext | K6 |
| 7 | Empty-StubRoot fallback and the `federation://` stub format (W§B-5) | Shoal index and the sentinel | K12 |
| 8 | `PeerConfiguration.Token` in the XML and page code that loads it (W§B-7) | SecretStore, admin API | K1, K2, U1 |
| 9 | Path-string route matching (W§B-8) | template table | K13 |
| 10 | Absolute `Http` `Path` from `Request.Host` (W§B-10) | `Protocol=File`, `Path=null` | K14 |
| 11 | Denylist field rewriting in `PlaybackInfoRewriter` (W§B-10) | whitelist construction (6.2.3) | K14 |
| 12 | `Math.random` DeviceId (W§B-12) | server-derived DeviceId | K5 |
| 13 | NFO `<runtime>` (ignored by core, gap d) | `StubEnricher` | K12 |
| 14 | "Re-authenticate on 401" suggestion | `AuthFailed`, then re-pair | K20 |
| 15 | API-key callers falling through to core; non-static `stream.ext` forwarded; no session cleanup; 30 s header timeout with no failure memory | 6.1, 6.8, 6.10 | K13, K19, K3 |

## Appendix C: hostile fixture catalog (from W§ Appendix B, numbered)

**Playlist lines (F-PL).** Each must yield 502 or the line dropped, no outbound request to the named host, and no hostile substring in the output (6.4).

| ID | Input | Expected rule |
|---|---|---|
| F-PL-01 | `https:/collect.evil.example/s.ts` | URI line, no rule matches → 502 |
| F-PL-02 | `https:collect.evil.example/s.ts` | 502 |
| F-PL-03 | `HTTPS:/h/x` | 502 |
| F-PL-04 | ` data:text/plain,x` (leading space) | 502 |
| F-PL-05 | `javascript:alert(1)` | 502 |
| F-PL-06 | `//h/x` | 502 |
| F-PL-07 | `/\h/x` | 502 |
| F-PL-08 | `%2e%2e/%2e%2e/Items/x/Download` | 502 |
| F-PL-09 | `hls1/main/1.ts?next=http://evil/` | `Q` excludes `:` and `/` → 502 |
| F-PL-10 | `#EXT-X-DATERANGE:ID="a",X-ASSET-URI="https:/h/p.m3u8"` | tag not allowed → dropped |
| F-PL-11 | `#EXT-X-DATERANGE:ID="b",X-ASSET-LIST="https://h/l.json"` | dropped |
| F-PL-12 | `#EXT-X-CONTENT-STEERING:SERVER-URI="https://h/s"` | dropped |
| F-PL-13 | `#EXT-X-SESSION-DATA:DATA-ID="a",URI="https:/h/x"` | dropped |
| F-PL-14 | `#EXT-X-KEY:METHOD=AES-128,URI="https://h/k"` | 502 |
| F-PL-15 | unquoted `URI=` in `EXT-X-MAP`; unquoted `URI=` in `EXT-X-MEDIA` | MAP: 502; MEDIA: dropped |
| F-PL-16 | duplicate `URI=` attributes in `EXT-X-MAP` and `EXT-X-MEDIA`; duplicate attribute in `STREAM-INF` | MAP: 502; MEDIA: output built from grant data only; STREAM-INF: 502 |
| F-PL-17 | TAB, lone CR, or U+2028 inside a URI | 502 |
| F-PL-18 | a 1 MB line | 502 |
| F-PL-19 | 300,000 lines | 502 |
| F-PL-20 | `http://evil/seg.ts` | 502 |
| F-PL-21 | `../x` | 502 |
| F-PL-22 | `/abs` | 502 |

**Responses (F-RS).** Each must be rejected or neutralized (6.10, 8.6).

| ID | Input |
|---|---|
| F-RS-01 | `Content-Type: text/html` with a script body, on static, segment, subtitle and attachment routes |
| F-RS-02 | `Content-Type: image/svg+xml` with a script, same routes |
| F-RS-03 | `Content-Type: application/json; charset=bogus` on API calls |
| F-RS-04 | chunked, endless body |
| F-RS-05 | 1 byte per minute trickle |
| F-RS-06 | `Content-Length` larger than the body |
| F-RS-07 | `Location` header on a 200 |
| F-RS-08 | `Set-Cookie` |
| F-RS-09 | `302` to `http://169.254.169.254/` |

**Metadata (F-MD).**

| ID | Input |
|---|---|
| F-MD-01 | `MediaStreams[].Title` and `DisplayTitle` = `<img src=x onerror=alert(1)>` |
| F-MD-02 | control and bidi characters in stream and item fields |
| F-MD-03 | a 100 KB field value |
| F-MD-04 | IMDb id `tt0133093"><img src=x onerror=alert(1)>` |
| F-MD-05 | TMDb id `603 "onmouseover=alert(1)` |
| F-MD-06 | `OfficialRating` `G` on an adult title |

**Capacity (F-CAP).**

| ID | Input |
|---|---|
| F-CAP-01 | 50,000 titles with 10 MB images each (run with a lowered quota) |
| F-CAP-02 | 15 unique actor names per title |
| F-CAP-03 | 49,000 new titles in one sync |
| F-CAP-04 | a WebP image |
| F-CAP-05 | an 8000 × 8000 PNG |

**Pairing and auth:** T-PAIR-6, T-PAIR-7, T-SEC-15, T-SEC-16, T-SEC-17 (Warden's ids, kept in 11.3).
