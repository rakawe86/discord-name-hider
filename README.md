<div align="center">

# Discord Name Hider

**Hide anyone from your Discord: friends list, DMs, messages, everywhere. One click to hide, one click to show.**

[![Download](https://img.shields.io/github/v/release/rakawe86/discord-name-hider?label=Download&style=for-the-badge&color=5865F2)](https://github.com/rakawe86/discord-name-hider/releases/latest)
![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?style=for-the-badge&logo=windows)
![License](https://img.shields.io/github/license/rakawe86/discord-name-hider?style=for-the-badge)

</div>

---

## Features

- **Hide people completely** from your friends list, DM list, chat messages, member lists, typing indicators and notifications
- **Instant on/off**: tick or untick a person and Discord updates immediately, no restart
- **Hide everyone toggle**: one switch hides or shows everyone on your list at once
- **Nothing slips through**: also hides replies to them, @mentions of them, search results, pins, reactions, voice channels and Active Now
- **Nothing is lost**: messages they send while hidden appear in the right place when you show them again
- **Shortcut in Discord**: press `Ctrl + Shift + H` to toggle hiding
- **Private**: only changes what *you* see. Hidden people are not blocked and not notified
- **Single .exe**: nothing else to install

## Download & use

1. Download **[DiscordNameHider.exe](https://github.com/rakawe86/discord-name-hider/releases/latest)**
2. Double-click it and press **Install into Discord** (one-time; Discord restarts once)
3. Add people by username or User ID. Ticked = hidden

> **Tip:** User IDs are the most reliable (names can change).
> In Discord: *Settings → Advanced → Developer Mode*, then right-click a person → **Copy User ID**.

> **Windows warning?** The app is not code-signed, so SmartScreen may show
> "Windows protected your PC". Click **More info → Run anyway**.

## Uninstall

Open the app and click **Uninstall from Discord**. Discord goes back to normal; your list is kept.

## How it works

The app installs a bundled build of [Vencord](https://github.com/Vendicated/Vencord) with a small
plugin (`plugin/hideUsers`). Your list is saved in `%APPDATA%\DiscordNameHider\state.json`, and the
plugin re-reads it every second, so changes apply live. It also keeps working after Discord updates.

## Build from source

Requires Node.js 18+ and Git.

```powershell
powershell -ExecutionPolicy Bypass -File .\build-exe.ps1
```

Output: `out\DiscordNameHider.exe`

## Disclaimer

Client modifications are against Discord's Terms of Service. Use at your own risk.
This project is not affiliated with Discord or Vencord.

## License

[GPL-3.0](LICENSE). Includes [Vencord](https://github.com/Vendicated/Vencord) (GPL-3.0).
