using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>
/// TypeValue's field name is unconfirmed. HeroManager.Abstain always acts on the caller's own character
/// regardless of this payload - trusting a client-supplied id to withdraw an arbitrary candidate would be
/// exploitable, and a candidate abstaining anyone but themselves wouldn't make sense anyway.
/// </summary>
public class CSHeroAbstainPacket() : GamePacket(CSOffsets.CSHeroAbstainPacket, 1)
{
    public ulong TypeValue { get; private set; }

    public override void Read(PacketStream stream)
    {
        TypeValue = stream.ReadUInt64();
        HeroManager.Instance.Abstain(Connection);
    }
}
