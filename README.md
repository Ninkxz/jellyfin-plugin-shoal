# Shoal

Watch your friends' Jellyfin libraries from your own server, without copying anything. Their titles show up in your Jellyfin like any other, play in Moonfin and jellyfin-web, and your friend's server does the heavy lifting.

## Best with Moonfin

Shoal works extremely well with [Moonfin](https://github.com/Moonfin-Client), and it is the client we recommend. It is a much better experience than the stock Jellyfin apps, and you can connect Seerr inside the app to request titles, so a friend's title you like can be requested for your own server in a few taps. jellyfin-web and the other Jellyfin clients are still supported: friends' titles show up and play there like any other title.

## Install

1. In Jellyfin, open **Dashboard**, **Plugins**, **Repositories**, click **+**, and add this address:
   `https://raw.githubusercontent.com/Ninkxz/jellyfin-plugin-shoal/main/manifest.json`
2. Open the **Catalog**, find **Shoal**, click **Install**, and restart Jellyfin.
3. Open **Dashboard**, **Shoal** and follow the steps. Design notes are in `docs/DESIGN.md`.

## Both servers need Shoal

Shoal has to be installed on **both** servers: yours, and your friend's. Install it as described above, then restart Jellyfin.

Pairing is by invite only. The server that shares makes an invite in Shoal ("Share with a friend") and sends it over a private channel. The server that watches pastes it, reads out a short safety code, and the friend types that code to approve. Nobody has to make an account or turn on anything else in Jellyfin. If you try to pair with a server that has no Shoal, the wizard says so and offers a message you can copy to your friend.

Servers that were paired the old way, before this change, keep their libraries and titles, but they are paused until you pair them again: their card shows "Needs pairing again" and a **Pair again with an invite** button. When you re-pair, your friend should also delete the old sharing account on their server.

## Protect your shared videos

Jellyfin serves some video and subtitle addresses without a sign-in. Shoal can require a sign-in for the libraries a server shares. On a **new** install it starts that way ("Require sign-in"). A server that already had Shoal keeps its current mode; its Settings page shows a one click "Require sign-in" card with what the last 7 days would have blocked. "Log only" is still there if an old app or a DLNA player breaks.

## Slow friend connections

Some friends have a slow upload. Shoal helps in two ways. **Fit quality to the link** (on by default, Settings) measures the speed to each friend's server and asks it for a quality the link can carry, about two thirds of the measured speed, so playback does not stall. **Video chunks to load ahead** (Settings, 2 to 20, default 6) makes Home fetch the next pieces of a converted stream from the friend's server a little before your player asks, and keep them in memory, so a slow or uneven link is smoothed out. It uses a small, fixed amount of memory on your server, never starts before playback is under way, does not start after you skip ahead, and stops when you stop. Direct play streams are not buffered this way. If playback still stalls, lower the quality in your player or ask your friend for a faster upload.

## Offline friends

When a friend's server is offline, playing one of their titles shows a short video named "Server offline" instead of a bare "Playback error". It says that the friend's server is down, that this is not a problem with your server, and that you can request the title on Seerr (or the request service you set) so it is downloaded to your own server. This is recommended; the video can't be clicked, so set the address of your request service in Settings and it is shown in the pop up message too. Turn the video off in Settings ("Offline card") to get the plain refusal back. The video is made once per friend name with Jellyfin's own ffmpeg and needs a font on the server; if it can't be made you get the plain refusal.

You can also hide a server's libraries while it is offline; see "Hiding a server that stays offline" below.

## Notes for operators

- **Transcode limit (Warden L7).** The per-share transcode limit covers sharing accounts only, and only a request that names a `PlaySessionId`. Anonymous transcodes (the routes core serves without a sign-in) are not counted when the hardening mode is `LogOnly` or `Off`; `Enforce` refuses them without a sign-in. Treat the limit as a courtesy to the host, not a hard cap.
- **How friends reach your server: HTTPS first, Tailscale also supported (Warden L8).** The recommended way is an https address with a real certificate, for example `https://movies.example.com`. Tailscale is also supported: on a Tailnet plain http is fine, because Tailscale encrypts the link. Home refuses to redeem an invite over cleartext unless the address is on a Tailnet, so a plain-HTTP address outside Tailscale cannot be paired. The Peer cannot check that the invite request reached it over HTTPS: the token is handed to Home in the answer to `POST /Shoal/Pair`. If the Peer sits behind a reverse proxy, serve it over TLS only and do not expose a plain-HTTP port.
- **Rate limits behind a reverse proxy (Warden 12 L2).** Shoal's anonymous endpoints (`GET /Shoal/Ping` at 20 per minute per address and 300 per minute overall, and the invite limiter of `POST /Shoal/Pair`) count the address Jellyfin resolves for the client. If Jellyfin sits behind a reverse proxy that is not listed under **Dashboard > Networking > Known proxies**, every client looks like the proxy, so all visitors share one budget: a handful of requests can then make the ping answer `429`, which a friend's Shoal shows as "ping throttled" (not as "no Shoal"). Add the proxy under Known proxies. IPv6 clients are counted per /64 network. With "Let friends' servers detect Shoal" off, the ping always answers the plain `404` and is never limited.

## Hiding a server that stays offline

Off by default (Settings, "Hide a server's libraries while it's offline"). After the server has been offline for the number of hours you set (fractions allowed, 0.5 is half an hour), Shoal takes its libraries off the library lists of people who have an explicit list. Nothing is deleted and no folder moves, so watch history stays. They come back after the server has been online for 10 minutes. People who see all libraries still see its titles. To change who can see a hidden server, use its access setting; Shoal puts back only what it removed, so a library you tick by hand on a profile in the meantime may be taken out again when the server returns. Turning the setting off, or disabling or removing the server, shows everything again.
