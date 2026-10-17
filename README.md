# HostPause for Terraria

**Press F9 as the host to pause the game for everyone. Press F9 again to resume.**

HostPause is a patch for **vanilla Terraria 1.4.5.8**, with a frozen grayscale screen
and a centered pause banner. It does not require tModLoader.

[Русская инструкция](README_RU.md) · [Downloads](https://github.com/Abdeygodin/terraria-hostpause/releases)

![Pause overlay captured in the actual Linux game client](docs/pause.png)

## Status

**Experimental prerelease, version 0.1.1.** Tested with the native Linux Steam
Flatpak game, an actual graphical FNA client, and a dedicated TCP server.
The Windows x64 installer is built, but Windows gameplay and Steam friend
connections have not been tested. Other Terraria versions are unsupported.

## Windows installer fix in 0.1.1

Fixes `Failed to resolve assembly: Microsoft.Xna.Framework.Graphics` during
installation. The patcher now probes the Windows .NET Framework directories and
both legacy and modern GAC layouts, checking assembly identities. No system
assemblies are copied into the game or bundled with this release.

The original error was reproduced with the official Windows 1.4.5.8 server.
Ten regression checks passed on Linux using a simulated Windows GAC and Wine
Mono XNA facades, including successful patching of that server and byte-identical
Linux outputs. This is not a live Windows gameplay test.

If 0.1.0 failed with this error, extract the new archive into a separate directory
and run `Install-Windows.cmd` again. There is no installed patch to remove after
that staging failure. If 0.1.0 installed successfully, uninstall it before updating.
The gameplay runtime and network protocol are unchanged; existing Linux players
do not need to reinstall for this installer fix.

## Install and play

1. Everyone saves and fully closes Terraria and its server.
2. Download `HostPause-0.1.1.zip` from Releases and extract it.
3. Each player installs the patch: run `Install-Windows.cmd` on Windows or
   `bash Install-Linux.sh` on Linux.
4. Start Terraria through Steam as usual. The host starts a Host & Play session;
   the other players join.
5. Wait for the readiness message. The host presses **F9** to pause or resume.

If installation cannot locate the game, pass its directory explicitly:

```bash
bash Install-Linux.sh "/path/to/Terraria"
```

Back up your characters and worlds before trying this prerelease. The installer
backs up the executables in `HostPause.backups` and does not edit save files.

## Behavior

- Only the host authenticated by Terraria's Host & Play token can control the
  shared pause. Joining first or using a local IP does not grant host rights.
- The simulation stops: world time, enemies, projectiles, items, physics, damage,
  and gameplay timers. Networking continues so the host can resume.
- Every connected player needs the patch. An unpatched or still-loading player
  prevents pausing; normal gameplay remains available.
- New joins are refused while paused. Reconnect after the host resumes.
- Leaving as host releases the pause; Host & Play may also close its server.
- F9 also toggles a local pause in singleplayer. This path has not had the same
  live test coverage as multiplayer.
- Starting a pause is blocked while typing in chat, a sign, or a chest name.
  Resuming an existing pause remains available. Chat appears after resuming.

To change the key, edit `HostPause.key.txt` next to `Terraria.exe`, for example to
`F8` or `Pause`, then restart. Compatibility with other patches is untested.

## Uninstall or update

Close the game and server. Run `Uninstall-Windows.cmd` or
`bash Uninstall-Linux.sh` to restore the original executables. Keep the backup
directory and installation manifest until removal is complete.

For another HostPause build, uninstall the previous one before installing it.
Steam updates and file verification may replace the patch. The uninstaller
refuses to overwrite executables that have changed since installation; use
Steam's file verification if the game has already updated.

## Source and build

The repository contains the patch source, its banner, and verification evidence.
Release archives contain the installer and runtime for Linux x64 and Windows x64.
No Terraria executables, game libraries, characters, or worlds are distributed.
The patcher generates modified executables from the user's own game installation.

Requirements: Python 3, .NET SDK 8, and an original Terraria 1.4.5.8 installation.
Runtime compilation needs a .NET Framework `mscorlib.dll` reference: the native
Linux game includes one; the script also supports the Windows framework location.

```bash
python3 source/build.py "/path/to/original/Terraria"
dotnet source/build/HostPause.Patcher.dll prepare "/path/to/original/Terraria" "/new/staging/directory"
```

`prepare` writes patched files to a separate staging directory. To install the
build, use `dotnet source/build/HostPause.Patcher.dll install "/path/to/Terraria"`.
The patcher rejects unsupported assembly versions and already-patched inputs.

## Verification

`verification/integration-result.json` records 19 live Linux server checks:
host permissions, shared pause state, frozen simulation and player state,
a 125-second pause without disconnecting, state heartbeats, resume, mixed
patched/unpatched clients, host exit, and reconnecting after an empty paused
session. `verification/render-test.log` records F9 pause/resume in the actual FNA
client; the screenshot above comes from that run. Installation and removal were
also checked on an isolated copy of the game files.

`verification/resolver-result.json` records the installer dependency regression
checks, and `verification/installer-0.1.1-result.json` records the self-contained
Linux installer and byte-exact rollback check.

The source tests use isolated game copies and a synthetic localhost test host
token. They must not be run against a live player session.

## License and affiliation

HostPause code is under the [MIT License](LICENSE). Mono.Cecil and the bundled
.NET runtime have their accompanying license notices. Terraria belongs to
Re-Logic; this is an unofficial community project.
