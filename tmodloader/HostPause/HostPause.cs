using System.IO;
using HostPause.Common;
using Terraria.ModLoader;

namespace HostPause
{
    public class HostPause : Mod
    {
        public override void HandlePacket(BinaryReader reader, int whoAmI) => PauseSystem.Receive(reader, whoAmI);
    }
}
