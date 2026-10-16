using System;
using System.IO;
using System.Globalization;
using Terraria;
using Microsoft.Xna.Framework;

public static class HostPauseAudit
{
    static long next;
    static bool spawned;
    public static void Observe()
    {
        if (Main.gameMenu || Main.netMode != 2) return;
        if (!spawned && Main.player[0].active)
        {
            spawned = true;
            var npc = Main.npc[199];
            npc.SetDefaults(1); npc.active = true; npc.friendly = true;
            npc.position = new Vector2(Main.spawnTileX * 16, (Main.spawnTileY-15)*16);
            npc.velocity = new Vector2(1, 0); npc.timeLeft = 100000;
            var projectile = Main.projectile[999];
            projectile.SetDefaults(1); projectile.active = true;
            projectile.position = new Vector2(Main.spawnTileX * 16, (Main.spawnTileY-20)*16);
            projectile.velocity = new Vector2(1, 0); projectile.timeLeft = 100000;
            projectile.tileCollide = false; projectile.owner = 255;
        }
        long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        if (now < next) return;
        next = now + 200;
        string line = String.Join("\t", new string[] {
            now.ToString(), HostPause.Runtime.IsPaused.ToString(), Main.time.ToString("R", CultureInfo.InvariantCulture),
            Main.GameUpdateCount.ToString(),
            Main.npc[199].position.ToString(), Main.npc[199].life.ToString(), Main.npc[199].timeLeft.ToString(),
            Main.projectile[999].position.ToString(), Main.projectile[999].timeLeft.ToString(),
            Main.player[0].position.ToString(), Main.player[0].statLife.ToString(), Main.player[0].respawnTimer.ToString()
        });
        File.AppendAllText("../test-data/audit.tsv", line + "\n");
    }
}
