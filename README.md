# GameSpy Revival

**Play the classic multiplayer games that need a host, with your friends, from one app.**
Your group lives on your PC: nobody can ban it or shut it down.

- **Game library** (Steam-style): every supported game in one place, ready to play, host or find a server.
- **Groups like Discord servers**: text and voice channels, DMs, reactions, replies, invite links. Any player hosts their group on their own PC.
- **Live server browsers** for Quake III Arena, OpenArena and Unreal Tournament (333networks).
- **Metal Fatigue online play** through a built-in relay server.
- Links to the communities keeping Battlefield 2, Red Alert 2, Tribes and more alive.

Windows only for now. Not affiliated with GameSpy, Discord, or any game publisher. You need your own copy of each commercial game.

## Download

Get `GameSpyRevival-x.y.zip` from [Releases](../../releases), unzip it, run `GameSpyRevival.exe`. See [HOW_TO_PLAY.txt](HOW_TO_PLAY.txt).

## Build from source

| Folder | What it is | Build |
|---|---|---|
| `app/` | The Windows app (C#, WinForms, .NET Framework 4) | `app\Build.cmd` (uses the C# compiler built into Windows, no downloads) |
| `lobby-server/` | Group server: accounts, chat, voice relay over TLS (Go) | `GOOS=windows go build -ldflags "-H=windowsgui"` → `gamespy-lobby.exe` |
| `metal-fatigue/shim/` | `DINPUT.dll`, the in-game network shim (C) | see `build.sh` (clang + lld) |
| `metal-fatigue/relay/` | `mfnet-relay.exe`, the Metal Fatigue game server (Go) | `GOOS=windows go build` |

The built helper programs sit in `app/` so `Build.cmd` can embed them.

## Invite links

`docs/` is a GitHub Pages site. With Pages on (Settings → Pages → branch `main`, folder `/docs`), invites can be shared as clickable links:
`https://Wulfshark.github.io/gamespy-revival/join.html#INVITE-CODE`

## License

GNU General Public License v3.0. See [LICENSE](LICENSE).
