using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.Chat;
using Terraria.GameContent.UI.Chat;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.IO;
using Terraria.ModLoader;

namespace HostPauseTestBot
{
    // Test-only automation for HostPause on a local tModLoader server. Never publish this mod.
    // Driven by environment variables and a command file that the test script appends lines to:
    //   HPBOT_LOG   log file (one status line per second plus events)
    //   HPBOT_CMDS  command file, polled every tick
    //   HPBOT_NAME, HPBOT_IP, HPBOT_PORT   client only: create this player and join that server
    public class TestBot : Mod
    {
        delegate void OrigAddMessage(RemadeChatMonitor self, string text, Color color, int width);
        static string logPath, cmdPath;
        static long cmdOffset;
        static int ticks, joinTicks;
        static bool joined, pressKey, walking, digging, quitting;
        static int walkDirection;
        static string screenshotPath;
        static PropertyInfo paused, countdown, source;
        static Hook newTextHook;

        public override void Load()
        {
            logPath = Environment.GetEnvironmentVariable("HPBOT_LOG");
            cmdPath = Environment.GetEnvironmentVariable("HPBOT_CMDS");
            if (logPath == null) return;
            Type system = ModLoader.GetMod("HostPause").Code.GetType("HostPause.Common.PauseSystem");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            paused = system.GetProperty("Paused", flags);
            countdown = system.GetProperty("Countdown", flags);
            source = system.GetProperty("Source", flags);
            Main.OnTickForThirdPartySoftwareOnly += Tick;
            if (!Main.dedServ)
            {
                // Every chat line ends here; Main.NewText itself is small enough to be inlined.
                newTextHook = new Hook(typeof(RemadeChatMonitor).GetMethod("AddNewMessage"),
                    (OrigAddMessage orig, RemadeChatMonitor self, string text, Color color, int width) => { Log("CHAT " + text); orig(self, text, color, width); });
                On_Main.Draw += AfterDraw;
            }
            Log("BOT loaded role=" + (Main.dedServ ? "server" : "client"));
        }

        public override void Unload()
        {
            Main.OnTickForThirdPartySoftwareOnly -= Tick;
            newTextHook?.Dispose();
        }

        internal static void Log(string text)
        {
            if (logPath == null) return;
            lock (typeof(TestBot)) File.AppendAllText(logPath, DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine);
        }

        static bool Paused => (bool)paused.GetValue(null);

        // Runs every tick on the dedicated server loop and in every client DoUpdate, paused or not.
        static void Tick()
        {
            try
            {
                ticks++;
                ReadCommands();
                // Join from the title menu once mod loading has fully finished.
                if (!Main.dedServ && !joined) joinTicks = Main.gameMenu && Main.menuMode == 0 ? joinTicks + 1 : 0;
                if (!Main.dedServ && !joined && joinTicks > 120) Join();
                if (ticks % 60 == 0) Status();
                Bind();
            }
            catch (Exception ex) { Log("BOT ERROR " + ex); }
        }

        // Bind the pause key as a player would in Settings > Controls (tModLoader leaves mod keys unbound).
        static void Bind()
        {
            string key = Environment.GetEnvironmentVariable("HPBOT_BIND");
            if (Main.dedServ || key == null || PlayerInput.CurrentProfile == null) return;
            var status = PlayerInput.CurrentProfile.InputModes[InputMode.Keyboard].KeyStatus;
            if (!status.TryGetValue("HostPause/Pause", out var keys) || keys.Contains(key)) return;
            keys.Clear();
            keys.Add(key);
            Log("BOUND HostPause/Pause to " + key);
        }

        static void Join()
        {
            joined = true;
            string name = Environment.GetEnvironmentVariable("HPBOT_NAME") ?? "Bot";
            var player = new Player { name = name };
            // Load the saved file back: loading sets up per-player data a bare Player lacks.
            PlayerFileData data = Player.GetFileData(PlayerFileData.CreateAndSave(player).Path, false);
            data.SetAsActive();
            string world = Environment.GetEnvironmentVariable("HPBOT_WORLD");
            if (world != null)
            {
                // Single player: play a copy of the test world.
                Main.ActiveWorldFileData = WorldFile.GetAllMetadata(world, false);
                Main.ActiveWorldFileData.SetAsActive();
                WorldGen.playWorld();
                Log("BOT playing single player " + world);
                return;
            }
            Netplay.ListenPort = int.Parse(Environment.GetEnvironmentVariable("HPBOT_PORT") ?? "7777");
            Netplay.SetRemoteIP(Environment.GetEnvironmentVariable("HPBOT_IP") ?? "127.0.0.1");
            Main.menuMode = 10;
            Netplay.StartTcpClient();
            Log("BOT joining as " + name);
        }

        static void Status()
        {
            string line = "STATUS paused=" + Paused + " countdown=" + countdown.GetValue(null) + " source=" + source.GetValue(null)
                + " time=" + Main.time.ToString("F0") + " day=" + Main.dayTime + " gamePaused=" + Main.gamePaused;
            if (Main.gameMenu) { Log(line + " menu=" + Main.menuMode); return; }
            var players = Enumerable.Range(0, 255).Where(i => Main.player[i].active)
                .Select(i => Main.player[i].name + "@" + Main.player[i].position.X.ToString("F1") + "," + Main.player[i].position.Y.ToString("F1"));
            var npcs = Main.npc.Where(n => n.active).ToList();
            double npcSum = npcs.Sum(n => n.position.X + n.position.Y * 3);
            int projectiles = Main.projectile.Count(p => p.active);
            Log(line + " players=[" + string.Join(";", players) + "] npcs=" + npcs.Count + " npcSum=" + npcSum.ToString("F1") + " proj=" + projectiles);
        }

        static void ReadCommands()
        {
            if (cmdPath == null || !File.Exists(cmdPath)) return;
            using var stream = new FileStream(cmdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= cmdOffset) return;
            stream.Position = cmdOffset;
            using var reader = new StreamReader(stream);
            string all = reader.ReadToEnd();
            int end = all.LastIndexOf('\n');
            if (end < 0) return;
            cmdOffset += System.Text.Encoding.UTF8.GetByteCount(all.Substring(0, end + 1));
            foreach (string raw in all.Substring(0, end).Split('\n'))
            {
                string command = raw.Trim();
                if (command.Length == 0) continue;
                Log("CMD " + command);
                Run(command);
            }
        }

        static void Run(string command)
        {
            string[] args = command.Split(' ');
            switch (args[0])
            {
                case "key": pressKey = true; break;
                case "walk": walking = args[1] != "stop"; walkDirection = args[1] == "left" ? -1 : 1; break;
                case "dig":
                    digging = args[1] == "on";
                    if (digging)
                    {
                        Player me = Main.LocalPlayer;
                        digX = (int)(me.Center.X / 16) + 3; digY = (int)(me.Bottom.Y / 16) + 3;
                        Log("DIGROW " + digX + " " + digY);
                    }
                    break;
                case "chat": ChatHelper.SendChatMessageFromClient(new ChatMessage(command.Substring(5))); break;
                case "shot": screenshotPath = args[1]; break;
                case "status": Status(); break;
                case "spawn":
                    // Server: bunnies around the first player, so frozen NPC movement can be observed.
                    Player target = Main.player.First(p => p.active);
                    for (int i = 0; i < int.Parse(args[1]); i++)
                        NPC.NewNPC(null, (int)target.Center.X + (i - 2) * 48, (int)target.Center.Y - 32, NPCID.Bunny);
                    break;
                case "tiles":
                    // Dump the row of tiles the dig command works on.
                    Log("TILES " + Row(int.Parse(args[1]), int.Parse(args[2])));
                    break;
                case "quit":
                    quitting = true;
                    Netplay.Disconnect = true;
                    Main.instance.Exit();
                    break;
            }
        }

        internal static int digX, digY;

        internal static string Row(int x0, int y)
        {
            var row = new System.Text.StringBuilder();
            for (int x = x0; x < x0 + 40; x++) row.Append(Main.tile[x, y].HasTile ? '#' : '.');
            return x0 + " " + y + " " + row;
        }

        internal static bool ConsumeKey()
        {
            bool value = pressKey;
            pressKey = false;
            return value;
        }

        internal static bool Walking(out int direction) { direction = walkDirection; return walking; }
        internal static bool Digging => digging;

        static void AfterDraw(On_Main.orig_Draw orig, Main self, GameTime gameTime)
        {
            orig(self, gameTime);
            if (screenshotPath == null || quitting) return;
            string path = screenshotPath;
            screenshotPath = null;
            try
            {
                GraphicsDevice device = Main.instance.GraphicsDevice;
                int width = device.PresentationParameters.BackBufferWidth, height = device.PresentationParameters.BackBufferHeight;
                var pixels = new Color[width * height];
                device.GetBackBufferData(pixels);
                using var texture = new Texture2D(device, width, height);
                texture.SetData(pixels);
                using FileStream file = File.Create(path);
                texture.SaveAsPng(file, width, height);
                Log("SHOT " + path + " " + width + "x" + height);
            }
            catch (Exception ex) { Log("SHOT FAILED " + ex.Message); }
        }
    }

    // Simulates the HostPause key press exactly where a real one is seen: after input is read,
    // before the simulation gate in Main.DoUpdate.
    public class BotInput : ModSystem
    {
        public override void PostUpdateInput()
        {
            if (TestBot.ConsumeKey()) PlayerInput.Triggers.JustPressed.KeyStatus["HostPause/Pause"] = true;
        }
    }

    // Gameplay actions run inside the simulation, so they stop exactly when the client freezes.
    public class BotPlayer : ModPlayer
    {
        int digStep;

        public override void SetControls()
        {
            if (Player.whoAmI != Main.myPlayer || !TestBot.Walking(out int direction)) return;
            Player.controlRight = direction > 0;
            Player.controlLeft = direction < 0;
        }

        public override void PostUpdate()
        {
            if (Player.whoAmI != Main.myPlayer || Main.netMode != NetmodeID.MultiplayerClient || !TestBot.Digging) return;
            // Toggle one tile per tick along a row under the player: place dirt, or remove it.
            int x = TestBot.digX + digStep % 40, y = TestBot.digY;
            digStep++;
            if (Main.tile[x, y].HasTile)
            {
                WorldGen.KillTile(x, y, noItem: true);
                NetMessage.SendData(MessageID.TileManipulation, -1, -1, null, 4, x, y);
            }
            else
            {
                WorldGen.PlaceTile(x, y, TileID.Dirt, forced: true);
                NetMessage.SendData(MessageID.TileManipulation, -1, -1, null, 1, x, y, TileID.Dirt);
            }
        }
    }
}
