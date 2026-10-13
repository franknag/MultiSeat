# Per-seat Steam (optional, off by default)

This page covers `MultiSeat:SteamIsolation`. It lets each seat run its own Steam at the same time as the console's Steam and the other seats' Steams.

The feature is **off by default**. With it off, MultiSeat launches Steam exactly as it did before. Read the "What is not verified" section before you turn it on.

## The problem

Steam allows one running instance per machine. It enforces this with a machine-wide name that every Steam process uses to find the others.

In practice, starting Steam in a seat closes the Steam that is running in the console session or in another seat.

## What this feature does

It does two things.

1. **Each seat gets its own copy of the Steam client folder.** Two Steams that share one install folder fight over the same config, logs and login files. The second one fails to sign in. A separate folder avoids that.
2. **Each seat's Steam starts with its own IPC name.** MultiSeat adds `-master_ipc_name_override <name>` to the command line. Steam then looks for other instances only under that name.

MultiSeat applies both whenever it starts the system Steam for a seat. It does this in all three places Steam can start:

- `POST /api/seats/{id}/launch` (and the dashboard's launch action).
- `LaunchOnConnect` apps.
- The seat's Apollo app list (`apps.json`), for entries such as `steam.exe -bigpicture`.

A launch counts as "the system Steam" when its executable resolves to `steam.exe` in the system Steam folder. The comparison uses full paths and ignores case. A bare `steam.exe` is looked up on `PATH` first. Any other program is left alone.

The rewrite follows these rules:

- The program becomes the `steam.exe` in the seat's own folder.
- `-master_ipc_name_override <name>` goes in front of the existing arguments, once.
- If the caller already passed `-master_ipc_name_override`, theirs is kept and nothing is added.
- If the working directory was empty, or was the system Steam folder, it becomes the seat folder.
- Running the rewrite on its own output changes nothing more.

If the seat's folder cannot be prepared, the launch is **refused**. MultiSeat never falls back to the system Steam, because that is the exact thing that closes the other Steam.

## What you need

- **One Steam account per seat.** Each seat signs in to its own Steam. Two Steam clients cannot play with one account at the same time.
- **About 1.4 GB of disk per seat.** That is the size of the copied client folder on the reference host. Games are not copied. They live in a Steam library, and the shared `SteamLibrary` folder works for that.
- **A normal Steam install on the host.** MultiSeat copies from it.

Steam's terms of service are between you and Valve. This feature does not change them. Running several accounts on one PC, and sharing a game library between accounts, are both things you should check against Steam's current subscriber agreement and Family Sharing rules before you rely on them. This page makes no legal claim either way.

## Turn it on

Put this in `C:\Program Files\MultiSeat\appsettings.local.json`, then restart the service:

```jsonc
{
  "MultiSeat": {
    "SteamIsolation": {
      "Enabled": true,
      "SourceDir": "",     // empty = the installed Steam
      "SeatRootDir": ""    // empty = SharedGameLibraryDir, C:\MultiSeatGames by default
    }
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Master switch. |
| `SourceDir` | empty | The Steam folder to copy from. Empty means the `InstallPath` in `HKLM\SOFTWARE\WOW6432Node\Valve\Steam`, and if that is missing, `C:\Program Files (x86)\Steam`. |
| `SeatRootDir` | empty | The folder that holds the per-seat copies. Empty means `SharedGameLibraryDir`. Do not put it inside the Steam folder. MultiSeat refuses folders that overlap. |

Each seat's copy lives in `<SeatRootDir>\Steam-<account>`, for example `C:\MultiSeatGames\Steam-Seat01`.

Then sign in once in each seat's Steam, using that seat's own account.

## What gets copied

The copy skips everything that belongs to one particular Steam. These are left out at any depth in the tree, without regard to case:

- Folders: `steamapps`, `logs`, `config`, `userdata`, `dumps`, `appcache`, `depotcache`, `htmlcache`.
- Files: `loginusers.vdf`, and any file whose name starts with `ssfn` (the Steam Guard sentry files).

Everything else is copied. This matches the `robocopy` command that was tested by hand on the reference host.

MultiSeat builds the folder when the seat is provisioned, so the first Steam launch does not wait for the copy. It checks again at every Steam launch. If that check fails at provisioning, the seat still comes up, and Steam launches for it are refused until the folder can be built.

## Updates

Steam updates itself inside each seat's folder. When the system Steam is updated, MultiSeat sees that its small fingerprint of the client changed (size and time of `steam.exe`, the two client DLLs, and the files in `package`). The next launch then refreshes the seat's copy.

A refresh only adds and overwrites client files. It never touches the seat's own `config`, `userdata`, `steamapps`, login files or caches. It never deletes anything.

Two limits to know about:

- A refresh can replace a client file that the seat's Steam had already updated on its own. Steam will simply update that file again.
- A file that the running seat Steam holds open cannot be replaced. MultiSeat logs a warning, keeps using the existing copy, and tries again at the next launch.

## Permissions

The seat's folder is locked to SYSTEM, Administrators and that seat's account (Modify). It does not inherit from its parent.

This is narrower than the shared game library, which gives `BUILTIN\Users` Modify. That is right for ROMs and `SteamLibrary`, which seats are meant to share. It is wrong for a folder that holds one player's Steam login, because every other seat could then read or replace it. The copy follows the same rule MultiSeat already uses for each seat's Apollo folders.

Nothing is followed through a link. A junction or symbolic link inside the Steam folder is skipped, and one inside the seat folder stops the copy of that subtree.

## What the dashboard and API show

When the feature is on, each seat reports `steamIsolation` in its JSON, and its card on the dashboard shows the IPC name and whether the folder exists. When the feature is off, the field is `null` and the card shows nothing.

## Removing a seat's Steam folder

MultiSeat does **not** delete the folder when a seat is torn down. It holds that player's Steam login and any downloads, and a re-provisioned seat reuses it.

To remove one:

1. Tear the seat down, and make sure no Steam process is running for that account.
2. Delete `<SeatRootDir>\Steam-<account>` as an administrator.

To switch the feature off, set `Enabled` to `false` and restart the service. Launches then go back to the system Steam. The folders stay on disk until you delete them.

## What is verified, and what is not

**Verified by hand on the reference host (2026-10-13):**

- `steam.exe -master_ipc_name_override gaming` lets two Steams run side by side in different sessions.
- With both Steams on the same install folder, the seat's Steam failed to sign in.
- With the seat on its own copy of the folder (made with the exclusions above, 1.36 GB), both Steams stayed running and the seat signed in with its own account.

**Verified by the automated tests in this repository:**

- The IPC name rules, the path matching, and the argument rewrite.
- What the copy includes and excludes, that a refresh keeps the seat's own files, and that links are not followed.
- That all three launch paths hand the rewritten launch to the process launcher, and that with the feature off they hand over exactly what they always did.

The automated tests run the real launch code, but they stop short of starting a process in a Windows session.

**Not verified:**

- **Games and Steam's helper processes.** Nobody has yet shown that a game started from a seat's Steam finds that seat's Steam, rather than the console's. Do not assume it works. Test the games you care about.
- **Launching a game directly** (not through Steam) is not handled by this feature.
- **Anti-cheat, Steam Cloud, Steam Input and Big Picture** behaviour with several Steams at once.
- **Provisioning with the feature on.** The step that builds the folder at provisioning is wired in and best-effort, but no automated test drives a full seat provision.

**A known unknown.** During the manual test, the shader helper `bin\gldriverquery.exe` exited in the seat with code 3221225781 (`0xC0000135`, a DLL it needs was not found). The cause is not known, and this feature does not try to fix it.

## Related work

Pull request #110 on this repository (open as a draft at the time of writing) proposes a different approach: a native supervisor and hooks that wrap Steam launches. It is not part of this feature, and the two have not been compared or combined.
