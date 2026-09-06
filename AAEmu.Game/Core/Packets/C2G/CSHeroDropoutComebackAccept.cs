using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>Reverses a prior CSHeroAbstainPacket for the caller's own character - see HeroManager.DropoutComeback.</summary>
public class CSHeroDropoutComebackAccept() : GamePacket(CSOffsets.CSHeroDropoutComebackAccept, 1)
{
    public ulong Type { get; private set; }

    public override void Read(PacketStream stream)
    {
        Type = stream.ReadUInt64();
        HeroManager.Instance.DropoutComeback(Connection);
    }
}
