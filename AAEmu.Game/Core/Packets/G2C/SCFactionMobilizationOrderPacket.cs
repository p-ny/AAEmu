using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// Faction-wide broadcast: a Hero has issued a Mobilization Order. Answers
/// CSFactionIssuanceOfMobilizationOrderPacket / CSFactionMobilizationOrderPacket - see
/// HeroManager.IssueMobilizationOrder.
/// </summary>
/// <remarks>
/// 2026-08-31: RTTI-decoded the real Unpack (FUN_39c60be0) - 3 fields at entry+0x10 (slot 0x88),
/// entry+0x18 (slot 0x98, this project's established 8-byte-plain-value slot), entry+0x20 (fixed
/// 128-byte string, the "name" field, XlStringSize-driven like other name fields in this codebase).
/// The 0x10-to-0x18 offset gap is 8 bytes, so <c>type</c> was widened from the earlier guessed
/// <c>short</c> to <c>ulong</c> to match - this is offset-gap evidence, not a decompiled primitive
/// width, so treat it as best-effort (SC packet, cosmetic risk only per this project's risk model:
/// worst case is a garbled broadcast, not a crash). <c>type2</c>'s width (slot 0x98) IS
/// primitive-confirmed. Semantics: <c>type</c>/<c>type2</c> are unnamed in the decompile; this
/// implementation uses type2 for the acting Hero's persistent character id and name for their
/// display name, type left 0 (unconfirmed purpose).
/// </remarks>
public class SCFactionMobilizationOrderPacket(ulong @type, ulong @type2, string name) : GamePacket(SCOffsets.SCFactionMobilizationOrderPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(@type);
        stream.Write(@type2);
        stream.Write(name);
        return stream;
    }
}
