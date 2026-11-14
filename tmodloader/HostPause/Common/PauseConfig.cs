using System.ComponentModel;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace HostPause.Common
{
    // Server-side: in multiplayer the server's values apply and tModLoader syncs them to clients.
    // In-game edits are accepted only from the host (tModLoader's default rule for server configs).
    public class PauseConfig : ModConfig
    {
        public override ConfigScope Mode => ConfigScope.ServerSide;

        [Range(0, 10)]
        [Slider]
        [DefaultValue(3)]
        public int ResumeCountdown;

        [DefaultValue(false)]
        public bool AnyPlayerCanPause;

        internal static PauseConfig Instance => ModContent.GetInstance<PauseConfig>();
    }
}
