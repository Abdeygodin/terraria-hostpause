"""Build the portable patcher and the .NET Framework-compatible runtime from source."""
from pathlib import Path
import os
import re
import subprocess
import sys
import shutil

root = Path(__file__).resolve().parent
game = Path(sys.argv[1]).resolve()
dotnet = os.environ.get('HOSTPAUSE_DOTNET', 'dotnet')
subprocess.run([dotnet, 'build', str(root/'Patcher'), '-c', 'Release', '-o', str(root/'build')], check=True)
sdks = subprocess.check_output([dotnet, '--list-sdks'], text=True)
sdk = next(reversed(re.findall(r'^(8\.[\d.]+) \[(.+)\]$', sdks, re.M)))
csc = Path(sdk[1])/sdk[0]/'Roslyn/bincore/csc.dll'
reference = game/'mscorlib.dll'
if not reference.exists() and os.name == 'nt':
    reference = Path(os.environ['SystemRoot'])/'Microsoft.NET/Framework64/v4.0.30319/mscorlib.dll'
if not reference.exists():
    raise SystemExit('A .NET Framework mscorlib.dll reference is required.')
subprocess.run([dotnet, str(csc), '-nologo', '-deterministic+', '-target:library', '-nostdlib+', '-r:'+str(reference), '-out:'+str(root/'build/HostPause.Runtime.dll'), str(root/'Runtime.cs'), str(root/'Visuals.cs')], check=True)
shutil.copy2(root/'assets/banner.png', root/'build/HostPause.banner.png')
print('Built:', root/'build')
