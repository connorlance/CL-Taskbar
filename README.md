# CL-Taskbar

**See the apps on every virtual desktop at once, right on your taskbar.**

CL-Taskbar sits over the app area of the Windows taskbar and shows every open app from every virtual desktop, grouped and labeled by desktop. Click an app to jump straight to it, even on another desktop. Drag it onto another desktop's group to move it there. Add your own groups of pinned apps, menus of links and tools, and per-desktop pins, and make all of it look the way you want.

It never changes Windows. It reads what it needs and draws its own bar on top. Exit it and your normal taskbar is right there.

Works on **Windows 10 and Windows 11**.

<!-- Screenshots: add yours here, for example
![CL-Taskbar with three desktops](docs/bar.png)
-->

## Download and run

1. Download the latest `CL-Taskbar.zip` from **Releases**.
2. **Before unzipping**, right-click the zip › Properties › tick **Unblock** › OK. This skips Windows' "unrecognized app" warning. Otherwise click **More info › Run anyway** the first time.
3. Unzip it somewhere permanent, like `Documents\CL-Taskbar`, and run `CL-Taskbar.exe`. There's nothing to install.
4. Right-click an empty spot on the bar for **CL-Taskbar settings** or **Exit**.

## Features

**Your desktops, all at once**
- Open apps from every virtual desktop, grouped by desktop, with labels, gaps and divider lines.
- Desktop labels can be the number, the name, both, a picture, or nothing. Each desktop can have its own colors.
- The desktop you're on is highlighted, and the highlight updates the moment you switch.
- Click an app to switch to it, even on another desktop. Click it again to minimize it.
- Drag an app onto another desktop's group to move its window there.
- Drag apps to reorder them. The order is remembered per desktop.
- Live window previews on hover.

**Pinned apps, your way**
- **Icon Groups:** your own rows of pinned apps, or a popup grid behind a `^` button, like the notification area.
- **Menus:** one button that opens a list of apps, files, folders, Windows tools and web links, with sub-menus.
- **Spaces:** gaps and divider lines wherever you want them.
- **Per-desktop pins:** apps that belong to one desktop and open there.
- A picker with about 120 Windows tools and settings pages, like Device Manager, classic Control Panel pages and Sound, plus anything you'd type in Win+R.
- **Web links:** each one can use its own browser, open in a new tab or window, and open in a regular or private (incognito) window.
- Custom names, icons (files, `.ico`, `.exe`, or web images), colors and sizes for anything.

**Right-click menus**
- Every option can be turned on or off and dragged into your own order, including divider lines.
- Extras include Move to desktop, End task, Copy path, Run as administrator, recent files (jump list) and Show desktop.
- **Shift+right-click** gives File Explorer's full menu. There's also an optional mode that uses only Windows' own menus.
- Long window titles are shortened to keep menus narrow.

**Notification badges**
- Unread counts on apps that report one to Windows, like Teams and Mail.
- **Classic Outlook:** your real unread email count.

**Settings**
- Everything lives in one window with a dark theme (light, or match Windows, if you prefer).
- Drag and drop to rearrange, with a color palette and a Tips page.
- Settings are saved automatically to a file next to the program.

## What it doesn't change

- It never modifies Windows, Explorer or the real taskbar. There's no code injection and no registry editing.
- It only writes files in its own folder: settings (`CL-Taskbar.settings.json`), copies of pinned shortcuts (`Pins\`), and website icons (`Cache\`).
- Two things happen only when you ask for them:
  - **Start when I sign in** adds a shortcut to your Startup folder.
  - **Dragging an app to another desktop** moves that window.
- Unread badges come from a private copy of Windows' notification list. The copy is made in `Cache\` and deleted right away.
- Classic Outlook's count is read from Outlook itself.
- Nothing is sent anywhere. The only internet access is downloading website icons, from the site itself or from DuckDuckGo's icon service.

## Not supported (yet)

- Taskbars on the left or right side of the screen.
- Taskbars on other monitors.
- Progress bars and overlay icons that apps send only to the real taskbar.
- The parts of jump lists that apps make themselves, such as Chrome's "Most visited".

## Building from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) on Windows:

```
dotnet build -c Release
```

The program ends up in `bin\Release\net48\`. It targets .NET Framework 4.8, which is built into Windows 10 and 11, so the finished program needs nothing else installed. It uses no third-party libraries.

## How it works (for the curious)

- It's written in C# with Windows Forms.
- **Windows it shows:** it finds windows with the standard Win32 functions. Virtual desktops come from Windows' documented virtual desktop interface.
- **Moving windows between desktops:** this uses Windows' internal virtual desktop interface, the same one tools like PowerToys use. It only asks for the versions it knows. If a Windows update changes that interface, dragging to another desktop just shows a message.
- **Where the bar goes:** it finds the taskbar's icon area with UI Automation, and uses the Windows 10 taskbar window where needed.
- **Everything else** uses Windows' own built-in pieces:
  - the SQLite library built into Windows (`winsqlite3.dll`) for badges
  - Windows Imaging Component (WIC) for images
  - `IApplicationDocumentLists` for jump lists
  - Explorer's own context menus for Shift+right-click
