/*
 * Shoal admin page controller.
 *
 * ES module. Jellyfin loads it as configurationpage?name=Shoal.js (ui-spec 1) and calls the
 * default export with (view, params). Everything the admin sees is rendered from here.
 *
 * Rules this file keeps (ui-spec 1.3, DESIGN 9.1; tests/AdminUiResourceTests.cs checks them):
 *  - No HTML-string APIs. DOM comes from <template> elements in shoal.html and createElement,
 *    and every string from the server is set with textContent or setAttribute.
 *  - No style attributes and no inline handlers. Geometry goes through CSS custom properties
 *    (style.setProperty('--shoal-...')) and SVG attributes.
 *  - All copy lives in STRINGS. Peer-supplied text (server, library, title, device names) is
 *    text only, never interpreted.
 *  - The page only talks to Shoal/Admin/* (JSON). It never reads plugin configuration and never
 *    receives tokens, invite secrets after first display, or peer GUIDs.
 *  - Nothing here knows about test data. The development harness in ui-dev/ provides a fake
 *    ApiClient; this file contains no alternate data source.
 */

/* ====================================================================================== */
/* Copy (ui-spec 8 and 11). Plain words: server, friend, sharing account, safety code.    */
/* Array values are [one, other] picked by {n}. Keep this file ASCII: use \u escapes.     */
/* ====================================================================================== */

const STRINGS = {
    // ---- pairing v2 (docs/ui-spec-pairing-v2.md): every line of the new pair and share flows. No dashes in any of it. ----
    installTitle: "This server doesn't have Shoal yet, or has an older Shoal",
    installBody: "Both servers need Shoal. Ask your friend to install it from the link below, then check again.",
    installMessage: "Hi! I'd like to watch some of your movies through Shoal. Both of our Jellyfin servers need the Shoal plugin. You can install it here: {url} After that, open Shoal in your Jellyfin dashboard, choose Share with a friend, and send me the invite. My server is called {home}.",
    installMessageLabel: "Message to your friend",
    checkAgain: "Check again",
    pv2CodeNumber: "That's a number, not an invite or a safety code. An invite starts with shoal1. A safety code is 8 letters and numbers, like K7QM 2XA4.",
    needsRepairTitle: "Needs re-pair by invite",
    needsRepairBody: "{server} was paired the old way, which Shoal no longer supports, so it is paused. Its titles stay listed. Ask your friend to install Shoal and send an invite, then re-pair.",
    repairWithInvite: "Re-pair with an invite",
    repairBlockedChip: "Needs a safer address",
    repairBlockedHint: "Switch this server to Tailscale or https, then pair it again, or remove it.",
    repairBlockedTitle: "This server cannot be paired again as it is",
    repairBlockedBody: "{server} is on plain http outside Tailscale, and invites are refused there. Switch this server to Tailscale or https, then pair it again, or remove it.",
    changeAddressSteps: "Change the address: give your friend's server a Tailscale name or an https address, then add it again with an invite.",
    removeOrChangeAddress: "Remove or change the address",
    chipNeedsRepair: "Needs re-pair by invite",
    hintNeedsRepair: "{server} was paired the old way and is paused. Re-pair it with an invite.",
    legacyBanner: ["{n} server was paired the old way and is paused. Re-pair it with an invite.", "{n} servers were paired the old way and are paused. Re-pair each with an invite."],
    out_unknown_title: "Pairing didn't finish",
    out_unknown_body: "Something stopped this pairing. Nothing was saved. Start again with a new invite.",
    pv2PageShare: "Share with a friend",
    pv2StepAddress: "Server address",
    pv2StepInvite: "Get an invite",
    pv2StepCode: "Read the code",
    pv2StepReach: "How your friend reaches you",
    pv2StepWait: "Wait for your friend",
    pv2WhoYou: "You do this",
    pv2WhoFriend: "Your friend does this",
    pv2WhoWait: "Shoal does this, just wait",
    pv2SayLead: "Say to your friend:",
    pv2SayLeadTo: "Say to {friend}:",
    pv2CopyMessage: "Copy message",
    pv2Copied: "Copied",
    pv2Under3: "Under 3 minutes left.",
    pv2Under3Invite: "If this is not enough time, ask your friend for a new invite.",
    pv2Under3Code: "If this is not enough time, cancel and get a new code.",
    pv2Under3Share: "If {friend} needs more time, make a new invite.",
    // where each code goes
    pv2CodesTitle: "Where each code goes",
    pv2WhereLink: "Where does each code go?",
    pv2ShowCodes: "Show where each code goes",
    pv2WhoMakes: "Made by:",
    pv2WhoTypes: "Typed by:",
    pv2WhereGoes: "Goes in:",
    pv2CodeRowInvite: "Invite",
    pv2LooksInvite: "a long text starting with shoal1",
    pv2CodeRowSafety8: "Safety code, 8 characters",
    pv2LooksSafety8: "K7QM 2XA4",
    pv2MadeByFriendShoal: "Your friend, in their Shoal",
    pv2MadeByFriendShoalShows: "Your friend's Shoal shows it to them",
    pv2MadeByYouHere: "You, on this page",
    pv2MadeByThisPage: "This page shows it to you",
    pv2TypedByYou: "You",
    pv2TypedByFriend: "Your friend",
    pv2GoesInviteBox: "The Invite box in this wizard",
    pv2GoesFriendInviteBox: "The Invite box in their Shoal",
    pv2GoesFriendApprove: "On their Shoal, in the approval box",
    pv2GoesApproveBox: "The approval box on this page",
    // step 1 and the route question
    pv2DirTitle: "What do you want to do?",
    pv2DirWatch: "Watch a friend's library",
    pv2DirWatchBody: "Their movies show up in your library. They keep the files.",
    pv2DirShare: "Share my library with a friend",
    pv2DirShareBody: "Your movies show up in their library. Your friend needs Shoal too.",
    pv2DirBoth: "To do both, pair once each way.",
    pv2ChooseOne: "Choose one to continue",
    // step 3, the address
    pv2AddrTitle: "What is your friend's server address?",
    pv2NameHelp: "Your users see this in library names, like Hops Movies.",
    pv2AddrHelp: "Ask your friend to send it. For example http://myserver.tail1234.ts.net:8096",
    pv2ReachPrivate: "Private address (home network or Tailscale router)",
    pv2NetHelpPrivate: "Not encrypted. Works only if your two servers share a network or Tailscale routes it. A Tailscale name is easier.",
    pv2NetChose: "Shoal chose this from the address. Change it if it's wrong.",
    pv2AddrTailscale: "Looks like a Tailscale address. Both servers need Tailscale running.",
    pv2AddrPrivate: "This is a private address inside your friend's network. It only works from here if your servers share a network, or Tailscale routes it. Ask your friend for their Tailscale name if you can.",
    pv2AddrSelf: "That points to this server itself. Use your friend's address.",
    pv2ShAddrPrivate: "This is a private address inside your own network. It only works for {friend} if the two servers share a network, or Tailscale routes it. A Tailscale name or an https address is easier.",
    pv2ShAddrSelf: "localhost and 127 addresses only work on this server. Send an address {friend} can reach.",
    pv2AddrHttpsNumbers: "Addresses made of numbers rarely have a valid https certificate, so the check may fail. A name works better.",
    pv2AddrHttps8096: "Jellyfin uses plain http on port 8096 unless your friend turned on https there. If the check fails, try http, or ask for their https address.",
    pv2AddrHttpPublic: "Over the internet the address must start with https://.",
    pv2AddrInternet: "Looks like an internet address. It needs a valid https certificate.",
    pv2AddrTrimmed: "Shoal removed {path}. Only the start of the address is needed.",
    pv2AddrNoScheme: "Start the address with http:// or https://.",
    pv2AddHttp: "Add http://",
    pv2ErrHttpsOnly: "Internet servers need an address that starts with https://. A DMZ or port forward on their router does not change this.",
    pv2ErrUnreachable: "Shoal could not reach that address from this server.",
    pv2Unreach_1: "Check the address for typing mistakes.",
    pv2Unreach_2: "If the address is https, try http with the same address, or the other way round.",
    pv2Unreach_3: "If your friend uses Tailscale, check that Tailscale is running on both servers.",
    pv2Unreach_4: "A private address only works if your servers share a network or Tailscale routes it.",
    pv2OpenHelper: "Open the connection helper",
    pv2ErrCert: "That server's https certificate isn't trusted, so Shoal won't connect. A name usually has a valid certificate. An address made of numbers almost never does. Ask your friend for a name, or use Tailscale.",
    pv2ErrNotJellyfin: "That address answered, but it isn't a Jellyfin server. Check the port. Jellyfin usually uses 8096.",
    pv2HelperTitle: "Not sure which address to use?",
    pv2HelperLead: "The address must be one that works from your server, not from your friend's living room.",
    pv2HelperSay: "Please send me the address of your Jellyfin that I can reach from my server. If you use Tailscale, send the Tailscale name.",
    pv2Setup1Title: "Your friend shared their server with you in Tailscale",
    pv2Setup1Body: "Use the Tailscale name of the Jellyfin machine, or its Tailscale address that starts with 100, for example http://myserver.tail1234.ts.net:8096. Plain http is fine here, Tailscale encrypts it. The Jellyfin computer needs to be its own Tailscale machine. Choose Tailscale.",
    pv2Setup2Title: "Your friend shares their home network through a Tailscale router",
    pv2Setup2Body: "You can use their home address, for example http://10.0.0.20:8096. This only works if Tailscale on your server accepts that route, and your friend approved it. Choose Private address. If it doesn't work, the easy fix is to install Tailscale on the Jellyfin computer itself.",
    pv2Setup3Title: "Your friend has a public https address",
    pv2Setup3Body: "Use the name, for example https://movies.example.com. It must start with https:// and have a real certificate. Usually there is no port number. Choose Internet.",
    pv2Setup4Title: "Your friend put their server in a DMZ or opened a port on the router",
    pv2Setup4Body: "That is a router setting. Shoal does not need to know about it. What matters is the address you can reach: a public https name, or Tailscale. Plain http over the open internet is not accepted. Choose Internet and use https.",
    pv2SetupClose: "Same house? Use the home address, for example http://192.168.1.20:8096, and choose Private address.",
    pv2UseSetup: "Use this setup",
    // step 4, the check
    pv2CheckTitle: "Is this the right server?",
    pv2ServerIdNote: "Shoal uses the Server ID later to notice if this address starts leading somewhere else.",
    pv2HasShoalYes: "Shoal is installed there. Good.",
    pv2YesThisIsIt: "Yes, this is it",
    pv2NoChangeAddress: "No, change the address",
    // invite route
    pv2InviteTitle: "Ask your friend for an invite",
    pv2InviteStep_1: "Open Shoal in the Jellyfin dashboard, under Plugins.",
    pv2InviteStep_2: "Choose Share with a friend.",
    pv2InviteStep_3: "Pick the libraries to share and choose Continue.",
    pv2InviteStep_4: "Choose how your server is reached, then choose Create invite.",
    pv2InviteStep_5: "Send the invite and their server address to you.",
    pv2InviteSay: "I'm ready for the invite. Please make it now, because it only lasts 15 minutes.",
    pv2InviteLabel: "Invite",
    pv2InviteHelp: "Paste it here. It starts with shoal1.",
    pv2InviteFor: "Your invite is for {address}.",
    pv2InviteLeft: ["This invite has about {m} minute left.", "This invite has about {m} minutes left."],
    pv2PasteToContinue: "Paste the invite to continue",
    pv2ErrInviteEmpty: "Paste the invite your friend sent.",
    pv2ErrInviteOther: "That invite is for a different server. Check that you are using the address your friend sent with the invite.",
    pv2ErrInviteGone: "That invite no longer works. It lasts 15 minutes and works once. Ask your friend to choose Create a new invite, then paste it here.",
    pv2ErrInviteLan: "Invites need Tailscale or an https address so nobody on the network can copy them. There is no other way to pair over plain http. Use a Tailscale name or an https address instead.",
    pv2ChangeAddress: "Change the address",
    pv2CodeTitle: "Read this code to your friend",
    pv2CodeBody: "Your friend types this code into the box on their Shoal. It proves you are talking to the right person.",
    pv2GettingCode: "Getting the code from {server}. This takes a few seconds.",
    pv2CodeSay: "Open Shoal. There is a request from {home}. Type this code when it asks. I'll read it slowly: {code}.",
    pv2NoRequestTitle: "My friend doesn't see a request",
    pv2NoRequestBody: "Make sure they opened Shoal on {server} and chose the request from {home}. If there is nothing there, cancel and check the address with them.",
    pv2ApprovedConnecting: "Approved. Connecting…",
    // Quick Connect route
    // code recognition
    pv2CodeSafety: "That's a safety code. It's read out and typed on your friend's Shoal. It does not go in this box.",
    pv2CodeAddress: "That's an address. It goes in Server address on the earlier step.",
    pv2GoToAddress: "Go to the address",
    pv2MoveInvite: "Move it to the Invite box",
    pv2CodeInviteElsewhere: "That's an invite. It goes in the Invite box on the invite step.",
    pv2CodeCutOff: "That invite looks cut off. Copy the whole text, from the first letter to the end.",
    pv2CodeOther: "That doesn't look like an invite. An invite starts with shoal1.",
    pv2CodeSafetyShape: "A safety code is 8 letters and numbers, like K7QM 2XA4.",
    // recovery cards
    pv2Kept: "Kept: {what}",
    pv2KeptWatch: "the name {name}, the address {url} and the route",
    pv2KeptWatchNoUrl: "the name and the address",
    pv2KeptShare: "{friend}, the libraries and the address",
    pv2RanOutInviteTitle: "The invite ran out",
    pv2RanOutInviteBody: "Invites last 15 minutes and work once. Nothing was saved. Ask your friend to choose Create a new invite in Shoal, then paste it here.",
    pv2InviteDidntTitle: "That invite didn't work",
    pv2InviteDidntBody: "It may have run out, been used already, or been replaced by a newer one. Ask your friend for a new one.",
    pv2DeniedTitle: "Your friend chose Deny",
    pv2DeniedBody: "Nothing was saved. If that was a mistake, ask them for a new invite.",
    pv2MismatchTitle: "The safety code didn't match",
    pv2MismatchBody: "Your friend typed the wrong code 3 times, so their Shoal cancelled the request and the invite. Nothing was saved. Read the code again slowly, one group at a time. If it still fails, check the address with your friend.",
    pv2PasteNewInvite: "Paste a new invite",
    pv2WhoLineInvite: "Your friend makes a new invite, then you paste it.",
    pv2CodeCancelled: "The code was cancelled.",
    // share side
    pv2ShWhoTitle: "Who are you sharing with?",
    pv2ShNote: "Shoal makes a hidden sharing account for {friend}. It can only watch the libraries you pick, can't change anything, and you can remove it anytime.",
    pv2ShReachTitle: "How will {friend} reach your server?",
    pv2ShTs: "Through Tailscale. I shared this server with {friend}",
    pv2ShTsHint: "Their address will look like http://myserver.tail1234.ts.net:8096",
    pv2ShPublic: "Through a public https address",
    pv2ShPublicHint: "Their address will look like https://movies.example.com",
    pv2ShHome: "Through my home network or a Tailscale router",
    pv2ShHomeHint: "Use only if {friend} is on the same network or routes it. Invites do not work over plain http, so give {friend} a Tailscale name or an https address.",
    pv2ShHomeHttp: "That address is plain http. Invites need Tailscale or an https address, so {friend} could not use this invite. Use a Tailscale name or an https address instead.",
    pv2ShAddrLabel: "The address to send",
    pv2ShAddrHelp: "This is the address {friend} will type. It must work from their server.",
    pv2ShAddrHelpEmpty: "Type the address {friend} should use, for example https://movies.example.com",
    pv2ShHelperTitle: "Which address do I use?",
    pv2ShHelperLead: "The address must be one that works from {friend}'s server, not from your living room.",
    pv2ShSetup1Title: "I shared my server with {friend} in Tailscale",
    pv2ShSetup1Body: "Send the Tailscale name of this Jellyfin machine, or its Tailscale address that starts with 100, for example http://myserver.tail1234.ts.net:8096. Plain http is fine here, Tailscale encrypts it. This computer needs to be its own Tailscale machine. Choose Tailscale.",
    pv2ShSetup2Title: "I share my home network through a Tailscale router",
    pv2ShSetup2Body: "{friend} can use this server's home address, for example http://10.0.0.20:8096. This only works if Tailscale on their server accepts that route and you approved it. Choose the home network option. If it doesn't work, the easy fix is to install Tailscale on this computer itself.",
    pv2ShSetup3Title: "This server has a public https address",
    pv2ShSetup3Body: "Send the name, for example https://movies.example.com. It must start with https:// and have a real certificate. Usually there is no port number. Choose the public https option.",
    pv2ShSetup4Title: "I put this server in a DMZ or opened a port on my router",
    pv2ShSetup4Body: "That is a router setting. Shoal does not need to know about it. What matters is the address {friend} can reach: a public https name, or Tailscale. Plain http over the open internet is not accepted. Choose the public https option and use https.",
    pv2ShSetupClose: "Same house? Send the home address, for example http://192.168.1.20:8096, and choose the home network option.",
    pv2ShSayMaking: "I'm making the invite now. It lasts 15 minutes. Tell me when you're ready to paste it.",
    pv2ShSendTitle: "Send this to {friend}",
    pv2ShMessageLabel: "Message to send",
    pv2ShSecretWarn: "This message contains the invite. Send it privately, only to {friend}.",
    pv2ShMessage: "Shoal invite for you. Address: {address}. Invite: {invite}. It lasts 15 minutes. In Shoal, choose Pair a server, Yes they use Shoal, then paste the address and the invite. Your screen will show a code. Read it to me and I will type it.",
    pv2ShSendSay: "I sent you the message. When Shoal shows you a code, read it out to me.",
    pv2ShApproveTitle: "Type the code {friend} reads to you",
    pv2ShCodeLabel: "Safety code from {friend}",
    pv2ShRestarted: "{friend}'s server started this request again. Check the code with {friend} again.",
    pv2ShCodeHelp: "8 letters and numbers, like K7QM 2XA4. {friend} sees it on their screen.",
    pv2ShRanOutTitle: "The invite ran out",
    pv2ShRanOutBody: "Invites last 15 minutes and work once. Make a new one when {friend} is ready.",
    pv2ShMakeNew: "Make a new invite",
    pv2ShCreateNew: "Create a new invite",
    appName: "Shoal",
    navOverview: "Overview",
    navLibraries: "Libraries & access",
    navSettings: "Settings",
    loadingShoal: "Loading Shoal",
    loadFailed: "Shoal couldn't load. Your server didn't answer. Try again in a moment.",
    tryAgain: "Try again",
    genericFailure: "That didn't work. Try again in a moment.",
    needsNewerShoal: "This feature needs a newer Shoal. Update it in Dashboard, Plugins, Shoal, then restart Jellyfin.",
    versionLine: "Shoal version {version}",
    versionNewer: "A newer Shoal is available: {latest}. Update it in Dashboard, Plugins, Shoal, then restart Jellyfin.",
    unreadableAnswer: "Your server answered in a way this page could not read. The action may have worked: reload the page to see.",
    unexpectedError: "Something went wrong in this page. Reload it and try again. If it keeps happening, tell the developer: {detail}",
    removeBlockedSyncing: "Shoal is syncing {server} right now, so it can't be removed yet. Wait for the sync to finish, then press Remove server again.",
    removeBlockedPairing: "A pairing with {server} is in progress. Cancel it first (Pair a server, then Cancel pairing), then remove the server.",
    dedupNote: ["{count} title comes from {winner} instead (it wins when both have the same title). Set priority or remove it to use this server's copy.", "{count} titles come from {winner} instead (it wins when both have the same title). Set priority or remove it to use this server's copy."],
    dedupNoteHome: ["{count} title is your own copy instead (yours wins when both have the same title).", "{count} titles are your own copies instead (yours win when both have the same title)."],
    removeFailed: "Shoal couldn't remove the server. Nothing was changed. Try again in a moment.",
    ghostPairing: "This pairing is not there any more: the server it was for was removed. Start over to pair again.",
    startOver: "Start over",
    remoteUnreachable: "Shoal couldn't reach {server} just now, so it can't list its libraries. Check that the server is online, then try again.",
    saveFailed: "Couldn't save. Try again in a moment.",
    tooManyTries: "Too many tries. Try again in a few minutes.",
    noticeGone: "That server isn't paired anymore.",
    backOverview: "Overview",
    backServer: "Server",
    cancel: "Cancel",
    close: "Close",
    save: "Save",
    saved: "Saved",
    saving: "Saving…",
    applying: "Applying…",
    continue: "Continue",
    back: "Back",
    edit: "Edit",
    remove: "Remove",
    never: "never",
    unknown: "unknown",
    namesAnd: "{names} and {last}",
    namesAndMore: "{names} and {n} more",
    nLibraries: ["{n} library", "{n} libraries"],
    nDevices: ["{n} device", "{n} devices"],

    // units and time
    unitMbps: "{v} Mbps",
    unitMbpsShort: "Mbps",
    unitMs: "{v} ms",
    unitMsShort: "ms",
    unitTB: "{v} TB",
    unitGB: "{v} GB",
    unitMB: "{v} MB",
    unitKB: "{v} KB",
    justNow: "just now",
    minAgo: "{n} min ago",
    hoursAgo: ["{n} hour ago", "{n} hours ago"],
    yesterdayAt: "yesterday at {time}",
    forMin: "for {n} min",
    forHours: ["for {n} hour", "for {n} hours"],
    forDays: ["for {n} day", "for {n} days"],
    availableIn: "Available again in {n} min",
    expiresIn: "Expires in {time}",
    everyHours: ["{n} hour", "{n} hours"],

    // overview
    syncNow: "Sync now",
    syncing: "Syncing…",
    syncingNow: "Syncing now…",
    syncFailed: "Sync didn't start. Try again in a moment.",
    pairServer: "Pair a server",
    shareWithFriend: "Share with a friend",
    statusOnline: ["{online} of {total} server online", "{online} of {total} servers online"],
    needAttention: ["{n} needs attention", "{n} need attention"],
    titlesSharedWithYou: ["{count} title shared with you", "{count} titles shared with you"],
    titlesOfflineToo: ["{count} title shared with you, {off} more on servers that are offline", "{count} titles shared with you, {off} more on servers that are offline"],
    titlesOnlyPaused: ["Nothing available right now: {count} title on a paused server", "Nothing available right now: {count} titles on paused servers"],
    titlesOnlyOffline: ["Nothing available right now: {off} title on a server that is offline", "Nothing available right now: {off} titles on servers that are offline"],
    lastSync: "Last sync {when}",
    notSyncedYet: "Not synced yet",
    friendsServers: "Friends' servers",
    selectServerNote: "Select a server to see its speed below",
    noServersYet: "You aren't watching anyone's library yet.",
    emptyTitle: "Watch your friends' movies from your own server",
    emptyBody: "Shoal adds a friend's Jellyfin library next to yours. Nothing is copied, your friend doesn't need to install anything, and you choose who here can see it.",
    emptyStep1: "Your friend shares their server with you in Tailscale, or gives you its https address.",
    emptyStep2: "Pair: send them a code to approve, or paste the invite they sent if they have Shoal.",
    emptyStep3: "Check the safety code together, then try it yourself before you open it to everyone here.",
    recentlyShared: "Recently shared with you",
    seeAll: "See all",
    postersNone: "Nothing new lately.",
    postersNoneYet: "Nothing shared with you yet. Titles appear after the first sync.",
    posterMeta: "{year} · {runtime} min · from {server}",
    posterMetaYours: "Also on {server} · your copy plays",
    yourCopy: "Your copy",
    parkedTitles: "{server}'s titles are hidden while it's offline.",
    parkedBack: "They come back on their own.",
    friendsWatching: "Friends watching from you",
    friendsEmpty: "No one watches from your server yet. Share a library and it shows up on their server, next to theirs.",
    friendMeta: "{libs} · {devs} · last seen {when}",
    friendActive: "Active",
    friendWaiting: "Waiting for friend",
    friendPaused: "Paused",
    friendRemoved: "Access removed",
    unexpectedDevices: ["{n} unexpected device signed out", "{n} unexpected devices signed out"],
    playingNow: "Playing now: {n}",
    playingNowTranscoding: "Playing now: {n} ({t} transcoding)",

    // server cards
    noPictures: "No pictures yet",
    waitingFor: "Waiting for {server}",
    pillLatest: "Latest from {server}: {title}",
    viaTailscale: "via Tailscale",
    viaHome: "via home network",
    viaInternet: "via internet (HTTPS)",
    viaThroughProxy: "through proxy",
    metricConnection: "Connection",
    metricLatency: "Latency",
    metricSpeed: "Speed",
    connDirect: "Direct",
    connRelayed: "Relayed",
    connMaybeRelayed: "Maybe relayed",
    connProxy: "Proxy",
    titlesShared: ["**{count}** title shared", "**{count}** titles shared"],
    nextCheck: "Next check in {n} min",
    speedShownBelow: "Speed shown below",
    testLinkAgain: "Test link again",
    retryNow: "Retry now",
    announceTestStarted: "Testing the link. The result shows in about a minute.",
    announceRetryStarted: "Checking the server again. The result shows in about a minute.",
    retrying: "Retrying…",
    pairAgain: "Pair again",
    resume: "Resume",
    resuming: "Resuming…",
    pause: "Pause",
    pausing: "Pausing…",
    continuePairing: "Continue pairing",
    reviewCertificate: "Check certificate",
    thingsToCheck: ["{n} thing to check", "{n} things to check"],
    andKMore: " \u00b7 {k} more",
    pairingStoppedArt: "Pairing stopped",
    removalsOnHold: ["{count} removal on hold", "{count} removals on hold"],
    additionsOnHold: ["{count} new title on hold", "{count} new titles on hold"],
    chipOnline: "Online",
    chipRelayed: "Online, relayed",
    chipMaybeRelayed: "Online, maybe relayed",
    chipUnstable: "Online, unstable",
    chipOffline: "Offline",
    chipPaused: "Paused",
    chipAccessRemoved: "Access removed",
    chipDifferentServer: "Different server",
    chipPairing: "Pairing",
    chipPairingSafety: "Stopped for safety",
    chipPairingStopped: "Pairing stopped",
    chipSyncing: "Syncing",
    verdictOffline: "Offline {duration} · titles stay listed but won't play until it's back",
    verdictOfflineShort: "Offline · titles stay listed but won't play until it's back",
    verdictWord_smooth: "smooth",
    verdictWord_mayBuffer: "might pause",
    verdictWord_needsTranscode: "needs transcode",
    verdictWord_willBuffer: "will buffer",
    hintRelayed: "This friend is connected through a Tailscale relay, so speed is limited. Open UDP 41641 on one side.",
    hintMaybeRelayed: "This link may be relayed. To check, run tailscale ping {host} on your server.",
    hintUnstable: "The link is unstable ({jitter} ms jitter, {loss}% loss), so playback may pause.",
    hintPaused: "You paused {server}. Its titles stay listed but won't play.",
    hintAccessRemoved: "{server}'s admin removed your access, or the key stopped working. Pair again to keep watching.",
    hintDifferentServer: "The server at this address isn't the one you paired with, so Shoal stopped talking to it. If {server} was reinstalled, pair again and compare the safety code.",
    hintCertChanged: "{server}'s certificate changed, so Shoal stopped talking to it. That's normal after your friend renews it, and a warning sign if they didn't. Check with them before you trust it.",

    // announcements
    announceSyncStarted: "Sync started",
    announceSyncFinished: "Sync finished: {added} added, {removed} removed",
    announceNowState: "{server} is now {state}",
    announceManyChanged: "{n} servers changed status",
    announceShowingSpeed: "Showing link speed for {server}",
    announceCodeExpired: "The code expired",
    announceOneMinute: "1 minute left",
    announceFiveMinutes: "5 minutes left",
    announceRemoved: "{server} removed",
    announceResumed: "{server} resumed.",
    announceHoldReleased: "Done",
    announceCertTrusted: "New certificate trusted",
    announceFriendPaused: "{friend} paused",
    announceFriendResumed: "{friend} resumed",
    announceDeviceRemoved: "Device removed",
    announceAccessDeleted: "Access for {friend} deleted",
    announceDeletedAll: "All Shoal data deleted",
    copied: "Copied",

    // link speed panel and chart
    linkSpeed: "Link speed",
    serverLabel: "Server",
    timeRange: "Time range",
    range_24h: "24 h",
    range_7d: "7 days",
    range_30d: "30 days",
    rangeLabel_24h: "last 24 hours",
    rangeLabel_7d: "last 7 days",
    rangeLabel_30d: "last 30 days",
    testWord: ["test", "tests"],
    playWord: ["play", "plays"],
    speedSub_24h: "Last 24 hours · {tests} {testWord} and {plays} {playWord}, measured from your server",
    speedSub_7d: "Last 7 days · {tests} {testWord} and {plays} {playWord}, measured from your server",
    speedSub_30d: "Last 30 days · {tests} {testWord} and {plays} {playWord}, measured from your server",
    kpiNow: "Speed now",
    kpiLatency: "Latency",
    kpiStability: "Stability",
    kpiLowest_24h: "Lowest in 24 h",
    kpiLowest_7d: "Lowest in 7 days",
    kpiLowest_30d: "Lowest in 30 days",
    stabilityGood: "Good",
    stabilityBad: "{jitter} ms jitter · {loss}% loss",
    offlineWord: "offline",
    ladderTitle: "What plays from {server}",
    ladderClass_remux4k: "4K remux",
    ladderClass_remux1080: "1080p remux",
    ladderClass_4k: "4K",
    ladderClass_1080p: "1080p",
    ladderClass_1080pTranscode: "1080p transcode",
    ladderClass_720p: "720p",
    ladderSmooth: "Smooth",
    ladderMayBuffer: "Might pause at busy scenes",
    ladderNeedsTranscode: "Plays if {server} transcodes it",
    ladderWillBuffer: "Won't play smoothly · needs about {n} Mbps",
    bottleneck_peerUpload: "Limited by {server}'s upload, not your connection.",
    bottleneck_homeDownload: "Your server's download or local network is the likely bottleneck.",
    bottleneck_relay: "Relayed through Tailscale: expect lower speed and higher latency. Allow UDP 41641 or enable UPnP/NAT-PMP on one side.",
    bottleneck_relayRegion: "Relayed through Tailscale ({region}): expect lower speed and higher latency. Allow UDP 41641 or enable UPnP/NAT-PMP on one side.",
    bottleneck_peerTranscode: "{server}'s transcoding is too slow. Use direct play or a lower quality; {server} needs hardware acceleration.",
    bottleneck_perConnection: "A per-connection limit (latency or a relay) is holding single streams back.",
    bottleneck_proxyCpu: "The Tailscale proxy adds CPU overhead. If speed is low, check that container's CPU.",
    openDiagnostics: "Open full diagnostics",
    testNow: "Test now",
    testing: "Testing…",
    testFailed: "The test didn't run. Try again in a moment.",
    chartNoTests: "No speed tests yet.",
    chartFewTests: "Not enough tests yet. Shoal tests every 6 hours.",
    chartNoSamples: "No speed tests yet.",
    speedLoadFailed: "Couldn't load speed history.",
    speedOffline: "{server} is offline. Last measured {when}.",
    speedOfflineNever: "{server} is offline and hasn't been measured yet.",
    chartThr4k: "4K needs about 30 Mbps",
    chartThr1080: "1080p needs about 15 Mbps",
    axis24Ago: "24 h ago",
    axisHoursAgo: "{n} h",
    axisNow: "Now",
    chartOfflineBand: "Offline",
    chartTitle: "Speed from {server}, {range}",
    showAsTable: "Show as a table",
    tableSpeedCaption: "Speed tests, {range}",
    chartSummaryMany: "Speed from {server} over the {range}: {n} measurements (tests and plays), between {min} and {max} Mbps, latest {mbps} Mbps at {time}.",
    chartSummaryOne: "Speed from {server} over the {range}: one test, {mbps} Mbps at {time}.",
    chartSummaryNone: "No speed tests from {server} in the {range}.",
    chartEvery: "Every test was above the {mbps} Mbps needed for {quality}.",
    chartBelow: ["{n} test was below the {mbps} Mbps needed for {quality}.", "{n} tests were below the {mbps} Mbps needed for {quality}."],
    chartOffline: "Offline from {from} to {to}.",
    sampleFailed: "Failed",
    kind_scheduled: "Scheduled",
    kind_manual: "Manual",
    kind_deep: "Deep",
    kind_playback: "Playback",
    "path_direct": "Direct",
    "path_derp": "Relayed",
    "path_derp?": "Maybe relayed",
    "path_proxy": "Proxy",
    "path_unknown": "Unknown",
    quality_4k: "4K",
    quality_1080p: "1080p",
    quality_720p: "720p",
    quality_sd: "SD",
    colTime: "Time",
    colType: "Type",
    colSpeedMbps: "Speed (Mbps)",
    colLatencyMs: "Latency (ms)",
    colJitterMs: "Jitter (ms)",
    colLoss: "Loss",
    colRoute: "Route",
    colResult: "Result",
    colPerson: "Person",
    colMethod: "Method",
    colAvgSpeed: "Average speed (Mbps)",
    colSegWait: "Segment wait (ms)",
    colPauses: "Pauses",

    // overlap
    overlapTitle: "Library overlap with {server}",
    overlapSentence: "You both have **{both} {bothWord}** ({bothSize} of your storage). {server} adds **{gain} {gainWord}** ({gainSize}) you don't store.",
    titleWord: ["title", "titles"],
    itemWord: ["item", "items"],
    titlesCounted: "Titles are movies and series. Seasons and episodes belong to their series and are not counted on their own.",
    overlapAria: "You both have {both} {bothWord} ({bothSize} of your storage). {server} adds {gain} {gainWord} ({gainSize}) you don't store.",
    legendBoth: "In both libraries · {n} · {size}",
    legendNew: "New from {server} · {n} · {size}",
    fullReport: "Full report",
    overlapNone: "Overlap shows up after the first full sync.",
    overlapReport: "Library overlap",
    overlapReportNone: "No overlap report yet. It appears after the first full sync.",
    yourCopyPlays: "Your copy always plays when you both have a title.",
    byQuality: "By quality",
    overlapByQualityCaption: "Overlap by quality",
    colQuality: "Quality",
    colBothTitles: "In both (titles)",
    colYourStorage: "Your storage",
    colNewTitles: "New from {server} (titles)",
    colTheirStorage: "Their storage",
    qualityTotal: "Total",
    betterCopy: ["{server}'s copy is better than yours for {n} title.", "{server}'s copy is better than yours for {n} titles."],
    betterCopyLater: "Alternate versions are coming in a later version.",
    overlapUpdated: "Updated after the last full sync, {when}.",

    // libraries & access (ui-spec 8.4)
    sharedWithYou: "Shared with you",
    sharedWithYouIntro: "At first only the person who added a library can see it. Try it, then open it to everyone here with one switch.",
    youShare: "You share",
    youShareIntro: "What each friend's server can see from yours.",
    noLibrariesYet: "No libraries from friends yet.",
    noSharing: "You aren't sharing any libraries.",
    libraryMetaSplit: "{remote} on their server, {local} here · last sync {when}",
    libraryFromMetaSplit: "From {name} · {remote} on their server, {local} here · last sync {when} ({result})",
    libMetaRemote: "{type} · {count} on their server",
    libraryMeta: ["{count} title \u00b7 last sync {when}", "{count} titles \u00b7 last sync {when}"],
    shareMeta: "{k}: {names} \u00b7 highest quality {limit} \u00b7 {tc}",
    limitMbps: "{n} Mbps",
    limitNone: "no limit",
    transcodesAtOnce: ["{n} transcode at once", "{n} transcodes at once"],
    youWord: "You",
    accessHidden: "Hidden from everyone",
    accessOnlyYou: "Only you",
    accessPeople: ["{n} person: {names}", "{n} people: {names}"],
    accessEveryoneShort: "Everyone on this server",
    choosePeople: "Choose people",
    seeRemembered: "See saved selection",
    savedSelectionNote: ["Your earlier selection of {n} person is saved. It comes back if you switch back.", "Your earlier selection of {n} people is saved. It comes back if you switch back."],
    modeSelected: "Only selected users",
    modeEveryone: "Everyone on this server",
    showFullList: "Show the full list",
    accessConfirmEveryoneTitle: "Open {library} to everyone?",
    accessConfirmEveryoneBody: ["{n} more person can see and play it now: {names}. People you add to this server later get it too.", "{n} more people can see and play it now: {names}. People you add to this server later get it too."],
    accessConfirmEveryoneNone: "Nobody new can see it now, but people you add to this server later will.",
    accessConfirmRestrictedStay: ["{r} profile with parental limits stays out. You can include it afterwards.", "{r} profiles with parental limits stay out. You can include them afterwards."],
    openToEveryone: "Open to everyone",
    accessConfirmSelectedTitle: "Limit {library} to selected people?",
    accessConfirmSelectedLose: ["{n} person loses access: {names}.", "{n} people lose access: {names}."],
    accessConfirmSelectedKeep: ["Your saved selection of {m} person comes back.", "Your saved selection of {m} people comes back."],
    accessConfirmSelectedKeepOnly: ["Nobody loses access. Your saved selection of {m} person comes back.", "Nobody loses access. Your saved selection of {m} people comes back."],
    accessConfirmSelectedEmpty: "Nobody will be able to see it until you choose people.",
    limitAccess: "Limit access",
    includeRestricted: "Include profiles with parental limits",
    includeRestrictedNote: "Ratings come from {server} and aren't checked.",
    ratingsTitle: "Include profiles with parental limits?",
    ratingsBody: "Ratings for {library} come from {server}, and Shoal can't check them. A child's profile could see a movie that's rated wrong.",
    ratingsAckBox: "I understand ratings from {server} aren't checked.",
    includeThem: "Include them",
    announceAccessSaved: "Saved who can see {library}",
    announceEveryone: "{library} is open to everyone on this server",
    announceLimited: ["{library} is limited to {m} person", "{library} is limited to {m} people"],
    announceRestrictedIncluded: "Profiles with parental limits can now see {library}",
    accessFailed: "Couldn't change who can see {library}. Nothing changed.",
    accessLabel: "Who can see {library}",
    accessDescSelected: "Only the people you choose can see and play it. Everyone else won't know it's there.",
    accessDescEveryone: "Everyone with an account here can see and play it, including people you add later.",
    driftSummary: ["Access changed outside Shoal for {n} person", "Access changed outside Shoal for {n} people"],
    driftIntro: "Someone changed library access in Jellyfin's dashboard. Shoal's choice here wins: playback is checked every time, and lists catch up within about an hour, unless you accept the change.",
    accessConfirmCantPlay: "They can't play it from now on. It can stay in their lists for up to an hour.",
    drift_extraAccess: "Can see it in Jellyfin, but isn't selected here.",
    drift_missingAccess: "Selected here, but Jellyfin hides it from them.",
    drift_managed: "Shoal keeps this account's library list so it can hide {library}.",
    drift_unmanaged: "Shoal stopped managing this account's library list because someone edited it in the dashboard.",
    driftSelect: "Select {name}",
    driftRemove: "Remove {name}",
    reapplyNow: "Re-apply now",
    reapplying: "Re-applying\u2026",
    announceReapplied: "Access to {library} re-applied",
    unknownUser: "Unknown account",
    pickerTitle: "Who can see {library}?",
    pickerBody: "Only the people you tick can see and play this library. Everyone else won't know it's there.",
    pickerReadOnlyTitle: "Saved selection for {library}",
    pickerReadOnly: "These people get it back if you switch to Only selected users.",
    pickerSearch: "Find a person",
    pickerEveryone: "Select all",
    pickerCount: "{n} of {total} selected",
    pickerAck: "Ratings come from {server} and aren't checked. Let me choose profiles with parental limits.",
    pickerYouWarn: "You won't see this library either.",
    noteYou: "You",
    noteAdmin: "Admin",
    noteDisabled: "Disabled account",
    noteAllLibraries: "Has access to all libraries. If unticked, Shoal hides this library from them and keeps the rest as it is.",
    noteRestricted: "Parental limits: ratings come from {server} and aren't checked",
    noteManaged: "Shoal keeps this account's library list so it can hide some friends' libraries.",
    safetyCodeLabel: "Safety code {code}",
    typeToConfirm: "Type {text} to confirm",
    typedHelp: "The button turns on when the text matches exactly.",

    // settings (ui-spec 8.5)
    sLibraryFiles: "Library files",
    fStubRoot: "Folder for Shoal's library files",
    stubOk: "Local disk. Good.",
    stubNetwork: "That folder is on a network share. Shoal needs a local disk so a network hiccup can't freeze your server.",
    stubInside: "That folder is inside one of your libraries. Pick a folder outside them.",
    stubNotWritable: "Shoal can't write to that folder.",
    fStubAck: "I understand the risk, use it anyway",
    sSyncing: "Syncing",
    fSyncEvery: "Check friends' libraries every",
    fMassLegend: "Safety net for big deletions",
    massA: "Hold removals when more than",
    massB: "titles or",
    massC: "% of a library disappear at once",
    fMassHelp: "Protects you when a friend's drive disconnects.",
    fAddLegend: "Safety net for big additions",
    addA: "Hold new titles when more than",
    addB: "titles or",
    addC: "% arrive at once",
    fAddHelp: "Protects your server if a friend's server suddenly lists thousands of titles. You add them yourself from the alert; Shoal never does it on its own.",
    fieldTitles: "Titles",
    fieldPercent: "Percent",
    fQuota: "Storage limit per server (GB)",
    fQuotaHelp: "Pictures and library files Shoal saves for each friend's server. When it's full, new titles still appear with plain art.",
    fArt: "Picture quality",
    artStandard: "Standard (about 0.2 MB per title)",
    artHigh: "High (sharper on big screens)",
    sPlayback: "Playback",
    fAutoFit: "Fit quality to each link",
    fAutoFitNote: "Asks your friend's server to transcode when the link is too slow.",
    fMaxStreams: "Streams at once per person",
    denying: "Denying…",
    fMaxStreamsHelp: "How many friends' titles one person here can watch at the same time, across all friends' servers. Up to 4.",
    fHlsSubs: "Send subtitles inside the video stream",
    fHlsSubsNote: "Turn off if subtitles misbehave in an app. Subtitles then come as separate files or are burned in.",
    sSpeedTests: "Speed tests",
    fSpeedEvery: "Test each link every",
    sOffline: "Offline servers",
    fNotify: "Tell people when a server is offline",
    fNotifyNote: "When someone presses Play on a title from an offline server, their app shows a short message, if the app supports it.",
    fPark: "Hide a server's libraries while it's offline",
    fParkHours: "Hours offline before hiding",
    fBufferAhead: "Video chunks to load ahead",
    fBufferAheadHelp: "How many video chunks your server loads ahead from a friend's server. Higher smooths a slow link and uses more memory.",
    fOfflineCard: "Show a short video when a friend's server is offline",
    fOfflineCardNote: "When a friend's server is offline, play shows a short video saying so instead of an error.",
    fRequestName: "Request service name",
    fRequestNameHelp: "What the offline message calls the place where people ask for a title, for example Seerr.",
    fRequestUrl: "Request service address (optional)",
    fRequestUrlHelp: "Shown to you and in the offline message, never in the video. Starts with http:// or https://.",
    errRequestUrl: "Use an address that starts with http:// or https://, or leave it empty.",
    fParkHoursHelp: "For example 72 for three days. Use 0.5 for half an hour.",
    fParkWarn: "Hiding only takes the libraries off people's lists, and nothing is deleted, so watch history stays. They come back after the server has been online for 10 minutes. People who see all libraries still see the titles. While a server is hidden, change who can see it with its access setting, not by ticking the library on a person's profile, because Shoal puts it back when the server returns.",
    errHoursNumber: "Enter a number from {min} to {max}.",
    sTailscale: "Tailscale",
    fTailscale: "Read Tailscale status",
    tsAuto: "Automatically",
    tsOff: "Off",
    tsCustom: "Custom path",
    fTsPath: "Path to the Tailscale socket",
    tsFound: "Found Tailscale on this server.",
    tsMissing: "Tailscale status isn't available here.",
    unsavedChanges: "Unsaved changes",
    settingsSaved: "Settings saved",
    fixFields: ["Fix {n} field to continue.", "Fix {n} fields to continue."],
    errEnterFolder: "Enter a folder.",
    errEnterPath: "Enter a path.",
    errWholeNumber: "Enter a whole number from {min} to {max}.",
    sProtect: "Protect your shared videos",
    protectLater: "This appears once you share a library.",
    protectIntro: "Jellyfin lets anyone who knows a video's internal ID stream it without signing in. Shoal requires sign-in for the libraries you share, from the first install. Your other libraries aren't affected. If something breaks, switch to log only to see what would be blocked, or turn it off.",
    protectSince: "since {date}",
    modeLogOnly: "Log only",
    modeLogOnlyTitle: "Log only (test first)",
    modeLogOnlyBody: "Nothing is blocked. Shoal counts requests that would have been blocked, so you can check nothing breaks.",
    modeEnforce: "Require sign-in",
    modeEnforceBody: "Requests without sign-in to your shared videos are blocked. Posters and pictures stay open so apps can still show them.",
    modeOff: "Off",
    modeCurrent: "Current",
    protectRequire: "Require sign-in\u2026",
    protectSwitchLog: "Switch to log only",
    protectTurnOff: "Turn off protection\u2026",
    troubleshooting: "Troubleshooting",
    troubleBody: "If you think Shoal is breaking playback on this server, you can turn this protection off completely. Shoal then stops checking and counting. Pairing limits and friends' transcode limits stay on.",
    protectOffNow: "Protection is off. Anyone who knows a shared video's ID can stream it.",
    wouldBeBlocked: "Would have been blocked, last 7 days",
    nothingYet: ["Nothing yet. Shoal has been watching for {d} day.", "Nothing yet. Shoal has been watching for {d} days."],
    colApp: "App",
    colNetwork: "Network",
    colKind: "Kind",
    colCount: "Count",
    netThisServer: "This server",
    netHome: "Home network",
    netTailscale: "Tailscale",
    netInternet: "Internet",
    kindStream: "Video",
    kindSubtitle: "Subtitles",
    kindAttachment: "Attachment",
    kindHls: "Streaming",
    kindImage: "Picture",
    protectOk: "Nothing unexpected in 7 days. It looks safe to require sign-in.",
    protectWarn: ["{n} request came from {app}. Requiring sign-in would stop that app from playing shared titles.", "{n} requests came from {app}. Requiring sign-in would stop that app from playing shared titles."],
    mightStop: "What might stop working",
    mightStop1: "Smart TVs that play over DLNA",
    mightStop2: "Older apps that sign in the old way",
    mightStop3: "Stream links copied into VLC or other players",
    protectConfirmTitle: "Require sign-in for shared videos?",
    protectConfirmBody: "Apps in the list above will stop playing shared titles until they sign in.",
    protectConfirmButton: "Require sign-in",
    protectOffTitle: "Turn off protection?",
    protectOffBody: "Shoal stops checking and counting requests for shared videos until you turn it back on.",
    protectOffButton: "Turn off",
    alwaysOn: "Always on while you share",
    shieldHits: "Pairing requests slowed down: {n}",
    capHits: "Transcodes refused over a friend's limit: {n}",
    guardErrorsHint: "Shoal hit {n} errors while checking requests and let those requests through, so your server kept working. If this keeps happening, check the server log.",
    announceProtectSaved: "Protection set to {mode}",
    sKeys: "Key storage",
    keyLine: "Shoal's keys are encrypted with {source}.",
    keyWordEnv: "an environment variable",
    keyWordDpapi: "Windows protection for the Jellyfin account",
    keyWordFile: "a key file in Shoal's data folder",
    keyWordEnvFile: "a key file you chose",
    keyHelp: "To keep the key out of backups, set JELLYFIN_SHOAL_KEY_FILE to a file holding 32 random bytes as 64 hex characters, for example from openssl rand -hex 32. Changing the key means pairing every server again.",
    wrongInvites: "Wrong invites tried in the last hour: {n}",
    shareAccountsLimits: "Sharing accounts can't sign in again elsewhere or change their password.",
    proxySuspected: "Most requests reach Jellyfin from one address on your own network, so Shoal can't tell visitors apart and its limits apply to everyone at once. If Jellyfin sits behind a reverse proxy, add the proxy under Known proxies in Dashboard, then Networking.",
    stateByCopy: "This folder can't make backups the usual way, so Shoal copies the file instead. That's fine.",
    keyVolumeUnsafe: "This drive can't protect Shoal's secret files. Treat secrets.enc and master.key as readable by anyone who can read the folder.",
    sParental: "Parental limits",
    parentalBody: "Every title from a friend carries the tag Shoal. Blocking that tag hides all friends' titles from profiles with parental limits, whatever the ratings say.",
    sState: "Backups and checks",
    stateBackup: "Last settings backup: {when}",
    stateNoBackup: "No settings backup yet.",
    stateClean: "Everything checks out.",
    finding_missingKey: "{server} has no saved key. Pair it again.",
    finding_missingFolder: "{server}'s library folder is missing.",
    finding_libraryMissing: "The library {library} is missing in Jellyfin. Pair {server} again or remove it.",
    finding_orphanSecret: "Shoal has a saved key for a server that isn't in its settings. Nothing is using it.",
    finding_orphanFolder: "Shoal has a library folder that belongs to no paired server. Nothing is using it.",
    finding_orphanFolders: ["{n} library folder belongs to no paired server. Nothing is using it.", "{n} library folders belong to no paired server. Nothing is using them."],
    finding_orphanSecrets: ["{n} saved key belongs to a server that isn't in Shoal's settings. Nothing is using it.", "{n} saved keys belong to servers that aren't in Shoal's settings. Nothing is using them."],
    findingFolderNames: "Folders: {names}",
    finding_orphanLibrary: "The library {library} has no paired server. This happens after a restore: pair the server again, or remove the library.",
    sDanger: "Delete all Shoal data",
    dangerBody: "Removes every paired server, every friend's access, Shoal's keys and the libraries Shoal created. People whose library list Shoal kept get All libraries back.",
    deleteAllButton: "Delete all Shoal data\u2026",
    deleteAllTitle: "Delete all Shoal data?",
    deleteAllBody: "This removes every paired server, every friend's access, Shoal's keys and the libraries Shoal created. It can't be undone.",
    deleteAllPhrase: "delete everything",
    deleteAll: "Delete all",

    // page banners (ui-spec 6.16, 8.8)
    bannerRecoveryTitle: "Shoal couldn't read its settings",
    bannerRecoveryBody: "Shoal's settings file and its backups are damaged, so Shoal paused sharing and won't change anything until you choose what to do. Titles from friends' servers won't play meanwhile.",
    bannerRecoveryNewer: "Shoal's settings were saved by a newer version of Shoal. Update Shoal, or restore an older backup. Until then Shoal paused sharing and won't change anything.",
    bannerRestore: "Restore a backup…",
    bannerStartOver: "Start over…",
    bannerStartOverTitle: "Start over with empty settings?",
    bannerStartOverBody: "Shoal moves the damaged files aside (it deletes nothing) and starts with no paired servers and no friends. You'll need to pair again.",
    bannerStartOverConfirm: "Start over",
    startOverPhrase: "start over",
    pausedUntilFixed: "Shoal is paused until its settings are fixed.",
    orphanTitle: "Leftover sharing accounts",
    orphanBody: "Shoal made these accounts for friends, but no share uses them any more (for example after a reset or a restore). They can still sign in to your server. Shoal never deletes them by itself.",
    orphanDisabled: "Disabled",
    orphanActive: "Can sign in",
    orphanDelete: "Delete…",
    orphanDeleteLabel: "Delete {name}",
    orphanDeleteTitle: "Delete the account {name}?",
    orphanDeleteBody: "This removes the account {name} from your server, with its sign-ins. It cannot be undone. Your own accounts and your friends' shares are not touched.",
    orphanDeleteConfirm: "Delete account",
    announceOrphanDeleted: "Deleted the account {name}",

    // two-way friends (DESIGN v2.6.2)
    twoWayTitle: "Friends who share both ways",
    twoWayIntro: "You watch their server and they watch yours.",
    twoWayWatch: "You watch {name}",
    twoWayWatched: "{name} watches you",
    titlesN: ["{n} title", "{n} titles"],
    streamsNowN: ["{n} stream now", "{n} streams now"],
    playingNowN: ["{n} playing now", "{n} playing now"],
    openServerLink: "Open server",
    openFriendLink: "Open friend",
    alsoYourServer: "Also your server: {name}",
    alsoWatchesTitle: "{name} also watches your libraries",
    alsoWatchesMeta: "{libs} · {devs} · last seen {when}",
    alsoWatchesNote: "Pausing or removing {name} here only changes what you watch. {name} keeps watching your libraries.",
    openFriendAs: "Open {name} as a friend",
    youAlsoWatchTitle: "You also watch {name}",
    openServerAria: "Open {name} as a server",
    openFriendAria: "Open {name} as a friend",
    pauseStillWatches: "{name} will still watch your libraries.",
    removeStillWatches: "{name} will still watch your libraries.",
    revokeStillWatching: "You will still watch {name}'s libraries.",
    chipStatusUnknown: "Unknown",
    pairAlsoShares: "You already share with {name}; this adds the other direction.",
    prefillFriend: "Use the name of a server you watch:",

    // token rotation (DESIGN v2.6.1)
    rotationTitle: "Server key",
    rotationNotSupported: "{server} was paired the old way, so Shoal cannot change its key. Re-pair it with an invite to get a fresh key.",
    rotationLast: "Key last changed",
    rotationNext: "Next change",
    rotationNoSchedule: "Not scheduled",
    rotationSchedule: "Change the key automatically",
    rotationScheduleHelp: "Changing the key limits how long an old key is of any use. It never happens while someone is watching.",
    rotationOff: "Never (change it by hand)",
    rotationEvery: "Every {n} days",
    rotateNow: "Change the key now",
    rotationGoto: "Go to key settings",
    rotationSaved: "Saved",
    rotating: "Changing…",
    rotateTitle: "Change {server}'s key now?",
    rotateBody: "Shoal asks {server} for a new key and uses it from now on. This takes a few seconds and needs {server} to be online with nobody watching from it.",
    rotateConfirm: "Change the key",
    announceRotated: "Changed {server}'s key",
    announceRotationSaved: "Key schedule saved",
    rotateErr_notSupported: "This server was paired the old way. Re-pair it with an invite to change its key.",
    rotateErr_notOnline: "The server is not online right now. Try again when it is.",
    rotateErr_streamsActive: "Someone is watching from this server. Try again when they have finished.",
    rotateErr_busy: "Another change is already running. Try again in a moment.",
    rotateErr_rejected: "The server refused to give a new key. Pair it again if it stops working.",
    rotateErr_lostResponse: "The new key never arrived and the old one probably stopped working. Pair the server again.",
    rotateErr_badResponse: "The server answered in a way Shoal did not trust. Nothing was changed.",
    rotateErr_failed: "The key could not be changed. Try again in a moment.",
    alertRotFailedTitle: "Changing {server}'s key failed",
    alertRotFailedBody_rejected: "{server} refused the request for a new key. If it stops working, pair it again.",
    alertRotFailedBody_lostResponse: "Shoal asked {server} for a new key but the answer never arrived. The old key has probably stopped working. Pair {server} again.",
    alertRotFailedBody_badResponse: "{server} answered the request for a new key in a way Shoal did not trust, so nothing was changed. If it stops working, pair it again.",
    alertRotStoreTitle: "{server}'s new key is not saved",
    alertRotStoreBody: "The new key works now, but Shoal could not save it. After Shoal restarts it would be lost: pair {server} again soon.",
    alertRotOverdueTitle: "{server}'s key change is overdue",
    alertRotOverdueBody: "The scheduled key change could not run for a day, for example because someone was watching or the server was offline. Change it now, or check the schedule.",
    friendKeyChanged: "Key last changed {when}",
    friendRotationFailed: "Changing {friend}'s key failed, so {friend}'s server has no working key. Create a new invite so it can connect again.",

    // optional features (DESIGN v2.6.4 to v2.6.7)
    sFeatures: "Extra features",
    fAltVersions: "Show every version of a title",
    fAltVersionsNote: "Turn on to see each version of a movie separately, for example 1080p and 4K.",
    fTrickplay: "Preview pictures when you drag the seek bar",
    fTrickplayNote: "Shoal fetches them from the friend's server when you first ask and keeps them on disk.",
    fTrickplayGb: "Most disk for preview pictures (GB)",
    fTrickplayGbHelp: "Between 1 and 20. The oldest are removed first when it is full.",
    trickplayUsed: "Preview pictures use {size} of disk now.",
    trickplayClear: "Clear",
    trickplayClearLabel: "Clear the preview pictures",
    announceTrickplayCleared: "Preview pictures cleared",
    fAttachments: "Use the fonts that come with a title's subtitles",
    fAttachmentsNote: "Styled subtitles (ASS) then look the way their maker intended. Only font files are used.",
    fReencode: "Check pictures before keeping them",
    fReencodeNote: "Shoal re-saves every picture from a friend with a separate helper program before keeping it, so a damaged picture can't harm your server.",
    sandboxActiveFull: "The picture check is on.",
    sandboxActivePartial: "The picture check is on, with time and size limits only.",
    sandboxRejected: ["{n} picture was refused since the server started.", "{n} pictures were refused since the server started."],
    sandboxOff: "Turned off. Pictures are checked the usual way.",
    sandboxUnavailable_ffmpegMissing: "Shoal could not find ffmpeg on this server. Pictures are checked the usual way.",
    sandboxUnavailable_selfTestFailed: "The test run of the check failed. Pictures are checked the usual way.",
    sandboxUnavailable_other: "Pictures are checked the usual way.",
    sandboxAlertTitle: "The picture check is not available",
    countersTitle: "What Shoal turned away",
    countersIntro: "Counted since {when}, and reset when the server restarts. No addresses, titles or apps are kept.",
    colCounter: "What",
    counter_nonstatic_stream: "Video requests that were not direct plays",
    counter_audio_route: "Audio requests",
    counter_trickplay_m3u8: "Preview-thumbnail playlists",
    counter_no_credentials: "Requests without a sign-in",
    counter_core_hls_route: "Requests that tried Jellyfin's own streaming addresses",
    counter_forbidden_route: "Requests to addresses Shoal blocks",
    counter_long_query: "Requests with an address that was too long",
    counter_optional_data_dropped: "Extras (thumbnails, fonts) a server sent that could not be read and were left out",
    restoreTitle: "Restore Shoal's settings",
    restoreBody: "Pick the copy to go back to. Changes made after it was saved will be missing.",
    restoreFile: "{file} · saved {date}",
    restoreNewest: "Newest",
    restoreOlder: "Older",
    restoreNone: "There is no backup to restore. Choose Start over instead.",
    restoreButton: "Restore",
    bannerKeysTitle: "Shoal can't unlock its saved keys",
    bannerKeysBody: "Shoal stopped using your friends' servers because it couldn't decrypt the keys it saved. That happens when the encryption key changed or the file was damaged. Shoal didn't make a new key, so nothing is lost yet.",
    bannerKeysHow: "How to fix it",
    bannerKeysFix_env: "Set JELLYFIN_SHOAL_KEY back to its old value and restart Jellyfin.",
    bannerKeysFix_file: "Put the original master.key back in Shoal's data folder and restart Jellyfin.",
    bannerKeysFix_envFile: "Put the original key back in the file JELLYFIN_SHOAL_KEY_FILE points to, and restart Jellyfin.",
    bannerKeysBadKey: "The key Shoal was given isn't a valid key. It must be 32 random bytes, written as 64 hex characters.",
    bannerKeysFix_badKey: "Put back the key you used before. Or make a new one with openssl rand -hex 32, restart Jellyfin, and pair each server again.",
    bannerKeysBothKeys: "Both JELLYFIN_SHOAL_KEY and JELLYFIN_SHOAL_KEY_FILE are set, so Shoal doesn't know which key to use.",
    bannerKeysFix_bothKeys: "Remove one of them and restart Jellyfin.",
    bannerGuardOk: "Your friends' sharing accounts keep their usual limits meanwhile.",
    bannerGuardNo: "Protection for the libraries you share is also off until you fix this.",
    checkLibraries: "Check libraries",
    checkFriends: "Check friends",
    confirm: "Confirm",
    confirming: "Confirming…",
    announceRestoreConfirmed: "Thanks. Shoal is back to normal.",
    bannerKeysFix_dpapi: "Run Jellyfin under the Windows account it used before, then restart it.",
    bannerKeysOr: "Or pair each server again. That replaces the saved keys.",
    notUsedUntilKeys: "Not in use until Shoal can unlock its keys",
    bannerRestoredTitle: "Shoal restored its settings from a backup",
    bannerRestoredBody: "The main settings file was damaged, so Shoal used {file} from {date}. Changes made after that are missing. To be safe, every friend's library is now limited to its selected people. Check who can see each library and which friends can connect, then confirm.",
    announceStartedOver: "Shoal started over",
    announceRestored: "Settings restored",

    // codes, checklist, approval card, access switch (ui-spec 6.10, 6.17-6.19)
    checkDone: "done",
    checkWorking: "working",
    checkFailed: "failed",
    checkTodo: "to do",
    approvalTitle: "{home} wants to connect",
    // pairing outcomes (ui-spec 7, 8.2.5, 8.2.9)
    seeWhatToDo: "See what to do",
    notPaired: "Not paired",
    startAgain: "Start again",
    dismiss: "Dismiss",
    stoppedAgo: "Stopped {when}",
    announceDismissedNew: "Removed from your list",
    announceDismissedBack: "{server} is back to how it was",
    hintSafety_serverChanged: "The server changed during pairing, so Shoal saved nothing.",
    hintSafety_adminAccount: "That was an admin account, so Shoal refused it.",
    hintSafety_badPeerResponse: "{server} didn't answer like Jellyfin, so Shoal stopped.",
    inviteNotice: "Someone tried {n} wrong invites on your server in the last hour. Your invites still work, and Shoal is slowing those attempts down. Keep sending invites privately.",
    out_expiredInvite_title: "The invite expired",
    out_expiredInvite_body: "Invites last 15 minutes. Ask your friend for a new one, then start again.",
    out_denied_title: "Your friend chose Deny",
    out_denied_body: "Your friend turned down the request on {server}, so nothing was saved. If that was a mistake, ask them for a new invite, then start again.",
    out_codeMismatch_title: "The safety code didn't match",
    out_codeMismatch_body: "Your friend typed a code that didn't match yours 3 times, so their server cancelled the request and the invite. Nothing was saved. Call them and read the code out slowly, one group at a time. Then ask for a new invite and start again. If it still doesn't match, stop and check the server address with them first.",
    out_inviteInvalid_title: "That invite didn't work",
    out_inviteInvalid_body: "It may have expired, been used already, or been cancelled. Ask your friend for a new one.",
    out_inviteReplaced_title: "That invite was replaced",
    out_inviteReplaced_body: "Your friend made a newer invite while this one was waiting, so their server dropped it. Nothing was saved. Ask them for the newest one, or have them send it again, then start again.",
    out_inviteCancelled_title: "That invite was cancelled",
    out_inviteCancelled_body: "Your friend cancelled the invite, or stopped sharing, before it was approved. Nothing was saved. Ask them for a new one, then start again.",
    out_tokenNotCollected_title: "The approval wasn't collected in time",
    out_tokenNotCollected_body: "Your friend approved, but this server didn't pick up the key within 2 minutes, so their server signed it out again. Nothing was saved. Check the connection, ask for a new invite, then start again.",
    out_adminAccount_title: "That account is an admin on {server}",
    out_adminAccount_body: "Shoal only accepts a limited sharing account, so it signed that account out and saved nothing. Ask your friend to approve the code from the sharing account they made for you.",
    out_serverChanged_title: "The server changed while pairing",
    out_serverChanged_body: "The server at this address isn't the one you checked. Shoal saved nothing. Check the address with your friend, then start again.",
    out_badPeerResponse_title: "Pairing stopped for your safety",
    out_badPeerResponse_body: "{server} answered in a way Jellyfin never does, so Shoal stopped and saved nothing. Check the address with your friend, then start again.",
    out_interruptedInvite_title: "The invite expired",
    out_interruptedInvite_body: "Your server restarted while waiting for your friend, so this invite can't be finished and Shoal saved nothing. If your friend approves it anyway, their Shoal signs that connection out within 2 minutes. Ask them for a new invite, then start again.",
    out_rateLimited_title: "Too many tries",
    out_rateLimited_body: "Wait {m} min, then try again. Nothing was saved.",
    out_network_title: "Lost contact with {server}",
    out_network_body: "Shoal couldn't reach {server} for 30 seconds while pairing, so it stopped and saved nothing. Check the connection, then start again.",
    out_server_title: "Pairing didn't work",
    out_server_body: "Something went wrong on your server. Try again in a moment.",

    // pairing (ui-spec 8.2)
    stepsLabel: "Pairing steps",
    stepOf: "Step {i} of {n} · {label}",
    stepCheck: "Check the server",
    stepChoose: "Choose what to add",
    stepWho: "Who and what",
    stepApprove: "Approve",
    pairTitleShort: "Pairing is still in progress",
    fServerName: "Name for this server",
    charsLeft: "{n} left",
    fServerAddress: "Server address",
    fReach: "How you reach it",
    reachTailnet: "Tailscale (private)",
    reachLan: "Home network (same house)",
    reachPublic: "Internet (https only)",
    netHelpTailnet: "Encrypted by Tailscale. Plain http is fine here.",
    netHelpPublic: "Needs an https address with a valid certificate.",
    advanced: "Advanced",
    fProxy: "Proxy (optional)",
    fProxyHelp: "Only if Tailscale on this server runs in userspace mode, for example socks5://127.0.0.1:1055",
    checkServer: "Check server",
    checking: "Checking…",
    errEnterName: "Enter a name.",
    errEnterAddress: "Enter the server address.",
    errBlocked: "Shoal can't connect to that address. It points to this server itself or to a private service.",
    errWrongNetwork: "That address isn't a Tailscale address. Change How you reach it, or check the address.",
    errWrongNetworkLan: "That address isn't a home network address. Change How you reach it, or check the address.",
    errWrongNetworkPublic: "That address is on a private network, not the internet. Change How you reach it, or check the address.",
    errProxyNotAllowed: "A proxy only works with a Tailscale address (a .ts.net name or a 100.x address).",
    rateLimitedIn: "Too many tries. You can try again in {n} min.",
    rateLimitedWait: "Too many tries. Wait {n} min, then try again.",
    factName: "Server name",
    factVersion: "Jellyfin version",
    factAddress: "Address",
    factLatency: "Latency",
    factServerId: "Server ID",
    factReach: "How you reach it",
    warnUntested: "Shoal hasn't been tested with Jellyfin {version} yet. It will probably work.",
    warnLanClear: "Home network connections aren't encrypted.",
    startPairing: "Start pairing",
    starting: "Starting…",
    alreadyPairing: "Another pairing is already in progress.",
    goToIt: "Go to it",
    safetyCodeWord: "Safety code",
    copyCode: "Copy code",
    waitingForFriend: "Waiting for your friend to approve on {server}…",
    confirmWithin: "Confirm within {time}",
    cancelPairing: "Cancel pairing",
    thisServer: "this server",
    thisServerName: "this server",
    chooseTitle: "Choose what to add",
    chooseBody: "Pick libraries from {server}. At first only you can see them, so you can try them. When they work, open them to everyone here in Libraries & access.",
    loadingLibraries: "Loading libraries…",
    libMeta: ["{type} · {count} title", "{type} · {count} titles"],
    libMetaTv: "{type} · {series} series, {episodes} episodes",
    libLater: "{type} are coming in a later version.",
    libTvOff: "TV shows are turned off. Turn them on in Settings, under Extra features.",
    ovTv: "TV shows. The counts above are movies and episodes, and each series is counted here. In both libraries: {a} series (episodes: {b}). New from {server}: {c} series (episodes: {d}).",
    heldSeriesTitle: "Held back, by series",
    heldSeriesIntro: "Shoal paused these changes from {server} until you decide. Each series is separate.",
    heldRemovalsHead: "Removals on hold",
    heldAdditionsHead: "Additions on hold",
    heldSeriesRow: "{title} ({count} episodes)",
    heldSeriesRowNoTitle: "A series ({count} episodes)",
    heldSeriesRemove: "Remove now",
    heldSeriesAdd: "Add now",
    announceSeriesReleased: "Released",
    pv2PingThrottled: "Shoal could not be checked just now because the server limited the check. Wait a minute and check again. This does not mean Shoal is missing.",
    fTvShows: "Show TV series from friends",
    fTvShowsNote: "Turn on to let Shoal sync TV libraries from friends, and to pick them when you add a library. It controls what Shoal syncs, not what Jellyfin already shows. Off by default.",
    fTvThumbs: "Episode pictures",
    fTvThumbsNote: "Keep a small picture for each episode. Turn off to save disk space.",
    libFromFriend: "Libraries from friends' servers can't be shared on.",
    type_movies: "Movies",
    type_tvshows: "TV shows",
    type_music: "Music",
    type_other: "Other",
    fLocalName: "Name in your library",
    defaultLocalName: "{server} {library}",
    whoCanSee: "Who can see this",
    addAll: "Add all {count} titles",
    addAllNote: "The first sync takes a while and saves about {gb} GB of pictures.",
    addAndSync: "Add and start syncing",
    pickAtLeastOne: "Pick at least one library",
    confirmBigAdd: "Tick the box under {library} to confirm adding all of its titles.",
    adding: "Adding…",
    addFailed: "Couldn't add those libraries. Try again in a moment.",
    addConflict: "That library is already added, or another library already uses its name. Nothing was changed.",
    noStubRootAdd: "Shoal has no folder for its library files yet. Choose one in Settings, then add the libraries again.",
    pairedTitle: "{server} is paired",
    addingProgress: "Adding titles: {done} of {total}",
    addingTitles: "Adding titles",
    leavePage: "You can leave this page. Syncing keeps going in the background.",
    noOneCanSee: "No one can see {library} yet. Choose people in Libraries & access.",
    goToOverview: "Go to Overview",
    openServer: "Open {server}",
    fFriendName: "Friend's name",
    fFriendNameHelp: "Shown only to you.",
    fLibrariesToShare: "Libraries to share",
    fQualityLimit: "Highest quality per stream",
    limitNoLimit: "No limit",
    limit40: "40 Mbps (4K)",
    limit20: "20 Mbps (1080p, high)",
    limit10: "10 Mbps (1080p)",
    limit4: "4 Mbps (720p)",
    fTranscodes: "Transcodes at once",
    fTranscodesHelp: "How many videos {friend}'s server can ask yours to convert at the same time. Converting uses your CPU. This is a limit, not a hard cap: audio and subtitle conversions aren't counted.",
    yourFriend: "your friend",
    errEnterFriend: "Enter your friend's name.",
    transportWhy: "Choose one to create the invite.",
    creatingInvite: "Creating…",
    inviteBody: "Send it to {friend} privately. In Shoal they choose Pair a server, then My friend sent me an invite, and enter it with your server's address. Their screen then shows a safety code, and you type it here to approve.",
    inviteShownOnce: "Shoal shows this invite only once. Copy it now and send it privately, for example in Signal or a text. It works one time, for 15 minutes.",
    waitingForUse: "Waiting for {friend} to use the invite…",
    cancelInvite: "Cancel invite",
    copyInvite: "Copy invite",
    approvalNoneTitle: "Nothing is waiting",
    approvalNone: "Nothing is waiting for approval.",
    approvedTitle: "Approved",
    approvedWaiting: "Approved. Waiting for {home} to connect…",
    approvalReconnect: "Approving replaces {friend}'s current connection.",
    approvalCall: "Ask {friend} to read you the safety code on their screen, by phone, text or in person. Type it here to approve. If you weren't expecting this, choose Deny.",
    approveReason: "Type the 8-character code to approve.",
    codeWrong: "That code doesn't match. Check it with {friend} and type it again. {n} tries left.",
    factFrom: "Connecting from",
    addr_tailnet: "a Tailscale address",
    addr_lan: "a home network address",
    addr_public: "an internet address",
    approve: "Approve",
    deny: "Deny",
    announceApprovedShare: "Approved. {friend}'s server is connected.",
    announceApprovalWaiting: "{server} wants to connect. Ask {friend} for the safety code.",
    announceDenied: "Request denied",
    shareCancelledTitle: "Request cancelled",
    shareCancelledBody: "The safety code didn't match 3 times, so Shoal cancelled this request and its invite to be safe. Nothing was shared. Call {friend}, check the code together, then create a new invite.",
    shareDeniedTitle: "You denied the request",
    shareDeniedBody: "Nothing was shared.",
    shareUncollectedTitle: "{home} didn't finish connecting",
    shareUncollectedBody: "Shoal signed that device out to be safe. Create a new invite and try again.",
    shareDoneTitle: "{friend} can now watch from your server",
    shareDoneBody: ["They'll see {k} library. You can pause or stop sharing on {friend}'s page.", "They'll see {k} libraries. You can pause or stop sharing on {friend}'s page."],
    openFriend: "Open {friend}",
    resumeBody: "Pairing with {server} is still in progress.",
    // steps for your friend (ui-spec 8.2.8)
    announcePairingStarted: "Pairing started with {server}",
    announcePaired: "{server} is paired",
    announceCancelled: "Pairing cancelled",
    announceApproved: "Approved. Connecting to {server}.",
    announceInviteExpired: "The invite expired",
    announceCodeCopied: "Code copied",
    announceInviteCopied: "Invite copied",
    announceCommandCopied: "Command copied",

    // server detail
    serverIdLabel: "Server ID",
    pairedByInvite: "Paired {date} by invite",
    pairedByQc: "Paired {date} the old way (Quick Connect)",
    testLink: "Test link",
    removeServerEllipsis: "Remove server…",
    needsAttention: "Needs attention",
    lastUpdated: "Last updated {when}",
    alertRemovalTitle: ["{count} removal on hold", "{count} removals on hold"],
    alertRemovalBody: "{server} stopped listing {count} titles at once. That can happen when a drive disconnects, so Shoal kept them for now. For example: {examples}. If they're still gone after 3 syncs over 24 hours, Shoal removes them.",
    removeThemNow: "Remove them now",
    alertAdditionTitle: ["{count} new title on hold","{count} new titles on hold"],
    alertAdditionBody: "{server} added {count} titles at once, so Shoal is holding them until you check. That protects your server if something goes wrong on theirs. For example: {examples}. Shoal never adds them on its own.",
    addThem: "Add them",
    alertDifferentTitle: "Different server at this address",
    alertCertTitle: "{server}'s certificate changed",
    certOld: "Pinned key",
    certNew: "Key now",
    trustNewCert: "Trust new certificate",
    alertForeignTitle: "Another device is using your account on {server}",
    alertForeignBody: "Shoal saw a device called {device} using the sharing account {server} gave you. If your friend doesn't recognize it, they should delete that sharing account and make a new one. Then pair again.",
    alertMergedTitle: "Unmerge {title}",
    alertMergedBody: "Jellyfin grouped {title} from {server} with one of your own movies as versions of one title. Titles from friends can't be merged with your copies, so it won't play from {server}. Open it in Jellyfin and split the versions.",
    alertQuotaTitle: "Storage limit reached for {server}",
    alertQuotaBody: "Shoal stopped saving pictures for {server} at {quota} GB. New titles still appear, with plain placeholder art.",
    alertStubTitle: "Shoal can't write its library files",
    alertStubBody: "The folder for {server}'s library can't be written. Check the folder in Settings.",
    openSettings: "Open Settings",
    routeLabel: "Route",
    route_direct: "Direct",
    route_maybe: "Maybe relayed",
    route_proxy: "Through proxy",
    route_unknown: "Unknown",
    routeRelayed: "Relayed through Tailscale ({region})",
    toCheck: "To check yourself, run",
    copyCommand: "Copy command",
    localApiYes: "Shoal can read Tailscale's status on this server.",
    localApiNo: "Shoal can't read Tailscale's status on this server, so whether the link goes through a relay is only a guess.",
    speedHint_relayed: "Relayed through Tailscale: expect lower speed and higher latency. Allow UDP 41641 or enable UPnP/NAT-PMP on one side.",
    speedHint_maybeRelayed: "This link may be relayed through Tailscale. Run tailscale ping to check.",
    speedHint_peerTranscodeSlow: "{server} transcodes too slowly. Use direct play or a lower quality; {server} needs hardware acceleration.",
    speedHint_peerTranscodeSlowShared: "{server} transcodes too slowly while {n} conversions from your server were running on it. Lower Conversions at once for {server}, or pick a quality that plays directly.",
    speedHint_homeDownload: "Your server's download or local network is the likely bottleneck.",
    speedHint_peerUpload: "{server}'s upload is the likely bottleneck.",
    speedHint_perConnection: "One stream is slower than the link allows, usually because of latency or a relay. Several streams at once still get the full speed.",
    speedHint_proxyCpu: "The Tailscale proxy adds CPU overhead. If speed is low, check that container's CPU.",
    deepTest: "Deep test…",
    deepTitle: "Run a deep test?",
    deepBody: "It downloads about 32 MB and asks {server} to transcode for about 20 seconds, which uses your friend's CPU.",
    runDeepTest: "Run deep test",
    recentTests: "Recent tests",
    recentPlays: "Recent plays",
    resultOk: "OK",
    testFailedReason: "Failed: {reason}",
    reason_timeout: "timed out",
    reason_unreachable: "couldn't connect",
    reason_refused: "was refused",
    reason_badResponse: "didn't answer like Jellyfin",
    deepSuggest: "Deep test: about {k} conversions at once should play smoothly at 1080p. {server}'s own viewers use the same processor. This is only a suggestion; nothing was changed.",
    reason_tls: "certificate problem",
    methodDirect: "Direct",
    methodTranscode: "Transcode",
    librariesFrom: "Libraries from {server}",
    noLibrariesFrom: "No libraries from {server} yet.",
    libraryFromMeta: ["From {remote} · {count} title · last sync {when} ({result})", "From {remote} · {count} titles · last sync {when} ({result})"],
    syncResult_ok: "OK",
    syncResult_partial: "partly",
    syncResult_failed: "failed",
    addAnotherLibrary: "Add another library",
    addLibraryTitle: "Add a library from {server}",
    noMoreLibraries: "There are no more libraries to add.",
    announceLibraryAdded: "Library added",
    playingNowTitle: "Playing now",
    nothingPlaying: "Nothing is playing from {server} right now.",
    playingMeta: "{who} · started {when}",
    serverSettings: "Settings for this server",
    fServerStreams: "Streams at once",
    fServerStreamsHelp: "How many people here can watch from {server} at the same time.",
    fPriority: "Priority",
    fPriorityHelp: "When two servers have the same title, the lower number wins.",
    reachChange: "To change this, pair again.",
    removeHeading: "Remove {server}",
    removeBody: "Stops all sharing from {server}.",
    removeTitle: "Remove {server}?",
    removeItem1: "Shoal signs out of {server} and deletes its key.",
    removeItem2: "People here stop seeing {server}'s titles.",
    removeKeepsLibrary: "The library stays in Jellyfin and Shoal won't manage it any more.",
    removeAlsoDelete: ["Also delete the {library} library and its {count} entry", "Also delete the {library} library and its {count} entries"],
    removeConfirm: "Remove server",

    checkCertificate: "Check certificate",
    pausePeerHelp: "Titles stay listed here but won't play until you resume.",
    announcePausedLong: "{server} paused. Its titles stay listed but won't play.",
    keepForNow: "Keep them for now",
    notNow: "Not now",
    alertCertBody: "Shoal stopped talking to {server} because its certificate key is different from the one you pinned. That's normal when your friend renews or replaces the certificate, and a warning sign if they didn't. Ask them before you trust it.",
    trustTitle: "Trust {server}'s new certificate?",
    trustBody: "Only do this if your friend told you they changed it. Compare the fingerprint below with your friend before you continue.",
    trustIt: "Trust it",
    pinTitle: "Pin {server}'s certificate?",
    pinBody: "Shoal will only talk to this server while it shows this exact key. Compare the fingerprint below with your friend before you continue.",
    pinIt: "Pin it",
    pinAfterAddress: "Save the new address first, pair again, then pin the certificate.",
    certFingerprint: "Fingerprint (SHA-256)",
    certMatches: "My friend confirmed that this fingerprint matches",
    certUntrustedWarn: "This certificate doesn't pass the normal checks. It may be self signed, expired or made for another name.",
    certUntrustedCheck: "I trust this certificate anyway",
    certReadFailed: "Shoal couldn't read the certificate. Check that the server is online.",
    certChangedAgain: "The certificate changed again, check with your friend",
    certUntrustedRefused: "That certificate doesn't pass the normal checks. Open the dialog again and tick the box to trust it anyway.",
    alertPolicyTitle: "The sharing account can do too much",
    alertPolicyBody: "{server}'s sharing account is allowed to {list}. Ask your friend to turn that off in the account's settings.",
    policy_allLibraries: "see all libraries",
    policy_deletion: "delete media",
    policy_download: "download media",
    policy_liveTv: "use Live TV",
    policy_remoteControl: "control other people's devices",
    policy_allDevices: "sign in from any device",
    openTitle: "Open {title}",
    alertEntityTitle: "Some names from {server} were skipped",
    alertEntityBody: "{server}'s library lists more than 20,000 cast, genre or studio names, so Shoal skipped the extra ones. Titles still appear.",
    alertEntryTitle: "{server}'s library is too big to sync",
    alertEntryBody: "Shoal syncs up to 50,000 titles per server, so syncing stopped. Titles already added still play.",
    changeWhoCanSee: "Change who can see this",
    removeEllipsis: "Remove…",
    addALibrary: "Add a library",
    removeLibraryTitle: "Remove {library}?",
    removeLibraryBody: "People here stop seeing its {count} titles. Shoal deletes the library and its entries; {server} keeps its files.",
    removeLibraryConfirm: "Remove library",
    announceLibraryRemoved: "{library} removed",
    storageTitle: "Storage",
    storageFor: "Shoal's storage for {server}",
    storageMeter: "Pictures and library files: {used} of {quota} GB",
    almostFull: "Almost full",
    capTitles: "Titles",
    capCast: "Cast names",
    capGenres: "Genres",
    capStudios: "Studios",
    capOf: "{n} of {cap}",
    limitReached: "limit reached",
    fAddress: "Server address",
    fAddressHelp: "If only the http/https part or the port changes, Shoal stays connected. A new name or IP address signs Shoal out, and you pair again.",
    afterPairing: "You can change this after pairing finishes.",
    fPin: "Pin this server's certificate",
    fPinNote: "Shoal stops if the certificate's key ever changes. Most certificates get a new key when they renew, every 2 to 3 months, and you'd have to trust it again each time.",
    addressTitle: "Change {server}'s address?",
    addressBody: "Shoal signs out of {server} and deletes its saved key before it uses the new address, so the key never reaches a server you haven't checked. Titles stay listed but won't play until you pair again.",
    addressConfirm: "Change address",
    removeItem3: "Shoal undoes the library access changes it made for {server}'s libraries.",
    removeNoteInvite: "Your friend can stop sharing with you in their Shoal.",
    aFriendsServer: "a friend's server",
    appsSummary: "Which apps can play titles from {server}",
    appsSummaryAll: "Which apps can play friends' titles",
    appsLead: "Titles from {server} play in most Jellyfin apps, but not all of them.",
    appsWorks: "Works",
    appsShould: "Should work",
    appsShouldNote: "These are built the same way, but nobody has tried them with Shoal on a real device.",
    appsRowWeb: "Jellyfin's web app, in a browser",
    appsRowMoonfinIos: "Moonfin on iPhone",
    appsRowMoonfinOther: "Moonfin on Android, Android TV, Fire TV, Apple TV, iPad, Samsung and LG TVs, Xbox, Roku and computers",
    appsRowJmp: "Jellyfin Media Player",
    appsRowAndroid: "The Jellyfin Android app, with its own player",
    appsDont: "Doesn't work",
    appsNot1: "The Jellyfin Android TV app",
    appsNot2: "Swiftfin",
    appsNot3: "Findroid",
    appsNot4: "The Jellyfin Android app when it hands a video to another player, such as VLC or MX Player",
    appsWhy: "Those apps ask for videos without signing in, and Shoal never sends a friend's video to a request that isn't signed in. A few titles may still play in them when {server} converts the video, but that isn't something to rely on.",
    appsOffline: "Downloading a friend's title to watch offline isn't available in any app.",
    appsWhatLead: "If a title won't play:",
    appsWhatBody: "ask which app they're using. If it's on the Doesn't work list, try Moonfin or Jellyfin's web app instead.",

    // friend detail (ui-spec 8.7)
    friendSharingAccount: "Sharing account {account} · since {date}",
    friendSharingSince: "Sharing since {date}",
    friendSharingAccountOnly: "Sharing account {account}",
    pauseHelp: "Signs {friend}'s server out at once. Its titles stay listed on their side but won't play.",
    resumeHelp: "Shoal first signs out any device that isn't {friend}'s server, then lets it back in.",
    friendAccessRemovedHint: "{friend}'s sharing account was turned off or deleted outside Shoal. Create an invite to share again, or stop sharing to clean up.",
    inviteSectionTitle: "Invite",
    inviteNone: "To connect {friend}'s server, or to pair it again, create an invite.",
    inviteActive: "An invite is active · expires in {time}",
    inviteUsed: "{home} used this invite. Approve or deny the request above.",
    inviteExpiredLine: "The invite expired.",
    devicesTitle: "Devices",
    devicesIntro: "Only {friend}'s server should be here. Shoal signs out anything else within a minute.",
    noDevices: "No devices yet. {friend}'s server appears here after it connects.",
    deviceMeta: "{app} {version} · first seen {first} · last seen {last}",
    deviceExpected: "{friend}'s server",
    unexpectedDevice: "Unexpected device signed out",
    removeDeviceTitle: "Remove this device?",
    removeDeviceBody: "It stops working right away. If it's {friend}'s server, they'll need a new invite.",
    removeDevice: "Remove device",
    friendLibrariesTitle: "Libraries and quality",
    stopSharingTitle: "Stop sharing with {friend}",
    stopSharingBody: "Shoal disables {friend}'s sharing account, signs out every device, checks that none are left, and deletes the account. Their server stops playing your titles within a minute.",
    stopSharingButton: "Stop sharing…",
    stopSharingDialogTitle: "Stop sharing with {friend}?",
    stopEffect1: "Shoal turns off {friend}'s sharing account.",
    stopEffect2: "Shoal signs out every device on it.",
    stopEffect3: "Shoal checks that no device is left.",
    stopEffect4: "Shoal deletes the account. {friend}'s server stops playing your titles within a minute.",
    stopAgainLater: "To share again later, create a new invite. Shoal makes a new sharing account.",
    deleteAccessButtonShort: "Delete {friend}'s access",
    stopResultTitle: "Stopped sharing with {friend}",
    resultDisabled: "Sharing account disabled",
    resultSignedOut: ["{n} device signed out", "{n} devices signed out"],
    resultNoneLeft: "No devices left",
    resultLeftLead: ["{n} device is still signed in.", "{n} devices are still signed in."],
    resultLeftBody: "Shoal tried twice. Disable the account in Dashboard, then Users, then delete its devices.",
    resultDeleted: "Sharing account deleted",
    resultNotDeletedLead: "Shoal couldn't delete the account.",
    resultNotDeletedBody: "Delete it in Dashboard, then Users.",
    twoMoreSteps: "Two more steps",
    stopStepTailscale: "Remove {friend} from this server's Tailscale sharing. In the Tailscale admin console, open Machines, then this server, then Share.",
    stopStepPublic: "Block {friend}'s address on your router or firewall.",
    stopDone: "Done",
    stopProtect_Enforce: "Protect your shared videos: requests without a sign in are blocked. Nothing more to do.",
    stopProtect_LogOnly: "Protect your shared videos: Shoal only writes down requests without a sign in and blocks none. Choose Require sign in in Settings to block them.",
    stopProtect_Off: "Protect your shared videos: protection is off. Choose Require sign in in Settings to turn it on.",
    stopStepProtectWarn: "Without it, a server that kept old links can still play your shared videos until you remove its network access.",
    createInvite: "Create invite",
    newInvite: "Create a new invite"
};


/* ====================================================================================== */
/* Constants                                                                              */
/* ====================================================================================== */

const PLUGIN_PAGE = 'Shoal';
const API_PREFIX = 'Shoal/Admin/';
const SVG_NS = 'http://www.w3.org/2000/svg';
const STATUS_POLL_MS = 10000;
const PAIRING_POLL_MS = 2000;
const SPEED_REFRESH_EVERY = 6; // status polls between background refreshes of the speed panel
const LIVE_CLEAR_MS = 5000;
const SELECTED_KEY = 'shoal.selected';
/**
 * The supported apps of ui-spec 8.3.12, one row each. An app moves from "Should work" to "Works" only when a real-device pass on the
 * shipping build is on record (T-CLI-13; the lab browser run for the web app). Flip a flag in the commit that names the test record.
 */
const APPS_ROWS = [
    { id: 'web', key: 'appsRowWeb' },
    { id: 'moonfinIos', key: 'appsRowMoonfinIos' },
    { id: 'moonfinOther', key: 'appsRowMoonfinOther' },
    { id: 'jmp', key: 'appsRowJmp' },
    { id: 'android', key: 'appsRowAndroid' }
];
const APPS_VERIFIED = { web: false, moonfinIos: false, moonfinOther: false, jmp: false, android: false };

/** Where the plugin is installed from: the link a friend gets when their server has no Shoal. */
const INSTALL_URL = 'https://github.com/Ninkxz/jellyfin-plugin-shoal';

const VIEWS = ['overview', 'libraries', 'settings', 'pair', 'server', 'overlap', 'friend'];
const TOP_VIEWS = ['overview', 'libraries', 'settings'];

/* Quality thresholds drawn on the chart (DESIGN 7.2: smooth if T >= 1.5 x reference bitrate). */
const QUALITY_LINES = [
    { q: '4k', mbps: 30, key: 'chartThr4k' },
    { q: '1080p', mbps: 15, key: 'chartThr1080' }
];

/* ====================================================================================== */
/* Strings                                                                                */
/* ====================================================================================== */

/**
 * Looks up copy and fills {placeholders}. Array entries are [one, other] chosen by vars.n.
 * An unknown key renders as itself, so a missing string is visible (and a test fails).
 */
const MISSING_STRINGS = new Set();

function t(key, vars) {
    let s = STRINGS[key];
    if (s === undefined) {
        MISSING_STRINGS.add(key);
        return key;
    }

    if (Array.isArray(s)) {
        s = vars && vars.n === 1 ? s[0] : s[1];
    }

    return vars ? fill(s, vars) : s;
}

function fill(s, vars) {
    return s.replace(/\{(\w+)\}/g, (m, k) => (Object.prototype.hasOwnProperty.call(vars, k) ? String(vars[k]) : m));
}

/**
 * Like t(), but a **double asterisk** pair in the copy makes the text between bold.
 * The copy is split first and the values are filled afterwards, so a value can never add markup.
 */
function tRich(key, vars) {
    let s = STRINGS[key];
    if (s === undefined) {
        MISSING_STRINGS.add(key);
    }

    if (Array.isArray(s)) {
        s = vars && vars.n === 1 ? s[0] : s[1];
    }

    const frag = document.createDocumentFragment();
    String(s === undefined ? key : s).split('**').forEach((part, i) => {
        const text = vars ? fill(part, vars) : part;
        if (!text) {
            return;
        }

        if (i % 2 === 1) {
            const b = document.createElement('strong');
            b.textContent = text;
            frag.append(b);
        } else {
            frag.append(document.createTextNode(text));
        }
    });
    return frag;
}

/* ====================================================================================== */
/* Formatting (ui-spec 11)                                                                */
/* ====================================================================================== */

function toMs(value) {
    if (value === null || value === undefined || value === '') {
        return null;
    }

    const n = value instanceof Date ? value.getTime() : typeof value === 'number' ? value : Date.parse(value);
    return Number.isFinite(n) ? n : null;
}

/** Builds the formatters for one locale and one clock. Pure, so tests can pin both. */
function makeFormat(locale, nowFn) {
    const nf = new Intl.NumberFormat(locale);
    const one = new Intl.NumberFormat(locale, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
    const timeF = new Intl.DateTimeFormat(locale, { hour: 'numeric', minute: '2-digit' });
    const dateF = new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric' });
    const dateYF = new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric', year: 'numeric' });
    const weekdayF = new Intl.DateTimeFormat(locale, { weekday: 'short' });

    const startOfDay = (ms) => {
        const d = new Date(ms);
        return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
    };

    const fmt = {
        num: (n) => nf.format(n),
        /** Speed: integer from 10 up, one decimal below 10. Unit not included. */
        mbpsValue: (n) => (n >= 10 ? nf.format(Math.round(n)) : one.format(n)),
        mbps: (n) => t('unitMbps', { v: fmt.mbpsValue(n) }),
        ms: (n) => t('unitMs', { v: nf.format(Math.round(n)) }),
        /** Base 10, one decimal for GB and TB. */
        bytes: (n) => {
            if (n >= 1e12) {
                return t('unitTB', { v: one.format(n / 1e12) });
            }

            if (n >= 1e9) {
                return t('unitGB', { v: one.format(n / 1e9) });
            }

            if (n >= 1e6) {
                return t('unitMB', { v: nf.format(Math.round(n / 1e6)) });
            }

            return t('unitKB', { v: nf.format(Math.max(0, Math.round(n / 1e3))) });
        },
        time: (value) => {
            const ms = toMs(value);
            return ms === null ? '' : timeF.format(new Date(ms));
        },
        date: (value) => {
            const ms = toMs(value);
            if (ms === null) {
                return '';
            }

            const sameYear = new Date(ms).getFullYear() === new Date(nowFn()).getFullYear();
            return (sameYear ? dateF : dateYF).format(new Date(ms));
        },
        weekday: (ms) => weekdayF.format(new Date(ms)),
        /** "just now", "4 min ago", "2 hours ago", "yesterday at 21:40", then a date. */
        relative: (value) => {
            const ms = toMs(value);
            if (ms === null) {
                return '';
            }

            const diff = (nowFn() - ms) / 1000;
            if (diff < 45) {
                return t('justNow');
            }

            if (diff < 3600) {
                return t('minAgo', { n: Math.max(1, Math.round(diff / 60)) });
            }

            if (diff < 86400 && startOfDay(ms) === startOfDay(nowFn())) {
                return t('hoursAgo', { n: Math.max(1, Math.round(diff / 3600)) });
            }

            if (startOfDay(nowFn()) - startOfDay(ms) === 86400000) {
                return t('yesterdayAt', { time: timeF.format(new Date(ms)) });
            }

            if (diff < 86400) {
                return t('hoursAgo', { n: Math.max(1, Math.round(diff / 3600)) });
            }

            return fmt.date(ms);
        },
        /** Whole minutes from now until a future time, at least 1. */
        minutesUntil: (value) => {
            const ms = toMs(value);
            return ms === null ? 0 : Math.max(1, Math.ceil((ms - nowFn()) / 60000));
        },
        /** "for 40 min", "for 2 hours", "for 3 days" (never "since 2 h"). */
        durationSince: (value) => {
            const ms = toMs(value);
            if (ms === null) {
                return '';
            }

            const mins = Math.max(1, Math.round((nowFn() - ms) / 60000));
            if (mins < 60) {
                return t('forMin', { n: mins });
            }

            if (mins < 2880) {
                return t('forHours', { n: Math.round(mins / 60) });
            }

            return t('forDays', { n: Math.round(mins / 1440) });
        },
        /** Countdown text: 9:41. */
        clock: (totalSeconds) => {
            const s = Math.max(0, Math.floor(totalSeconds));
            return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
        },
        startOfDay
    };
    return fmt;
}

/** "Rich, Sam and 1 more" from a list of names. */
function nameList(names, max) {
    const shown = names.slice(0, max);
    const more = names.length - shown.length;
    if (more > 0) {
        return t('namesAndMore', { names: shown.join(', '), n: more });
    }

    if (shown.length > 1) {
        return t('namesAnd', { names: shown.slice(0, -1).join(', '), last: shown[shown.length - 1] });
    }

    return shown.join('');
}

/* ====================================================================================== */
/* DOM helpers                                                                            */
/* ====================================================================================== */

const BANNED_ATTR = /^(on|style$|srcdoc$|formaction$)/i;
let uidCounter = 0;

function uid(prefix) {
    uidCounter += 1;
    return (prefix || 'shoal') + '-' + uidCounter;
}

function setAttr(node, name, value) {
    if (BANNED_ATTR.test(name)) {
        throw new Error('Shoal: attribute not allowed: ' + name);
    }

    if (value === false || value === null || value === undefined) {
        node.removeAttribute(name);
    } else {
        node.setAttribute(name, value === true ? '' : String(value));
    }
}

/** Sets a CSS custom property (the only way this file sizes or positions anything at runtime). */
function setVar(node, name, value) {
    if (!name.startsWith('--shoal-')) {
        throw new Error('Shoal: only --shoal-* custom properties may be set: ' + name);
    }

    node.style.setProperty(name, String(value));
}

function appendKids(node, kids) {
    for (const kid of kids.flat(Infinity)) {
        if (kid === null || kid === undefined || kid === false) {
            continue;
        }

        node.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
    }
}

/**
 * Creates an element. props: class, text, dataset {}, vars {--shoal-x: v}, anything else is an attribute
 * (null/false skips it, true makes a boolean attribute). Children may be nodes, strings or arrays.
 */
function el(tag, props, ...kids) {
    const node = document.createElement(tag);
    if (props) {
        for (const [key, value] of Object.entries(props)) {
            if (value === null || value === undefined || value === false) {
                continue;
            }

            if (key === 'class') {
                node.className = value;
            } else if (key === 'text') {
                node.textContent = value;
            } else if (key === 'dataset') {
                for (const [dk, dv] of Object.entries(value)) {
                    node.dataset[dk] = dv;
                }
            } else if (key === 'vars') {
                for (const [vk, vv] of Object.entries(value)) {
                    setVar(node, vk, vv);
                }
            } else {
                setAttr(node, key, value);
            }
        }
    }

    appendKids(node, kids);
    return node;
}

function svgEl(tag, attrs) {
    const node = document.createElementNS(SVG_NS, tag);
    if (attrs) {
        for (const [k, v] of Object.entries(attrs)) {
            setAttr(node, k, v);
        }
    }

    return node;
}

/** Inline icon from the sprite in shoal.html. Decorative: the text next to it carries the meaning. */
function icon(name, small) {
    const svg = svgEl('svg', { class: small ? 'shoal-icon shoal-icon--s' : 'shoal-icon', 'aria-hidden': 'true', focusable: 'false' });
    svg.append(svgEl('use', { href: '#shoal-i-' + name }));
    return svg;
}

function clear(node) {
    while (node.firstChild) {
        node.removeChild(node.firstChild);
    }

    return node;
}

function replaceKids(node, ...kids) {
    clear(node);
    appendKids(node, kids);
    return node;
}

function show(node, visible) {
    if (node) {
        node.hidden = !visible;
    }
}

function q(root, selector) {
    return root.querySelector(selector);
}

function slot(root, name) {
    return root.querySelector('[data-slot="' + name + '"]');
}

function qa(root, selector) {
    return Array.from(root.querySelectorAll(selector));
}

/** Fills data-t / data-t-aria / data-t-ph hooks from STRINGS. */
function applyStrings(root) {
    for (const node of qa(root, '[data-t]')) {
        node.textContent = t(node.getAttribute('data-t'));
    }

    for (const node of qa(root, '[data-t-aria]')) {
        node.setAttribute('aria-label', t(node.getAttribute('data-t-aria')));
    }

    for (const node of qa(root, '[data-t-ph]')) {
        node.setAttribute('placeholder', t(node.getAttribute('data-t-ph')));
    }

    return root;
}

/**
 * Gives every id inside root a per-instance suffix and rewrites the references to those ids.
 * Jellyfin keeps up to three page instances in the document, so static ids would collide.
 * Sprite symbols (shoal-i-*) and template ids are shared on purpose and left alone.
 */
function uniquify(root, suffix) {
    const nodes = qa(root, '[id]');
    if (root.id) {
        nodes.unshift(root);
    }

    const map = new Map();
    for (const node of nodes) {
        if (/^shoal-(i|t)-|^shoal-fish$/.test(node.id)) {
            continue;
        }

        const next = node.id + '-' + suffix;
        map.set(node.id, next);
        node.id = next;
    }

    if (map.size === 0) {
        return root;
    }

    const refs = ['for', 'aria-labelledby', 'aria-describedby', 'aria-controls'];
    const holders = qa(root, '[for],[aria-labelledby],[aria-describedby],[aria-controls]');
    if (refs.some((a) => root.hasAttribute && root.hasAttribute(a))) {
        holders.unshift(root);
    }

    for (const node of holders) {
        for (const a of refs) {
            if (node.hasAttribute(a)) {
                node.setAttribute(a, node.getAttribute(a).split(/\s+/).map((id) => map.get(id) || id).join(' '));
            }
        }
    }

    return root;
}

function safeStorage() {
    return {
        get(key) {
            try {
                return window.localStorage.getItem(key);
            } catch {
                return null;
            }
        },
        set(key, value) {
            try {
                window.localStorage.setItem(key, value);
            } catch {
                /* private window or blocked storage: the page works without it */
            }
        }
    };
}

function initialOf(name) {
    const ch = Array.from(String(name || '?').trim())[0] || '?';
    return ch.toLocaleUpperCase();
}

/* ====================================================================================== */
/* Admin API (DESIGN 9.6)                                                                 */
/* ====================================================================================== */

/**
 * Turns whatever ApiClient.ajax rejects with (a fetch Response, usually) into a small error.
 * Only the status, Retry-After and a whitelisted error kind survive: peer error bodies and
 * server messages are never shown (ui-spec 11).
 */
/** What the server said, from the text the client hands back: nothing is null, JSON is parsed, anything else is an error the page can name. */
function readAnswer(answer, noBody) {
    if (answer === undefined || answer === null) {
        return null;
    }

    if (typeof answer !== 'string') {
        return answer; // a client that already parsed it
    }

    const text = answer.trim();
    if (text === '') {
        return noBody ? '' : null;
    }

    try {
        return JSON.parse(text);
    } catch {
        const e = new Error('Shoal admin answer was not JSON');
        e.status = 0;
        e.retryAfter = 0;
        e.kind = 'unreadable';
        e.detail = '';
        throw e;
    }
}

/** Dotted version numbers compared by number: 1.0.10.0 is newer than 1.0.9.0. */
function compareVersions(a, b) {
    const pa = String(a).split('.').map((x) => Number(x) || 0);
    const pb = String(b).split('.').map((x) => Number(x) || 0);
    for (let i = 0; i < Math.max(pa.length, pb.length); i += 1) {
        const d = (pa[i] || 0) - (pb[i] || 0);
        if (d !== 0) {
            return d;
        }
    }

    return 0;
}

/** The version line and, when the catalog has a newer one, the update hint. Fills a host node; never fails the page. */
function fillVersion(ctx, host) {
    ctx.api.pluginVersion().then((v) => {
        if (!ctx.alive || !v || !v.installed) {
            return;
        }

        clear(host);
        host.append(el('span', { class: 'shoal-meta shoal-version__line', text: t('versionLine', { version: v.installed }) }));
        if (v.latest && compareVersions(v.latest, v.installed) > 0) {
            host.append(hint('info', 'info', t('versionNewer', { latest: v.latest })));
        }
    }).catch(() => {});
}

async function toApiError(err) {
    const status = err && typeof err.status === 'number' ? err.status : 0;
    let retryAfter = 0;
    let kind = '';
    let detail = '';
    try {
        const header = err && err.headers && typeof err.headers.get === 'function' ? err.headers.get('Retry-After') : null;
        retryAfter = header ? Math.max(0, Number(header) || 0) : 0;
        if (err && typeof err.json === 'function') {
            const body = await err.json();
            kind = body && typeof body.error === 'string' && /^[A-Za-z]{1,40}$/.test(body.error) ? body.error : '';
            detail = body && typeof body.detail === 'string' && /^[A-Za-z]{1,40}$/.test(body.detail) ? body.detail : '';
        }
    } catch {
        /* an unreadable error body is the same as no error body */
    }

    const e = new Error('Shoal admin request failed');
    e.status = status;
    e.retryAfter = retryAfter;
    e.kind = kind;
    e.detail = detail;
    return e;
}

/**
 * PairingSessionInfo (K5) to what the pairing screen works with. The server says where it is in `step` and whether it is
 * running, confirmed or aborted in `state`; the screen's own words are gettingCode, waiting, awaitingConfirm, done and failed.
 */
const REAL_STEPS = { preflight: 'waiting', initiating: 'waiting', awaitingApproval: 'waiting', validating: 'waiting', confirmed: 'done' };

function sessionFromServer(r) {
    if (!r || typeof r !== 'object') {
        return r;
    }

    const state = r.state === 'aborted' ? 'failed' : REAL_STEPS[r.step] || 'waiting';
    return Object.assign({}, r, { state, error: r.error || undefined, code: r.code || undefined, safetyCode: r.safetyCode || undefined, phase: r.phase || undefined, peerHandle: r.peerHandle || undefined });
}

/** ProbeResult (K5): a refused or unreachable server is a 200 with ok:false; the screen wants an error with a kind. */
function probeFromServer(r) {
    if (r && r.ok === false) {
        const kind = { WrongNetwork: 'wrongNetwork', ProxyNotAllowed: 'proxyNotAllowed', ProxyInvalid: 'proxyNotAllowed' }[r.addressProblem]
            || (r.addressProblem && r.addressProblem !== 'None' ? 'blocked' : { notJellyfin: 'notJellyfin', unreachable: 'unreachable', certificate: 'certificate', inviteWrongServer: 'inviteWrongServer', inviteInvalid: 'inviteGone', inviteExpired: 'inviteGone', inviteUsed: 'inviteGone', inviteLanHttp: 'inviteLanHttp' }[r.failure] || 'unreachable');
        const e = new Error('Shoal admin request failed');
        e.status = 400;
        e.retryAfter = 0;
        e.kind = kind;
        e.detail = '';
        throw e;
    }

    return Object.assign({}, r, { rttMs: r.latencyMs, warnings: r.warnings || [] });
}

/** The only code that talks to the server. client is Jellyfin's ApiClient. */
function createApi(client) {
    // The page always asks for text and reads the answer itself: a 200 or 204 with an empty body is a success, whatever a browser's JSON parser
    // says about an empty string (a rejected parse used to make a call that worked look like a failure; Firefox owner report). noBody is kept for the callers.
    function call(method, path, body, noBody) {
        const request = { type: method, url: client.getUrl(API_PREFIX + path), dataType: 'text' };
        if (body !== undefined) {
            request.data = JSON.stringify(body);
            request.contentType = 'application/json';
        }

        return client.ajax(request).then((answer) => readAnswer(answer, noBody), async (err) => {
            throw await toApiError(err);
        });
    }

    const enc = encodeURIComponent;
    return {
        status: () => call('GET', 'Status'),
        /** The installed Shoal version, and the newest one the plugin catalog offers (both best effort: null when the client cannot say). */
        pluginVersion: async () => {
            const raw = async (path) => {
                const answer = await client.ajax({ type: 'GET', url: client.getUrl(path), dataType: 'text' });
                return readAnswer(answer, false);
            };
            let installed = null;
            let latest = null;
            try {
                const plugins = await raw('Plugins');
                const mine = Array.isArray(plugins) ? plugins.find((x) => String(x.Name || x.name || '').toLowerCase() === 'shoal') : null;
                installed = mine ? String(mine.Version || mine.version || '') : null;
            } catch {
                installed = null;
            }

            try {
                const packages = await raw('Packages');
                const pkg = Array.isArray(packages) ? packages.find((x) => String(x.name || x.Name || '').toLowerCase() === 'shoal') : null;
                const versions = pkg ? (pkg.versions || pkg.Versions || []).map((v) => String(v.version || v.Version || '')).filter(Boolean) : [];
                latest = versions.sort(compareVersions).at(-1) || null;
            } catch {
                latest = null;
            }

            return { installed: installed || null, latest };
        },
        syncAll: () => call('POST', 'Sync'),
        peer: (h) => call('GET', 'Peers/' + enc(h)),
        updatePeer: (h, body) => call('PUT', 'Peers/' + enc(h), body),
        removePeer: (h, deleteLibrary, confirmName) => call('DELETE', 'Peers/' + enc(h) + '?deleteLibrary=' + (deleteLibrary ? 'true' : 'false'), { confirmName }),
        syncPeer: (h) => call('POST', 'Peers/' + enc(h) + '/Sync'),
        pausePeer: (h) => call('POST', 'Peers/' + enc(h) + '/Pause'),
        resumePeer: (h) => call('POST', 'Peers/' + enc(h) + '/Resume'),
        certificate: (h) => call('GET', 'Peers/' + enc(h) + '/Certificate'),
        trustCertificate: (h, body) => call('POST', 'Peers/' + enc(h) + '/TrustCertificate', body),
        remoteLibraries: (h) => call('GET', 'Peers/' + enc(h) + '/RemoteLibraries'),
        addLibrary: (h, list) => call('POST', 'Peers/' + enc(h) + '/Libraries', list),
        removeLibrary: (h, m) => call('DELETE', 'Peers/' + enc(h) + '/Libraries/' + enc(m)),
        hold: (h, kind, action) => call('POST', 'Peers/' + enc(h) + '/Holds/' + enc(kind) + '/' + enc(action)),
        releaseSeries: (h, kind, series) => call('POST', 'Peers/' + enc(h) + '/Holds/' + enc(kind) + '/' + enc('Release') + '?series=' + enc(series)),
        holds: (h) => call('GET', 'Peers/' + enc(h) + '/Holds'),
        overlap: (h) => call('GET', 'Peers/' + enc(h) + '/Overlap'),
        speed: (h, range) => call('GET', 'Peers/' + enc(h) + '/Speed?range=' + enc(range)),
        speedTest: (h, deep) => call('POST', 'Peers/' + enc(h) + '/SpeedTest', { deep: !!deep }),
        libraries: () => call('GET', 'Libraries'),
        setAccess: (m, body) => call('PUT', 'Libraries/' + enc(m) + '/Access', body),
        reconcile: (m) => call('POST', 'Libraries/' + enc(m) + '/Reconcile'),
        settings: () => call('GET', 'Settings'),
        saveSettings: (body) => call('PUT', 'Settings', body),
        state: () => call('GET', 'State'),
        restoreState: (file) => call('POST', 'State/Restore', { file }),
        startOver: (confirmName) => call('POST', 'State/StartOver', { confirmName }),
        deleteAll: (confirmName) => call('DELETE', 'All', { confirmName }),
        probe: (body) => call('POST', 'Peers/Probe', body).then(probeFromServer),
        startPairing: (body) => call('POST', 'Pairing', body).then(sessionFromServer),
        pairing: () => call('GET', 'Pairing').then((r) => (r && typeof r === 'object' ? sessionFromServer(r) : null)).catch((e) => {
            if (e.status === 404) {
                return null;
            }

            throw e;
        }),
        cancelPairing: () => call('DELETE', 'Pairing', undefined, true),
        shares: () => call('GET', 'Shares'),
        shareReach: () => call('GET', 'Shares/Reach'),
        share: (id) => call('GET', 'Shares/' + enc(id)),
        createShare: (body) => call('POST', 'Shares', body),
        updateShare: (id, body) => call('PUT', 'Shares/' + enc(id), body),
        deleteShare: (id, confirmName) => call('DELETE', 'Shares/' + enc(id), { confirmName }),
        deleteOrphanAccount: (id) => call('DELETE', 'OrphanAccounts/' + enc(id)),
        rotatePeer: (h) => call('POST', 'Peers/' + enc(h) + '/Rotate', {}),
        clearTrickplayCache: () => call('DELETE', 'TrickplayCache'),
        setRotation: (h, days) => call('PUT', 'Peers/' + enc(h) + '/Rotation', { days }),
        pauseShare: (id) => call('POST', 'Shares/' + enc(id) + '/Pause'),
        resumeShare: (id) => call('POST', 'Shares/' + enc(id) + '/Resume'),
        createInvite: (id) => call('POST', 'Shares/' + enc(id) + '/Invites'),
        cancelInvite: (id) => call('DELETE', 'Shares/' + enc(id) + '/Invites/current'),
        approval: (id) => call('GET', 'Shares/' + enc(id) + '/Approval'),
        decideApproval: (id, approve, sas) => call('POST', 'Shares/' + enc(id) + '/Approval', approve ? { approve: true, sas } : { approve: false }),
        dismissPairing: (h) => call('POST', 'Peers/' + enc(h) + '/DismissPairing', {}),
        confirmRestore: () => call('POST', 'State/ConfirmRestore', undefined, true),
        removeDevice: (id, device) => call('DELETE', 'Shares/' + enc(id) + '/Devices/' + enc(device)),
        hardening: () => call('GET', 'Hardening'),
        setHardening: (mode) => call('PUT', 'Hardening', { mode })
    };
}

/* ====================================================================================== */
/* Models: pure functions from API data to what the screens show (ui-spec 7)              */
/* ====================================================================================== */

/* ---------- pairing outcomes (ui-spec 7, 8.2.5, 8.2.9): one table for the card, the pairing page and the stored view ---------- */

/** The five kinds that stop a pairing for safety (DESIGN 4.2a "Two kinds"); everything else is an ordinary stop. */
const SAFETY_KINDS = ['serverChanged', 'adminAccount', 'badPeerResponse'];

/**
 * How each kind looks (icon, token) and which actions it offers. Texts live in STRINGS under out_<kind>_title / _body.
 * actions: start (Start again), change (Change the address), again (Try again, with a cooldown), newcode (Create a new invite is
 * the share side's and lives elsewhere), dismiss (only when the outcome is stored on a card).
 */
const OUTCOME_KINDS = {
    expired: { icon: 'clock', tone: 'warn', actions: ['start', 'dismiss'] },
    denied: { icon: 'x', tone: 'off', actions: ['start', 'dismiss'] },
    codeMismatch: { icon: 'x', tone: 'warn', actions: ['start', 'dismiss'] },
    inviteInvalid: { icon: 'x', tone: 'bad', actions: ['start'] },
    inviteReplaced: { icon: 'x', tone: 'warn', actions: ['start', 'dismiss'] },
    inviteCancelled: { icon: 'x', tone: 'warn', actions: ['start', 'dismiss'] },
    tokenNotCollected: { icon: 'clock', tone: 'warn', actions: ['start', 'dismiss'] },
    adminAccount: { icon: 'shield', tone: 'bad', actions: ['start', 'dismiss'] },
    serverChanged: { icon: 'shield', tone: 'bad', actions: ['start', 'dismiss'] },
    badPeerResponse: { icon: 'shield', tone: 'bad', actions: ['start', 'dismiss'] },
    interrupted: { icon: 'clock', tone: 'warn', actions: ['start', 'dismiss'] },
    rateLimited: { icon: 'clock', tone: 'warn', actions: ['again'] },
    network: { icon: 'offline', tone: 'off', actions: ['start', 'dismiss'] },
    server: { icon: 'x', tone: 'bad', actions: ['start'] },
    // A stored kind this version does not know (an old file): a calm generic card, never a crash.
    unknown: { icon: 'x', tone: 'off', actions: ['start', 'dismiss'] }
};

/**
 * What to say for one stored or live outcome. o: { server, home, code, method ('invite'|'qc'), phase, minutes, afterApproval }.
 * Returns { kind, safety, icon, tone, title, body, actions } or null for an unknown kind.
 */
function outcomeInfo(kind, o) {
    const known = OUTCOME_KINDS[kind] ? kind : 'unknown';
    const def = OUTCOME_KINDS[known];
    const p = o || {};
    let key = known;
    if (known === 'expired') {
        key = 'expiredInvite';
    } else if (known === 'interrupted') {
        key = 'interruptedInvite';
    }

    const vars = { server: p.server || t('thisServer'), home: p.home || '', code: p.code || '', m: p.minutes || 1 };
    return {
        kind: known,
        safety: SAFETY_KINDS.includes(known),
        icon: def.icon,
        tone: def.tone,
        title: t('out_' + key + '_title', vars),
        body: t('out_' + key + '_body', vars),
        actions: def.actions
    };
}


const SERVER_STATES = {
    online: { tone: 'ok', icon: 'ok', chip: 'chipOnline' },
    relayed: { tone: 'warn', icon: 'relay', chip: 'chipRelayed', hint: 'hintRelayed', action: 'test' },
    maybeRelayed: { tone: 'warn', icon: 'relay', chip: 'chipMaybeRelayed', hint: 'hintMaybeRelayed', action: 'test' },
    unstable: { tone: 'warn', icon: 'warn', chip: 'chipUnstable', hint: 'hintUnstable', action: 'test' },
    offline: { tone: 'off', icon: 'offline', chip: 'chipOffline', action: 'retry', dim: true },
    paused: { tone: 'off', icon: 'pause', chip: 'chipPaused', hint: 'hintPaused', action: 'resume', dim: true },
    accessRemoved: { tone: 'bad', icon: 'keyoff', chip: 'chipAccessRemoved', hint: 'hintAccessRemoved', hintTone: 'bad', action: 'repair' },
    differentServer: { tone: 'bad', icon: 'shield', chip: 'chipDifferentServer', hint: 'hintDifferentServer', hintTone: 'bad', action: 'repair' },
    certChanged: { tone: 'bad', icon: 'shield', chip: 'chipDifferentServer', hint: 'hintCertChanged', hintTone: 'bad', action: 'review' },
    pairing: { tone: 'info', icon: 'clock', chip: 'chipPairing', action: 'continuePair' },
    pairingSafety: { tone: 'bad', icon: 'shield', chip: 'chipPairingSafety', hintTone: 'bad', action: 'seeWhat' },
    pairingStopped: { tone: 'warn', icon: 'pause', chip: 'chipPairingStopped', hintTone: 'warn', action: 'seeWhat' },
    repairBlocked: { tone: 'warn', icon: 'keyoff', chip: 'repairBlockedChip', hint: 'repairBlockedHint', hintTone: 'warn', action: 'openRepair', dim: true },
    needsRepair: { tone: 'warn', icon: 'keyoff', chip: 'chipNeedsRepair', hint: 'hintNeedsRepair', hintTone: 'warn', action: 'repairInvite', dim: true }
};

const ACTION_LABELS = {
    test: 'testLinkAgain',
    retry: 'retryNow',
    resume: 'resume',
    repair: 'pairAgain',
    review: 'reviewCertificate',
    continuePair: 'continuePairing',
    seeWhat: 'seeWhatToDo',
    repairInvite: 'repairWithInvite',
    openRepair: 'removeOrChangeAddress'
};

function serverStateKey(s) {
    // A server paired the old way (Quick Connect) is not run any more: it needs a new pairing by invite (D17).
    if ((s.needsRepair === true || s.pairingMethod === 'qc') && s.status !== 'Pairing' && s.status !== 'PairingAborted') {
        return s.repairBlockedByHttp === true ? 'repairBlocked' : 'needsRepair';
    }

    switch (s.status) {
        case 'Online':
            return s.path === 'derp' ? 'relayed' : s.path === 'derp?' ? 'maybeRelayed' : 'online';
        case 'Degraded':
            return 'unstable';
        case 'Offline':
            return 'offline';
        case 'Disabled':
            return 'paused';
        case 'AuthFailed':
            return 'accessRemoved';
        case 'IdentityMismatch':
            return 'differentServer';
        case 'TlsKeyChanged':
            return 'certChanged';
        case 'Pairing':
            return 'pairing';
        case 'PairingAborted':
            return SAFETY_KINDS.includes(s.pairingError) ? 'pairingSafety' : 'pairingStopped';
        default:
            return 'offline';
    }
}

function hostOf(url) {
    try {
        return new URL(url).hostname;
    } catch {
        return '';
    }
}

/** Everything a server card (or the detail header) needs to show, decided in one place. */
function describeServer(s, fmt, opts) {
    const keysLocked = !!(opts && opts.keysLocked);
    const key = serverStateKey(s);
    const def = SERVER_STATES[key];
    const online = key === 'online' || key === 'relayed' || key === 'maybeRelayed' || key === 'unstable';
    const syncing = !!s.syncing && online;
    const name = s.displayName;
    const host = hostOf(s.url);
    const model = {
        key,
        name,
        host,
        tone: syncing ? 'info' : def.tone,
        icon: syncing ? 'sync' : def.icon,
        spin: syncing,
        chipText: t(syncing ? 'chipSyncing' : def.chip),
        dim: !!def.dim,
        online,
        stale: !online && key !== 'pairing',
        attention: def.tone === 'warn' || def.tone === 'bad' || (s.alertCount || 0) > 0,
        hint: null,
        action: null,
        verdict: ''
    };

    if (def.hint) {
        model.hint = {
            tone: def.hintTone || 'warn',
            text: t(def.hint, { server: name, host, jitter: Math.round(s.jitterMs || 0), loss: s.lossPct || 0 })
        };
    }

    if (key === 'pairingSafety' || key === 'pairingStopped') {
        // The card says what happened in one sentence: the safety hint for the five safety kinds, otherwise the outcome's title (ui-spec 7).
        const info = outcomeInfo(s.pairingError, { server: name, method: s.pairingMethod, phase: s.pairingPhase, afterApproval: s.pairingPhase === 'awaitingConfirm' });
        model.hint = {
            tone: def.hintTone,
            text: key === 'pairingSafety' ? t('hintSafety_' + info.kind, { server: name }) : info.title
        };
        model.neverPaired = s.paired === false;
    }

    if (def.action) {
        model.action = { kind: def.action, label: t(ACTION_LABELS[def.action]) };
    }

    if (keysLocked && key !== 'pairing' && key !== 'pairingStopped' && key !== 'pairingSafety') {
        // Banner 8.8 explains; every card offers "Pair again" and says it is not in use (ui-spec 7).
        model.keysLocked = true;
        model.hint = null;
        model.action = { kind: 'repair', label: t(ACTION_LABELS.repair) };
        model.verdict = t('notUsedUntilKeys');
        return model;
    }

    if (key === 'offline') {
        model.verdict = s.offlineSinceUtc
            ? t('verdictOffline', { duration: fmt.durationSince(s.offlineSinceUtc) })
            : t('verdictOfflineShort');
    } else if (online) {
        model.verdict = verdictLine(s.verdictSummary || []);
    }

    return model;
}

const VERDICT_CLASS_NAMES = { '720p': '720p', '1080p': '1080p', '4k': '4K', remux: 'remux', remux1080: '1080p remux', remux4k: '4K remux' };

/** "1080p smooth · 4K needs transcode · remux will buffer" (DESIGN 7.2 summary row). */
function verdictLine(summary) {
    return summary
        .map((v) => (VERDICT_CLASS_NAMES[v.cls] || String(v.cls)) + ' ' + t('verdictWord_' + v.verdict))
        .join(' · ');
}

/** The network class of a server as the lower-case word the page compares (the API says Tailnet, LAN or Public). */
function netClass(s) {
    return String(s.network || '').toLowerCase();
}

/** The via line: icon plus text (ui-spec 6.4 item 3). */
function viaOf(s) {
    const proxy = s.proxy ? t('viaThroughProxy') : '';
    if (netClass(s) === 'public') {
        return { icon: 'globe', text: t('viaInternet'), host: hostOf(s.url), proxy };
    }

    if (netClass(s) === 'lan') {
        return { icon: 'home', text: t('viaHome'), host: hostOf(s.url), proxy };
    }

    return { icon: 'lock', text: t('viaTailscale'), host: hostOf(s.url), proxy };
}

/** Connection metric: label plus icon, or null when unknown. */
function connectionOf(s, online) {
    if (!online) {
        return null;
    }

    switch (s.path) {
        case 'direct':
            return { icon: 'direct', text: t('connDirect') };
        case 'derp':
            return { icon: 'relay', text: t('connRelayed') };
        case 'derp?':
            return { icon: 'relay', text: t('connMaybeRelayed') };
        case 'proxy':
            return { icon: 'lock', text: t('connProxy') };
        default:
            return null;
    }
}

const FRIEND_STATES = {
    Active: { tone: 'ok', icon: 'ok', text: 'friendActive' },
    Paused: { tone: 'off', icon: 'pause', text: 'friendPaused' },
    AccessRemoved: { tone: 'bad', icon: 'keyoff', text: 'friendRemoved' }
};

/** A count with its plural word and thousands separators ("1,204 titles"): the plural is picked by the number, the digits are formatted after. */
function countText(ctx, key, n) {
    return t(key, { n }).replace(String(n), ctx.fmt.num(n));
}

/** A status word of the two-way cards (DESIGN v2.6.2): the server words and the share words in one chip. */
const LINK_STATUS = {
    Online: { tone: 'ok', icon: 'ok', text: 'chipOnline' },
    Active: { tone: 'ok', icon: 'ok', text: 'chipOnline' },
    Degraded: { tone: 'warn', icon: 'warn', text: 'chipUnstable' },
    Offline: { tone: 'off', icon: 'offline', text: 'chipOffline' },
    Paused: { tone: 'off', icon: 'pause', text: 'chipPaused' },
    Disabled: { tone: 'off', icon: 'pause', text: 'chipPaused' },
    AuthFailed: { tone: 'bad', icon: 'keyoff', text: 'chipAccessRemoved' },
    AccessRemoved: { tone: 'bad', icon: 'keyoff', text: 'chipAccessRemoved' },
    IdentityMismatch: { tone: 'bad', icon: 'shield', text: 'chipDifferentServer' },
    TlsKeyChanged: { tone: 'bad', icon: 'shield', text: 'chipDifferentServer' },
    PairingAborted: { tone: 'warn', icon: 'warn', text: 'chipPairingStopped' },
    Pairing: { tone: 'info', icon: 'sync', text: 'chipPairing' }
};

function describeLinkStatus(word) {
    const def = LINK_STATUS[word] || { tone: 'off', icon: 'info', text: 'chipStatusUnknown' };
    return { tone: def.tone, icon: def.icon, text: t(def.text) };
}

function describeFriend(f) {
    // A share nobody has connected to yet is not "Active" (Pilot 07 D2): it is waiting for the friend.
    if (f.status === 'Active' && (f.deviceCount || 0) === 0) {
        return { tone: 'info', icon: 'clock', text: t('friendWaiting') };
    }

    const def = FRIEND_STATES[f.status] || FRIEND_STATES.Paused;
    return { tone: def.tone, icon: def.icon, text: t(def.text) };
}

/** Header summary for the Overview (ui-spec 8.1.1 item 2). */
function summarizeServers(servers, fmt, friends) {
    const states = servers.map((s) => describeServer(s, fmt));
    const flaggedFriends = (friends || []).filter((f) => (f.unexpectedDeviceCount || 0) > 0).map((f) => f.handle);
    return {
        total: servers.length,
        online: states.filter((m) => m.online).length,
        attention: servers.filter((s, i) => states[i].attention).map((s) => s.handle),
        attentionFriends: flaggedFriends,
        // Titles of servers that are in use now (ui-spec 0.3 #9): an offline or paused server is not counted.
        titles: servers.reduce((sum, s, i) => sum + (states[i].online ? s.titleCount || 0 : 0), 0),
        titlesOffline: servers.reduce((sum, s, i) => sum + (states[i].online || isPausedKey(states[i].key) ? 0 : s.titleCount || 0), 0),
        titlesPaused: servers.reduce((sum, s, i) => sum + (isPausedKey(states[i].key) ? s.titleCount || 0 : 0), 0)
    };
}

/** A server paused until it is paired again by invite is paused, not offline (N16). */
function isPausedKey(key) {
    return key === 'needsRepair' || key === 'repairBlocked';
}

/** Default selection (ui-spec 8.1.3): first online server by name, else the first. */
function pickSelection(servers, remembered, fmt) {
    const sorted = sortServers(servers);
    if (remembered && sorted.some((s) => s.handle === remembered)) {
        return remembered;
    }

    const firstOnline = sorted.find((s) => describeServer(s, fmt).online);
    return (firstOnline || sorted[0] || { handle: null }).handle;
}

function sortServers(servers) {
    return servers.slice().sort((a, b) => String(a.displayName).localeCompare(String(b.displayName)));
}

/** Ladder row verdicts (ui-spec 7). */
const LADDER_ROWS = {
    overview: ['remux4k', '4k', '1080p', '720p'],
    detail: ['remux4k', 'remux1080', '4k', '1080p', '1080pTranscode', '720p']
};
const LADDER_VERDICTS = {
    smooth: { tone: 'ok', icon: 'check', key: 'ladderSmooth' },
    mayBuffer: { tone: 'warn', icon: 'warn', key: 'ladderMayBuffer' },
    needsTranscode: { tone: 'warn', icon: 'warn', key: 'ladderNeedsTranscode' },
    willBuffer: { tone: 'bad', icon: 'x', key: 'ladderWillBuffer' }
};

function ladderRows(ladder, which) {
    const byClass = new Map((ladder || []).map((r) => [r.cls, r]));
    return LADDER_ROWS[which].filter((cls) => byClass.has(cls)).map((cls) => ({ cls, ...byClass.get(cls) }));
}

function bottleneckText(bottleneck, serverName) {
    if (!bottleneck || !bottleneck.kind || bottleneck.kind === 'none') {
        return '';
    }

    const key = 'bottleneck_' + bottleneck.kind + (bottleneck.kind === 'relay' && bottleneck.region ? 'Region' : '');
    return STRINGS[key] === undefined ? '' : t(key, { server: serverName, region: bottleneck.region || '' });
}

/* ====================================================================================== */
/* Components (ui-spec 6)                                                                 */
/* ====================================================================================== */

/** Status chip: icon + text, never color alone (6.3). */
function chip(tone, iconName, text, id) {
    const node = el('span', { class: 'shoal-chip shoal-chip--' + tone, id }, icon(iconName, true), el('span', { class: 'shoal-chip__text', text }));
    if (iconName === 'sync') {
        node.classList.add('is-spin');
    }

    return node;
}

/** Hint box (6.4 item 6): note role, tinted background, icon plus text. */
function hint(tone, iconName, ...content) {
    return el('div', { class: 'shoal-hint shoal-hint--' + tone, role: tone === 'bad' ? null : 'note' }, icon(iconName, true), el('div', { class: 'shoal-hint__body' }, content));
}

function paragraph(text, cls) {
    return el('p', { class: cls || 'shoal-body-text', text });
}

/**
 * Buttons (6.2). Only <button is="emby-button"> is registered by Jellyfin, everything else is styled
 * here. variant: quiet | primary | danger.
 */
function button(opts) {
    const o = opts || {};
    const variant = o.variant || 'quiet';
    const cls = ['emby-button', 'shoal-btn'];
    if (variant === 'primary') {
        cls.unshift('raised', 'button-submit', 'shoal-btn--primary');
    } else if (variant === 'danger') {
        cls.unshift('raised', 'button-delete', 'shoal-btn--danger');
    } else {
        cls.unshift('raised');
    }

    if (o.small) {
        cls.push('shoal-btn--small');
    }

    const node = el('button', {
        is: 'emby-button',
        type: o.type || 'button',
        class: cls.join(' '),
        id: o.id,
        disabled: o.disabled,
        'aria-describedby': o.describedby,
        'data-action': o.action
    });
    if (o.icon) {
        node.append(icon(o.icon));
    }

    node.append(el('span', { class: 'shoal-btn__label', text: o.label }));
    if (o.onClick) {
        node.addEventListener('click', (ev) => {
            if (node.getAttribute('aria-disabled') === 'true' || node.disabled) {
                ev.preventDefault();
                return;
            }

            try {
                const result = o.onClick(ev, node);
                if (result && typeof result.catch === 'function') {
                    result.catch((err) => reportUnexpected(err));
                }
            } catch (err) {
                reportUnexpected(err);
            }
        });
    }

    return node;
}

/** The page that is mounted now, so a click handler that throws can still say so on screen (set by mount). */
let activeReporter = null;

/** An error nobody planned for becomes visible text with its name, not a button that does nothing. */
function reportUnexpected(err) {
    const detail = (err && (err.name ? err.name + ': ' : '') + (err.message || err)) || 'unknown';
    if (activeReporter) {
        activeReporter(t('unexpectedError', { detail: String(detail).slice(0, 200) }));
    }
}

/** Busy state (6.2): label becomes the progress text, icon spins, stays focusable, clicks ignored. */
function setBusy(btn, busy, busyText) {
    const label = btn.querySelector('.shoal-btn__label');
    if (!label) {
        return;
    }

    if (busy) {
        if (btn.dataset.label === undefined) {
            btn.dataset.label = label.textContent;
        }

        label.textContent = busyText;
        btn.setAttribute('aria-disabled', 'true');
        btn.classList.add('is-busy');
    } else if (btn.dataset.label !== undefined) {
        label.textContent = btn.dataset.label;
        delete btn.dataset.label;
        btn.removeAttribute('aria-disabled');
        btn.classList.remove('is-busy');
    }
}

/** Link-looking button for in-page actions (no hash links: the dashboard routes on the hash). */
function linkButton(label, onClick, extraClass) {
    const node = el('button', { type: 'button', class: 'button-link shoal-link ' + (extraClass || ''), text: label });
    if (onClick) {
        node.addEventListener('click', onClick);
    }

    return node;
}

/** Text input (6.7). Returns the wrapper plus handles to read and flag it. */
function textField(o) {
    const helpId = o.help ? uid('shoal-help') : null;
    const errId = uid('shoal-err');
    const input = el('input', {
        class: 'emby-input shoal-input' + (o.mono ? ' shoal-input--mono' : '') + (o.cls ? ' ' + o.cls : ''),
        id: o.id,
        type: o.type || 'text',
        value: o.value,
        required: o.required,
        maxlength: o.maxlength,
        inputmode: o.inputmode,
        autocomplete: o.autocomplete || 'off',
        autocapitalize: o.autocapitalize,
        spellcheck: o.spellcheck === undefined ? null : String(o.spellcheck),
        placeholder: o.placeholder,
        disabled: o.disabled,
        min: o.min,
        max: o.max,
        step: o.step
    });
    if (o.value !== undefined && o.value !== null) {
        input.value = o.value;
    }

    const errText = el('span', { class: 'shoal-fieldError__text' });
    const err = el('p', { class: 'shoal-fieldError', id: errId, hidden: true }, icon('x', true), errText);
    const helpNode = o.help ? el('div', { class: 'fieldDescription shoal-help', id: helpId, text: o.help }) : null;
    const root = el('div', { class: 'inputContainer shoal-field' },
        el('label', { class: 'inputLabel shoal-label', for: o.id, text: o.label }),
        input,
        helpNode,
        err);
    input.setAttribute('aria-describedby', [helpId, errId].filter(Boolean).join(' '));
    return {
        root,
        input,
        help: helpNode,
        setError(message) {
            const has = !!message;
            err.hidden = !has;
            errText.textContent = has ? message : '';
            input.setAttribute('aria-invalid', has ? 'true' : 'false');
            if (!has) {
                input.removeAttribute('aria-invalid');
            }
        },
        setHelp(text) {
            if (helpNode) {
                helpNode.textContent = text;
            }
        },
        value: () => input.value.trim()
    };
}

/** Select (6.7): native select under Jellyfin's class names, chevron as an inline svg. */
function selectField(o) {
    const helpId = o.help ? uid('shoal-help') : null;
    const select = el('select', { class: 'emby-select-withcolor emby-select shoal-select', id: o.id, disabled: o.disabled });
    for (const opt of o.options) {
        select.append(el('option', { value: opt.value, text: opt.label }));
    }

    if (o.value !== undefined && o.value !== null) {
        select.value = String(o.value);
    }

    const helpNode = o.help ? el('div', { class: 'fieldDescription shoal-help', id: helpId, text: o.help }) : null;
    if (helpId) {
        select.setAttribute('aria-describedby', helpId);
    }

    const root = el('div', { class: 'selectContainer shoal-field' },
        el('label', { class: 'selectLabel shoal-label', for: o.id, text: o.label }),
        el('div', { class: 'shoal-selectwrap' }, select, icon('down')),
        helpNode);
    return { root, select, help: helpNode, setHelp: (text) => helpNode && (helpNode.textContent = text), value: () => select.value };
}

/** Checkbox row (6.7): look of emby-checkbox, native input. */
function checkRow(o) {
    const input = el('input', { type: 'checkbox', id: o.id, name: o.name, checked: o.checked, disabled: o.disabled });
    if (o.checked) {
        input.checked = true;
    }

    const noteId = o.note ? uid('shoal-note') : null;
    if (noteId) {
        input.setAttribute('aria-describedby', noteId);
    }

    const root = el('label', { class: 'shoal-check' + (o.disabled ? ' is-disabled' : '') },
        input,
        el('span', { class: 'shoal-check__text' },
            el('span', { class: 'shoal-check__label', text: o.label }),
            o.note ? el('span', { class: 'shoal-check__note shoal-meta', id: noteId, text: o.note }) : null));
    if (o.onChange) {
        input.addEventListener('change', () => o.onChange(input.checked, input));
    }

    return { root, input };
}

/** Radio choice card (6.7). */
function choiceCard(o) {
    const input = el('input', { type: 'radio', name: o.name, value: o.value, id: o.id, checked: o.checked, disabled: o.disabled });
    if (o.checked) {
        input.checked = true;
    }

    const root = el('label', { class: 'shoal-choice' + (o.disabled ? ' is-disabled' : '') },
        input,
        el('span', { class: 'shoal-choice__title' }, icon('check', true), el('span', { text: o.title })),
        el('span', { class: 'shoal-choice__body shoal-meta', text: o.body }));
    input.addEventListener('focus', () => root.classList.add('is-focus'));
    input.addEventListener('blur', () => root.classList.remove('is-focus'));
    if (o.onChange) {
        input.addEventListener('change', () => input.checked && o.onChange(o.value));
    }

    return { root, input };
}

/** Collapsible section (6.8): native details/summary. */
function collapse(summaryText, content, open) {
    return el('details', { class: 'shoal-collapse', open: open || null, 'data-key': summaryText },
        el('summary', {}, el('span', { text: summaryText }), icon('down')),
        el('div', { class: 'shoal-collapse__body' }, content));
}

/** Which disclosures are open, by their summary text, so a redraw can put them back (Lumen M5). */
function openKeys(root) {
    return new Set(Array.from(root.querySelectorAll('details[data-key][open]')).map((d) => d.getAttribute('data-key')));
}

function restoreOpen(root, keys) {
    for (const d of root.querySelectorAll('details[data-key]')) {
        if (keys.has(d.getAttribute('data-key'))) {
            d.open = true;
        }
    }
}

/** Time-range segmented control (6.6): aria-pressed toggle buttons. */
function segmented(options, value, onChange, labelText) {
    const root = el('div', { class: 'shoal-seg', role: 'group', 'aria-label': labelText });
    const buttons = options.map((opt) => {
        const b = el('button', { type: 'button', 'aria-pressed': String(opt.value === value), 'data-range': opt.value, text: opt.label });
        b.addEventListener('click', () => {
            if (b.getAttribute('aria-pressed') === 'true') {
                return;
            }

            buttons.forEach((x) => x.setAttribute('aria-pressed', String(x === b)));
            onChange(opt.value);
        });
        return b;
    });
    root.append(...buttons);
    return root;
}

/** Wide tables scroll inside their own focusable region (12.6). */
function dataTable(o) {
    const head = el('tr', {}, o.columns.map((c) => el('th', { scope: 'col', class: c.num ? 'is-num' : null, text: c.label })));
    const body = el('tbody', {}, o.rows.map((row) => el('tr', {}, o.columns.map((c) => {
        const cell = row[c.key];
        return el('td', { class: c.num ? 'is-num' : null, 'data-label': c.label }, cell instanceof Node ? cell : String(cell === undefined || cell === null ? '' : cell));
    }))));
    const table = el('table', { class: 'shoal-table' },
        el('caption', { class: 'shoal-sr', text: o.caption }),
        el('thead', {}, head),
        body);
    return el('div', { class: 'shoal-tablewrap', tabindex: '0', role: 'region', 'aria-label': o.caption }, table);
}

/** Definition list of facts (server name, version, address...). */
function factList(items) {
    return el('dl', { class: 'shoal-facts' }, items.map(([label, value]) => el('div', { class: 'shoal-facts__row' },
        el('dt', { text: label }),
        el('dd', {}, value instanceof Node ? value : String(value)))));
}

/** An unknown value: a dash for sight, "unknown" for speech (ui-spec 6.4 item 4). */
function unknownValue() {
    return [el('span', { 'aria-hidden': 'true', text: '—' }), el('span', { class: 'shoal-sr', text: t('unknown') })];
}

/* ---------- panels and sections ---------- */

function panel(titleText, opts) {
    const o = opts || {};
    const headId = uid('shoal-h');
    const head = el('div', { class: 'shoal-panel__head' },
        el('div', { class: 'shoal-panel__titles' },
            el('h2', { class: 'shoal-h2', id: headId, text: titleText }),
            o.sub ? el('p', { class: 'shoal-panel__sub', text: o.sub }) : null),
        o.controls || null);
    const body = el('div', { class: 'shoal-panel__body' });
    const root = el('section', { class: 'shoal-panel' + (o.cls ? ' ' + o.cls : ''), 'aria-labelledby': headId }, head, body);
    return { root, body, head };
}

function section(titleText, opts) {
    const o = opts || {};
    const headId = uid('shoal-h');
    const head = el('div', { class: 'shoal-section__head' },
        el('h2', { class: 'shoal-h2', id: headId, text: titleText }),
        o.aside || null);
    const body = el('div', { class: 'shoal-section__body' });
    const root = el('section', { class: 'shoal-section' + (o.cls ? ' ' + o.cls : ''), 'aria-labelledby': headId }, head, o.intro ? el('p', { class: 'shoal-meta shoal-section__intro', text: o.intro }) : null, body);
    return { root, body, head };
}

/* ---------- code display (6.10): safety codes, Quick Connect code, invite, Server ID ---------- */

/** "K7QM" read letter by letter for the accessible name: "K 7 Q M". */
function spellOut(chars) {
    return Array.from(chars).join(' ');
}

/**
 * kind: qc (6 digits, shown 482 913), check (4 characters, K7QM), safety (8 characters, K7QM 2XA4).
 * Groups are separate spans with a gap, never a space character, so a copy or a screen reader gets clean
 * groups. The accessible name spells the code character by character; the visible text is hidden from readers.
 */
function codeBox(kind, value) {
    const raw = value === null || value === undefined || value === 'undefined' ? '' : String(value).replace(/[\s-]/g, '');
    let groups;
    if (kind === 'safety') {
        groups = [raw.slice(0, 4), raw.slice(4)];
    } else {
        groups = [raw];
    }

    const label = t('safetyCodeLabel', { code: groups.map(spellOut).join(', ') });
    const visible = el('span', { 'aria-hidden': 'true' }, groups.filter(Boolean).map((g) => el('span', { class: 'shoal-code__group', text: g })));
    return el('div', { class: 'shoal-code shoal-code--md', role: 'group', 'aria-label': label, dataset: { copy: raw } }, visible);
}

/** A code box with its small label above it ("Safety code"). */
function labelledCode(kind, value, labelKey) {
    return el('div', { class: 'shoal-codewrap' }, el('span', { class: 'shoal-codewrap__label', text: t(labelKey || 'safetyCodeWord') }), codeBox(kind, value));
}

/** The one-time invite string (6.10): shown once, selectable, wraps anywhere. Lives only in memory. */
function inviteBox(text) {
    return el('div', { class: 'shoal-invitebox' }, el('code', { class: 'shoal-codesm shoal-invitecode', text }));
}

/** Server ID (8 hex characters, 6.10): code-sm in fact lists. Never labelled "code", never copyable. */
function serverIdText(prefix) {
    return el('span', { class: 'shoal-codesm', text: prefix || '—' });
}

/* ---------- step checklist (6.19) ---------- */

const CHECK_STATES = {
    done: { icon: 'ok', tone: 'ok', word: 'checkDone' },
    working: { icon: 'sync', tone: 'info', word: 'checkWorking' },
    failed: { icon: 'x', tone: 'bad', word: 'checkFailed' },
    todo: { icon: null, tone: null, word: 'checkTodo' }
};

/**
 * items: [{ state: done|working|failed|todo, lead?, text, action?: Node }]. A failed row's first sentence is bold
 * in the bad color. Every icon has visually hidden status text, so the state is never carried by the icon alone.
 */
function stepChecklist(items) {
    return el('ol', { class: 'shoal-checklist' }, items.map((it, i) => {
        const def = CHECK_STATES[it.state || 'todo'];
        const mark = def.icon
            ? el('span', { class: 'shoal-checklist__icon shoal-tone--' + def.tone }, icon(def.icon), el('span', { class: 'shoal-sr', text: t(def.word) + ': ' }))
            : el('span', { class: 'shoal-checklist__icon' }, el('span', { class: 'shoal-checklist__n', 'aria-hidden': 'true', text: String(it.n || i + 1) }), el('span', { class: 'shoal-sr', text: t(def.word) + ': ' }));
        const text = el('span', { class: 'shoal-checklist__text' }, it.lead ? el('span', { class: 'shoal-checklist__lead', text: it.lead + ' ' }) : null, it.text);
        return el('li', { class: 'is-' + (it.state || 'todo') }, mark, text, it.action ? el('span', { class: 'shoal-checklist__action' }, it.action) : null);
    }));
}

/* ---------- approval card (6.18): the sharing side types the safety code; it is never shown here ---------- */

/**
 * o: { friend, approval: { homeName, homeServerIdPrefix, addressClass, triesLeft }, reconnect (the friend has connected before),
 * onApprove(sas) -> Promise<{ match, triesLeft, state }>, onDeny() -> Promise, onResolved(result) }.
 * homeName comes from the friend's server and is only ever set as text. The code the admin types is held in the input only:
 * it is never stored, logged or put in a URL.
 */
function approvalCard(ctx, o) {
    const a = o.approval;
    const titleId = uid('shoal-appr-t');
    const inputId = uid('shoal-appr-i');
    const helpId = uid('shoal-appr-h');
    const errId = uid('shoal-appr-e');
    const input = el('input', {
        class: 'emby-input shoal-input shoal-input--code', id: inputId, type: 'text', autocomplete: 'off', autocapitalize: 'characters',
        spellcheck: 'false', inputmode: 'text', maxlength: '11', 'aria-describedby': helpId + ' ' + errId
    });
    const help = el('div', { class: 'fieldDescription shoal-help', id: helpId, text: t('pv2ShCodeHelp', { friend: o.friend }) });
    const err = el('p', { class: 'shoal-fieldError', id: errId, hidden: true }, icon('x', true), el('span', { class: 'shoal-fieldError__text' }));
    const field = el('div', { class: 'inputContainer shoal-field' }, el('label', { class: 'inputLabel shoal-label', for: inputId, text: t('pv2ShCodeLabel', { friend: o.friend }) }), input, help, err);
    const reason = el('p', { class: 'shoal-meta shoal-approval__reason', text: t('approveReason') });
    const approve = button({ label: t('approve'), variant: 'primary', disabled: true, onClick: (ev, btn) => submit(btn) });
    const deny = button({ label: t('deny'), onClick: (ev, btn) => runAction(ctx, btn, { busyText: t('denying'), run: () => o.onDeny(), onDone: (r) => o.onResolved(r || { state: 'denied' }), onError: (e) => ctx.alertMessage(failureMessage(e)) }) });

    function setError(message) {
        err.hidden = !message;
        err.querySelector('.shoal-fieldError__text').textContent = message || '';
        if (message) {
            input.setAttribute('aria-invalid', 'true');
        } else {
            input.removeAttribute('aria-invalid');
        }
    }

    function sync() {
        const ok = validCode(input.value);
        approve.disabled = !ok;
        reason.hidden = ok;
    }

    input.addEventListener('input', () => {
        // Capitals as you type, without moving the caret (6.10). Spaces and hyphens are allowed and ignored.
        const start = input.selectionStart;
        const end = input.selectionEnd;
        const up = input.value.toUpperCase();
        if (up !== input.value) {
            input.value = up;
            try {
                input.setSelectionRange(start, end);
            } catch {
                /* some input types have no selection */
            }
        }

        setError('');
        sync();
    });
    // 7.1: a Quick Connect code, or something that is not 8 letters and numbers, gets a sentence that says what it is.
    const wrongKind = () => classifyCode(input.value, 'safety');
    input.addEventListener('blur', () => {
        const c = wrongKind();
        if (input.value.trim() !== '' && (c || !validCode(input.value))) {
            setError(t(c ? c.key : 'pv2CodeSafetyShape'));
        }
    });

    async function submit(btn) {
        if (!validCode(input.value)) {
            const c = wrongKind();
            setError(t(c ? c.key : 'pv2CodeSafetyShape'));
            input.focus();
            return;
        }

        const result = await runAction(ctx, btn, {
            busyText: t('checking'),
            run: () => o.onApprove(normalizeCode(input.value)),
            onError: (e) => {
                ctx.alertMessage(failureMessage(e));
            }
        });
        if (!result) {
            return;
        }

        if (result.match === false && result.state !== 'cancelledWrongCode') {
            const n = typeof result.triesLeft === 'number' ? result.triesLeft : 0;
            const message = t('codeWrong', { friend: o.friend, n });
            setError(message);
            ctx.alertMessage(message);
            input.focus();
            input.select();
            return;
        }

        o.onResolved(result);
    }

    const facts = factList([[t('factServerId'), serverIdText(a.homeServerIdPrefix)], [t('factFrom'), t('addr_' + a.addressClass)]]);
    const root = el('section', { class: 'shoal-panel shoal-approval', 'aria-labelledby': titleId },
        el('p', { class: 'shoal-h3 shoal-approval__home', id: titleId, text: t('approvalTitle', { home: a.homeName }) }),
        facts,
        el('p', { class: 'shoal-body-text', text: t('approvalCall', { friend: o.friend }) }),
        o.reconnect ? el('p', { class: 'shoal-meta', text: t('approvalReconnect', { friend: o.friend }) }) : null,
        a.restarted === true ? el('p', { class: 'shoal-meta shoal-approval__restarted', text: t('pv2ShRestarted', { friend: o.friend }) }) : null,
        field,
        el('div', { class: 'shoal-step__actions' }, approve, deny),
        reason);
    return { root, input, approve, deny, titleId, update(next) {
        // Polling updates the facts only; it never clears, re-renders or refocuses the input (6.18).
        const dds = root.querySelectorAll('.shoal-facts dd');
        if (dds[0]) {
            dds[0].replaceChildren(serverIdText(next.homeServerIdPrefix));
        }

        if (dds[1]) {
            dds[1].textContent = t('addr_' + next.addressClass);
        }
    } };
}

/* ---------- access switch (6.17): one control, two plain-word choices, a confirm step ---------- */

/**
 * mode: 'Selected' | 'Everyone'. onRequest(nextMode) is called when the unselected segment is pressed; the caller
 * opens the confirm dialog and, once the change is saved, redraws. Arrow keys are not used (simple toggle
 * buttons), so moving focus never opens a dialog.
 */
function accessSwitch(o) {
    const labelId = uid('shoal-acc-l');
    const make = (value, labelKey) => {
        const on = o.mode === value;
        const b = el('button', { type: 'button', 'aria-pressed': String(on), 'data-fkey': o.fkey ? o.fkey + ':' + value : null }, on ? icon('check', true) : null, el('span', { text: t(labelKey) }));
        b.addEventListener('click', () => {
            if (on || o.busy) {
                return;
            }

            o.onRequest(value);
        });
        return b;
    };
    const seg = el('div', { class: 'shoal-seg shoal-seg--access' }, make('Selected', 'modeSelected'), make('Everyone', 'modeEveryone'));
    return el('div', { class: 'shoal-access', role: 'group', 'aria-labelledby': labelId },
        el('p', { class: 'shoal-access__label', id: labelId, text: t('accessLabel', { library: o.library }) }),
        seg,
        el('p', { class: 'shoal-access__desc', text: t(o.mode === 'Everyone' ? 'accessDescEveryone' : 'accessDescSelected') }));
}

function legacyCopy(text) {
    const area = el('textarea', { readonly: true, 'aria-hidden': 'true', tabindex: '-1', class: 'shoal-sr' });
    area.value = text;
    document.body.append(area);
    area.select();
    let ok = false;
    try {
        ok = document.execCommand('copy');
    } catch {
        ok = false;
    }

    area.remove();
    return ok;
}

/** Puts text on the clipboard: the async API first, the old way when the page isn't allowed to. */
async function copyText(text) {
    try {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            await navigator.clipboard.writeText(text);
            return true;
        }
    } catch {
        /* fall through to the old way */
    }

    return legacyCopy(text);
}

/** Copy button (6.10): label becomes "Copied" for 2 s and the page announces it. */
function copyButton(ctx, value, labelKey, doneKey) {
    const btn = button({ label: t(labelKey), icon: 'copy', small: true });
    btn.addEventListener('click', async () => {
        const ok = await copyText(typeof value === 'function' ? value() : value);
        if (ok) {
            const label = btn.querySelector('.shoal-btn__label');
            label.textContent = t('copied');
            ctx.announce(t(doneKey));
            ctx.later(() => {
                label.textContent = t(labelKey);
            }, 2000);
        }
    });
    return btn;
}

/* ---------- pairing v2 helpers (docs/ui-spec-pairing-v2.md, sections 6 and 7) ---------- */

const PV2_NETWORK_OF = { tailnet: 'Tailnet', lan: 'LAN', public: 'Public' };

/** Is this host a Tailscale one: a .ts.net name, or an address from 100.64.0.0 to 100.127.255.255. */
function isTailscaleHost(host) {
    if (/\.ts\.net$/i.test(host)) {
        return true;
    }

    const m = /^100\.(\d{1,3})\.\d{1,3}\.\d{1,3}$/.exec(host);
    return !!m && Number(m[1]) >= 64 && Number(m[1]) <= 127;
}

/** Is this host a private (RFC1918) address, or a name that only means something inside a network (.local, .lan). */
function isPrivateHost(host) {
    if (/\.(local|lan)$/i.test(host)) {
        return true;
    }

    const m = /^(\d{1,3})\.(\d{1,3})\.\d{1,3}\.\d{1,3}$/.exec(host);
    if (!m) {
        return false;
    }

    const a = Number(m[1]);
    const b = Number(m[2]);
    return a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168);
}

/**
 * The live address reader (6.1): what the typed address looks like, which network it suggests, and what to say. Client only, nothing is sent.
 * Returns null for an empty or still incomplete address, else { network (Tailnet | LAN | Public | null), msgs: [{ tone, key, vars?, quickFix? }], trimmed (the
 * address without a /web/index.html tail, or null) }. A real base path such as /jellyfin is kept. ownHost is this server's own host name.
 */
function readAddress(value, ownHost) {
    const raw = String(value || '').trim();
    if (!raw) {
        return null;
    }

    if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(raw)) {
        const low = raw.toLowerCase();
        if ('http://'.startsWith(low) || 'https://'.startsWith(low) || !/[.:/]/.test(raw)) {
            return null;
        }

        return { network: null, msgs: [{ tone: 'warn', key: 'pv2AddrNoScheme', quickFix: 'http://' + raw }], trimmed: null };
    }

    let u;
    try {
        u = new URL(raw);
    } catch {
        return null;
    }

    if (u.protocol !== 'http:' && u.protocol !== 'https:') {
        return { network: null, msgs: [{ tone: 'warn', key: 'pv2AddrNoScheme' }], trimmed: null };
    }

    const msgs = [];
    let trimmed = null;
    const tail = /\/web(\/.*)?$/i.exec(u.pathname);
    if (tail) {
        const removed = u.pathname.slice(tail.index) + u.search + u.hash;
        const base = u.origin + u.pathname.slice(0, tail.index);
        trimmed = base;
        msgs.push({ tone: 'info', key: 'pv2AddrTrimmed', vars: { path: removed } });
    }

    const host = u.hostname.replace(/^\[|\]$/g, '').toLowerCase();
    const https = u.protocol === 'https:';
    const numeric = /^\d{1,3}(\.\d{1,3}){3}$/.test(host);
    let network = null;
    if (isTailscaleHost(host)) {
        network = 'Tailnet';
        msgs.push({ tone: 'ok', key: 'pv2AddrTailscale' });
    } else if (isPrivateHost(host)) {
        network = 'LAN';
        msgs.push({ tone: 'warn', key: 'pv2AddrPrivate' });
    } else if (host === 'localhost' || /^127\./.test(host) || host === '::1' || (ownHost && host === String(ownHost).toLowerCase())) {
        msgs.push({ tone: 'bad', key: 'pv2AddrSelf' });
    } else if (https && numeric) {
        network = 'Public';
        msgs.push({ tone: 'warn', key: 'pv2AddrHttpsNumbers' });
    } else if (https && u.port === '8096') {
        network = 'Public';
        msgs.push({ tone: 'warn', key: 'pv2AddrHttps8096' });
    } else if (!https && (numeric || host.includes('.'))) {
        network = 'Public';
        msgs.push({ tone: 'bad', key: 'pv2AddrHttpPublic' });
    } else if (https && /\.[a-z]{2,}$/.test(host)) {
        network = 'Public';
        msgs.push({ tone: 'info', key: 'pv2AddrInternet' });
    }

    // An https address on Jellyfin's plain http port is odd whatever else it is (a private address with it gets both notes).
    if (https && u.port === '8096' && !msgs.some((m) => m.key === 'pv2AddrHttps8096')) {
        const first = msgs.find((m) => m.key !== 'pv2AddrTrimmed');
        if (first) {
            first.extra = 'pv2AddrHttps8096';
            first.tone = first.tone === 'ok' ? 'warn' : first.tone;
        } else {
            msgs.push({ tone: 'warn', key: 'pv2AddrHttps8096' });
        }
    }

    return { network, msgs, trimmed };
}

/**
 * What a typed or pasted code is, for the boxes that expect something else (7.1). where: 'invite' (the Invite box), 'address' (the address
 * field), 'safety' (the share side's safety code box). Returns null when the text is what the box wants, else { kind, key, alert?, action? }.
 */
function classifyCode(value, where) {
    const raw = String(value || '').trim();
    if (!raw) {
        return null;
    }

    const compact = raw.replace(/\s+/g, '');
    const six = /^\d{6}$/.test(compact);
    const invite = /^shoal1/i.test(compact);
    if (where === 'safety') {
        return /^[A-Za-z0-9]{8}$/.test(compact) ? null : { kind: 'shape', key: 'pv2CodeSafetyShape' };
    }

    if (where === 'address') {
        if (six) {
            return { kind: 'number', key: 'pv2CodeNumber', alert: true };
        }

        return invite ? { kind: 'invite', key: 'pv2CodeInviteElsewhere', action: 'move' } : null;
    }

    if (six) {
        return { kind: 'number', key: 'pv2CodeNumber', alert: true, action: 'codes' };
    }

    if (invite) {
        return looksLikeInvite(raw) ? null : { kind: 'cut', key: 'pv2CodeCutOff' };
    }

    if (/^https?:/i.test(raw) || (raw.includes('.') && raw.includes('/'))) {
        return { kind: 'address', key: 'pv2CodeAddress', action: 'address' };
    }

    if (/^[A-Za-z0-9]{8}$/.test(compact)) {
        return { kind: 'safety', key: 'pv2CodeSafety' };
    }

    return { kind: 'other', key: 'pv2CodeOther' };
}

/** A safety code in two groups of four: K7QM2XA4 reads "K7QM 2XA4". */
function inFours(value) {
    return String(value === 'undefined' ? '' : value || '').replace(/\s+/g, '').replace(/(.{4})(?=.)/g, '$1 ');
}

/* ---------- pairing v2 components (docs/ui-spec-pairing-v2.md, section 3) ---------- */

const WHO_KINDS = { you: ['user', 'pv2WhoYou'], friend: ['users', 'pv2WhoFriend'], wait: ['sync', 'pv2WhoWait'] };

/** A radio group is named by the step heading above it (no second, hidden legend that reads the same twice). */
function labelByHeading(wrap, fieldset) {
    const h = wrap.querySelector('h2');
    if (h) {
        if (!h.id) {
            h.id = uid('shoal-h');
        }

        fieldset.setAttribute('aria-labelledby', h.id);
    }
}

/** The Who label (3.1): a pill that says who acts on this step. Icon plus text, never colour alone. */
function whoLabel(kind) {
    const def = WHO_KINDS[kind];
    return el('p', { class: 'shoal-who', 'data-who': kind, id: uid('shoal-who') }, icon(def[0], true), el('span', { text: t(def[1]) }));
}

/** The Say box (3.2): one or two sentences to read aloud, with a Copy message button. lead defaults to "Say to your friend:". */
function sayBox(ctx, text, lead) {
    return el('div', { class: 'shoal-say' },
        el('p', { class: 'shoal-say__text' }, el('strong', { text: (lead || t('pv2SayLead')) + ' ' }), text),
        copyButton(ctx, text, 'pv2CopyMessage', 'pv2Copied'));
}

/**
 * "Where each code goes" (3.3). side: 'watch' | 'share'. route: 'invite' | 'qc' | null (all four watch rows). Rows are definition lists, which
 * read well on a phone and on a desktop alike.
 */
function codesCard(ctx, side, route, open) {
    const rows = side === 'share'
        ? [['pv2CodeRowInvite', 'pv2LooksInvite', 'pv2MadeByYouHere', 'pv2TypedByFriend', 'pv2GoesFriendInviteBox'], ['pv2CodeRowSafety8', 'pv2LooksSafety8', 'pv2MadeByFriendShoalShows', 'pv2TypedByYou', 'pv2GoesApproveBox']]
        : [['pv2CodeRowInvite', 'pv2LooksInvite', 'pv2MadeByFriendShoal', 'pv2TypedByYou', 'pv2GoesInviteBox'],
            ['pv2CodeRowSafety8', 'pv2LooksSafety8', 'pv2MadeByThisPage', 'pv2TypedByFriend', 'pv2GoesFriendApprove']];
    const shown = rows;
    const content = el('div', { class: 'shoal-codesguide' },
        shown.map((r) => el('dl', { class: 'shoal-codesguide__row' },
            el('dt', { class: 'shoal-codesguide__name' }, el('strong', { text: t(r[0]) }), ' ', el('span', { class: 'shoal-meta', text: t(r[1]) })),
            el('dd', { text: t('pv2WhoMakes') + ' ' + t(r[2]) }),
            el('dd', { text: t('pv2WhoTypes') + ' ' + t(r[3]) }),
            el('dd', { text: t('pv2WhereGoes') + ' ' + t(r[4]) }))));
    const card = collapse(t('pv2CodesTitle'), content, open);
    card.classList.add('shoal-codescard');
    return card;
}

/** The invite input's client check (ui-spec 6.10): after removing spaces and hyphens it starts with shoal1 and is long enough. */
function looksLikeInvite(value) {
    const raw = String(value).replace(/[\s-]/g, '').toLowerCase();
    return raw.startsWith('shoal1') && raw.length >= 40;
}

/** What the server does to a typed safety code (DESIGN 4.1b): capitals, no spaces or hyphens, I and L are 1, O is 0. */
function normalizeCode(value) {
    return String(value).toUpperCase().replace(/[\s-]/g, '').replace(/[IL]/g, '1').replace(/O/g, '0');
}

function validCode(value) {
    return /^[0-9A-HJKMNP-TV-Z]{8}$/.test(normalizeCode(value));
}

/* ---------- countdown (6.11) ---------- */

/**
 * Shows "Expires in 9:41" with a thin bar. Visual updates every second; screen readers only get the
 * page's polite region at 5 min, 1 min and expiry.
 */
function countdown(ctx, expiresUtc, onExpire, kind, vars) {
    const end = toMs(expiresUtc);
    if (end === null) {
        // No expiry sent yet: unknown is not expired. Show nothing and never fire onExpire (N11).
        return { root: el('div', { class: 'shoal-countdown', hidden: true }), stop: () => {} };
    }

    const total = Math.max(1, end - ctx.now());
    const text = el('span', { class: 'shoal-meta shoal-countdown__text' });
    const fill = el('span', { class: 'shoal-countdown__fill' });
    // Under 3 minutes (pairing v2 3.4): words, not just a colour, and what to do if the time is not enough.
    const helperKey = kind === 'shareInvite' ? 'pv2Under3Share' : kind === 'invite' ? 'pv2Under3Invite' : 'pv2Under3Code';
    const warn = el('span', { class: 'shoal-countdown__warn shoal-tone--warn', hidden: true }, icon('warn', true), el('span', { text: t('pv2Under3') + ' ' + t(helperKey, vars) }));
    const root = el('div', { class: 'shoal-countdown' }, text, el('span', { class: 'shoal-countdown__bar', 'aria-hidden': 'true' }, fill), warn);
    let announced3 = false;
    let announced5 = false;
    let announced1 = false;
    let done = false;
    const tick = () => {
        const left = Math.max(0, end - ctx.now());
        text.textContent = t(kind === 'confirm' ? 'confirmWithin' : 'expiresIn', { time: ctx.fmt.clock(left / 1000) });
        setVar(fill, '--shoal-progress', Math.round((left / total) * 100) + '%');
        let spoke = false;
        if (left > 0 && left <= 180000) {
            root.classList.add('is-warn');
            warn.hidden = false;
            if (!announced3 && total > 180000) {
                announced3 = true;
                spoke = true;
                announced5 = true;
                ctx.announce(t('pv2Under3'));
            }
        }

        if (spoke) {
            return;
        }

        if (left <= 0 && !done) {
            done = true;
            ctx.announce(t(kind === 'invite' || kind === 'shareInvite' ? 'announceInviteExpired' : 'announceCodeExpired'));
            if (onExpire) {
                onExpire();
            }
        } else if (left <= 60000 && !announced1) {
            announced1 = true;
            ctx.announce(t('announceOneMinute'));
        } else if (left <= 300000 && !announced5 && total > 300000) {
            announced5 = true;
            ctx.announce(t('announceFiveMinutes'));
        }
    };

    tick();
    const timer = ctx.every(tick, 1000);
    return { root, stop: () => ctx.clearTimer(timer) };
}

/* ---------- dialogs (6.9) ---------- */

/**
 * Opens a native <dialog> (focus is trapped and Esc closes natively). Resolves with the value passed to
 * close(); Esc and Cancel resolve with undefined. Focus returns to the opener.
 *
 * o.build(body, dlg) fills the body and may return the element to focus first.
 * o.buttons: [{ label, variant, close: value | onClick(dlg) }] rendered right-aligned, first one is
 * the safe one in destructive dialogs.
 */
function openDialog(ctx, o) {
    const dialog = ctx.tmpl('dialog');
    const opener = document.activeElement;
    const titleNode = slot(dialog, 'title');
    const body = slot(dialog, 'body');
    const foot = slot(dialog, 'foot');
    const errorNode = slot(dialog, 'error');
    titleNode.textContent = o.title;
    if (o.wide) {
        dialog.classList.add('shoal-dialog--wide');
    }

    return new Promise((resolve) => {
        let result;
        const dlg = {
            el: dialog,
            body,
            foot,
            setError(message) {
                errorNode.textContent = message || '';
                errorNode.hidden = !message;
            },
            close(value) {
                result = value;
                if (typeof dialog.close === 'function') {
                    dialog.close();
                } else {
                    dialog.removeAttribute('open');
                    dialog.dispatchEvent(new Event('close'));
                }
            },
            buttons: {}
        };

        const focusFirst = o.build ? o.build(body, dlg) : null;
        for (const b of o.buttons || []) {
            const btn = button({
                label: b.label,
                variant: b.variant,
                disabled: b.disabled,
                onClick: async (ev, node) => {
                    if (typeof b.onClick === 'function') {
                        await b.onClick(dlg, node);
                    } else {
                        dlg.close(b.close);
                    }
                }
            });
            if (b.key) {
                dlg.buttons[b.key] = btn;
            }

            foot.append(btn);
        }

        if (o.onOpen) {
            o.onOpen(dlg);
        }

        dialog.addEventListener('close', () => {
            dialog.remove();
            if (opener && typeof opener.focus === 'function' && document.contains(opener)) {
                opener.focus();
            }

            resolve(result);
        });
        ctx.root.append(dialog);
        if (typeof dialog.showModal === 'function') {
            dialog.showModal();
        } else {
            dialog.setAttribute('open', '');
        }

        const target = focusFirst || (o.safeFocus ? foot.querySelector('button') : null) || q(body, 'input, select, textarea') || q(foot, 'button');
        if (target && typeof target.focus === 'function') {
            target.focus();
        }
    });
}

/**
 * Confirmation dialog. o: { title, intro, items: [text], checkbox: {label, checked}, typed: {expected},
 * confirmLabel, danger }. Resolves { confirmed: boolean, checked: boolean }.
 */
async function confirmDialog(ctx, o) {
    let checkbox = null;
    let typed = null;
    const result = await openDialog(ctx, {
        title: o.title,
        safeFocus: o.safeFocus !== undefined ? !!o.safeFocus : !!o.danger,
        build(body, dlg) {
            if (o.intro) {
                body.append(paragraph(o.intro));
            }

            if (o.items && o.items.length) {
                body.append(el('ul', { class: 'shoal-bullets' }, o.items.map((x) => el('li', { text: x }))));
            }

            for (const node of o.after || []) {
                if (node) {
                    body.append(node);
                }
            }

            if (o.checkbox) {
                checkbox = checkRow({ id: uid('shoal-chk'), label: o.checkbox.label, checked: o.checkbox.checked });
                body.append(checkbox.root);
                if (o.checkbox.whenUnchecked) {
                    // A note that only shows while the box is empty (Warden 11 L7: what "keep the library" leaves behind).
                    const note = el('div', { class: 'shoal-hint shoal-hint--info', role: 'note', hidden: checkbox.input.checked }, icon('info', true), el('div', { class: 'shoal-hint__body', text: o.checkbox.whenUnchecked }));
                    checkbox.input.addEventListener('change', () => {
                        note.hidden = checkbox.input.checked;
                    });
                    body.append(note);
                }

                if (o.requireChecked) {
                    checkbox.input.addEventListener('change', () => {
                        dlg.buttons.confirm.disabled = !checkbox.input.checked;
                    });
                }
            }

            if (o.typed) {
                typed = textField({ id: uid('shoal-typed'), label: o.typed.label, help: t('typedHelp'), required: true });
                body.append(typed.root);
                const recheck = () => {
                    const ok = typed.input.value.trim() === o.typed.expected;
                    dlg.buttons.confirm.disabled = !ok;
                };
                // input, change, keyup and paste: whichever the browser fires for typing, pasting or autofill wakes the button.
                for (const name of ['input', 'change', 'keyup', 'paste']) {
                    typed.input.addEventListener(name, name === 'paste' ? () => ctx.later(recheck, 0) : recheck);
                }
            }

            return null;
        },
        buttons: [
            { label: o.cancelLabel || t('cancel'), key: 'cancel', close: undefined },
            o.run
                ? { label: o.confirmLabel, key: 'confirm', variant: o.danger ? 'danger' : 'primary', disabled: !!o.typed || !!o.requireChecked, onClick: async (dlg, btn) => {
                    dlg.setError('');
                    btn.disabled = true;
                    try {
                        await o.run({ checked: checkbox ? checkbox.input.checked : false });
                        dlg.close('confirm');
                    } catch (err) {
                        // The dialog stays, with the reason in words: the admin can wait and press the button again, or cancel.
                        dlg.setError(o.errorText ? o.errorText(err) : failureMessage(err));
                        btn.disabled = false;
                    }
                } }
                : { label: o.confirmLabel, key: 'confirm', variant: o.danger ? 'danger' : 'primary', disabled: !!o.typed || !!o.requireChecked, close: 'confirm' }
        ]
    });
    return { confirmed: result === 'confirm', checked: checkbox ? checkbox.input.checked : false };
}

/** The SHA-256 fingerprint as groups of four hex characters, easy to read out loud ("01:AB:CD:..." in, ["01AB", "CD.."] out). */
function fingerprintGroups(fingerprint) {
    const hex = String(fingerprint || '').replace(/[^0-9a-fA-F]/g, '').toUpperCase();
    const groups = [];
    for (let i = 0; i < hex.length; i += 4) {
        groups.push(hex.slice(i, i + 4));
    }

    return groups;
}

/** The words for a refused trust or pin (Warden 11 H1): the key moved again, or the chain check needs the extra tick. */
function certErrorMessage(err, fallbackKey) {
    if (err && err.kind === 'changedAgain') {
        return t('certChangedAgain');
    }

    if (err && err.kind === 'untrusted') {
        return t('certUntrustedRefused');
    }

    return failureMessage(err, fallbackKey);
}

/**
 * Shows the key a server presents now (GET Certificate) and asks the admin to confirm its fingerprint with the friend before it is trusted
 * or pinned (Warden 11 H1, M2). A Public server whose certificate fails the normal chain checks needs a second tick. Resolves to
 * { expectedPin, acceptUntrusted? } for the request that follows, or null when the admin backed out or the key could not be read.
 * o: { handle, title, intro, confirmLabel, publicNet }
 */
async function certificateCheck(ctx, o) {
    let cert;
    try {
        cert = await ctx.api.certificate(o.handle);
    } catch {
        ctx.alertMessage(t('certReadFailed'));
        return null;
    }

    const needsExtra = !!o.publicNet && cert.chainValid === false;
    let agree = null;
    let extra = null;
    const result = await openDialog(ctx, {
        title: o.title,
        safeFocus: true,
        build(body, dlg) {
            body.append(paragraph(o.intro));
            body.append(el('p', { class: 'shoal-meta', text: t('certFingerprint') }), el('p', { class: 'shoal-mono shoal-fingerprint', 'data-testid': 'fingerprint', text: fingerprintGroups(cert.fingerprint).join(' ') }));
            agree = checkRow({ id: uid('shoal-cert'), label: t('certMatches'), checked: false });
            if (needsExtra) {
                body.append(hint('warn', 'warn', t('certUntrustedWarn')));
                extra = checkRow({ id: uid('shoal-cert'), label: t('certUntrustedCheck'), checked: false });
            }

            body.append(agree.root);
            if (extra) {
                body.append(extra.root);
            }

            const refresh = () => {
                dlg.buttons.confirm.disabled = !agree.input.checked || (extra !== null && !extra.input.checked);
            };
            agree.input.addEventListener('change', refresh);
            if (extra) {
                extra.input.addEventListener('change', refresh);
            }

            return null;
        },
        buttons: [
            { label: t('cancel'), key: 'cancel', close: undefined },
            { label: o.confirmLabel, key: 'confirm', variant: 'primary', disabled: true, close: 'confirm' }
        ]
    });
    if (result !== 'confirm') {
        return null;
    }

    return needsExtra ? { expectedPin: cert.pin, acceptUntrusted: true } : { expectedPin: cert.pin };
}

/* ====================================================================================== */
/* Link speed chart (ui-spec 9)                                                           */
/* ====================================================================================== */

const RANGE_MS = { '24h': 86400000, '7d': 7 * 86400000, '30d': 30 * 86400000 };
const CHART_INSET = { top: 12, right: 8, bottom: 0, left: 0 };

/** 1-2-5 sequence: 10, 20, 50, 100, 200 ... */
function niceCeil(v) {
    if (!(v > 0)) {
        return 10;
    }

    const pow = Math.pow(10, Math.floor(Math.log10(v)));
    const f = v / pow;
    const nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
    return nice * pow;
}

function xTicks(rangeKey, nowMs, fmt) {
    const span = RANGE_MS[rangeKey];
    const start = nowMs - span;
    if (rangeKey === '24h') {
        return [
            { pct: 0, label: t('axis24Ago') },
            { pct: 25, label: t('axisHoursAgo', { n: 18 }) },
            { pct: 50, label: t('axisHoursAgo', { n: 12 }) },
            { pct: 75, label: t('axisHoursAgo', { n: 6 }) },
            { pct: 100, label: t('axisNow') }
        ];
    }

    const ticks = [];
    if (rangeKey === '7d') {
        const first = fmt.startOfDay(start) + 86400000;
        for (let m = first; m < nowMs; m += 86400000) {
            const pct = ((m - start) / span) * 100;
            if (pct < 97) {
                ticks.push({ pct, label: fmt.weekday(m) });
            }
        }
    } else {
        for (let k = 4; k >= 1; k -= 1) {
            const m = nowMs - k * 7 * 86400000;
            ticks.push({ pct: ((m - start) / span) * 100, label: fmt.date(m) });
        }
    }

    ticks.push({ pct: 100, label: t('axisNow') });
    return ticks;
}

/**
 * Pure geometry for the chart (drawn in pixel space, ui-spec 9). o: samples, offline, routeChanges,
 * rangeKey, nowMs, width, height. Returns paths, markers, bands and thresholds ready to draw.
 */
function chartModel(o) {
    const span = RANGE_MS[o.rangeKey];
    const start = o.nowMs - span;
    const plotW = Math.max(1, o.width - CHART_INSET.left - CHART_INSET.right);
    const plotH = Math.max(1, o.height - CHART_INSET.top - CHART_INSET.bottom);
    const pts = (o.samples || [])
        .filter((s) => typeof s.mbps === 'number' && !s.error)
        .map((s) => ({ ...s, ms: toMs(s.t) }))
        .filter((s) => s.ms !== null && s.ms >= start && s.ms <= o.nowMs)
        .sort((a, b) => a.ms - b.ms);
    const maxMbps = pts.reduce((m, p) => Math.max(m, p.mbps), 0);
    const floor = pts.some((p) => p.mbps >= 20) ? 30 : 15;
    const yMax = niceCeil(Math.max(maxMbps, floor) * 1.15);
    const xOf = (ms) => CHART_INSET.left + ((ms - start) / span) * plotW;
    const yOf = (v) => CHART_INSET.top + (1 - v / yMax) * plotH;
    const bottom = CHART_INSET.top + plotH;
    const gaps = (o.offline || []).map((g) => ({ from: toMs(g.fromUtc), to: toMs(g.toUtc) })).filter((g) => g.from !== null && g.to !== null);

    const segments = [];
    let current = [];
    pts.forEach((p, i) => {
        const prev = pts[i - 1];
        if (prev && gaps.some((g) => g.from < p.ms && g.to > prev.ms)) {
            segments.push(current);
            current = [];
        }

        current.push(p);
    });
    if (current.length) {
        segments.push(current);
    }

    const num = (n) => Math.round(n * 100) / 100;
    const lines = [];
    const areas = [];
    for (const seg of segments) {
        if (seg.length < 2) {
            continue;
        }

        const path = seg.map((p, i) => (i === 0 ? 'M' : 'L') + num(xOf(p.ms)) + ' ' + num(yOf(p.mbps))).join(' ');
        lines.push(path);
        areas.push(path + ' L' + num(xOf(seg[seg.length - 1].ms)) + ' ' + num(bottom) + ' L' + num(xOf(seg[0].ms)) + ' ' + num(bottom) + ' Z');
    }

    const bands = gaps
        .map((g) => ({ x0: xOf(Math.max(g.from, start)), x1: xOf(Math.min(g.to, o.nowMs)), from: g.from, to: g.to }))
        .filter((b) => b.x1 > b.x0)
        .map((b) => ({ x: num(b.x0), w: num(b.x1 - b.x0), from: b.from, to: b.to, label: b.x1 - b.x0 >= 48 }));

    return {
        yMax,
        width: o.width,
        height: o.height,
        bottom: num(bottom),
        grid: [0.25, 0.5, 0.75].map((f) => num(yOf(yMax * f))),
        points: pts.map((p, i) => ({
            x: num(xOf(p.ms)),
            y: num(yOf(p.mbps)),
            kind: p.kind,
            latest: i === pts.length - 1,
            mbps: p.mbps,
            ms: p.ms,
            rttMedianMs: p.rttMedianMs,
            path: p.path
        })),
        lines,
        areas,
        bands,
        routeLines: (o.routeChanges || []).map((r) => toMs(r.t)).filter((ms) => ms !== null && ms >= start && ms <= o.nowMs).map((ms) => num(xOf(ms))),
        thresholds: QUALITY_LINES.filter((l) => l.mbps <= yMax).map((l) => ({ ...l, y: num(yOf(l.mbps)), pct: num((yOf(l.mbps) / o.height) * 100) })),
        ticks: xTicks(o.rangeKey, o.nowMs, o.fmt),
        counts: pts.length,
        gaps
    };
}

/** The generated sentence for screen readers (ui-spec 9, Accessibility). */
function chartSummary(o, fmt) {
    const pts = o.points;
    const rangeLabel = t('rangeLabel_' + o.rangeKey);
    if (pts.length === 0) {
        return t('chartSummaryNone', { server: o.serverName, range: rangeLabel });
    }

    const speeds = pts.map((p) => p.mbps);
    const latest = pts[pts.length - 1];
    const parts = [];
    if (pts.length === 1) {
        parts.push(t('chartSummaryOne', { server: o.serverName, range: rangeLabel, mbps: fmt.mbpsValue(latest.mbps), time: fmt.time(latest.ms) }));
    } else {
        parts.push(t('chartSummaryMany', {
            server: o.serverName,
            range: rangeLabel,
            n: pts.length,
            min: fmt.mbpsValue(Math.min(...speeds)),
            max: fmt.mbpsValue(Math.max(...speeds)),
            mbps: fmt.mbpsValue(latest.mbps),
            time: fmt.time(latest.ms)
        }));
        for (const line of QUALITY_LINES.slice().reverse()) {
            const below = pts.filter((p) => p.mbps < line.mbps).length;
            const vars = { n: below, mbps: line.mbps, quality: t('quality_' + line.q) };
            parts.push(below === 0 ? t('chartEvery', vars) : t('chartBelow', vars));
        }
    }

    for (const g of o.gaps.slice(0, 3)) {
        parts.push(t('chartOffline', { from: fmt.time(g.from), to: fmt.time(g.to) }));
    }

    return parts.join(' ');
}

/** The chart widget. update(data) redraws; the plot is rebuilt at the plot's current pixel size. */
function createChart(ctx) {
    const captionId = uid('shoal-chart-title');
    const summaryId = uid('shoal-chart-summary');
    const caption = el('figcaption', { class: 'shoal-sr', id: captionId });
    const svg = svgEl('svg', { role: 'img', 'aria-labelledby': captionId + ' ' + summaryId, focusable: 'false', width: '100%', height: '100%' });
    const thrNodes = QUALITY_LINES.map((l) => el('span', { class: 'shoal-chart__thr', 'data-q': l.q, hidden: true }));
    const plot = el('div', { class: 'shoal-chart__plot' }, svg, thrNodes);
    const xAxis = el('div', { class: 'shoal-chart__x', 'aria-hidden': 'true' });
    const summary = el('p', { class: 'shoal-sr', id: summaryId });
    const tableHost = el('div', { class: 'shoal-chart__table' });
    const details = collapse(t('showAsTable'), tableHost);
    const root = el('figure', { class: 'shoal-chart', 'aria-labelledby': captionId }, caption, plot, xAxis, summary, details);
    const empty = el('p', { class: 'shoal-chart__empty shoal-meta', hidden: true });
    plot.append(empty);

    let data = null;
    let observer = null;
    let timer = null;

    function draw() {
        if (!data) {
            return;
        }

        const width = Math.round(plot.clientWidth) || 640;
        const height = Math.round(plot.clientHeight) || 184;
        svg.setAttribute('viewBox', '0 0 ' + width + ' ' + height);
        const m = chartModel({ samples: data.samples, offline: data.offline, routeChanges: data.routeChanges, rangeKey: data.rangeKey, nowMs: ctx.now(), width, height, fmt: ctx.fmt });
        clear(svg);
        for (const y of m.grid) {
            svg.append(svgEl('line', { class: 'shoal-chart__grid', x1: 0, x2: width, y1: y, y2: y }));
        }

        for (const b of m.bands) {
            svg.append(svgEl('rect', { class: 'shoal-chart__band', x: b.x, y: CHART_INSET.top, width: b.w, height: m.bottom - CHART_INSET.top }));
            if (b.label) {
                const label = svgEl('text', { class: 'shoal-chart__bandlabel', x: b.x + b.w / 2, y: CHART_INSET.top + 16, 'text-anchor': 'middle' });
                label.textContent = t('chartOfflineBand');
                svg.append(label);
            }
        }

        for (const th of m.thresholds) {
            svg.append(svgEl('line', { class: 'shoal-chart__thrline', x1: 0, x2: width, y1: th.y, y2: th.y }));
        }

        for (const x of m.routeLines) {
            svg.append(svgEl('line', { class: 'shoal-chart__route', x1: x, x2: x, y1: CHART_INSET.top, y2: m.bottom }));
        }

        for (const d of m.areas) {
            svg.append(svgEl('path', { class: 'shoal-chart__area', d }));
        }

        for (const d of m.lines) {
            svg.append(svgEl('path', { class: 'shoal-chart__line', d }));
        }

        for (const p of m.points) {
            if (p.latest) {
                svg.append(svgEl('circle', { class: 'shoal-chart__halo', cx: p.x, cy: p.y, r: 9 }));
            }

            const manual = p.kind === 'manual' || p.kind === 'deep';
            const r = p.latest ? 5 : manual ? 4.5 : p.kind === 'playback' ? 3 : 3.5;
            svg.append(svgEl('circle', { class: 'shoal-chart__pt' + (manual ? ' is-manual' : ''), cx: p.x, cy: p.y, r }));
        }

        QUALITY_LINES.forEach((l, i) => {
            const th = m.thresholds.find((x) => x.q === l.q);
            thrNodes[i].hidden = !th;
            if (th) {
                thrNodes[i].textContent = t(l.key);
                setVar(thrNodes[i], '--shoal-y', th.pct + '%');
            }
        });

        replaceKids(xAxis, m.ticks.map((tick) => {
            const s = el('span', { text: tick.label });
            setVar(s, '--shoal-x', tick.pct + '%');
            return s;
        }));
        caption.textContent = t('chartTitle', { server: data.serverName, range: t('rangeLabel_' + data.rangeKey) });
        summary.textContent = chartSummary({ serverName: data.serverName, rangeKey: data.rangeKey, points: m.points, gaps: m.gaps }, ctx.fmt);
        empty.hidden = m.points.length > 0;
        if (m.points.length === 0) {
            empty.textContent = t('chartNoSamples');
        }

        const rows = (data.samples || [])
            .filter((s) => toMs(s.t) !== null)
            .slice()
            .sort((a, b) => toMs(b.t) - toMs(a.t))
            .map((s) => ({
                time: ctx.fmt.relative(s.t) + ' · ' + ctx.fmt.time(s.t),
                type: t('kind_' + s.kind),
                speed: typeof s.mbps === 'number' ? ctx.fmt.mbpsValue(s.mbps) : '—',
                latency: typeof s.rttMedianMs === 'number' ? ctx.fmt.num(Math.round(s.rttMedianMs)) : '—',
                route: s.error ? t('sampleFailed') : t('path_' + (s.path || 'unknown'))
            }));
        replaceKids(tableHost, dataTable({
            caption: t('tableSpeedCaption', { range: t('rangeLabel_' + data.rangeKey) }),
            columns: [
                { key: 'time', label: t('colTime') },
                { key: 'type', label: t('colType') },
                { key: 'speed', label: t('colSpeedMbps'), num: true },
                { key: 'latency', label: t('colLatencyMs'), num: true },
                { key: 'route', label: t('colRoute') }
            ],
            rows
        }));
    }

    function schedule() {
        ctx.clearTimer(timer);
        timer = ctx.later(draw, 100);
    }

    if (typeof ResizeObserver !== 'undefined') {
        observer = new ResizeObserver(schedule);
        observer.observe(plot);
    }

    return {
        root,
        update(next) {
            data = next;
            draw();
        },
        destroy() {
            if (observer) {
                observer.disconnect();
            }

            ctx.clearTimer(timer);
        }
    };
}

/* ====================================================================================== */
/* Page context: navigation, announcements, timers, polling, theming                      */
/* ====================================================================================== */

/** Relative luminance of "#rgb", "#rrggbb", "rgb(...)" or "rgba(...)"; null when unreadable. */
function luminanceOf(color) {
    const c = String(color || '').trim().toLowerCase();
    let rgb = null;
    let m = /^#([0-9a-f]{3})$/.exec(c);
    if (m) {
        rgb = m[1].split('').map((h) => parseInt(h + h, 16));
    }

    m = /^#([0-9a-f]{6})/.exec(c);
    if (m) {
        rgb = [0, 2, 4].map((i) => parseInt(m[1].slice(i, i + 2), 16));
    }

    m = /^rgba?\(\s*(\d+(?:\.\d+)?)[ ,]+(\d+(?:\.\d+)?)[ ,]+(\d+(?:\.\d+)?)/.exec(c);
    if (m) {
        rgb = [Number(m[1]), Number(m[2]), Number(m[3])];
    }

    if (!rgb) {
        return null;
    }

    const lin = rgb.map((v) => {
        const s = v / 255;
        return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
    });
    return 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2];
}

/** Theme detection (ui-spec 5.2): which primary-button set and which signal set to use. */
function applyTheme(page) {
    const cs = window.getComputedStyle(document.documentElement);
    const primary = cs.getPropertyValue('--jf-palette-primary-main').trim().toLowerCase();
    const paper = cs.getPropertyValue('--jf-palette-background-paper').trim();
    page.classList.toggle('shoal-defaultBlue', primary === '' || primary === '#00a4dc');
    page.classList.toggle('shoal-themedPrimary', primary !== '' && primary !== '#00a4dc');
    const lum = luminanceOf(paper);
    page.classList.toggle('shoal-light', lum !== null && lum > 0.5);
}

function defaultDeps() {
    const client = window.ApiClient;
    return {
        api: createApi(client),
        client,
        dashboard: window.Dashboard,
        now: () => Date.now(),
        locale: document.documentElement.lang || undefined,
        storage: safeStorage()
    };
}

/** Per-page-instance services shared by every screen. */
function createContext(page, params, deps) {
    const timers = new Set();
    const root = q(page, '.shoal-root');
    const liveRegion = slot(root, 'live');
    const alertRegion = slot(root, 'alert');
    const instance = uid('i');
    let liveTimer = null;
    let lastAnnounce = '';

    const ctx = {
        page,
        root,
        params,
        deps,
        api: deps.api,
        client: deps.client,
        storage: deps.storage,
        instance,
        alive: true,
        shown: false,
        now: deps.now,
        fmt: makeFormat(deps.locale, deps.now),
        cooldowns: new Map(),
        features: Object.assign({ appsWork: false }, deps.features),

        every(fn, ms) {
            const id = setInterval(() => ctx.alive && fn(), ms);
            timers.add(id);
            return id;
        },
        later(fn, ms) {
            const id = setTimeout(() => {
                timers.delete(id);
                if (ctx.alive) {
                    fn();
                }
            }, ms);
            timers.add(id);
            return id;
        },
        clearTimer(id) {
            if (id !== null && id !== undefined) {
                clearInterval(id);
                clearTimeout(id);
                timers.delete(id);
            }
        },
        clearAllTimers() {
            timers.forEach((id) => {
                clearInterval(id);
                clearTimeout(id);
            });
            timers.clear();
        },

        /** Polite announcement (ui-spec 6.15): cleared after 5 s so a repeat is read again. */
        announce(message) {
            ctx.clearTimer(liveTimer);
            liveRegion.textContent = message === lastAnnounce && liveRegion.textContent ? message + ' ' : message;
            lastAnnounce = message;
            liveTimer = ctx.later(() => {
                liveRegion.textContent = '';
                lastAnnounce = '';
            }, LIVE_CLEAR_MS);
        },
        /** A short visible message for what a click did (6 s), so "nothing happened" is never the answer. The live region says it aloud. */
        toast(message) {
            const old = root.querySelector('.shoal-toast');
            if (old) {
                old.remove();
            }

            const node = el('div', { class: 'shoal-toast', 'aria-hidden': 'true' }, icon('ok', true), el('span', { text: message }));
            root.append(node);
            ctx.later(() => node.remove(), 6000);
        },
        /** A failure of something the admin did: spoken like alertMessage, and kept on screen as a bar until it is dismissed (never a 5 second whisper). */
        fail(message) {
            ctx.alertMessage(message);
            let host = root.querySelector('.shoal-errors');
            if (!host) {
                host = el('div', { class: 'shoal-errors' });
                root.append(host);
            }

            for (const old of host.querySelectorAll('.shoal-errorbar')) {
                if (old.dataset.text === message) {
                    old.remove();
                }
            }

            const bar = el('div', { class: 'shoal-errorbar shoal-hint shoal-hint--bad', role: 'alert', 'data-text': message }, icon('x', true),
                el('div', { class: 'shoal-hint__body', text: message }),
                button({ label: t('dismiss'), small: true, onClick: () => bar.remove() }));
            host.prepend(bar);
        },
        alertMessage(message) {
            alertRegion.textContent = message;
            ctx.later(() => {
                alertRegion.textContent = '';
            }, LIVE_CLEAR_MS);
        },

        /** Clones a <template> from this page instance, with strings applied and ids made unique. */
        tmpl(name) {
            const tpl = root.querySelector('template[id="shoal-t-' + name + '"]');
            const node = tpl.content.firstElementChild.cloneNode(true);
            uniquify(node, uid('c'));
            return applyStrings(node);
        },

        href(view, query) {
            return '#/' + routePath(view, query);
        },
        navigate(view, query) {
            const path = routePath(view, query);
            if (deps.dashboard && typeof deps.dashboard.navigate === 'function') {
                return deps.dashboard.navigate(path);
            }

            window.location.hash = '#/' + path;
            return Promise.resolve();
        },
        /** Makes an <a> a real link (open in new tab works) that navigates through Dashboard.navigate. */
        link(a, view, query) {
            a.setAttribute('href', ctx.href(view, query));
            if (!a.dataset.shoalLink) {
                a.dataset.shoalLink = '1';
                a.addEventListener('click', (ev) => {
                    if (ev.defaultPrevented || ev.button !== 0 || ev.ctrlKey || ev.metaKey || ev.shiftKey || ev.altKey) {
                        return;
                    }

                    ev.preventDefault();
                    ctx.navigate(a.dataset.shoalTo, JSON.parse(a.dataset.shoalQuery || '{}'));
                });
            }

            a.dataset.shoalTo = view;
            a.dataset.shoalQuery = JSON.stringify(query || {});
            return a;
        },
        linkEl(view, query, cls, ...kids) {
            return ctx.link(el('a', { class: cls }, kids), view, query);
        },

        /** Moves focus to the visible view's heading and scrolls to the top (ui-spec 2.2). */
        focusHeading(target) {
            const h = target
                || root.querySelector('[data-shoal-view]:not([hidden]) h1[tabindex="-1"]')
                || root.querySelector('[data-shoal-view]:not([hidden]) [tabindex="-1"]');
            if (h) {
                h.focus({ preventScroll: true });
            }

            window.scrollTo(0, 0);
            const main = document.querySelector('.mainAnimatedPages');
            if (main && typeof main.scrollTo === 'function') {
                main.scrollTo(0, 0);
            }
        },

        /** Cooldowns come from Retry-After (ui-spec 6.2). Returns the remaining whole minutes or 0. */
        setCooldown(key, seconds) {
            ctx.cooldowns.set(key, ctx.now() + seconds * 1000);
        },
        cooldownMinutes(key) {
            const until = ctx.cooldowns.get(key);
            if (!until || until <= ctx.now()) {
                ctx.cooldowns.delete(key);
                return 0;
            }

            return Math.max(1, Math.ceil((until - ctx.now()) / 60000));
        },

        /** Visible and on screen: the condition for any polling (ui-spec 2.3). */
        isActive() {
            return ctx.alive && ctx.shown && document.visibilityState === 'visible';
        }
    };
    return ctx;
}

function routePath(view, query) {
    const parts = ['name=' + encodeURIComponent(PLUGIN_PAGE)];
    if (view && view !== 'overview') {
        parts.push('view=' + encodeURIComponent(view));
    } else {
        parts.push('view=overview');
    }

    for (const [k, v] of Object.entries(query || {})) {
        if (v !== undefined && v !== null && v !== '') {
            parts.push(encodeURIComponent(k) + '=' + encodeURIComponent(v));
        }
    }

    return 'configurationpage?' + parts.join('&');
}

/**
 * Polls fn every ms while the page is visible and shown. Runs once immediately when the page
 * becomes active again. Never moves focus.
 */
function createPoller(ctx, fn, ms) {
    let timer = null;
    let running = false;
    const run = async () => {
        if (running || !ctx.isActive()) {
            return;
        }

        running = true;
        try {
            await fn();
        } finally {
            running = false;
        }
    };
    const onVisibility = () => {
        if (document.visibilityState === 'visible') {
            run();
        }
    };
    return {
        start() {
            ctx.clearTimer(timer);
            timer = ctx.every(run, ms);
            document.removeEventListener('visibilitychange', onVisibility);
            document.addEventListener('visibilitychange', onVisibility);
        },
        stop() {
            ctx.clearTimer(timer);
            timer = null;
            document.removeEventListener('visibilitychange', onVisibility);
        },
        runNow: run
    };
}

/** Maps an API failure to the generic sentence the UI is allowed to show. */
function failureMessage(err, fallbackKey) {
    if (err && err.status === 429) {
        return t('tooManyTries');
    }

    if (err && err.kind === 'unreadable') {
        return t('unreadableAnswer');
    }

    // The admin API answers its own refusals with a word in the body; a bare 404 or 405 means the route does not exist: an older Shoal is installed.
    if (err && (err.status === 404 || err.status === 405) && !err.kind) {
        return t('needsNewerShoal');
    }

    return t(fallbackKey || 'genericFailure');
}

/** Cooldown helper text next to a button: "Available again in 4 min" (ui-spec 6.2). */
function cooldownNote(ctx, key, node, btn) {
    const mins = ctx.cooldownMinutes(key);
    if (mins > 0) {
        node.textContent = t('availableIn', { n: mins });
        node.hidden = false;
        btn.setAttribute('aria-disabled', 'true');
        if (node.id) {
            btn.setAttribute('aria-describedby', node.id);
        }

        return true;
    }

    node.hidden = true;
    node.textContent = '';
    if (btn.dataset.busy !== 'true' && !btn.classList.contains('is-busy')) {
        btn.removeAttribute('aria-disabled');
    }

    btn.removeAttribute('aria-describedby');
    return false;
}

/** Runs a button action with busy state, error line and Retry-After cooldown handling. */
async function runAction(ctx, btn, o) {
    if (btn.getAttribute('aria-disabled') === 'true') {
        return null;
    }

    setBusy(btn, true, o.busyText);
    let finished = false;
    let watchdog = null;
    try {
        // A call that never answers must not leave the button dead: after 45 seconds it is a visible failure.
        const stalled = new Promise((resolve, reject) => {
            watchdog = setTimeout(() => {
                if (!finished) {
                    const e = new Error('no answer');
                    e.status = 0;
                    e.kind = 'noAnswer';
                    reject(e);
                }
            }, 45000);
        });
        const result = await Promise.race([Promise.resolve().then(() => o.run()), stalled]);
        finished = true;
        if (o.onDone) {
            try {
                o.onDone(result);
            } catch (inner) {
                reportUnexpected(inner);
            }
        }

        return result;
    } catch (err) {
        finished = true;
        if (err && err.status === 429 && o.cooldownKey) {
            ctx.setCooldown(o.cooldownKey, err.retryAfter || 60);
        }

        try {
            if (o.onError) {
                o.onError(err);
            } else {
                ctx.fail(failureMessage(err, o.failKey));
            }
        } catch (inner) {
            reportUnexpected(inner);
        }

        return null;
    } finally {
        clearTimeout(watchdog);
        setBusy(btn, false);
        if (o.afterBusy) {
            o.afterBusy();
        }
    }
}

/* ====================================================================================== */
/* Images (ui-spec 10): always local item ids on the Home server, never a peer URL        */
/* ====================================================================================== */

function dpr() {
    return Math.min(Math.max(window.devicePixelRatio || 1, 1), 2);
}

/** Local item image URL through the signed-in ApiClient. Falls back to nothing when the id is missing. */
function itemImageUrl(ctx, itemId, type, tag, cssWidth, quality) {
    if (!itemId || !ctx.client || typeof ctx.client.getImageUrl !== 'function') {
        return '';
    }

    return ctx.client.getImageUrl(itemId, { type, tag: tag || undefined, maxWidth: Math.round(cssWidth * dpr()), quality: quality || 80 });
}

function userImageUrl(ctx, userId, tag) {
    if (!userId || !tag || !ctx.client || typeof ctx.client.getUserImageUrl !== 'function') {
        return '';
    }

    return ctx.client.getUserImageUrl(userId, { type: 'Primary', tag, maxWidth: 64 });
}

/**
 * Loads img with a chain of candidates [{url, cls}] and falls back one step per error, then calls
 * onFail once (the placeholder). Uses addEventListener, never an inline handler.
 */
function loadImageChain(img, chain, onFail) {
    let index = 0;
    const next = () => {
        while (index < chain.length && !chain[index].url) {
            index += 1;
        }

        if (index >= chain.length) {
            img.hidden = true;
            onFail();
            return;
        }

        const c = chain[index];
        index += 1;
        img.classList.toggle('is-primary-art', c.cls === 'primary');
        img.src = c.url;
        img.hidden = false;
    };
    if (img.dataset.chainBound !== '1') {
        img.dataset.chainBound = '1';
        img.addEventListener('error', () => img._shoalNext && img._shoalNext());
    }

    img._shoalNext = next;
    next();
}

/* ====================================================================================== */
/* Overlap panel (ui-spec 6.14, 8.1)                                                      */
/* ====================================================================================== */

/** Why a library cannot be picked: TV shows have their own switch in Settings, everything else is not supported yet. */
function libCountNote(ctx, lib) {
    if (lib.type === 'tvshows' && typeof lib.seriesCount === 'number') {
        return t('libMetaTv', { type: t('type_tvshows'), series: ctx.fmt.num(lib.seriesCount), episodes: ctx.fmt.num(lib.episodeCount || 0) });
    }

    if (typeof lib.remoteCount === 'number') {
        return t('libMetaRemote', { type: t('type_' + lib.type), count: ctx.fmt.num(lib.remoteCount) });
    }

    return t('libMeta', { type: t('type_' + lib.type), count: ctx.fmt.num(lib.count), n: lib.count });
}

/** True when the friend lists more titles than this server has stubs for, so both numbers are told (N2). */
function countsDiffer(m) {
    return typeof m.remoteCount === 'number' && typeof m.localCount === 'number' && m.remoteCount !== m.localCount;
}

/** "N titles come from X instead": a server whose titles another server (or this one) carries says so, and how to change it (K43 dedup). */
function dedupNote(s) {
    const n = s && s.hiddenByDedup;
    if (!(n > 0)) {
        return null;
    }

    return el('span', { class: 'shoal-meta shoal-card__dedup', text: ' ' + t(s.dedupWinner ? 'dedupNote' : 'dedupNoteHome', { n, count: new Intl.NumberFormat().format(n), winner: s.dedupWinner || '' }) });
}

function libLaterNote(type) {
    return type === 'tvshows' ? t('libTvOff') : t('libLater', { type: t('type_' + type) });
}

/** The TV line of an overlap report (GET Overlap tv), or null when the server sent none. */
function overlapTvLine(ctx, serverName, ov) {
    const tv = ov && ov.tv;
    if (!tv) {
        return null;
    }

    return t('ovTv', { a: ctx.fmt.num(tv.sharedSeries || 0), b: ctx.fmt.num(tv.sharedSeriesEpisodes || 0), server: serverName, c: ctx.fmt.num(tv.gainSeries || 0), d: ctx.fmt.num(tv.gainEpisodes || 0) });
}

function overlapSentences(ctx, serverName, ov) {
    const both = ov.both || { count: 0, localBytes: 0 };
    const gain = ov.gain || { count: 0, peerBytes: 0 };
    // With TV in the report the counts are movies plus episodes, so they are called items and the TV line says what they hold (D14).
    const word = (n) => (ov.tv ? t('itemWord', { n }) : t('titleWord', { n }));
    return {
        text: tRich('overlapSentence', {
            both: ctx.fmt.num(both.count),
            bothWord: word(both.count),
            bothSize: ctx.fmt.bytes(both.localBytes),
            server: serverName,
            gain: ctx.fmt.num(gain.count),
            gainWord: word(gain.count),
            gainSize: ctx.fmt.bytes(gain.peerBytes)
        }),
        plain: t('overlapAria', {
            both: ctx.fmt.num(both.count),
            bothWord: word(both.count),
            bothSize: ctx.fmt.bytes(both.localBytes),
            server: serverName,
            gain: ctx.fmt.num(gain.count),
            gainWord: word(gain.count),
            gainSize: ctx.fmt.bytes(gain.peerBytes)
        })
    };
}

/** The two-segment bar with its legend. The numbers sit in the legend so color is never the only key. */
function overlapViz(ctx, serverName, ov) {
    const both = ov.both || { count: 0, localBytes: 0 };
    const gain = ov.gain || { count: 0, peerBytes: 0 };
    const sentence = overlapSentences(ctx, serverName, ov);
    const bar = el('div', { class: 'shoal-obar', role: 'img', 'aria-label': sentence.plain });
    setVar(bar, '--shoal-both', Math.max(both.count, 0));
    setVar(bar, '--shoal-new', Math.max(gain.count, 0));
    if (both.count > 0) {
        bar.append(el('span', { class: 'shoal-obar__both' }));
    }

    if (gain.count > 0) {
        bar.append(el('span', { class: 'shoal-obar__new' }));
    }

    const legend = el('ul', { class: 'shoal-legend' },
        el('li', {}, el('span', { class: 'shoal-swatch shoal-swatch--both', 'aria-hidden': 'true' }),
            el('span', { text: t('legendBoth', { n: ctx.fmt.num(both.count), size: ctx.fmt.bytes(both.localBytes) }) })),
        el('li', {}, el('span', { class: 'shoal-swatch shoal-swatch--new', 'aria-hidden': 'true' }),
            el('span', { text: t('legendNew', { server: serverName, n: ctx.fmt.num(gain.count), size: ctx.fmt.bytes(gain.peerBytes) }) })));
    return el('div', { class: 'shoal-ov' }, bar, legend);
}

/** Builds the overlap panel's content into a container (Overview and Server detail share it). */
function fillOverlap(ctx, host, serverName, ov, linkTo) {
    clear(host);
    if (!ov || !ov.computedUtc) {
        host.append(el('p', { class: 'shoal-meta shoal-emptyline', text: t('overlapNone') }));
        return;
    }

    const sentence = overlapSentences(ctx, serverName, ov);
    const text = el('div', { class: 'shoal-overlap__text' }, el('p', { class: 'shoal-lead' }, sentence.text));
    const tvLine = overlapTvLine(ctx, serverName, ov);
    if (tvLine) {
        text.append(el('p', { class: 'shoal-body-text shoal-overlap__tv', text: tvLine }));
    }

    if (linkTo) {
        text.append(ctx.linkEl(linkTo.view, linkTo.query, 'shoal-link', t('fullReport')));
    }

    host.append(el('div', { class: 'shoal-overlap__grid' }, text, el('div', { class: 'shoal-overlap__viz' }, overlapViz(ctx, serverName, ov))));
}

/* ====================================================================================== */
/* Link speed panel (ui-spec 8.1 item 4, 8.3.3). Shared by Overview and Server detail      */
/* ====================================================================================== */

function kpi(label, valueNodes, unit) {
    return el('div', { class: 'shoal-kpi' },
        el('dt', { text: label }),
        el('dd', {}, valueNodes, unit ? el('span', { class: 'shoal-kpi__unit', text: unit }) : null));
}

/**
 * o: { detail: bool (30 days, six-row ladder, route and stability), servers, selected, onSelect(handle) }.
 * Returns { root, setServers(list, selected), refresh(), destroy() }.
 */
function createSpeedPanel(ctx, o) {
    const headId = uid('shoal-h');
    const select = el('select', { class: 'emby-select-withcolor emby-select shoal-select shoal-select--title', 'aria-label': t('serverLabel') });
    const selectWrap = el('span', { class: 'selectContainer shoal-field shoal-field--inline' }, select, icon('down'));
    const nameNode = el('span', { class: 'shoal-titlerow__name', hidden: true });
    const sub = el('p', { class: 'shoal-panel__sub' });
    const h2 = el('h2', { class: 'shoal-h2', id: headId, tabindex: '-1', text: t('linkSpeed') });
    const titleRow = el('div', { class: 'shoal-titlerow' }, h2, el('span', { class: 'shoal-titlerow__colon', 'aria-hidden': 'true', text: ':' }), selectWrap, nameNode);
    const ranges = o.detail ? ['24h', '7d', '30d'] : ['24h', '7d'];
    let rangeKey = '24h';
    const seg = segmented(ranges.map((r) => ({ value: r, label: t('range_' + r) })), rangeKey, (v) => {
        rangeKey = v;
        load();
    }, t('timeRange'));
    const kpis = el('dl', { class: 'shoal-kpis' });
    const chart = createChart(ctx);
    const noteText = el('p', { class: 'shoal-meta' });
    const testNote = el('span', { class: 'shoal-meta shoal-action__note', id: uid('shoal-note'), hidden: true });
    const testBtn = button({ label: t('testNow'), small: true, action: 'test-now' });
    const note = el('div', { class: 'shoal-speed__nodata', hidden: true }, noteText, el('span', { class: 'shoal-action' }, testBtn, testNote));
    const ladderTitle = el('h3', { class: 'shoal-h3' });
    const ladder = el('ul', { class: 'shoal-ladder' });
    const offlineNote = el('p', { class: 'shoal-offlinenote', hidden: true });
    const bottleneck = el('p', { class: 'shoal-bottleneck', hidden: true }, icon('up'), el('span'));
    const side = el('div', { class: 'shoal-speed__side' }, ladderTitle, ladder, offlineNote, bottleneck);
    const diag = o.detail ? null : ctx.linkEl('server', {}, 'shoal-link', t('openDiagnostics'));
    if (diag) {
        side.append(diag);
    }

    const errBox = el('div', { class: 'shoal-hint shoal-hint--bad shoal-speed__error', role: 'alert', hidden: true },
        icon('warn', true), el('p', { class: 'shoal-loaderror__text', text: t('speedLoadFailed') }),
        button({ label: t('tryAgain'), small: true, onClick: () => load() }));
    const extra = el('div', { class: 'shoal-speed__extra' });
    const main = el('div', { class: 'shoal-speed__main' }, kpis, chart.root, note, errBox);
    const root = el('section', { class: 'shoal-panel shoal-speed', 'aria-labelledby': headId, id: o.detail ? undefined : 'shoal-speed' },
        el('div', { class: 'shoal-panel__head' }, el('div', { class: 'shoal-panel__titles' }, titleRow, sub), seg),
        el('div', { class: 'shoal-speed__grid' }, main, side),
        extra);

    let servers = o.servers || [];
    let selected = o.selected || null;
    let data = null;
    let seq = 0;
    let loadedAt = 0;

    select.addEventListener('change', () => o.onSelect && o.onSelect(select.value));
    testBtn.addEventListener('click', () => {
        if (testBtn.getAttribute('aria-disabled') === 'true' || !selected) {
            return;
        }

        runAction(ctx, testBtn, {
            busyText: t('testing'),
            cooldownKey: 'test:' + selected,
            run: () => ctx.api.speedTest(selected, false),
            onDone: () => ctx.later(() => load(true), 1500),
            onError: (err) => {
                if (err.status === 429) {
                    ctx.setCooldown('test:' + selected, err.retryAfter || 60);
                } else {
                    ctx.alertMessage(t('testFailed'));
                }
            },
            afterBusy: syncCooldown
        });
    });

    function syncCooldown() {
        if (selected) {
            cooldownNote(ctx, 'test:' + selected, testNote, testBtn);
        }
    }

    function current() {
        return servers.find((s) => s.handle === selected) || null;
    }

    function renderHeader() {
        replaceKids(select, servers.map((s) => el('option', { value: s.handle, text: s.displayName })));
        if (selected) {
            select.value = selected;
        }

        const many = servers.length > 1;
        selectWrap.hidden = !many;
        nameNode.hidden = many;
        const cur = current();
        nameNode.textContent = cur ? cur.displayName : '';
        if (diag && cur) {
            ctx.link(diag, 'server', { id: cur.handle, focus: 'speed' });
        }
    }

    function renderBody() {
        const cur = current();
        if (!cur || !data) {
            return;
        }

        const model = describeServer(cur, ctx.fmt);
        const samples = data.samples || [];
        const tests = samples.filter((s) => s.kind !== 'playback').length;
        sub.textContent = t('speedSub_' + rangeKey, { tests, plays: samples.length - tests, testWord: t('testWord', { n: tests }), playWord: t('playWord', { n: samples.length - tests }) });
        const k = data.kpis || {};
        const offline = model.key === 'offline';
        // "Speed now" is the newest point of the chart, so the card and the chart never give two different latest speeds.
        const newest = samples.filter((x) => typeof x.mbps === 'number' && !x.error).reduce((m, x) => (!m || (toMs(x.t) || 0) >= (toMs(m.t) || 0) ? x : m), null);
        const nowMbps = newest ? newest.mbps : k.nowMbps;
        const nowValue = !offline && typeof nowMbps === 'number' ? [el('span', { class: 'shoal-kpi__num', text: ctx.fmt.mbpsValue(nowMbps) })] : [unknownValue(), offline ? el('span', { class: 'shoal-kpi__offline', text: t('offlineWord') }) : null];
        const kpiNodes = [
            kpi(t('kpiNow'), nowValue, !offline && typeof nowMbps === 'number' ? t('unitMbpsShort') : null),
            kpi(t('kpiLatency'), !offline && typeof k.latencyMs === 'number' ? [el('span', { class: 'shoal-kpi__num', text: ctx.fmt.num(Math.round(k.latencyMs)) })] : unknownValue(), !offline && typeof k.latencyMs === 'number' ? t('unitMsShort') : null)
        ];
        if (o.detail) {
            const unstable = (k.jitterMs || 0) > 30 || (k.lossPct || 0) > 2;
            kpiNodes.push(kpi(t('kpiStability'), offline ? unknownValue() : el('span', { class: 'shoal-kpi__word' + (unstable ? ' is-warn' : ''), text: unstable ? t('stabilityBad', { jitter: Math.round(k.jitterMs || 0), loss: k.lossPct || 0 }) : t('stabilityGood') })));
        }

        kpiNodes.push(kpi(t('kpiLowest_' + rangeKey), typeof k.lowestMbps === 'number' ? [el('span', { class: 'shoal-kpi__num', text: ctx.fmt.mbpsValue(k.lowestMbps) })] : unknownValue(), typeof k.lowestMbps === 'number' ? t('unitMbpsShort') : null));
        replaceKids(kpis, kpiNodes);
        kpis.classList.toggle('shoal-kpis--four', kpiNodes.length === 4);

        chart.update({ serverName: cur.displayName, rangeKey, samples, offline: data.offline, routeChanges: data.routeChanges });

        const plotted = samples.filter((s) => typeof s.mbps === 'number' && !s.error).length;
        note.hidden = plotted >= 2;
        if (plotted < 2) {
            noteText.textContent = t(plotted === 0 ? 'chartNoTests' : 'chartFewTests');
        }

        ladderTitle.textContent = t('ladderTitle', { server: cur.displayName });
        replaceKids(ladder, ladderRows(data.ladder, o.detail ? 'detail' : 'overview').map((r) => {
            const v = LADDER_VERDICTS[r.verdict] || LADDER_VERDICTS.mayBuffer;
            return el('li', { class: 'shoal-ladder__row shoal-tone--' + v.tone },
                icon(v.icon),
                el('span', { class: 'shoal-ladder__q', text: t('ladderClass_' + r.cls) }),
                el('span', { class: 'shoal-ladder__v', text: t(v.key, { server: cur.displayName, n: Math.ceil(r.neededMbps || 0) }) }));
        }));
        ladder.hidden = offline;
        ladderTitle.hidden = offline;
        offlineNote.hidden = !offline;
        if (offline) {
            const last = samples.length ? samples.reduce((m, s) => Math.max(m, toMs(s.t) || 0), 0) : toMs(data.lastMeasuredUtc);
            offlineNote.textContent = last ? t('speedOffline', { server: cur.displayName, when: ctx.fmt.relative(last) }) : t('speedOfflineNever', { server: cur.displayName });
        }

        const bt = offline ? '' : bottleneckText(data.bottleneck, cur.displayName);
        bottleneck.hidden = !bt;
        bottleneck.querySelector('span').textContent = bt;

        if (data.cooldowns && data.cooldowns.testUntilUtc) {
            const secs = Math.round((toMs(data.cooldowns.testUntilUtc) - ctx.now()) / 1000);
            if (secs > 0) {
                ctx.setCooldown('test:' + selected, secs);
            }
        }

        syncCooldown();
        if (o.onData) {
            o.onData(data, cur, extra);
        }
    }

    async function load(quiet) {
        const handle = selected;
        if (!handle) {
            return;
        }

        const mine = ++seq;
        errBox.hidden = true;
        if (!quiet) {
            root.classList.add('is-loading');
        }

        try {
            const d = await ctx.api.speed(handle, rangeKey);
            if (mine !== seq || !ctx.alive) {
                return;
            }

            data = d;
            loadedAt = ctx.now();
            renderBody();
            if (d && d.testRunning) {
                // A test takes seconds to a minute: look again until the result is in.
                ctx.later(() => load(true), 3000);
            }
        } catch {
            if (mine !== seq || !ctx.alive) {
                return;
            }

            if (!data) {
                errBox.hidden = false;
            }
        } finally {
            if (mine === seq) {
                root.classList.remove('is-loading');
            }
        }
    }

    renderHeader();
    return {
        root,
        extra,
        range: () => rangeKey,
        setServers(list, sel) {
            const changed = sel !== selected;
            servers = list;
            selected = sel;
            renderHeader();
            if (changed || (!data && !root.classList.contains('is-loading'))) {
                data = null;
                load();
            } else if (data) {
                renderBody();
            }
        },
        refresh(force) {
            if (force || ctx.now() - loadedAt > 55000) {
                return load(true);
            }

            return Promise.resolve();
        },
        load,
        destroy() {
            chart.destroy();
        }
    };
}

/* ====================================================================================== */
/* Screen: Overview (ui-spec 8.1)                                                         */
/* ====================================================================================== */

function sizeClass(root) {
    const w = root.clientWidth || 1240;
    return w >= 1080 ? 'L' : w >= 640 ? 'M' : 'S';
}

function reducedMotion() {
    return typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

function scrollToNode(node) {
    if (node && typeof node.scrollIntoView === 'function') {
        node.scrollIntoView({ behavior: reducedMotion() ? 'auto' : 'smooth', block: 'start' });
    }
}

function overviewScreen(ctx) {
    const view = q(ctx.root, '[data-shoal-view="overview"]');
    const versionHost = el('div', { class: 'shoal-version' });
    view.append(versionHost);
    fillVersion(ctx, versionHost);
    const blocks = {};
    for (const name of ['loading', 'error', 'empty', 'content']) {
        blocks[name] = q(view, '[data-state-block="' + name + '"]');
    }

    const statusLine = slot(view, 'statusline');
    const syncBtn = q(view, '[data-action="sync-all"]');
    const syncNote = slot(view, 'syncnote');
    syncNote.id = uid('shoal-sn');
    const actionError = slot(view, 'actionerror');
    const cardsHost = slot(view, 'cards');
    let legacyNode = null;
    const noServers = slot(view, 'noservers');
    const serversNote = slot(view, 'serversnote');
    const speedHost = slot(view, 'speedhost');
    const overlapSection = q(view, '[data-section="overlap"]');
    const overlapTitle = slot(view, 'overlaptitle');
    const overlapBody = slot(view, 'overlapbody');
    const postersSection = q(view, '[data-section="posters"]');
    const postersHost = slot(view, 'posters');
    const postersEmpty = slot(view, 'postersempty');
    const seeAll = slot(view, 'seeall');
    const friendsHost = slot(view, 'friends');
    const friendsEmpty = slot(view, 'friendsempty');
    let orphanHost = null;
    let linkedHost = null;
    let sandboxHost = null;
    const cards = new Map();
    let inviteNotice = null;
    const st = { data: null, selected: null, speed: null, overlapHandle: null, overlapSeq: 0, polls: 0, prev: new Map(), posterSig: '', friendSig: '', loaded: false, lastAnnounce: 0 };
    const poller = createPoller(ctx, () => load(false), STATUS_POLL_MS);

    ctx.link(seeAll, 'libraries');

    function setBlock(name) {
        for (const [key, node] of Object.entries(blocks)) {
            node.hidden = key !== name;
        }
    }

    function showActionError(message) {
        actionError.hidden = !message;
        slot(actionError, 'actionerrortext').textContent = message || '';
        if (message) {
            ctx.alertMessage(message);
        }
    }

    function syncCooldown() {
        cooldownNote(ctx, 'sync', syncNote, syncBtn);
    }

    for (const b of qa(view, '[data-action="pair"]')) {
        b.addEventListener('click', () => ctx.navigate('pair', { dir: 'watch' }));
    }

    for (const b of qa(view, '[data-action="share"]')) {
        b.addEventListener('click', () => ctx.navigate('pair', { dir: 'share' }));
    }

    q(view, '[data-action="retry-load"]').addEventListener('click', () => load(true));
    syncBtn.addEventListener('click', () => {
        runAction(ctx, syncBtn, {
            busyText: t('syncing'),
            cooldownKey: 'sync',
            run: () => ctx.api.syncAll(),
            onDone: () => {
                showActionError('');
                ctx.announce(t('announceSyncStarted'));
                poller.runNow();
            },
            onError: (err) => {
                if (err.status === 429) {
                    ctx.setCooldown('sync', err.retryAfter || 60);
                } else {
                    showActionError(t('syncFailed'));
                }
            },
            afterBusy: syncCooldown
        });
    });

    /* ---------- status line ---------- */

    function sep() {
        return el('span', { class: 'shoal-status__sep', 'aria-hidden': 'true', text: '·' });
    }

    function renderStatusLine(servers, friends) {
        if (servers.length === 0) {
            statusLine.hidden = true;
            return;
        }

        statusLine.hidden = false;
        const sum = summarizeServers(servers, ctx.fmt, friends);
        const needs = sum.attention.length + sum.attentionFriends.length;
        const problem = needs > 0 || sum.online < sum.total;
        const items = [el('span', { class: 'shoal-status__item shoal-tone--' + (problem ? 'warn' : 'ok') }, icon(problem ? 'warn' : 'ok', true), t('statusOnline', { online: sum.online, total: sum.total }))];
        if (needs > 0) {
            items.push(sep(), linkButton(t('needAttention', { n: needs }), () => {
                const first = sortServers(servers).find((s) => sum.attention.includes(s.handle));
                const card = first && cards.get(first.handle);
                const link = card ? slot(card, 'name') : q(friendsHost, '[data-attention="1"]');
                if (link) {
                    link.focus();
                }
            }, 'shoal-status__link'));
        }

        const off = sum.titlesOffline || 0;
        const paused = sum.titlesPaused || 0;
        const titlesText = paused > 0 && off === 0 && sum.titles === 0
            ? t('titlesOnlyPaused', { n: paused, count: ctx.fmt.num(paused) })
            : off > 0 && sum.titles === 0
            ? t('titlesOnlyOffline', { n: off, off: ctx.fmt.num(off) })
            : off > 0 ? t('titlesOfflineToo', { n: sum.titles, count: ctx.fmt.num(sum.titles), off: ctx.fmt.num(off) }) : t('titlesSharedWithYou', { n: sum.titles, count: ctx.fmt.num(sum.titles) });
        items.push(sep(), el('span', { class: 'shoal-status__item', text: titlesText }));
        const syncing = servers.some((s) => s.syncing);
        const last = servers.reduce((m, s) => Math.max(m, toMs(s.lastSync && s.lastSync.atUtc) || 0), 0);
        items.push(sep(), el('span', { class: 'shoal-status__item' }, icon('clock', true), syncing ? t('syncingNow') : last ? t('lastSync', { when: ctx.fmt.relative(last) }) : t('notSyncedYet')));
        // Servers paired the old way (D17): one calm banner above the cards, with the way forward on each card.
        const legacy = servers.filter((x) => x.needsRepair === true || x.pairingMethod === 'qc').length;
        if (legacyNode) {
            legacyNode.remove();
            legacyNode = null;
        }

        if (legacy > 0 && cardsHost.parentNode) {
            legacyNode = hint('warn', 'keyoff', t('legacyBanner', { n: legacy }));
            legacyNode.classList.add('shoal-legacybanner');
            cardsHost.parentNode.insertBefore(legacyNode, cardsHost);
        }

        // What counts as a title, in plain sight (Lumen 04): touch and keyboard users never see a tooltip.
        items.push(el('span', { class: 'shoal-status__note shoal-meta', text: t('titlesCounted') }));
        replaceKids(statusLine, items);
    }

    /* ---------- cards ---------- */

    function buildCard(server) {
        const node = ctx.tmpl('card');
        node.dataset.handle = server.handle;
        const nameId = uid('shoal-cn');
        const chipId = uid('shoal-cc');
        slot(node, 'name').id = nameId;
        slot(node, 'chip').dataset.chipId = chipId;
        node.setAttribute('aria-labelledby', nameId + ' ' + chipId);
        ctx.link(slot(node, 'name'), 'server', { id: server.handle });
        return node;
    }

    function artChain(s) {
        const l = s.latest;
        if (!l || !l.localItemId) {
            return [];
        }

        return [
            l.backdropTag ? { url: itemImageUrl(ctx, l.localItemId, 'Backdrop', l.backdropTag, 400, 80), cls: 'backdrop' } : null,
            l.primaryTag ? { url: itemImageUrl(ctx, l.localItemId, 'Primary', l.primaryTag, 400, 80), cls: 'primary' } : null
        ].filter(Boolean);
    }

    function updateCard(node, s) {
        const keysLocked = !!(ctx.status && ctx.status.secretsUnreadable);
        const model = describeServer(s, ctx.fmt, { keysLocked });
        const isSelected = st.selected === s.handle;
        const sig = JSON.stringify([s, isSelected, keysLocked, ctx.cooldownMinutes('test:' + s.handle)]) + Math.floor(ctx.now() / 60000);
        if (node.dataset.sig === sig) {
            return;
        }

        node.dataset.sig = sig;
        node.dataset.state = model.key;
        node.classList.toggle('is-selected', isSelected);
        node.classList.toggle('is-dim', model.dim);

        // art
        const img = slot(node, 'img');
        const ph = slot(node, 'ph');
        const pill = slot(node, 'pill');
        const chain = artChain(s);
        const key = chain.map((c) => c.url).join('|');
        if (key !== img.dataset.artKey) {
            img.dataset.artKey = key;
            if (chain.length) {
                ph.hidden = true;
                loadImageChain(img, chain, () => {
                    ph.hidden = false;
                });
            } else {
                img.hidden = true;
                img.removeAttribute('src');
                ph.hidden = false;
            }
        }

        slot(node, 'phtext').textContent = model.key === 'pairing' ? t('waitingFor', { server: s.displayName }) : model.key === 'pairingStopped' || model.key === 'pairingSafety' ? t('pairingStoppedArt') : t('noPictures');
        pill.hidden = false;
        const pillIcon = q(pill, 'svg');
        if (model.key === 'offline' || model.key === 'paused') {
            pill.hidden = true; // the status chip beside the name says it; a second "Offline" on the picture only repeats it
        } else if (s.latest && s.latest.title) {
            replaceIcon(pillIcon, 'play');
            slot(pill, 'pilltext').textContent = t('pillLatest', { server: s.displayName, title: s.latest.title });
        } else {
            pill.hidden = true;
        }

        // name and chip
        slot(node, 'nametext').textContent = s.displayName;
        const chipHost = slot(node, 'chip');
        replaceKids(chipHost, chip(model.tone, model.icon, model.chipText, chipHost.dataset.chipId));

        // via line
        const via = viaOf(s);
        replaceKids(slot(node, 'via'), icon(via.icon, true), el('span', { text: via.text }), via.host ? [el('span', { 'aria-hidden': 'true', text: '·' }), el('span', { class: 'shoal-card__host', text: via.host })] : null, via.proxy ? el('span', { text: via.proxy }) : null);

        // metrics
        const conn = connectionOf(s, model.online);
        replaceKids(slot(node, 'mconn'), conn ? [icon(conn.icon, true), conn.text] : unknownValue());
        replaceKids(slot(node, 'mrtt'), model.online && typeof s.rttMs === 'number' ? ctx.fmt.ms(s.rttMs) : unknownValue());
        replaceKids(slot(node, 'mspeed'), model.online && typeof s.mbps === 'number' ? ctx.fmt.mbps(s.mbps) : unknownValue());

        // verdict and hint
        const verdict = slot(node, 'verdict');
        verdict.hidden = !model.verdict;
        slot(node, 'verdicttext').textContent = model.verdict;
        replaceKids(slot(node, 'hint'), model.hint ? hint(model.hint.tone, model.hint.tone === 'bad' ? 'warn' : model.icon === 'offline' || model.icon === 'pause' ? 'info' : 'warn', model.hint.text) : null);

        // alerts line (ui-spec 7): the first that applies, "and k more" when more exist; a link to the server's alerts
        const hold = slot(node, 'hold');
        const alertCount = s.alertCount || 0;
        hold.hidden = alertCount <= 0 || model.keysLocked;
        if (!hold.hidden) {
            const held = s.heldRemovals || 0;
            const heldAdd = s.heldAdditions || 0;
            let text;
            let named = 1;
            if (held > 0) {
                text = t('removalsOnHold', { n: held, count: ctx.fmt.num(held) });
            } else if (heldAdd > 0) {
                text = t('additionsOnHold', { n: heldAdd, count: ctx.fmt.num(heldAdd) });
            } else {
                text = t('thingsToCheck', { n: alertCount });
                named = alertCount;
            }

            if (alertCount > named) {
                text += t('andKMore', { k: alertCount - named });
            }

            replaceKids(hold, icon('warn', true), ctx.linkEl('server', { id: s.handle, focus: 'alerts' }, 'shoal-link', text));
        }

        // footer: title count (or next check), action
        const titles = slot(node, 'titles');
        if (model.key === 'offline' && s.nextCheckUtc) {
            titles.textContent = t('nextCheck', { n: ctx.fmt.minutesUntil(s.nextCheckUtc) });
        } else if (model.key === 'pairing' || ((model.key === 'pairingStopped' || model.key === 'pairingSafety') && model.neverPaired)) {
            titles.textContent = model.key === 'pairing' ? '' : t('notPaired');
        } else {
            replaceKids(titles, [tRich('titlesShared', { n: s.titleCount || 0, count: ctx.fmt.num(s.titleCount || 0) }), dedupNote(s)]);
        }

        const actionHost = slot(node, 'action');
        if (model.action) {
            const existing = q(actionHost, 'button[data-kind]');
            if (existing && existing.dataset.kind === model.action.kind) {
                const label = q(existing, '.shoal-btn__label');
                if (label && existing.dataset.label === undefined) {
                    label.textContent = model.action.label;
                }
            } else {
                const btn = button({ label: model.action.label, small: true, onClick: () => cardAction(model.action.kind, s.handle, btn) });
                btn.dataset.kind = model.action.kind;
                replaceKids(actionHost, btn);
            }
        } else if (isSelected) {
            const existing = q(actionHost, 'button[data-kind="below"]');
            if (!existing) {
                const b = linkButton(t('speedShownBelow'), () => scrollToSpeed(), 'shoal-card__below');
                b.dataset.kind = 'below';
                b.prepend(icon('down', true));
                replaceKids(actionHost, b);
            }
        } else {
            clear(actionHost);
        }
    }

    function replaceIcon(svg, name) {
        const use = svg && svg.querySelector('use');
        if (use) {
            use.setAttribute('href', '#shoal-i-' + name);
        }
    }

    function scrollToSpeed() {
        if (st.speed) {
            const shown = serversOf().find((x) => x.handle === st.selected);
            ctx.toast(t('announceShowingSpeed', { server: shown ? shown.displayName : '' }));
            scrollToNode(st.speed.root);
            const h = q(st.speed.root, 'h2');
            if (h) {
                h.focus({ preventScroll: true });
            }

            st.speed.root.classList.add('is-flash');
            ctx.later(() => st.speed.root.classList.remove('is-flash'), 1600);
        }
    }

    function cardAction(kind, handle, btn) {
        switch (kind) {
            case 'test':
            case 'retry':
                return runAction(ctx, btn, {
                    busyText: t(kind === 'test' ? 'testing' : 'retrying'),
                    cooldownKey: 'test:' + handle,
                    run: () => ctx.api.speedTest(handle, false),
                    onDone: () => {
                        const said = t(kind === 'test' ? 'announceTestStarted' : 'announceRetryStarted');
                        ctx.announce(said);
                        ctx.toast(said);
                        ctx.later(() => poller.runNow(), 1500);
                    },
                    onError: (err) => {
                        if (err.status === 429) {
                            ctx.setCooldown('test:' + handle, err.retryAfter || 60);
                            ctx.alertMessage(t('availableIn', { n: ctx.cooldownMinutes('test:' + handle) || 1 }));
                        } else {
                            ctx.alertMessage(t('testFailed'));
                        }
                    }
                });
            case 'resume':
                return runAction(ctx, btn, {
                    busyText: t('resuming'),
                    run: () => ctx.api.resumePeer(handle),
                    onDone: () => poller.runNow(),
                    failKey: 'genericFailure'
                });
            case 'repair':
            case 'repairInvite':
                return ctx.navigate('pair', { dir: 'watch', again: handle });
            case 'openRepair':
                return ctx.navigate('server', { id: handle });
            case 'review':
                return ctx.navigate('server', { id: handle, focus: 'alerts' });
            case 'continuePair':
                return ctx.navigate('pair', { dir: 'watch' });
            default:
                // "See what to do": the stored outcome, with Start again and Dismiss (ui-spec 8.2.9).
                return ctx.navigate('pair', { id: handle });
        }
    }

    cardsHost.addEventListener('click', (ev) => {
        if (ev.target.closest('a, button')) {
            return;
        }

        const card = ev.target.closest('.shoal-card');
        if (card && card.dataset.handle) {
            select(card.dataset.handle, true);
        }
    });

    function renderCards(servers) {
        const keep = new Set();
        servers.forEach((s, index) => {
            keep.add(s.handle);
            let node = cards.get(s.handle);
            if (!node) {
                node = buildCard(s);
                cards.set(s.handle, node);
            }

            updateCard(node, s);
            if (cardsHost.children[index] !== node) {
                cardsHost.insertBefore(node, cardsHost.children[index] || null);
            }
        });
        for (const [handle, node] of cards) {
            if (!keep.has(handle)) {
                node.remove();
                cards.delete(handle);
            }
        }
    }

    /* ---------- selection, speed and overlap ---------- */

    function select(handle, announce) {
        if (!handle || handle === st.selected) {
            return;
        }

        st.selected = handle;
        ctx.storage.set(SELECTED_KEY, handle);
        applySelection();
        if (announce) {
            const s = serversOf().find((x) => x.handle === handle);
            ctx.announce(t('announceShowingSpeed', { server: s ? s.displayName : '' }));
ctx.toast(t('announceShowingSpeed', { server: s ? s.displayName : '' }));
        }
    }

    function serversOf() {
        return sortServers((st.data && st.data.servers) || []);
    }

    function applySelection() {
        const servers = serversOf();
        for (const s of servers) {
            const node = cards.get(s.handle);
            if (node) {
                updateCard(node, s);
            }
        }

        if (st.speed) {
            st.speed.setServers(servers, st.selected);
        }

        loadOverlap();
    }

    async function loadOverlap() {
        const handle = st.selected;
        const cur = serversOf().find((s) => s.handle === handle);
        show(overlapSection, !!cur);
        if (!cur) {
            return;
        }

        overlapTitle.textContent = t('overlapTitle', { server: cur.displayName });
        const mine = ++st.overlapSeq;
        try {
            const ov = await ctx.api.overlap(handle);
            if (mine !== st.overlapSeq || !ctx.alive) {
                return;
            }

            st.overlapHandle = handle;
            fillOverlap(ctx, overlapBody, cur.displayName, ov, { view: 'overlap', query: { id: handle } });
        } catch {
            if (mine === st.overlapSeq) {
                replaceKids(overlapBody, el('p', { class: 'shoal-meta shoal-emptyline', text: t('overlapNone') }));
            }
        }
    }

    function ensureSpeedPanel() {
        if (st.speed) {
            return;
        }

        st.speed = createSpeedPanel(ctx, { servers: [], selected: null, onSelect: (h) => select(h, true) });
        speedHost.append(st.speed.root);
    }

    /* ---------- posters and friends ---------- */

    function visiblePosterCount() {
        const size = sizeClass(ctx.root);
        if (size === 'S') {
            return 4;
        }

        const w = ctx.root.clientWidth || 1240;
        const min = size === 'L' ? 160 : 140;
        const gap = size === 'L' ? 20 : 16;
        return Math.max(2, Math.min(6, Math.floor((w - 48 + gap) / (min + gap))));
    }

    function renderPosters(data) {
        const list = (data.recentlyShared || []).slice(0, 12);
        const servers = data.servers || [];
        const withTitles = servers.some((s) => (s.titleCount || 0) > 0);
        postersSection.hidden = servers.length === 0;
        if (servers.length === 0) {
            return;
        }

        const sig = JSON.stringify(list) + visiblePosterCount();
        if (sig === st.posterSig) {
            return;
        }

        st.posterSig = sig;
        clear(postersHost);
        const parked = servers.filter((s) => (data.parkedServers || []).includes(s.handle));
        for (const p of list) {
            postersHost.append(buildPoster(p));
        }

        for (const s of parked) {
            postersHost.append(el('li', { class: 'shoal-posters__item shoal-posters__parked' },
                icon('offline'),
                el('span', { class: 'shoal-posters__parkedtext', text: t('parkedTitles', { server: s.displayName }) }),
                el('span', { class: 'shoal-meta', text: t('parkedBack') })));
        }

        const limit = visiblePosterCount();
        Array.from(postersHost.children).forEach((node, i) => {
            node.hidden = i >= limit;
        });
        postersEmpty.hidden = list.length > 0 || !!parked.length;
        if (!postersEmpty.hidden) {
            postersEmpty.textContent = t(withTitles ? 'postersNone' : 'postersNoneYet');
        }
    }

    function buildPoster(p) {
        const node = ctx.tmpl('poster');
        const link = q(node, 'a');
        const serverId = ctx.client && typeof ctx.client.serverId === 'function' ? ctx.client.serverId() : '';
        link.setAttribute('href', '#/details?id=' + encodeURIComponent(p.localItemId) + (serverId ? '&serverId=' + encodeURIComponent(serverId) : ''));
        const yours = p.kind === 'yourCopy';
        const img = slot(node, 'img');
        const ph = slot(node, 'ph');
        slot(node, 'phtitle').textContent = p.title;
        loadImageChain(img, [{ url: itemImageUrl(ctx, p.localItemId, 'Primary', p.primaryTag, 180, 90) }], () => {
            ph.hidden = false;
        });
        const source = slot(node, 'source');
        source.classList.toggle('is-yours', yours);
        replaceIcon(q(source, 'svg'), yours ? 'check' : 'share');
        slot(node, 'sourcetext').textContent = yours ? t('yourCopy') : p.serverName;
        slot(node, 'title').textContent = p.title;
        slot(node, 'meta').textContent = yours
            ? t('posterMetaYours', { server: p.serverName })
            : t('posterMeta', { year: p.year || '', runtime: p.runtimeMin || '', server: p.serverName });
        return node;
    }

    function renderInviteNotice(data) {
        const f = data.inviteFailures;
        const show = !!(f && f.notice);
        if (!inviteNotice) {
            inviteNotice = hint('info', 'info', t('inviteNotice', { n: show ? f.lastHour : 0 }));
            inviteNotice.hidden = true;
            friendsHost.before(inviteNotice);
        }

        inviteNotice.hidden = !show;
        if (show) {
            q(inviteNotice, '.shoal-hint__body').textContent = t('inviteNotice', { n: f.lastHour });
        }
    }

    // Leftover sharing accounts (Warden 08 M7): listed with a Delete button; Shoal never deletes one by itself, and the dialog wants the name typed.
    function renderOrphans(data) {
        const list = Array.isArray(data.orphanAccounts) ? data.orphanAccounts.filter((a) => a && typeof a.id === 'string' && typeof a.name === 'string') : [];
        const sig = JSON.stringify(list.map((a) => [a.id, a.name, !!a.isDisabled]));
        if (!orphanHost) {
            orphanHost = el('section', { class: 'shoal-orphans', 'aria-labelledby': 'shoal-orphans-h-' + ctx.instance });
            orphanHost.hidden = true;
            blocks.content.before(orphanHost);
        }

        if (sig === st.orphanSig) {
            return;
        }

        st.orphanSig = sig;
        clear(orphanHost);
        orphanHost.hidden = list.length === 0;
        if (!list.length) {
            return;
        }

        const rows = list.map((a) => {
            const del = button({ label: t('orphanDelete'), variant: 'danger', onClick: () => deleteOrphan(a) });
            del.setAttribute('aria-label', t('orphanDeleteLabel', { name: a.name }));
            return el('li', { class: 'shoal-row shoal-orphans__row' },
                el('span', { class: 'shoal-row__name', text: a.name }),
                chip(a.isDisabled ? 'off' : 'warn', a.isDisabled ? 'pause' : 'warn', t(a.isDisabled ? 'orphanDisabled' : 'orphanActive')),
                del);
        });
        orphanHost.append(
            el('h2', { class: 'shoal-h2', id: 'shoal-orphans-h-' + ctx.instance, text: t('orphanTitle') }),
            hint('warn', 'warn', t('orphanBody')),
            el('ul', { class: 'shoal-list shoal-orphans__list' }, rows));
    }

    async function deleteOrphan(account) {
        const res = await confirmDialog(ctx, {
            title: t('orphanDeleteTitle', { name: account.name }),
            intro: t('orphanDeleteBody', { name: account.name }),
            typed: { label: t('typeToConfirm', { text: account.name }), expected: account.name },
            confirmLabel: t('orphanDeleteConfirm'),
            danger: true
        });
        if (!res.confirmed) {
            return;
        }

        try {
            await ctx.api.deleteOrphanAccount(account.id);
            ctx.announce(t('announceOrphanDeleted', { name: account.name }));
        } catch (err) {
            if (!err || err.status !== 404) {
                ctx.fail(failureMessage(err));
                return;
            }
        }

        load(true);
    }

    // The picture check could not start (DESIGN v2.6.7): a calm alert with the way to Settings; pictures go on the usual way meanwhile.
    function renderSandboxNotice(data) {
        if (!sandboxHost) {
            sandboxHost = el('section', { class: 'shoal-sandboxalert' });
            sandboxHost.hidden = true;
            blocks.content.before(sandboxHost);
        }

        const down = !!(data.imageSandbox && data.imageSandbox.state === 'unavailable');
        const reason = data.imageSandbox && (data.imageSandbox.reason === 'ffmpegMissing' || data.imageSandbox.reason === 'selfTestFailed') ? data.imageSandbox.reason : 'other';
        const sig = down ? reason : '';
        if (sig === st.sandboxSig) {
            return;
        }

        st.sandboxSig = sig;
        clear(sandboxHost);
        sandboxHost.hidden = !down;
        if (down) {
            sandboxHost.append(hint('warn', 'warn', el('strong', { text: t('sandboxAlertTitle') }), el('span', { class: 'shoal-hint__line', text: t('sandboxUnavailable_' + reason) }), ctx.linkEl('settings', {}, 'shoal-link', t('openSettings'))));
        }
    }

    // Friends who share both ways (DESIGN v2.6.2): one card, two rows, and the worse of the two states as the chip. Unlinked friends stay as they were.
    function renderLinkedFriends(data) {
        const linked = (Array.isArray(data.friendLinks) ? data.friendLinks : []).filter((l) => l && l.linked && l.watch && l.share);
        if (!linkedHost) {
            linkedHost = el('section', { class: 'shoal-twoway', 'aria-labelledby': 'shoal-twoway-h-' + ctx.instance });
            linkedHost.hidden = true;
            // Its own block above the servers, not inside the servers' header row.
            const serversSection = serversNote.closest('.shoal-section');
            if (serversSection) {
                serversSection.before(linkedHost);
            } else {
                cardsHost.before(linkedHost);
            }
        }

        const sig = JSON.stringify(linked);
        if (sig === st.linkedSig) {
            return;
        }

        st.linkedSig = sig;
        clear(linkedHost);
        linkedHost.hidden = linked.length === 0;
        if (!linked.length) {
            return;
        }

        const cardsList = linked.map((l) => {
            const roll = describeLinkStatus(l.status);
            const watch = describeLinkStatus(l.watch.status);
            const share = describeLinkStatus(l.share.status);
            const watchMeta = [countText(ctx, 'titlesN', l.watch.titles), l.watch.activeStreams > 0 ? countText(ctx, 'streamsNowN', l.watch.activeStreams) : null].filter(Boolean).join(' · ');
            const sharedMeta = [t('nLibraries', { n: l.share.libraryCount }), t('nDevices', { n: l.share.deviceCount }), l.share.playingNow > 0 ? countText(ctx, 'playingNowN', l.share.playingNow) : null].filter(Boolean).join(' · ');
            const openServer = ctx.linkEl('server', { id: l.watch.peerHandle }, 'shoal-link', t('openServerLink'));
            openServer.setAttribute('aria-label', t('openServerAria', { name: l.name }));
            const openFriend = ctx.linkEl('friend', { id: l.share.shareHandle }, 'shoal-link', t('openFriendLink'));
            openFriend.setAttribute('aria-label', t('openFriendAria', { name: l.name }));
            // The card's own chip is only worth showing when it says something the two rows do not.
            const rollNeeded = roll.text !== watch.text && roll.text !== share.text;
            return el('li', { class: 'shoal-twoway__card' },
                el('div', { class: 'shoal-twoway__head' }, el('span', { class: 'shoal-twoway__name', text: l.name }), rollNeeded ? chip(roll.tone, roll.icon, roll.text) : null),
                el('div', { class: 'shoal-twoway__row' },
                    el('span', { class: 'shoal-twoway__label', text: t('twoWayWatch', { name: l.name }) }),
                    chip(watch.tone, watch.icon, watch.text),
                    el('span', { class: 'shoal-meta', text: watchMeta }),
                    openServer),
                el('div', { class: 'shoal-twoway__row' },
                    el('span', { class: 'shoal-twoway__label', text: t('twoWayWatched', { name: l.name }) }),
                    chip(share.tone, share.icon, share.text),
                    el('span', { class: 'shoal-meta', text: sharedMeta }),
                    openFriend));
        });
        linkedHost.append(
            el('h2', { class: 'shoal-h2', id: 'shoal-twoway-h-' + ctx.instance, text: t('twoWayTitle') }),
            el('p', { class: 'shoal-meta', text: t('twoWayIntro') }),
            el('ul', { class: 'shoal-twoway__list' }, cardsList));
    }

    function renderFriends(data) {
        const friends = data.friends || [];
        renderInviteNotice(data);
        const alsoServer = new Map((Array.isArray(data.friendLinks) ? data.friendLinks : []).filter((l) => l && l.linked && l.share).map((l) => [l.share.shareHandle, l.name]));
        const sig = JSON.stringify(friends) + JSON.stringify(Array.from(alsoServer));
        friendsEmpty.hidden = friends.length > 0;
        if (sig === st.friendSig) {
            return;
        }

        st.friendSig = sig;
        clear(friendsHost);
        for (const f of friends) {
            const node = ctx.tmpl('friend-row');
            ctx.link(q(node, 'a'), 'friend', { id: f.handle });
            slot(node, 'name').textContent = f.name;
            const metaNode = slot(node, 'meta');
            metaNode.textContent = t('friendMeta', { libs: t('nLibraries', { n: f.libraryCount }), devs: t('nDevices', { n: f.deviceCount }), when: f.lastSeenUtc ? ctx.fmt.relative(f.lastSeenUtc) : t('never') });
            const model = describeFriend(f);
            slot(node, 'chip').append(chip(model.tone, model.icon, model.text));
            if (f.unexpectedDeviceCount > 0) {
                q(node, 'a').dataset.attention = '1';
                metaNode.after(el('span', { class: 'shoal-row__meta shoal-meta shoal-tone--warn shoal-row__warn' }, icon('warn', true), t('unexpectedDevices', { n: f.unexpectedDeviceCount })));
            }

            if (alsoServer.has(f.handle)) {
                metaNode.after(el('span', { class: 'shoal-row__meta shoal-meta shoal-row__also', text: t('alsoYourServer', { name: alsoServer.get(f.handle) }) }));
            }

            const now = slot(node, 'now');
            if (f.playingNow > 0) {
                now.hidden = false;
                now.textContent = f.transcodingNow > 0 ? t('playingNowTranscoding', { n: f.playingNow, t: f.transcodingNow }) : t('playingNow', { n: f.playingNow });
            }

            friendsHost.append(node);
        }
    }

    /* ---------- load and apply ---------- */

    function announceChanges(servers) {
        const now = ctx.now();
        const changed = [];
        let finished = null;
        for (const s of servers) {
            const model = describeServer(s, ctx.fmt);
            const prev = st.prev.get(s.handle);
            if (prev && prev.key !== model.key) {
                changed.push(t('announceNowState', { server: s.displayName, state: model.chipText }));
            }

            if (prev && prev.syncing && !s.syncing && s.lastSync) {
                finished = finished || { added: 0, removed: 0 };
                finished.added += s.lastSync.added || 0;
                finished.removed += s.lastSync.removed || 0;
            }

            st.prev.set(s.handle, { key: model.key, syncing: !!s.syncing });
        }

        if (changed.length && now - st.lastAnnounce > 10000) {
            st.lastAnnounce = now;
            ctx.announce(changed.length === 1 ? changed[0] : t('announceManyChanged', { n: changed.length }));
        } else if (finished) {
            ctx.announce(t('announceSyncFinished', finished));
        }
    }

    function apply(data) {
        st.data = data;
        const servers = sortServers(data.servers || []);
        const friends = data.friends || [];
        ctx.applyStatus(data);
        const firstRun = servers.length === 0 && friends.length === 0;
        setBlock(firstRun ? 'empty' : 'content');
        renderOrphans(data);
        renderSandboxNotice(data);
        renderLinkedFriends(data);
        renderStatusLine(servers, friends);
        if (data.cooldowns && data.cooldowns.syncAllUntilUtc) {
            const secs = Math.round((toMs(data.cooldowns.syncAllUntilUtc) - ctx.now()) / 1000);
            if (secs > 0) {
                ctx.setCooldown('sync', secs);
            }
        }

        if (!syncBtn.classList.contains('is-busy')) {
            syncBtn.classList.toggle('is-syncing', servers.some((s) => s.syncing));
        }

        // With the keys locked, sync is replaced by Pair again on each card (ui-spec 8.8).
        syncBtn.hidden = !!data.secretsUnreadable;

        syncCooldown();
        if (firstRun) {
            return;
        }

        const remembered = ctx.storage.get(SELECTED_KEY);
        if (!st.selected || !servers.some((s) => s.handle === st.selected)) {
            st.selected = pickSelection(servers, remembered, ctx.fmt);
        }

        const hasServers = servers.length > 0;
        noServers.hidden = hasServers;
        cardsHost.hidden = !hasServers;
        serversNote.hidden = !hasServers;
        renderCards(servers);
        ensureSpeedPanel();
        speedHost.hidden = !hasServers;
        if (hasServers) {
            const changed = st.speed.setServersIfChanged ? false : false;
            st.speed.setServers(servers, st.selected);
            if (!changed) {
                st.polls += 1;
                if (st.polls % SPEED_REFRESH_EVERY === 0) {
                    st.speed.refresh(false);
                }
            }

            if (st.overlapHandle !== st.selected) {
                loadOverlap();
            }
        } else {
            overlapSection.hidden = true;
        }

        renderPosters(data);
        renderFriends(data);
        if (ctx.params.focus === 'friends' && !st.focusedFriends) {
            st.focusedFriends = true;
            ctx.later(() => scrollToNode(friendsHost), 50);
        }

        announceChanges(servers);
        const credit = slot(view, 'credit');
        credit.hidden = !data.imageCredit;
        credit.textContent = data.imageCredit || '';
    }

    async function load(initial) {
        if (initial) {
            setBlock('loading');
        }

        try {
            const data = await ctx.api.status();
            if (!ctx.alive) {
                return;
            }

            st.loaded = true;
            apply(data);
        } catch {
            if (!ctx.alive) {
                return;
            }

            if (initial || !st.loaded) {
                setBlock('error');
            }
        }
    }

    return {
        show(restored) {
            if (!st.loaded) {
                load(true);
            } else {
                load(false);
            }

            poller.start();
        },
        hide() {
            poller.stop();
        },
        destroy() {
            poller.stop();
            if (st.speed) {
                st.speed.destroy();
            }
        },
        /** Exposed for tests and the development harness. */
        refresh: () => load(false)
    };
}

/* ====================================================================================== */
/* Screen: Libraries & access (ui-spec 8.4, DESIGN 5.6, owner D3/D4)                      */
/* ====================================================================================== */

function sortUsers(users) {
    return users.slice().sort((a, b) => String(a.name).localeCompare(String(b.name)));
}

/**
 * Who is admitted to a library right now (DESIGN 5.6): in Everyone mode every user except profiles with parental limits
 * (unless they are included); in Selected mode exactly the selection.
 */
function admittedUsers(mapping, users) {
    const a = mapping.access;
    if (a.mode === 'Everyone') {
        return users.filter((u) => !u.restricted || a.includeRestricted);
    }

    return users.filter((u) => a.selectedUserIds.includes(u.id));
}

function currentUserId(ctx, data) {
    const raw = ctx.client && typeof ctx.client.getCurrentUserId === 'function' ? ctx.client.getCurrentUserId() : data && data.currentUserId;
    // Jellyfin's client writes ids without dashes, the list of users may write them with: the picker compares them as the users are written (Pilot 07 D4).
    const plain = (x) => String(x || '').replace(/-/g, '').toLowerCase();
    const match = raw && data && Array.isArray(data.users) ? data.users.find((u) => plain(u.id) === plain(raw)) : null;
    return match ? match.id : raw;
}

/** Notes under a name in the picker, in the order of ui-spec 8.4.3. */
function userNotes(user, serverName, isYou) {
    const notes = [];
    if (isYou) {
        notes.push(t('noteYou'));
    }

    if (user.isAdmin) {
        notes.push(t('noteAdmin'));
    }

    if (user.isDisabled) {
        notes.push(t('noteDisabled'));
    }

    if (user.allLibraries) {
        notes.push(t('noteAllLibraries'));
    }

    if (user.restricted) {
        notes.push(t('noteRestricted', { server: serverName }));
    }

    if (user.managedByShoal) {
        notes.push(t('noteManaged'));
    }

    return notes;
}

function avatar(ctx, user) {
    const url = userImageUrl(ctx, user.id, user.imageTag);
    const fallback = el('span', { class: 'shoal-avatar__initial', 'aria-hidden': 'true', text: initialOf(user.name) });
    const root = el('span', { class: 'shoal-avatar' }, fallback);
    if (url) {
        const img = el('img', { alt: '', width: '32', height: '32', loading: 'lazy', decoding: 'async', hidden: true });
        img.addEventListener('load', () => {
            img.hidden = false;
            fallback.hidden = true;
        });
        img.addEventListener('error', () => {
            img.hidden = true;
        });
        img.src = url;
        root.append(img);
    }

    return root;
}

/** Names of the selected users, the current admin as "You" (ui-spec 7: "{n} people: You, Sam and 1 more"). */
function selectionNames(access, users, youId) {
    const picked = sortUsers(users.filter((u) => access.selectedUserIds.includes(u.id)));
    const you = picked.filter((u) => u.id === youId);
    return you.map(() => t('youWord')).concat(picked.filter((u) => u.id !== youId).map((u) => u.name));
}

/** "Who can see this" summary line (ui-spec 7, 8.4.1): icon plus text. */
function accessSummary(access, users, youId, everyoneKey) {
    if (access.mode === 'Everyone') {
        return el('span', { class: 'shoal-accessline shoal-access--everyone' }, icon('eye', true), el('span', { text: t(everyoneKey || 'accessEveryoneShort') }));
    }

    const names = selectionNames(access, users, youId);
    if (names.length === 0) {
        return el('span', { class: 'shoal-accessline shoal-tone--warn' }, icon('eyeoff', true), el('span', { text: t('accessHidden') }));
    }

    if (names.length === 1 && names[0] === t('youWord')) {
        return el('span', { class: 'shoal-accessline' }, icon('eye', true), el('span', { text: t('accessOnlyYou') }));
    }

    return el('span', { class: 'shoal-accessline' }, icon('eye', true), el('span', { text: t('accessPeople', { n: names.length, names: nameList(names, 2) }) }));
}

/** First five names as text, "and k more" after them, the whole list behind a disclosure (ui-spec 8.4.4). */
function namesBlock(names) {
    const shown = names.slice(0, 5);
    const rest = names.length - shown.length;
    const text = rest > 0 ? t('namesAndMore', { names: shown.join(', '), n: rest }) : nameList(shown, 5);
    const nodes = [text];
    return { text, rest, list: rest > 0 ? collapse(t('showFullList'), el('ul', { class: 'shoal-bullets' }, names.map((n) => el('li', { text: n })))) : null, nodes };
}

/**
 * The picker dialog (ui-spec 8.4.3). Editable in Selected mode and in the wizard, read-only in Everyone mode.
 * opts: { readOnly, youId, title }. Resolves { selectedUserIds, ratingsAcknowledged } or undefined.
 */
function openPicker(ctx, server, mapping, users, opts) {
    const o = opts || {};
    const readOnly = !!o.readOnly || mapping.access.mode === 'Everyone';
    const youId = o.youId;
    const selected = new Set(mapping.access.selectedUserIds);
    const list = sortUsers(users);
    const anyRestricted = list.some((u) => u.restricted);
    const rows = new Map();
    let ack = null;
    let search = null;
    let everyone = null;
    let countNode = null;
    let warnNode = null;
    let dlgRef = null;
    let countTimer = null;

    const restrictedPicked = () => list.some((u) => u.restricted && selected.has(u.id));
    const selectable = () => list.filter((u) => !u.restricted || (ack && ack.input.checked));

    function syncChecks(immediate) {
        const pickable = selectable();
        const n = pickable.filter((u) => selected.has(u.id)).length;
        if (everyone) {
            everyone.input.checked = pickable.length > 0 && n === pickable.length;
            everyone.input.indeterminate = n > 0 && n < pickable.length;
        }

        for (const [id, row] of rows) {
            row.input.checked = selected.has(id);
            const u = list.find((x) => x.id === id);
            row.input.disabled = readOnly || (u.restricted && !(ack && ack.input.checked));
            row.root.classList.toggle('is-disabled', row.input.disabled && !readOnly);
        }

        if (dlgRef && dlgRef.buttons.save) {
            dlgRef.buttons.save.disabled = restrictedPicked() && !(ack && ack.input.checked);
        }

        if (warnNode) {
            warnNode.hidden = !(youId && !selected.has(youId));
        }

        const total = list.length;
        const picked = list.filter((u) => selected.has(u.id)).length;
        ctx.clearTimer(countTimer);
        const write = () => {
            countNode.textContent = t('pickerCount', { n: picked, total });
        };
        if (immediate) {
            write();
        } else {
            countTimer = ctx.later(write, 500);
        }
    }

    return openDialog(ctx, {
        title: readOnly ? t('pickerReadOnlyTitle', { library: mapping.localLibraryName }) : t('pickerTitle', { library: mapping.localLibraryName }),
        wide: true,
        build(body) {
            body.append(paragraph(readOnly ? t('pickerReadOnly') : t('pickerBody'), 'shoal-meta'));
            if (list.length > 8) {
                search = textField({ id: uid('shoal-psearch'), label: t('pickerSearch'), type: 'search' });
                search.input.addEventListener('input', () => {
                    const needle = search.input.value.trim().toLocaleLowerCase();
                    for (const u of list) {
                        rows.get(u.id).root.parentElement.hidden = needle !== '' && !String(u.name).toLocaleLowerCase().includes(needle);
                    }
                });
                body.append(search.root);
            }

            if (!readOnly) {
                everyone = checkRow({
                    id: uid('shoal-pall'),
                    label: t('pickerEveryone'),
                    onChange: (checked) => {
                        selectable().forEach((u) => (checked ? selected.add(u.id) : selected.delete(u.id)));
                        syncChecks(true);
                    }
                });
                everyone.root.classList.add('shoal-picker__all');
                body.append(everyone.root);
                if (anyRestricted) {
                    ack = checkRow({ id: uid('shoal-pack'), label: t('pickerAck', { server: server.displayName }), checked: restrictedPicked(), onChange: () => syncChecks(true) });
                    body.append(el('div', { class: 'shoal-picker__ack' }, ack.root));
                }
            }

            const ul = el('ul', { class: 'shoal-picker' });
            for (const u of list) {
                const notes = userNotes(u, server.displayName, u.id === youId);
                const row = checkRow({
                    id: uid('shoal-pu'),
                    label: u.name,
                    note: notes.join(' · '),
                    disabled: readOnly,
                    checked: selected.has(u.id),
                    onChange: (checked) => {
                        if (checked) {
                            selected.add(u.id);
                        } else {
                            selected.delete(u.id);
                        }

                        syncChecks(false);
                    }
                });
                row.root.classList.add('shoal-picker__row');
                row.root.prepend(avatar(ctx, u));
                rows.set(u.id, row);
                ul.append(el('li', {}, row.root));
            }

            body.append(ul);
            countNode = el('p', { class: 'shoal-meta shoal-picker__count', role: 'status', 'aria-live': 'polite' });
            warnNode = el('p', { class: 'shoal-meta shoal-tone--warn shoal-picker__warn', hidden: true }, icon('warn', true), t('pickerYouWarn'));
            body.append(warnNode, countNode);
            syncChecks(true);
            return search ? search.input : everyone ? everyone.input : null;
        },
        onOpen(dlg) {
            dlgRef = dlg;
            syncChecks(true);
        },
        buttons: readOnly
            ? [{ label: t('close'), close: undefined }]
            : [
                { label: t('cancel'), close: undefined },
                {
                    label: t('save'),
                    variant: 'primary',
                    key: 'save',
                    onClick: (dlg) => {
                        dlg.close({ selectedUserIds: list.filter((u) => selected.has(u.id)).map((u) => u.id), ratingsAcknowledged: !!(ack && ack.input.checked) });
                    }
                }
            ]
    });
}

function librariesScreen(ctx) {
    const view = q(ctx.root, '[data-shoal-view="libraries"]');
    const body = slot(view, 'body');
    const st = { data: null, shares: null, servers: [], loaded: false, focusKey: null, busy: new Set(), errors: new Map() };

    function youId() {
        return currentUserId(ctx, st.data);
    }

    function serverOf(mapping) {
        return st.servers.find((s) => s.handle === mapping.serverHandle) || { handle: mapping.serverHandle, displayName: mapping.serverName || '', status: 'Online' };
    }

    /** Confirm dialogs list who gains or loses access (ui-spec 8.4.4); nobody is changed until the admin confirms. */
    async function confirmModeChange(server, mapping, toMode) {
        const users = st.data.users;
        const a = mapping.access;
        const library = mapping.localLibraryName;
        const admittedNow = new Set(admittedUsers(mapping, users).map((u) => u.id));
        if (toMode === 'Everyone') {
            const nextAdmitted = users.filter((u) => !u.restricted || a.includeRestricted);
            const gaining = nextAdmitted.filter((u) => !admittedNow.has(u.id)).map((u) => u.name).sort((x, y) => String(x).localeCompare(String(y)));
            const staysOut = users.filter((u) => u.restricted && !a.includeRestricted).length;
            const block = namesBlock(gaining);
            return confirmDialog(ctx, {
                title: t('accessConfirmEveryoneTitle', { library }),
                intro: gaining.length ? t('accessConfirmEveryoneBody', { n: gaining.length, names: block.text }) : t('accessConfirmEveryoneNone'),
                after: [block.list, staysOut > 0 ? paragraph(t('accessConfirmRestrictedStay', { r: staysOut, n: staysOut }), 'shoal-meta') : null],
                confirmLabel: t('openToEveryone')
            });
        }

        const keep = new Set(a.selectedUserIds);
        const losing = Array.from(admittedNow).filter((id) => !keep.has(id)).map((id) => (users.find((u) => u.id === id) || {}).name).filter(Boolean).sort((x, y) => String(x).localeCompare(String(y)));
        const savedCount = users.filter((u) => keep.has(u.id)).length;
        const block = namesBlock(losing);
        let intro = '';
        if (losing.length) {
            intro = t('accessConfirmSelectedLose', { n: losing.length, names: block.text });
            intro += ' ' + (savedCount ? t('accessConfirmSelectedKeep', { m: savedCount, n: savedCount }) : t('accessConfirmSelectedEmpty'));
        } else {
            intro = savedCount ? t('accessConfirmSelectedKeepOnly', { m: savedCount, n: savedCount }) : t('accessConfirmSelectedEmpty');
        }

        return confirmDialog(ctx, {
            title: t('accessConfirmSelectedTitle', { library }),
            intro,
            after: [block.list, losing.length ? paragraph(t('accessConfirmCantPlay'), 'shoal-meta') : null],
            confirmLabel: t('limitAccess')
        });
    }

    /** PUT the access, then redraw from the answer. The switch keeps showing the saved mode until it succeeds. */
    async function putAccess(mapping, patch, announceKey) {
        const next = { ...mapping.access, ...patch };
        st.busy.add(mapping.mappingKey);
        st.errors.delete(mapping.mappingKey);
        render();
        try {
            const result = await ctx.api.setAccess(mapping.mappingKey, {
                mode: next.mode,
                selectedUserIds: next.selectedUserIds,
                includeRestricted: next.includeRestricted,
                ratingsAcknowledged: !!patch.ratingsAcknowledged
            });
            mapping.access = result && result.access ? result.access : next;
            st.busy.delete(mapping.mappingKey);
            ctx.announce(t(announceKey || 'announceAccessSaved', { library: mapping.localLibraryName, m: selectedCount(mapping), n: selectedCount(mapping) }));
            render();
            return true;
        } catch {
            st.busy.delete(mapping.mappingKey);
            st.errors.set(mapping.mappingKey, true);
            ctx.alertMessage(t('accessFailed', { library: mapping.localLibraryName }));
            render();
            return false;
        }
    }

    function selectedCount(mapping) {
        return st.data.users.filter((u) => mapping.access.selectedUserIds.includes(u.id)).length;
    }

    function switchFor(server, mapping) {
        const busy = st.busy.has(mapping.mappingKey);
        const node = accessSwitch({
            mode: mapping.access.mode,
            library: mapping.localLibraryName,
            fkey: mapping.mappingKey + ':mode',
            busy,
            onRequest: async (value) => {
                st.focusKey = mapping.mappingKey + ':mode:' + value;
                const res = await confirmModeChange(server, mapping, value);
                if (!res.confirmed) {
                    render();
                    return;
                }

                await putAccess(mapping, { mode: value }, value === 'Everyone' ? 'announceEveryone' : 'announceLimited');
            }
        });
        if (busy) {
            for (const b of qa(node, '.shoal-seg button')) {
                b.setAttribute('aria-disabled', 'true');
            }

            const pressed = q(node, '.shoal-seg button[aria-pressed="false"]');
            if (pressed) {
                pressed.prepend(icon('sync', true));
                pressed.querySelector('.shoal-icon').classList.add('is-busy-icon');
            }
        }

        return node;
    }

    function driftBlock(mapping) {
        const drift = mapping.drift || [];
        if (!drift.length) {
            return null;
        }

        const users = st.data.users;
        const byId = new Map(users.map((u) => [u.id, u]));
        const rowsNodes = drift.map((d) => {
            const u = byId.get(d.userId);
            const name = u ? u.name : t('unknownUser');
            let action = null;
            if (u && d.kind === 'extraAccess') {
                action = button({ label: t('driftSelect', { name }), small: true, onClick: () => putAccess(mapping, { selectedUserIds: mapping.access.selectedUserIds.concat([u.id]) }) });
            } else if (u && d.kind === 'missingAccess') {
                action = button({ label: t('driftRemove', { name }), small: true, onClick: () => putAccess(mapping, { selectedUserIds: mapping.access.selectedUserIds.filter((id) => id !== u.id) }) });
            }

            return el('li', { class: 'shoal-drift__row' },
                el('span', { class: 'shoal-drift__text' }, el('strong', { text: name }), ' ' + t('drift_' + d.kind, { library: mapping.localLibraryName })),
                action);
        });
        const reapply = button({
            label: t('reapplyNow'),
            small: true,
            onClick: (ev, btn) => runAction(ctx, btn, {
                busyText: t('reapplying'),
                run: () => ctx.api.reconcile(mapping.mappingKey),
                onDone: async () => {
                    ctx.announce(t('announceReapplied', { library: mapping.localLibraryName }));
                    await load(false);
                }
            })
        });
        const det = collapse('', [el('p', { class: 'shoal-meta', text: t('driftIntro') }), el('ul', { class: 'shoal-drift' }, rowsNodes), reapply]);
        const summary = det.querySelector('summary > span');
        summary.replaceWith(el('span', { class: 'shoal-drift__summary' }, icon('warn', true), t('driftSummary', { n: drift.length })));
        det.classList.add('shoal-drift__details');
        return det;
    }

    function libraryBlock(server, mapping) {
        const a = mapping.access;
        const everyone = a.mode === 'Everyone';
        const users = st.data.users;
        const titleId = uid('shoal-lib');
        const holds = [];
        const held = (n, key) => el('span', { class: 'shoal-tone--warn shoal-holdline' }, icon('warn', true), ctx.linkEl('server', { id: server.handle, focus: 'alerts' }, 'shoal-link', t(key, { n, count: ctx.fmt.num(n) })));
        if (mapping.heldRemovals > 0) {
            holds.push(held(mapping.heldRemovals, 'removalsOnHold'));
        }

        if (mapping.heldAdditions > 0) {
            holds.push(held(mapping.heldAdditions, 'additionsOnHold'));
        }

        const main = el('div', { class: 'shoal-libblock__main' },
            el('span', { class: 'shoal-row__title', id: titleId, text: mapping.localLibraryName }),
            el('span', { class: 'shoal-row__meta shoal-meta', text: countsDiffer(mapping)
                ? t('libraryMetaSplit', { remote: ctx.fmt.num(mapping.remoteCount), local: ctx.fmt.num(mapping.localCount), when: mapping.lastSync ? ctx.fmt.relative(mapping.lastSync.atUtc) : t('never') })
                : t('libraryMeta', { count: ctx.fmt.num(mapping.count), n: mapping.count, when: mapping.lastSync ? ctx.fmt.relative(mapping.lastSync.atUtc) : t('never') }) }),
            holds.length ? el('span', { class: 'shoal-row__holds' }, holds) : null);

        const savedCount = users.filter((u) => a.selectedUserIds.includes(u.id)).length;
        const openPick = async () => {
            st.focusKey = mapping.mappingKey + ':pick';
            const res = await openPicker(ctx, server, mapping, users, { youId: youId() });
            if (res && res.selectedUserIds) {
                await putAccess(mapping, { selectedUserIds: res.selectedUserIds, ratingsAcknowledged: res.ratingsAcknowledged });
            }
        };
        // "See saved selection" is a link (8.4.1); "Choose people" is the small quiet button.
        const pick = everyone ? linkButton(t('seeRemembered'), openPick) : button({ label: t('choosePeople'), small: true, onClick: openPick });
        pick.dataset.fkey = mapping.mappingKey + ':pick';
        const summaryRow = el('div', { class: 'shoal-libblock__summary' },
            accessSummary(a, users, youId()),
            pick);
        const access = el('div', { class: 'shoal-libblock__access' }, switchFor(server, mapping), summaryRow);
        if (everyone) {
            access.insertBefore(el('p', { class: 'shoal-meta', text: t('savedSelectionNote', { n: savedCount }) }), summaryRow.nextSibling);
            const include = checkRow({
                id: uid('shoal-incl'),
                label: t('includeRestricted'),
                note: t('includeRestrictedNote', { server: server.displayName }),
                checked: !!a.includeRestricted,
                onChange: async (checked, input) => {
                    st.focusKey = mapping.mappingKey + ':restricted';
                    if (checked) {
                        const res = await confirmDialog(ctx, {
                            title: t('ratingsTitle'),
                            intro: t('ratingsBody', { server: server.displayName, library: mapping.localLibraryName }),
                            checkbox: { label: t('ratingsAckBox', { server: server.displayName }), checked: false, required: true },
                            confirmLabel: t('includeThem'),
                            requireChecked: true
                        });
                        if (!res.confirmed) {
                            input.checked = false;
                            return;
                        }

                        await putAccess(mapping, { includeRestricted: true, ratingsAcknowledged: true }, 'announceRestrictedIncluded');
                    } else {
                        await putAccess(mapping, { includeRestricted: false });
                    }
                }
            });
            include.input.dataset.fkey = mapping.mappingKey + ':restricted';
            access.append(include.root);
        }

        if (st.errors.get(mapping.mappingKey)) {
            access.append(el('p', { class: 'shoal-fieldError', role: 'alert' }, icon('x', true), el('span', { class: 'shoal-fieldError__text', text: t('accessFailed', { library: mapping.localLibraryName }) })));
        }

        const drift = driftBlock(mapping);
        return el('article', { class: 'shoal-libblock', id: 'lib-' + mapping.mappingKey, 'aria-labelledby': titleId }, main, access, drift ? el('div', { class: 'shoal-libblock__drift' }, drift) : null);
    }

    function shareRow(f) {
        const model = describeFriend(f);
        const names = f.libraryNames || [];
        const limit = f.maxBitrateMbps ? t('limitMbps', { n: f.maxBitrateMbps }) : t('limitNone');
        const edit = button({ label: t('edit'), small: true, onClick: () => ctx.navigate('friend', { id: f.handle, focus: 'libraries' }) });
        const aside = el('div', { class: 'shoal-row__controls shoal-row__controls--inline' }, chip(model.tone, model.icon, model.text), edit);
        const main = el('div', { class: 'shoal-row__main' },
            el('span', { class: 'shoal-row__title', text: f.friendName }),
            el('span', { class: 'shoal-row__meta shoal-meta', text: t('shareMeta', { k: t('nLibraries', { n: names.length }), names: names.join(', '), limit, tc: t('transcodesAtOnce', { n: f.maxConcurrentTranscodes || 2 }) }) }));
        if ((f.unexpectedDeviceCount || 0) > 0) {
            main.append(el('span', { class: 'shoal-row__meta shoal-meta shoal-tone--warn shoal-row__warn' }, icon('warn', true), t('unexpectedDevices', { n: f.unexpectedDeviceCount })));
        }

        return el('li', { class: 'shoal-row shoal-row--share' }, main, aside);
    }

    function render() {
        const data = st.data;
        clear(body);
        const incoming = section(t('sharedWithYou'), { intro: t('sharedWithYouIntro') });
        const mappings = data.mappings || [];
        if (!mappings.length) {
            incoming.body.append(el('div', { class: 'shoal-emptybox' },
                el('p', { class: 'shoal-lead', text: t('noLibrariesYet') }),
                button({ label: t('pairServer'), variant: 'primary', icon: 'plus', onClick: () => ctx.navigate('pair', { dir: 'watch' }) })));
        }

        const groups = new Map();
        for (const m of mappings) {
            if (!groups.has(m.serverHandle)) {
                groups.set(m.serverHandle, []);
            }

            groups.get(m.serverHandle).push(m);
        }

        const ordered = Array.from(groups.entries()).sort((x, y) => String(serverOf(x[1][0]).displayName).localeCompare(String(serverOf(y[1][0]).displayName)));
        for (const [, list] of ordered) {
            const server = serverOf(list[0]);
            const model = describeServer(server, ctx.fmt);
            incoming.body.append(el('div', { class: 'shoal-group' },
                el('div', { class: 'shoal-group__head' }, el('h3', { class: 'shoal-h3', text: server.displayName }), chip(model.tone, model.icon, model.chipText)),
                el('div', { class: 'shoal-libblocks' }, list.map((m) => libraryBlock(server, m)))));
        }

        body.append(incoming.root);

        const outgoing = section(t('youShare'), { intro: t('youShareIntro') });
        const shares = (st.shares && st.shares.shares) || [];
        if (!shares.length) {
            outgoing.body.append(el('div', { class: 'shoal-emptybox' },
                el('p', { class: 'shoal-lead', text: t('noSharing') }),
                button({ label: t('shareWithFriend'), onClick: () => ctx.navigate('pair', { dir: 'share' }) })));
        } else {
            outgoing.body.append(el('ul', { class: 'shoal-list' }, shares.map(shareRow)));
        }

        body.append(outgoing.root);
        const target = st.focusKey && q(body, '[data-fkey="' + st.focusKey + '"]');
        if (target) {
            target.focus();
        }

        st.focusKey = null;
        if (!st.scrolled && ctx.params.focus && ctx.params.focus.startsWith('lib-')) {
            st.scrolled = true;
            const node = body.querySelector('#' + CSS.escape(ctx.params.focus));
            if (node) {
                ctx.later(() => scrollToNode(node), 50);
            }
        }
    }

    async function load(initial) {
        if (initial && !st.loaded) {
            replaceKids(body, el('div', { class: 'shoal-skel', 'aria-busy': 'true' }, el('span', { class: 'shoal-sr', text: t('loadingShoal') }), el('div', { class: 'shoal-skel__panel' }), el('div', { class: 'shoal-skel__panel shoal-skel__panel--short' })));
        }

        try {
            const [data, shares, status] = await Promise.all([ctx.api.libraries(), ctx.api.shares().catch(() => ({ shares: [] })), ctx.api.status().catch(() => null)]);
            if (!ctx.alive) {
                return;
            }

            // Sharing accounts Shoal made for friends are never listed or counted (ui-spec 8.4.3, DESIGN 5.6).
            st.data = Object.assign({}, data, { users: (data.users || []).filter((u) => !u.isShareUser) });
            st.shares = shares;
            st.servers = status ? status.servers || [] : st.servers;
            st.loaded = true;
            render();
        } catch {
            if (!st.loaded && ctx.alive) {
                replaceKids(body, loadErrorBox(() => load(true)));
            }
        }
    }

    return {
        show() {
            load(!st.loaded);
        },
        hide() {},
        destroy() {}
    };
}

/** The standard "couldn't load" box (ui-spec 8.1.5): never shows a server error body. */
function loadErrorBox(onRetry) {
    return el('div', { class: 'shoal-hint shoal-hint--bad shoal-loaderror', role: 'alert' },
        icon('warn', true),
        el('p', { class: 'shoal-loaderror__text', text: t('loadFailed') }),
        button({ label: t('tryAgain'), small: true, onClick: onRetry }));
}

/* ====================================================================================== */
/* Screen: Settings (ui-spec 8.5, DESIGN 9.3)                                             */
/* ====================================================================================== */

const NETWORK_LABELS = { thisServer: 'netThisServer', lan: 'netHome', tailnet: 'netTailscale', internet: 'netInternet' };
const GUARD_KIND_LABELS = { stream: 'kindStream', subtitle: 'kindSubtitle', attachment: 'kindAttachment', hls: 'kindHls', image: 'kindImage' };
const SYNC_HOURS = [1, 3, 6, 12, 24];
const SPEED_HOURS = [3, 6, 12, 24];
const KEY_SOURCE_WORDS = { env: 'keyWordEnv', envFile: 'keyWordEnvFile', dpapi: 'keyWordDpapi', file: 'keyWordFile' };

function numberField(o) {
    return textField({ ...o, type: 'number', inputmode: o.step ? 'decimal' : 'numeric', value: String(o.value), min: o.min, max: o.max, step: o.step || '1', cls: 'shoal-input--num' });
}

/**
 * A sentence with number boxes in it ("Hold removals when more than [25] titles or [10] % ..."). Each box keeps its own
 * accessible name; the fieldset's legend names the group.
 */
function sentenceField(o) {
    const errId = uid('shoal-err');
    const err = el('p', { class: 'shoal-fieldError', id: errId, hidden: true }, icon('x', true), el('span', { class: 'shoal-fieldError__text' }));
    const inputs = o.pieces.filter((p) => p.input).map((p) => p.input);
    const kids = o.pieces.map((p) => (p.input ? p.input : el('span', { class: 'shoal-sentence__text', text: p.text })));
    const root = el('fieldset', { class: 'shoal-sentence shoal-field' },
        el('legend', { class: 'shoal-label', text: o.legend }),
        el('div', { class: 'shoal-sentence__line' }, kids),
        o.help ? el('div', { class: 'fieldDescription shoal-help', text: o.help }) : null,
        err);
    return {
        root,
        inputs,
        setError(message, input) {
            err.hidden = !message;
            err.querySelector('.shoal-fieldError__text').textContent = message || '';
            for (const i of inputs) {
                if (message && (!input || i === input)) {
                    i.setAttribute('aria-invalid', 'true');
                } else {
                    i.removeAttribute('aria-invalid');
                }
            }
        }
    };
}

function numberBox(o) {
    return el('input', { class: 'emby-input shoal-input shoal-input--num', type: 'number', inputmode: 'numeric', min: o.min, max: o.max, step: '1', value: String(o.value), 'aria-label': o.label, id: uid('shoal-nb') });
}

function settingsScreen(ctx) {
    const view = q(ctx.root, '[data-shoal-view="settings"]');
    const body = slot(view, 'body');
    const versionHost = el('div', { class: 'shoal-version' });
    body.before(versionHost);
    fillVersion(ctx, versionHost);
    const st = { settings: null, hardening: null, state: null, hasFriends: false, loaded: false, dirty: false, networkRoot: null };
    const f = {};
    let saveBtn = null;
    let dirtyNote = null;
    let savedNote = null;
    let summary = null;
    let protectNode = null;

    function markDirty() {
        st.dirty = true;
        dirtyNote.hidden = false;
        savedNote.hidden = true;
    }

    function checkLine(check) {
        const map = {
            ok: ['ok', 'ok', 'stubOk'],
            network: ['bad', 'x', 'stubNetwork'],
            insideLibrary: ['bad', 'x', 'stubInside'],
            notWritable: ['bad', 'x', 'stubNotWritable']
        };
        const [tone, ic, key] = map[check] || map.ok;
        return el('p', { class: 'shoal-checkline shoal-tone--' + tone }, icon(ic, true), el('span', { text: t(key) }));
    }

    function settingsPanel(titleText, ...content) {
        const p = panel(titleText, { cls: 'shoal-panel--form' });
        appendKids(p.body, content);
        return p.root;
    }

    function renderTrickCache(bytes) {
        clear(f.trickUsed);
        if (typeof bytes !== 'number') {
            return;
        }

        const size = bytes >= 1e9 ? ctx.fmt.num(Math.round(bytes / 1e8) / 10) + ' GB' : ctx.fmt.num(Math.round(bytes / 1e5) / 10) + ' MB';
        const clearBtn = button({ label: t('trickplayClear'), small: true, onClick: (ev, btn) => runAction(ctx, btn, {
            busyText: t('applying'),
            run: () => ctx.api.clearTrickplayCache(),
            onDone: () => {
                st.settings = Object.assign({}, st.settings, { trickplayCacheBytes: 0 });
                renderTrickCache(0);
                ctx.announce(t('announceTrickplayCleared'));
            },
            onError: (err) => ctx.alertMessage(failureMessage(err))
        }) });
        clearBtn.setAttribute('aria-label', t('trickplayClearLabel'));
        appendKids(f.trickUsed, [el('p', { class: 'shoal-meta', text: t('trickplayUsed', { size }) }), bytes > 0 ? clearBtn : null]);
    }

    // The state of the picture check: running (full or limited), off, or not available and why.
    function renderSandbox(info) {
        clear(f.sandbox);
        if (!info) {
            return;
        }

        if (info.state === 'unavailable') {
            const reason = info.reason === 'ffmpegMissing' || info.reason === 'selfTestFailed' ? info.reason : 'other';
            f.sandbox.append(hint('warn', 'warn', el('strong', { text: t('sandboxAlertTitle') }), el('span', { class: 'shoal-hint__line', text: t('sandboxUnavailable_' + reason) })));
        } else if (info.state === 'off') {
            f.sandbox.append(el('p', { class: 'shoal-checkline shoal-tone--off' }, icon('offline', true), el('span', { text: t('sandboxOff') })));
        } else {
            const full = info.limits !== 'partial';
            f.sandbox.append(el('p', { class: 'shoal-checkline shoal-tone--' + (full ? 'ok' : 'warn') }, icon(full ? 'ok' : 'warn', true), el('span', { text: t(full ? 'sandboxActiveFull' : 'sandboxActivePartial') })));
        }

        if (Number(info.rejected) > 0) {
            f.sandbox.append(el('p', { class: 'shoal-meta', text: t('sandboxRejected', { n: Number(info.rejected) }) }));
        }
    }

    function buildForm(d) {
        const form = el('form', { class: 'shoal-form', novalidate: true });
        form.addEventListener('submit', (ev) => {
            ev.preventDefault();
            save();
        });
        form.addEventListener('input', markDirty);
        form.addEventListener('change', markDirty);
        summary = el('div', { class: 'shoal-hint shoal-hint--bad shoal-formsummary', role: 'alert', hidden: true });
        form.append(summary);

        // 1. Library files
        f.stubRoot = textField({ id: uid('shoal-f'), label: t('fStubRoot'), value: d.stubRoot, mono: true, spellcheck: false, required: true });
        f.stubAck = checkRow({ id: uid('shoal-f'), label: t('fStubAck'), checked: !!d.stubRootNetworkAck });
        f.stubAck.root.hidden = d.stubRootCheck !== 'network';
        form.append(settingsPanel(t('sLibraryFiles'), f.stubRoot.root, el('div', { class: 'shoal-checkhost' }, checkLine(d.stubRootCheck)), f.stubAck.root));

        // 2. Syncing
        f.syncInterval = selectField({ id: uid('shoal-f'), label: t('fSyncEvery'), options: SYNC_HOURS.map((h) => ({ value: String(h), label: t('everyHours', { n: h }) })), value: String(d.syncIntervalHours) });
        f.massMin = numberBox({ label: t('fieldTitles'), value: d.massDeletionMinCount, min: 1, max: 100000 });
        f.massPct = numberBox({ label: t('fieldPercent'), value: d.massDeletionPercent, min: 1, max: 100 });
        f.mass = sentenceField({ legend: t('fMassLegend'), help: t('fMassHelp'), pieces: [{ text: t('massA') }, { input: f.massMin }, { text: t('massB') }, { input: f.massPct }, { text: t('massC') }] });
        f.addMin = numberBox({ label: t('fieldTitles'), value: d.newItemsMinCount, min: 1, max: 100000 });
        f.addPct = numberBox({ label: t('fieldPercent'), value: d.newItemsPercent, min: 1, max: 100 });
        f.add = sentenceField({ legend: t('fAddLegend'), help: t('fAddHelp'), pieces: [{ text: t('addA') }, { input: f.addMin }, { text: t('addB') }, { input: f.addPct }, { text: t('addC') }] });
        f.quota = numberField({ id: uid('shoal-f'), label: t('fQuota'), value: d.diskQuotaGbPerPeer, min: 1, max: 50, help: t('fQuotaHelp') });
        f.art = selectField({ id: uid('shoal-f'), label: t('fArt'), options: [{ value: 'Standard', label: t('artStandard') }, { value: 'High', label: t('artHigh') }], value: d.artQuality });
        form.append(settingsPanel(t('sSyncing'), f.syncInterval.root, f.mass.root, f.add.root, f.quota.root, f.art.root));

        // 3. Playback
        f.autoFit = checkRow({ id: uid('shoal-f'), label: t('fAutoFit'), note: t('fAutoFitNote'), checked: d.autoFitQualityToLink });
        f.maxStreams = numberField({ id: uid('shoal-f'), label: t('fMaxStreams'), help: t('fMaxStreamsHelp'), value: d.maxStreamsPerUser, min: 1, max: 4 });
        f.bufferAhead = numberField({ id: uid('shoal-f'), label: t('fBufferAhead'), help: t('fBufferAheadHelp'), value: d.hlsBufferAhead ?? 6, min: 2, max: 20 });
        f.hlsSubs = checkRow({ id: uid('shoal-f'), label: t('fHlsSubs'), note: t('fHlsSubsNote'), checked: d.hlsSubtitlesEnabled });
        form.append(settingsPanel(t('sPlayback'), f.autoFit.root, f.maxStreams.root, f.bufferAhead.root, f.hlsSubs.root, ctx.features && ctx.features.appsWork ? appsWorkBlock(ctx, '') : null));

        // 3b. Extra features (alternate versions, preview pictures, fonts, picture check)
        f.altVersions = checkRow({ id: uid('shoal-f'), label: t('fAltVersions'), note: t('fAltVersionsNote'), checked: !!d.alternateVersionsEnabled });
        f.trickplay = checkRow({ id: uid('shoal-f'), label: t('fTrickplay'), note: t('fTrickplayNote'), checked: d.trickplayEnabled !== false });
        f.trickGb = numberField({ id: uid('shoal-f'), label: t('fTrickplayGb'), help: t('fTrickplayGbHelp'), value: d.trickplayCacheGb ?? 1, min: 1, max: 20 });
        f.trickGb.input.disabled = d.trickplayEnabled === false;
        f.trickplay.input.addEventListener('change', () => {
            f.trickGb.input.disabled = !f.trickplay.input.checked;
        });
        f.trickUsed = el('div', { class: 'shoal-trickcache' });
        renderTrickCache(d.trickplayCacheBytes);
        f.attachments = checkRow({ id: uid('shoal-f'), label: t('fAttachments'), note: t('fAttachmentsNote'), checked: d.attachmentsEnabled !== false });
        f.tvShows = checkRow({ id: uid('shoal-f'), label: t('fTvShows'), note: t('fTvShowsNote'), checked: !!d.tvShowsEnabled });
        f.tvThumbs = checkRow({ id: uid('shoal-f'), label: t('fTvThumbs'), note: t('fTvThumbsNote'), checked: d.tvEpisodeThumbs !== false, disabled: !d.tvShowsEnabled });
        f.tvShows.input.addEventListener('change', () => {
            f.tvThumbs.input.disabled = !f.tvShows.input.checked;
        });
        f.reencode = checkRow({ id: uid('shoal-f'), label: t('fReencode'), note: t('fReencodeNote'), checked: d.reencodeImages !== false });
        f.sandbox = el('div', { class: 'shoal-sandbox' });
        renderSandbox(d.imageSandbox);
        form.append(settingsPanel(t('sFeatures'), f.altVersions.root, f.tvShows.root, f.tvThumbs.root, f.trickplay.root, f.trickGb.root, f.trickUsed, f.attachments.root, f.reencode.root, f.sandbox));

        // 4. Speed tests
        f.speedEvery = selectField({ id: uid('shoal-f'), label: t('fSpeedEvery'), options: SPEED_HOURS.map((h) => ({ value: String(h), label: t('everyHours', { n: h }) })), value: String(d.speedTestIntervalHours) });
        form.append(settingsPanel(t('sSpeedTests'), f.speedEvery.root));

        // 5. Offline servers
        f.notify = checkRow({ id: uid('shoal-f'), label: t('fNotify'), note: t('fNotifyNote'), checked: d.notifyClientsWhenOffline });
        f.park = checkRow({ id: uid('shoal-f'), label: t('fPark'), checked: d.parkOfflinePeers });
        f.parkHours = numberField({ id: uid('shoal-f'), label: t('fParkHours'), help: t('fParkHoursHelp'), value: d.parkAfterHours, min: 0.1, max: 8760, step: '0.1' });
        f.parkHours.input.disabled = !d.parkOfflinePeers;
        f.park.input.addEventListener('change', () => {
            f.parkHours.input.disabled = !f.park.input.checked;
        });
        f.offlineCard = checkRow({ id: uid('shoal-f'), label: t('fOfflineCard'), note: t('fOfflineCardNote'), checked: d.offlineCardEnabled !== false });
        f.reqName = textField({ id: uid('shoal-f'), label: t('fRequestName'), help: t('fRequestNameHelp'), value: d.requestServiceName ?? 'Seerr', maxlength: 40 });
        f.reqUrl = textField({ id: uid('shoal-f'), label: t('fRequestUrl'), help: t('fRequestUrlHelp'), value: d.requestServiceUrl || '', type: 'url', inputmode: 'url', autocapitalize: 'off', spellcheck: false });
        form.append(settingsPanel(t('sOffline'), f.notify.root, f.park.root, f.parkHours.root, hint('warn', 'warn', t('fParkWarn')), f.offlineCard.root, f.reqName.root, f.reqUrl.root));

        // 6. Tailscale
        const ts = d.tailscaleSocket === 'auto' || d.tailscaleSocket === 'off' ? d.tailscaleSocket : 'custom';
        f.tsMode = selectField({
            id: uid('shoal-f'),
            label: t('fTailscale'),
            options: [{ value: 'auto', label: t('tsAuto') }, { value: 'off', label: t('tsOff') }, { value: 'custom', label: t('tsCustom') }],
            value: ts
        });
        f.tsPath = textField({ id: uid('shoal-f'), label: t('fTsPath'), value: ts === 'custom' ? d.tailscaleSocket : '', mono: true, spellcheck: false });
        f.tsPath.root.hidden = ts !== 'custom';
        f.tsMode.select.addEventListener('change', () => {
            f.tsPath.root.hidden = f.tsMode.select.value !== 'custom';
        });
        form.append(settingsPanel(t('sTailscale'), f.tsMode.root, f.tsPath.root,
            el('p', { class: 'shoal-checkline shoal-tone--' + (d.tailscaleDetected ? 'ok' : 'off') }, icon(d.tailscaleDetected ? 'ok' : 'offline', true), el('span', { text: t(d.tailscaleDetected ? 'tsFound' : 'tsMissing') }))));

        // save bar
        saveBtn = button({ label: t('save'), variant: 'primary', type: 'submit' });
        dirtyNote = el('span', { class: 'shoal-meta shoal-tone--warn shoal-savebar__dirty', hidden: true }, icon('warn', true), t('unsavedChanges'));
        savedNote = el('span', { class: 'shoal-meta shoal-tone--ok shoal-savebar__saved', role: 'status', hidden: true }, icon('ok', true), t('settingsSaved'));
        form.append(el('div', { class: 'shoal-savebar' }, saveBtn, dirtyNote, savedNote));
        return form;
    }

    function collect() {
        const tsMode = f.tsMode.select.value;
        return {
            stubRoot: f.stubRoot.value(),
            stubRootNetworkAck: f.stubAck.input.checked,
            syncIntervalHours: Number(f.syncInterval.select.value),
            massDeletionMinCount: Number(f.massMin.value),
            massDeletionPercent: Number(f.massPct.value),
            newItemsMinCount: Number(f.addMin.value),
            newItemsPercent: Number(f.addPct.value),
            diskQuotaGbPerPeer: Number(f.quota.input.value),
            artQuality: f.art.select.value,
            autoFitQualityToLink: f.autoFit.input.checked,
            maxStreamsPerUser: Number(f.maxStreams.input.value),
            hlsBufferAhead: Number(f.bufferAhead.input.value),
            hlsSubtitlesEnabled: f.hlsSubs.input.checked,
            speedTestIntervalHours: Number(f.speedEvery.select.value),
            notifyClientsWhenOffline: f.notify.input.checked,
            parkOfflinePeers: f.park.input.checked,
            parkAfterHours: Number(f.parkHours.input.value),
            tailscaleSocket: tsMode === 'custom' ? f.tsPath.value() : tsMode,
            alternateVersionsEnabled: f.altVersions.input.checked,
            tvShowsEnabled: f.tvShows.input.checked,
            tvEpisodeThumbs: f.tvThumbs.input.checked,
            trickplayEnabled: f.trickplay.input.checked,
            trickplayCacheGb: Number(f.trickGb.input.value),
            attachmentsEnabled: f.attachments.input.checked,
            reencodeImages: f.reencode.input.checked,
            offlineCardEnabled: f.offlineCard.input.checked,
            requestServiceName: f.reqName.value(),
            requestServiceUrl: f.reqUrl.value()
        };
    }

    function validate(v) {
        const problems = [];
        const whole = (value, min, max) => Number.isInteger(value) && value >= min && value <= max;
        const flag = (field, label, input, ok, key, min, max) => {
            field.setError(ok ? '' : t(key, { min, max }), input);
            if (!ok) {
                problems.push({ label, input: input || field.input });
            }
        };
        flag(f.stubRoot, t('fStubRoot'), f.stubRoot.input, !!v.stubRoot, 'errEnterFolder');
        if (((st.settings.stubRootCheck === 'network' && v.stubRoot === st.settings.stubRoot) || st.networkRoot === v.stubRoot) && !v.stubRootNetworkAck) {
            problems.push({ label: t('fStubAck'), input: f.stubAck.input });
        }

        // The two number boxes of one sentence are reported together (one message, the first bad box takes focus).
        const pair = (field, a, b, label, ranges) => {
            const bad = [[a, ranges[0]], [b, ranges[1]]].find(([input, r]) => !whole(Number(input.value), r[0], r[1]));
            field.setError(bad ? t('errWholeNumber', { min: bad[1][0], max: bad[1][1] }) : '', bad && bad[0]);
            if (bad) {
                problems.push({ label, input: bad[0] });
            }
        };
        pair(f.mass, f.massMin, f.massPct, t('fMassLegend'), [[1, 100000], [1, 100]]);
        pair(f.add, f.addMin, f.addPct, t('fAddLegend'), [[1, 100000], [1, 100]]);
        flag(f.quota, t('fQuota'), f.quota.input, whole(v.diskQuotaGbPerPeer, 1, 50), 'errWholeNumber', 1, 50);
        flag(f.maxStreams, t('fMaxStreams'), f.maxStreams.input, whole(v.maxStreamsPerUser, 1, 4), 'errWholeNumber', 1, 4);
        flag(f.bufferAhead, t('fBufferAhead'), f.bufferAhead.input, whole(v.hlsBufferAhead, 2, 20), 'errWholeNumber', 2, 20);
        flag(f.reqUrl, t('fRequestUrl'), f.reqUrl.input, v.requestServiceUrl === '' || /^https?:\/\/\S+$/i.test(v.requestServiceUrl), 'errRequestUrl');
        flag(f.parkHours, t('fParkHours'), f.parkHours.input, Number.isFinite(v.parkAfterHours) && v.parkAfterHours >= 0.1 && v.parkAfterHours <= 8760, 'errHoursNumber', 0.1, 8760);
        flag(f.trickGb, t('fTrickplayGb'), f.trickGb.input, whole(v.trickplayCacheGb, 1, 20), 'errWholeNumber', 1, 20);
        if (f.tsMode.select.value === 'custom') {
            flag(f.tsPath, t('fTsPath'), f.tsPath.input, !!v.tailscaleSocket, 'errEnterPath');
        }

        return problems;
    }

    function showSummary(problems) {
        summary.hidden = problems.length === 0;
        if (problems.length === 0) {
            return;
        }

        replaceKids(summary, icon('warn', true), el('div', { class: 'shoal-hint__body' },
            el('p', { class: 'shoal-hint__title', text: t('fixFields', { n: problems.length }) }),
            el('ul', { class: 'shoal-bullets' }, problems.map((p) => el('li', {}, linkButton(p.label, () => p.input.focus()))))));
        summary.setAttribute('tabindex', '-1');
        summary.focus();
    }

    async function save() {
        const values = collect();
        const problems = validate(values);
        showSummary(problems);
        if (problems.length) {
            return;
        }

        await runAction(ctx, saveBtn, {
            busyText: t('saving'),
            run: () => ctx.api.saveSettings(values),
            onDone: (result) => {
                st.dirty = false;
                dirtyNote.hidden = true;
                savedNote.hidden = false;
                ctx.announce(t('settingsSaved'));
ctx.toast(t('settingsSaved'));
                if (result) {
                    st.settings = Object.assign({}, st.settings, result);
                    if (result.imageSandbox) {
                        renderSandbox(result.imageSandbox);
                    }

                    if (result.stubRootCheck) {
                        replaceKids(q(body, '.shoal-checkhost'), checkLine(result.stubRootCheck));
                        f.stubAck.root.hidden = result.stubRootCheck !== 'network';
                    }
                }
            },
            onError: (err) => {
                if (err.status === 400 && err.kind === 'stubRoot') {
                    // The server refused the folder: show why, in the words of the folder check (ui-spec 8.5 item 1).
                    replaceKids(q(body, '.shoal-checkhost'), checkLine(err.detail));
                    f.stubAck.root.hidden = err.detail !== 'network';
                    st.networkRoot = err.detail === 'network' ? f.stubRoot.value() : null;
                    showSummary([{ label: t('fStubRoot'), input: f.stubRoot.input }]);
                    return;
                }

                ctx.alertMessage(failureMessage(err, 'saveFailed'));
            }
        });
    }

    /* ---------- Protect your shared videos (DESIGN 8.4, ui-spec 8.5 item 7, owner D9) ---------- */

    /** A mode card: looks like a choice card but is not a radio, because a change saves at once. */
    function modeCard(o) {
        const head = el('p', { class: 'shoal-modecard__title' }, o.current ? icon('check', true) : null, el('span', { text: o.title }), o.current ? el('span', { class: 'shoal-modecard__current', text: t('modeCurrent') }) : null);
        return el('div', { class: 'shoal-modecard' + (o.current ? ' is-current' : '') }, head, el('p', { class: 'shoal-meta', text: o.body }), o.action || null);
    }

    function hardeningPanel() {
        const h = st.hardening;
        const p = panel(t('sProtect'), { cls: 'shoal-panel--form shoal-protect' });
        if (!st.hasFriends || !h) {
            p.body.append(paragraph(t('protectLater'), 'shoal-meta'));
            return p.root;
        }

        const modeName = { LogOnly: 'modeLogOnly', Enforce: 'modeEnforce', Off: 'modeOff' };
        const tone = h.mode === 'Enforce' ? 'ok' : h.mode === 'LogOnly' ? 'warn' : 'off';
        const days = Math.max(0, Math.floor((ctx.now() - (toMs(h.sinceUtc) || ctx.now())) / 86400000));
        if (h.mode === 'Off') {
            p.body.append(hint('warn', 'warn', t('protectOffNow')));
        }

        p.body.append(
            paragraph(t('protectIntro')),
            el('p', { class: 'shoal-protect__current' }, chip(tone, 'shield', t(modeName[h.mode])), toMs(h.sinceUtc) ? el('span', { class: 'shoal-meta', text: t('protectSince', { date: ctx.fmt.date(h.sinceUtc) }) }) : null));

        const change = async (mode, opener) => {
            const toEnforce = mode === 'Enforce';
            const toOff = mode === 'Off';
            let confirmed = true;
            if (toEnforce || toOff) {
                const res = await confirmDialog(ctx, {
                    title: t(toEnforce ? 'protectConfirmTitle' : 'protectOffTitle'),
                    intro: t(toEnforce ? 'protectConfirmBody' : 'protectOffBody'),
                    confirmLabel: t(toEnforce ? 'protectConfirmButton' : 'protectOffButton'),
                    danger: false,
                    safeFocus: toOff
                });
                confirmed = res.confirmed;
            }

            if (confirmed) {
                try {
                    const next = await ctx.api.setHardening(mode);
                    st.hardening = Object.assign({}, h, next && next.mode ? next : { mode });
                    ctx.announce(t('announceProtectSaved', { mode: t(modeName[st.hardening.mode]).toLowerCase() }));
                } catch (err) {
                    ctx.alertMessage(failureMessage(err, 'saveFailed'));
                }
            }

            // Only this panel is redrawn, so unsaved edits in the settings form above survive.
            const fresh = hardeningPanel();
            protectNode.replaceWith(fresh);
            protectNode = fresh;
            const again = q(fresh, '[data-mode-action="' + (confirmed ? mode : (opener || '')) + '"]') || q(fresh, 'button[data-mode-action]');
            if (again) {
                again.focus();
            }
        };

        const modeBtn = (mode, labelKey, primary) => {
            const b = button({ label: t(labelKey), variant: primary ? 'primary' : 'quiet', onClick: () => change(mode, mode) });
            b.dataset.modeAction = mode;
            return b;
        };
        const logOnly = modeCard({
            title: t('modeLogOnlyTitle'), body: t('modeLogOnlyBody'), current: h.mode === 'LogOnly',
            action: h.mode === 'LogOnly' ? null : modeBtn('LogOnly', 'protectSwitchLog', false)
        });
        const enforce = modeCard({
            title: t('modeEnforce'), body: t('modeEnforceBody'), current: h.mode === 'Enforce',
            action: h.mode === 'Enforce' ? null : modeBtn('Enforce', 'protectRequire', true)
        });
        p.body.append(el('div', { class: 'shoal-modecards' }, logOnly, enforce));

        const rows = (h.counters || []).map((c) => ({
            app: c.app,
            network: t(NETWORK_LABELS[c.network] || 'netInternet'),
            kind: t(GUARD_KIND_LABELS[c.kind] || 'kindStream'),
            count: ctx.fmt.num(c.count)
        }));
        const wouldBlock = el('div', { class: 'shoal-protect__block' }, el('h3', { class: 'shoal-h3', text: t('wouldBeBlocked') }));
        if (rows.length === 0) {
            wouldBlock.append(paragraph(t('nothingYet', { d: days }), 'shoal-meta'));
        } else {
            wouldBlock.append(dataTable({
                caption: t('wouldBeBlocked'),
                columns: [{ key: 'app', label: t('colApp') }, { key: 'network', label: t('colNetwork') }, { key: 'kind', label: t('colKind') }, { key: 'count', label: t('colCount'), num: true }],
                rows
            }));
        }

        p.body.append(wouldBlock);
        if (h.mode === 'LogOnly' && days >= 7) {
            const top = (h.counters || []).slice().sort((a, b) => b.count - a.count)[0];
            p.body.append(top ? hint('warn', 'warn', t('protectWarn', { n: top.count, app: top.app })) : hint('ok', 'ok', t('protectOk')));
        }

        p.body.append(el('div', { class: 'shoal-protect__block' },
            el('h3', { class: 'shoal-h3', text: t('alwaysOn') }),
            el('p', { class: 'shoal-meta', text: t('shieldHits', { n: h.shieldHits || 0 }) }),
            el('p', { class: 'shoal-meta', text: t('wrongInvites', { n: h.inviteFailures ? h.inviteFailures.lastHour || 0 : 0 }) }),
            el('p', { class: 'shoal-meta', text: t('capHits', { n: h.transcodeCapHits || 0 }) }),
            el('p', { class: 'shoal-meta', text: t('shareAccountsLimits') })));
        if (h.inviteFailures && h.inviteFailures.notice) {
            p.body.append(hint('info', 'info', t('inviteNotice', { n: h.inviteFailures.lastHour })));
        }

        if (h.proxySuspected) {
            p.body.append(hint('warn', 'warn', t('proxySuspected')));
        }

        if ((h.guardErrors || 0) > 0) {
            p.body.append(hint('warn', 'warn', t('guardErrorsHint', { n: h.guardErrors })));
        }

        p.body.append(collapse(t('mightStop'), el('ul', { class: 'shoal-bullets' }, ['mightStop1', 'mightStop2', 'mightStop3'].map((k) => el('li', { text: t(k) })))));
        const counted = ctx.status && ctx.status.intercept;
        if (counted && Array.isArray(counted.counters) && counted.counters.length) {
            p.body.append(collapse(t('countersTitle'), [
                paragraph(t('countersIntro', { when: ctx.fmt.relative(counted.sinceUtc) }), 'shoal-meta'),
                dataTable({
                    caption: t('countersTitle'),
                    columns: [{ key: 'what', label: t('colCounter') }, { key: 'count', label: t('colCount'), num: true }],
                    rows: counted.counters
                        .filter((c) => c && typeof c.name === 'string' && /^[a-z0-9-]{1,40}$/.test(c.name))
                        .map((c) => ({ what: STRINGS['counter_' + c.name.replace(/-/g, '_')] === undefined ? c.name : t('counter_' + c.name.replace(/-/g, '_')), count: ctx.fmt.num(Number(c.count) || 0) }))
                })
            ]));
        }

        if ((h.modes || []).includes('Off') && h.mode !== 'Off') {
            p.body.append(collapse(t('troubleshooting'), [paragraph(t('troubleBody')), modeBtn('Off', 'protectTurnOff', false)]));
        }

        return p.root;
    }

    function keyStoragePanel(d) {
        const p = panel(t('sKeys'), { cls: 'shoal-panel--form' });
        p.body.append(paragraph(t('keyLine', { source: t(KEY_SOURCE_WORDS[d.keySource] || 'keyWordFile') })));
        if (d.keySource !== 'env' && d.keySource !== 'envFile') {
            p.body.append(paragraph(t('keyHelp'), 'shoal-meta'));
        }

        if (d.secretsVolumeUnsafe) {
            p.body.append(hint('warn', 'warn', t('keyVolumeUnsafe')));
        }

        return p.root;
    }

    function parentalPanel() {
        const p = panel(t('sParental'), { cls: 'shoal-panel--form' });
        p.body.append(paragraph(t('parentalBody')));
        return p.root;
    }

    function statePanel() {
        const p = panel(t('sState'), { cls: 'shoal-panel--form' });
        const s = st.state || {};
        p.body.append(paragraph(s.lastBackupUtc ? t('stateBackup', { when: ctx.fmt.relative(s.lastBackupUtc) }) : t('stateNoBackup')));
        if (s.backupByCopy) {
            p.body.append(paragraph(t('stateByCopy'), 'shoal-meta'));
        }

        const findings = s.findings || [];
        if (findings.length) {
            // The same sentence 25 times is noise: folders and keys that belong to no server are counted once, with the folder names.
            const grouped = ['orphanFolder', 'orphanSecret'].map((kind) => [kind, findings.filter((x) => x.kind === kind)]).filter((g) => g[1].length > 0);
            const single = findings.filter((x) => x.kind !== 'orphanFolder' && x.kind !== 'orphanSecret');
            const rows = grouped.map(([kind, list]) => {
                const names = list.map((x) => x.library).filter(Boolean).map((n) => String(n).slice(0, 8));
                return el('li', { class: 'shoal-row' }, icon('warn', true), el('span', { class: 'shoal-tone--warn' }, ' '),
                    el('span', { class: 'shoal-row__main' }, t(kind === 'orphanFolder' ? 'finding_orphanFolders' : 'finding_orphanSecrets', { n: list.length }),
                        names.length ? el('span', { class: 'shoal-meta shoal-row__sub', text: ' ' + t('findingFolderNames', { names: names.join(', ') }) }) : null));
            }).concat(single.map((x) => el('li', { class: 'shoal-row' }, icon('warn', true), el('span', { class: 'shoal-tone--warn' }, ' '), el('span', { class: 'shoal-row__main', text: t('finding_' + x.kind, { server: x.serverName || '', library: x.library || '' }) }))));
            p.body.append(el('ul', { class: 'shoal-list' }, rows));
        } else {
            p.body.append(el('p', { class: 'shoal-checkline shoal-tone--ok' }, icon('ok', true), el('span', { text: t('stateClean') })));
        }

        return p.root;
    }

    function dangerPanel() {
        const btn = button({
            label: t('deleteAllButton'),
            onClick: async () => {
                const res = await confirmDialog(ctx, {
                    title: t('deleteAllTitle'),
                    intro: t('deleteAllBody'),
                    typed: { label: t('typeToConfirm', { text: t('deleteAllPhrase') }), expected: t('deleteAllPhrase') },
                    confirmLabel: t('deleteAll'),
                    danger: true
                });
                if (res.confirmed) {
                    try {
                        await ctx.api.deleteAll(t('deleteAllPhrase'));
                        ctx.announce(t('announceDeletedAll'));
                        ctx.navigate('overview');
                    } catch (err) {
                        ctx.fail(failureMessage(err));
                    }
                }
            }
        });
        return el('section', { class: 'shoal-danger', 'aria-labelledby': 'shoal-danger-h-' + ctx.instance },
            el('h2', { class: 'shoal-h2', id: 'shoal-danger-h-' + ctx.instance, text: t('sDanger') }),
            paragraph(t('dangerBody')),
            btn);
    }

    function render() {
        clear(body);
        for (const k of Object.keys(f)) {
            delete f[k];
        }

        protectNode = hardeningPanel();
        body.append(buildForm(st.settings), protectNode, keyStoragePanel(st.settings), parentalPanel(), statePanel(), dangerPanel());
    }

    async function load(initial) {
        if (initial) {
            replaceKids(body, el('div', { class: 'shoal-skel', 'aria-busy': 'true' }, el('span', { class: 'shoal-sr', text: t('loadingShoal') }), el('div', { class: 'shoal-skel__panel' }), el('div', { class: 'shoal-skel__panel shoal-skel__panel--short' })));
        }

        try {
            const status = ctx.api.status().catch(() => ctx.status);
            const [settings, state, sts] = await Promise.all([ctx.api.settings(), ctx.api.state().catch(() => ({})), status]);
            st.hasFriends = !!(sts && (sts.friends || []).length);
            st.hardening = st.hasFriends ? await ctx.api.hardening().catch(() => null) : null;
            if (!ctx.alive) {
                return;
            }

            st.settings = settings;
            st.state = state;
            st.loaded = true;
            render();
        } catch {
            if (!st.loaded && ctx.alive) {
                replaceKids(body, loadErrorBox(() => load(true)));
            }
        }
    }

    return {
        show() {
            if (!st.loaded) {
                load(true);
            }
        },
        hide() {},
        destroy() {}
    };
}

/* ====================================================================================== */
/* Screen: Pair a server (ui-spec 8.2, DESIGN 4.1b, 4.2, 4.2a, 9.3, 9.5)                  */
/* ====================================================================================== */

const PROBE_FIELD_ERRORS = {
    blocked: ['url', 'errBlocked'], wrongNetwork: ['url', 'errWrongNetwork'], proxyNotAllowed: ['proxy', 'errProxyNotAllowed']
};
const NETWORKS = ['Tailnet', 'LAN', 'Public'];

function stepsIndicator(labels, current, doneAll) {
    const items = labels.map((label, i) => {
        const done = doneAll || i < current;
        const cur = !doneAll && i === current;
        return el('li', { class: 'shoal-steps__item' + (done ? ' is-done' : '') + (cur ? ' is-current' : ''), 'aria-current': cur ? 'step' : null },
            el('span', { class: 'shoal-steps__n', 'aria-hidden': 'true' }, done ? icon('check', true) : String(i + 1)),
            el('span', { class: 'shoal-steps__label', text: label }));
    });
    return el('ol', { class: 'shoal-steps', 'aria-label': t('stepsLabel') }, items);
}

function markSvg() {
    const svg = svgEl('svg', { class: 'shoal-mark', viewBox: '0 0 120 48', 'aria-hidden': 'true', focusable: 'false' });
    const g = svgEl('g', { fill: 'currentColor' });
    for (const [transform, opacity] of [['translate(6 6)', null], ['translate(54 22) scale(.8)', '.7'], ['translate(86 6) scale(.62)', '.45']]) {
        g.append(svgEl('use', { href: '#shoal-fish', transform, opacity }));
    }

    const eyes = svgEl('g', { class: 'shoal-mark__eyes' });
    for (const [cx, cy, r] of [[39, 14.5, 1.6], [80.4, 28.8, 1.3], [106.5, 11.3, 1]]) {
        eyes.append(svgEl('circle', { cx, cy, r }));
    }

    svg.append(g, eyes);
    return svg;
}

function pairScreen(ctx) {
    const view = q(ctx.root, '[data-shoal-view="pair"]');
    const host = slot(view, 'body');
    const freshWizard = () => ({
        dir: ctx.params.dir === 'share' ? 'share' : ctx.params.dir === 'watch' ? 'watch' : null,
        step: 'direction',
        form: { method: null, name: '', url: '', network: 'Tailnet', netTouched: false, proxy: '', invite: '', lanAck: true },
        inviteLeftMin: null,
        reach: null,
        reachLoaded: false,
        probe: null,
        session: null,
        outcome: null,
        stored: null,
        peerHandle: null,
        known: null,
        resume: false,
        remote: null,
        libs: null,
        added: null,
        // share direction
        shareForm: { name: '', libraries: new Set(), limit: '', transcodes: 4, transport: null, address: '', addressEdited: false },
        share: null,
        invite: null,
        approval: null,
        shareOutcome: null
    });
    const wiz = freshWizard();
    function restartWizard() {
        poller.stop();
        for (const key of Object.keys(wiz)) {
            delete wiz[key];
        }

        Object.assign(wiz, freshWizard());
        init();
    }

    let countdownRef = null;
    let progressTimer = null;
    const poller = createPoller(ctx, poll, PAIRING_POLL_MS);
    let started = false;
    let step2Hint = null;

    /* ---------- shared pieces ---------- */

    function stopTimers() {
        if (countdownRef) {
            countdownRef.stop();
            countdownRef = null;
        }

        ctx.clearTimer(progressTimer);
        progressTimer = null;
    }

    function focusStep() {
        const h = q(host, 'h2[tabindex]');
        if (h) {
            h.focus({ preventScroll: true });
        }
    }

    function go(step, keepFocus) {
        wiz.step = step;
        wiz.outcome = null;
        render();
        if (!keepFocus) {
            focusStep();
        }
    }

    function stepTitle(text) {
        return el('h2', { class: 'shoal-h2 shoal-step__title', tabindex: '-1', text });
    }

    function actions(...btns) {
        return el('div', { class: 'shoal-step__actions' }, btns.filter(Boolean));
    }

    function formHint(message, tone) {
        return el('div', { class: 'shoal-hint shoal-hint--' + (tone || 'bad'), role: 'alert' }, icon('warn', true), el('div', { class: 'shoal-hint__body', text: message }));
    }

    function labels() {
        if (wiz.dir === 'share') {
            return [t('stepWho'), t('pv2StepReach'), t('pv2StepWait'), t('stepApprove')];
        }

        return [t('pv2StepAddress'), t('stepCheck'), t('pv2StepInvite'), t('pv2StepCode'), t('stepChoose')];
    }

    function stepIndex() {
        if (wiz.dir === 'share') {
            return { who: 0, reach: 1, wait: 2, approve: 3, done: 3 }[wiz.step] ?? 0;
        }

        return { connect: 0, check: 1, invite: 2, pair: 3, choose: 4, done: 4 }[wiz.step] ?? 0;
    }

    /** The head of a step (3.1): the Who label(s), then the h2 that is described by them. */
    function head(title, ...kinds) {
        const pills = kinds.map((k) => whoLabel(k));
        const h = stepTitle(title);
        h.setAttribute('aria-describedby', pills.map((p) => p.id).join(' '));
        return el('div', { class: 'shoal-stephead' }, el('div', { class: 'shoal-whos' }, pills), h);
    }

    /** "Where each code goes" in a dialog, for the steps that only link to it. */
    function showCodes(side, route) {
        return openDialog(ctx, {
            title: t('pv2CodesTitle'),
            wide: true,
            build(body) {
                body.append(codesCard(ctx, side, route, true));
                return null;
            },
            buttons: [{ label: t('close'), close: undefined }]
        });
    }

    /** Opens and scrolls to the codes card when the page has one, else shows it in a dialog. */
    function revealCodes(side, route) {
        const card = q(host, '.shoal-codescard');
        if (card) {
            card.open = true;
            card.scrollIntoView({ block: 'nearest' });
            const sum = q(card, 'summary');
            if (sum) {
                sum.focus({ preventScroll: true });
            }

            return;
        }

        showCodes(side, route);
    }

    function keptLine() {
        const f = wiz.form;
        return el('p', { class: 'shoal-meta shoal-recover__kept', text: t('pv2Kept', { what: f.url ? t('pv2KeptWatch', { name: f.name || serverName(), url: f.url }) : t('pv2KeptWatchNoUrl') }) });
    }

    function indicator() {
        const ls = labels();
        const i = stepIndex();
        const doneAll = wiz.step === 'done';
        return el('div', { class: 'shoal-stepshead' },
            stepsIndicator(ls, i, doneAll),
            el('p', { class: 'shoal-steps__compact shoal-meta', text: t('stepOf', { i: i + 1, n: ls.length, label: ls[i] }) }));
    }

    function statusRow(tone, iconName, text, id) {
        return el('p', { class: 'shoal-pairstatus shoal-tone--' + tone, role: 'status', id }, icon(iconName), el('span', { text }));
    }

    function countdownNode(expiresUtc, onExpire, key, vars) {
        const cd = countdown(ctx, expiresUtc, onExpire, key, vars);
        countdownRef = cd;
        return cd.root;
    }

    function serverName() {
        return (wiz.session && wiz.session.displayName) || wiz.form.name || (wiz.stored && wiz.stored.displayName) || t('thisServer');
    }

    function homeName() {
        return (wiz.session && wiz.session.homeName) || (ctx.status && ctx.status.homeName) || t('thisServerName');
    }

    /* ---------- rendering ---------- */

    function render() {
        stopTimers();
        clear(host);
        const pageTitle = q(view, 'h1');
        if (pageTitle) {
            pageTitle.textContent = t(wiz.dir === 'share' ? 'pv2PageShare' : 'pairServer');
        }

        const wrap = el('div', { class: 'shoal-wizard' });
        host.append(wrap);
        if (wiz.outcome) {
            wrap.append(renderOutcome());
            return;
        }

        if (wiz.shareOutcome) {
            wrap.append(renderShareOutcome());
            return;
        }

        if (wiz.resume) {
            wrap.append(renderResume());
            return;
        }

        if (!wiz.dir) {
            wrap.append(renderDirection());
            return;
        }

        wrap.append(indicator());
        const panelNode = el('section', { class: 'shoal-panel shoal-step' });
        wrap.append(panelNode);
        const table = wiz.dir === 'watch'
            ? { connect: renderConnect, check: renderCheck, invite: renderInviteStep, pair: renderPair, choose: renderChoose, done: renderDone }
            : { who: renderShareWho, reach: renderShareReach, wait: renderShareWait, approve: renderShareApprove, done: renderShareDone };
        panelNode.append((table[wiz.step] || (wiz.dir === 'watch' ? renderConnect : renderShareWho))());
    }

    function renderResume() {
        const s = wiz.session;
        return el('section', { class: 'shoal-panel shoal-step' },
            stepTitle(t('pairTitleShort')),
            el('div', { class: 'shoal-hint shoal-hint--info', role: 'note' }, icon('info', true), el('div', { class: 'shoal-hint__body', text: t('resumeBody', { server: serverName() }) })),
            actions(
                button({ label: t('continue'), variant: 'primary', onClick: () => {
                    wiz.resume = false;
                    go(s.state === 'done' ? 'choose' : 'pair');
                } }),
                linkButton(t('cancelPairing'), () => cancelSession())));
    }

    function renderDirection() {
        const name = 'shoal-dir-' + ctx.instance;
        let chosen = null;
        const next = button({ label: t('continue'), variant: 'primary', disabled: true, onClick: () => {
            wiz.dir = chosen;
            go(chosen === 'share' ? 'who' : 'connect');
        } });
        const why = el('span', { class: 'shoal-meta shoal-step__why', text: t('pv2ChooseOne') });
        const pick = (v) => {
            chosen = v;
            next.disabled = false;
            why.hidden = true;
        };
        return el('section', { class: 'shoal-panel shoal-step' },
            stepTitle(t('pv2DirTitle')),
            el('fieldset', { class: 'shoal-choices' },
                el('legend', { class: 'shoal-sr', text: t('pv2DirTitle') }),
                choiceCard({ name, value: 'watch', title: t('pv2DirWatch'), body: t('pv2DirWatchBody'), onChange: pick }).root,
                choiceCard({ name, value: 'share', title: t('pv2DirShare'), body: t('pv2DirShareBody'), onChange: pick }).root),
            paragraph(t('pv2DirBoth'), 'shoal-meta'),
            actions(next, why));
    }

    /* ----- outcome (8.2.5, pairing v2 8.3), live or stored (8.2.9): the recovery card (3.5) ----- */

    /**
     * What the recovery card says for a stopped watch pairing: who acts, the words, and the one step the primary button returns to.
     * Returns { info, who: [kinds], title, body, next: 'invite' | 'prepare' | 'connect' | 'again', label }.
     */
    function recoverySpec(o) {
        const server = serverName();
        const info = outcomeInfo(o.kind, { server, home: homeName(), code: o.code, method: 'invite', phase: o.phase, afterApproval: o.afterApproval, minutes: o.minutes });
        const spec = { info, who: ['friend'], title: info.title, body: info.body, next: 'invite', label: t('pv2PasteNewInvite') };
        switch (info.kind) {
            case 'expired':
            case 'interrupted':
                return Object.assign(spec, { title: t('pv2RanOutInviteTitle'), body: info.kind === 'expired' ? t('pv2RanOutInviteBody') : info.body });
            case 'inviteInvalid':
                return Object.assign(spec, { title: t('pv2InviteDidntTitle'), body: t('pv2InviteDidntBody') });
            case 'denied':
                return Object.assign(spec, { title: t('pv2DeniedTitle'), body: t('pv2DeniedBody') });
            case 'codeMismatch':
                return Object.assign(spec, { who: ['you', 'friend'], title: t('pv2MismatchTitle'), body: t('pv2MismatchBody') });
            case 'serverChanged':
            case 'badPeerResponse':
            case 'adminAccount':
                return Object.assign(spec, { who: ['you'], next: 'connect', label: t('pv2ChangeAddress') });
            case 'rateLimited':
                return Object.assign(spec, { who: ['you'], next: 'again', label: t('tryAgain') });
            case 'network':
            case 'server':
                return Object.assign(spec, { who: ['you'], label: t('startAgain') });
            default:
                return spec;
        }
    }

    function renderOutcome() {
        const o = wiz.outcome;
        const spec = recoverySpec(o);
        const info = spec.info;
        const stored = !!wiz.peerHandle;
        const btns = [];
        if (spec.next === 'again') {
            const again = button({ label: spec.label, variant: 'primary', onClick: () => {
                wiz.outcome = null;
                go(wiz.session ? 'check' : 'connect');
            } });
            const note = el('span', { class: 'shoal-meta shoal-action__note', id: uid('shoal-note'), hidden: true });
            ctx.setCooldown('pair-rate', o.retryAfter || 60);
            cooldownNote(ctx, 'pair-rate', note, again);
            btns.push(el('span', { class: 'shoal-action' }, again, note));
        } else {
            btns.push(button({ label: spec.label, variant: 'primary', onClick: () => startAgain(info.kind, spec.next) }));
        }

        if (stored && info.actions.includes('dismiss')) {
            btns.push(button({ label: t('dismiss'), onClick: (ev, btn) => dismiss(btn) }));
        }

        const failure = info.tone === 'bad';
        const cancelled = false;
        const node = el('section', { class: 'shoal-panel shoal-step shoal-outcome shoal-recover shoal-outcome--' + info.tone, role: failure || info.safety ? 'alert' : null },
            el('div', { class: 'shoal-outcome__icon shoal-tone--' + info.tone }, icon(info.icon)),
            stepTitle(spec.title),
            o.stoppedUtc ? el('p', { class: 'shoal-meta', text: t('stoppedAgo', { when: ctx.fmt.relative(o.stoppedUtc) }) }) : null,
            paragraph(spec.body, 'shoal-lead'),
            cancelled ? paragraph(t('pv2CodeCancelled'), 'shoal-meta') : null,
            el('div', { class: 'shoal-whos' }, spec.who.map((k) => whoLabel(k))),
            spec.next === 'invite' && spec.who.length === 1 && spec.who[0] === 'friend' ? paragraph(t('pv2WhoLineInvite'), 'shoal-meta') : null,
            actions(...btns),
            keptLine());
        return node;
    }

    function showOutcome(kind, extra) {
        poller.stop();
        wiz.outcome = Object.assign({ kind }, extra || {});
        render();
        focusStep();
        const info = outcomeInfo(kind, { server: serverName() });
        ctx.alertMessage(recoverySpec(wiz.outcome).title || info.title);
    }

    async function dismiss(btn) {
        await runAction(ctx, btn, {
            busyText: t('applying'),
            run: () => ctx.api.dismissPairing(wiz.peerHandle),
            onDone: (r) => {
                ctx.announce(r && r.restored ? t('announceDismissedBack', { server: serverName() }) : t('announceDismissedNew'));
                ctx.navigate('overview');
            },
            onError: (err) => ctx.alertMessage(failureMessage(err))
        });
    }

    /**
     * The recovery card's one primary button: back to the step that fixes it, with the name, the address and the route kept, never a new pairing
     * on its own. The invite and any code are never kept (single use). After a safety stop that left the sharing account suspect, the Quick
     * Connect step asks for a new account first.
     */
    async function startAgain(kind, next) {
        wiz.session = wiz.session && wiz.session.state === 'failed' ? null : wiz.session;
        wiz.outcome = null;
        wiz.form.invite = '';
        go(next === 'connect' ? 'connect' : 'invite');
        if (next !== 'connect') {
            // After "The invite ran out" the empty invite box is ready for the new one (3.5, 8.3).
            const box = q(host, '.shoal-invitefield input');
            if (box) {
                box.focus();
            }
        }
    }

    /* ----- watch: the address ----- */

    function networkHelp(value) {
        return t(value === 'Tailnet' ? 'netHelpTailnet' : value === 'LAN' ? 'pv2NetHelpPrivate' : 'netHelpPublic');
    }

    /* ----- watch: the address (pairing v2 5.3, 6) ----- */

    /**
     * The connection helper (6.2): four setups with the address to use and the network to choose. o: { share, friend, onUse(network, focusAddress) }.
     * Watch side words are about the friend's server, share side words about yours.
     */
    function addressHelper(o) {
        const vars = { friend: o.friend || t('yourFriend') };
        const keys = o.share
            ? [['pv2ShSetup1Title', 'pv2ShSetup1Body'], ['pv2ShSetup2Title', 'pv2ShSetup2Body'], ['pv2ShSetup3Title', 'pv2ShSetup3Body'], ['pv2ShSetup4Title', 'pv2ShSetup4Body']]
            : [['pv2Setup1Title', 'pv2Setup1Body'], ['pv2Setup2Title', 'pv2Setup2Body'], ['pv2Setup3Title', 'pv2Setup3Body'], ['pv2Setup4Title', 'pv2Setup4Body']];
        const cards = keys.map(([title, body], i) => {
            const n = i + 1;
            const network = n === 1 ? 'Tailnet' : n === 2 ? 'LAN' : 'Public';
            return el('div', { class: 'shoal-setup' },
                el('h4', { class: 'shoal-setup__title', text: t(title, vars) }),
                el('p', { class: 'shoal-meta', text: t(body, vars) }),
                button({ label: t('pv2UseSetup'), small: true, onClick: () => o.onUse(network, n === 1 || n === 3) }));
        });
        const content = el('div', { class: 'shoal-helper' },
            paragraph(o.share ? t('pv2ShHelperLead', vars) : t('pv2HelperLead')),
            o.share ? null : sayBox(ctx, t('pv2HelperSay')),
            el('div', { class: 'shoal-setups' }, cards),
            paragraph(t(o.share ? 'pv2ShSetupClose' : 'pv2SetupClose'), 'shoal-meta'));
        return collapse(t(o.share ? 'pv2ShHelperTitle' : 'pv2HelperTitle'), content);
    }

    /** The live address hints (6.1) as hint boxes, with the quick fix button for a missing scheme. */
    function shareWords(reading, friend) {
    // The share side types its own server's address: the friend's words ("inside your friend's network", "points to this server") would name the wrong owner.
    const map = { pv2AddrPrivate: 'pv2ShAddrPrivate', pv2AddrSelf: 'pv2ShAddrSelf' };
    return Object.assign({}, reading, { msgs: reading.msgs.map((m) => (map[m.key] ? Object.assign({}, m, { key: map[m.key], vars: Object.assign({}, m.vars, { friend }) }) : m)) });
}

function addressHints(reading, onFix) {
        const nodes = [];
        for (const m of reading.msgs) {
            const tone = m.tone === 'bad' ? 'bad' : m.tone;
            const node = hint(tone, tone === 'bad' ? 'x' : tone === 'ok' ? 'ok' : tone === 'warn' ? 'warn' : 'info', t(m.key, m.vars) + (m.extra ? ' ' + t(m.extra) : ''));
            if (m.quickFix) {
                node.querySelector('.shoal-hint__body').append(' ', linkButton(t('pv2AddHttp'), () => onFix(m.quickFix)));
            }

            nodes.push(node);
        }

        return nodes;
    }

    function renderConnect() {
        const f = wiz.form;
        const wrap = el('div', { class: 'shoal-step__body' }, head(t('pv2AddrTitle'), 'you'));
        const errorHost = el('div', { class: 'shoal-formerror' });
        const fields = {};
        fields.name = textField({ id: uid('shoal-pf'), label: t('fServerName'), help: t('pv2NameHelp'), value: f.name, maxlength: 64, required: true });
        fields.url = textField({ id: uid('shoal-pf'), label: t('fServerAddress'), help: t('pv2AddrHelp'), value: f.url, type: 'url', inputmode: 'url', autocapitalize: 'off', spellcheck: false, required: true });
        const hintHost = el('div', { class: 'shoal-addrhints', 'aria-live': 'polite' });
        fields.network = selectField({
            id: uid('shoal-pf'),
            label: t('fReach'),
            options: [{ value: 'Tailnet', label: t('reachTailnet') }, { value: 'LAN', label: t('pv2ReachPrivate') }, { value: 'Public', label: t('reachPublic') }],
            value: f.network,
            help: networkHelp(f.network)
        });
        fields.proxy = textField({ id: uid('shoal-pf'), label: t('fProxy'), help: t('fProxyHelp'), value: f.proxy, mono: true, spellcheck: false });
        const advanced = collapse(t('advanced'), fields.proxy.root);
        let touched = !!f.netTouched;
        let auto = false;
        let timer = null;
        const helper = addressHelper({
            share: false,
            onUse: (network, focusAddress) => {
                touched = true;
                f.netTouched = true;
                fields.network.select.value = network;
                fields.network.setHelp(networkHelp(network));
                sync();
                if (focusAddress) {
                    fields.url.input.focus();
                } else {
                    fields.network.select.focus();
                }
            }
        });

        const blockHost = el('div', { class: 'shoal-addrblock', 'aria-live': 'polite' });
        const sync = () => {
            const net = fields.network.select.value;
            const http = /^http:/i.test(fields.url.input.value.trim());
            clear(blockHost);
            if (http && net !== 'Tailnet') {
                blockHost.append(hint('warn', 'warn', t('pv2ErrInviteLan')));
            }

            advanced.hidden = net !== 'Tailnet';
        };
        const read = () => {
            ctx.clearTimer(timer);
            clear(hintHost);
            const misplaced = classifyCode(fields.url.input.value, 'address');
            if (misplaced) {
                // 7.1: an invite in the address field moves to the Invite box on the invite step.
                const note = hint('warn', 'warn', t(misplaced.key), ' ', misplaced.action === 'move' ? linkButton(t('pv2MoveInvite'), () => {
                    f.invite = fields.url.input.value.trim();
                    fields.url.input.value = '';
                    read();
                    fields.url.input.focus();
                }) : null);
                if (misplaced.alert) {
                    note.setAttribute('role', 'alert');
                }

                hintHost.append(note);
                return;
            }

            const r = readAddress(fields.url.input.value, window.location.hostname);
            auto = false;
            if (!r) {
                fields.network.setHelp(networkHelp(fields.network.select.value));
                sync();
                return;
            }

            if (r.trimmed) {
                fields.url.input.value = r.trimmed;
                f.trimNote = r.msgs.find((m) => m.key === 'pv2AddrTrimmed') || null;
            }

            if (r.network && !touched) {
                fields.network.select.value = r.network;
                auto = true;
            }

            fields.network.setHelp(auto ? t('pv2NetChose') : networkHelp(fields.network.select.value));
            const shown = r.msgs.some((m) => m.key === 'pv2AddrTrimmed') || !f.trimNote ? r : Object.assign({}, r, { msgs: [f.trimNote, ...r.msgs] });
            hintHost.append(...addressHints(shown, (fixed) => {
                fields.url.input.value = fixed;
                f.trimNote = null;
                read();
                fields.url.input.focus();
            }));
            if (r.msgs.some((m) => m.tone === 'warn' || m.tone === 'bad')) {
                helper.open = true;
            }

            sync();
        };
        fields.network.select.addEventListener('change', () => {
            touched = true;
            f.netTouched = true;
            auto = false;
            fields.network.setHelp(networkHelp(fields.network.select.value));
            sync();
        });
        // The hints appear while typing, never on blur: a hint that arrives on blur moves the Check server button between press and release and the click is lost.
        let lastRead = null;
        const readIfChanged = () => {
            if (fields.url.input.value !== lastRead) {
                read();
                lastRead = fields.url.input.value;
            }
        };
        fields.url.input.addEventListener('input', () => {
            ctx.clearTimer(timer);
            readIfChanged();
        });
        fields.url.input.addEventListener('blur', readIfChanged);
        fields.url.input.addEventListener('change', readIfChanged);
        fields.name.input.addEventListener('input', () => {
            const left = 64 - fields.name.input.value.length;
            fields.name.setHelp(left <= 14 ? t('charsLeft', { n: left }) : t('pv2NameHelp'));
        });

        const checkBtn = button({ label: t('checkServer'), variant: 'primary', type: 'submit' });
        const form = el('form', { class: 'shoal-form', novalidate: true },
            errorHost,
            fields.name.root,
            fields.url.root,
            hintHost,
            fields.network.root,
            blockHost,
            advanced,
            helper,
            actions(checkBtn, ctx.params.dir ? null : button({ label: t('back'), onClick: () => {
                wiz.dir = null;
                render();
                focusStep();
            } })));
        wrap.append(form);
        sync();
        if (f.url) {
            read();
        }

        form.addEventListener('submit', async (ev) => {
            ev.preventDefault();
            clear(errorHost);
            for (const fld of Object.values(fields)) {
                if (fld && fld.setError) {
                    fld.setError('');
                }
            }

            read();
            f.name = fields.name.value();
            f.url = fields.url.value();
            f.network = fields.network.select.value;
            f.proxy = f.network === 'Tailnet' ? fields.proxy.value() : '';
            const problems = [];
            const bad = (fld, key) => {
                fld.setError(t(key));
                problems.push(fld);
            };
            if (!f.name) {
                bad(fields.name, 'errEnterName');
            }

            const reading = readAddress(f.url, window.location.hostname);
            if (!f.url) {
                bad(fields.url, 'errEnterAddress');
            } else if (!/^https?:\/\//i.test(f.url)) {
                bad(fields.url, 'pv2AddrNoScheme');
            } else if (f.network === 'Public' && !/^https:/i.test(f.url)) {
                bad(fields.url, 'pv2ErrHttpsOnly');
            } else if (reading && reading.msgs.some((m) => m.key === 'pv2AddrSelf')) {
                bad(fields.url, 'pv2AddrSelf');
            }

            for (const old of form.querySelectorAll('.shoal-ackerror')) {
                old.remove();
            }

            if (problems.length) {
                if (problems.some((p) => p.setError)) {
                    errorHost.append(el('div', { class: 'shoal-hint shoal-hint--bad', role: 'alert' }, icon('warn', true), el('div', { class: 'shoal-hint__body', text: t('fixFields', { n: problems.length }) })));
                }

                const first = problems[0];
                (first.input || first.select || first.root).focus();
                return;
            }

            await runAction(ctx, checkBtn, {
                busyText: t('checking'),
                run: () => ctx.api.probe({ url: f.url, network: f.network, proxyUrl: f.proxy || null, lanAck: f.lanAck }),
                onDone: (probe) => {
                    wiz.probe = probe;
                    go('check');
                },
                onError: (err) => {
                    if (err.status === 429) {
                        errorHost.append(formHint(t('rateLimitedIn', { n: Math.max(1, Math.ceil((err.retryAfter || 60) / 60)) })));
                        return;
                    }

                    const fieldErr = PROBE_FIELD_ERRORS[err.kind];
                    if (fieldErr && fields[fieldErr[0]]) {
                        // 8.2.3: the wrong-network line is adapted to the class the admin chose.
                        const key = fieldErr[1] === 'errWrongNetwork' && f.network === 'LAN' ? 'errWrongNetworkLan' : fieldErr[1] === 'errWrongNetwork' && f.network === 'Public' ? 'errWrongNetworkPublic' : fieldErr[1];
                        fields[fieldErr[0]].setError(t(key));
                        fields[fieldErr[0]].input.focus();
                        return;
                    }

                    if (err.kind === 'certificate') {
                        errorHost.append(formHint(t('pv2ErrCert')));
                    } else if (err.kind === 'notJellyfin') {
                        errorHost.append(formHint(t('pv2ErrNotJellyfin')));
                    } else {
                        errorHost.append(el('div', { class: 'shoal-hint shoal-hint--bad', role: 'alert' }, icon('warn', true), el('div', { class: 'shoal-hint__body' },
                            el('p', { text: t('pv2ErrUnreachable') }),
                            el('ul', { class: 'shoal-bullets' }, [1, 2, 3, 4].map((n) => el('li', { text: t('pv2Unreach_' + n) }))),
                            button({ label: t('pv2OpenHelper'), small: true, onClick: () => {
                                helper.open = true;
                                if (helper.scrollIntoView) {
                                    helper.scrollIntoView({ block: 'nearest' });
                                }

                                const sum = q(helper, 'summary');
                                if (sum) {
                                    sum.focus({ preventScroll: true });
                                }
                            } }))));
                    }
                }
            });
        });
        return wrap;
    }

    /* ----- watch: the check (pairing v2 5.4) ----- */

    function renderCheck() {
        const p = wiz.probe;
        const f = wiz.form;
        const wrap = el('div', { class: 'shoal-step__body' }, head(t('pv2CheckTitle'), 'you'));
        // Both servers need Shoal (D17). A definite "no Shoal" blocks with the way forward; a throttled or unknown answer never blocks.
        const noShoal = p.shoalPingThrottled !== true && p.hasShoal === false;
        if (p.shoalPingThrottled === true) {
            wrap.append(hint('info', 'info', t('pv2PingThrottled')));
        } else if (p.hasShoal === true) {
            wrap.append(hint('ok', 'ok', t('pv2HasShoalYes')));
        }

        wrap.append(factList([
            [t('factName'), p.serverName],
            [t('factVersion'), p.version],
            [t('factAddress'), f.url],
            [t('factLatency'), ctx.fmt.ms(p.rttMs)],
            [t('factServerId'), serverIdText(p.serverIdPrefix)]
        ]), paragraph(t('pv2ServerIdNote'), 'shoal-meta'));
        if (p.versionTested === false) {
            wrap.append(hint('warn', 'warn', t('warnUntested', { version: p.version })));
        }

        if (p.alsoWatchesYou === true) {
            wrap.append(hint('info', 'info', t('pairAlsoShares', { name: p.serverName })));
        }

        if (f.network === 'LAN') {
            wrap.append(hint('warn', 'warn', t('warnLanClear')));
        }

        if (noShoal) {
            const message = t('installMessage', { url: INSTALL_URL, home: homeName() });
            const box = el('textarea', { class: 'emby-textarea shoal-input shoal-message', readonly: true, rows: '5', 'aria-label': t('installMessageLabel') });
            box.value = message;
            wrap.append(el('div', { class: 'shoal-hint shoal-hint--warn shoal-install', role: 'alert' }, icon('warn', true), el('div', { class: 'shoal-hint__body' },
                el('p', { class: 'shoal-hint__title', text: t('installTitle') }),
                el('p', { text: t('installBody') }),
                el('p', {}, el('a', { class: 'shoal-link', href: INSTALL_URL, target: '_blank', rel: 'noopener noreferrer', text: INSTALL_URL })),
                box,
                copyButton(ctx, message, 'pv2CopyMessage', 'pv2Copied'))));
            wrap.append(actions(
                button({ label: t('checkAgain'), variant: 'primary', onClick: (ev, btn) => recheck(btn) }),
                button({ label: t('pv2ChangeAddress'), onClick: () => go('connect') })));
            return wrap;
        }

        // An invite never travels over plain http outside Tailscale: a dead end with the reason, and the way forward is a different address.
        const inviteBlocked = /^http:/i.test(f.url) && f.network !== 'Tailnet';
        if (inviteBlocked) {
            wrap.append(hint('warn', 'warn', t('pv2ErrInviteLan')));
            wrap.append(actions(button({ label: t('pv2ChangeAddress'), variant: 'primary', onClick: () => go('connect') })));
            return wrap;
        }

        wrap.append(actions(button({ label: t('pv2YesThisIsIt'), variant: 'primary', onClick: () => go('invite') }), button({ label: t('pv2NoChangeAddress'), onClick: () => go('connect') })));
        return wrap;
    }

    async function recheck(btn) {
        const f = wiz.form;
        await runAction(ctx, btn, {
            busyText: t('checking'),
            run: () => ctx.api.probe({ url: f.url, network: f.network, proxyUrl: f.proxy || null, lanAck: f.lanAck }),
            onDone: (probe) => {
                wiz.probe = probe;
                render();
                focusStep();
            }
        });
    }

    /* ----- invite route: ask for the invite, then start (pairing v2 5.5) ----- */

    function renderInviteStep() {
        const f = wiz.form;
        const wrap = el('div', { class: 'shoal-step__body' }, stepTitle(t('pv2InviteTitle')));
        const blockA = el('div', { class: 'shoal-block' }, whoLabel('friend'),
            el('ol', { class: 'shoal-numbered' }, [1, 2, 3, 4, 5].map((n) => el('li', { text: t('pv2InviteStep_' + n) }))),
            sayBox(ctx, t('pv2InviteSay')));
        const errorHost = el('div', { class: 'shoal-formerror' });
        const field = textField({ id: uid('shoal-pi'), label: t('pv2InviteLabel'), help: t('pv2InviteHelp'), value: f.invite, mono: true, spellcheck: false, autocapitalize: 'off', required: true });
        field.root.classList.add('shoal-invitefield');
        const noteHost = el('div', { class: 'shoal-invitenote', 'aria-live': 'polite' });
        const forLine = el('p', { class: 'shoal-meta', hidden: true });
        const startBtn = button({ label: t('startPairing'), variant: 'primary', disabled: !looksLikeInvite(f.invite), onClick: (ev, btn) => start(btn) });
        const why = el('span', { class: 'shoal-meta shoal-step__why', text: t('pv2PasteToContinue'), hidden: looksLikeInvite(f.invite) });
        const blockB = el('div', { class: 'shoal-block' }, whoLabel('you'), field.root, forLine, noteHost, errorHost, actions(startBtn, button({ label: t('back'), onClick: () => go('check') }), why));
        wrap.append(blockA, blockB, el('p', {}, linkButton(t('pv2WhereLink'), () => showCodes('watch', 'invite'))));

        const reflect = () => {
            clear(noteHost);
            field.setError('');
            const value = field.input.value;
            const ok = looksLikeInvite(value);
            startBtn.disabled = !ok;
            why.hidden = ok;
            forLine.hidden = !ok;
            forLine.textContent = ok ? t('pv2InviteFor', { address: f.url }) : '';
            const c = classifyCode(value, 'invite');
            if (!c) {
                return;
            }

            const note = hint('bad', 'x', t(c.key));
            if (c.alert) {
                note.setAttribute('role', 'alert');
            } else {
                ctx.announce(t(c.key));
            }

            const body = note.querySelector('.shoal-hint__body');
            if (c.action === 'codes') {
                body.append(' ', linkButton(t('pv2ShowCodes'), () => showCodes('watch', 'invite')));
            } else if (c.action === 'address') {
                body.append(' ', linkButton(t('pv2GoToAddress'), () => go('connect')));
            }

            noteHost.append(note);
        };
        field.input.addEventListener('input', reflect);
        field.input.addEventListener('blur', reflect);
        if (f.invite) {
            reflect();
        }

        async function start(btn) {
            f.invite = field.value();
            clear(errorHost);
            field.setError('');
            if (!f.invite) {
                field.setError(t('pv2ErrInviteEmpty'));
                field.input.focus();
                return;
            }

            await runAction(ctx, btn, {
                busyText: t('starting'),
                run: async () => {
                    // The cheap re-check that confirms the invite belongs to this server, then the pairing itself.
                    const probe = await ctx.api.probe({ url: f.url, network: f.network, proxyUrl: f.proxy || null, invite: f.invite, lanAck: f.lanAck });
                    wiz.probe = probe;
                    wiz.inviteLeftMin = probe.inviteExpiresUtc ? Math.max(0, Math.round((toMs(probe.inviteExpiresUtc) - ctx.now()) / 60000)) : null;
                    return ctx.api.startPairing({ probeHandle: probe.probeHandle, displayName: f.name, method: 'invite' });
                },
                onDone: (session) => {
                    f.invite = '';
                    wiz.session = session;
                    wiz.peerHandle = session.peerHandle || wiz.peerHandle;
                    ctx.announce(t('announcePairingStarted', { server: f.name }));
                    go('pair');
                },
                onError: (err) => {
                    if (err.kind === 'inviteWrongServer') {
                        field.setError(t('pv2ErrInviteOther'));
                        errorHost.append(el('p', {}, linkButton(t('pv2ChangeAddress'), () => go('connect'))));
                        field.input.focus();
                    } else if (err.kind === 'inviteGone') {
                        field.input.value = '';
                        f.invite = '';
                        reflect();
                        field.setError(t('pv2ErrInviteGone'));
                        field.input.focus();
                    } else if (err.kind === 'inviteLanHttp') {
                        field.setError(t('pv2ErrInviteLan'));
                    } else if (err.kind === 'certificate') {
                        errorHost.append(formHint(t('pv2ErrCert')));
                    } else if (err.kind === 'notJellyfin') {
                        errorHost.append(formHint(t('pv2ErrNotJellyfin')));
                    } else if (err.kind === 'unreachable' || err.kind === 'blocked' || err.kind === 'wrongNetwork' || err.kind === 'proxyNotAllowed') {
                        errorHost.append(formHint(t('pv2ErrUnreachable')));
                    } else {
                        startError(err, errorHost);
                    }
                }
            });
        }

        return wrap;
    }

    /** The failures every start shares: the check ran out, another pairing is live, too many tries, or the server broke. */
    function startError(err, host2) {
        const old = q(host2, '.shoal-startfail');
        if (old) {
            old.remove();
        }

        if (err.status === 409 && err.kind !== 'probeExpired') {
            host2.append(el('div', { class: 'shoal-startfail shoal-hint shoal-hint--bad', role: 'alert' }, icon('warn', true),
                el('div', { class: 'shoal-hint__body' }, el('p', { text: t('alreadyPairing') }), linkButton(t('goToIt'), () => {
                    wiz.resume = false;
                    init(true);
                }))));
            return;
        }

        if (err.status === 429) {
            host2.append(el('div', { class: 'shoal-startfail' }, formHint(t('rateLimitedWait', { n: Math.max(1, Math.ceil((err.retryAfter || 60) / 60)) }))));
            return;
        }

        wiz.outcome = { kind: 'server' };
        render();
        focusStep();
    }

    /* ----- watch: read the safety code to your friend ----- */

    function renderPair() {
        const s = wiz.session;
        const server = serverName();
        const wrap = el('div', { class: 'shoal-step__body' });
        poller.start();
        const approved = s.state === 'approved' || s.state === 'awaitingConfirm';
        const hasCode = typeof s.safetyCode === 'string' && s.safetyCode.length > 0;
        appendKids(wrap, [
            head(t('pv2CodeTitle'), 'you'),
            paragraph(t('pv2CodeBody')),
            hasCode ? el('div', { class: 'shoal-codeblock' }, labelledCode('safety', s.safetyCode, 'safetyCodeWord'), copyButton(ctx, () => normalizeCode(s.safetyCode), 'copyCode', 'announceCodeCopied')) : statusRow('info', 'clock', t('pv2GettingCode', { server }), 'shoal-pairwait'),
            hasCode ? sayBox(ctx, t('pv2CodeSay', { home: homeName(), code: inFours(s.safetyCode) })) : null,
            approved ? statusRow('ok', 'ok', t('pv2ApprovedConnecting'), 'shoal-pairwait') : statusRow('info', 'clock', t('waitingForFriend', { server }), 'shoal-pairwait'),
            countdownNode(s.expiresUtc, () => showOutcome('expired', { method: 'invite' }), 'invite'),
            typeof wiz.inviteLeftMin === 'number' ? paragraph(t('pv2InviteLeft', { m: wiz.inviteLeftMin, n: wiz.inviteLeftMin }), 'shoal-meta') : null,
            el('p', {}, linkButton(t('pv2WhereLink'), () => showCodes('watch', 'invite'))),
            collapse(t('pv2NoRequestTitle'), paragraph(t('pv2NoRequestBody', { server, home: homeName() }), 'shoal-meta')),
            actions(linkButton(t('cancelPairing'), () => cancelSession()))]);
        return wrap;
    }

    function closeFinishedPairing() {
        poller.stop();
        ctx.api.cancelPairing().catch(() => {});
    }

    async function cancelSession() {
        try {
            await ctx.api.cancelPairing();
        } catch {
            /* nothing was saved either way */
        }

        poller.stop();
        ctx.announce(t('announceCancelled'));
        ctx.navigate('overview');
    }

    async function poll() {
        if (wiz.dir === 'share') {
            return pollApproval();
        }

        if (wiz.step !== 'pair' || wiz.outcome) {
            return;
        }

        let s = null;
        try {
            s = await ctx.api.pairing();
        } catch {
            return;
        }

        if (!ctx.alive || wiz.step !== 'pair') {
            return;
        }

        if (!s) {
            // The session is gone: either it was cancelled elsewhere or this server restarted. A restart stores "interrupted" with the server.
            poller.stop();
            if (wiz.peerHandle) {
                await loadStored(wiz.peerHandle);
            } else {
                showOutcome('interrupted', { method: wiz.form.method, phase: 'beforeApproval' });
            }

            return;
        }

        const before = wiz.session;
        wiz.session = s;
        wiz.peerHandle = s.peerHandle || wiz.peerHandle;
        if (s.state === 'done' || (s.state === 'paired' && s.peerHandle)) {
            poller.stop();
            ctx.announce(t('announcePaired', { server: serverName() }));
            go('choose');
        } else if (s.error) {
            showOutcome(s.error, { method: s.method, phase: s.phase, code: s.code, afterApproval: s.phase === 'awaitingConfirm', retryAfter: s.retryAfter, minutes: s.retryAfter ? Math.ceil(s.retryAfter / 60) : 1 });
        } else if (!before || before.state !== s.state || before.step !== s.step || (!!before.code !== !!s.code) || (!!before.safetyCode !== !!s.safetyCode) || !!before.peerSilent !== !!s.peerSilent) {
            if (s.state === 'awaitingConfirm' || s.state === 'approved') {
                ctx.announce(t('announceApproved', { server: serverName() }));
            }

            render();
        }
    }

    /** A stopped pairing read from the server record (8.2.9), not from the live session. */
    async function loadStored(handle) {
        try {
            const peer = await ctx.api.peer(handle);
            wiz.stored = peer;
            wiz.peerHandle = handle;
            wiz.form.name = peer.displayName;
            wiz.form.url = peer.url;
            wiz.form.network = peer.network || 'Tailnet';
            wiz.form.proxy = peer.proxy ? '' : '';
            wiz.form.method = 'invite';
            wiz.dir = 'watch';
            wiz.known = peer;
            if (peer.pairingError) {
                showOutcome(peer.pairingError, { method: peer.pairingMethod, phase: peer.pairingPhase, afterApproval: peer.pairingPhase === 'awaitingConfirm', stoppedUtc: peer.pairingStoppedUtc });
            } else if (peer.paired !== false && peer.status !== 'Pairing' && wiz.step === 'pair') {
                // The pairing finished and the server closed it: the server is paired, so go on to choosing libraries.
                ctx.announce(t('announcePaired', { server: serverName() }));
                go('choose');
            } else {
                go('connect');
            }
        } catch (err) {
            if (err.status === 404) {
                ctx.notFound();
                return;
            }

            replaceKids(host, loadErrorBox(() => init()));
        }
    }

    /* ----- watch: step 4 choose libraries ----- */

    function renderChoose() {
        const server = serverName();
        const wrap = el('div', { class: 'shoal-step__body' }, stepTitle(t('chooseTitle')), paragraph(t('chooseBody', { server })));
        const listHost = el('div', { class: 'shoal-libpick' }, el('p', { class: 'shoal-meta', text: t('loadingLibraries') }));
        // Never natively disabled: a disabled button swallows the click and the page looks dead. It is aria-disabled with the reason beside it, and a click says the reason again.
        const whyId = uid('shoal-why');
        const addBtn = button({ label: t('addAndSync'), variant: 'primary', describedby: whyId });
        addBtn.setAttribute('aria-disabled', 'true');
        const why = el('span', { class: 'shoal-meta shoal-step__why', id: whyId, text: t('pickAtLeastOne') });
        // Add errors show here, next to the button: the page alert at the top is out of sight when the list is long.
        const addError = el('div', { class: 'shoal-step__error', 'data-slot': 'adderror' });
        wrap.append(listHost, actions(addBtn, why), addError);

        function showAddError(err) {
            const noRoot = !!err && err.kind === 'noStubRoot';
            const kids = [el('p', { class: 'shoal-fieldError', role: 'alert' }, icon('x', true), el('span', { class: 'shoal-fieldError__text', text: noRoot ? t('noStubRootAdd') : err && err.status === 409 ? t('addConflict') : failureMessage(err, 'addFailed') }))];
            if (noRoot) {
                kids.push(button({ label: t('openSettings'), small: true, onClick: () => ctx.navigate('settings') }));
            }

            replaceKids(addError, kids);
        }
        const ticked = new Map();
        const required = new Set();
        const bigNames = new Map();

        /** Why Add cannot run yet, or an empty string when it can. */
        function blockedReason() {
            if (ticked.size === 0) {
                return t('pickAtLeastOne');
            }

            const open = Array.from(required).find((k) => !(ticked.has(k) && ticked.get(k).bigOk));
            return open ? t('confirmBigAdd', { library: bigNames.get(open) || '' }) : '';
        }

        function sync() {
            const reason = blockedReason();
            if (!addBtn.classList.contains('is-busy')) {
                if (reason) {
                    addBtn.setAttribute('aria-disabled', 'true');
                } else {
                    addBtn.removeAttribute('aria-disabled');
                }
            }

            why.textContent = reason;
            why.hidden = !reason;
            if (!reason && addError.dataset.kind === 'blocked') {
                clear(addError);
                delete addError.dataset.kind;
            }
        }

        async function build() {
            try {
                const [remote, libs] = await Promise.all([ctx.api.remoteLibraries(wiz.peerHandle), wiz.libs ? Promise.resolve(wiz.libs) : ctx.api.libraries()]);
                if (!ctx.alive) {
                    return;
                }

                wiz.libs = libs;
                wiz.remote = remote;
                clear(listHost);
                const youId = currentUserId(ctx, libs);
                const users = (libs.users || []).filter((u) => !u.isShareUser);
                let have = new Set();
                try {
                    const known = await ctx.api.peer(wiz.peerHandle);
                    have = new Set((known.libraries || []).map((m) => m.mappingKey));
                } catch {
                    /* the picker then offers everything and the server refuses a duplicate in words */
                }

                for (const lib of remote.filter((r) => !r.added && !have.has(r.mappingKey))) {
                    const block = el('div', { class: 'shoal-libpick__item' });
                    const detail = el('div', { class: 'shoal-libpick__detail', hidden: true });
                    const note = lib.selectable ? libCountNote(ctx, lib) : libLaterNote(lib.type);
                    const row = checkRow({
                        id: uid('shoal-rl'),
                        label: lib.name,
                        note,
                        disabled: !lib.selectable,
                        onChange: (checked) => {
                            detail.hidden = !checked;
                            if (checked) {
                                const entry = { mappingKey: lib.mappingKey, localLibraryName: t('defaultLocalName', { server, library: lib.name }), selectedUserIds: users.some((u) => u.id === youId) ? [youId] : [], ratingsAcknowledged: false, bigOk: lib.count <= 5000 };
                                ticked.set(lib.mappingKey, entry);
                                if (lib.count > 5000) {
                                    required.add(lib.mappingKey);
                                    bigNames.set(lib.mappingKey, lib.name);
                                }

                                buildDetail(lib, entry, detail, users, youId);
                            } else {
                                ticked.delete(lib.mappingKey);
                                required.delete(lib.mappingKey);
                            }

                            sync();
                        }
                    });
                    row.root.classList.add('shoal-libpick__check');
                    block.append(row.root, detail);
                    listHost.append(block);
                }
            } catch (err) {
                if (err && err.status === 404) {
                    // The server this pairing was for is gone (a pairing that was left behind): say so and give a way out.
                    replaceKids(listHost, [formHint(t('ghostPairing'), 'warn'), button({ label: t('startOver'), variant: 'primary', onClick: async () => {
                        try {
                            await ctx.api.cancelPairing();
                        } catch {
                            /* nothing to cancel */
                        }

                        restartWizard();
                    } })]);
                    return;
                }

                replaceKids(listHost, err && err.status === 502 ? [formHint(t('remoteUnreachable', { server }), 'warn'), loadErrorBox(() => build())] : loadErrorBox(() => build()));
            }
        }

        function buildDetail(lib, entry, detail, users, youId) {
            const nameField = textField({ id: uid('shoal-ln'), label: t('fLocalName'), value: entry.localLibraryName, maxlength: 64 });
            nameField.input.addEventListener('input', () => {
                entry.localLibraryName = nameField.input.value.trim();
            });
            const summaryNode = el('p', { class: 'shoal-libpick__access' });
            const refresh = () => replaceKids(summaryNode, accessSummary({ mode: 'Selected', selectedUserIds: entry.selectedUserIds }, users, youId));
            refresh();
            const pickBtn = button({
                label: t('choosePeople'),
                small: true,
                onClick: async () => {
                    const res = await openPicker(ctx, { displayName: server }, { localLibraryName: entry.localLibraryName, access: { mode: 'Selected', selectedUserIds: entry.selectedUserIds, includeRestricted: false } }, users, { youId });
                    if (res) {
                        entry.selectedUserIds = res.selectedUserIds;
                        entry.ratingsAcknowledged = res.ratingsAcknowledged;
                        refresh();
                    }
                }
            });
            const kids = [nameField.root, el('div', { class: 'shoal-libpick__who' }, el('span', { class: 'shoal-label', text: t('whoCanSee') }), summaryNode, pickBtn)];
            if (lib.count > 5000) {
                const gb = ctx.fmt.num(Math.round(lib.count * 0.2) / 1000);
                const big = checkRow({ id: uid('shoal-big'), label: t('addAll', { count: ctx.fmt.num(lib.count) }), note: t('addAllNote', { gb: (Math.round(lib.count * 0.2 / 100) / 10).toFixed(1) }), onChange: (c) => {
                    entry.bigOk = c;
                    sync();
                } });
                kids.push(big.root);
            }

            replaceKids(detail, kids);
        }

        addBtn.addEventListener('click', () => {
            const reason = blockedReason();
            if (reason) {
                replaceKids(addError, el('p', { class: 'shoal-fieldError', role: 'alert' }, icon('x', true), el('span', { class: 'shoal-fieldError__text', text: reason })));
                addError.dataset.kind = 'blocked';
                return null;
            }

            return runAction(ctx, addBtn, {
                busyText: t('adding'),
                run: () => {
                    clear(addError);
                    delete addError.dataset.kind;
                    return ctx.api.addLibrary(wiz.peerHandle, Array.from(ticked.values()).map((e) => ({ mappingKey: e.mappingKey, localLibraryName: e.localLibraryName, selectedUserIds: e.selectedUserIds, ratingsAcknowledged: e.ratingsAcknowledged })));
                },
                onDone: (result) => {
                    wiz.added = result || {};
                    wiz.step = 'done';
                    // The pairing is finished: close it on the server so "Pair a server" does not offer to resume it (N13).
                    closeFinishedPairing();
                    render();
                    focusStep();
                },
                onError: showAddError,
                afterBusy: sync
            });
        });
        build();
        return wrap;
    }

    function renderDone() {
        const server = serverName();
        const added = wiz.added || {};
        const wrap = el('div', { class: 'shoal-step__body shoal-done' }, markSvg(), stepTitle(t('pairedTitle', { server })));
        const progress = el('div', { class: 'shoal-progress' },
            el('p', { class: 'shoal-meta shoal-progress__text' }),
            el('div', { class: 'shoal-bar', role: 'progressbar', 'aria-label': t('addingTitles'), 'aria-valuemin': '0', 'aria-valuemax': '1', 'aria-valuenow': '0' }, el('span', { class: 'shoal-bar__fill' })));
        wrap.append(progress, paragraph(t('leavePage')));
        for (const lib of added.hiddenLibraries || []) {
            wrap.append(hint('warn', 'eyeoff', t('noOneCanSee', { library: lib })));
        }

        wrap.append(actions(
            button({ label: t('goToOverview'), variant: 'primary', onClick: () => ctx.navigate('overview') }),
            button({ label: t('openServer', { server }), onClick: () => ctx.navigate('server', { id: wiz.peerHandle }) })));
        const text = q(progress, '.shoal-progress__text');
        const bar = q(progress, '.shoal-bar');
        const fillNode = q(bar, '.shoal-bar__fill');
        const update = async () => {
            try {
                const peer = await ctx.api.peer(wiz.peerHandle);
                const sp = peer.syncProgress || { done: 0, total: 0 };
                text.textContent = sp.total > 0 ? t('addingProgress', { done: ctx.fmt.num(sp.done), total: ctx.fmt.num(sp.total) }) : '';
                bar.setAttribute('aria-valuemax', String(sp.total || 1));
                bar.setAttribute('aria-valuenow', String(sp.done || 0));
                setVar(fillNode, '--shoal-progress', Math.round(((sp.done || 0) / Math.max(1, sp.total || 1)) * 100) + '%');
                if (sp.total > 0 && sp.done >= sp.total) {
                    ctx.clearTimer(progressTimer);
                }
            } catch {
                text.textContent = '';
            }
        };
        progressTimer = ctx.every(update, PAIRING_POLL_MS);
        update();
        return wrap;
    }

    /* ----- share direction (ui-spec 8.2.4): invite-only; the friend's code is typed here, never shown ----- */

    function renderShareWho() {
        const f = wiz.shareForm;
        const wrap = el('div', { class: 'shoal-step__body' }, head(t('pv2ShWhoTitle'), 'you'));
        const nameField = textField({ id: uid('shoal-sw'), label: t('fFriendName'), help: t('fFriendNameHelp'), value: f.name, maxlength: 64, required: true });
        const watched = ((ctx.status && ctx.status.servers) || []).filter((s) => s && typeof s.displayName === 'string' && s.displayName).slice(0, 6);
        const prefill = watched.length
            ? el('div', { class: 'shoal-prefill' }, el('span', { class: 'shoal-meta', text: t('prefillFriend') }), watched.map((s) => button({ label: s.displayName, small: true, onClick: () => {
                nameField.input.value = s.displayName;
                nameField.input.focus();
            } })))
            : null;
        const libsHost = el('fieldset', { class: 'shoal-choices shoal-choices--checks' }, el('legend', { class: 'shoal-label', text: t('fLibrariesToShare') }), el('p', { class: 'shoal-meta', text: t('loadingLibraries') }));
        const limit = selectField({
            id: uid('shoal-sw'),
            label: t('fQualityLimit'),
            options: [{ value: '', label: t('limitNoLimit') }, { value: '40', label: t('limit40') }, { value: '20', label: t('limit20') }, { value: '10', label: t('limit10') }, { value: '4', label: t('limit4') }],
            value: f.limit
        });
        const transcodes = numberField({ id: uid('shoal-sw'), label: t('fTranscodes'), value: f.transcodes, min: 1, max: 10, help: t('fTranscodesHelp', { friend: f.name || t('yourFriend') }) });
        const next = button({ label: t('continue'), variant: 'primary', type: 'submit' });
        const form = el('form', { class: 'shoal-form', novalidate: true },
            nameField.root, prefill, libsHost, limit.root, transcodes.root,
            el('div', { class: 'shoal-hint shoal-hint--info', role: 'note' }, icon('info', true), el('div', { class: 'shoal-hint__body', text: t('pv2ShNote', { friend: f.name || t('yourFriend') }) })),
            actions(next));
        wrap.append(form, codesCard(ctx, 'share', null, false));

        ctx.api.shares().then((data) => {
            if (!ctx.alive) {
                return;
            }

            clear(libsHost);
            libsHost.append(el('legend', { class: 'shoal-label', text: t('fLibrariesToShare') }));
            for (const lib of data.availableLibraries || []) {
                const fromFriend = lib.note === 'fromFriend' || lib.reason === 'federated';
                const selectable = lib.selectable !== false && lib.shareable !== false;
                const row = checkRow({
                    id: uid('shoal-sl'),
                    label: lib.name,
                    note: fromFriend ? t('libFromFriend') : selectable ? (typeof lib.count === 'number' ? t('libMeta', { type: t('type_' + lib.type), count: ctx.fmt.num(lib.count), n: lib.count }) : null) : libLaterNote(lib.type),
                    disabled: !selectable,
                    checked: f.libraries.has(lib.id),
                    onChange: (checked) => (checked ? f.libraries.add(lib.id) : f.libraries.delete(lib.id))
                });
                libsHost.append(row.root);
            }
        }).catch(() => {
            replaceKids(libsHost, loadErrorBox(() => render()));
        });

        form.addEventListener('submit', async (ev) => {
            ev.preventDefault();
            f.name = nameField.value();
            f.limit = limit.select.value;
            f.transcodes = Number(transcodes.input.value);
            nameField.setError(f.name ? '' : t('errEnterFriend'));
            const okT = Number.isInteger(f.transcodes) && f.transcodes >= 1 && f.transcodes <= 10;
            transcodes.setError(okT ? '' : t('errWholeNumber', { min: 1, max: 10 }));
            if (!f.name) {
                nameField.input.focus();
                return;
            }

            if (!okT) {
                transcodes.input.focus();
                return;
            }

            if (f.libraries.size === 0) {
                ctx.alertMessage(t('pickAtLeastOne'));
                return;
            }

            await runAction(ctx, next, {
                busyText: t('saving'),
                run: () => ctx.api.createShare({ friendName: f.name, libraryIds: Array.from(f.libraries), maxBitrateMbps: f.limit ? Number(f.limit) : null, maxConcurrentTranscodes: f.transcodes }),
                onDone: (share) => {
                    wiz.share = Object.assign({ handle: share.handle, name: f.name, libraryIds: Array.from(f.libraries) }, share);
                    go('reach');
                },
                onError: (err) => ctx.alertMessage(failureMessage(err, 'saveFailed'))
            });
        });
        return wrap;
    }

    /* ----- share: step 2, how your friend reaches you, with the address to send (pairing v2 9.2) ----- */

    const TRANSPORT_NETWORK = { tailnet: 'Tailnet', home: 'LAN', public: 'Public' };

    /** Shoal's best guess for the address to send, from GET Shares/Reach. Returns '' when there is none or the field is missing. */
    function guessAddress(transport) {
        const r = wiz.reach;
        if (!r || typeof r !== 'object') {
            return '';
        }

        if (transport === 'tailnet' && r.tailscale && r.tailscale.dnsName) {
            return 'http://' + String(r.tailscale.dnsName).replace(/\.$/, '') + ':8096';
        }

        if (transport === 'public' && r.https && r.https.url) {
            return String(r.https.url);
        }

        if (transport === 'home') {
            // Never the container's own address: the one the admin used to open this page, else a listed one that matches it.
            const here = window.location.hostname;
            const urls = Array.isArray(r.lan) ? r.lan.map((x) => String(x.url || '')).filter(Boolean) : [];
            const match = urls.find((u) => hostOf(u) === here);
            if (match) {
                return match;
            }

            return isPrivateHost(here) ? window.location.origin : '';
        }

        return '';
    }

    function renderShareReach() {
        const f = wiz.shareForm;
        const share = wiz.share;
        const friend = share.name || share.friendName;
        const wrap = el('div', { class: 'shoal-step__body' }, head(t('pv2ShReachTitle', { friend }), 'you'));
        const name = 'shoal-transport-' + ctx.instance;
        const errorHost = el('div', { class: 'shoal-formerror' });
        const hintHost = el('div', { class: 'shoal-addrhints', 'aria-live': 'polite' });
        const address = textField({ id: uid('shoal-sa'), label: t('pv2ShAddrLabel'), help: t('pv2ShAddrHelpEmpty', { friend }), value: f.address || '', type: 'url', inputmode: 'url', autocapitalize: 'off', spellcheck: false, required: true, mono: true });
        const makeBtn = button({ label: t('createInvite'), variant: 'primary', disabled: !f.transport, onClick: (ev, btn) => create(btn) });
        const why = el('span', { class: 'shoal-meta shoal-step__why', text: t('transportWhy'), hidden: !!f.transport });
        const readAddr = () => {
            clear(hintHost);
            const r = readAddress(address.input.value, null);
            if (f.transport === 'home' && /^http:/i.test(address.input.value.trim())) {
                hintHost.append(hint('warn', 'warn', t('pv2ShHomeHttp', { friend })));
            }

            if (r) {
                if (r.trimmed) {
                    address.input.value = r.trimmed;
                }

                hintHost.append(...addressHints(shareWords(r, friend), (fixed) => {
                    address.input.value = fixed;
                    readAddr();
                }));
            }
        };
        const fillGuess = () => {
            if (f.addressEdited && address.input.value) {
                return;
            }

            const guess = guessAddress(f.transport);
            address.input.value = guess;
            address.setHelp(guess ? t('pv2ShAddrHelp', { friend }) : t('pv2ShAddrHelpEmpty', { friend }));
            readAddr();
        };
        const pickT = (v) => {
            f.transport = v;
            makeBtn.disabled = false;
            why.hidden = true;
            fillGuess();
        };
        const choose = (value, titleKey, hintKey) => choiceCard({ name, value, title: t(titleKey, { friend }), body: t(hintKey, { friend }), checked: f.transport === value, onChange: pickT }).root;
        const helper = addressHelper({
            share: true,
            friend,
            onUse: (network, focusAddress) => {
                const value = network === 'Tailnet' ? 'tailnet' : network === 'LAN' ? 'home' : 'public';
                const input = q(wrap, `input[type="radio"][value="${value}"]`);
                if (input) {
                    input.checked = true;
                    pickT(value);
                }

                if (focusAddress) {
                    address.input.focus();
                }
            }
        });
        address.input.addEventListener('input', () => {
            f.addressEdited = true;
            readAddr();
        });
        address.input.addEventListener('blur', readAddr);
        const reachSet = el('fieldset', { class: 'shoal-choices' },
            choose('tailnet', 'pv2ShTs', 'pv2ShTsHint'),
            choose('public', 'pv2ShPublic', 'pv2ShPublicHint'),
            choose('home', 'pv2ShHome', 'pv2ShHomeHint'));
        labelByHeading(wrap, reachSet);
        wrap.append(
            reachSet,
            address.root,
            hintHost,
            helper,
            sayBox(ctx, t('pv2ShSayMaking'), t('pv2SayLeadTo', { friend })),
            errorHost,
            actions(makeBtn, button({ label: t('back'), onClick: () => go('who') }), why));
        if (f.address) {
            readAddr();
        } else if (f.transport) {
            fillGuess();
        }

        // The suggestions (Shoal's own address from Tailscale and its configuration) arrive when the server knows them; without them the field is empty.
        if (!wiz.reachLoaded) {
            wiz.reachLoaded = true;
            ctx.api.shareReach().then((r) => {
                wiz.reach = r;
                if (ctx.alive && wiz.step === 'reach' && f.transport) {
                    fillGuess();
                }
            }).catch(() => {
                wiz.reach = null;
            });
        }

        async function create(btn) {
            clear(errorHost);
            address.setError('');
            const value = address.value();
            f.address = value;
            if (!value) {
                address.setError(t('errEnterAddress'));
                address.input.focus();
                return;
            }

            if (!/^https?:\/\//i.test(value)) {
                address.setError(t('pv2AddrNoScheme'));
                address.input.focus();
                return;
            }

            await runAction(ctx, btn, {
                busyText: t('creatingInvite'),
                run: () => ctx.api.createInvite(share.handle),
                onDone: (inv) => {
                    wiz.invite = inv;
                    wiz.shareOutcome = null;
                    go('wait');
                    poller.start();
                },
                onError: (err) => ctx.alertMessage(failureMessage(err))
            });
        }

        return wrap;
    }

    /* ----- share: step 3, send the invite and wait (pairing v2 9.3) ----- */

    function shareMessage(invite) {
        return t('pv2ShMessage', { address: wiz.shareForm.address || '', invite });
    }

    function renderShareWait() {
        const share = wiz.share;
        const friend = share.name || share.friendName;
        const wrap = el('div', { class: 'shoal-step__body' }, head(t('pv2ShSendTitle', { friend }), 'you'));
        const host2 = el('div', { class: 'shoal-invite' });
        wrap.append(host2);

        async function create(btn) {
            await runAction(ctx, btn, {
                busyText: t('creatingInvite'),
                run: () => ctx.api.createInvite(share.handle),
                onDone: (inv) => {
                    wiz.invite = inv;
                    draw();
                    poller.start();
                },
                onError: (err) => ctx.alertMessage(failureMessage(err))
            });
        }

        function draw() {
            const invite = wiz.invite;
            clear(host2);
            if (countdownRef) {
                countdownRef.stop();
                countdownRef = null;
            }

            if (!invite || !invite.invite) {
                return;
            }

            const message = shareMessage(invite.invite);
            const box = el('textarea', { class: 'emby-textarea shoal-input shoal-message', readonly: true, rows: '9', 'aria-label': t('pv2ShMessageLabel') });
            box.value = message;
            host2.append(
                inviteBox(invite.invite),
                el('div', { class: 'shoal-inline' }, copyButton(ctx, invite.invite, 'copyInvite', 'announceInviteCopied')),
                countdownNode(invite.expiresUtc, () => {
                    wiz.invite = null;
                    ctx.announce(t('announceInviteExpired'));
                    poller.stop();
                    wiz.shareOutcome = { kind: 'inviteExpired' };
                    render();
                    focusStep();
                }, 'shareInvite', { friend }),
                el('div', { class: 'shoal-hint shoal-hint--warn', role: 'note' }, icon('warn', true), el('div', { class: 'shoal-hint__body' },
                    el('p', { text: t('pv2ShSecretWarn', { friend }) }), el('p', { text: t('inviteShownOnce') }))),
                box,
                el('div', { class: 'shoal-inline' }, copyButton(ctx, message, 'pv2CopyMessage', 'pv2Copied')),
                sayBox(ctx, t('pv2ShSendSay'), t('pv2SayLeadTo', { friend })),
                statusRow('info', 'clock', t('waitingForUse', { friend }), 'shoal-pairwait'),
                el('div', { class: 'shoal-inline' },
                    button({ label: t('newInvite'), onClick: (ev, btn) => create(btn) }),
                    linkButton(t('cancelInvite'), async () => {
                        try {
                            await ctx.api.cancelInvite(share.handle);
                        } catch {
                            /* it expires on its own */
                        }

                        wiz.invite = null;
                        poller.stop();
                        go('reach');
                    })));
        }

        draw();
        return wrap;
    }

    async function pollApproval() {
        if (wiz.step !== 'wait' && wiz.step !== 'approve') {
            return;
        }

        if (!wiz.share) {
            return;
        }

        try {
            const a = await ctx.api.approval(wiz.share.handle);
            if (!ctx.alive) {
                return;
            }

            const prev = wiz.approval;
            wiz.approval = a;
            if (a.state === 'pending' && wiz.step === 'wait') {
                ctx.announce(t('announceApprovalWaiting', { server: a.homeName, friend: wiz.share.name }));
                go('approve', true);
            } else if (wiz.step === 'approve' && a.state === 'pending' && wiz.approvalCard) {
                wiz.approvalCard.update(a);
            } else if (a.state === 'collected' && wiz.step === 'approve') {
                poller.stop();
                go('done');
            } else if (a.state === 'uncollected') {
                poller.stop();
                wiz.shareOutcome = { kind: 'uncollected' };
                render();
                focusStep();
            } else if (a.state === 'cancelledWrongCode' && !(prev && prev.state === 'cancelledWrongCode')) {
                poller.stop();
                wiz.shareOutcome = { kind: 'cancelledWrongCode' };
                render();
                focusStep();
            }
        } catch {
            /* keep waiting */
        }
    }

    function renderShareApprove() {
        const a = wiz.approval;
        const share = wiz.share;
        const friend = share.name || share.friendName;
        const wrap = el('div', { class: 'shoal-step__body' });
        if (!a || a.state === 'none') {
            wrap.append(head(t('approvalNoneTitle'), 'you'), paragraph(t('approvalNone')), actions(button({ label: t('back'), onClick: () => go('wait') })));
            return wrap;
        }

        if (a.state === 'approved') {
            wrap.append(head(t('approvedTitle'), 'wait'), statusRow('info', 'clock', t('approvedWaiting', { home: a.homeName }), 'shoal-pairwait'));
            poller.start();
            return wrap;
        }

        const card = approvalCard(ctx, {
            friend,
            approval: a,
            reconnect: (share.deviceCount || 0) > 0,
            onApprove: (sas) => ctx.api.decideApproval(share.handle, true, sas),
            onDeny: () => ctx.api.decideApproval(share.handle, false),
            onResolved: (r) => {
                if (r.state === 'approved') {
                    ctx.announce(t('announceApprovedShare', { friend, home: a.homeName }));
                    wiz.approval = Object.assign({}, a, { state: 'approved' });
                    go('approve');
                } else if (r.state === 'cancelledWrongCode') {
                    wiz.shareOutcome = { kind: 'cancelledWrongCode' };
                    poller.stop();
                    render();
                    focusStep();
                } else {
                    ctx.announce(t('announceDenied'));
                    wiz.shareOutcome = { kind: 'denied' };
                    poller.stop();
                    render();
                    focusStep();
                }
            }
        });
        wiz.approvalCard = card;
        poller.start();
        wrap.append(head(t('pv2ShApproveTitle', { friend }), 'you'), card.root);
        return wrap;
    }

    /** A stopped share pairing as a recovery card (3.5, 9.4, 9.5): who acts, one primary button, what was kept. */
    function renderShareOutcome() {
        const o = wiz.shareOutcome;
        const share = wiz.share;
        const friend = share.name || share.friendName;
        const home = (wiz.approval && wiz.approval.homeName) || '';
        const table = {
            inviteExpired: { icon: 'clock', tone: 'warn', who: 'you', title: t('pv2ShRanOutTitle'), body: t('pv2ShRanOutBody', { friend }), label: t('pv2ShMakeNew') },
            cancelledWrongCode: { icon: 'x', tone: 'off', who: 'you', title: t('shareCancelledTitle'), body: t('shareCancelledBody', { friend }), label: t('pv2ShCreateNew') },
            denied: { icon: 'x', tone: 'off', who: 'friend', title: t('shareDeniedTitle'), body: t('shareDeniedBody'), label: t('pv2ShCreateNew') },
            uncollected: { icon: 'x', tone: 'bad', who: 'friend', title: t('shareUncollectedTitle', { home }), body: t('shareUncollectedBody'), label: t('pv2ShCreateNew') }
        };
        const d = table[o.kind];
        return el('section', { class: 'shoal-panel shoal-step shoal-outcome shoal-recover shoal-outcome--' + d.tone, role: 'alert' },
            el('div', { class: 'shoal-outcome__icon shoal-tone--' + d.tone }, icon(d.icon)),
            stepTitle(d.title),
            paragraph(d.body, 'shoal-lead'),
            el('div', { class: 'shoal-whos' }, whoLabel(d.who)),
            actions(button({ label: d.label, variant: 'primary', onClick: () => {
                wiz.shareOutcome = null;
                wiz.invite = null;
                wiz.approval = null;
                go('reach');
            } })),
            el('p', { class: 'shoal-meta shoal-recover__kept', text: t('pv2Kept', { what: t('pv2KeptShare', { friend }) }) }));
    }

    function renderShareDone() {
        const share = wiz.share;
        const friend = share.name || share.friendName;
        const wrap = el('div', { class: 'shoal-step__body shoal-done' }, markSvg(), stepTitle(t('shareDoneTitle', { friend })), paragraph(t('shareDoneBody', { k: (share.libraryIds || []).length, n: (share.libraryIds || []).length, friend })));
        wrap.append(actions(
            button({ label: t('goToOverview'), variant: 'primary', onClick: () => ctx.navigate('overview') }),
            button({ label: t('openFriend', { friend }), onClick: () => ctx.navigate('friend', { id: share.handle }) })));
        return wrap;
    }

    /* ---------- lifecycle ---------- */

    async function init(forceResume) {
        if (ctx.params.fresh) {
            try {
                await ctx.api.cancelPairing();
            } catch {
                /* nothing to cancel */
            }
        }

        const handle = ctx.params.id || ctx.params.again;
        if (handle) {
            // A stopped pairing, or "Pair again": the server's own record is the source of the form.
            try {
                const peer = await ctx.api.peer(handle);
                if (!ctx.alive) {
                    return;
                }

                wiz.known = peer;
                wiz.peerHandle = handle;
                wiz.dir = 'watch';
                wiz.form.name = peer.displayName;
                wiz.form.url = peer.url;
                wiz.form.network = peer.network || 'Tailnet';
                wiz.form.method = 'invite';
                if (ctx.params.id && peer.pairingError) {
                    wiz.stored = peer;
                    wiz.outcome = { kind: peer.pairingError, method: peer.pairingMethod, phase: peer.pairingPhase, afterApproval: peer.pairingPhase === 'awaitingConfirm', stoppedUtc: peer.pairingStoppedUtc };
                    render();
                    focusStep();
                    return;
                }

                wiz.step = 'connect';
                render();
                focusStep();
                return;
            } catch (err) {
                if (err.status === 404) {
                    ctx.notFound();
                    return;
                }

                replaceKids(host, loadErrorBox(() => init()));
                return;
            }
        }

        if (wiz.dir !== 'share') {
            let existing = null;
            try {
                existing = await ctx.api.pairing();
            } catch {
                existing = null;
            }

            if (existing && existing.direction === 'watch' && existing.state !== 'aborted' && existing.step === 'confirmed' && existing.peerHandle) {
                // A pairing that is confirmed and whose server already has libraries is finished: close it and start fresh (N13).
                try {
                    const known = await ctx.api.peer(existing.peerHandle);
                    if ((known.libraries || []).length > 0) {
                        await ctx.api.cancelPairing();
                        existing = null;
                    }
                } catch {
                    /* keep the pairing as it is */
                }
            }

            if (existing && existing.direction === 'watch') {
                wiz.session = existing;
                wiz.dir = 'watch';
                wiz.peerHandle = existing.peerHandle || null;
                wiz.form.method = existing.method;
                // The session names its server by handle only; the name is read from the server's own record.
                wiz.form.name = existing.displayName || '';
                if (!wiz.form.name && existing.peerHandle) {
                    try {
                        const known = await ctx.api.peer(existing.peerHandle);
                        wiz.form.name = known.displayName || '';
                        wiz.form.url = known.url || wiz.form.url;
                        wiz.form.network = known.network || wiz.form.network;
                    } catch {
                        /* the screen falls back to "this server" */
                    }
                }

                if (existing.error) {
                    wiz.outcome = { kind: existing.error, method: existing.method, phase: existing.phase, code: existing.code };
                } else {
                    wiz.resume = !forceResume;
                    wiz.step = existing.state === 'done' ? 'choose' : 'pair';
                }
            }
        }

        if (!ctx.alive) {
            return;
        }

        if (wiz.dir && !wiz.session) {
            wiz.step = wiz.dir === 'share' ? 'who' : 'connect';
        }

        render();
        focusStep();
    }

    return {
        show() {
            if (!started) {
                started = true;
                init();
                return;
            }

            // Coming back to the page: an old red error and the old address do not belong to a new visit. A running pairing is kept (it lives on the server);
            // one the server no longer has is dropped.
            if (wiz.step === 'done' || (!wiz.session && !wiz.peerHandle)) {
                restartWizard();
            } else if (wiz.session && wiz.step !== 'done') {
                ctx.api.pairing().then((now) => {
                    if (!now && ctx.alive && wiz.step !== 'done') {
                        restartWizard();
                    }
                }).catch(() => {});
            }
        },
        hide() {
            poller.stop();
        },
        /** Exposed for tests: one poll right now, and the wizard state. */
        poll: () => poll(),
        state: () => wiz,
        destroy() {
            poller.stop();
            stopTimers();
        }
    };
}

/* ====================================================================================== */
/* Screen: Server detail (ui-spec 8.3, DESIGN 9.3)                                        */
/* ====================================================================================== */

function serverBanner(ctx, s, model) {
    const img = el('img', { class: 'shoal-hero__img', alt: '', width: '1240', height: '160', loading: 'lazy', decoding: 'async', hidden: true });
    const ph = el('div', { class: 'shoal-hero__ph' }, icon('server'), el('span', { class: 'shoal-meta', text: t('noPictures') }));
    const l = s.latest;
    const chain = l && l.localItemId ? [
        l.backdropTag ? { url: itemImageUrl(ctx, l.localItemId, 'Backdrop', l.backdropTag, 1240, 80), cls: 'backdrop' } : null,
        l.primaryTag ? { url: itemImageUrl(ctx, l.localItemId, 'Primary', l.primaryTag, 1240, 80), cls: 'primary' } : null
    ].filter(Boolean) : [];
    if (chain.length) {
        loadImageChain(img, chain, () => {
            ph.hidden = false;
        });
        ph.hidden = true;
    }

    const pill = el('span', { class: 'shoal-pill' });
    if (model.key === 'offline' || model.key === 'paused') {
        pill.hidden = true; // the chip in the header already says it
    } else if (l && l.title) {
        pill.append(icon('play', true), el('span', { class: 'shoal-pill__text', text: t('pillLatest', { server: s.displayName, title: l.title }) }));
    } else {
        pill.hidden = true;
    }

    return el('div', { class: 'shoal-hero' + (model.dim ? ' is-dim' : '') }, img, ph, pill);
}

/**
 * "Which apps can play {Server}'s titles" (ui-spec 8.3.12). One block, written once, rendered under Playing now
 * and in Settings. It explains, it doesn't warn: no hint box, no alert count. The lists are fixed text.
 * Ships only when the owner has confirmed G1 and Warden has read the help text (ctx.features.appsWork).
 */
function appsWorkBlock(ctx, serverName) {
    const named = !!serverName;
    const vars = { server: serverName || t('aFriendsServer') };
    const list = (keys, iconName, tone) => el('ul', { class: 'shoal-appslist' }, keys.map((k) => el('li', { class: 'shoal-appslist__item' },
        el('span', { class: 'shoal-tone--' + tone }, icon(iconName, true)), el('span', { text: t(k) }))));
    const verified = APPS_ROWS.filter((r) => APPS_VERIFIED[r.id]).map((r) => r.key);
    const should = APPS_ROWS.filter((r) => !APPS_VERIFIED[r.id]).map((r) => r.key);
    const content = el('div', { class: 'shoal-apps' },
        el('p', { class: 'shoal-lead', text: t('appsLead', vars) }),
        verified.length ? el('h3', { class: 'shoal-label', text: t('appsWorks') }) : null,
        verified.length ? list(verified, 'check', 'ok') : null,
        should.length ? el('h3', { class: 'shoal-label', text: t('appsShould') }) : null,
        should.length ? list(should, 'info', 'muted') : null,
        should.length ? el('p', { class: 'shoal-meta', text: t('appsShouldNote') }) : null,
        el('h3', { class: 'shoal-label', text: t('appsDont') }),
        list(['appsNot1', 'appsNot2', 'appsNot3', 'appsNot4'], 'x', 'off'),
        el('p', { class: 'shoal-meta shoal-apps__why', text: t('appsWhy', vars) }),
        el('p', { class: 'shoal-meta', text: t('appsOffline') }),
        el('p', { class: 'shoal-meta' }, el('strong', { text: t('appsWhatLead') + ' ' }), t('appsWhatBody')));
    return collapse(t(named ? 'appsSummary' : 'appsSummaryAll', vars), content);
}

function joinAnd(items) {
    if (items.length < 2) {
        return items.join('');
    }

    return t('namesAnd', { names: items.slice(0, -1).join(', '), last: items[items.length - 1] });
}

function serverDetailScreen(ctx) {
    const view = q(ctx.root, '[data-shoal-view="server"]');
    const body = slot(view, 'body');
    const handle = ctx.params.id;
    const h1Id = uid('shoal-h1');
    view.setAttribute('aria-labelledby', h1Id);
    const st = { data: null, loaded: false, speed: null, alertsNode: null, certTitle: null, focused: false, sig: '', rendered: false, rotationNode: null };
    const poller = createPoller(ctx, () => load(false), STATUS_POLL_MS);

    async function act(btn, o) {
        return runAction(ctx, btn, {
            busyText: o.busy,
            cooldownKey: o.cooldownKey,
            run: o.run,
            onDone: (r) => {
                if (o.announce) {
                    ctx.announce(o.announce);
                    ctx.toast(o.announce);
                }

                if (o.after) {
                    o.after(r);
                }

                load(false, true);
            },
            onError: (err) => {
                if (err.status === 429 && o.cooldownKey) {
                    ctx.setCooldown(o.cooldownKey, err.retryAfter || 60);
                    ctx.alertMessage(t('availableIn', { n: ctx.cooldownMinutes(o.cooldownKey) || 1 }));
                } else {
                    ctx.fail(o.errorText ? o.errorText(err) : failureMessage(err));
                }
            }
        });
    }

    function homeName() {
        return (ctx.status && ctx.status.homeName) || t('thisServerName');
    }

    // The friend who also watches this server, when the two records are linked (display only).
    function alsoWatching() {
        return st.data && st.data.link && st.data.link.share ? st.data.link : null;
    }

    function removeDialog(s) {
        return async () => {
            const libs = (st.data.libraries || []).map((l) => l.localLibraryName);
            const entries = st.data.usage ? st.data.usage.stubs : s.titleCount;
            const res = await confirmDialog(ctx, {
                title: t('removeTitle', { server: s.displayName }),
                items: [t('removeItem1', { server: s.displayName }), t('removeItem2', { server: s.displayName }), t('removeItem3', { server: s.displayName })],
                after: [
                    el('div', { class: 'shoal-hint shoal-hint--info', role: 'note' }, icon('info', true), el('div', { class: 'shoal-hint__body', text: t('removeNoteInvite') })),
                    alsoWatching() ? el('div', { class: 'shoal-hint shoal-hint--info', role: 'note' }, icon('info', true), el('div', { class: 'shoal-hint__body', text: t('removeStillWatches', { name: s.displayName }) })) : null
                ],
                checkbox: libs.length ? { label: t('removeAlsoDelete', { library: libs[0], count: ctx.fmt.num(entries), n: entries }), checked: true, whenUnchecked: t('removeKeepsLibrary') } : null,
                typed: { label: t('typeToConfirm', { text: s.displayName }), expected: s.displayName },
                confirmLabel: t('removeConfirm'),
                danger: true,
                run: (state) => ctx.api.removePeer(s.handle, state.checked, s.displayName),
                errorText: (err) => (err && err.kind === 'syncing' ? t('removeBlockedSyncing', { server: s.displayName }) : err && err.kind === 'pairingActive' ? t('removeBlockedPairing', { server: s.displayName }) : failureMessage(err, 'removeFailed'))
            });
            if (!res.confirmed) {
                return;
            }

            ctx.announce(t('announceRemoved', { server: s.displayName }));
            ctx.navigate('overview');
        };
    }

    function pairAgainGo(s) {
        ctx.navigate('pair', { dir: 'watch', again: s.handle });
    }

    /* ---------- header (8.3.1) ---------- */

    function checkItem(ok, okKey, askKey, vars) {
        return el('span', { class: 'shoal-detail__metaitem shoal-tone--' + (ok ? 'ok' : 'warn') }, icon(ok ? 'lock' : 'warn', true), t(ok ? okKey : askKey, vars));
    }

    function headerPanel(s, model) {
        const via = viaOf(s);
        const d = st.data;
        const nameRow = el('div', { class: 'shoal-detail__namerow' },
            el('h1', { class: 'shoal-h1', id: h1Id, tabindex: '-1', text: s.displayName }),
            chip(model.tone, model.icon, model.chipText));
        const metaItems = [
            el('span', { class: 'shoal-detail__metaitem' }, icon(via.icon, true), via.text, via.host ? ' · ' : '', via.host ? el('span', { class: 'shoal-card__host', text: via.host }) : null, via.proxy),
            el('span', { class: 'shoal-detail__metaitem' }, t('serverIdLabel'), ' ', el('span', { class: 'shoal-mono', text: d.serverIdPrefix || '—' })),
            d.pairedUtc ? el('span', { class: 'shoal-detail__metaitem', text: t(d.pairingMethod === 'qc' ? 'pairedByQc' : 'pairedByInvite', { date: ctx.fmt.date(d.pairedUtc) }) }) : null
        ];
        const meta = el('div', { class: 'shoal-detail__meta shoal-meta' }, metaItems);
        const extras = [];
        if (s.repairBlockedByHttp === true) {
            extras.push(hint('warn', 'keyoff', el('strong', { text: t('repairBlockedTitle') }), el('span', { class: 'shoal-hint__line', text: t('repairBlockedBody', { server: s.displayName }) }), el('span', { class: 'shoal-hint__line', text: t('changeAddressSteps') })));
        } else if (d.pairingMethod === 'qc') {
            extras.push(hint('warn', 'keyoff', el('strong', { text: t('needsRepairTitle') }), el('span', { class: 'shoal-hint__line', text: t('needsRepairBody', { server: s.displayName }) })));
        }

        const problem = model.key === 'accessRemoved' || model.key === 'differentServer';
        const certProblem = model.key === 'certChanged';
        const syncNote = el('span', { class: 'shoal-meta shoal-action__note', id: uid('shoal-note'), hidden: true });
        const syncBtn = button({ label: t('syncNow'), icon: 'sync', onClick: (ev, btn) => act(btn, { busy: t('syncing'), cooldownKey: 'peersync:' + s.handle, run: () => ctx.api.syncPeer(s.handle), announce: t('announceSyncStarted') }) });
        const testNote = el('span', { class: 'shoal-meta shoal-action__note', id: uid('shoal-note'), hidden: true });
        const testBtn = button({ label: t('testLink'), onClick: (ev, btn) => act(btn, { busy: t('testing'), cooldownKey: 'test:' + s.handle, run: () => ctx.api.speedTest(s.handle, false), after: () => ctx.later(() => st.speed && st.speed.load(true), 1500) }) });
        // With the keys locked every action that needs a key is replaced by Pair again (ui-spec 8.3.9, 8.8).
        const locked = !!(ctx.status && ctx.status.secretsUnreadable);
        const pairAgain = button({ label: t('pairAgain'), variant: problem || locked ? 'primary' : 'quiet', onClick: () => pairAgainGo(s) });
        const paused = model.key === 'paused';
        const pauseNote = el('span', { class: 'shoal-meta shoal-action__note', id: uid('shoal-note'), text: t('pausePeerHelp') + (alsoWatching() ? ' ' + t('pauseStillWatches', { name: s.displayName }) : '') });
        const pauseBtn = button({
            label: t(paused ? 'resume' : 'pause'),
            variant: paused ? 'primary' : 'quiet',
            describedby: paused ? null : pauseNote.id,
            onClick: (ev, btn) => act(btn, { busy: t(paused ? 'resuming' : 'pausing'), run: () => (paused ? ctx.api.resumePeer(s.handle) : ctx.api.pausePeer(s.handle)), announce: t(paused ? 'announceResumed' : 'announcePausedLong', { server: s.displayName }) })
        });
        const blocked = s.repairBlockedByHttp === true;
        const removeBtn = button({ label: t('removeServerEllipsis'), variant: blocked ? 'primary' : 'quiet', onClick: removeDialog(s) });
        const retry = model.key === 'offline'
            ? button({ label: t('retryNow'), variant: 'primary', onClick: (ev, btn) => act(btn, { busy: t('retrying'), cooldownKey: 'test:' + s.handle, run: () => ctx.api.speedTest(s.handle, false), announce: t('announceRetryStarted') }) })
            : null;
        const checkCert = certProblem
            ? button({ label: t('checkCertificate'), variant: 'primary', onClick: () => {
                if (st.certTitle) {
                    st.certTitle.focus();
                    scrollToNode(st.certTitle);
                }
            } })
            : null;
        // The fix for the state comes first, as the one primary; the quiet order after it is Sync now, Test link, Pause, Pair again, Remove.
        const needsRepair = d.pairingMethod === 'qc' || s.needsRepair === true;
        const repairBtn = needsRepair && !blocked ? button({ label: t('repairWithInvite'), variant: 'primary', onClick: () => pairAgainGo(s) }) : null;
        const fix = repairBtn || (blocked ? removeBtn : locked ? pairAgain : retry || checkCert || (paused ? pauseBtn : problem ? pairAgain : null));
        const rest = [];
        if (!locked && !needsRepair) {
            rest.push(el('span', { class: 'shoal-action' }, syncBtn, syncNote), el('span', { class: 'shoal-action' }, testBtn, testNote));
            if (fix !== pauseBtn) {
                rest.push(el('span', { class: 'shoal-action' }, pauseBtn, paused ? null : pauseNote));
            }
        }

        if (fix !== pairAgain && !needsRepair) {
            rest.push(pairAgain);
        }

        if (!blocked) {
            rest.push(removeBtn);
        }

        const actionsNode = el('div', { class: 'shoal-detail__actions' }, fix, rest);
        cooldownNote(ctx, 'peersync:' + s.handle, syncNote, syncBtn);
        cooldownNote(ctx, 'test:' + s.handle, testNote, testBtn);
        return el('section', { class: 'shoal-panel shoal-detail__header' },
            serverBanner(ctx, s, model),
            el('div', { class: 'shoal-detail__head' }, el('div', { class: 'shoal-detail__title' }, nameRow, meta, extras), actionsNode));
    }

    /* ---------- alerts (8.3.2) ---------- */

    function alertBox(tone, title, bodyText, buttons, extra) {
        const h3 = el('h3', { class: 'shoal-hint__title', tabindex: '-1', text: title });
        const box = el('div', { class: 'shoal-alert shoal-hint shoal-hint--' + tone, role: 'note' },
            icon(tone === 'bad' ? 'shield' : 'warn', true),
            el('div', { class: 'shoal-hint__body' },
                h3,
                bodyText ? el('p', { text: bodyText }) : null,
                extra || null,
                buttons && buttons.length ? el('div', { class: 'shoal-hint__actions' }, buttons) : null));
        box.titleNode = h3;
        return box;
    }

    function alertsFor(s) {
        const out = [];
        const sv = s.displayName;
        const quotaGb = st.data.usage ? Math.round(st.data.usage.quotaBytes / 1e9) : 0;
        const settingsLink = () => button({ label: t('openSettings'), small: true, onClick: () => ctx.navigate('settings') });
        const rank = (a) => (a.severity === 'bad' ? 0 : 1);
        const alerts = (st.data.alerts || []).map((a, i) => ({ a, i })).sort((x, y) => rank(x.a) - rank(y.a) || x.i - y.i).map((x) => x.a);
        for (const a of alerts) {
            const before = out.length;
            switch (a.kind) {
                case 'heldRemovals':
                    out.push(alertBox('warn', t('alertRemovalTitle', { n: a.count, count: ctx.fmt.num(a.count) }), t('alertRemovalBody', { server: sv, n: a.count, count: ctx.fmt.num(a.count), examples: (a.examples || []).join(', ') }), [
                        button({ label: t('removeThemNow'), variant: 'primary', onClick: (ev, btn) => act(btn, { busy: t('applying'), run: () => ctx.api.hold(s.handle, 'removals', 'Release'), announce: t('announceHoldReleased') }) }),
                        button({ label: t('keepForNow'), onClick: (ev, btn) => act(btn, { busy: t('applying'), run: () => ctx.api.hold(s.handle, 'removals', 'Keep') }) })
                    ]));
                    break;
                case 'heldAdditions':
                    out.push(alertBox('warn', t('alertAdditionTitle', { n: a.count, count: ctx.fmt.num(a.count) }), t('alertAdditionBody', { server: sv, n: a.count, count: ctx.fmt.num(a.count), examples: (a.examples || []).join(', ') }), [
                        button({ label: t('addThem'), variant: 'primary', onClick: (ev, btn) => act(btn, { busy: t('applying'), run: () => ctx.api.hold(s.handle, 'additions', 'Release') }) }),
                        button({ label: t('notNow'), onClick: (ev, btn) => act(btn, { busy: t('applying'), run: () => ctx.api.hold(s.handle, 'additions', 'Keep') }) })
                    ]));
                    break;
                case 'authFailed':
                    out.push(alertBox('bad', t('chipAccessRemoved'), t('hintAccessRemoved', { server: sv }), [button({ label: t('pairAgain'), variant: 'primary', onClick: () => pairAgainGo(s) })]));
                    break;
                case 'identityMismatch':
                    out.push(alertBox('bad', t('alertDifferentTitle'), t('hintDifferentServer', { server: sv }), [
                        button({ label: t('pairAgain'), variant: 'primary', onClick: () => pairAgainGo(s) }),
                        button({ label: t('removeServerEllipsis'), onClick: removeDialog(s) })
                    ]));
                    break;
                case 'tlsKeyChanged': {
                    const box = alertBox('bad', t('alertCertTitle', { server: sv }), t('alertCertBody', { server: sv }), [
                        button({ label: t('trustNewCert'), variant: 'primary', onClick: async (ev, btn) => {
                            const seen = await certificateCheck(ctx, { handle: s.handle, title: t('trustTitle', { server: sv }), intro: t('trustBody'), confirmLabel: t('trustIt'), publicNet: netClass(s) === 'public' });
                            if (seen) {
                                act(btn, { busy: t('applying'), run: () => ctx.api.trustCertificate(s.handle, seen), announce: t('announceCertTrusted'), errorText: certErrorMessage });
                            }
                        } }),
                        button({ label: t('removeServerEllipsis'), onClick: removeDialog(s) })
                    ], factList([[t('certOld'), el('span', { class: 'shoal-mono', text: a.pinnedKey })], [t('certNew'), el('span', { class: 'shoal-mono', text: a.presentedKey })]]));
                    st.certTitle = box.titleNode;
                    out.push(box);
                    break;
                }

                case 'foreignDevice':
                    out.push(alertBox('bad', t('alertForeignTitle', { server: sv }), t('alertForeignBody', { server: sv, device: a.deviceName }), [button({ label: t('pairAgain'), onClick: () => pairAgainGo(s) })]));
                    break;
                case 'policy':
                    out.push(alertBox('warn', t('alertPolicyTitle'), t('alertPolicyBody', { server: sv, list: joinAnd((a.policy || []).map((p) => t('policy_' + p))) }), []));
                    break;
                case 'mergedVersion': {
                    const link = el('a', { class: 'shoal-link', text: t('openTitle', { title: a.title }) });
                    const serverId = ctx.client && typeof ctx.client.serverId === 'function' ? ctx.client.serverId() : '';
                    link.setAttribute('href', '#/details?id=' + encodeURIComponent(a.localItemId) + (serverId ? '&serverId=' + encodeURIComponent(serverId) : ''));
                    out.push(alertBox('warn', t('alertMergedTitle', { title: a.title }), t('alertMergedBody', { server: sv, title: a.title }), [link]));
                    break;
                }

                case 'diskQuota':
                    out.push(alertBox('warn', t('alertQuotaTitle', { server: sv }), t('alertQuotaBody', { server: sv, quota: ctx.fmt.num(quotaGb) }), [settingsLink()]));
                    break;
                case 'entityCap':
                    out.push(alertBox('warn', t('alertEntityTitle', { server: sv }), t('alertEntityBody', { server: sv }), []));
                    break;
                case 'entryCap':
                    out.push(alertBox('bad', t('alertEntryTitle', { server: sv }), t('alertEntryBody'), []));
                    break;
                case 'stubFolder':
                    out.push(alertBox('bad', t('alertStubTitle'), t('alertStubBody', { server: sv }), [settingsLink()]));
                    break;
                default:
                    break;
            }

            if (out.length > before) {
                out[before].alertKind = a.kind;
                out[before].alertTone = a.severity === 'bad' ? 'bad' : 'warn';
            }
        }

        const rot = st.data.rotation;
        if (rot && rot.supported) {
            const tag = (node, kind, tone) => {
                node.alertKind = kind;
                node.alertTone = tone;
                return node;
            };
            const again = () => button({ label: t('pairAgain'), variant: 'primary', onClick: () => pairAgainGo(s) });
            if (rot.error === 'storeFailed') {
                out.push(tag(alertBox('bad', t('alertRotStoreTitle', { server: sv }), t('alertRotStoreBody', { server: sv }), [again()]), 'rotationStoreFailed', 'bad'));
            } else if (rot.error) {
                const bodyKey = 'alertRotFailedBody_' + (['rejected', 'lostResponse', 'badResponse'].includes(rot.error) ? rot.error : 'badResponse');
                out.push(tag(alertBox('bad', t('alertRotFailedTitle', { server: sv }), t(bodyKey, { server: sv }), [again()]), 'rotationFailed', 'bad'));
            }

            // A failed change already says what to do; "overdue" on top of it would contradict it.
            if (rot.overdue && !rot.error) {
                out.push(tag(alertBox('warn', t('alertRotOverdueTitle', { server: sv }), t('alertRotOverdueBody', { server: sv }), [button({ label: t('rotationGoto'), small: true, onClick: () => st.rotationNode && scrollToNode(st.rotationNode) })]), 'rotationOverdue', 'warn'));
            }
        }

        return out;
    }

    /* ---------- the friend who also watches this server (DESIGN v2.6.2) ---------- */

    function linkPanel(s) {
        const l = alsoWatching();
        if (!l) {
            return null;
        }

        const sh = l.share;
        const model = describeLinkStatus(sh.status);
        const sec = section(t('alsoWatchesTitle', { name: s.displayName }));
        appendKids(sec.body, [
            el('div', { class: 'shoal-row shoal-row--tight' }, chip(model.tone, model.icon, model.text),
                el('span', { class: 'shoal-meta', text: t('alsoWatchesMeta', { libs: t('nLibraries', { n: sh.libraryCount }), devs: t('nDevices', { n: sh.deviceCount }), when: sh.lastSeenUtc ? ctx.fmt.relative(sh.lastSeenUtc) : t('never') }) })),
            sh.unexpectedDeviceCount > 0 ? el('p', { class: 'shoal-meta shoal-tone--warn' }, icon('warn', true), t('unexpectedDevices', { n: sh.unexpectedDeviceCount })) : null,
            el('p', { class: 'shoal-meta', text: t('alsoWatchesNote', { name: s.displayName }) }),
            ctx.linkEl('friend', { id: sh.shareHandle }, 'shoal-link', t('openFriendAs', { name: s.displayName }))]);
        return sec.root;
    }

    /* ---------- token rotation (DESIGN v2.6.1) ---------- */

    function rotationSection(s) {
        const r = st.data.rotation;
        if (!r) {
            return null;
        }

        const sec = section(t('rotationTitle'));
        st.rotationNode = sec.root;
        if (!r.supported) {
            sec.body.append(el('p', { class: 'shoal-meta', text: t('rotationNotSupported', { server: s.displayName }) }));
            return sec.root;
        }

        sec.body.append(factList([
            [t('rotationLast'), r.lastRotatedUtc ? ctx.fmt.relative(r.lastRotatedUtc) : t('never')],
            [t('rotationNext'), r.days > 0 && r.nextDueUtc ? ctx.fmt.date(r.nextDueUtc) : t('rotationNoSchedule')]
        ]));
        const pick = selectField({
            id: uid('shoal-rot'),
            label: t('rotationSchedule'),
            options: [0, 30, 60, 90].map((d) => ({ value: String(d), label: d === 0 ? t('rotationOff') : t('rotationEvery', { n: d }) })),
            value: String(r.days || 0),
            help: t('rotationScheduleHelp')
        });
        const savedNote = el('span', { class: 'shoal-meta shoal-tone--ok', role: 'status', hidden: !st.rotationSaved }, icon('ok', true), t('rotationSaved'));
        pick.select.addEventListener('change', async () => {
            try {
                // The answer is the whole rotation block with the new next-change date: show it, with a visible "Saved".
                const fresh = await ctx.api.setRotation(s.handle, Number(pick.select.value));
                if (fresh && typeof fresh === 'object') {
                    st.data.rotation = fresh;
                }

                st.rotationSaved = true;
                ctx.announce(t('announceRotationSaved'));
                st.sig = JSON.stringify(st.data);
                render();
            } catch (err) {
                ctx.alertMessage(t(err && err.kind === 'notSupported' ? 'rotateErr_notSupported' : 'saveFailed'));
                load(false, true);
            }
        });
        const note = el('span', { class: 'shoal-meta shoal-action__note', id: uid('shoal-note'), hidden: true });
        const btn = button({
            label: t('rotateNow'),
            onClick: async (ev, node) => {
                const res = await confirmDialog(ctx, { title: t('rotateTitle', { server: s.displayName }), intro: t('rotateBody', { server: s.displayName }), confirmLabel: t('rotateConfirm') });
                if (!res.confirmed) {
                    return;
                }

                await runAction(ctx, node, {
                    busyText: t('rotating'),
                    cooldownKey: 'rotate:' + s.handle,
                    run: () => ctx.api.rotatePeer(s.handle),
                    onDone: () => {
                        ctx.setCooldown('rotate:' + s.handle, 600); // one change per 10 minutes
                        ctx.announce(t('announceRotated', { server: s.displayName }));
                        load(false, true);
                    },
                    onError: (err) => {
                        if (err && err.status === 429) {
                            ctx.alertMessage(t('availableIn', { n: ctx.cooldownMinutes('rotate:' + s.handle) || 1 }));
                        } else {
                            const key = 'rotateErr_' + (err && err.kind);
                            ctx.alertMessage(t(STRINGS[key] === undefined ? 'rotateErr_failed' : key));
                        }

                        load(false, true);
                    },
                    afterBusy: () => cooldownNote(ctx, 'rotate:' + s.handle, note, btn)
                });
            }
        });
        cooldownNote(ctx, 'rotate:' + s.handle, note, btn);
        sec.body.append(pick.root, savedNote, el('div', { class: 'shoal-detail__actions' }, el('span', { class: 'shoal-action' }, btn, note)));
        return sec.root;
    }

    /* ---------- link speed extras (route, hints, tests) ---------- */

    function speedExtras(data, s, host) {
        clear(host);
        const route = (() => {
            const r = data.route || { kind: 'unknown' };
            const text = r.kind === 'derp' ? t('routeRelayed', { region: r.region || '' }) : t('route_' + r.kind);
            const tone = r.kind === 'direct' ? 'ok' : r.kind === 'unknown' ? 'off' : 'warn';
            return el('div', { class: 'shoal-route' },
                el('div', { class: 'shoal-route__head' }, el('span', { class: 'shoal-label', text: t('routeLabel') }), chip(tone, r.kind === 'direct' ? 'direct' : 'relay', text)),
                isTailscaleHost(hostOf(s.url).toLowerCase()) ? el('p', { class: 'shoal-route__line' }, t('toCheck'), ' ', el('code', { class: 'shoal-inlinecode', text: 'tailscale ping ' + hostOf(s.url) }), ' ', copyButton(ctx, 'tailscale ping ' + hostOf(s.url), 'copyCommand', 'announceCommandCopied')) : null,
                isTailscaleHost(hostOf(s.url).toLowerCase()) ? el('p', { class: 'shoal-meta', text: t(data.localApiAvailable ? 'localApiYes' : 'localApiNo') }) : null);
        })();
        if (isTailscaleHost(hostOf(s.url).toLowerCase())) {
            host.append(route);
        }

        const allSmooth = (data.ladder || []).length > 0 && (data.ladder || []).every((r) => r.verdict === 'smooth');
        const hints = (data.hints || []).map((h) => (allSmooth && h.severity !== 'bad' ? hint('info', 'info', t('speedHint_' + h.kind, { server: s.displayName, n: h.n })) : hint(h.severity === 'bad' ? 'bad' : 'warn', 'warn', t('speedHint_' + h.kind, { server: s.displayName, n: h.n }))));
        if (hints.length) {
            host.append(el('div', { class: 'shoal-hintlist' }, hints));
        }

        if (data.deep && typeof data.deep.suggestedConversions === 'number') {
            host.append(el('p', { class: 'shoal-meta', text: t('deepSuggest', { k: data.deep.suggestedConversions, server: s.displayName }) }));
        }

        const deepNote = el('span', { class: 'shoal-meta shoal-action__note', id: uid('shoal-note'), hidden: true });
        const deepBtn = button({
            label: t('deepTest'),
            onClick: async (ev, btn) => {
                const res = await confirmDialog(ctx, { title: t('deepTitle'), intro: t('deepBody', { server: s.displayName }), confirmLabel: t('runDeepTest') });
                if (res.confirmed) {
                    await act(btn, { busy: t('testing'), cooldownKey: 'deep:' + s.handle, run: () => ctx.api.speedTest(s.handle, true), after: () => ctx.later(() => st.speed && st.speed.load(true), 2000) });
                    cooldownNote(ctx, 'deep:' + s.handle, deepNote, deepBtn);
                }
            }
        });
        cooldownNote(ctx, 'deep:' + s.handle, deepNote, deepBtn);
        host.append(el('div', { class: 'shoal-speed__tests' }, el('span', { class: 'shoal-action' }, deepBtn, deepNote)));

        const tests = (data.samples || []).filter((x) => x.kind !== 'playback').slice().sort((a, b) => toMs(b.t) - toMs(a.t)).slice(0, 12);
        host.append(collapse(t('recentTests'), dataTable({
            caption: t('recentTests'),
            columns: [
                { key: 'time', label: t('colTime') }, { key: 'type', label: t('colType') }, { key: 'speed', label: t('colSpeedMbps'), num: true },
                { key: 'latency', label: t('colLatencyMs'), num: true }, { key: 'jitter', label: t('colJitterMs'), num: true }, { key: 'loss', label: t('colLoss'), num: true },
                { key: 'route', label: t('colRoute') }, { key: 'result', label: t('colResult') }
            ],
            rows: tests.map((x) => ({
                time: ctx.fmt.relative(x.t),
                type: t('kind_' + x.kind),
                speed: typeof x.mbps === 'number' ? ctx.fmt.mbpsValue(x.mbps) : '—',
                latency: typeof x.rttMedianMs === 'number' ? ctx.fmt.num(Math.round(x.rttMedianMs)) : '—',
                jitter: typeof x.jitterMs === 'number' ? ctx.fmt.num(Math.round(x.jitterMs)) : '—',
                loss: typeof x.lossPct === 'number' ? x.lossPct + '%' : '—',
                route: t('path_' + (x.path || 'unknown')),
                result: x.error ? t('testFailedReason', { reason: t('reason_' + x.error) }) : t('resultOk')
            }))
        })));

        const plays = (data.samples || []).filter((x) => x.kind === 'playback').slice().sort((a, b) => toMs(b.t) - toMs(a.t)).slice(0, 12);
        host.append(collapse(t('recentPlays'), dataTable({
            caption: t('recentPlays'),
            columns: [{ key: 'time', label: t('colTime') }, { key: 'who', label: t('colPerson') }, { key: 'method', label: t('colMethod') }, { key: 'speed', label: t('colAvgSpeed'), num: true }, { key: 'wait', label: t('colSegWait'), num: true }, { key: 'pauses', label: t('colPauses'), num: true }],
            rows: plays.map((x) => ({
                time: ctx.fmt.relative(x.t),
                who: x.who || '—',
                method: t(x.method === 'transcode' ? 'methodTranscode' : 'methodDirect'),
                speed: typeof x.mbps === 'number' ? ctx.fmt.mbpsValue(x.mbps) : '—',
                wait: typeof x.segmentWaitMs === 'number' ? ctx.fmt.num(Math.round(x.segmentWaitMs)) : '—',
                pauses: typeof x.pauses === 'number' ? x.pauses : '—'
            }))
        })));
    }

    /* ---------- libraries (8.3.4) ---------- */

    function librariesSection(s) {
        const sec = section(t('librariesFrom', { server: s.displayName }));
        const dedup = dedupNote(st.data);
        if (dedup) {
            sec.body.append(el('p', { class: 'shoal-meta' }, dedup.textContent.trim()));
        }

        const libs = st.data.libraries || [];
        if (!libs.length) {
            sec.body.append(el('p', { class: 'shoal-meta shoal-emptyline', text: t('noLibrariesFrom', { server: s.displayName }) }));
        } else {
            sec.body.append(el('ul', { class: 'shoal-list' }, libs.map((m) => {
                const summary = accessSummary(m.access, st.data.users || [], currentUserId(ctx, st.data));
                const holds = [];
                if (m.heldRemovals > 0) {
                    holds.push(el('span', { class: 'shoal-tone--warn shoal-holdline' }, icon('warn', true), t('removalsOnHold', { n: m.heldRemovals, count: ctx.fmt.num(m.heldRemovals) })));
                }

                if (m.heldAdditions > 0) {
                    holds.push(el('span', { class: 'shoal-tone--warn shoal-holdline' }, icon('warn', true), t('additionsOnHold', { n: m.heldAdditions, count: ctx.fmt.num(m.heldAdditions) })));
                }

                const rm = button({ label: t('removeEllipsis'), small: true, onClick: () => removeLibrary(s, m) });
                return el('li', { class: 'shoal-row shoal-row--library' },
                    el('div', { class: 'shoal-row__main' },
                        el('span', { class: 'shoal-row__title', text: m.localLibraryName }),
                        el('span', { class: 'shoal-row__meta shoal-meta', text: countsDiffer(m)
                            ? t('libraryFromMetaSplit', { name: m.remoteName || '', remote: ctx.fmt.num(m.remoteCount), local: ctx.fmt.num(m.localCount), when: m.lastSync ? ctx.fmt.relative(m.lastSync.atUtc) : t('never'), result: m.lastSync ? t('syncResult_' + m.lastSync.result) : '' })
                            : t('libraryFromMeta', { remote: m.remoteName || '', count: ctx.fmt.num(m.count), n: m.count, when: m.lastSync ? ctx.fmt.relative(m.lastSync.atUtc) : t('never'), result: m.lastSync ? t('syncResult_' + m.lastSync.result) : '' }) }),
                        holds),
                    el('div', { class: 'shoal-row__controls shoal-row__controls--inline' }, summary, ctx.linkEl('libraries', { focus: 'lib-' + m.mappingKey }, 'shoal-link', t('changeWhoCanSee')), rm));
            })));
        }

        sec.body.append(button({ label: t(libs.length ? 'addAnotherLibrary' : 'addALibrary'), onClick: () => addLibraryDialog(s) }));
        return sec.root;
    }

    async function removeLibrary(s, m) {
        const res = await confirmDialog(ctx, {
            title: t('removeLibraryTitle', { library: m.localLibraryName }),
            intro: t('removeLibraryBody', { server: s.displayName, n: m.count, count: ctx.fmt.num(m.count) }),
            confirmLabel: t('removeLibraryConfirm'),
            danger: true
        });
        if (!res.confirmed) {
            return;
        }

        try {
            await ctx.api.removeLibrary(s.handle, m.mappingKey);
            ctx.announce(t('announceLibraryRemoved', { library: m.localLibraryName }));
            load(false, true);
        } catch (err) {
            ctx.fail(failureMessage(err));
        }
    }

    async function addLibraryDialog(s) {
        let remote = [];
        try {
            remote = await ctx.api.remoteLibraries(s.handle);
        } catch (err) {
            ctx.alertMessage(err && err.status === 502 ? t('remoteUnreachable', { server: s.displayName }) : t('genericFailure'));
            return;
        }

        const have = new Set((st.data.libraries || []).map((m) => m.mappingKey));
        const choices = remote.filter((r) => !r.added && !have.has(r.mappingKey));
        const ticked = new Set();
        const res = await openDialog(ctx, {
            title: t('addLibraryTitle', { server: s.displayName }),
            build(dBody) {
                if (!choices.length) {
                    dBody.append(paragraph(t('noMoreLibraries')));
                    return null;
                }

                dBody.append(paragraph(t('chooseBody', { server: s.displayName }), 'shoal-meta'));
                for (const r of choices) {
                    dBody.append(checkRow({
                        id: uid('shoal-al'),
                        label: r.name,
                        note: r.selectable ? libCountNote(ctx, r) : libLaterNote(r.type),
                        disabled: !r.selectable,
                        onChange: (c) => (c ? ticked.add(r.mappingKey) : ticked.delete(r.mappingKey))
                    }).root);
                }

                return null;
            },
            buttons: [{ label: t('cancel'), close: undefined }, { label: t('addAndSync'), variant: 'primary', disabled: !choices.length, onClick: (dlg) => dlg.close(Array.from(ticked)) }]
        });
        if (res && res.length) {
            try {
                await ctx.api.addLibrary(s.handle, res.map((k) => ({ mappingKey: k, localLibraryName: t('defaultLocalName', { server: s.displayName, library: (remote.find((r) => r.mappingKey === k) || {}).name || '' }), selectedUserIds: [] })));
                ctx.announce(t('announceLibraryAdded'));
ctx.toast(t('announceLibraryAdded'));
                load(false, true);
            } catch (err) {
                ctx.alertMessage(err && err.status === 409 ? t('addConflict') : failureMessage(err, 'addFailed'));
            }
        }
    }

    /* ---------- held back, by series (K40): each series is released on its own ---------- */

    function holdsSection(s) {
        const sec = section(t('heldSeriesTitle'));
        sec.root.hidden = true;
        const draw = (data) => {
            clear(sec.body);
            const groups = [['removals', 'heldRemovalsHead', 'heldSeriesRemove', data.removals || []], ['additions', 'heldAdditionsHead', 'heldSeriesAdd', data.additions || []]].filter((g) => g[3].length > 0);
            sec.root.hidden = groups.length === 0;
            if (!groups.length) {
                return;
            }

            sec.body.append(el('p', { class: 'shoal-meta', text: t('heldSeriesIntro', { server: s.displayName }) }));
            for (const [kind, headKey, buttonKey, rows] of groups) {
                sec.body.append(el('h3', { class: 'shoal-label', text: t(headKey) }), el('ul', { class: 'shoal-list shoal-holdseries' }, rows.map((r) => el('li', { class: 'shoal-row' },
                    el('span', { class: 'shoal-row__main', text: r.title ? t('heldSeriesRow', { title: r.title + (r.year ? ' ' + r.year : ''), count: ctx.fmt.num(r.episodes) }) : t('heldSeriesRowNoTitle', { count: ctx.fmt.num(r.episodes) }) }),
                    button({ label: t(buttonKey), small: true, onClick: (ev, btn) => act(btn, { busy: t('applying'), run: () => ctx.api.releaseSeries(s.handle, kind, r.series), announce: t('announceSeriesReleased') }) })))));
            }
        };
        ctx.api.holds(s.handle).then((data) => {
            if (ctx.alive) {
                draw(data || {});
            }
        }).catch(() => {
            sec.root.hidden = true;
        });
        return sec.root;
    }

    /* ---------- storage (8.3.5) ---------- */

    function storageSection(s) {
        const sec = section(t('storageTitle'));
        const ovPanel = el('div', { class: 'shoal-overlap' });
        sec.body.append(ovPanel);
        ctx.api.overlap(s.handle).then((ov) => {
            if (ctx.alive) {
                fillOverlap(ctx, ovPanel, s.displayName, ov, { view: 'overlap', query: { id: s.handle, from: 'server' } });
            }
        }).catch(() => fillOverlap(ctx, ovPanel, s.displayName, null, null));
        const u = st.data.usage;
        if (u) {
            const gb = (b) => ctx.fmt.num(Math.round(b / 1e8) / 10);
            const pct = Math.min(100, Math.round((u.diskBytes / Math.max(1, u.quotaBytes)) * 100));
            const full = pct >= 90;
            const label = t('storageMeter', { used: gb(u.diskBytes), quota: gb(u.quotaBytes) });
            const bar = el('div', { class: 'shoal-bar', role: 'meter', 'aria-label': label, 'aria-valuemin': '0', 'aria-valuemax': String(u.quotaBytes), 'aria-valuenow': String(u.diskBytes) }, el('span', { class: 'shoal-bar__fill' }));
            setVar(q(bar, '.shoal-bar__fill'), '--shoal-progress', pct + '%');
            const cap = (n, max) => {
                const atCap = n >= max;
                return el('span', { class: atCap ? 'shoal-tone--warn' : null }, atCap ? icon('warn', true) : null, t('capOf', { n: ctx.fmt.num(n), cap: ctx.fmt.num(max) }), atCap ? ' · ' + t('limitReached') : '');
            };
            sec.body.append(el('h3', { class: 'shoal-h3', text: t('storageFor', { server: s.displayName }) }),
                el('p', { class: 'shoal-meta' + (full ? ' shoal-tone--warn' : '') }, full ? icon('warn', true) : null, label, full ? ' · ' + t('almostFull') : ''),
                bar,
                factList([[t('capTitles'), cap(u.stubs, 50000)], [t('capCast'), cap(u.people, 20000)], [t('capGenres'), cap(u.genres, 20000)], [t('capStudios'), cap(u.studios, 20000)]]));
        }

        return sec.root;
    }

    /* ---------- playing now (8.3.6) ---------- */

    function playingSection(s) {
        const sec = section(t('playingNowTitle'));
        const list = st.data.streams || [];
        if (!list.length) {
            sec.body.append(el('p', { class: 'shoal-meta shoal-emptyline', text: t('nothingPlaying', { server: s.displayName }) }));
        } else {
            sec.body.append(el('ul', { class: 'shoal-list' }, list.map((p) => el('li', { class: 'shoal-row' },
                el('div', { class: 'shoal-row__main' },
                    el('span', { class: 'shoal-row__title', text: p.title }),
                    el('span', { class: 'shoal-row__meta shoal-meta', text: t('playingMeta', { who: p.person, when: ctx.fmt.relative(p.startedUtc) }) })),
                el('div', { class: 'shoal-row__controls shoal-row__controls--inline' },
                    chip(String(p.method).toLowerCase() === 'transcode' ? 'warn' : 'ok', String(p.method).toLowerCase() === 'transcode' ? 'relay' : 'direct', t(String(p.method).toLowerCase() === 'transcode' ? 'methodTranscode' : 'methodDirect')),
                    el('span', { class: 'shoal-meta', text: ctx.fmt.mbps(p.mbps || 0) }))))));
        }

        if (ctx.features && ctx.features.appsWork) {
            sec.body.append(appsWorkBlock(ctx, s.displayName));
        }

        return sec.root;
    }

    /* ---------- settings for this server (8.3.7) ---------- */

    function hostChanged(oldUrl, newUrl) {
        return hostOf(oldUrl).toLowerCase() !== hostOf(newUrl).toLowerCase();
    }

    function settingsSection(s) {
        const set = st.data.settings || {};
        const locked = !!st.data.pairingSession;
        const streams = numberField({ id: uid('shoal-ss'), label: t('fServerStreams'), help: t('fServerStreamsHelp', { server: s.displayName }), value: set.maxConcurrentStreams ?? 4, min: 1, max: 10 });
        const priority = numberField({ id: uid('shoal-ss'), label: t('fPriority'), help: t('fPriorityHelp'), value: set.priority ?? 100, min: 1, max: 1000 });
        const address = textField({ id: uid('shoal-ss'), label: t('fAddress'), help: locked ? t('afterPairing') : t('fAddressHelp'), value: s.url || '', mono: true, spellcheck: false, disabled: locked });
        const proxy = textField({ id: uid('shoal-ss'), label: t('fProxy'), help: locked ? t('afterPairing') : t('fProxyHelp'), value: set.proxyUrl || '', mono: true, spellcheck: false, disabled: locked });
        const publicNet = netClass(s) === 'public';
        const pin = publicNet ? checkRow({ id: uid('shoal-ss'), label: t('fPin'), note: t('fPinNote'), checked: !!set.tlsPinned }) : null;
        const saved = el('span', { class: 'shoal-meta shoal-tone--ok', role: 'status', hidden: true }, icon('ok', true), t('saved'));
        async function doSave(btn, confirmName, seen) {
            const a = Number(streams.input.value);
            const b = Number(priority.input.value);
            const payload = { maxConcurrentStreams: a, priority: b, url: address.value(), proxyUrl: proxy.value() || null, tlsPinned: pin ? pin.input.checked : false };
            if (confirmName) {
                payload.confirmName = confirmName;
            }

            if (seen) {
                // A new pin carries the key the admin compared with the friend (Warden 11 M2).
                payload.expectedPin = seen.expectedPin;
                if (seen.acceptUntrusted) {
                    payload.acceptUntrusted = true;
                }
            }

            await runAction(ctx, btn, {
                busyText: t('saving'),
                run: () => ctx.api.updatePeer(s.handle, payload),
                onDone: () => {
                    saved.hidden = false;
                    ctx.announce(t('saved'));
ctx.toast(t('saved'));
                    if (confirmName) {
                        load(false, true);
                    }
                },
                onError: (err) => ctx.alertMessage(certErrorMessage(err, 'saveFailed'))
            });
        }

        const saveBtn = button({ label: t('save'), variant: 'primary', onClick: async (ev, btn) => {
            const a = Number(streams.input.value);
            const b = Number(priority.input.value);
            streams.setError(Number.isInteger(a) && a >= 1 && a <= 10 ? '' : t('errWholeNumber', { min: 1, max: 10 }));
            priority.setError(Number.isInteger(b) && b >= 1 && b <= 1000 ? '' : t('errWholeNumber', { min: 1, max: 1000 }));
            if (streams.input.getAttribute('aria-invalid') || priority.input.getAttribute('aria-invalid')) {
                return;
            }

            const newPin = !!pin && pin.input.checked && !set.tlsPinned;
            if (newPin && !locked && hostChanged(s.url, address.value())) {
                ctx.alertMessage(t('pinAfterAddress'));
                return;
            }

            let seen = null;
            if (newPin) {
                seen = await certificateCheck(ctx, { handle: s.handle, title: t('pinTitle', { server: s.displayName }), intro: t('pinBody'), confirmLabel: t('pinIt'), publicNet: true });
                if (!seen) {
                    return;
                }
            }

            if (!locked && hostChanged(s.url, address.value())) {
                const res = await confirmDialog(ctx, {
                    title: t('addressTitle', { server: s.displayName }),
                    intro: t('addressBody', { server: s.displayName }),
                    typed: { label: t('typeToConfirm', { text: s.displayName }), expected: s.displayName },
                    confirmLabel: t('addressConfirm'),
                    safeFocus: true
                });
                if (!res.confirmed) {
                    return;
                }

                await doSave(btn, s.displayName);
                return;
            }

            await doSave(btn, null, seen);
        } });
        const reach = factList([[t('factReach'), t(netClass(s) === 'public' ? 'reachPublic' : netClass(s) === 'lan' ? 'reachLan' : 'reachTailnet')]]);
        const content = el('div', { class: 'shoal-form' }, streams.root, priority.root, address.root, proxy.root, reach, paragraph(t('reachChange'), 'shoal-meta'), pin ? pin.root : null, el('div', { class: 'shoal-step__actions' }, saveBtn, saved));
        // A poll must not redraw over values the admin typed and has not saved (Lumen M5).
        content.addEventListener('input', () => {
            st.dirty = true;
        });
        content.addEventListener('change', () => {
            st.dirty = true;
        });
        return el('section', { class: 'shoal-panel' }, collapse(t('serverSettings'), content));
    }

    function dangerSection(s) {
        const btn = button({ label: t('removeServerEllipsis'), onClick: removeDialog(s) });
        return el('section', { class: 'shoal-danger', 'aria-labelledby': 'shoal-rm-' + ctx.instance },
            el('h2', { class: 'shoal-h2', id: 'shoal-rm-' + ctx.instance, text: t('removeHeading', { server: s.displayName }) }),
            paragraph(t('removeBody', { server: s.displayName })),
            btn);
    }

    /* ---------- render ---------- */

    function render() {
        const s = st.data;
        const model = describeServer(s, ctx.fmt, { keysLocked: !!(ctx.status && ctx.status.secretsUnreadable) });
        const open = openKeys(body);
        st.certTitle = null;
        st.dirty = false;
        clear(body);
        body.append(headerPanel(s, model));
        const alerts = alertsFor(s);
        // Only a bad alert kind that was not there before is announced; a redraw never repeats the old ones (ui-spec 12.4).
        const kinds = new Set(alerts.map((a) => a.alertKind));
        if (st.alertKinds) {
            const fresh = alerts.filter((a) => a.alertTone === 'bad' && !st.alertKinds.has(a.alertKind));
            if (fresh.length) {
                ctx.alertMessage(q(fresh[0], '.shoal-hint__title').textContent);
            }
        }

        st.alertKinds = kinds;
        if (alerts.length) {
            const sec = section(t('needsAttention'));
            sec.root.id = 'alerts';
            sec.body.append(...alerts);
            st.alertsNode = sec.root;
            body.append(sec.root);
        } else {
            st.alertsNode = null;
        }

        if (model.stale) {
            if (s.updatedUtc) {
                body.append(el('p', { class: 'shoal-meta', text: t('lastUpdated', { when: ctx.fmt.relative(s.updatedUtc) }) }));
            }
        }

        // One speed panel per screen: a redraw only hands it the new server, so the range the admin picked survives.
        if (!st.speed) {
            st.speed = createSpeedPanel(ctx, {
                detail: true,
                servers: [s],
                selected: s.handle,
                onSelect: () => {},
                onData: (data, cur, extra) => speedExtras(data, cur, extra)
            });
            st.speed.root.id = uid('shoal-speed');
        }

        const linked = linkPanel(s);
        if (linked) {
            body.append(linked);
        }

        body.append(st.speed.root);
        body.append(librariesSection(s), holdsSection(s), storageSection(s), playingSection(s));
        const rotation = rotationSection(s);
        if (rotation) {
            body.append(rotation);
        }

        body.append(settingsSection(s), dangerSection(s));
        st.speed.setServers([s], s.handle);
        restoreOpen(body, open);
        if (!st.focused) {
            st.focused = true;
            const target = ctx.params.focus === 'alerts' && st.alertsNode ? st.alertsNode : ctx.params.focus === 'speed' ? st.speed.root : null;
            if (target) {
                ctx.later(() => scrollToNode(target), 50);
            }
        }
    }

    function patch(force) {
        // Polling only redraws when the payload changed and nothing in the page has focus; a redraw
        // would move focus (ui-spec 12.2).
        const sig = JSON.stringify(st.data);
        if (sig === st.sig && !force) {
            return;
        }

        if (st.dirty && !force) {
            return;
        }

        const active = document.activeElement;
        const inside = active && body.contains(active) && active !== document.body && active.tagName !== 'H1';
        if (inside && st.rendered && !force) {
            return;
        }

        st.sig = sig;
        render();
        st.rendered = true;
    }

    async function load(initial, force) {
        if (initial && !st.loaded) {
            replaceKids(body, el('div', { class: 'shoal-skel', 'aria-busy': 'true' }, el('span', { class: 'shoal-sr', text: t('loadingShoal') }), el('div', { class: 'shoal-skel__panel' }), el('div', { class: 'shoal-skel__panel shoal-skel__panel--short' })));
        }

        try {
            const data = await ctx.api.peer(handle);
            if (!ctx.alive) {
                return;
            }

            const first = !st.loaded;
            st.data = data;
            st.loaded = true;
            if (first) {
                st.sig = JSON.stringify(data);
                render();
                st.rendered = true;
                ctx.focusHeading(q(body, 'h1'));
            } else {
                patch(force);
            }
        } catch (err) {
            if (!ctx.alive) {
                return;
            }

            if (err.status === 404) {
                ctx.notFound();
                return;
            }

            if (!st.loaded) {
                replaceKids(body, loadErrorBox(() => load(true)));
            }
        }
    }

    return {
        show() {
            load(!st.loaded);
            poller.start();
        },
        hide() {
            poller.stop();
        },
        destroy() {
            poller.stop();
            if (st.speed) {
                st.speed.destroy();
            }
        },
        refresh: () => load(false),
        state: () => st
    };
}

/* ====================================================================================== */
/* Screen: Overlap report (ui-spec 8.6)                                                   */
/* ====================================================================================== */

function overlapReportScreen(ctx) {
    const view = q(ctx.root, '[data-shoal-view="overlap"]');
    const body = slot(view, 'body');
    const select = slot(view, 'overlapselect');
    const selectWrap = slot(view, 'overlapselectwrap');
    const st = { servers: [], handle: ctx.params.id || null, loaded: false, seq: 0 };

    select.addEventListener('change', () => {
        st.handle = select.value;
        loadOverlap();
    });

    async function loadOverlap() {
        const cur = st.servers.find((s) => s.handle === st.handle);
        if (!cur) {
            return;
        }

        const mine = ++st.seq;
        replaceKids(body, el('div', { class: 'shoal-skel', 'aria-busy': 'true' }, el('span', { class: 'shoal-sr', text: t('loadingShoal') }), el('div', { class: 'shoal-skel__panel' })));
        try {
            const ov = await ctx.api.overlap(cur.handle);
            if (mine !== st.seq || !ctx.alive) {
                return;
            }

            renderReport(cur, ov);
        } catch {
            if (mine === st.seq) {
                replaceKids(body, loadErrorBox(() => loadOverlap()));
            }
        }
    }

    function renderReport(cur, ov) {
        clear(body);
        if (!ov || !ov.computedUtc) {
            body.append(el('p', { class: 'shoal-lead', text: t('overlapReportNone') }));
            return;
        }

        const both = ov.both;
        const gain = ov.gain;
        const sentence = overlapSentences(ctx, cur.displayName, ov);
        const top = el('section', { class: 'shoal-panel shoal-overlap' },
            el('div', { class: 'shoal-overlap__grid' },
                el('div', { class: 'shoal-overlap__text' },
                    el('p', { class: 'shoal-lead' }, sentence.text),
                    overlapTvLine(ctx, cur.displayName, ov) ? el('p', { class: 'shoal-body-text shoal-overlap__tv', text: overlapTvLine(ctx, cur.displayName, ov) }) : null,
                    el('p', { class: 'shoal-meta', text: t('yourCopyPlays') })),
                el('div', { class: 'shoal-overlap__viz' }, overlapViz(ctx, cur.displayName, ov))));
        body.append(top);

        const rows = (ov.byQuality || []).map((r) => ({
            q: t('quality_' + r.q),
            bothCount: ctx.fmt.num(r.bothCount),
            local: ctx.fmt.bytes(r.localBytes),
            gainCount: ctx.fmt.num(r.gainCount),
            peer: ctx.fmt.bytes(r.peerBytes)
        }));
        const totals = (ov.byQuality || []).reduce((a, r) => ({ b: a.b + r.bothCount, l: a.l + r.localBytes, g: a.g + r.gainCount, p: a.p + r.peerBytes }), { b: 0, l: 0, g: 0, p: 0 });
        rows.push({ q: el('strong', { text: t('qualityTotal') }), bothCount: ctx.fmt.num(totals.b), local: ctx.fmt.bytes(totals.l), gainCount: ctx.fmt.num(totals.g), peer: ctx.fmt.bytes(totals.p) });
        const byQuality = section(t('byQuality'));
        byQuality.body.append(dataTable({
            caption: t('overlapByQualityCaption'),
            columns: [
                { key: 'q', label: t('colQuality') },
                { key: 'bothCount', label: t('colBothTitles'), num: true },
                { key: 'local', label: t('colYourStorage'), num: true },
                { key: 'gainCount', label: t('colNewTitles', { server: cur.displayName }), num: true },
                { key: 'peer', label: t('colTheirStorage'), num: true }
            ],
            rows
        }));
        body.append(byQuality.root);
        if (typeof ov.betterCopyCount === 'number') {
            body.append(el('p', { class: 'shoal-body-text' }, t('betterCopy', { server: cur.displayName, n: ov.betterCopyCount }), ' ', el('span', { class: 'shoal-meta', text: t('betterCopyLater') })));
        }

        body.append(el('p', { class: 'shoal-meta', text: t('overlapUpdated', { when: ctx.fmt.relative(ov.computedUtc) }) }));
    }

    async function init() {
        try {
            const data = await ctx.api.status();
            if (!ctx.alive) {
                return;
            }

            st.servers = sortServers(data.servers || []);
            if (!st.servers.some((s) => s.handle === st.handle)) {
                if (st.handle) {
                    ctx.notFound();
                    return;
                }

                st.handle = pickSelection(st.servers, null, ctx.fmt);
            }

            replaceKids(select, st.servers.map((s) => el('option', { value: s.handle, text: s.displayName })));
            select.value = st.handle;
            selectWrap.hidden = st.servers.length < 2;
            if (ctx.params.from === 'server') {
                // Back link reads the server's name (ui-spec 8.6).
                const cur = st.servers.find((s) => s.handle === st.handle);
                const label = slot(ctx.root, 'backlabel');
                if (cur && label) {
                    label.textContent = cur.displayName;
                }
            }

            st.loaded = true;
            loadOverlap();
        } catch {
            replaceKids(body, loadErrorBox(() => init()));
        }
    }

    return {
        show() {
            if (!st.loaded) {
                init();
            }
        },
        hide() {},
        destroy() {}
    };
}

/* ====================================================================================== */
/* Screen: Friend detail, the sharing side (ui-spec 8.7, DESIGN 4.1b, 4.5, 9.3)           */
/* ====================================================================================== */

function friendDetailScreen(ctx) {
    // The sharing account and the date come from the server when it knows them (the share service, K21); the line says only what is known.
    const sharingMeta = (f) => f.shareUserName && f.sinceUtc ? t('friendSharingAccount', { account: f.shareUserName, date: ctx.fmt.date(f.sinceUtc) }) : f.sinceUtc ? t('friendSharingSince', { date: ctx.fmt.date(f.sinceUtc) }) : f.shareUserName ? t('friendSharingAccountOnly', { account: f.shareUserName }) : '';
    const view = q(ctx.root, '[data-shoal-view="friend"]');
    const body = slot(view, 'body');
    const handle = ctx.params.id;
    const h1Id = uid('shoal-h1');
    view.setAttribute('aria-labelledby', h1Id);
    // st.invite is the one-time invite string: memory only, gone when the admin leaves (ui-spec 6.10).
    const st = { data: null, loaded: false, invite: null, cancelled: false, sig: '', result: null, card: null, focused: false };
    const poller = createPoller(ctx, () => load(false), PAIRING_POLL_MS);
    let cdRef = null;

    function stopCountdown() {
        if (cdRef) {
            cdRef.stop();
            cdRef = null;
        }
    }

    function name() {
        return st.data.friendName;
    }

    // The server this friend also runs that you watch (DESIGN v2.6.2), from the Overview's friend links.
    function watchedLink() {
        const links = (ctx.status && Array.isArray(ctx.status.friendLinks)) ? ctx.status.friendLinks : [];
        return links.find((l) => l && l.linked && l.share && l.share.shareHandle === handle && l.watch) || null;
    }

    function youAlsoWatchPanel() {
        const l = watchedLink();
        if (!l) {
            return null;
        }

        const model = describeLinkStatus(l.watch.status);
        const sec = section(t('youAlsoWatchTitle', { name: l.name }));
        appendKids(sec.body, [
            el('div', { class: 'shoal-row shoal-row--tight' }, chip(model.tone, model.icon, model.text), el('span', { class: 'shoal-meta', text: countText(ctx, 'titlesN', l.watch.titles) }),
                ctx.linkEl('server', { id: l.watch.peerHandle }, 'shoal-link', t('openServerLink')))]);
        return sec.root;
    }

    function headerPanel(f) {
        const model = describeFriend({ status: f.status });
        const paused = f.status === 'Paused';
        const pauseBtn = button({
            label: t(paused ? 'resume' : 'pause'),
            variant: paused ? 'primary' : 'quiet',
            onClick: (ev, btn) => runAction(ctx, btn, {
                busyText: t(paused ? 'resuming' : 'pausing'),
                run: () => (paused ? ctx.api.resumeShare(handle) : ctx.api.pauseShare(handle)),
                onDone: () => {
                    ctx.announce(t(paused ? 'announceFriendResumed' : 'announceFriendPaused', { friend: f.friendName }));
                    load(false, true);
                },
                onError: (err) => ctx.alertMessage(failureMessage(err))
            })
        });
        const panelNode = el('section', { class: 'shoal-panel shoal-detail__header shoal-detail__header--plain' },
            el('div', { class: 'shoal-detail__head' },
                el('div', { class: 'shoal-detail__title' },
                    el('div', { class: 'shoal-detail__namerow' }, el('h1', { class: 'shoal-h1', id: h1Id, tabindex: '-1', text: f.friendName }), chip(model.tone, model.icon, model.text)),
                    el('div', { class: 'shoal-detail__meta shoal-meta' }, el('span', { class: 'shoal-detail__metaitem', text: sharingMeta(f) }), f.lastRotatedUtc ? el('span', { class: 'shoal-detail__metaitem', text: t('friendKeyChanged', { when: ctx.fmt.relative(f.lastRotatedUtc) }) }) : null)),
                el('div', { class: 'shoal-detail__actions' }, el('span', { class: 'shoal-action' }, pauseBtn, el('span', { class: 'shoal-meta shoal-action__note', text: t(paused ? 'resumeHelp' : 'pauseHelp', { friend: f.friendName }) })))));
        if (f.status === 'AccessRemoved') {
            panelNode.append(el('div', { class: 'shoal-detail__alert' }, hint('bad', 'keyoff', t('friendAccessRemovedHint', { friend: f.friendName }))));
        }

        if (f.rotationFailed) {
            panelNode.append(el('div', { class: 'shoal-detail__alert' }, hint('bad', 'keyoff', t('friendRotationFailed', { friend: f.friendName }))));
        }

        return panelNode;
    }

    /* ---------- approval (6.18) ---------- */

    function approvalSection(f) {
        if (st.cancelled) {
            return el('section', { class: 'shoal-panel shoal-outcome shoal-outcome--off', role: 'alert' },
                el('div', { class: 'shoal-outcome__icon shoal-tone--off' }, icon('x')),
                el('h2', { class: 'shoal-h2', text: t('shareCancelledTitle') }),
                paragraph(t('shareCancelledBody', { friend: f.friendName }), 'shoal-lead'));
        }

        const a = f.approval;
        if (!a || a.state !== 'pending') {
            st.card = null;
            return null;
        }

        const card = approvalCard(ctx, {
            friend: f.friendName,
            approval: a,
            reconnect: (f.deviceCount || 0) > 0,
            onApprove: (sas) => ctx.api.decideApproval(handle, true, sas),
            onDeny: () => ctx.api.decideApproval(handle, false),
            onResolved: (r) => {
                if (r.state === 'approved') {
                    ctx.announce(t('announceApprovedShare', { friend: f.friendName, home: a.homeName }));
                } else if (r.state === 'cancelledWrongCode') {
                    st.cancelled = true;
                } else {
                    ctx.announce(t('announceDenied'));
                }

                load(false, true);
            }
        });
        st.card = card;
        return card.root;
    }

    /* ---------- invite ---------- */

    function inviteSection(f) {
        const sec = section(t('inviteSectionTitle'));
        const host = sec.body;
        const inv = f.invite || { state: 'none' };
        const neverConnected = (f.deviceCount || 0) === 0;
        const makeBtn = (variantPrimary) => button({
            label: t(inv.state === 'active' || st.invite ? 'newInvite' : 'createInvite'),
            variant: variantPrimary ? 'primary' : 'quiet',
            onClick: (ev, btn) => runAction(ctx, btn, {
                busyText: t('creatingInvite'),
                run: () => ctx.api.createInvite(handle),
                onDone: (invite) => {
                    st.invite = invite;
                    st.cancelled = false;
                    load(false, true);
                },
                onError: (err) => ctx.alertMessage(failureMessage(err))
            })
        });
        const cancelBtn = linkButton(t('cancelInvite'), async () => {
            try {
                await ctx.api.cancelInvite(handle);
            } catch {
                /* it expires on its own */
            }

            st.invite = null;
            load(false, true);
        });

        if (f.approval && f.approval.state === 'pending') {
            // The invite was used: the card above is the next step, so the section says so and offers only to cancel (Lumen S7).
            host.append(el('p', { class: 'shoal-meta', text: t('inviteUsed', { home: f.approval.homeName }) }), el('div', { class: 'shoal-inline' }, cancelBtn));
        } else if (st.invite && st.invite.invite) {
            stopCountdown();
            cdRef = countdown(ctx, st.invite.expiresUtc, () => {
                st.invite = null;
                ctx.announce(t('announceInviteExpired'));
                load(false, true);
            }, 'invite');
            host.append(
                paragraph(t('inviteBody', { friend: f.friendName })),
                inviteBox(st.invite.invite),
                el('div', { class: 'shoal-inline' }, copyButton(ctx, st.invite.invite, 'copyInvite', 'announceInviteCopied'), makeBtn(false), cancelBtn),
                cdRef.root,
                el('div', { class: 'shoal-hint shoal-hint--warn', role: 'note' }, icon('warn', true), el('div', { class: 'shoal-hint__body', text: t('inviteShownOnce') })));
        } else if (inv.state === 'active') {
            host.append(
                el('p', { class: 'shoal-pairstatus shoal-tone--info' }, icon('clock'), el('span', { text: t('inviteActive', { time: ctx.fmt.clock((toMs(inv.expiresUtc) - ctx.now()) / 1000) }) })),
                el('div', { class: 'shoal-inline' }, makeBtn(false), cancelBtn));
        } else if (inv.state === 'expired') {
            host.append(el('p', { class: 'shoal-pairstatus shoal-tone--off' }, icon('clock'), el('span', { text: t('inviteExpiredLine') })), el('div', { class: 'shoal-step__actions' }, makeBtn(true)));
        } else {
            host.append(paragraph(t('inviteNone', { friend: f.friendName }), 'shoal-meta'), el('div', { class: 'shoal-step__actions' }, makeBtn(neverConnected)));
        }

        return sec.root;
    }

    /* ---------- devices ---------- */

    function devicesSection(f) {
        const sec = section(t('devicesTitle'), { intro: t('devicesIntro', { friend: f.friendName }) });
        const devices = f.devices || [];
        if (!devices.length) {
            sec.body.append(el('p', { class: 'shoal-meta shoal-emptyline', text: t('noDevices', { friend: f.friendName }) }));
            return sec.root;
        }

        sec.body.append(el('ul', { class: 'shoal-list' }, devices.map((d) => {
            const rm = button({
                label: t('remove'),
                small: true,
                onClick: async () => {
                    const res = await confirmDialog(ctx, { title: t('removeDeviceTitle'), intro: t('removeDeviceBody', { friend: f.friendName }), confirmLabel: t('removeDevice'), danger: true });
                    if (res.confirmed) {
                        try {
                            await ctx.api.removeDevice(handle, d.deviceHandle);
                            ctx.announce(t('announceDeviceRemoved'));
                            load(false, true);
                        } catch (err) {
                            ctx.fail(failureMessage(err));
                        }
                    }
                }
            });
            return el('li', { class: 'shoal-row' + (d.expected ? '' : ' is-unexpected') },
                el('div', { class: 'shoal-row__main' },
                    el('span', { class: 'shoal-row__title', text: d.name }),
                    el('span', { class: 'shoal-row__meta shoal-meta', text: t('deviceMeta', { app: d.app, version: d.version, first: ctx.fmt.date(d.firstSeenUtc), last: ctx.fmt.relative(d.lastSeenUtc) }) })),
                el('div', { class: 'shoal-row__controls shoal-row__controls--inline' },
                    d.expected ? chip('ok', 'server', t('deviceExpected', { friend: f.friendName })) : chip('warn', 'warn', t('unexpectedDevice')), rm));
        })));
        return sec.root;
    }

    /* ---------- libraries and quality ---------- */

    function librariesSection(f) {
        const sec = section(t('friendLibrariesTitle'));
        sec.root.id = 'shoal-friend-libraries';
        const picked = new Set(f.libraryIds || []);
        const boxes = (f.availableLibraries || []).map((l) => {
            const fromFriend = l.note === 'fromFriend' || l.reason === 'federated';
            const selectable = l.selectable !== false && l.shareable !== false;
            return checkRow({
                id: uid('shoal-fl'),
                label: l.name,
                note: fromFriend ? t('libFromFriend') : selectable ? null : libLaterNote(l.type || 'movies'),
                disabled: !selectable,
                checked: picked.has(l.id),
                onChange: (c) => (c ? picked.add(l.id) : picked.delete(l.id))
            }).root;
        });
        const limit = selectField({
            id: uid('shoal-fq'),
            label: t('fQualityLimit'),
            options: [{ value: '', label: t('limitNoLimit') }, { value: '40', label: t('limit40') }, { value: '20', label: t('limit20') }, { value: '10', label: t('limit10') }, { value: '4', label: t('limit4') }],
            value: f.maxBitrateMbps ? String(f.maxBitrateMbps) : ''
        });
        const transcodes = numberField({ id: uid('shoal-ft'), label: t('fTranscodes'), value: f.maxConcurrentTranscodes || 4, min: 1, max: 10, help: t('fTranscodesHelp', { friend: f.friendName }) });
        const saved = el('span', { class: 'shoal-meta shoal-tone--ok', role: 'status', hidden: true }, icon('ok', true), t('saved'));
        const save = button({ label: t('save'), variant: 'primary', onClick: (ev, btn) => {
            const n = Number(transcodes.input.value);
            const ok = Number.isInteger(n) && n >= 1 && n <= 10;
            transcodes.setError(ok ? '' : t('errWholeNumber', { min: 1, max: 10 }));
            if (!ok) {
                transcodes.input.focus();
                return;
            }

            runAction(ctx, btn, {
                busyText: t('saving'),
                run: () => ctx.api.updateShare(handle, { friendName: f.friendName, libraryIds: Array.from(picked), maxBitrateMbps: limit.select.value ? Number(limit.select.value) : null, maxConcurrentTranscodes: n }),
                onDone: () => {
                    saved.hidden = false;
                    ctx.announce(t('saved'));
ctx.toast(t('saved'));
                },
                onError: (err) => ctx.alertMessage(failureMessage(err, 'saveFailed'))
            });
        } });
        sec.body.append(el('div', { class: 'shoal-form' }, el('fieldset', { class: 'shoal-choices shoal-choices--checks' }, el('legend', { class: 'shoal-label', text: t('fLibrariesToShare') }), boxes), limit.root, transcodes.root, el('div', { class: 'shoal-step__actions' }, save, saved)));
        return sec.root;
    }

    /* ---------- stop sharing (4.5): one guided flow ---------- */

    function stopSection(f) {
        const sec = el('section', { class: 'shoal-danger', 'aria-labelledby': 'shoal-stop-h-' + ctx.instance });
        const btn = button({
            label: t('stopSharingButton'),
            onClick: async () => {
                const res = await openDialog(ctx, {
                    title: t('stopSharingDialogTitle', { friend: f.friendName }),
                    safeFocus: true,
                    build(dBody, dlg) {
                        dBody.append(el('ul', { class: 'shoal-bullets' }, ['stopEffect1', 'stopEffect2', 'stopEffect3', 'stopEffect4'].map((k) => el('li', { text: t(k, { friend: f.friendName }) }))), paragraph(t('stopAgainLater'), 'shoal-meta'));
                        const typed = textField({ id: uid('shoal-typed'), label: t('typeToConfirm', { text: f.friendName }), help: t('typedHelp'), required: true });
                        typed.input.addEventListener('input', () => {
                            dlg.buttons.confirm.disabled = typed.input.value.trim() !== f.friendName;
                        });
                        dBody.append(typed.root);
                        if (watchedLink()) {
                            dBody.append(paragraph(t('revokeStillWatching', { name: watchedLink().name }), 'shoal-meta'));
                        }

                        return null;
                    },
                    buttons: [
                        { label: t('cancel'), close: undefined },
                        { label: t('deleteAccessButtonShort', { friend: f.friendName }), key: 'confirm', variant: 'danger', disabled: true, close: 'confirm' }
                    ]
                });
                if (res !== 'confirm') {
                    return;
                }

                try {
                    const result = await ctx.api.deleteShare(handle, f.friendName);
                    st.result = Object.assign({ friend: f.friendName, mode: f.hardeningMode, reach: f.reach }, result);
                    poller.stop();
                    stopCountdown();
                    st.invite = null;
                    renderResult();
                } catch (err) {
                    ctx.fail(failureMessage(err));
                }
            }
        });
        sec.append(el('h2', { class: 'shoal-h2', id: 'shoal-stop-h-' + ctx.instance, text: t('stopSharingTitle', { friend: f.friendName }) }), paragraph(t('stopSharingBody', { friend: f.friendName })), btn);
        return sec;
    }

    function renderResult() {
        const r = st.result;
        clear(body);
        const items = [
            { state: r.disabled ? 'done' : 'failed', text: t('resultDisabled') },
            { state: 'done', text: t('resultSignedOut', { n: r.signedOut, count: ctx.fmt.num(r.signedOut) }) },
            r.remaining > 0
                ? { state: 'failed', lead: t('resultLeftLead', { n: r.remaining }), text: t('resultLeftBody') }
                : { state: 'done', text: t('resultNoneLeft') },
            r.deleted ? { state: 'done', text: t('resultDeleted') } : { state: 'failed', lead: t('resultNotDeletedLead'), text: t('resultNotDeletedBody') }
        ];
        const tsDone = checkRow({ id: uid('shoal-ts'), label: t('stopDone') });
        const more = [
            { state: 'todo', n: 1, text: t(r.reach === 'public' ? 'stopStepPublic' : 'stopStepTailscale', { friend: r.friend }), action: tsDone.root },
            { state: 'todo', n: 2, text: t('stopProtect_' + (['Enforce', 'LogOnly', 'Off'].includes(r.mode) ? r.mode : 'LogOnly')), action: ctx.linkEl('settings', {}, 'shoal-link', t('openSettings')) }
        ];
        const panelNode = el('section', { class: 'shoal-panel shoal-result' },
            el('h1', { class: 'shoal-h1', id: h1Id, tabindex: '-1', text: t('stopResultTitle', { friend: r.friend }) }),
            stepChecklist(items),
            el('h2', { class: 'shoal-h3 shoal-result__more', text: t('twoMoreSteps') }),
            stepChecklist(more),
            r.mode !== 'Enforce' ? hint('warn', 'warn', t('stopStepProtectWarn')) : null,
            el('div', { class: 'shoal-step__actions' }, button({ label: t('goToOverview'), variant: 'primary', onClick: () => ctx.navigate('overview') })));
        body.append(panelNode);
        ctx.announce(t('announceAccessDeleted', { friend: r.friend }));
        ctx.focusHeading(q(body, 'h1'));
    }

    function render() {
        const f = st.data;
        stopCountdown();
        clear(body);
        body.append(headerPanel(f));
        const approval = approvalSection(f);
        if (approval) {
            body.append(approval);
        }

        const also = youAlsoWatchPanel();
        if (also) {
            body.append(also);
        }

        body.append(inviteSection(f), devicesSection(f), librariesSection(f), stopSection(f));
        const target = ctx.params.focus === 'libraries' && !st.focused ? q(body, '#shoal-friend-libraries') : null;
        if (target) {
            st.focused = true;
            ctx.later(() => scrollToNode(target), 50);
        }
    }

    function signature(data) {
        // The approval's tries counter changes while the admin is typing; it must not redraw the card.
        const copy = JSON.parse(JSON.stringify(data));
        if (copy.approval) {
            delete copy.approval.triesLeft;
        }

        return JSON.stringify(copy) + '|' + (st.invite ? 'i' : '') + (st.cancelled ? 'c' : '');
    }

    async function load(initial, force) {
        if (st.result) {
            return;
        }

        if (initial && !st.loaded) {
            replaceKids(body, el('div', { class: 'shoal-skel', 'aria-busy': 'true' }, el('span', { class: 'shoal-sr', text: t('loadingShoal') }), el('div', { class: 'shoal-skel__panel' }), el('div', { class: 'shoal-skel__panel shoal-skel__panel--short' }), el('div', { class: 'shoal-skel__panel shoal-skel__panel--short' })));
        }

        try {
            const data = await ctx.api.share(handle);
            if (!ctx.alive || st.result) {
                return;
            }

            const first = !st.loaded;
            const sig = signature(data);
            st.data = data;
            st.loaded = true;
            const active = document.activeElement;
            const focusInside = active && body.contains(active) && active !== document.body && active.tagName !== 'H1';
            if (first || force || (sig !== st.sig && !focusInside)) {
                st.sig = sig;
                render();
                if (first) {
                    ctx.focusHeading(q(body, 'h1'));
                }
            } else if (st.card && data.approval && data.approval.state === 'pending') {
                st.card.update(data.approval);
            }
        } catch (err) {
            if (!ctx.alive) {
                return;
            }

            if (err.status === 404) {
                ctx.notFound();
                return;
            }

            if (!st.loaded) {
                replaceKids(body, loadErrorBox(() => load(true)));
            }
        }
    }

    return {
        show() {
            load(!st.loaded);
            poller.start();
        },
        hide() {
            poller.stop();
            // The invite lives only on this page: leaving clears it from the DOM (ui-spec 6.10).
            if (st.invite) {
                st.invite = null;
                stopCountdown();
                if (st.data && !st.result) {
                    render();
                }
            }
        },
        destroy() {
            poller.stop();
            stopCountdown();
        },
        refresh: () => load(false, true),
        state: () => st
    };
}

/* ====================================================================================== */
/* Page banners (ui-spec 6.16, 8.8): recovery mode, keys that can't be unlocked, restored  */
/* ====================================================================================== */


/** Decides which banner (at most one) the Status response calls for. */
function bannerModel(status) {
    if (!status) {
        return null;
    }

    const state = status.state || {};
    if (state.mode === 'Recovery') {
        return { kind: 'recovery', tone: 'bad', reason: state.reason === 'newerSchema' ? 'newerSchema' : 'unreadable', guard: state.guardLastGood === 'ok' || state.guardLastGood === 'unreadable' ? state.guardLastGood : '' };
    }

    if (status.secretsUnreadable) {
        return { kind: 'keys', tone: 'bad', keySource: status.keySource, reason: status.secretsReason === 'badKey' || status.secretsReason === 'bothKeys' ? status.secretsReason : '' };
    }

    if (state.restoredFrom && state.restoredFrom.needsConfirm) {
        return { kind: 'restored', tone: 'info', restoredFrom: state.restoredFrom };
    }

    return null;
}

function bannerSignature(model) {
    return model ? [model.kind, model.reason || '', model.guard || '', model.keySource || '', model.restoredFrom ? model.restoredFrom.atUtc : ''].join('|') : '';
}

async function openRestoreDialog(ctx) {
    let state = null;
    try {
        state = await ctx.api.state();
    } catch (err) {
        ctx.alertMessage(failureMessage(err));
        return false;
    }

    const files = (state.files || []).slice().sort((a, b) => toMs(b.writtenUtc) - toMs(a.writtenUtc));
    let chosen = files.length ? files[0].file : null;
    const name = 'shoal-restore-' + ctx.instance;
    const result = await openDialog(ctx, {
        title: t('restoreTitle'),
        build(body) {
            if (!files.length) {
                body.append(paragraph(t('restoreNone')));
                return null;
            }

            const group = el('fieldset', { class: 'shoal-choices' }, el('legend', { class: 'shoal-sr', text: t('restoreTitle') }));
            files.forEach((f, i) => {
                const card = choiceCard({
                    name,
                    value: f.file,
                    title: t('restoreFile', { file: f.file, date: ctx.fmt.date(f.writtenUtc) }),
                    body: i === 0 ? t('restoreNewest') : t('restoreOlder'),
                    checked: i === 0,
                    onChange: (v) => {
                        chosen = v;
                    }
                });
                group.append(card.root);
            });
            body.append(paragraph(t('restoreBody'), 'shoal-meta'), group);
            return null;
        },
        buttons: [
            { label: t('cancel'), close: undefined },
            { label: t('restoreButton'), variant: 'primary', disabled: !files.length, onClick: async (dlg, btn) => {
                setBusy(btn, true, t('applying'));
                try {
                    await ctx.api.restoreState(chosen);
                    dlg.close('done');
                } catch (err) {
                    setBusy(btn, false);
                    dlg.setError(failureMessage(err));
                }
            } }
        ]
    });
    return result === 'done';
}

function renderBanners(ctx, host, model) {
    const sig = bannerSignature(model);
    if (host.dataset.sig === sig) {
        return;
    }

    host.dataset.sig = sig;
    clear(host);
    if (!model) {
        return;
    }

    const titleId = uid('shoal-banner-t');
    const bad = model.tone === 'bad';
    const body = el('div', { class: 'shoal-banner__text' });
    const actions = el('div', { class: 'shoal-banner__actions' });
    let title = '';
    if (model.kind === 'recovery') {
        title = t('bannerRecoveryTitle');
        body.append(el('p', { class: 'shoal-banner__body', text: t(model.reason === 'newerSchema' ? 'bannerRecoveryNewer' : 'bannerRecoveryBody') + (model.guard ? ' ' + t(model.guard === 'ok' ? 'bannerGuardOk' : 'bannerGuardNo') : '') }));
        actions.append(
            button({ label: t('bannerRestore'), variant: 'primary', onClick: async () => {
                if (await openRestoreDialog(ctx)) {
                    ctx.announce(t('announceRestored'));
                    ctx.reloadPage();
                }
            } }),
            button({ label: t('bannerStartOver'), onClick: async () => {
                const res = await confirmDialog(ctx, {
                    title: t('bannerStartOverTitle'),
                    intro: t('bannerStartOverBody'),
                    typed: { label: t('typeToConfirm', { text: t('startOverPhrase') }), expected: t('startOverPhrase') },
                    confirmLabel: t('bannerStartOverConfirm'),
                    danger: true
                });
                if (res.confirmed) {
                    try {
                        await ctx.api.startOver(t('startOverPhrase'));
                        ctx.announce(t('announceStartedOver'));
                        ctx.reloadPage();
                    } catch (err) {
                        ctx.fail(failureMessage(err));
                    }
                }
            } }));
    } else if (model.kind === 'keys') {
        title = t('bannerKeysTitle');
        const fixKey = model.reason === 'badKey' ? 'bannerKeysFix_badKey' : model.reason === 'bothKeys' ? 'bannerKeysFix_bothKeys' : 'bannerKeysFix_' + (['env', 'envFile', 'file', 'dpapi'].includes(model.keySource) ? model.keySource : 'file');
        const bodyKey = model.reason === 'badKey' ? 'bannerKeysBadKey' : model.reason === 'bothKeys' ? 'bannerKeysBothKeys' : 'bannerKeysBody';
        body.append(
            el('p', { class: 'shoal-banner__body', text: t(bodyKey) }),
            collapse(t('bannerKeysHow'), [el('p', { text: t(fixKey) }), model.reason === 'badKey' || model.reason === 'bothKeys' ? null : el('p', { text: t('bannerKeysOr') })]));
    } else {
        title = t('bannerRestoredTitle');
        const f = model.restoredFrom;
        body.append(el('p', { class: 'shoal-banner__body', text: t('bannerRestoredBody', { file: f.file, date: ctx.fmt.date(f.atUtc) }) }));
        actions.append(
            button({ label: t('checkLibraries'), onClick: () => ctx.navigate('libraries') }),
            button({ label: t('checkFriends'), onClick: () => ctx.navigate('overview', { focus: 'friends' }) }),
            button({ label: t('confirm'), variant: 'primary', onClick: (ev, btn) => runAction(ctx, btn, {
                busyText: t('confirming'),
                run: () => ctx.api.confirmRestore(),
                onDone: () => {
                    ctx.announce(t('announceRestoreConfirmed'));
                    if (ctx.status && ctx.status.state) {
                        ctx.status.state.restoredFrom = null;
                    }

                    ctx.refreshBanner();
                },
                onError: (err) => ctx.alertMessage(failureMessage(err))
            }) }));
    }

    body.prepend(el('p', { class: 'shoal-banner__title', id: titleId, text: title }));
    const iconNode = icon(bad ? 'x' : 'info');
    iconNode.classList.add('shoal-banner__icon');
    const node = el('section', { class: 'shoal-banner shoal-banner--' + model.tone, 'aria-labelledby': titleId }, iconNode, body, actions.children.length ? actions : null);
    host.append(node);

    // Announced once per page load; polling never repeats it (ui-spec 6.16).
    if (ctx.bannerAnnounced !== model.kind) {
        ctx.bannerAnnounced = model.kind;
        if (bad) {
            ctx.alertMessage(title);
        } else {
            ctx.announce(title);
        }
    }

    ctx.bannerTitleId = titleId;
    const h1 = ctx.root.querySelector('[data-shoal-view]:not([hidden]) h1');
    if (h1) {
        h1.setAttribute('aria-describedby', titleId);
    }
}

/* ====================================================================================== */
/* Controller                                                                             */
/* ====================================================================================== */

const SCREEN_FACTORIES = {
    overview: overviewScreen,
    libraries: librariesScreen,
    settings: settingsScreen,
    pair: pairScreen,
    server: serverDetailScreen,
    overlap: overlapReportScreen,
    friend: friendDetailScreen
};

/** The h1 shown in recovery mode, where the real screen is replaced by the banner (ui-spec 8.8). */
const RECOVERY_TITLES = { overview: 'appName', libraries: 'navLibraries', settings: 'navSettings', pair: 'pairServer', server: 'appName', overlap: 'overlapReport', friend: 'appName' };

/** Where the back link of each sub-screen goes (ui-spec 2.2, section 8). */
function backTarget(view, params) {
    if (view === 'overlap' && params.from === 'server' && params.id) {
        return { view: 'server', query: { id: params.id }, label: 'backServer' };
    }

    return { view: 'overview', query: {}, label: 'backOverview' };
}

function cssReady(page) {
    return new Promise((resolve) => {
        let link = page.querySelector('link[data-shoal-css]');
        if (!link) {
            link = document.createElement('link');
            link.rel = 'stylesheet';
            link.setAttribute('data-shoal-css', '');
            const client = window.ApiClient;
            link.href = client && typeof client.getUrl === 'function' ? client.getUrl('web/ConfigurationPage', { name: 'Shoal.css' }) : 'configurationpage?name=Shoal.css';
            page.prepend(link);
        }

        if (link.sheet) {
            resolve();
            return;
        }

        const done = () => resolve();
        link.addEventListener('load', done, { once: true });
        link.addEventListener('error', done, { once: true });
        setTimeout(done, 3000);
    });
}

/**
 * Mounts one page instance. Exported for the development harness and the render tests; Jellyfin
 * itself only uses the default export.
 */
function mount(page, params, deps) {
    const ctx = createContext(page, params || {}, deps);
    const root = ctx.root;
    uniquify(page, ctx.instance);
    applyStrings(page);
    applyTheme(page);
    let view = params && VIEWS.includes(params.view) ? params.view : 'overview';
    const unknownView = !!(params && params.view && !VIEWS.includes(params.view));
    ctx.view = view;
    ctx.notFound = () => {
        showNotice(t('noticeGone'));
        switchTo('overview', true);
        if (!recovery) {
            screen.show(false);
        }
    };
    ctx.status = null;
    ctx.reloadPage = () => window.location.reload();

    const notice = slot(root, 'notice');
    function showNotice(text) {
        notice.hidden = !text;
        slot(notice, 'noticetext').textContent = text || '';
    }

    const localnav = slot(root, 'localnav');
    const backbar = slot(root, 'backbar');
    const bannerHost = slot(root, 'banner');
    const recoveryView = q(root, '[data-shoal-view="recovery"]');
    let screen = null;
    let recovery = false;
    let started = false;

    /**
     * Every screen hands its Status response here: banners (ui-spec 8.8) come from it on every
     * screen, and recovery mode replaces the screen with the banner, the h1 and one line.
     */
    ctx.applyStatus = (data) => {
        ctx.status = data;
        const model = bannerModel(data);
        renderBanners(ctx, bannerHost, model);
        setRecovery(!!model && model.kind === 'recovery');
    };
    ctx.refreshBanner = () => renderBanners(ctx, bannerHost, bannerModel(ctx.status));

    function setRecovery(on) {
        if (on === recovery) {
            return;
        }

        recovery = on;
        root.dataset.recovery = on ? 'true' : 'false';
        if (on) {
            if (screen) {
                screen.hide();
            }

            for (const section of qa(root, '[data-shoal-view]')) {
                section.hidden = section.getAttribute('data-shoal-view') !== 'recovery';
            }

            const h1 = q(recoveryView, 'h1');
            h1.textContent = t(RECOVERY_TITLES[view] || 'appName');
            if (ctx.bannerTitleId) {
                h1.setAttribute('aria-describedby', ctx.bannerTitleId);
            }

            if (started) {
                ctx.focusHeading(h1);
            }
        } else {
            recoveryView.hidden = true;
            switchTo(view, false);
            if (started) {
                screen.show(false);
            }
        }
    }

    function switchTo(next, replace) {
        if (screen) {
            screen.hide();
            if (screen.destroy) {
                screen.destroy();
            }
        }

        view = next;
        ctx.view = next;
        root.dataset.view = next;
        for (const section of qa(root, '[data-shoal-view]')) {
            section.hidden = recovery ? section.getAttribute('data-shoal-view') !== 'recovery' : section.getAttribute('data-shoal-view') !== next;
        }

        const top = TOP_VIEWS.includes(next);
        localnav.hidden = !top && !recovery;
        backbar.hidden = top || recovery;
        for (const a of qa(localnav, '[data-nav]')) {
            const v = a.getAttribute('data-nav');
            ctx.link(a, v);
            if (v === next) {
                a.setAttribute('aria-current', 'page');
            } else {
                a.removeAttribute('aria-current');
            }
        }

        if (replace) {
            ctx.params = {};
        }

        if (!top) {
            const target = backTarget(next, ctx.params);
            const back = slot(backbar, 'back');
            ctx.link(back, target.view, target.query);
            slot(backbar, 'backlabel').textContent = t(target.label);
        }

        screen = SCREEN_FACTORIES[next](ctx);
        if (started && !recovery) {
            screen.show(false);
            ctx.focusHeading();
        }
    }

    switchTo(view, false);
    if (unknownView) {
        showNotice(t('noticeGone'));
    }

    // Banners (and recovery mode) come from Status on every screen; the screen itself starts only once
    // Status says it may, so a page in recovery mode never calls endpoints that would fail.
    const statusReady = () => ctx.api.status().then((d) => {
        if (ctx.alive) {
            ctx.applyStatus(d);
        }
    }).catch(() => {});
    let firstStatus = statusReady();

    cssReady(page).then(() => {
        root.dataset.state = 'ready';
        if (started && ctx.alive) {
            ctx.focusHeading();
        }
    });

    // Jellyfin dispatches viewshow right after it constructs the controller, and again whenever it
    // restores a cached page (isRestored). Screens start loading here and stop on viewhide.
    const onShow = (ev) => {
        ctx.shown = true;
        started = true;
        applyTheme(page);
        const restored = !!(ev && ev.detail && ev.detail.isRestored);
        const pending = restored ? statusReady() : firstStatus;
        firstStatus = null;
        Promise.resolve(pending).then(() => {
            if (!ctx.alive || !ctx.shown) {
                return;
            }

            if (recovery) {
                ctx.focusHeading(q(recoveryView, 'h1'));
                return;
            }

            if (screen) {
                screen.show(restored);
            }

            ctx.focusHeading();
        });
    };
    const onHide = () => {
        ctx.shown = false;
        if (screen) {
            screen.hide();
        }
    };
    const onDestroy = () => {
        ctx.alive = false;
        ctx.shown = false;
        ctx.clearAllTimers();
        if (screen) {
            screen.hide();
            if (screen.destroy) {
                screen.destroy();
            }
        }

        if (themeObserver) {
            themeObserver.disconnect();
        }
    };
    page.addEventListener('viewshow', onShow);
    page.addEventListener('viewhide', onHide);
    page.addEventListener('viewdestroy', onDestroy);
    // Errors that escape every handler (a script error, a rejected promise nobody awaited) are shown, not swallowed.
    activeReporter = (message) => ctx.fail(message);
    const onWindowError = (ev) => {
        if (ctx.alive && ctx.shown) {
            reportUnexpected(ev && (ev.error || { message: ev.message }));
        }
    };
    const onRejection = (ev) => {
        if (ctx.alive && ctx.shown) {
            reportUnexpected(ev && ev.reason);
        }
    };
    window.addEventListener('error', onWindowError);
    window.addEventListener('unhandledrejection', onRejection);
    page.addEventListener('viewdestroy', () => {
        window.removeEventListener('error', onWindowError);
        window.removeEventListener('unhandledrejection', onRejection);
        if (activeReporter) {
            activeReporter = null;
        }
    });

    let themeObserver = null;
    if (typeof MutationObserver !== 'undefined') {
        themeObserver = new MutationObserver(() => applyTheme(page));
        themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'class', 'style'] });
    }

    return { ctx, screen: () => screen, destroy: onDestroy };
}

/**
 * The module's default export: Jellyfin calls it with `new` for every page instance it creates
 * (viewManager: new controllerFactory.default(view, params)), so it must be a plain function.
 */
export default function ShoalController(view, params) {
    mount(view, params, defaultDeps());
}

/* Named exports are for the development harness and tests; Jellyfin ignores them. */
export {
    mount, createApi, createContext, makeFormat, chartModel, chartSummary, niceCeil, describeServer, describeFriend,
    summarizeServers, pickSelection, sortServers, verdictLine, serverStateKey, luminanceOf, uniquify, t, STRINGS, QUALITY_LINES,
    admittedUsers, bottleneckText, ladderRows, nameList, toApiError, bannerModel, MISSING_STRINGS, overlapSentences, currentUserId, readAddress, classifyCode, inFours, fingerprintGroups, outcomeInfo, normalizeCode, validCode, looksLikeInvite, describeLinkStatus
};
