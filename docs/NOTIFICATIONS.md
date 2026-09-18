# Notifications and tray presence

The companion runs on a second screen while the game has the first one. Nobody is looking at it
during a fight, so the question this feature answers is not "what could we tell him" but "what
would he want to be interrupted for". The answer is five things, and the list is closed.

## The five

| Notification | Fires | Source |
| --- | --- | --- |
| A squadmate dropped a ping or a mark | **during a raid** | `GroupSnapshot.Waypoints` / `.Pings` |
| A raid ended and the debrief is ready | after the raid | `RaidSnapshot.State` reaching `PostRaid` |
| The squad relay went unreachable while sharing was on | after the raid | `GroupSnapshot.StaleSince` |
| The game-data refresh failed, naming the endpoints | after the raid | `RuntimeDataState.FailedEndpoints` |
| A newer build is downloaded and waiting | after the raid | `SettingsPageViewModel.CanRestartForUpdate` |

Only the first one interrupts a raid. The other four are suppressed while `RaidLifecycleState` is
`InRaid` and — this is the part worth keeping — the coordinator does not record that it suppressed
them, so the condition is still true when the raid ends and the notification arrives then rather
than being swallowed.

## The three rules that cut across all five

- **Nothing repeats.** Each is keyed on its own cause: a mark's relay id, a raid's id, which
  endpoints failed, which build is waiting, one relay outage. A cause that clears (a clean refresh,
  a relay that answers again) re-arms it, so tomorrow's failure is news again.
- **Nothing fires for something the player did.** A mark this player dropped is never announced
  back to them, matched on the relay display name from `IGroupSettingsStore`.
- **A burst becomes one line.** Marks are gathered for four seconds, or until five have arrived,
  and come out as "3 marks from Ferret" or "4 marks from 2 squadmates".

`NotificationCoordinator` holds all of it and has no dependencies: it is handed a
`NotificationInputs` snapshot and returns the requests worth raising, which is why every rule above
is a test with no timer in it. `NotificationBridge` is the only thing that knows where the inputs
come from, and has no judgement of its own.

## Where a notification goes

The tray is the quiet channel and the default one: the icon carries the raid state, and the tooltip
carries the detail and a count of what has not been looked at. Nothing is drawn on screen unless
`ShowsDesktopPopup` is switched on, which it is not by default — that is the whole of "nothing pops
over a full-screen game unless the player asked for it". The pop-up is an in-window toast rather
than a desktop one, so even switched on it cannot appear over a game that is covering the window.

## Tray presence

`TrayPresence` is Windows only, the way the rest of the platform layer draws its line; everywhere
else it reports unavailable and the application behaves exactly as it did before. When it is there,
closing the window hides it instead of quitting (the shutdown mode moves to explicit), and the tray
menu — Show, Raid, Team, Setup, Quit — is the way back. `TrayPresenceState` decides the words and
the colour and has no Avalonia in it, so what the tray says is tested rather than photographed.

The icon is the application icon with a state-coloured dot: cyan in a raid, amber loading, green
for a finished raid with a debrief waiting, red when something needs attention. Composing it needs
a rendering platform, so the fallback ladder is the coloured icon, then the plain application icon,
then no tray at all.

## Setup › Notifications

Every notification has a row: what it is in one plain sentence, whether it reaches you mid-raid, a
switch, and a **Test this** button that sends the real notification through the real channels. Four
of the five can otherwise only be seen by waiting for something to go wrong, which is a poor way to
discover that a switch does nothing. The test ignores the switch beside it on purpose — pressing it
on a notification you have turned off is how you decide whether to turn it on.

Settings live in `Config/notifications.json`. An unreadable file falls back to the defaults (all
five on, no pop-up) rather than to silence, and each switch is defaulted per property so a file
written by an older build cannot silently turn off a notification added later.
