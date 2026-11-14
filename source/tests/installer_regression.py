"""Installer lifecycle on an isolated copy of the game files.

Usage: python3 tests/installer_regression.py <test game dir> <original exes dir> <0.1.2 patcher> <output json>

Copies the test game into a temporary fixture, installs the previous 0.1.2 release,
customises its key, then checks the 0.2.0 upgrade, a simulated Steam file replacement,
repair, status reporting and byte-exact uninstall. Never touches a real installation.
"""
from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile

game, originals, legacy, output = (Path(a).resolve() for a in sys.argv[1:5])
root = Path(__file__).resolve().parents[1]
dotnet = os.environ.get('HOSTPAUSE_DOTNET', 'dotnet')
results = []


def check(condition, name):
    assert condition, name
    results.append(name)
    print('PASS', name, flush=True)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(command, fixture, expect_ok=True):
    process = subprocess.run(command + [str(fixture)], capture_output=True, text=True)
    text = process.stdout + process.stderr
    assert (process.returncode == 0) == expect_ok, text
    return text


def new(action, fixture, expect_ok=True):
    return run([dotnet, str(root/'build/HostPause.Patcher.dll'), action], fixture, expect_ok)


with tempfile.TemporaryDirectory(prefix='hostpause-installer-') as temp:
    fixture = Path(temp)/'Terraria'
    shutil.copytree(game, fixture, ignore=shutil.ignore_patterns('HostPause*'))
    for name in ['Terraria.exe', 'TerrariaServer.exe']:
        shutil.copy2(originals/name, fixture/name)
    before = {p.relative_to(fixture).as_posix(): sha(p) for p in fixture.rglob('*') if p.is_file()}

    run([str(legacy), 'install'], fixture)
    check(json.loads((fixture/'HostPause.install.json').read_text())['Version'] == '0.1.2', 'Previous 0.1.2 release installed in the fixture')
    (fixture/'HostPause.key.txt').write_text('F8\n')

    text = new('install', fixture)
    manifest = json.loads((fixture/'HostPause.install.json').read_text())
    check(manifest['Version'] == '0.2.0' and 'Удалена предыдущая установка HostPause 0.1.2' in text, 'Installing 0.2.0 over 0.1.2 upgrades in place')
    check('key=F8' in (fixture/'HostPause.config.txt').read_text() and not (fixture/'HostPause.key.txt').exists(), 'Customised 0.1.x key moves into HostPause.config.txt')
    check('патч активен' in new('status', fixture), 'status reports an active patch')

    (fixture/'HostPause.config.txt').write_text('key=F7\ncountdown=5\n')
    shutil.copy2(originals/'Terraria.exe', fixture/'Terraria.exe')  # What a Steam update or file verification does.
    check('Steam заменил файлы игры' in new('status', fixture), 'status detects executables replaced by Steam')
    text = new('install', fixture)
    check('Steam заменил файлы игры после установки' in text and 'патч активен' in new('status', fixture), 'Install repairs the patch after a Steam replacement')
    check((fixture/'HostPause.config.txt').read_text() == 'key=F7\ncountdown=5\n', 'Repair keeps the edited settings')

    shutil.copy2(originals/'TerrariaServer.exe', fixture/'TerrariaServer.exe')  # Only one executable replaced.
    text = new('uninstall', fixture)
    check('Steam уже заменил TerrariaServer.exe' in text, 'Uninstall after a partial Steam replacement leaves Steam\'s file alone')
    check(not (fixture/'HostPause.install.json').exists() and not (fixture/'HostPause.Runtime.dll').exists(), 'Uninstall removes the manifest and runtime')
    check((fixture/'HostPause.config.txt').exists(), 'Uninstall keeps edited settings for a later install')
    (fixture/'HostPause.config.txt').unlink()
    after = {p.relative_to(fixture).as_posix(): sha(p) for p in fixture.rglob('*') if p.is_file() and 'HostPause.backups' not in p.parts}
    check(after == before, 'Every original game file is byte-identical after uninstall')

    new('install', fixture)
    new('uninstall', fixture)
    check(not (fixture/'HostPause.config.txt').exists(), 'Unedited default settings are removed on uninstall')
    check(new('uninstall', fixture, expect_ok=False).strip().startswith('HostPause не установлен'), 'Uninstall without an installation fails clearly')

output.write_text(json.dumps({'passed': len(results), 'checks': results}, ensure_ascii=False, indent=2))
print(json.dumps({'passed': len(results)}), flush=True)
