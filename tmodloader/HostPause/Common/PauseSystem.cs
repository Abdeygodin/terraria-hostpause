using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.NetModules;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.Net;

namespace HostPause.Common
{
    public enum PauseSource : byte { Local = 0, Player = 1, Console = 2 }

    public enum PauseCommandKind { Pause, Resume, Status }

    // The server owns the pause; clients mirror it. In single player the local game owns it.
    // The design follows the vanilla HostPause 0.2.0 runtime (source/Runtime.cs) on tModLoader's APIs.
    public class PauseSystem : ModSystem
    {
        enum Op : byte { Hello = 1, State = 2, Request = 3, Denied = 4, Ack = 5 }

        // Request values: resume (countdown), pause or cancel the countdown, release now (client fault).
        const byte RequestResume = 0, RequestPause = 1, RequestRelease = 2;
        const byte DeniedNotAllowed = 1, DeniedJoining = 2, DeniedConsole = 3;
        const int AckWindow = 2000, RequestInterval = 250, NoticeInterval = 300;

        // Kick text for a connection attempt while paused. The joining client may not have
        // HostPause loaded yet, so this is a literal in both languages, not a localization key.
        const string JoinRefused = "Игра на паузе. Подключитесь после её снятия. / The game is paused. Join again after it resumes.";

        internal static ModKeybind PauseKey;
        static Mod mod;
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static long Now => clock.ElapsedMilliseconds;

        internal static bool Paused { get; private set; }
        internal static PauseSource Source { get; private set; }
        internal static byte Countdown { get; private set; }
        static uint revision, pauseRevision;
        static long resumeAt, pausedAt, nextHeartbeat, nextNotice, nextHello;
        static bool ready, canControl, faulted;
        static string lastNotice;
        static readonly bool[] acked = new bool[256], known = new bool[256];
        static readonly long[] lastRequests = new long[256];
        // Console commands arrive on the server input thread and run on the main thread.
        static readonly ConcurrentQueue<PauseCommandKind> consoleQueue = new();

        public override void Load()
        {
            mod = Mod;
            PauseKey = KeybindLoader.RegisterKeybind(Mod, "Pause", "F9");
            IL_Main.DoUpdate += InjectSimulationGate;
            // Raised every tick by the dedicated server loop, even with no players connected,
            // when Main.Update (and so DoUpdate) does not run at all.
            Main.OnTickForThirdPartySoftwareOnly += ServerTick;
            if (!Main.dedServ)
            {
                On_Main.DoDraw += DrawHook;
                On_Main.DoUpdate_F9_ToggleLighting += SuppressF9;
            }
        }

        public override void Unload()
        {
            Main.OnTickForThirdPartySoftwareOnly -= ServerTick;
            ResetState();
            faulted = false;
            if (!Main.dedServ) Main.QueueMainThreadAction(PauseVisuals.Unload);
            PauseKey = null;
            mod = null;
        }

        public override void ClearWorld() => ResetState();
        public override void OnWorldUnload() => ResetState();

        static void ResetState()
        {
            Paused = ready = canControl = false;
            Source = PauseSource.Local;
            Countdown = 0;
            revision = pauseRevision = 0;
            resumeAt = pausedAt = nextHeartbeat = nextHello = 0;
            Array.Clear(acked);
            Array.Clear(known);
            Array.Clear(lastRequests);
            consoleQueue.Clear();
            if (!Main.dedServ) PauseVisuals.Reset();
        }

        // Inserts "if (BeforeSimulation()) return;" in Main.DoUpdate right before
        // UpdateWindyDayState: after networking, input, chat and menus, before any simulation.
        // Skipping the rest freezes players, NPCs, projectiles, items, liquids, time, events,
        // and every mod hook that runs inside the world update.
        void InjectSimulationGate(ILContext il)
        {
            var c = new ILCursor(il);
            if (!c.TryGotoNext(MoveType.Before, i => i.MatchLdarg0(), i => i.MatchCall<Main>("UpdateWindyDayState")))
                throw new ILPatchFailureException(Mod, il, new Exception("Simulation boundary (UpdateWindyDayState) not found in Main.DoUpdate"));
            c.MoveAfterLabels();
            c.EmitDelegate(BeforeSimulation);
            ILLabel run = c.DefineLabel();
            c.Emit(OpCodes.Brfalse, run);
            c.Emit(OpCodes.Ret);
            c.MarkLabel(run);
        }

        static bool BeforeSimulation()
        {
            if (faulted || Main.gameMenu) return false;
            try
            {
                if (Main.netMode == NetmodeID.MultiplayerClient && !ready && Now >= nextHello)
                {
                    // The server also pushes the state when the join completes; this covers a lost reply.
                    nextHello = Now + 1000;
                    SendHello();
                }
                if (Main.netMode != NetmodeID.Server)
                {
                    ClientInput();
                    if (Main.netMode == NetmodeID.SinglePlayer) CountdownTick();
                }
                if (!Paused) return false;
                Main.gamePaused = true;
                // Keep chat fading normally; nothing else is updated.
                if (!Main.dedServ) Main.chatMonitor.Update();
                return true;
            }
            catch (Exception ex) { Fault(ex); return false; }
        }

        // ---------- Client and single player ----------

        static void ClientInput()
        {
            if (!KeyJustPressed()) return;
            bool typing = Main.drawingPlayerChat || Main.editSign || Main.editChest || Main.blockInput;
            // Starting a pause is blocked while typing; resuming or cancelling is not,
            // because the chat box is not drawn on the frozen frame.
            if (typing && !Paused) return;
            LocalRequest(Paused && Countdown == 0 ? RequestResume : RequestPause);
        }

        static void LocalRequest(byte value)
        {
            if (Main.netMode == NetmodeID.SinglePlayer)
            {
                if (value == RequestPause)
                {
                    if (!Paused) ChangeState(true, PauseSource.Local, "single player");
                    else CancelResume("single player");
                }
                else StartResume("single player");
                return;
            }
            if (!ready) { Notice(T("NotReady")); return; }
            ModPacket packet = Packet(Op.Request);
            packet.Write(value);
            packet.Write(revision);
            packet.Send();
        }

        internal static void SendHello()
        {
            if (Main.netMode != NetmodeID.MultiplayerClient || faulted) return;
            Packet(Op.Hello).Send();
        }

        static void ClientState(bool nowPaused, PauseSource nowSource, byte nowCountdown, uint nowRevision, bool nowControl)
        {
            bool first = !ready, pauseChanged = Paused != nowPaused, sourceChanged = Source != nowSource;
            byte previousCountdown = Countdown;
            Paused = nowPaused;
            Source = nowSource;
            Countdown = nowPaused ? nowCountdown : (byte)0;
            revision = nowRevision;
            canControl = nowControl;
            ready = true;
            if (!Paused) PauseVisuals.Reset();
            if (pauseChanged || first) Log("Client received " + (Paused ? "PAUSED" : "RUNNING") + " source=" + Source + " revision=" + revision);
            // From here this client sends no gameplay; the server may drop anything else it sends.
            if (Paused && (pauseChanged || first))
            {
                ModPacket ack = Packet(Op.Ack);
                ack.Write(revision);
                ack.Send();
            }
            // A new pause, or the console taking over a host's pause, is announced in full.
            if (pauseChanged || (first && Paused) || (Paused && sourceChanged)) { Announce(); return; }
            if (first) { Notice(!canControl ? T("ReadyGuest") : KeyBound ? T("ReadyController", KeyName) : T("ReadyUnbound")); return; }
            if (Paused && Countdown != previousCountdown)
            {
                if (Countdown > 0) { Sound(SoundID.MenuTick); Notice(T("Countdown", Countdown)); }
                else { Sound(SoundID.MenuOpen); Notice(T("CountdownCancelled")); }
            }
        }

        // tModLoader leaves mod keys unbound until the player assigns one (or resets controls to
        // default, which gives F9). Until then the chat commands do the same job.
        // Read the trigger tables defensively: during a mod reload they may not contain the key yet,
        // and ModKeybind's own accessors would throw.
        static string KeyId => mod?.Name + "/Pause";
        static bool KeyJustPressed() =>
            PauseKey != null && PlayerInput.Triggers.JustPressed.KeyStatus.TryGetValue(KeyId, out bool pressed) && pressed;
        static string AssignedKey =>
            PauseKey != null && PlayerInput.CurrentProfile?.InputModes[InputMode.Keyboard].KeyStatus.TryGetValue(KeyId, out var keys) == true
                ? keys.FirstOrDefault() : null;
        static bool KeyBound => AssignedKey != null;
        static string KeyName => AssignedKey ?? (Paused ? "/resume" : "/pause");

        // Text under the pause banner.
        internal static string Caption
        {
            get
            {
                if (Countdown > 0) return T("CaptionCountdown", Countdown);
                if (Source == PauseSource.Console) return T("CaptionConsole");
                if (Main.netMode == NetmodeID.SinglePlayer || canControl) return T("CaptionController", KeyName);
                return T("CaptionGuest");
            }
        }

        static void SuppressF9(On_Main.orig_DoUpdate_F9_ToggleLighting orig)
        {
            // Vanilla F9 cycles the lighting mode; do not do that when F9 is the pause key.
            if (!Main.gameMenu && !faulted && AssignedKey == "F9") return;
            orig();
        }

        static void DrawHook(On_Main.orig_DoDraw orig, Main self, GameTime gameTime)
        {
            if (Paused && !faulted && !Main.gameMenu && PauseVisuals.DrawCached(Caption, Countdown > 0)) return;
            orig(self, gameTime);
            if (Paused && !faulted && !Main.gameMenu) PauseVisuals.CaptureAndDraw(Caption, Countdown > 0);
            else PauseVisuals.Reset();
        }

        // ---------- Server ----------

        static bool Connected(int slot)
        {
            if (slot < 0 || slot >= 255) return false;
            RemoteClient client = Netplay.Clients[slot];
            return client.IsActive && client.State == 10 && !client.PendingTermination;
        }

        // Host: tModLoader's own rule (NetMessage.DoesPlayerSlotCountAsAHost), the player who
        // connects from the server machine itself, which is the Host & Play host.
        static bool CanControl(int slot) =>
            Connected(slot) && (PauseConfig.Instance.AnyPlayerCanPause || NetMessage.DoesPlayerSlotCountAsAHost(slot));

        static bool ControllerPresent()
        {
            for (int i = 0; i < 255; i++) if (CanControl(i)) return true;
            return false;
        }

        // A player mid-join would arrive in a frozen world without the pause state.
        static bool SomeoneJoining()
        {
            for (int i = 0; i < 255; i++)
            {
                RemoteClient client = Netplay.Clients[i];
                if (client.IsActive && client.State != 0 && client.State < 10 && !client.PendingTermination) return true;
            }
            return false;
        }

        static void ServerTick()
        {
            if (Main.netMode != NetmodeID.Server || faulted) return;
            try
            {
                for (int i = 0; i < 255; i++)
                {
                    bool connected = Connected(i);
                    if (!connected) { acked[i] = false; lastRequests[i] = 0; }
                    // A player who just finished joining gets the state (and whether they may pause) at once.
                    else if (!known[i]) SendState(i);
                    known[i] = connected;
                }
                // A player's pause ends when nobody who may resume it is online.
                // A console pause belongs to the server operator.
                if (Paused && Source == PauseSource.Player && !ControllerPresent()) ChangeState(false, PauseSource.Player, "host left");
                while (consoleQueue.TryDequeue(out PauseCommandKind command)) RunConsoleCommand(command);
                CountdownTick();
                if (Paused && Now >= nextHeartbeat)
                {
                    nextHeartbeat = Now + 1000;
                    BroadcastState();
                }
            }
            catch (Exception ex) { Fault(ex); }
        }

        public override bool HijackGetData(ref byte messageType, ref BinaryReader reader, int playerNumber)
        {
            if (Main.netMode != NetmodeID.Server || !Paused || faulted) return false;
            try
            {
                if (!Connected(playerNumber))
                {
                    // New joins are refused while paused; reconnect after resuming.
                    if (playerNumber >= 0 && playerNumber < 255 && !Netplay.Clients[playerNumber].PendingTermination)
                    {
                        Log("Refused connection from slot " + playerNumber + " while paused");
                        NetMessage.BootPlayer(playerNumber, NetworkText.FromLiteral(JoinRefused));
                    }
                    return true;
                }
                // tModLoader's own messages and mod packets (HostPause included) pass.
                if (messageType >= MessageID.InGameChangeConfig) return false;
                if (messageType == MessageID.NetModules && IsChat(reader)) return false;
                // Gameplay a client sent before it learned of the pause was already applied on
                // that client; apply it here too or the world desyncs. TCP order makes the ack the cut-off.
                if (!acked[playerNumber] && Now - pausedAt < AckWindow) return false;
                return true;
            }
            catch (Exception ex) { Fault(ex); return false; }
        }

        static bool IsChat(BinaryReader reader)
        {
            long position = reader.BaseStream.Position;
            ushort module = reader.ReadUInt16();
            reader.BaseStream.Position = position;
            return module == NetManager.Instance.GetId<NetTextModule>();
        }

        static void HandleRequest(int slot, byte value, uint requestRevision, bool command)
        {
            if (!CanControl(slot)) { Deny(slot, DeniedNotAllowed); return; }
            // The requester's client failed and now simulates on its own: release at once.
            if (value == RequestRelease) { if (Paused) ChangeState(false, Source, "client fault"); return; }
            // A request made against an outdated state (someone else acted first) is answered with the state.
            if (value > RequestRelease || (!command && requestRevision != revision)) { SendState(slot); return; }
            if (Paused && Source == PauseSource.Console) { Deny(slot, DeniedConsole); return; }
            // A cancel is never rate-limited: dropping it would resume against the host's intent.
            bool cancel = value == RequestPause && Paused && resumeAt != 0;
            if (!cancel && Now - lastRequests[slot] < RequestInterval) { SendState(slot); return; }
            lastRequests[slot] = Now;
            string who = Main.player[slot].name + " (slot " + slot + ")";
            if (value == RequestPause)
            {
                if (Paused) CancelResume(who);
                else if (SomeoneJoining()) Deny(slot, DeniedJoining);
                else ChangeState(true, PauseSource.Player, who);
            }
            else StartResume(who);
        }

        static void Deny(int slot, byte reason)
        {
            ModPacket packet = Packet(Op.Denied);
            packet.Write(reason);
            packet.Send(slot);
        }

        static void BroadcastState()
        {
            for (int i = 0; i < 255; i++) if (Connected(i)) SendState(i);
        }

        static void SendState(int slot)
        {
            ModPacket packet = Packet(Op.State);
            packet.Write(Paused);
            packet.Write((byte)Source);
            packet.Write(Paused ? Countdown : (byte)0);
            packet.Write(revision);
            packet.Write(CanControl(slot));
            packet.Send(slot);
        }

        internal static void Receive(BinaryReader reader, int sender)
        {
            if (faulted) return;
            try
            {
                Op op = (Op)reader.ReadByte();
                if (Main.netMode == NetmodeID.Server)
                {
                    if (!Connected(sender)) return;
                    switch (op)
                    {
                        case Op.Hello:
                            SendState(sender);
                            break;
                        case Op.Ack:
                            uint ackRevision = reader.ReadUInt32();
                            if (Paused && ackRevision >= pauseRevision) acked[sender] = true;
                            break;
                        case Op.Request:
                            byte value = reader.ReadByte();
                            uint requestRevision = reader.ReadUInt32();
                            HandleRequest(sender, value, requestRevision, false);
                            break;
                    }
                }
                else if (Main.netMode == NetmodeID.MultiplayerClient)
                {
                    switch (op)
                    {
                        case Op.State:
                            bool nowPaused = reader.ReadBoolean();
                            PauseSource nowSource = (PauseSource)reader.ReadByte();
                            byte nowCountdown = reader.ReadByte();
                            uint nowRevision = reader.ReadUInt32();
                            bool nowControl = reader.ReadBoolean();
                            if (!ready || nowRevision >= revision) ClientState(nowPaused, nowSource, nowCountdown, nowRevision, nowControl);
                            break;
                        case Op.Denied:
                            byte reason = reader.ReadByte();
                            Notice(reason == DeniedNotAllowed ? T("DeniedNotAllowed") : reason == DeniedConsole ? T("DeniedConsole") : T("DeniedJoining"));
                            break;
                    }
                }
            }
            catch (Exception ex) { Fault(ex); }
        }

        // ---------- Shared state machine (server, or the single player game) ----------

        static byte SecondsLeft => resumeAt == 0 ? (byte)0 : (byte)Math.Clamp((resumeAt - Now + 999) / 1000, 1, 255);

        static void CountdownTick()
        {
            if (resumeAt == 0) return;
            if (Now >= resumeAt)
            {
                resumeAt = 0;
                Countdown = 0;
                ChangeState(false, Source, "countdown finished");
                return;
            }
            byte left = SecondsLeft;
            if (left == Countdown) return;
            Countdown = left;
            if (Main.netMode == NetmodeID.Server) BroadcastState();
            else { Sound(SoundID.MenuTick); Notice(T("Countdown", left)); }
        }

        static void StartResume(string reason)
        {
            if (!Paused || resumeAt != 0) return;
            int seconds = PauseConfig.Instance.ResumeCountdown;
            if (seconds <= 0) { ChangeState(false, Source, reason); return; }
            resumeAt = Now + seconds * 1000L;
            Countdown = SecondsLeft;
            revision++;
            Log("COUNTDOWN " + seconds + "s revision=" + revision + " by " + reason);
            Announce();
        }

        static void CancelResume(string reason)
        {
            if (!Paused || resumeAt == 0) return;
            resumeAt = 0;
            Countdown = 0;
            revision++;
            Log("COUNTDOWN CANCELLED revision=" + revision + " by " + reason);
            if (Main.netMode == NetmodeID.Server) BroadcastState();
            else { Sound(SoundID.MenuOpen); Notice(T("CountdownCancelled")); }
        }

        static void ChangeState(bool value, PauseSource by, string reason)
        {
            if (Paused == value) return;
            Paused = value;
            Source = by;
            resumeAt = 0;
            Countdown = 0;
            revision++;
            if (value) { pauseRevision = revision; pausedAt = Now; Array.Clear(acked); }
            else if (!Main.dedServ) PauseVisuals.Reset();
            Log((Paused ? "PAUSED" : "RESUMED") + " source=" + Source + " revision=" + revision + " by " + reason);
            Announce();
        }

        // The server tells everyone; a client or single player game shows it.
        static void Announce()
        {
            if (Main.netMode == NetmodeID.Server) { BroadcastState(); return; }
            if (Paused && Countdown > 0) { Sound(SoundID.MenuTick); Notice(T("Countdown", Countdown)); return; }
            Sound(Paused ? SoundID.MenuOpen : SoundID.MenuClose);
            if (!Paused) Notice(T("Resumed"));
            else if (Main.netMode == NetmodeID.SinglePlayer) Notice(T("PausedSingle", KeyName));
            else if (Source == PauseSource.Console) Notice(T("PausedConsole"));
            else Notice(canControl ? T("PausedController", KeyName) : T("PausedGuest"));
        }

        // ---------- Commands ----------

        internal static void Command(CommandCaller caller, PauseCommandKind kind)
        {
            if (caller.CommandType == CommandType.Console) { consoleQueue.Enqueue(kind); return; }
            if (kind == PauseCommandKind.Status) { caller.Reply(StatusText(), new Color(255, 215, 90)); return; }
            if (Main.netMode == NetmodeID.SinglePlayer)
            {
                if (kind == PauseCommandKind.Pause && Paused && resumeAt == 0) caller.Reply(T("AlreadyPaused"));
                else if (kind == PauseCommandKind.Resume && (!Paused || resumeAt != 0)) caller.Reply(Paused ? T("CountdownRunning") : T("NotPaused"));
                else LocalRequest(kind == PauseCommandKind.Pause ? RequestPause : RequestResume);
                return;
            }
            if (Main.netMode != NetmodeID.Server || caller.Player == null) return;
            int slot = caller.Player.whoAmI;
            if (kind == PauseCommandKind.Pause && Paused && resumeAt == 0 && CanControl(slot)) { caller.Reply(T("AlreadyPaused")); return; }
            if (kind == PauseCommandKind.Resume && (!Paused || resumeAt != 0) && CanControl(slot)) { caller.Reply(Paused ? T("CountdownRunning") : T("NotPaused")); return; }
            HandleRequest(slot, kind == PauseCommandKind.Pause ? RequestPause : RequestResume, revision, true);
        }

        static string StatusText()
        {
            int players = 0;
            if (Main.netMode == NetmodeID.Server) for (int i = 0; i < 255; i++) if (Connected(i)) players++;
            string state = !Paused ? T("StatusRunning") : resumeAt != 0 ? T("StatusCountdown", SecondsLeft)
                : Source == PauseSource.Console ? T("StatusConsole") : T("StatusPlayer");
            return T("Status", state, players, PauseConfig.Instance.ResumeCountdown);
        }

        static void RunConsoleCommand(PauseCommandKind command)
        {
            if (command == PauseCommandKind.Status)
            {
                Console.WriteLine("HostPause: " + StatusText());
                Console.WriteLine(T("ConsoleHelp"));
                return;
            }
            if (command == PauseCommandKind.Pause)
            {
                if (Paused)
                {
                    if (Source == PauseSource.Console && resumeAt == 0) { Console.WriteLine("HostPause: " + T("AlreadyPaused")); return; }
                    // Take the pause over: it no longer ends when the host leaves, and only the console resumes it.
                    bool counting = resumeAt != 0;
                    Source = PauseSource.Console;
                    resumeAt = 0;
                    Countdown = 0;
                    revision++;
                    Log("PAUSE TAKEN BY CONSOLE revision=" + revision);
                    Announce();
                    Console.WriteLine("HostPause: " + (counting ? T("ConsoleTakeOverCountdown") : T("ConsoleTakeOver")));
                    return;
                }
                if (SomeoneJoining()) { Console.WriteLine("HostPause: " + T("ConsoleJoining")); return; }
                ChangeState(true, PauseSource.Console, "console");
                Console.WriteLine("HostPause: " + T("ConsolePaused"));
                return;
            }
            if (!Paused) { Console.WriteLine("HostPause: " + T("NotPaused")); return; }
            if (resumeAt != 0) { Console.WriteLine("HostPause: " + T("CountdownRunning")); return; }
            StartResume("console");
            Console.WriteLine("HostPause: " + (Paused ? T("ConsoleResuming", PauseConfig.Instance.ResumeCountdown) : T("Resumed")));
        }

        // ---------- Helpers ----------

        static ModPacket Packet(Op op)
        {
            ModPacket packet = mod.GetPacket();
            packet.Write((byte)op);
            return packet;
        }

        // A broken mod must never crash the game or leave a server stuck on pause.
        static void Fault(Exception ex)
        {
            if (faulted) return;
            try
            {
                // A client that can resume releases the server's pause before it starts simulating alone.
                if (Paused && ready && canControl && Main.netMode == NetmodeID.MultiplayerClient)
                {
                    ModPacket packet = Packet(Op.Request);
                    packet.Write(RequestRelease);
                    packet.Write(revision);
                    packet.Send();
                }
            }
            catch { }
            faulted = true;
            Paused = false;
            resumeAt = 0;
            Countdown = 0;
            revision++;
            mod?.Logger.Error("HostPause disabled after an error; the game is no longer paused.", ex);
            try { if (Main.netMode == NetmodeID.Server) BroadcastState(); } catch { }
            try { if (!Main.dedServ) Main.NewText(T("Faulted"), 255, 120, 90); } catch { }
        }

        internal static void VisualFault(Exception ex)
        {
            mod?.Logger.Warn("Pause visuals disabled; the pause itself keeps working.", ex);
            Notice(T("VisualFault"));
        }

        static void Notice(string text)
        {
            // Only an identical message repeated within a moment is dropped (key bounce, heartbeat).
            if (Main.dedServ || (text == lastNotice && Now < nextNotice)) return;
            lastNotice = text;
            nextNotice = Now + NoticeInterval;
            Main.NewText(text, 255, 215, 90);
        }

        static void Sound(Terraria.Audio.SoundStyle style)
        {
            if (!Main.dedServ) SoundEngine.PlaySound(style);
        }

        static void Log(string text)
        {
            mod?.Logger.Info(text);
            if (Main.dedServ) Console.WriteLine("[HostPause] " + text);
        }

        static string T(string key, params object[] args) => Language.GetTextValue("Mods.HostPause.Messages." + key, args);
    }

    public class PausePlayer : ModPlayer
    {
        public override void OnEnterWorld() => PauseSystem.SendHello();
    }
}
