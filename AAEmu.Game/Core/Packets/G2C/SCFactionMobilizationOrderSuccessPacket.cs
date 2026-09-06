using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// Bare ack sent to the issuing Hero confirming a Mobilization Order was accepted - see
/// HeroManager.IssueMobilizationOrder.
/// </summary>
/// <remarks>
/// 2026-08-31: RTTI-decoded (opcode discovery: see SCOffsets.cs's SCFactionMobilizationOrderSuccessPacket
/// entry). Its own Unpack (vtable+0x10, FUN_395e5690) is a trivial empty function - this packet carries no
/// payload beyond the standard packet header, confirmed via decompile, not assumed.
/// </remarks>
public class SCFactionMobilizationOrderSuccessPacket() : GamePacket(SCOffsets.SCFactionMobilizationOrderSuccessPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        return stream;
    }
}
