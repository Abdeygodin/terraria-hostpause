using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;

namespace HostPause
{
    // Deliberately depends only on mscorlib. FNA and XNA clients use the same protocol.
    public static class Runtime
    {
        const byte PacketId = 254, Protocol = 2, Hello = 1, State = 2, Request = 3, Denied = 4;
        const int PacketLength = 14;
        static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        static Type main, netplay, message;
        static readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
        static object[] peerSockets = new object[256];
        static bool[] compatible = new bool[256];
        static long[] lastRequests = new long[256];
        static bool paused, ready, wasDown, announced, faulted, visualFaulted;
        static readonly object logLock = new object();
        static int previousMode = -1;
        static uint revision;
        static long nextHello, nextHeartbeat, nextNotice;
        static string keyName = "F9";
        static object key;
        static MethodInfo keyDown;

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
            try { if (paused && ready && main != null && Get<int>(main, "netMode") == 1) Send(-1, Request, 0); }
            catch { }
            faulted = true;
            paused = false;
            revision++;
            Log("Disabled after error: " + ex);
            try { if (main != null && Get<int>(main, "netMode") == 2) BroadcastState(); }
            catch { }
        }

        static void Init(Assembly assembly)
        {
            if (main != null) return;
            main = assembly.GetType("Terraria.Main", true);
            netplay = assembly.GetType("Terraria.Netplay", true);
            message = assembly.GetType("Terraria.NetMessage", true);
            if (!Get<bool>(main, "dedServ"))
            {
                Type keyboard = Field(main, "keyState").FieldType;
                keyDown = keyboard.GetMethod("IsKeyDown");
                Type keys = keyDown.GetParameters()[0].ParameterType;
                string config = Path.Combine(Path.GetDirectoryName(assembly.Location), "HostPause.key.txt");
                if (File.Exists(config)) keyName = File.ReadAllText(config).Trim();
                try { key = Enum.Parse(keys, keyName, true); }
                catch { keyName = "F9"; key = Enum.Parse(keys, keyName); }
                try { Visuals.Init(main); }
                catch (Exception ex) { VisualFault(ex); }
            }
            Log("HostPause 0.1.2 loaded; protocol=" + Protocol + "; key=" + keyName);
        }

        static FieldInfo Field(Type type, string name)
        {
            string id = type.FullName + "." + name;
            FieldInfo field;
            if (!fields.TryGetValue(id, out field))
            {
                field = type.GetField(name, Flags);
                if (field == null) throw new MissingFieldException(id);
                fields.Add(id, field);
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
        static bool IsHost(int slot)
        {
            if (!Connected(slot)) return false;
            // The vanilla host flag is set only after validating the Host & Play token (packet 161).
            return Member<bool>(Get<Array>(main, "player").GetValue(slot), "host");
        }
        static long Now { get { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; } }

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
            if (Get<bool>(main, "gameMenu") || mode != previousMode || (mode == 1 && Get<bool>(netplay, "Disconnect")))
            {
                paused = ready = wasDown = announced = false;
                Visuals.Reset();
                revision = 0;
                nextHello = nextHeartbeat = 0;
                Array.Clear(compatible, 0, compatible.Length);
                Array.Clear(peerSockets, 0, peerSockets.Length);
                Array.Clear(lastRequests, 0, lastRequests.Length);
                previousMode = mode;
                return;
            }
            if (mode == 2)
            {
                bool hostPresent = false;
                for (int i = 0; i < 255; i++)
                {
                    if (!Connected(i) || !Object.ReferenceEquals(peerSockets[i], Socket(i))) compatible[i] = false;
                    if (!Connected(i)) { peerSockets[i] = null; lastRequests[i] = 0; }
                    if (IsHost(i)) hostPresent = true;
                }
                if (paused && !hostPresent) ChangeState(false, "host disconnected");
                if (Now >= nextHeartbeat)
                {
                    nextHeartbeat = Now + 1000;
                    BroadcastState();
                }
            }
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
                if (down && !wasDown && (!blocked || paused))
                {
                    if (mode == 0) ChangeState(!paused, "single player");
                    else if (!ready) Notice("HostPause: сервер не подтвердил версию 0.1.2. Обновите патч у хоста и всех игроков.");
                    else Send(-1, Request, (byte)(paused ? 0 : 1));
                }
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
            if (mode == 2 && paused)
            {
                bool hostPresent = false;
                for (int i = 0; i < 255; i++) if (IsHost(i)) { hostPresent = true; break; }
                if (!hostPresent) ChangeState(false, "host disconnected before next connection");
            }
            if (type == PacketId)
            {
                messageType = PacketId;
                ResetTimeout(sender, mode);
                if (length != PacketLength - 2 || bytes[start + 1] != 'H' || bytes[start + 2] != 'P' || bytes[start + 3] != 'Z' || bytes[start + 4] != '1' || bytes[start + 5] != Protocol) return true;
                byte op = bytes[start + 6], value = bytes[start + 7];
                uint incomingRevision = BitConverter.ToUInt32(bytes, start + 8);
                if (mode == 2 && Connected(sender))
                {
                    if (op == Hello)
                    {
                        if (!compatible[sender]) Log("Negotiated protocol=" + Protocol + " slot=" + sender + " transport=" + Socket(sender).GetType().Name);
                        compatible[sender] = true;
                        peerSockets[sender] = Socket(sender);
                        Send(sender, State, (byte)(paused ? 1 : 0));
                    }
                    else if (op == Request)
                    {
                        if (!IsHost(sender)) { Send(sender, Denied, 1); return true; }
                        if (!compatible[sender] || !Object.ReferenceEquals(peerSockets[sender], Socket(sender))) return true;
                        if (value > 1 || incomingRevision != revision) { Send(sender, State, (byte)(paused ? 1 : 0)); return true; }
                        if (Now - lastRequests[sender] < 250) return true;
                        lastRequests[sender] = Now;
                        if (value == 1)
                        {
                            for (int i = 0; i < 255; i++)
                            {
                                // Loading clients must finish their handshake first, or try joining after resume.
                                int state = Member<int>(Client(i), "State");
                                if ((state > 0 && state < 10) || (Connected(i) && (!compatible[i] || !Object.ReferenceEquals(peerSockets[i], Socket(i)))))
                                { Send(sender, Denied, 2); return true; }
                            }
                        }
                        ChangeState(value == 1, "host request");
                    }
                }
                else if (mode == 1 && sender == 256)
                {
                    if (op == State && value <= 1 && (!ready || incomingRevision >= revision))
                    {
                        bool changed = !ready || paused != (value == 1);
                        paused = value == 1;
                        revision = incomingRevision;
                        ready = true;
                        if (changed)
                        {
                            Log("Client received " + (paused ? "PAUSED" : "RUNNING") + " revision=" + revision);
                            if (paused) Notice("ПАУЗА. Хост может продолжить игру клавишей " + keyName + ".");
                            else if (!announced) Notice("HostPause 0.1.2 готов. Клавиша паузы у хоста: " + keyName + ".");
                            else Notice("Игра продолжена.");
                            announced = true;
                        }
                    }
                    else if (op == Denied)
                        Notice(value == 1 ? "Пауза доступна только хосту." : "HostPause 0.1.2 нужен всем игрокам; дождитесь окончания подключения.");
                }
                return true;
            }
            if (!paused) return false;
            // Chat's NetTextModule is ID 1 in 1.4.5.8. No other NetModule is allowed to mutate a frozen world.
            bool chat = type == 82 && length >= 3 && bytes[start + 1] == 1 && bytes[start + 2] == 0;
            if (chat || (mode == 1 && (type == 2 || type == 14))) return false;
            if (mode == 2 && !Connected(sender))
                SendDisconnect(sender, "Игра на паузе. Подключитесь после её снятия.");
            messageType = type;
            ResetTimeout(sender, mode);
            return true;
        }

        static void ResetTimeout(int sender, int mode)
        {
            object peer = mode == 2 && sender >= 0 && sender < 256 ? Client(sender) : Get<object>(netplay, "Connection");
            Field(peer.GetType(), "TimeOutTimer").SetValue(peer, 0);
        }

        static void ChangeState(bool value, string reason)
        {
            if (paused == value) return;
            paused = value;
            if (!value) Visuals.Reset();
            revision++;
            Log((paused ? "PAUSED" : "RESUMED") + " revision=" + revision + " reason=" + reason);
            if (Get<int>(main, "netMode") == 2) BroadcastState();
            else if (Get<int>(main, "netMode") == 0) Notice(paused ? "ПАУЗА — " + keyName + " для продолжения." : "Игра продолжена.");
        }

        static void BroadcastState()
        {
            for (int i = 0; i < 255; i++)
                if (Connected(i) && compatible[i] && Object.ReferenceEquals(peerSockets[i], Socket(i))) Send(i, State, (byte)(paused ? 1 : 0));
        }

        static void Send(int target, byte op, byte value)
        {
            byte[] packet = new byte[PacketLength];
            packet[0] = PacketLength; packet[2] = PacketId;
            packet[3] = (byte)'H'; packet[4] = (byte)'P'; packet[5] = (byte)'Z'; packet[6] = (byte)'1';
            packet[7] = Protocol; packet[8] = op; packet[9] = value;
            Array.Copy(BitConverter.GetBytes(revision), 0, packet, 10, 4);
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
            try { return Visuals.DrawCached(keyName); }
            catch (Exception ex) { VisualFault(ex); return false; }
        }

        public static void AfterDraw()
        {
            if (faulted || visualFaulted || main == null || Get<bool>(main, "dedServ")) return;
            try
            {
                if (paused && !Get<bool>(main, "gameMenu")) Visuals.CaptureAndDraw(keyName);
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
