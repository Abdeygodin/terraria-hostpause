"""Two protocol clients exercise the actual patched 1.4.5.8 server and its audit hook.

The server must run with HostPause.config.txt countdown=2."""
from network_probe import Client
from pathlib import Path
import json
import struct
import time

root = Path(__file__).resolve().parents[1]
results = []


def check(condition, name):
    assert condition, name
    results.append(name)
    print('PASS', name, flush=True)


def pump(clients, seconds):
    deadline = time.monotonic()+seconds
    while time.monotonic() < deadline:
        for client in clients:
            client.read()


def audit():
    return [line.split('\t') for line in (root/'test-data/audit.tsv').read_text().splitlines() if line]


host = Client('HP-Host', 'HostPause-Isolated-Test')
host.pause(1)
host.drain(.4)
guest = Client('HP-Guest')
guest.pause(1)
pump([host, guest], 1)
check(bool(host.states) and bool(guest.states), 'Both clients negotiated protocol v3')
check(host.states[-1][0] == guest.states[-1][0] == 0, 'Initial state is running')

guest.pause(3, 1)
pump([host, guest], .4)
check(guest.denied[-1] == 1 and host.states[-1][0] == 0, 'Guest cannot pause, even on loopback')

before = audit()[-1]
pump([host, guest], .6)
after = audit()[-1]
check(before[2:4] != after[2:4], 'Real simulation and world clock advance before pause')

host.pause(3, 1)
pump([host, guest], .6)
check(host.states[-1][0] == guest.states[-1][0] == 1, 'Host pauses both clients')
check(guest.states[-1][3] == 1 and guest.states[-1][4] == 0, 'Clients are told the host paused, with no countdown')
frozen = audit()[-1]
check(frozen[1] == 'True', 'Server simulation is paused')

# Attempt mutations through normal vanilla packets while frozen.
host.send(13, bytes([host.slot, 0, 16, 0, 0, 0])+struct.pack('<ff', 500, 500))
host.send(16, struct.pack('<Bhh', host.slot, 1, 100))
guest.pause(2, 0)  # A guest may not forge a server-state broadcast.
guest.send(254, b'bad')
host.pause(3, 0, revision=0)  # Old/duplicate requests must not toggle the current state.
pump([host, guest], .8)
check(host.states[-1][0] == guest.states[-1][0] == 1, 'Stale requests and forged state cannot resume')
check(audit()[-1][2:] == frozen[2:], 'World tick, clock, NPC, projectile, player position, HP and respawn timer stay frozen')

guest.pause(3, 0)
pump([host, guest], .5)
check(guest.denied[-1] == 1 and host.states[-1][0] == 1, 'Guest cannot resume')

joiner = Client.__new__(Client)
import socket
from network_probe import string
joiner.sock = socket.create_connection(('127.0.0.1', 17777), 5)
joiner.sock.sendall(struct.pack('<HB', len(string('Terraria326'))+3, 1)+string('Terraria326'))
joiner.sock.settimeout(3)
reply = joiner.sock.recv(4096)
joiner.sock.close()
check(reply[2] == 2 and 'паузе'.encode() in reply, 'New joins receive a clear paused-server message')

# Exceeds vanilla's usual 7,200-tick connection timeout. State heartbeats must keep flowing.
deadline = time.monotonic()+125
last_report = time.monotonic()
while time.monotonic() < deadline:
    pump([host, guest], 2)
    if time.monotonic()-last_report > 25:
        print('Long pause remains connected;', round(deadline-time.monotonic()), 'seconds left', flush=True)
        last_report = time.monotonic()
check(audit()[-1][2:] == frozen[2:], '125-second pause freezes simulation without disconnecting either client')
check(len([s for s in host.states if s[0] == 1]) > 110, 'State heartbeats continue throughout pause')

# Resume starts a countdown; pressing the key again during it cancels and stays paused.
host.pause(3, 0)
pump([host, guest], .5)
check(guest.states[-1][0] == 1 and guest.states[-1][4] == 2, 'Resume request starts a 2-second countdown while still paused')
host.pause(3, 1)
pump([host, guest], .5)
check(guest.states[-1][0] == 1 and guest.states[-1][4] == 0, 'Host cancels the countdown and the game stays paused')
pump([host, guest], 2.5)
check(guest.states[-1][0] == 1 and audit()[-1][2:] == frozen[2:], 'A cancelled countdown never resumes the world')

host.pause(3, 0)
pump([host, guest], .5)
check(audit()[-1][2:] == frozen[2:], 'World stays frozen during the countdown')
second = len(guest.states)
started = time.monotonic()
while guest.states[-1][0] == 1 and time.monotonic()-started < 5:
    pump([host, guest], .1)
seconds = [s[4] for s in guest.states[second-1:] if s[0] == 1 and s[4] > 0]
check(host.states[-1][0] == guest.states[-1][0] == 0 and 1.0 < time.monotonic()-started < 2.6, 'Countdown resumes both clients after about 2 seconds')
check(2 in seconds and 1 in seconds, 'Clients receive each countdown second')
pump([host, guest], .5)
check(audit()[-1][2:4] != frozen[2:4], 'Real world simulation resumes')

# Gameplay a client sent before it saw the pause must still reach the server, or the world desyncs.
pump([host, guest], .5)
host.pause(3, 1)
host.send(13, bytes([host.slot, 0, 16, 0, 0, 0])+struct.pack('<ff', 640, 480))
pump([host, guest], .6)
check(host.states[-1][0] == 1 and '640' in audit()[-1][9], 'A move sent in flight with the pause request is applied, not lost')
host.send(13, bytes([host.slot, 0, 16, 0, 0, 0])+struct.pack('<ff', 900, 300))
pump([host, guest], .6)
check('640' in audit()[-1][9] and '900' not in audit()[-1][9], 'Moves after the client acknowledged the pause are dropped')
host.pause(3, 0)
pump([host, guest], 3)
check(host.states[-1][0] == 0, 'Resume after the in-flight check')

# A client without the patch must prevent pause, without preventing ordinary play.
vanilla = Client('HP-No-Patch')
pump([host, guest, vanilla], .7)
host.pause(3, 1)
pump([host, guest, vanilla], .5)
check(host.denied[-1] == 2 and host.states[-1][0] == 0, 'Mixed patched/unpatched sessions reject pause safely')
vanilla.close()
pump([host, guest], 1.1)
legacy = Client('HP-Old-Runtime')
legacy.send(254, b'HPZ1'+bytes([1, 1, 0])+struct.pack('<I', 0))
pump([host, guest, legacy], .7)
host.pause(3, 1)
pump([host, guest, legacy], .5)
check(host.denied[-1] == 2 and host.states[-1][0] == 0, 'Old protocol v1 runtimes cannot silently join a v3 pause')
legacy.close()
pump([host, guest], 1.1)
legacy = Client('HP-0.1.2-Runtime')
legacy.send(254, b'HPZ1'+bytes([2, 1, 0])+struct.pack('<I', 0))
pump([host, guest, legacy], .7)
host.pause(3, 1)
pump([host, guest, legacy], .5)
check(host.denied[-1] == 2 and host.states[-1][0] == 0, '0.1.2 protocol v2 runtimes cannot silently join a v3 pause')
legacy.close()
pump([host, guest], 1.1)
host.pause(3, 1)
pump([host, guest], .6)
check(host.states[-1][0] == guest.states[-1][0] == 1, 'Pause works again after unpatched guest disconnects')
host.pause(3, 0)
pump([host, guest], .4)
check(guest.states[-1][4] > 0, 'Countdown running before host leaves')
host.close()
pump([guest], 1.0)
check(guest.states[-1][0] == 0, 'Host disconnect automatically releases pause, even mid-countdown')
guest.close()

# Vanilla stops simulation updates when the last client leaves; packet handling must still release pause.
time.sleep(1)
solo = Client('HP-Solo-Host', 'HostPause-Isolated-Test')
solo.pause(1)
solo.drain(.6)
solo.pause(3, 1)
solo.drain(.6)
check(solo.states[-1][0] == 1, 'A solo host can pause')
solo.close()
time.sleep(1)
recovery = Client('HP-Reconnect', 'HostPause-Isolated-Test')
recovery.pause(1)
recovery.drain(.6)
check(recovery.states[-1][0] == 0, 'Empty paused server accepts the next connection after solo host leaves')
recovery.close()

report = {'passed': len(results), 'checks': results, 'server': 'Terraria Linux 1.4.5.8', 'clients': 'two independent TCP protocol clients', 'long_pause_seconds': 125, 'countdown_seconds': 2}
(root/'test-data/integration-result.json').write_text(json.dumps(report, ensure_ascii=False, indent=2))
print(json.dumps(report, ensure_ascii=False, indent=2), flush=True)
