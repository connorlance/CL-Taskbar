<!--
  Screenshot placeholders: each "Screenshot:" line describes one image.
  Edit this file on github.com, delete the placeholder line, and drag the image into that spot.
-->

# CL-Taskbar

A Windows taskbar overlay that shows open apps from all virtual desktops at once, grouped by desktop.

It sits over the app area of the Windows taskbar and draws its own bar. It does not modify Windows, Explorer, or the system taskbar.

Windows 10 and Windows 11. .NET Framework 4.8 (included with Windows).

## Screenshots

> Screenshot: Default configuration. Desktop numbers with each desktop's apps grouped after them.

> Screenshot: Custom group and label colors per desktop, current desktop highlighted.

> Screenshot: Desktop names and pictures used as labels.

> Screenshot: Icon Group, Space, and Menus placed before the desktop groups.

> Screenshot: Popup grid open above its button.

> Screenshot: Light taskbar theme.

## Installation

1. Download `CL-Taskbar-v1.1.0.zip` from [Releases](../../releases/latest).
2. Right-click the zip › Properties › check **Unblock** › OK. Without this, Windows SmartScreen shows a prompt on first launch (**More info › Run anyway**).
3. Extract to a permanent folder and run `CL-Taskbar.exe`.

Settings and Exit are on the right-click menu of an empty spot on the bar, and on the tray icon.

## Features

### Virtual desktops
- Open apps from all virtual desktops, grouped by desktop.
- Desktop labels: number, name, both, picture, or none.
- Per-desktop group color, label color, and label background.
- Highlight on the current desktop.
- Clicking an app switches to it, including apps on other desktops.
- Dragging an app onto another desktop's group moves the window to that desktop.
- Drag to reorder. The order is saved per desktop.
- Apps can be pinned to a specific desktop.
- Window previews on hover. Point at a preview to see that window on the screen.

> Screenshot: Hover preview for an app with multiple windows.

> Screenshot (GIF): Dragging an app from one desktop group to another.

### Bar sections
- **Icon Group:** pinned apps shown as taskbar buttons, or as a popup grid behind a `^` button.
- **Menu:** a button that opens a list of apps, files, folders, Windows tools, and web links. Supports sub-menus and separators.
- **Space:** a gap, with an optional divider line.

Items can be:
- apps
- files
- folders
- commands (anything accepted by Win + R)
- web links
- entries from a list of about 120 Windows tools and settings pages

> Screenshot: A Menu open with a sub-menu expanded.

> Screenshot: The "Pick from a list" window.

### Web links
Each link has its own settings, or follows the settings of its Menu or group:
- which browser opens it
- where it opens: a new tab on this desktop, a new window, and so on
- a regular or private window

> Screenshot: Web link settings.

### Right-click menus
- Every option can be turned on or off and reordered, including separators.
- Optional items:
  - Move to desktop
  - End task
  - Copy path
  - Minimize / Maximize
  - Show desktop
  - Recent files (jump list)
- Shift + right-click opens the File Explorer context menu for the app.
- An optional mode uses the File Explorer menu for all app right-clicks.
- Long window titles are shortened to a configurable maximum menu width.

> Screenshot: Right-click menu on an app with multiple windows.

> Screenshot: Right-click menu settings.

### Notification badges
- Unread counts for apps that report a badge to Windows, such as Teams and Mail.
- Unread email count for classic Outlook.
- Optional count of notifications waiting in the notification center.

> Screenshot: Unread badge on an app icon.

### Customization
- Custom name, icon, and background for any app or item. Icons can be a file, an `.ico`/`.exe`/`.dll`, or an image URL.
- Colors, icon size, button width, section spacing, and indicator line colors.
- Per-item Run as administrator, arguments, and start-in folder.
- Settings window with dark, light, or system theme.

> Screenshot: Settings window.

## Files and system access

CL-Taskbar writes only to its own folder:

| Path | Contents |
|---|---|
| `CL-Taskbar.settings.json` | Settings |
| `Pins\` | Copies of pinned shortcuts |
| `Cache\` | Downloaded website icons and images |
| `CL-Taskbar-errors.log` | Error log, created only if an error occurs |

Actions outside its folder:

- **Start when I sign in** (off by default): creates a shortcut in the Startup folder.
- **Dragging an app to another desktop:** moves that window.

Other system access:

- **Notification badges:** Windows' notification database is copied into `Cache\`, read, and the copy is deleted. The original is not opened.
- **Classic Outlook:** the unread count is read from Outlook's automation interface.
- **Network:** the only access is downloading website icons, from the site or from DuckDuckGo's icon service.

## Limitations

- Main taskbar only; taskbars on other monitors are not supported.
- Bottom taskbar only; taskbars on the left or right side are not supported.
- Progress bars and overlay icons sent to the system taskbar are not shown.
- App-defined jump list sections (for example Chrome's "Most visited") are not available.
- Moving windows between desktops uses an undocumented Windows interface and may stop working after a Windows update. Other features are unaffected.

## Building

Requires the [.NET SDK](https://dotnet.microsoft.com/download) on Windows.

```
dotnet build -c Release
```

Output: `bin\Release\net48\CL-Taskbar.exe`. No third-party dependencies.

## Implementation notes

C# / Windows Forms, targeting .NET Framework 4.8.

| Area | Windows API used |
|---|---|
| Windows | Win32 window enumeration |
| Virtual desktops | `IVirtualDesktopManager` |
| Moving windows between desktops | `IVirtualDesktopManagerInternal` (undocumented) |
| Taskbar position | UI Automation |
| Previews | DWM thumbnails |
| Images | WIC |
| Badges | `winsqlite3.dll` |
| Jump lists | `IApplicationDocumentLists` |
| Shift + right-click | Shell context menus (`IContextMenu`) |
