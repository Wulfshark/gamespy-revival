# Changelog

Every build gets a version number. Test builds end in `-dev` (for example `0.6.1-dev`);
releases drop it (`0.6.1`). Newest first. The version is shown at the bottom of Settings.

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
