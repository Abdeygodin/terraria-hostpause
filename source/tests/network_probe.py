import socket
import struct
import time
from collections import Counter


def string(text):
    data = text.encode('utf-8')
    length, prefix = len(data), bytearray()
    while length >= 128:
        prefix.append((length & 127) | 128)
        length >>= 7
    return bytes(prefix + bytes([length])) + data


class Client:
    def __init__(self, name, token=None):
        self.sock = socket.create_connection(('127.0.0.1', 17777), 5)
        self.sock.settimeout(.25)
        self.buf = b''
        self.counts = Counter()
        self.states = []
        self.denied = []
        self.revision = 0
        self.slot = None
        self.send(1, string('Terraria326'))
        deadline = time.monotonic() + 10
        while self.slot is None and time.monotonic() < deadline:
            for kind, body in self.read():
                if kind == 2:
                    raise RuntimeError('Disconnected: ' + repr(body))
                if kind == 3:
                    self.slot = body[0]
        assert self.slot is not None, self.counts
        body = bytes([self.slot, 0, 1]) + struct.pack('<f', 0) + bytes([0]) + string(name)
        body += bytes(4) + bytes([128, 128, 128])*7 + bytes(3)
        self.send(4, body)
        self.send(16, struct.pack('<Bhh', self.slot, 100, 100))
        if token:
            self.send(161, string(token))
        self.send(6)
        deadline = time.monotonic() + 10
        got_world = False
        while not got_world and time.monotonic() < deadline:
            for kind, body in self.read():
                if kind == 2:
                    raise RuntimeError('Disconnected: ' + repr(body))
                if kind == 7:
                    self.spawn = struct.unpack_from('<hh', body, 10)
                    got_world = True
        assert got_world, self.counts
        self.send(8, struct.pack('<iiB', -1, -1, 0))
        finished = False
        deadline = time.monotonic() + 20
        while not finished and time.monotonic() < deadline:
            for kind, body in self.read():
                if kind == 2:
                    raise RuntimeError('Disconnected: ' + repr(body))
                if kind == 49:
                    finished = True
        assert finished, self.counts
        self.send(12, struct.pack('<BhhihhBB', self.slot, -1, -1, 0, 0, 0, 0, 0))
        time.sleep(.2)

    def send(self, kind, payload=b''):
        self.sock.sendall(struct.pack('<HB', len(payload)+3, kind)+payload)

    def pause(self, op, value=0, revision=None):
        self.send(254, b'HPZ1'+bytes([2, op, value])+struct.pack('<I', self.revision if revision is None else revision))

    def read(self):
        try:
            data = self.sock.recv(1048576)
            if not data:
                raise ConnectionError('Server closed socket')
            self.buf += data
        except socket.timeout:
            pass
        packets = []
        while len(self.buf) >= 2:
            length = struct.unpack_from('<H', self.buf)[0]
            assert length >= 3
            if len(self.buf) < length:
                break
            kind, body = self.buf[2], self.buf[3:length]
            self.buf = self.buf[length:]
            self.counts[kind] += 1
            packets.append((kind, body))
            if kind == 254 and body[:5] == b'HPZ1\x02':
                op, value, revision = struct.unpack('<BBI', body[5:])
                if op == 2:
                    self.revision = revision
                    self.states.append((value, revision, time.monotonic()))
                elif op == 4:
                    self.denied.append(value)
        return packets

    def drain(self, seconds=.3):
        deadline = time.monotonic()+seconds
        while time.monotonic() < deadline:
            self.read()

    def close(self):
        self.sock.close()


if __name__ == '__main__':
    host = Client('HP-Test-Host', 'HostPause-Isolated-Test')
    host.pause(1)
    host.drain(1)
    print('HOST', host.slot, host.spawn, host.states, host.counts, flush=True)
    guest = Client('HP-Test-Guest')
    guest.pause(1)
    guest.drain(1)
    host.drain(.3)
    print('GUEST', guest.slot, guest.states, guest.counts, flush=True)
    host.pause(3, 1)
    host.drain(1)
    guest.drain(.3)
    print('PAUSE', host.states, guest.states, host.denied, flush=True)
    guest.pause(3, 0)
    guest.drain(.5)
    print('GUEST_DENIED', guest.denied, flush=True)
    host.pause(3, 0)
    host.drain(.6)
    guest.drain(.3)
    print('RESUME', host.states, guest.states, flush=True)
    host.close(); guest.close()
