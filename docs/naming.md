# Naming: replacing "Jellyfin Federation"

Owner: Lumen (branding). Date: 2026-10-14. Status: proposal, nothing registered or created.

## Why rename at all
The working title is already taken in this exact niche:
- `Angablade/JellyfinFederationPlugin` (72 stars): "allows multiple Jellyfin servers to federate, sharing media libraries"
- `MaxBly/JellyFed`: "Native Jellyfin federation plugin, share libraries between Jellyfin instances"
- Codeberg `BenjaminV/JellyFinFederationPlugin` (work in progress)
- Adjacent: Jellyswarrm (reverse proxy across several Jellyfin servers), which also rules out "swarm" names.

A Jellyfin core team member pointed to the Angablade plugin and Jellyswarrm as the existing federation efforts, so a "Federation" name would be confused with them on day one. The name should also follow the "X for Jellyfin" form, not lead with "Jellyfin", so it doesn't read as official.

## What the name has to do
- Sit in the dashboard nav and the plugin catalog like a first-party feature (Native Plus): one plain word, calm, no gimmick spelling.
- Say "friends sharing", while leaving room for the security-first story (scoped, revocable, nothing exposed by default).
- Work beyond Tailscale (public HTTPS peers are supported).
- Be searchable alongside "Jellyfin", with no collision in software or media.

## Ranked candidates

| # | Name | Why it fits | Tagline |
|---|---|---|---|
| 1 | **Shoal** (for Jellyfin) | A shoal is fish that swim together while each one stays independent. That is federation with no central server, and it suits Jellyfin's fish. One syllable, reads as native in the nav. | Your friends' libraries, next to yours. Nothing copied, nothing exposed. |
| 2 | **Interloan** (for Jellyfin) | Taken from "interlibrary loan": you borrow from another library under its rules, and the book goes back. That maps exactly to streaming a peer's title without copying it. Hard to confuse with anything else. | Interlibrary loan for your Jellyfin. Friends' shelves, your rules. |
| 3 | **Treaty** (for Jellyfin) | Pairing becomes an explicit agreement with set terms that either side can revoke. This is the most security-first of the set, though less warm. | Pair servers on terms you both set. Revoke them anytime. |
| 4 | **Borrow** (for Jellyfin) | Plain verb. It tells a non-technical friend exactly what happens: you borrow, you don't copy. | Watch a friend's movies without copying a file. |
| 5 | **Stoop** (for Jellyfin) | Neighbors sharing on the front step. Friendly and casual. | Share your shelf with the neighbors. |
| 6 | **Peerage** (for Jellyfin) | A pun on peers, meaning a server's circle of trusted peers. | Your server's circle of trusted peers. |
| 7 | **Tailfin** (for Jellyfin) | Tail(scale) plus (Jelly)fin, and a real word. Clever, but it ties the product to one network. | Jellyfin sharing, built for your tailnet. |
| 8 | **Potluck** (for Jellyfin) | Everyone brings a dish, or here a library. The warmest option, but it says nothing about security. | Everyone brings their library. |

## Availability checks (run 2026-10-14)

Method:
- GitHub: `gh search repos "<name>"`, `gh search repos "<name> jellyfin"`, and an exact-name search for `jellyfin-plugin-<name>` and `<name>-for-jellyfin`.
- Jellyfin plugins: grep of the official catalog `repo.jellyfin.org/files/plugin/manifest.json` (36 plugins) and the `awesome-jellyfin` README, plus a web search for "<name> jellyfin plugin" where noted.
- Brands and trademarks: web search, which surfaces third-party aggregators (Justia, TrademarkElite, GleanMark). This is not a direct USPTO, EUIPO or UK IPO search.

| Name | GitHub | Jellyfin plugin clash | Brand / trademark | Risk |
|---|---|---|---|---|
| Shoal | Unrelated repos (cazala/shoal 248 stars, agents; BT10011/shoal 64 stars, terminal LAN discovery, which is networking-adjacent but not media). No `<name> jellyfin` hits. `jellyfin-plugin-shoal` is free. The GitHub user `shoal` is taken, which only matters if you wanted that handle. | None (catalog, awesome list, web search) | SHOAL application (serial 99750992, filed Apr 2026) by Shoal Outdoors LLC covers apparel and sporting goods, not software. SHOALID (software) is a different word. Shoals Technologies is solar hardware. No software or media "Shoal" found. | Low |
| Interloan | Only tiny library-tool repos (0 stars). `jellyfin-plugin-interloan` is free. The GitHub org `interloan` exists (library-related). | None (catalog, awesome list) | No INTERLOAN mark found. It is a generic library term in NZ and AU, so it is weak to register but safe to use. | Low |
| Treaty | Unrelated repos (rules engine, smart contracts, under 20 stars). `jellyfin-plugin-treaty` is free. | None (catalog, awesome list) | Only TREATY NAVIGATOR (tax software, registered 2013), where "treaty" is disclaimed. | Low |
| Borrow | Crowded with Rust "borrow checker" noise, which hurts search. `jellyfin-plugin-borrow` is free. | None (catalog, awesome list) | BORROW application (2021, Galen Howlett) for P2P household-lending software. Same class, different goods. BORROWED (2025) is a lending app. | Medium |
| Stoop | Unrelated repos. `jellyfin-plugin-stoop` is free. | None (catalog, awesome list) | Several STOOP software filings: Stoop Social (rentals, 2019), Stoopi LLC (second-hand goods apps, 2025, likely active), First Code (cancelled), M&S (abandoned). There is also a "Stoop" iOS app. | Medium |
| Peerage | mrluc/peerage (398 stars, Elixir node clustering) is in the same "peer discovery" vocabulary. `jellyfin-plugin-peerage` is free. | None (catalog, awesome list) | No PEERAGE mark found. Carries an aristocracy connotation. | Medium (confusion, not legal) |
| Tailfin | Small unrelated repos. `jellyfin-plugin-tailfin` is free. | None (catalog, awesome list) | Tailfin is an established UK bikepacking brand (acquired by MVC Group, Sept 2026, about EUR 10M revenue). "Tail-" is Tailscale's own naming family (tailnet, Taildrop), and Tailscale is a registered trademark, so the name implies an affiliation. | High |
| Potluck | bsdpot/potluck (48 stars), inkandswitch/potluck (92 stars). `jellyfin-plugin-potluck` is free. | None (catalog, awesome list) | Potluck, Inc. holds POTLUCK marks for mobile app software (2021, 2023). A 2025 POTLUCK SaaS filing covers "community-based food sharing". There is also an older Potluck link-sharing app. | High |

Screened out before ranking:
- **Jellyfin Federation, JellyFed**: taken (see above).
- **Causeway**: Apache Causeway is an ASF trademark ("Apache Causeway™").
- **Ferry**: crowded (gql-dart/ferry 635 stars, lanyulei/ferry 6.6k stars).
- **Mutual**: too generic to search.
- **Pier**: crowded.
- **Swarm**: Jellyswarrm.
- **Medusa**: pymedusa, a media PVR.

## Recommendation: Shoal

**Shoal for Jellyfin.**
- Plugin repo: `jellyfin-plugin-shoal`.
- Catalog description: "Share libraries between friends' Jellyfin servers."

Why Shoal over the runner-up:
- It is the only candidate that is short, warm and clean all at once.
- The metaphor carries the architecture: independent servers moving together, nobody in charge.
- Interloan is a strong second if the owner prefers a name that explains itself to librarians and grown-ups more than one that sounds like a product.

Native Plus note: keep the page title as the product name ("Shoal"). Also keep the descriptive status line under it, so an admin who has never heard the name still knows what the page does.

## Not verified
- No direct USPTO, EUIPO, UK IPO or WIPO search. Trademark findings come from aggregator pages and may lag. Run a live USPTO search (Class 9 and 42) on the chosen name before release.
- App stores (Apple, Google Play) were not checked.
- Domains were not checked.
- Third-party Jellyfin plugin repositories outside the official catalog and the awesome-jellyfin list were only covered by GitHub and web search, not exhaustively.
- Web "<name> jellyfin plugin" searches were run for Shoal only. The others rely on GitHub plus catalog and awesome-list checks.

## Sources
- [Angablade/JellyfinFederationPlugin (listing)](https://oosmetrics.com/repo/Angablade/JellyfinFederationPlugin)
- [Jellyfin forum: federation system thread](https://forum.jellyfin.org/t-federation-system)
- [Codeberg: Federation plugin for JellyFin](https://codeberg.org/BenjaminV/JellyFinFederationPlugin)
- [SHOAL application 99750992 (TrademarkElite)](https://www.trademarkelite.com/trademark/trademark-detail/99750992/SHOAL)
- [SHOALID LLC trademarks (Justia)](https://trademark.justia.com/owners/shoalid-llc-5067028)
- [TREATY NAVIGATOR (Justia)](https://trademarks.justia.com/852/20/treaty-85220190.html)
- [Borrowed Inc. trademarks (Justia)](https://trademark.justia.com/owners/borrowed-inc-6201264)
- [Howlett BORROW filing (Justia)](https://trademark.justia.com/owners/howlett-galen-alexander-5060880)
- [Stoop Social trademarks (Justia)](https://trademarks.justia.com/owners/stoop-social-llc-4088863)
- [Stoopi LLC trademarks (Justia)](https://trademarks.justia.com/owners/stoopi-llc-6187786)
- [Potluck, Inc. trademarks (Justia)](https://trademark.justia.com/owners/potluck-inc-2664346)
- [MVC Group acquires Tailfin (Business Wire)](https://secure.businesswire.com/news/home/20260922606435/en/MVC-Group-Acquires-Tailfin-Entering-the-Premium-Bikepacking-Sector)
- [Tailscale Inc. trademarks (Justia)](https://trademarks.justia.com/owners/tailscale-inc-4352296)
- Official plugin catalog: https://repo.jellyfin.org/files/plugin/manifest.json
- awesome-jellyfin: https://github.com/awesome-jellyfin/awesome-jellyfin
