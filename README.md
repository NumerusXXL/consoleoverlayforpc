# Console Overlay for PC

A lightweight, controller-friendly overlay for Windows, in the spirit of the Xbox Game Bar but built around a gamepad first.

Status: early development. See the roadmap below.

## Roadmap

1. **Overlay skeleton**: tray app, overlay on top of games, open with a hotkey or a controller button, full controller navigation.
2. **Time/date and sound**: clock header, master and per-app volume, output and input device switching.
3. **All Tabs and Discord**: switch between open windows with live previews, Discord voice controls.
4. **Settings and release**: in-app button binding, themes, autostart, downloadable builds.

## Running it

You need Windows 10 or 11 and the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

- **Download:** every push builds the app on GitHub Actions. Open the latest run under the Actions tab and download `ConsoleOverlay-win-x64`.
- **Build it yourself:** install the .NET 8 SDK, then run `dotnet run --project src/ConsoleOverlay`.

The app lives in the system tray. Right-click the tray icon to open the overlay, edit settings, open the log or quit.

If something goes wrong, the log is at `%APPDATA%\ConsoleOverlay\log.txt`. The log from the previous run is kept next to it as `log.previous.txt`.

## Controls

| | Controller | Keyboard |
|---|---|---|
| Open / close | View + Menu (hold both) | Ctrl+Alt+G |
| Move | D-pad or left stick | Arrow keys |
| Select | A | Enter |
| Close | B | Esc |
| Switch tab | LB / RB | Q / E |

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

Both the shortcut and the controller combo can be changed in the settings file.

## Settings

Settings are stored in `%APPDATA%\ConsoleOverlay\settings.json`. Use **Edit settings** in the tray menu, save the file, then **Reload settings**.

```json
{
  "Hotkey": "Ctrl+Alt+G",
  "ControllerCombo": [ "Back", "Start" ],
  "ExclusiveFullscreenFallback": true,
  "ExclusiveFullscreenExcludedGames": [ "oldgame.exe" ],
  "DiscordClientId": "",
  "DiscordClientSecret": ""
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
7. In Console Overlay, right-click the tray icon, choose **Edit settings**, and paste both values:
   ```json
   "DiscordClientId": "123456789012345678",
   "DiscordClientSecret": "the secret you copied"
   ```
   Save the file, then choose **Reload settings** in the tray menu.
8. Open the overlay, go to the **Discord** page and pick **Connect to Discord**. The overlay closes and Discord shows a prompt asking to allow Console Overlay. Click **Authorize**.

That's it. Next time the page connects by itself. The sign-in is saved in `%APPDATA%\ConsoleOverlay\discord-signin.dat`, encrypted so only your Windows account can read it; delete that file to sign out. The Client Secret stays in your settings file and is only ever sent to Discord.

If it doesn't connect:

- **"Discord isn't running"**: use the Discord desktop app. The browser version can't be controlled.
- **Nothing happens after Connect**: Discord might be minimized to the tray. Open it; the Authorize prompt is waiting there.
- **Errors about the secret or the redirect**: check step 5 and 6, then reload settings.
- **Someone else wants to use your app**: Discord only lets the app's owner use these controls (and up to 50 people added under **App Testers** in the portal). The simplest is for each person to create their own app.

## Fullscreen games

- **Borderless / windowed fullscreen:** the overlay draws on top of the game.
- **Exclusive fullscreen:** a normal window can't draw on top without hooking into the game, which anti-cheat can flag. Instead the overlay minimizes the game, opens as its own screen, and brings the game back when you close it. Some older games are slow or unstable when minimized; add them to `ExclusiveFullscreenExcludedGames` to skip this.

## Controllers

Step 1 reads Xbox-compatible (XInput) controllers. PlayStation and other controllers work when Steam Input or a tool like DS4Windows presents them as an Xbox controller.
