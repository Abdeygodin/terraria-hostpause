# HostPause 0.1.0 — experimental prerelease

Pause vanilla Terraria 1.4.5.8 for all players with **F9** as the host.
Press F9 again to resume. The game freezes with a grayscale screen and a compact,
centered pause banner.

Download **HostPause-0.1.0.zip**, extract it, and install on every player's
computer using `Install-Windows.cmd` or `bash Install-Linux.sh`.
Fully close Terraria and its server before installation or removal.

Only the authenticated Host & Play host can toggle the shared pause. The game
simulation stops while networking remains available. Sessions with an unpatched
player cannot pause, and new players must wait until the host resumes.

The installer keeps the original executables for rollback. Save files are not
modified. To replace an earlier HostPause build, uninstall it first, then install
this build. Back up characters and worlds before trying the prerelease.

**Compatibility:** Terraria 1.4.5.8 only. Live tests covered native Linux with
Steam Flatpak game files, a dedicated TCP server, and the actual FNA client.
Windows and Steam friend connections are not yet live-tested.

The release includes 19 server checks and a client F9 pause/resume capture.
Source code is MIT licensed; no Terraria executables, libraries, or saves are
included. `RELEASE_SHA256SUMS.txt` contains the archive checksum.

---

Пауза для всех игроков в обычной Terraria 1.4.5.8: **F9 у хоста**, повторное
**F9** продолжает игру. Патч нужен каждому участнику.

Скачайте **HostPause-0.1.0.zip**, распакуйте и при полностью закрытой игре
запустите `Install-Windows.cmd` или `bash Install-Linux.sh`.
Перед заменой старого выпуска сначала удалите его через Uninstall.
Это предварительный выпуск: Linux проверен, Windows и подключение через
Steam к другу ещё не проверены. Сохраните резервные копии персонажей и миров.
