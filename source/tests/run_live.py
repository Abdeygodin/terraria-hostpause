from pathlib import Path
import subprocess,os,socket,time,signal,sys,json
root=Path(__file__).resolve().parents[1]
# Live end-to-end checks on an isolated test bed made by prepare_testbed.py.
data=root/'test-data'
sys.path.insert(0,str(root/'tests'))
results=[]
def check(condition,name):
 assert condition,name
 results.append(name)
 print('PASS',name,flush=True)
from network_probe import Client
base_env=os.environ.copy()
base_env.update(SDL_VIDEODRIVER='offscreen',FNA3D_FORCE_DRIVER='OpenGL')
children=[]
def console_checks(server):
 def say(command):
  server.stdin.write((command+'\n').encode());server.stdin.flush()
 log=data/'server-0.2.0.log'
 time.sleep(1.5)
 guest=Client('HP-Console-Guest');guest.pause(1);guest.drain(.8)
 say('pause');guest.drain(.8)
 check(guest.states[-1][0]==1 and guest.states[-1][3]==2,'Console pause reaches players and names the server as the source')
 guest.pause(3,0);guest.drain(.4)
 check(guest.denied[-1]==1 and guest.states[-1][0]==1,'Players cannot resume a console pause')
 say('hostpause');guest.drain(.6)
 check('HostPause 0.2.0: пауза (консоль)' in log.read_text(),'hostpause console command reports the state')
 say('resume');guest.drain(.5)
 check(guest.states[-1][0]==1 and guest.states[-1][4]==2,'Console resume starts the countdown')
 guest.drain(2.2)
 check(guest.states[-1][0]==0,'Console resume finishes after the countdown')
 guest.close();time.sleep(1.5)
 paused_lines=log.read_text().count('HostPause: пауза. Новые игроки')
 say('pause');time.sleep(1)
 check(log.read_text().count('HostPause: пауза. Новые игроки')==paused_lines+1,'Console pause works on an empty server')
 refused=False
 try:Client('HP-Console-Late')
 except RuntimeError as e:refused=str(e).startswith('Disconnected')
 check(refused,'Joining a console-paused empty server is refused with a message')
 time.sleep(1);say('resume');time.sleep(3)
 late=Client('HP-Console-Late');late.pause(1);late.drain(.8)
 check(late.states and late.states[-1][0]==0,'Empty server resumes from the console without any connected player')
 late.close();time.sleep(1)
 # A host cannot end a console pause; a console pause taken over from the host survives the host leaving.
 host=Client('HP-Console-Host','HostPause-Isolated-Test');host.pause(1);host.drain(.8)
 say('pause');host.drain(.8)
 host.pause(3,0);host.drain(.5)
 check(host.denied[-1]==3 and host.states[-1][0]==1,'The host cannot resume a console pause')
 say('resume');host.drain(3)
 check(host.states[-1][0]==0,'Console resume ends it')
 host.drain(.5);host.pause(3,1);host.drain(.6)
 check(host.states[-1][0]==1 and host.states[-1][3]==1,'Host pause on the same server')
 say('pause');host.drain(.6)
 check(host.states[-1][0]==1 and host.states[-1][3]==2,'Console pause takes over a host pause')
 host.close();time.sleep(1.5)
 refused=False
 try:Client('HP-Console-Late2')
 except RuntimeError as e:refused=str(e).startswith('Disconnected')
 check(refused,'A taken-over pause survives the host leaving')
 say('resume');time.sleep(3)
 late=Client('HP-Console-Late3');late.pause(1);late.drain(.8)
 check(late.states and late.states[-1][0]==0,'Server runs again after console resume')
 late.close();time.sleep(1)
 # Commands sent in one burst must all run, in order.
 burst=log.read_text().count('HostPause 0.2.0: ')
 server.stdin.write(b'pause\nhostpause\nresume\nhostpause\n');server.stdin.flush();time.sleep(4)
 text=log.read_text()
 status=[l for l in text.splitlines() if 'HostPause 0.2.0: ' in l][-2:]
 check(text.count('HostPause 0.2.0: ')==burst+2 and 'пауза (консоль)' in status[0] and 'продолжение через' in status[1],'Console commands sent in one burst all run in order')
def stop(process):
 if process.poll() is None:
  os.killpg(process.pid,signal.SIGTERM)
  try:process.wait(10)
  except subprocess.TimeoutExpired:os.killpg(process.pid,signal.SIGKILL);process.wait()
def client(env,log):
 stream=(data/log).open('w')
 process=subprocess.Popen(['./Terraria','-savedirectory',str(root/'test-client'),'-nosteam'],cwd=root/'test-game',env=env,stdout=stream,stderr=subprocess.STDOUT,start_new_session=True)
 children.append(process)
 return process
# Clients append to these logs: start empty so a stale PASS line can never count.
for stale in [data/'render-test.log',data/'guest-render-test.log',root/'test-game/HostPause.log',data/'guest-frozen.signal']:stale.unlink(missing_ok=True)
with (data/'server-0.2.0.log').open('w') as stream:
 server=subprocess.Popen(['./TerrariaServer','-config',str(data/'serverconfig.txt'),'-savedirectory',str(data),'-nosteam','-hosttoken','HostPause-Isolated-Test'],cwd=root/'test-game',stdin=subprocess.PIPE,stdout=stream,stderr=subprocess.STDOUT,start_new_session=True)
 children.append(server)
 try:
  deadline=time.monotonic()+60
  while True:
   assert server.poll() is None,'Server exited'
   # Wait for the log line, not a probe connection: vanilla 1.4.5.8 servers can crash with
   # ObjectDisposedException when a TCP connection opens and closes immediately.
   if 'Server started' in (data/'server-0.2.0.log').read_text(errors='replace'):break
   if time.monotonic()>deadline:raise TimeoutError('Server startup')
   time.sleep(.5)
  if '--guest-only' not in sys.argv:
   with (data/'integration-0.2.0.log').open('w') as log:
    subprocess.run([sys.executable,str(root/'tests/integration.py')],stdout=log,stderr=subprocess.STDOUT,check=True,timeout=200)
   check(True,'full server integration (integration-result.json)')
   console_checks(server)
   host_client=client(base_env,'client-0.2.0.log')
   assert host_client.wait(120)==0,'Graphical host failed'
   render=(data/'render-test.log').read_text()
   assert 'TIMEOUT' not in render and 'FAIL' not in render,render
   assert 'PASS: actual F9 resumed client simulation; captured=True' in render,render
   assert 'PASS: countdown kept the client paused' in render,render
   assert 'PASS: captured pause frame is grayscale' in render,render
   check(True,'graphical host F9 pause, countdown resume and grayscale screenshot')
   time.sleep(1)
  host=Client('HP-GuestTestHost','HostPause-Isolated-Test');host.pause(1);host.drain(.8)
  marker=data/'guest-frozen.signal';marker.unlink(missing_ok=True)
  env=base_env.copy();env.update(HOSTPAUSE_RENDER_ROLE='guest',HOSTPAUSE_TEST_CAPTURE_FAILURE='1')
  guest_joins_before=(data/'server-0.2.0.log').read_text().count('HP-Render-Client has joined.')
  guest_client=client(env,'guest-client-0.2.0.log')
  deadline=time.monotonic()+75
  while (data/'server-0.2.0.log').read_text().count('HP-Render-Client has joined.') <= guest_joins_before:
   assert guest_client.poll() is None,'Guest exited before join'
   if time.monotonic()>deadline:raise TimeoutError('Guest join')
   host.drain(.2)
  deadline=time.monotonic()+15
  while True:
   assert guest_client.poll() is None,'Guest exited before pause'
   host.pause(3,1);host.drain(.4)
   if host.states and host.states[-1][0]==1:break
   if time.monotonic()>deadline:raise TimeoutError('Guest negotiation')
   time.sleep(.5)
  deadline=time.monotonic()+20
  while not marker.exists():
   assert guest_client.poll() is None,'Guest resumed/exited locally after capture failure'
   if time.monotonic()>deadline:raise TimeoutError('Guest did not freeze')
   host.drain(.2)
  host.pause(3,0);host.drain(.6)
  assert guest_client.wait(45)==0,'Guest did not resume'
  text=(data/'guest-render-test.log').read_text()
  assert 'PASS: graphical guest simulation frozen despite screenshot failure' in text
  assert 'PASS: server resume reached graphical guest after screenshot failure' in text
  assert 'Pause visuals disabled; simulation and network pause remain active' in (root/'test-game/HostPause.log').read_text()
  host.close()
  (data/'graphical-guest-result.json').write_text(json.dumps({'graphical_guest_capture_failure_injected':True,'guest_simulation_frozen':True,'server_resume_received':True,'platform':'actual Linux FNA graphical client; controlled capture failure'},indent=2))
  check(True,'graphical guest stays frozen after capture failure and resumes only with server')
  (data/'live-result.json').write_text(json.dumps({'passed':len(results),'checks':results},ensure_ascii=False,indent=2))
 finally:
  for process in reversed(children):stop(process)
