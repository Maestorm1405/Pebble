# Pebble: Microsoft Store listing

Copy each section into Partner Center → your app → Submission → **Store listings → English**.

---

## Product name
Pebble Island  (the reserved name; inside the app it still calls itself Pebble)

## Short description (max ~100 characters)
A Dynamic Island for Windows: music, lyrics, timers, notifications and quick search at the top of your screen.

## Description
Pebble brings a Dynamic Island to Windows. It's a small pill at the top of your screen that shows what matters and opens when you hover over it.

**Now playing, with lyrics**
See the song, album art and progress for Spotify, YouTube, Media Player and more. Play, pause, skip and change the volume without switching apps. Turn on synced lyrics and follow along line by line, right in the island.

**Notifications, done right**
Messages from Discord, WhatsApp, email and other apps slide into the island with the app's icon. A Notifications tab keeps your recent ones, so nothing gets lost.

**Built for gaming**
Game mode notices when you're playing, shows your FPS and ping in the pill, and holds notifications until you're done. You get a short summary when you finish.

**Search anything**
Press Alt+Space and a search bar slides out of the island. Open apps, settings and recent files, do quick math, or search the web.

**Everyday tools in one place**
- Pomodoro timer with a live countdown in the pill
- To-do list
- Clipboard history
- File shelf: drag files in, then drag them out later
- Teleprompter that scrolls near your webcam
- Color picker (Ctrl+Alt+C)
- Auto-tidy Downloads: sorts files into folders, with one-click undo

**Thoughtful details**
Live weather, battery and Bluetooth alerts, volume pop-ups, and a welcome tour on first launch. It hides automatically during fullscreen videos. Everything can be switched on or off in Settings.

Your clipboard, to-dos, files and notifications stay on your PC. Pebble has no account, no ads and no tracking.

## Features (Partner Center "Product features", one per line, max 20)
Dynamic Island-style pill at the top of the screen
Now playing controls for Spotify, YouTube and more
Synced lyrics in the island
Notifications in the island with a history tab
Game mode with FPS and ping
Spotlight-style search with Alt+Space
Pomodoro timer
To-do list
Clipboard history
File shelf with drag and drop
Teleprompter
Color picker
Auto-tidy Downloads with undo
Live weather
Battery and Bluetooth alerts

## Search terms (max 7)
dynamic island
notch
music widget
lyrics
launcher
productivity
game mode

## Category
Productivity  (subcategory: none)

## Age rating questionnaire hints
- No violence, sexual content, gambling or user-to-user communication inside the app.
- The app accesses location (for weather) and the internet.
- Expected rating: 3+ / Everyone.

## "Why does your app need runFullTrust?" (Submission options → restricted capabilities)
Pebble is a desktop utility built with WPF/.NET that runs as a full-trust Win32 process. It shows an always-on-top overlay window, reads the system media session and master volume, registers global keyboard shortcuts (Alt+Space for search, Ctrl+Alt+C for the color picker), launches apps the user picks from search, and sorts files in the user's Downloads folder when they turn that feature on. These need the standard desktop (full trust) app model.

## Notification access (userNotificationListener)
Pebble shows incoming notifications inside its island and keeps a short in-memory history. Windows asks the user for permission first, notifications are never stored on disk or sent anywhere, and the feature can be turned off in Settings.
