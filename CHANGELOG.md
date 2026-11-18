# Changelog

Every build gets a version number. Test builds end in `-dev` (for example `0.6.1-dev`);
releases drop it (`0.6.1`). Newest first. The version is shown at the bottom of Settings.

## Planned
Direction: games first, with groups, chat and voice built around them.
- **0.6.2:** Steam-style game library (sidebar list, hero banner, PLAY / HOST / SET UP action bar,
  players and servers online, friends playing, library shelf home). App opens on the library.
  Metal Fatigue moved onto the same game page as the other games (setup wizard, host, join).
  README leads with the games.
- **0.7:** one-click hosting for Quake III, OpenArena and Unreal Tournament (map, mode, players),
  listed on the master servers and posted to your group.
- **0.8:** setup wizards per game: find or get the game, community patches, master servers, fixes.
- **Later:** more host-required games (BF2Hub, CnCNet, Tribes), file sharing for maps and mods,
  owner-only announcement channels, Matrix support so groups can connect to each other.

## 0.6.2-dev (unreleased)
- **Games first:** the app now opens on a Steam-style **Library**: tall game tiles with their year,
  genre and status (installed / not set up / how it's played). Hover grows a tile; click opens the game.
  Change back to opening on chat with the `ui.start` setting.
- **Library** link at the top of the Games sidebar; the controller button opens the Library.
- **Metal Fatigue page** uses the same layout as the other games: banner with Play and Invite crew,
  a live status line (game folder, mod, server), and a clean menu row (Play / Host a server / Setup / About)
  instead of the old Windows tabs.
- Metal Fatigue sections are dark cards like the rest of the app (no white frames or table headers),
  and the menu row now actually switches between Play, Host a server, Setup and About.
- Discord-style scrollbars everywhere: pages, server lists, player lists and settings all use the same
  thin, rounded gray bar as chat (drag, click or mouse wheel), with no Windows scrollbar or white track.
- No more Windows-drawn controls on the game pages: dark dropdowns with a thin chevron, rounded blue
  checkboxes and radio buttons, flat text boxes, and the port box without the white spinner arrows.

- **New groups start simple:** just #general and one General voice channel. No preset game channels or
  templates; owners add their own text and voice channels in Settings > Host this group (new
  "Voice channels" box). Groups made before this keep their current channels.
- Group server: new `-voice` option for the voice channel list (default "General").
- **Moderation, like Discord's right-click menu** (group owner and admins):
  Server Mute, Server Deafen, Disconnect from voice, Time Out (60 seconds to 1 week: can read but not
  chat or talk), Kick (with an optional reason they see), Ban (can't sign in until unbanned), and for the
  owner, Make Admin / Remove Admin. Nobody can act on the owner; only the owner can act on admins.
  Banned members are listed in Settings > Host this group with an Unban button. All of it is saved with the group.
- **User Volume slider** in the right-click menu for anyone in your voice channel (0-200%, snaps to 100%).
- Fixed: a kicked member's app no longer reconnects on its own.

## 0.6.1 (2026-11-18)
- Fixed: the "Back up my identity" and game folder "Browse..." buttons crashed the app.
- Invite links are clickable https links (GitHub Pages) that open the app or point to the download.
- Opening your own invite link on the PC that hosts the group now takes you to your group
  (via 127.0.0.1) instead of failing on your internet address.
- Shorter password label on the sign-in window.
- Version number shown at the bottom of the Settings menu.
- This changelog.

## 0.6 (2026-11-18, pre-release)
- **Look and feel:** custom Discord-style window and title bar (back/forward, min/max/close),
  pop-up windows in the app's own style, Dark and Midnight themes, app/tray icon drawn in code.
- **Settings rebuilt in Discord's layout:** My Account, Data & Privacy, Notifications,
  Voice & Video, Appearance, Accessibility, System, Language & Time, Registered Games,
  Activity Privacy, Host this group. Toggle switches; centered panel on big windows.
- **Chat:** replies, edits (Up arrow), deletes, reactions with a quick-reaction row, emoji picker,
  profile cards, ignore, notes, mute channel/server, close DM, collapsible categories.
- **Status:** Online / Idle / Do Not Disturb / Invisible, custom status, auto-idle after 10 minutes.
- **Games area:** controller button on the left rail; server browsers, Metal Fatigue and
  community links moved out of the group's channel list.
- **Groups:** "Create Your Group" wizard (templates, community option), passwordless accounts
  (this PC is your key), account chooser like Google's, identity backup/restore.
- **Invites:** short codes, `gamespy://join/` links and clickable https links through GitHub Pages;
  optional permanent address (dynamic DNS); one app window per profile.
- **Hosting:** the group server runs in the background on its own (survives closing the app,
  optional start with Windows), runs and restarts the Metal Fatigue server, writes its own log.
- **Tray:** icon shows voice state (in voice, talking, muted, deafened) and unread mentions;
  menu with mute, deafen, disconnect, status, quit. Close button minimizes to the tray.
- **Fixes:** text scrolling in long lists, host not signing back in on startup, leftover servers
  holding the port, public address lookup (TLS 1.2), file dialogs crashing, settings close button.

## 0.5 (2026-11-17)
- Discord-style layout: group rail, channel list with text and voice channels, member list,
  user panel, unread dots and mention highlights, invite cards, Home with Friends and DMs.
- Voice: two-way voice with speaking rings, mute/deafen, per-user volume, voice settings page.

## 0.4 and earlier (2026-11-15 to 11-17)
- Group server (TLS) with accounts, friends, presence, saved text channels and DMs.
- Voice chat (ADPCM over the group connection, push-to-talk, noise gate).
- Metal Fatigue online play through the relay; hosting merged with the group.
- Server browsers for Quake III Arena, OpenArena and Unreal Tournament (333networks).
- Community directory (BF2, Red Alert 2, Tribes and more).
