# Console Overlay for PC

A lightweight, controller-friendly overlay for Windows, in the spirit of the Xbox Game Bar but built around a gamepad first.

Status: v1. Sound, Discord voice controls, a window switcher and the time, all reachable from a controller.

## Roadmap

1. **Overlay skeleton**: tray app, overlay on top of games, open with a hotkey or a controller button, full controller navigation.
2. **Time/date and sound**: clock header, master and per-app volume, output and input device switching.
3. **All Tabs and Discord**: switch between open windows with live previews, Discord voice controls.
4. **Settings and release**: in-app button binding, themes, autostart, downloadable builds.

## Download

Get the latest version from the [Releases page](https://github.com/NumerusXXL/consoleoverlayforpc/releases/latest):

1. Download `ConsoleOverlay-<version>-win-x64.zip`, unzip it anywhere (for example `Documents\ConsoleOverlay`) and run `ConsoleOverlay.exe`. Nothing else to install.
2. Windows may say the app is from an unknown publisher, because it isn't code-signed. Click **More info**, then **Run anyway**.
3. It waits in the system tray. Press **View + Menu** on your controller or **Ctrl+Alt+G** to open it.
4. To have it start with Windows, open **Settings › Startup** in the overlay and turn on **Start with Windows**.

The `needs-dotnet` zip is the same app, much smaller, for PCs that already have the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

To update, quit it from the tray, replace the files, and start it again. Your settings are kept.

**Build it yourself:** install the .NET 8 SDK, then run `dotnet run --project src/ConsoleOverlay`. Every push also builds a test version on GitHub Actions (`ConsoleOverlay-win-x64` under the run), which needs the .NET 8 Desktop Runtime.

**Publishing a release** (for the maintainer): push a tag, for example `git tag v1.0.0 && git push origin v1.0.0`. The Release workflow builds both zips and publishes them on the Releases page.

The app lives in the system tray. Right-click the tray icon to open the overlay or its settings, open the log or quit.

If something goes wrong, the log is at `%APPDATA%\ConsoleOverlay\log.txt`. The log from the previous run is kept next to it as `log.previous.txt`.

## Controls

| | Controller | Keyboard |
|---|---|---|
| Open / close | View + Menu (hold both) | Ctrl+Alt+G |
| Move | D-pad or left stick | Arrow keys |
| Select | A | Enter or Space |
| Back / close | B | Esc or Backspace |
| Switch tab | LB / RB | Q / E |
| X and Y buttons | X / Y | X / Y |

Everything works from the keyboard alone. As soon as you press a key, the hints at the bottom switch to keyboard keys.

On the **Sound** page:

| | Controller | Keyboard |
|---|---|---|
| Change volume | Left / right | Left / right (or mouse wheel) |
| Mute | Y (or A on a volume) | M |
| Use a device | A on it | Enter |
| Jump between devices and mixer | LT / RT (or stick clicks) | Tab |

On the **All Tabs** page (opens first):

| | Controller | Keyboard |
|---|---|---|
| Switch to a window | A | Enter |
| Close a window | X (press twice for the game you came from) | X or Delete |

Each tile shows a live preview of the window. If an app asks something when it closes (like "save changes?"), the overlay brings it to the front so you can answer.

On the **Discord** page (laid out like Discord on Xbox: you and a menu on the left, the picked view on the right):

- **In this call**: everyone in your voice channel, with a ring around whoever is talking. A on someone opens their options: volume (0–200%), mute for me, reset volume.
- **Join / Switch channel**: the voice channels of the server you're in and who is in each. A joins. **Other servers** lists all your servers.
- **Voice & audio**: microphone and speaker volume, voice activity or push to talk, noise suppression, echo cancellation, automatic gain, microphone and output device.

| | Controller | Keyboard |
|---|---|---|
| Mute your mic | X (anywhere on the page) | X |
| Select / toggle | A | Enter |
| Mute the focused person (for you) or your mic | Y | M |
| Change the focused volume | Left / right | Left / right (or mouse wheel) |
| Go back a step | B | Esc or Backspace |
| Jump between the menu and the view | LT / RT | Tab |

## Settings

The **Settings** page in the overlay has a list of sections on the left. Moving through the list shows each section on the right; A (or →) goes into it, B (or ←) comes back. On a choice, ← → changes it. Every change is saved right away.

- **Controls**: record a new controller combo (pick it, then hold the new buttons together for a second) or a new keyboard shortcut (pick it, then press the keys). Esc cancels.
- **Appearance**: accent color, the background behind the overlay (blurred game, sharp game, or dark), 12 or 24-hour clock.
- **Startup**: start with Windows, and whether to show a notification when it starts.
- **Games**: what to do with exclusive fullscreen games, and which games to skip. Open the overlay over a game and it offers to skip that one.
- **Discord**: enter your Discord app's Client ID and Client Secret (type them, or X to paste from the clipboard), open the setup guide, sign out.
- **About**: version, check for updates, open the log, quit.

Everything is stored in `%APPDATA%\ConsoleOverlay\settings.json`. You can still edit that file by hand (**Edit settings file** in the tray menu, then **Reload settings**):

```json
{
  "Hotkey": "Ctrl+Alt+G",
  "ControllerCombo": [ "Back", "Start" ],
  "ExclusiveFullscreenFallback": true,
  "ExclusiveFullscreenExcludedGames": [ "oldgame.exe" ],
  "DiscordClientId": "",
  "DiscordClientSecret": "",
  "AccentColor": "White",
  "Background": "Blur",
  "ClockFormat": "System",
  "StartupNotification": true
}
```

Button names: `A`, `B`, `X`, `Y`, `DPadUp`, `DPadDown`, `DPadLeft`, `DPadRight`, `Start`, `Back`, `LeftStick`, `RightStick`, `LeftShoulder`, `RightShoulder`, `LeftTrigger`, `RightTrigger`, `Guide`.

`Guide` is the Xbox button. Windows usually gives it to the Xbox Game Bar (and Steam may take it too), so turn those off for the Guide button if you want to use it here.

## Discord voice controls

The Discord page shows the voice channel you're in, with a ring around whoever is talking, and lets you mute, deafen, leave the call and set how loud you hear each person. It talks to the Discord desktop app on your PC.

Discord only allows this for apps you registered yourself, so there is a one-time setup of a couple of minutes. It's free and needs no coding.

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and sign in with your normal Discord account.
2. Click **New Application**, name it something like `Console Overlay`, accept the terms and click **Create**.
3. In the menu on the left, open **OAuth2**.
4. Copy the **Client ID**.
5. Under **Client Secret**, click **Reset Secret** and copy the secret. Discord only shows it once, so paste it somewhere right away.
6. Still on the OAuth2 page, under **Redirects**, click **Add Redirect**, type `http://localhost` and click **Save Changes**. Keep this as the **only** redirect: with none or more than one, Discord refuses with "Missing redirect_uri".
7. Open the overlay and go to **Settings › Discord**. Pick **Client ID**, paste it (X on the controller, or Ctrl+V) and save, then do the same for **Client Secret**.
8. Open the overlay, go to the **Discord** page and pick **Connect to Discord**. The overlay closes and Discord shows a prompt asking to allow Console Overlay. Click **Authorize**.

That's it. Next time the page connects by itself. The sign-in is saved in `%APPDATA%\ConsoleOverlay\discord-signin.dat`, encrypted so only your Windows account can read it; delete that file to sign out. The Client Secret stays in your settings file and is only ever sent to Discord.

If it doesn't connect:

- **"Discord isn't running"**: use the Discord desktop app. The browser version can't be controlled.
- **Nothing happens after Connect**: Discord might be minimized to the tray. Open it; the Authorize prompt is waiting there.
- **Errors about the secret or the redirect**: check step 5 and 6, then enter the values again in Settings › Discord.
- **Someone else wants to use your app**: Discord only lets the app's owner use these controls (and up to 50 people added under **App Testers** in the portal). The simplest is for each person to create their own app.

## Fullscreen games

- **Borderless / windowed fullscreen:** the overlay draws on top of the game.
- **Exclusive fullscreen:** a normal window can't draw on top without hooking into the game, which anti-cheat can flag. Instead the overlay minimizes the game, opens as its own screen, and brings the game back when you close it. Some older games are slow or unstable when minimized; skip them in **Settings › Games**.

## Controllers

Step 1 reads Xbox-compatible (XInput) controllers. PlayStation and other controllers work when Steam Input or a tool like DS4Windows presents them as an Xbox controller.
