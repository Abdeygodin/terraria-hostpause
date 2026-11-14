using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Collections.Generic;

namespace HostPause
{
    // Deliberately depends only on mscorlib. FNA and XNA clients use the same protocol.
    public static class Runtime
    {
        public const string Version = "0.2.0";
        const byte PacketId = 254, Protocol = 3, Hello = 1, State = 2, Request = 3, Denied = 4, Ack = 5;
        // Request values: 0 resume (countdown), 1 pause or cancel the countdown, 2 release now (host patch fault).
        // Denied values: 1 not the host, 2 a player lacks HostPause or is still joining, 3 console pause.
        // Pause sources, carried in every state packet so clients can say who paused.
        const byte SourceLocal = 0, SourceHost = 1, SourceConsole = 2;
        // Console commands queued by the server input thread for the main thread.
        const int ConsolePause = 1, ConsoleResume = 2, ConsoleStatus = 3;
        const int PacketLength = 16, MaxCountdown = 10, AckWindow = 2000;
        static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        static Type main, netplay, message;
        static readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
        static object[] peerSockets = new object[256];
        static bool[] compatible = new bool[256], acked = new bool[256];
        static long[] lastRequests = new long[256];
        static bool paused, ready, wasDown, faulted, visualFaulted, soundFaulted;
        static byte source, countdown;
        static readonly object logLock = new object(), initLock = new object(), clockLock = new object();
        static readonly Queue<int> consoleQueue = new Queue<int>();
        static int previousMode = -1, lastTick;
        static bool clockStarted;
        static uint revision, pauseRevision;
        static long nextHello, nextHeartbeat, nextNotice, resumeAt, pausedAt, monotonic;
        static string keyName = "F9";
        static int countdownSeconds = 3;
        static object key;
        static MethodInfo keyDown, playSound;

        public static bool IsPaused { get { return paused; } }

        // Rendering is optional. A GPU/readback failure must not resume client gameplay
        // while the authoritative server and other clients remain frozen.
        static void VisualFault(Exception ex)
        {
            if (visualFaulted) return;
            visualFaulted = true;
            Log("Pause visuals disabled; simulation and network pause remain active: " + ex);
            try { Visuals.Reset(); } catch { }
            try { Notice("HostPause: ошибка эффектов паузы. Пауза мира продолжает работать; подробности в HostPause.log."); } catch { }
        }

        // A broken patch must never crash the game or leave the server stuck on pause.
        static void Fault(Exception ex)
        {
            if (faulted) return;
            // If graphics fail on the host, release its server pause before disabling the client patch.
            try { if (paused && ready && main != null && Get<int>(main, "netMode") == 1) Send(-1, Request, 2); }
            catch { }
            faulted = true;
            paused = false;
            resumeAt = 0;
            countdown = 0;
            revision++;
            Log("Disabled after error: " + ex);
            try { if (main != null && Get<int>(main, "netMode") == 2) BroadcastState(); }
            catch { }
        }

        static void Init(Assembly assembly)
        {
            if (main != null) return;
            lock (initLock)
            {
                if (main != null) return;
                Type mainType = assembly.GetType("Terraria.Main", true);
                netplay = assembly.GetType("Terraria.Netplay", true);
                message = assembly.GetType("Terraria.NetMessage", true);
                string directory = Path.GetDirectoryName(assembly.Location);
                LoadSettings(directory);
                bool dedicated = (bool)mainType.GetField("dedServ", Flags).GetValue(null);
                if (!dedicated)
                {
                    Type keyboard = mainType.GetField("keyState", Flags).FieldType;
                    keyDown = keyboard.GetMethod("IsKeyDown");
                    Type keys = keyDown.GetParameters()[0].ParameterType;
                    try { key = Enum.Parse(keys, keyName, true); }
                    catch { Log("Unknown key '" + keyName + "'; using F9"); keyName = "F9"; key = Enum.Parse(keys, keyName); }
                    try { Visuals.Init(mainType); }
                    catch (Exception ex) { main = mainType; VisualFault(ex); }
                    playSound = FindPlaySound(assembly);
                }
                else HookIdleServer(mainType);
                main = mainType;
            }
            Log("HostPause " + Version + " loaded; protocol=" + Protocol + "; key=" + keyName + "; countdown=" + countdownSeconds);
        }

        // HostPause.config.txt: "key=F9" and "countdown=3" (0 resumes instantly).
        // The 0.1.x HostPause.key.txt still sets the key when the config does not.
        static void LoadSettings(string directory)
        {
            bool keySet = false;
            string config = Path.Combine(directory, "HostPause.config.txt");
            if (File.Exists(config))
            {
                foreach (string raw in File.ReadAllLines(config))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int split = line.IndexOf('=');
                    if (split <= 0) continue;
                    string name = line.Substring(0, split).Trim().ToLowerInvariant(), value = line.Substring(split + 1).Trim();
                    int number;
                    if (name == "key" && value.Length > 0) { keyName = value; keySet = true; }
                    else if (name == "countdown" && Int32.TryParse(value, out number)) countdownSeconds = Math.Max(0, Math.Min(MaxCountdown, number));
                }
            }
            string legacy = Path.Combine(directory, "HostPause.key.txt");
            if (!keySet && File.Exists(legacy))
            {
                string value = File.ReadAllText(legacy).Trim();
                if (value.Length > 0) keyName = value;
            }
        }

        // A dedicated server with no fully connected players skips Main.Update entirely,
        // but still raises this public tick. Countdowns and console commands must run there too.
        static void HookIdleServer(Type mainType)
        {
            FieldInfo tick = mainType.GetField("OnTickForThirdPartySoftwareOnly", Flags);
            if (tick == null || !typeof(Delegate).IsAssignableFrom(tick.FieldType)) { Log("Idle server tick not found; console commands apply once a player is connected"); return; }
            Delegate handler = Delegate.CreateDelegate(tick.FieldType, typeof(Runtime).GetMethod("OnIdleServerTick", Flags));
            tick.SetValue(null, Delegate.Combine((Delegate)tick.GetValue(null), handler));
        }

        static MethodInfo FindPlaySound(Assembly assembly)
        {
            Type engine = assembly.GetType("Terraria.Audio.SoundEngine");
            if (engine == null) return null;
            MethodInfo best = null;
            foreach (MethodInfo method in engine.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "PlaySound") continue;
                ParameterInfo[] p = method.GetParameters();
                if (p.Length == 0 || p[0].ParameterType != typeof(int)) continue;
                bool optional = true;
                for (int i = 1; i < p.Length; i++) if (!p[i].IsOptional) optional = false;
                if (optional && (best == null || p.Length < best.GetParameters().Length)) best = method;
            }
            return best;
        }

        // Terraria sound IDs: 10 menu open, 11 menu close, 12 menu tick.
        static void Sound(int type)
        {
            if (playSound == null || soundFaulted) return;
            try
            {
                ParameterInfo[] p = playSound.GetParameters();
                object[] args = new object[p.Length];
                args[0] = type;
                for (int i = 1; i < p.Length; i++) args[i] = p[i].DefaultValue;
                playSound.Invoke(null, args);
            }
            catch (Exception ex) { soundFaulted = true; Log("Pause sounds disabled: " + ex.GetType().Name); }
        }

        static FieldInfo Field(Type type, string name)
        {
            string id = type.FullName + "." + name;
            FieldInfo field;
            lock (fields)
            {
                if (!fields.TryGetValue(id, out field))
                {
                    field = type.GetField(name, Flags);
                    if (field == null) throw new MissingFieldException(id);
                    fields.Add(id, field);
                }
            }
            return field;
        }

        static T Get<T>(Type type, string name) { return (T)Field(type, name).GetValue(null); }
        static T Member<T>(object obj, string name) { return (T)Field(obj.GetType(), name).GetValue(obj); }
        static void Set(Type type, string name, object value) { Field(type, name).SetValue(null, value); }
        static Array Clients { get { return Get<Array>(netplay, "Clients"); } }
        static object Client(int slot) { return Clients.GetValue(slot); }
        static bool Connected(int slot) { return slot >= 0 && slot < 255 && Member<int>(Client(slot), "State") == 10 && Member<bool>(Client(slot), "IsActive") && !Member<bool>(Client(slot), "PendingTermination"); }
        static object Socket(int slot) { return Field(Client(slot).GetType(), "Socket").GetValue(Client(slot)); }
        static bool Compatible(int slot) { return compatible[slot] && Object.ReferenceEquals(peerSockets[slot], Socket(slot)); }
        // The protocol does not carry the host's key, so only the host's own client names it.
        static bool LocalHost { get { return Get<int>(main, "netMode") == 1 && Get<string>(netplay, "HostToken") != null; } }
        static bool IsHost(int slot)
        {
            if (!Connected(slot)) return false;
            // The vanilla host flag is set only after validating the Host & Play token (packet 161).
            return Member<bool>(Get<Array>(main, "player").GetValue(slot), "host");
        }
        static bool HostPresent()
        {
            for (int i = 0; i < 255; i++) if (IsHost(i)) return true;
            return false;
        }
        // Monotonic milliseconds: wall-clock changes (NTP, manual) must not stretch or skip a countdown.
        static long Now
        {
            get
            {
                lock (clockLock)
                {
                    int tick = Environment.TickCount;
                    if (!clockStarted) { clockStarted = true; lastTick = tick; }
                    int delta = unchecked(tick - lastTick);
                    if (delta > 0) monotonic += delta;
                    lastTick = tick;
                    return monotonic;
                }
            }
        }
        static byte SecondsLeft { get { return resumeAt == 0 ? (byte)0 : (byte)Math.Max(1, Math.Min(255, (resumeAt - Now + 999) / 1000)); } }

        public static void OnFrame(object game)
        {
            if (faulted) return;
            try { OnFrameCore(game); }
            catch (Exception ex) { Fault(ex); }
        }

        static void OnFrameCore(object game)
        {
            Init(game.GetType().Assembly);
            int mode = Get<int>(main, "netMode");
            // A dedicated server may already be paused from its console before its first full update.
            if (mode == 2 && previousMode == -1 && Get<bool>(main, "dedServ")) previousMode = mode;
            if (Get<bool>(main, "gameMenu") || mode != previousMode || (mode == 1 && Get<bool>(netplay, "Disconnect")))
            {
                paused = ready = wasDown = false;
                source = countdown = 0;
                resumeAt = 0;
                Visuals.Reset();
                revision = 0;
                nextHello = nextHeartbeat = 0;
                Array.Clear(compatible, 0, compatible.Length);
                Array.Clear(acked, 0, acked.Length);
                Array.Clear(peerSockets, 0, peerSockets.Length);
                Array.Clear(lastRequests, 0, lastRequests.Length);
                previousMode = mode;
                return;
            }
            if (mode == 2) ServerTick();
            else if (mode == 0) CountdownTick();
            else if (mode == 1)
            {
                object connection = Get<object>(netplay, "Connection");
                if (Member<int>(connection, "State") == 10 && Now >= nextHello)
                {
                    nextHello = Now + (ready ? 10000 : 1000);
                    Send(-1, Hello, 0);
                }
            }
        }

        public static void OnIdleServerTick()
        {
            if (faulted || main == null) return;
            try { if (Get<int>(main, "netMode") == 2) ServerTick(); }
            catch (Exception ex) { Fault(ex); }
        }

        static void ServerTick()
        {
            for (int i = 0; i < 255; i++)
            {
                if (!Connected(i) || !Object.ReferenceEquals(peerSockets[i], Socket(i))) compatible[i] = false;
                if (!Connected(i)) { peerSockets[i] = null; lastRequests[i] = 0; }
            }
            // A Host & Play pause ends with its host. A console pause is the server operator's to end.
            if (paused && source == SourceHost && !HostPresent()) ChangeState(false, SourceHost, "host disconnected");
            while (true)
            {
                int command;
                lock (consoleQueue)
                {
                    if (consoleQueue.Count == 0) break;
                    command = consoleQueue.Dequeue();
                }
                RunConsoleCommand(command);
            }
            CountdownTick();
            if (Now >= nextHeartbeat)
            {
                nextHeartbeat = Now + 1000;
                BroadcastState();
            }
        }

        // Runs only where the pause is authoritative: the server, or a single player game.
        static void CountdownTick()
        {
            if (resumeAt == 0) return;
            if (Now >= resumeAt)
            {
                resumeAt = 0;
                countdown = 0;
                ChangeState(false, source, "countdown finished");
                return;
            }
            byte left = SecondsLeft;
            if (left == countdown) return;
            countdown = left;
            if (Get<int>(main, "netMode") == 2) BroadcastState();
            else { Sound(12); Notice("Продолжение через " + left + "…"); }
        }

        // Inserted immediately before UpdateWindyDayState, after vanilla networking and input.
        // Returning skips the entire simulation: players, NPCs, projectiles, items, liquids and events.
        public static bool BeforeSimulation(object game)
        {
            if (faulted) return false;
            try { return BeforeSimulationCore(game); }
            catch (Exception ex) { Fault(ex); return false; }
        }

        static bool BeforeSimulationCore(object game)
        {
            Init(game.GetType().Assembly);
            int mode = Get<int>(main, "netMode");
            if (Get<bool>(main, "gameMenu")) return false;
            if (mode != 2)
            {
                bool down = (bool)keyDown.Invoke(Field(main, "keyState").GetValue(null), new object[] { key });
                bool blocked = Get<bool>(main, "drawingPlayerChat") || Get<bool>(main, "editSign") || Get<bool>(main, "editChest");
                if (down && !wasDown && (!blocked || paused)) KeyPressed(mode);
                wasDown = down;
            }
            if (!paused) return false;
            Set(main, "gamePaused", true);
            // Keep chat fading and the normal pause UI responsive; no player/world update runs.
            if (mode != 2)
            {
                object monitor = Get<object>(main, "chatMonitor");
                monitor.GetType().GetMethod("Update", Flags).Invoke(monitor, null);
            }
            return true;
        }

        // One key does everything: pause; while paused start the resume countdown;
        // during the countdown cancel it and stay paused.
        static void KeyPressed(int mode)
        {
            bool resume = paused && countdown == 0;
            if (mode == 0)
            {
                if (!paused) ChangeState(true, SourceLocal, "single player");
                else if (resume) StartResume("single player");
                else CancelResume("single player");
            }
            else if (!ready) Notice("HostPause: сервер не подтвердил версию " + Version + ". Обновите патч у хоста и всех игроков.");
            else Send(-1, Request, (byte)(resume ? 0 : 1));
        }

        static void StartResume(string reason)
        {
            if (!paused || resumeAt != 0) return;
            if (countdownSeconds == 0) { ChangeState(false, source, reason); return; }
            resumeAt = Now + countdownSeconds * 1000L;
            countdown = SecondsLeft;
            revision++;
            Log("COUNTDOWN " + countdownSeconds + "s revision=" + revision + " reason=" + reason);
            Announce();
        }

        static void CancelResume(string reason)
        {
            if (!paused || resumeAt == 0) return;
            resumeAt = 0;
            countdown = 0;
            revision++;
            Log("COUNTDOWN CANCELLED revision=" + revision + " reason=" + reason);
            Announce();
        }

        // Every connected player must have a matching runtime, and nobody may be mid-join:
        // an unpatched client would keep simulating its own player in a frozen world.
        static int PauseBlocker()
        {
            for (int i = 0; i < 255; i++)
            {
                int state = Member<int>(Client(i), "State");
                if (state > 0 && state < 10) return 2;
                if (Connected(i) && !Compatible(i)) return 2;
            }
            return 0;
        }

        public static bool HandlePacket(object buffer, int start, int length, out int messageType)
        {
            messageType = 0;
            if (faulted) return false;
            try { return HandlePacketCore(buffer, start, length, out messageType); }
            catch (Exception ex) { Fault(ex); return false; }
        }

        static bool HandlePacketCore(object buffer, int start, int length, out int messageType)
        {
            Init(buffer.GetType().Assembly);
            byte[] bytes = Member<byte[]>(buffer, "readBuffer");
            messageType = 0;
            if (start < 0 || length < 1 || start > bytes.Length - length) return false;
            int type = bytes[start];
            int mode = Get<int>(main, "netMode");
            int sender = Member<int>(buffer, "whoAmI");
            // An empty vanilla server stops simulation updates but still handles packets.
            // Release a departed solo host's pause here so the next connection can join.
            if (mode == 2 && paused && source == SourceHost && !HostPresent()) ChangeState(false, SourceHost, "host disconnected before next connection");
            if (type == PacketId)
            {
                messageType = PacketId;
                ResetTimeout(sender, mode);
                if (length != PacketLength - 2 || bytes[start + 1] != 'H' || bytes[start + 2] != 'P' || bytes[start + 3] != 'Z' || bytes[start + 4] != '1' || bytes[start + 5] != Protocol) return true;
                byte op = bytes[start + 6], value = bytes[start + 7], incomingSource = bytes[start + 8], incomingCountdown = bytes[start + 9];
                uint incomingRevision = BitConverter.ToUInt32(bytes, start + 10);
                if (mode == 2 && Connected(sender))
                {
                    if (op == Hello)
                    {
                        if (!compatible[sender]) Log("Negotiated protocol=" + Protocol + " slot=" + sender + " transport=" + Socket(sender).GetType().Name);
                        compatible[sender] = true;
                        peerSockets[sender] = Socket(sender);
                        SendState(sender);
                    }
                    else if (op == Ack)
                    {
                        if (paused && value == 1 && incomingRevision >= pauseRevision) acked[sender] = true;
                    }
                    else if (op == Request)
                    {
                        if (!IsHost(sender)) { Send(sender, Denied, 1); return true; }
                        if (!Compatible(sender)) return true;
                        // The host's patch failed and its client now simulates: release at once.
                        if (value == 2) { if (paused) ChangeState(false, source, "host patch fault"); return true; }
                        if (value > 1 || incomingRevision != revision) { SendState(sender); return true; }
                        if (paused && source == SourceConsole) { Send(sender, Denied, 3); return true; }
                        // A cancel is never rate-limited: dropping it would resume against the host's intent.
                        bool cancel = value == 1 && paused && resumeAt != 0;
                        if (!cancel && Now - lastRequests[sender] < 250) { SendState(sender); return true; }
                        lastRequests[sender] = Now;
                        if (value == 1)
                        {
                            if (paused) CancelResume("host request");
                            else
                            {
                                int blocker = PauseBlocker();
                                if (blocker != 0) { Send(sender, Denied, (byte)blocker); return true; }
                                ChangeState(true, SourceHost, "host request");
                            }
                        }
                        else StartResume("host request");
                    }
                }
                else if (mode == 1 && sender == 256)
                {
                    if (op == State && value <= 1 && (!ready || incomingRevision >= revision)) ClientState(value == 1, incomingSource, incomingCountdown, incomingRevision);
                    else if (op == Denied)
                        Notice(value == 1 ? "Пауза доступна только хосту." : value == 3 ? "Паузу поставил сервер: продолжить можно только командой resume в его консоли." : "HostPause " + Version + " нужен всем игрокам; дождитесь окончания подключения.");
                }
                return true;
            }
            if (!paused) return false;
            // Chat's NetTextModule is ID 1 in 1.4.5.8. No other NetModule is allowed to mutate a frozen world.
            bool chat = type == 82 && length >= 3 && bytes[start + 1] == 1 && bytes[start + 2] == 0;
            // Clients still accept disconnects, player activity and world-info snapshots (console time commands).
            if (chat || (mode == 1 && (type == 2 || type == 7 || type == 14))) return false;
            // Gameplay a client sent before it learned of the pause was already applied on that client;
            // apply it here too, or the world desyncs. TCP order makes its ack the cut-off point.
            if (mode == 2 && Connected(sender) && Compatible(sender) && !acked[sender] && Now - pausedAt < AckWindow) return false;
            if (mode == 2 && !Connected(sender))
                SendDisconnect(sender, "Игра на паузе. Подключитесь после её снятия.");
            messageType = type;
            ResetTimeout(sender, mode);
            return true;
        }

        static void ClientState(bool nowPaused, byte nowSource, byte nowCountdown, uint nowRevision)
        {
            bool first = !ready, pauseChanged = paused != nowPaused;
            byte previousCountdown = countdown;
            paused = nowPaused;
            source = nowSource;
            countdown = nowPaused ? nowCountdown : (byte)0;
            revision = nowRevision;
            ready = true;
            if (pauseChanged || first) Log("Client received " + (paused ? "PAUSED" : "RUNNING") + " source=" + source + " revision=" + revision);
            // From here this client sends no gameplay; the server may drop anything else from it.
            if (paused && (pauseChanged || first)) Send(-1, Ack, 1);
            if (pauseChanged || (first && paused)) { Announce(); return; }
            if (first) { Notice(LocalHost ? "HostPause " + Version + " готов. Ваша клавиша паузы: " + keyName + "." : "HostPause " + Version + " готов. Ставить паузу может хост."); return; }
            if (paused && countdown != previousCountdown)
            {
                if (countdown > 0) { Sound(12); Notice("Продолжение через " + countdown + "…"); }
                else { Sound(10); Notice("Отсчёт отменён. Пауза продолжается."); }
            }
        }

        static void ChangeState(bool value, byte by, string reason)
        {
            if (paused == value) return;
            paused = value;
            source = by;
            resumeAt = 0;
            countdown = 0;
            if (!value) Visuals.Reset();
            revision++;
            if (value) { pauseRevision = revision; pausedAt = Now; Array.Clear(acked, 0, acked.Length); }
            Log((paused ? "PAUSED" : "RESUMED") + " source=" + source + " revision=" + revision + " reason=" + reason);
            Announce();
        }

        // Tell everyone about a state transition: the server broadcasts, a local game shows it.
        static void Announce()
        {
            int mode = Get<int>(main, "netMode");
            if (mode == 2) { BroadcastState(); return; }
            if (paused && countdown > 0) { Sound(12); Notice("Продолжение через " + countdown + "…"); return; }
            Sound(paused ? 10 : 11);
            if (!paused) Notice("Игра продолжена.");
            else if (mode == 0) Notice("ПАУЗА — " + keyName + " для продолжения.");
            else if (source == SourceConsole) Notice("ПАУЗА: её поставил сервер. Продолжение — командой resume в консоли сервера.");
            else Notice(LocalHost ? "ПАУЗА. Продолжить: " + keyName + "." : "ПАУЗА. Продолжить игру может хост.");
        }

        // Text under the pause banner; shown by Visuals every paused frame.
        internal static string Caption
        {
            get
            {
                if (countdown > 0) return "Продолжение через " + countdown + "…";
                if (source == SourceConsole) return "Пауза сервера — resume в консоли";
                if (Get<int>(main, "netMode") == 0 || LocalHost) return keyName + " — продолжить игру";
                return "Пауза. Продолжить может хост";
            }
        }

        // Called on the dedicated server's input thread instead of Console.ReadLine.
        // HostPause commands are consumed here and executed on the main thread.
        public static string ReadConsoleLine()
        {
            // The same method answers the startup prompts (world, password...): only the
            // command loop's thread may treat a line as a HostPause command.
            if (Thread.CurrentThread.Name != "Server Input Thread") return Console.ReadLine();
            InitFromConsole();
            while (true)
            {
                string line = Console.ReadLine();
                if (line == null || faulted) return line;
                int command;
                switch (line.Trim().ToLowerInvariant())
                {
                    case "pause": case "пауза": command = ConsolePause; break;
                    case "resume": case "продолжить": command = ConsoleResume; break;
                    case "hostpause": command = ConsoleStatus; break;
                    default: return line;
                }
                lock (consoleQueue) consoleQueue.Enqueue(command);
                Console.Write(": ");
            }
        }

        // The input thread starts after the world loads, possibly before any player connects.
        // Initialising here installs the idle-server tick so console commands work on an empty server.
        static void InitFromConsole()
        {
            if (main != null || faulted) return;
            try
            {
                Assembly game = Assembly.GetEntryAssembly();
                if (game == null || game.GetType("Terraria.Main") == null)
                {
                    game = null;
                    foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
                        if (candidate.GetType("Terraria.Main") != null) { game = candidate; break; }
                }
                if (game != null) Init(game);
            }
            catch (Exception ex) { Fault(ex); }
        }

        static void RunConsoleCommand(int command)
        {
            if (command == ConsoleStatus)
            {
                int players = 0, patched = 0;
                for (int i = 0; i < 255; i++) if (Connected(i)) { players++; if (Compatible(i)) patched++; }
                Console.WriteLine("HostPause " + Version + ": " + (!paused ? "игра идёт" : resumeAt != 0 ? "продолжение через " + SecondsLeft + " с" : source == SourceConsole ? "пауза (консоль)" : "пауза (хост)") +
                    "; игроков " + players + ", с патчем " + patched + "; отсчёт " + countdownSeconds + " с.");
                Console.WriteLine("Команды: pause (пауза), resume (продолжить), hostpause (состояние).");
                return;
            }
            if (command == ConsolePause)
            {
                if (paused)
                {
                    if (source == SourceConsole && resumeAt == 0) { Console.WriteLine("HostPause: игра уже на паузе."); return; }
                    // Take over the pause: it no longer ends when the host leaves, and only the console resumes it.
                    bool counting = resumeAt != 0;
                    source = SourceConsole;
                    resumeAt = 0;
                    countdown = 0;
                    revision++;
                    Log("PAUSE TAKEN BY CONSOLE revision=" + revision);
                    Announce();
                    Console.WriteLine(counting ? "HostPause: отсчёт отменён, пауза теперь управляется консолью." : "HostPause: пауза теперь управляется консолью.");
                    return;
                }
                if (PauseBlocker() != 0) { Console.WriteLine("HostPause: пауза невозможна — у кого-то нет HostPause " + Version + " или игрок ещё подключается."); return; }
                ChangeState(true, SourceConsole, "console");
                Console.WriteLine("HostPause: пауза. Новые игроки не смогут подключиться до команды resume.");
                return;
            }
            if (!paused) { Console.WriteLine("HostPause: игра не на паузе."); return; }
            if (resumeAt != 0) { Console.WriteLine("HostPause: отсчёт уже идёт."); return; }
            StartResume("console");
            Console.WriteLine(countdownSeconds == 0 ? "HostPause: игра продолжена." : "HostPause: продолжение через " + countdownSeconds + " с.");
        }

        static void ResetTimeout(int sender, int mode)
        {
            object peer = mode == 2 && sender >= 0 && sender < 256 ? Client(sender) : Get<object>(netplay, "Connection");
            Field(peer.GetType(), "TimeOutTimer").SetValue(peer, 0);
        }

        static void BroadcastState()
        {
            for (int i = 0; i < 255; i++)
                if (Connected(i) && Compatible(i)) SendState(i);
        }

        static void SendState(int target) { Send(target, State, (byte)(paused ? 1 : 0)); }

        static void Send(int target, byte op, byte value)
        {
            byte[] packet = new byte[PacketLength];
            packet[0] = PacketLength; packet[2] = PacketId;
            packet[3] = (byte)'H'; packet[4] = (byte)'P'; packet[5] = (byte)'Z'; packet[6] = (byte)'1';
            packet[7] = Protocol; packet[8] = op; packet[9] = value;
            packet[10] = source; packet[11] = paused ? countdown : (byte)0;
            Array.Copy(BitConverter.GetBytes(revision), 0, packet, 12, 4);
            SendRaw(target, packet);
        }

        static void SendDisconnect(int target, string text)
        {
            if (target < 0 || target >= 255) return;
            using (MemoryStream stream = new MemoryStream())
            {
                BinaryWriter writer = new BinaryWriter(stream);
                writer.Write((ushort)0); writer.Write((byte)2);
                writer.Write((byte)0); writer.Write(text); writer.Write((byte)0);
                byte[] packet = stream.ToArray();
                Array.Copy(BitConverter.GetBytes((ushort)packet.Length), packet, 2);
                SendRaw(target, packet);
            }
        }

        static void SendRaw(int target, byte[] packet)
        {
            object peer = target < 0 ? Get<object>(netplay, "Connection") : Client(target);
            FieldInfo socketField = Field(peer.GetType(), "Socket");
            object socket = socketField.GetValue(peer);
            if (socket == null) return;
            // TcpSocket implements ISocket explicitly, so look the method up on the interface.
            MethodInfo send = socketField.FieldType.GetMethod("AsyncSend");
            Type callbackType = send.GetParameters()[3].ParameterType;
            string callbackName = target < 0 ? "ClientWriteCallBack" : "ServerWriteCallBack";
            Delegate callback = Delegate.CreateDelegate(callbackType, peer, callbackName);
            try { send.Invoke(socket, new object[] { packet, 0, packet.Length, callback, null }); }
            catch (TargetInvocationException ex) { Log("Socket send failed: " + ex.InnerException.GetType().Name); }
        }

        static void Notice(string text)
        {
            if (Get<bool>(main, "dedServ")) return;
            if (Now < nextNotice) return;
            nextNotice = Now + 300;
            MethodInfo method = main.GetMethod("NewText", Flags, null, new Type[] { typeof(string), typeof(byte), typeof(byte), typeof(byte) }, null);
            method.Invoke(null, new object[] { text, (byte)255, (byte)215, (byte)90 });
        }

        public static bool SuppressF9()
        {
            return !faulted && main != null && !Get<bool>(main, "gameMenu") && String.Equals(keyName, "F9", StringComparison.OrdinalIgnoreCase);
        }

        public static bool DrawFrozenFrame()
        {
            if (faulted || visualFaulted || !paused || main == null || Get<bool>(main, "dedServ") || Get<bool>(main, "gameMenu")) return false;
            try { return Visuals.DrawCached(Caption, countdown > 0); }
            catch (Exception ex) { VisualFault(ex); return false; }
        }

        public static void AfterDraw()
        {
            if (faulted || visualFaulted || main == null || Get<bool>(main, "dedServ")) return;
            try
            {
                if (paused && !Get<bool>(main, "gameMenu")) Visuals.CaptureAndDraw(Caption, countdown > 0);
                else Visuals.Reset();
            }
            catch (Exception ex) { VisualFault(ex); }
        }

        static void Log(string text)
        {
            Console.WriteLine("[HostPause] " + text);
            try
            {
                if (main == null) return;
                string path = Path.Combine(Path.GetDirectoryName(main.Assembly.Location), "HostPause.log");
                lock (logLock)
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    {
                        if (File.Exists(path + ".1")) File.Delete(path + ".1");
                        File.Move(path, path + ".1");
                    }
                    File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + text + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
