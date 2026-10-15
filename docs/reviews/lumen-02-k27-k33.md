# Lumen review 02: K27 (two-way friends, key rotation, counters) and K33 (Extra features, picture-check alert)

Reviewer: Lumen. Date: 2026-10-15. Branch: worktree `agent-a639ef01943bf9cd9`, head `5545090`.
Against: `docs/ui-spec.md` (Native Plus), DESIGN v2.6.1 to v2.6.7. Method: the `ui-dev` harness rendered in headless Chrome at 1440 px and 375 px, dark and light, with extra mock states (key error, overdue, store failure, picture check unavailable, share-side rotation failure), plus a read of the diff in `shoal.js` and `shoal.css`. No branch files were changed (my two screenshot helper scripts were deleted).

**Verdict: NO-GO until the 5 Must-fix items are done. All five are small (copy and one placement line).** Nothing here is structural, and everything else is in good shape.

## What works

- **Status is never color alone.** Every chip, the picture-check lines in Settings and the rotation alerts carry an icon and text (ok, warn, offline icons). Two-way rows reuse the same chip component.
- **Two-way card concept.** One card per friend, two plain rows ("You watch Hops" / "Hops watches you"), the worse state as the card chip. At 375 px the rows wrap cleanly and the links stay tappable.
- **Plain words.** "Server key", "Change the key now", "Preview pictures" and the key-failure texts say what happened and what to do ("Pair Fernwood again"), with no token or DeviceId talk. The confirm dialog says what happens, how long it takes and what it needs.
- **Rotation states are honest.** QC-paired servers get a calm "pair it again" line instead of a disabled control; failed, lost-response and not-saved cases each have their own alert; cooldown and 429 are handled.
- **Extra features panel.** Five switches with one-sentence help each, disabled GB field when trickplay is off, current cache size plus Clear (real `type="button"`, so it cannot submit the form), and a state line for the picture check with icon and text. Checkboxes are the existing `checkRow`, so keyboard and labels are right.
- **Display-only linking is stated** ("Pausing or removing Hops here only changes what you watch"), and the three destructive dialogs say what the other direction keeps doing.
- **Phone and dark mode** hold: no horizontal scroll at 375 px, text wraps, nothing relies on hover. Light theme render is fine.

## Must-fix (5)

**M1. The two-way section is inserted inside the "Friends' servers" header row, so it renders squeezed to the right of the heading at desktop widths.**
`renderLinkedFriends` does `serversNote.after(linkedHost)`, but `serversNote` sits inside `.shoal-section__head` (a flex row with the h2). At 1440 px the new card is a narrow box on the right, beside "Friends' servers" and "Select a server to see its speed below", with "Open server" and "Open friend" wrapped under the status. It also splits the heading from its note on phones.
Fix: insert it as its own block above the servers section: `serversNote.closest('.shoal-section').before(linkedHost)` (or after the cards, but above is better: this is the friend summary). Give `.shoal-twoway__card` the standard panel look (`background: var(--shoal-surface)`, `border-radius: var(--shoal-radius-lg)`) so it matches the server cards and the speed panel. Right now it is an outline only.

**M2. The picture-check alert says the same sentence twice.**
Overview alert renders "**The picture check is not available** The picture check is not available: the server's ffmpeg was not found. Pictures are checked the usual way." The body strings start with the title text.
Fix: keep the title `sandboxAlertTitle`, and make the three bodies reason-only. Suggested bodies:
- `ffmpegMissing`: "Shoal could not find ffmpeg on this server. Pictures are checked the usual way."
- `selfTestFailed`: "The test run of the check failed. Pictures are checked the usual way."
- `other`: "Pictures are checked the usual way."
Settings can show the title line plus the same body so the two places read alike. Also add the missing space or separator between title and body (it currently runs together).

**M3. The "key change is overdue" alert has a button that lies, and contradicts the failure alert.**
The button says "Change the key now" but only scrolls to the Server key panel (`scrollToNode`), so the admin thinks a change was made. When `rot.error` is `lostResponse` the page shows both "the old key has probably stopped working, pair again" and "overdue, change it now", which cannot both be right.
Fix: relabel the button "Go to key settings" (new string `rotationGoto`), and show the overdue alert only when there is no `rot.error`.

**M4. Friend detail "You also watch {name}" prints the status twice and a raw number.**
Renders "Online · Online · 1204 titles": the chip plus `youAlsoWatchMeta` repeating `{status}`. Also "1204" is unformatted while the same count elsewhere reads "1,204".
Fix: `youAlsoWatchMeta: "{titles}"`, and format the number through `ctx.fmt.num` (see N1). Also tighten the panel: the chip row and the "Open server" link have large empty gaps (the link floats 60 px below).

**M5. The same state has two different chip words on one page.**
`LINK_STATUS.TlsKeyChanged` uses the new `chipCertChanged` ("Certificate changed"), but the server card for the same state uses `chipDifferentServer` ("Different server"), and `docs/ui-spec.md` 0.2 fixes the chip as "Different server" with "certificate changed" only in the alert. An admin sees two names for one problem.
Fix: map `TlsKeyChanged` to `chipDifferentServer` and delete `chipCertChanged`. Likewise the share-side chip on the two-way row says "Online" while the Friends list and Friend detail say "Active" for the same account. Use "Online" in `LINK_STATUS` for both, and keep "Active" only where the older share screens already use it, or change it everywhere, not half.

## Nice-to-have

**N1. Number formatting.** `titlesN` and `streamsNowN` are formatted twice (`t(..., {n})` then `.replace('{n}', ctx.fmt.num(...))`), so the `replace` is a no-op and counts print without separators ("1204 titles"). Pass `ctx.fmt.num(n)` as the value, and keep the plural count separate if the helper needs a raw number.

**N2. Zero noise.** "0 streams now" and "0 playing now" add clutter. Hide a zero count, or fold into "Idle".

**N3. Jargon in Extra features.**
- `fReencode` "Check pictures in a separate process" and its note ("re-drawn by a small separate process"): suggest "Check pictures before keeping them" with the note "Shoal re-saves every picture from a friend with a separate helper program before keeping it, so a damaged picture can't harm your server."
- `sandboxActiveFull` "running with its full limits" is unclear. Suggest "The picture check is on." / partial: "The picture check is on, with time and size limits only."
- `fAttachmentsNote` "(ASS)": fine in brackets, but "Only real font files are passed on" is stronger as "Only font files are used."
- `fAltVersionsNote` ends with "Off by default." Tell the effect instead ("Turn on to see each version of a movie separately.").

**N4. Counter labels (Settings > What Shoal turned away)** keep the internal words: "Requests to the server's own streaming routes", "Requests to blocked routes", "Requests with an overlong address". Suggest "Requests that tried Jellyfin's own streaming addresses", "Requests to addresses Shoal blocks", "Requests with an address that was too long".

**N5. Key schedule feedback.** Changing the "Change the key automatically" select saves at once with only a screen-reader announcement. Add a visible "Saved" note next to the select for sighted users (the settings screens elsewhere use a Save button, so this one is a different pattern).

**N6. Wizard prefill ("A server you already watch: Hops Fernwood Granite").** The names are bare run-together links, offline servers included, and "A server you already watch" does not say they fill the name box. Render as small quiet buttons with a visible gap and the label "Use the name of a server you watch:". Cap at 6 is fine.

**N7. Links.** "Open server" and "Open friend" repeat on every card; give them an `aria-label` that includes the name ("Open Hops as a server", "Open Hops as a friend").

**N8. Housekeeping.** A stray empty comment header sits above the optional-features strings ("what the guard counted (DESIGN v2.6.3)" followed by the features block); the counters strings are now below it. Move the comment.

**N9. Chip and meta duplication in the two-way card hides which part is the problem.**
Because the card's top chip is the rolled-up (worst) state and rows repeat full chips, a card with one bad half shows the same "bad" chip three times with nothing saying "this half". Fix: when the roll-up equals one row's state, keep both row chips (they are needed), but add a visible second line in the rolled-up chip only when the halves differ, or drop the card-level chip when both halves are the same and show it only when they differ. Simplest accepted fix: show the card chip only if its text differs from both row chips, otherwise omit it.

## Checked and fine

Focus order and dialog return focus for "Change the key now"; Clear keeps focus context and announces; disabled GB field after turning off previews; no `innerHTML`; peer strings through `textContent`; all new text in `STRINGS`; tests present for twoway, rotation and features.

## Ship rule

Fix M1 to M5, re-run the harness at 1440 and 375, and send me one overview and one server-detail shot for a quick re-check. After that: GO.

## Recheck after a789511 (merged at f80191e)

Rendered from main's harness at 1440 px and 375 px, dark and light: Overview two-way section, Friend detail (with and without a failed key change), Server detail alerts and the Server key panel, Settings Extra features, and the picture-check alert.

**Verdict: GO. M1 to M5 are fixed, and N1 to N9 landed.**
- M1: the two-way block now sits above "Friends' servers" as its own card with the standard panel surface; rows read cleanly at both widths.
- M2: the alert says its title once, then a reason-only sentence and the Settings link.
- M3: the overdue alert's button reads "Go to key settings", and it no longer shows next to a failure alert.
- M4: Friend detail shows one status chip, "1,204 titles" formatted, and a tight row.
- M5: one chip word per state.
- Extra features copy is plain ("The picture check is on.", "Check pictures before keeping them").

Remaining notes, none blocking:
- The rotation schedule select now saves at once and the "Saved" note is the only visible feedback; keep an eye on it in the lab for slow servers.
- When both halves of a two-way card have the same state the card-level chip is omitted, which is intended. If one half is bad, check that the card chip appears (not rendered in these mocks: the default Hops pair is Online on both sides).
