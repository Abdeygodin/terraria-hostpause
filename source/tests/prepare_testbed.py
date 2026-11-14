"""Patch an isolated test copy of Terraria 1.4.5.8 and add the test instrumentation.

Usage: python3 tests/prepare_testbed.py <directory with original Terraria.exe and TerrariaServer.exe>

Expects the source tree root to contain test-game/ (a copy of the Linux game files) and
test-data/. Never point this at a real game installation: it overwrites test-game/.
"""
from pathlib import Path
import os
import re
import shutil
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
originals = Path(sys.argv[1]).resolve()
game = root/'test-game'
dotnet = os.environ.get('HOSTPAUSE_DOTNET', 'dotnet')
assert (game/'mscorlib.dll').exists(), 'test-game must be a copy of the Linux game files'

subprocess.run([sys.executable, str(root/'build.py'), str(game)], check=True)
for path in game.glob('HostPause*'):
    if path.is_dir(): shutil.rmtree(path)
    else: path.unlink()
for name in ['Terraria.exe', 'TerrariaServer.exe']:
    shutil.copy2(originals/name, game/name)

staging = root/'build/staged'
shutil.rmtree(staging, ignore_errors=True)
subprocess.run([dotnet, str(root/'build/HostPause.Patcher.dll'), 'prepare', str(game), str(staging)], check=True)
for path in staging.iterdir():
    if path.name != 'patch-report.json': shutil.copy2(path, game/path.name)
(game/'HostPause.config.txt').write_text('key=F9\ncountdown=2\n')

sdks = subprocess.check_output([dotnet, '--list-sdks'], text=True)
sdk = next(reversed(re.findall(r'^(8\.[\d.]+) \[(.+)\]$', sdks, re.M)))
csc = Path(sdk[1])/sdk[0]/'Roslyn/bincore/csc.dll'
framework = ['-r:'+str(game/n) for n in ['mscorlib.dll', 'System.dll', 'System.Core.dll', 'FNA.dll', 'HostPause.Runtime.dll']]
for source, output, target in [('Audit.cs', 'HostPause.Audit.dll', 'TerrariaServer.exe'), ('RenderClient.cs', 'HostPause.RenderTest.dll', 'Terraria.exe')]:
    subprocess.run([dotnet, str(csc), '-nologo', '-target:library', '-nostdlib+', '-nowarn:1701,1702', *framework, '-r:'+str(game/target),
                    '-out:'+str(game/output), str(root/'tests'/source)], check=True)
    subprocess.run([dotnet, 'run', '--project', str(root/'tests/Instrument'), '-c', 'Release', '--', str(game/target), str(game/output)], check=True)
print('Test bed ready:', game)
