# discord-name-hider

A Vencord plugin (**HideUsers**) that hides chosen people from your own Discord client:

- Friends list
- DM list (1-on-1 DMs with them disappear)
- Their messages in every channel, DM and group (incl. search, pins, mentions)
- Typing indicators, voice speaking, notifications
- Member lists / reactions (best effort, by avatar)

It only changes what **you** see. They are not blocked and are not notified.

> Client mods are against Discord's Terms of Service. Use at your own risk.

## Easy way: DiscordNameHider.exe

Double-click `DiscordNameHider.exe`, type the people to hide (one per line), click **Hide them**.
Discord closes and reopens with them hidden. Run it again to change the list, or click
**Remove tool** to put Discord back to normal. Nothing else to install.

Build the exe (developer PC only, needs Node + Git):

```powershell
powershell -ExecutionPolicy Bypass -File .\build-exe.ps1
```

Output: `out\DiscordNameHider.exe`.

## Manual install (developers)

Needs Node.js and Git.

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Then in Discord: **Settings → Vencord → Plugins → HideUsers** → enable → enter names.

## Hiding someone

Enter user IDs or usernames separated by commas. **User IDs are best** (names can change):
Settings → Advanced → Developer Mode ON, then right-click the person → **Copy User ID**.

After changing the list, press **Ctrl+R** to reload Discord so everything is re-filtered.

## Update after editing the plugin

Run `install.ps1` again.
