# HostPause 0.2.0 — resume countdown, server console, Steam-proof installer

- **Resume countdown.** F9 during a pause starts a countdown (3 s by default) before the
  world moves again; F9 during the countdown cancels it. Leaving as host still releases
  the pause immediately.
- **Dedicated server console:** `pause`, `resume`, `hostpause`. Works on an empty server.
  A console pause survives the host leaving, and only the console can end it.
- **Who paused** is shown to every player; pause, resume and countdown play sounds.
  Guests no longer see their own key presented as the host's.
- **No desync at the pause edge.** Gameplay a client sent before it learned of the pause
  is applied on the server (clients acknowledge the pause; only later packets are dropped).
- **Settings:** `HostPause.config.txt` with `key` and `countdown`. A 0.1.x
  `HostPause.key.txt` is migrated on upgrade.
- **Installer:** `status` command; Install over an earlier version upgrades in place and
  patches the originals before touching the old install; after a Steam update or file
  verification, Install repairs and Uninstall cleans up without touching Steam's files;
  edited settings are kept; a game folder moved by Steam still finds its backups.
- Fixes: a host whose patch fails now releases the server pause at once; a cancel press
  is never rate-limited; the countdown uses a monotonic clock; console commands sent in a
  burst all run; server startup prompts are never mistaken for commands.

**Update every participant, host, and server.** Protocol 3 refuses shared pause with
0.1.x. Close Terraria and its server, extract **HostPause-0.2.0.zip** into a new folder,
and run Install on each PC — the earlier version is replaced automatically.

Validation (Linux only): 31 server integration checks; live console, takeover and burst
checks; a graphical host that verifies the countdown and the grayscale frame; a graphical
guest with a forced capture failure; installer lifecycle from the real 0.1.2 release;
graphics-failure regressions. Windows gameplay and Steam friend connections still need
live verification. This remains an experimental prerelease limited to Terraria 1.4.5.8.

---

**F9 во время паузы запускает отсчёт перед продолжением**, повторное F9 отменяет его.
Выделенный сервер ставится на паузу командами `pause` / `resume` в консоли. Игроки видят,
кто поставил паузу. Действия, сделанные за мгновение до паузы, больше не теряются.
Настройки — в `HostPause.config.txt`. Установщик понимает обновления Steam: `status`
показывает, работает ли патч, а повторный Install восстанавливает его.

**Обновиться до 0.2.0 нужно всем, включая хоста и сервер.** Закройте игру, распакуйте
**HostPause-0.2.0.zip** в новую папку и выполните Install — прошлая версия заменится сама.
