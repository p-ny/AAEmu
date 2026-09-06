using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>
/// The client's response to the Mobilization Order popup (X2Faction:RequestMobilizationOrder) shown to
/// non-issuing faction members - sent on Accept, Cancel/decline, or the popup timing out. See
/// HeroManager.AcceptMobilizationOrder.
/// </summary>
/// <remarks>
/// 2026-08-31: RTTI-decoded the real Unpack (FUN_39c66100) - 3 fields: "result" @ +0x10 (slot 0xa0, width
/// not primitive-confirmed - kept at the pre-existing uint32 guess), @ +0x18 (slot 0x98, this project's
/// established 8-byte-plain-value width - matches the pre-existing ulong), @ +0x20 (slot 0x88, width not
/// primitive-confirmed - kept at the pre-existing int16 guess). Field COUNT and relative order are
/// decompile-confirmed; individual widths beyond the first field are offset-gap evidence only.
///
/// 2026-09-05: field semantics resolved via mobilization_order.lua's own call site -
/// X2Faction:RequestMobilizationOrder(result, param.heroId, param.zoneGroupType), result being
/// MOBILIZATION_ORDER_RESULT (Accept=1/Cancel=2/TimeOver=3) - so this is the popup's real response, not a
/// plain status query as earlier guessed. ZoneGroupType is only ever echoed back from whatever the server
/// itself sent in SCFactionMobilizationOrderPacket - not acted on here.
/// </remarks>
public class CSFactionMobilizationOrderPacket() : GamePacket(CSOffsets.CSFactionMobilizationOrderPacket, 1)
{
    public uint Result { get; private set; }
    public ulong HeroId { get; private set; }
    public short ZoneGroupType { get; private set; }

    public override void Read(PacketStream stream)
    {
        Result = stream.ReadUInt32();
        HeroId = stream.ReadUInt64();
        ZoneGroupType = stream.ReadInt16();

        var character = Connection.ActiveChar;
        if (character == null)
            return;

        if (Result == (uint)MobilizationOrderResultType.Accept)
            HeroManager.Instance.AcceptMobilizationOrder(character, HeroId);

        character.SendPacket(new SCHeroMobilizationOrderUpdatedPacket(
            0, HeroManager.Instance.ResolveMobilizationOrderZoneGroupId(character), character.Id,
            (uint)character.MobilizationOrderTodayCount, (uint)character.MobilizationOrderTotalCount));
    }
}
