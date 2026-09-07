# ShinyFileServe

A self-hosted file server for a team. It runs as a desktop app on **macOS (AppKit)**, **Linux
(GTK4)** and **Windows**, keeps to the system tray, and serves a browser UI so nobody has to
install a client. Folders are published to groups with explicit permissions and metered quotas,
and every action is written to an audit log.

The desktop app has **no screens of its own** — it is a window onto its own server, and everything
an operator does happens in the portal. Building the same forms twice would have meant two of
everything to keep in step and one of them quietly falling behind.

```
┌─────────────────────────────────────────────────────────────────────┐
│  Desktop app (.NET MAUI)          tray icon · console window        │
│  ├── Shiny.Net.HttpServer  ───────  HTTP on every interface, :8080  │
│  │     └── embedded zip    ───────  Blazor WebAssembly frontend     │
│  ├── Shiny.DocumentDb      ───────  one SQLite file: users, roles,  │
│  │                                  shares, grants, quotas, log     │
│  ├── QuickTunnel (pinggy)  ───────  optional public https address   │
│  └── Shiny.Notifications   ───────  desktop notification per event  │
└─────────────────────────────────────────────────────────────────────┘
```

## Projects

| Project | TFM | What it is |
| --- | --- | --- |
| `src/ShinyFileServe.Shared` | `net10.0` | DTOs that cross the wire |
| `src/ShinyFileServe.Core` | `net10.0` | Documents and domain services on Shiny.DocumentDb + SQLite |
| `src/ShinyFileServe.Server` | `net10.0` | Shiny.Net.HttpServer endpoints, cookie auth, tunnel, static SPA |
| `src/ShinyFileServe.Web` | `net10.0` | Blazor WebAssembly frontend (Shiny.Blazor.Controls) |
| `src/ShinyFileServe.App` | `net10.0` | The MAUI shell: a WebView, the tray icon, server control |
| `heads/ShinyFileServe.MacOS` | `net10.0-macos` | AppKit head — `dotnet/maui-labs` |
| `heads/ShinyFileServe.Linux` | `net10.0` | GTK4 head — `dotnet/maui-labs` |
| `heads/ShinyFileServe.Windows` | `net10.0-windows10.0.19041.0` | WinUI head |

`ShinyFileServe.App` targets plain `net10.0` on purpose: there is no official `-linux` TFM, so the
GTK4 head is itself a `net10.0` project and the shared UI has to be something it can reference.

## Building and running

```bash
dotnet build ShinyFileServe.slnx          # everything buildable on this machine

dotnet run --project heads/ShinyFileServe.MacOS     # macOS, AppKit
dotnet run --project heads/ShinyFileServe.Linux     # Linux, GTK4
dotnet run --project heads/ShinyFileServe.Windows   # Windows, WinUI
```

Add `-p:SkipFrontendBuild=true` for inner-loop work on the desktop UI — it skips publishing and
zipping the Blazor app, which is the slow part of a build.

Nothing is seeded on first run. The first person to open the site creates the administrator — an
account with a password the whole internet knows is worse than no account at all.

### Linux prerequisites

```bash
sudo apt install libgtk-4-dev libwebkitgtk-6.0-dev gobject-introspection \
                 libgirepository1.0-dev gir1.2-gtk-4.0 gir1.2-webkit-6.0 pkg-config
sudo apt install libayatana-appindicator3-1 libgtk-3-0    # tray icon
```

GNOME 40+ needs the AppIndicator shell extension for the tray icon; KDE works as shipped.

## Signing in

Three ways in, in the order they are decided:

1. **From the machine running the server**, no password. The desktop app opens straight into the
   portal. The trust rests on two facts about the connection, checked every request: it came from
   a loopback address, and it did **not** arrive through a tunnel. pinggy forwards into loopback,
   so the address alone would hand the internet an administrator the moment a tunnel opened. No
   header is involved by design — a header is something a client can claim.
2. **A session cookie**, from a normal sign-in.
3. **Nothing**, which is a 401 and the sign-in page.

The first administrator and the `Administrators` group it belongs to are both **protected**: they
can be renamed and re-passworded, and they cannot be deleted, disabled, or stripped of admin. Every
other account can be enabled, disabled or deleted freely.

## How the frontend gets served

`ShinyFileServe.Web` is published, zipped, and embedded into `ShinyFileServe.Server.dll` as
`ShinyFileServe.Server.wwwroot.zip`. At runtime a `ZipFileSource` reads out of the resource and
`UseBlazorWebAssembly` serves it, precompressed assets included — so a head ships one assembly and
serves the whole SPA. See the targets at the bottom of `ShinyFileServe.Server.csproj`; they hook
`AssignTargetPaths`, not `BeforeCompile`, because resource manifest names are assigned before
`BeforeCompile` runs and an `EmbeddedResource` added there is silently dropped.

## Permissions

Rights are granted to a **group**, never to a person. A user in two groups gets the union of both,
and the most generous quota of the two — adding someone to a group can never take access away.

| Permission | Without it |
| --- | --- |
| List | The share is invisible; the API answers 404, not 403 |
| Download | Files can be seen but not read |
| Upload | No new files, no overwrites |
| Delete | Files stay |
| New Folder | No sub-directories |

A group can be marked **Administrator**, which grants every share regardless of grant, the activity
log, and the settings — from the console window or the browser. `User.IsAdmin` pins the same power
to one account.

## Quotas

Per group, per share, for download and upload separately: a byte cap over a **day, week, month, or
in total**, plus an optional cap on any single upload. Weeks are ISO-8601 so a weekly quota does not
roll over on a different day depending on the machine's culture.

A download charges the whole file the moment it is authorised, even for a range request — charging
per range would let a client fetch a file in pieces for free. An upload that turns out to exceed
the quota is deleted rather than left on disk.

## Sessions

- **Keep me signed in** can be turned off entirely; when on, the window is configurable in days.
- **Idle sign-out** after N minutes with no keyboard or mouse. Enforced in the browser, which is
  the only place that can see a screen go idle; the server's part is that a non-remembered session
  is a browser session and a deactivated account's cookie is rejected on its next request.

## Names and notes

An administrator can give any file or folder inside a share a friendly name and a note. The name on
disk stays visible beside the alias — a renamed file nobody can find by its real name is worse than
one with an awkward name.

## The tray

The tray icon is the app's front door; the window is one view onto it.

- The address people need — the **public tunnel when it is open, otherwise the LAN address**.
- Start/stop the server, open/close the tunnel.
- Open FileServe, and the only Quit in the application.

Closing the window puts the app away rather than shutting the server down, and the app leaves the
Dock/taskbar the first time its window is closed rather than at launch — an app that vanished
before anyone had seen it would be hard to get back.

**The status item has to be created straight after `builder.Build()`, before any window exists.**
Created later — from `App.CreateWindow`, dispatched, or delayed — the `NSStatusItem` is constructed
without error and never appears in the menu bar. Each head calls `StartFileServe()` on the built
`MauiApp` for exactly this reason. On macOS, Quit also goes to `NSApplication.Terminate` rather
than `Application.Quit()`: the head turns off terminate-after-last-window so the server outlives
the window, which means MAUI's Quit would only put the window away.

## Notifications

Desktop notifications for whichever activities you pick — sign-ins, refusals, transfers, deletions.
`Shiny.Notifications` registers per head (`AddNotifications()` only exists on the platform builds);
where no manager is registered the tray icon's own balloon is used instead.

## API

Everything under `/api`. JSON is source-generated end to end — there is no reflection path on
either side, so a type missing from a `JsonSerializerContext` is a build warning rather than a
runtime surprise on a trimmed head.

| Route | Who |
| --- | --- |
| `POST /api/auth/login`, `POST /api/auth/logout`, `GET /api/auth/me` | anyone |
| `GET /api/auth/policy` | anonymous — the sign-in page needs it before anyone signs in |
| `GET /api/folders`, `GET /api/folders/{id}` | signed in |
| `GET /api/files`, `/download`, `POST /upload`, `/directory`, `DELETE /api/files` | signed in, per permission |
| `POST /api/auth/setup` | anonymous, and only until the first account exists |
| `GET /api/activity` (filter by user, action and date window) | administrators |
| `GET /api/admin/status`, `PUT /api/admin/settings`, `/server/{start,stop}`, `/tunnel/{start,stop}` | administrators |
| `GET /api/admin/user-details`, `POST /api/admin/users`, `DELETE /api/admin/users/{id}` | administrators |
| `GET /api/admin/roles`, `POST /api/admin/roles`, `DELETE /api/admin/roles/{id}` | administrators |
| `GET /api/admin/shares`, `/{id}`, `POST`, `DELETE /{id}` | administrators |
| `PUT /api/admin/annotation` | administrators |

## Where data lives

One SQLite file plus a cookie-signing key, under the platform's application-data directory
(`~/Library/Application Support/ShinyFileServe` on macOS, `~/.config/ShinyFileServe` on Linux,
`%AppData%\ShinyFileServe` on Windows). Settings live in that database rather than beside it,
because the browser edits the same values and a second copy on disk would be a second source of
truth.

## Security notes

- The server binds **every interface**. On a LAN that is the point; through the tunnel it is the
  public internet, so give people real passwords before opening one.
- Passwords are PBKDF2-SHA256 at 210,000 iterations.
- Path traversal is refused at the one function every file operation goes through, before the
  filesystem is touched: `.` and `..` segments are rejected outright, and the canonicalised result
  is checked against the share root to catch symlinks.
- A share a user has no grant on answers **404**, not 403 — they should not learn it exists.
- Refusals are logged, not just successes.
- A loopback session is marked as such, and the portal hides Sign out on one — signing out of a
  session that signs itself back in on the next request would be a button that does nothing.
