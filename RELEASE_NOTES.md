# HostPause 0.1.2 — keep every client paused after a graphics failure

Fixes a client-side bug where a screenshot/overlay error could disable all hooks,
letting that client move and simulate enemies while the server and other players
remained paused. Windows XNA Reach blocks normal backbuffer readback, making it
susceptible to this failure path.

- Rendering errors now disable only pause effects. The client simulation stays
  frozen, networking continues, and the server can resume it normally.
- XNA Reach screenshot capture temporarily lifts only the readback capability,
  restoring it even on exceptions. The graphics profile and settings are unchanged.
- Protocol 2 refuses pause with old 0.1.0/0.1.1 runtimes instead of silently mixing
  clients with the vulnerable error handler.
- `HostPause.log` beside the game executable records version, handshake, received
  states, and faults. No player names, Steam IDs, or host tokens are logged.
- The Windows XNA dependency discovery fix from 0.1.1 is retained.

**Update every participant, host, and server.** Download **HostPause-0.1.2.zip**
and extract it into a new folder. Close Terraria and its server, run the previous
Uninstall script, then the new Install script on each PC. If the earlier install
failed during XNA resolution, no uninstall is needed. Wait for the
**HostPause 0.1.2 готов** message after reconnecting, then use **F9** as host.

Validation: 20 real Linux server checks, including a 125-second pause and old
protocol rejection; F9 pause/resume in the graphical host; a graphical guest with
an intentionally broken capture that remains frozen until server resume; seven
focused graphics-failure regressions. The game is still limited to Terraria 1.4.5.8.
Windows gameplay and the full Steam friend connection path need live verification.
This remains an experimental prerelease.

---

Исправлен случай, когда из-за ошибки картинки паузы клиент продолжал ходить и
обновлять врагов. Теперь ошибка графики отключает только эффекты, а пауза
симуляции и сетевые команды продолжают работать. Добавлен обход ограничения
чтения кадра XNA Reach с обязательным восстановлением флага.

**Обновиться до 0.1.2 нужно всем, включая хоста и сервер.** Закройте игру,
удалите старый патч через Uninstall, распакуйте **HostPause-0.1.2.zip** в новую
папку и выполните Install. Со старыми версиями совместная пауза не включается.
После подключения дождитесь **HostPause 0.1.2 готов**, затем хост нажимает F9.

В случае повторения проблемы приложите `HostPause.log` из папок игры хоста и
проблемного клиента. Windows и полная связка через Steam ещё требуют проверки.
