# Console Overlay for PC

A lightweight, controller-friendly overlay for Windows, in the spirit of the Xbox Game Bar but built around a gamepad first.

Status: early development. See the roadmap below.

## Roadmap

1. **Overlay skeleton**: tray app, overlay on top of games, open with a hotkey or a controller button, full controller navigation.
2. **Time/date and sound**: clock header, master and per-app volume, output device switching.
3. **All Tabs and Discord**: switch between open windows, Discord voice controls.
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

Both the shortcut and the controller combo can be changed in the settings file.

## Settings

Settings are stored in `%APPDATA%\ConsoleOverlay\settings.json`. Use **Edit settings** in the tray menu, save the file, then **Reload settings**.

```json
{
  "Hotkey": "Ctrl+Alt+G",
  "ControllerCombo": [ "Back", "Start" ],
  "ExclusiveFullscreenFallback": true,
  "ExclusiveFullscreenExcludedGames": [ "oldgame.exe" ]
}
```

Button names: `A`, `B`, `X`, `Y`, `DPadUp`, `DPadDown`, `DPadLeft`, `DPadRight`, `Start`, `Back`, `LeftStick`, `RightStick`, `LeftShoulder`, `RightShoulder`, `LeftTrigger`, `RightTrigger`, `Guide`.

`Guide` is the Xbox button. Windows usually gives it to the Xbox Game Bar (and Steam may take it too), so turn those off for the Guide button if you want to use it here.

## Fullscreen games

- **Borderless / windowed fullscreen:** the overlay draws on top of the game.
- **Exclusive fullscreen:** a normal window can't draw on top without hooking into the game, which anti-cheat can flag. Instead the overlay minimizes the game, opens as its own screen, and brings the game back when you close it. Some older games are slow or unstable when minimized; add them to `ExclusiveFullscreenExcludedGames` to skip this.

## Controllers

Step 1 reads Xbox-compatible (XInput) controllers. PlayStation and other controllers work when Steam Input or a tool like DS4Windows presents them as an Xbox controller.
