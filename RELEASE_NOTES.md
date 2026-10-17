# HostPause 0.1.1 — Windows installer fix (experimental prerelease)

Fixes the Windows installation error:

```text
Failed to resolve assembly: Microsoft.Xna.Framework.Graphics, Version=4.0.0.0
```

The .NET 8 patcher now searches the Windows .NET Framework directories and the
legacy/modern Global Assembly Cache for the exact referenced assemblies. XNA is
read from the existing system installation; no XNA or Terraria binaries are shipped.

**If 0.1.0 failed with this error:** download **HostPause-0.1.1.zip**, extract it
into a separate directory, close Terraria, and run **Install-Windows.cmd** again.
That failure happens during staging, before the game files are replaced, so there
is no installed patch to uninstall. If an earlier build installed successfully,
uninstall it first before replacing it.

F9 pause/resume, the compact grayscale overlay, the runtime, and the network
protocol are unchanged. Every participant still needs HostPause. Working Linux
installations can stay on 0.1.0 for this installer-only fix.

**Validation:** reproduced the original error with the official Windows 1.4.5.8
server executable; the same executable patches successfully with a simulated
Windows GAC using Wine Mono XNA facades. Ten regression checks passed. Linux
patched executables are byte-identical to the earlier tested release. The new
self-contained Linux installer and exact uninstall rollback also passed.

**Limits:** Terraria 1.4.5.8 only. Windows gameplay and Steam friend connections
remain untested. This is an experimental prerelease.

---

Исправлен поиск XNA в установщике Windows. Если 0.1.0 завершился ошибкой
**Microsoft.Xna.Framework.Graphics**, скачайте **HostPause-0.1.1.zip**,
распакуйте в отдельную папку и при закрытой Terraria повторите
**Install-Windows.cmd**. Удалять неудавшуюся установку не требуется.

Уже работающий патч на Linux обновлять не нужно. Управление через F9, картинка
паузы и сетевой протокол не изменились. Сама игра на Windows и подключение через
Steam к другу пока не проверены.
