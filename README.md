# Console Overlay for PC

A lightweight, controller-friendly overlay for Windows, in the spirit of the Xbox Game Bar but built around a gamepad first.

Status: v1.1 in progress. Window switcher, Steam library, store and friends, shortcuts, media, sound, Discord voice controls, capture and performance stats, all reachable from a controller.

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

On the **Steam** page, LT / RT (or Tab) switches between four views: **Recent**, **Installed**, **Store** and **Friends**.

**Recent** and **Installed** show your installed Steam games as covers, read from Steam's own files on your PC (no sign-in or API key needed). Recent shows what you played last, Installed shows everything A–Z.

| | Controller | Keyboard |
|---|---|---|
| Play the game (or go back to it, if it's the one running) | A | Enter |
| Open the game's page (in the overlay) | X | X |
| Go to Friends | Y | Y |

**Store** is drawn by the overlay like a console store: Specials, Top sellers, New releases, Coming soon and your **Wishlist** (when your profile's game details are public) as big tiles with prices and discounts, and games you own marked **In library** / **Installed**. ↑ / ↓ changes shelf, Y searches (on-screen keyboard with English and Russian letters, or just type).

A opens the game's page: its screenshot fills the background, with the title, price, controller support, genres, multiplayer modes, description, Metacritic score and review count, and a row of screenshots (moving through them changes the background).

| On a game's page | Controller | Keyboard |
|---|---|---|
| Buy it (or Install / Play, if it's yours) | A | Enter |
| Reviews, DLC and wishlist in Steam (achievements and news, if it's yours) | X | X |
| Back | B | Esc |

**Friends** shows your Steam friends as big picture tiles: in a game first, then online, then offline. A opens a friend's page: their picture, status and game, Steam level, since when you're friends, and the games they played in the last two weeks (the game they're in fills the background). On a game there, A plays it if you have it installed, or opens its page.

| On a friend | Controller | Keyboard |
|---|---|---|
| Their page | A | Enter |
| Chat with them and invite them to your game, in Steam | X | X |
| Join their game (when Steam allows joining it), otherwise their profile in Steam | Y | Y |

The friends list comes from the Steam app on your PC. To see who is online, what they play, their level and your game list, add your own **Steam Web API key** in Settings › Steam (get one free at [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey); any domain name works, like `localhost`). It stays on your PC and is only sent to Steam.

**What has to happen in Steam itself.** Valve offers no public way for another app to buy games, send chat messages or send game invites. The only ways would be unofficial clients that log in as you, which can get an account restricted, so Console Overlay doesn't do that. Buying, chat and invites open in the Steam app (in **Big Picture**, Steam's controller mode, by default; Settings › Steam turns that off), on the right game or friend. Everything else on the Store and Friends views is drawn by the overlay from Steam's public store data and official Web API, and never logs in to your account.

On the **Shortcuts** page, your own apps, games and links sit as tiles. A launches one (the overlay closes first), X removes it (press twice). The **Add** tile adds an app or game from the Start menu (Steam, Epic and other launchers put their games there; type to search, or LT / RT to jump between letters), a link like `youtube.com` or `steam://`, or any file you browse to.

On the **Sound** page:

| | Controller | Keyboard |
|---|---|---|
| Change volume (hold to go faster) | Left / right | Left / right (or mouse wheel) |
| Mute | Y (or A on a volume) | M |
| Use a device | A on it | Enter |
| Jump between devices and mixer | LT / RT (or stick clicks) | Tab |

On the **All Tabs** page (opens first):

| | Controller | Keyboard |
|---|---|---|
| Switch to a window | A | Enter |
| Close a window | X (press twice for the game you came from) | X or Delete |

Each window is a square tile with its icon on its own color; the focused one grows, and a blurred picture of that window fills the background behind the page. Its name sits big underneath, with how long the game you came from has been running. If an app asks something when it closes (like "save changes?"), the overlay brings it to the front so you can answer.

On the **Media** page, whatever is playing (Spotify, a browser, any app that shows up in Windows' media controls) appears with its cover, a progress bar and previous / play-pause / next. LT / RT jump 10 seconds back or forward. Other apps that are playing sit below; A on one shows it instead.

On the **Capture** page:

- **Screenshot**: the overlay closes and Console Overlay saves a PNG of the game to `Videos\Captures`. A note in the corner confirms it.
- **Save last clip** and **Record**: these ask the recorder that's already on your PC, which keeps recent gameplay far more cheaply than this app could. With an NVIDIA GPU that's the **NVIDIA app** (turn on its in-game overlay, and Instant Replay for clips). On other GPUs it's **Xbox Game Bar** (turn on "Record what happened" in Windows Settings › Gaming › Captures). Pick one in Settings › Capture.
- **Recent**: your newest screenshots and clips (from `Videos\Captures`, the NVIDIA app's folders in Videos, and `Pictures\Screenshots`). A opens one.

On the **Performance** page: the game's frame rate, 1% low and frame time with a graph of the last minute, and CPU, GPU, memory and video memory. GPU temperature, clock and video memory come from the NVIDIA driver; on other GPUs they say "Not supported". Y turns on **stats in the corner**, a small box with the frame rate, CPU and GPU that stays on screen while you play (pick the corner in Settings › Performance; exclusive fullscreen games draw over it).

Counting frames needs permission from Windows: either run Console Overlay as administrator (X on the Performance page restarts it that way), or use **Allow counting frames for good** in Settings › Performance, which adds your account to Windows' Performance Log Users group once (then sign out and back in).

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
- **Appearance**: size (Big, the default, fits the overlay to the screen like a console on a TV; Standard is for a desk; Huge is bigger still), accent color, the background behind the overlay (blurred game, sharp game, or dark), 12 or 24-hour clock.
- **Startup**: start with Windows, and whether to show a notification when it starts.
- **Games**: what to do with exclusive fullscreen games, and which games to skip. Open the overlay over a game and it offers to skip that one.
- **Capture**: who makes clips and recordings (NVIDIA app or Xbox Game Bar), and where screenshots go.
- **Performance**: stats in the corner on or off and which corner, and permission to count frames.
- **Steam**: your Steam Web API key (for who is online on Friends; type it, or X to paste), a link to get one, and whether buying and chat open in Big Picture.
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
  "SteamWebApiKey": "",
  "SteamBigPicture": true,
  "AccentColor": "White",
  "Background": "Blur",
  "ClockFormat": "System",
  "StartupNotification": true,
  "CornerStats": false,
  "CornerStatsPosition": "TopLeft",
  "Recorder": "NVIDIA"
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
