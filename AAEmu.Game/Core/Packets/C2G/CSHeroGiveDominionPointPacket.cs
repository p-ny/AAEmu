using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>
/// Backs X2Hero:GiveDominionPoint(zoneGroup) - see HeroManager.GiveDominionPoint's own doc comment.
/// </summary>
/// <remarks>
/// 2026-08-31: the client's own serializer only names this one field "TypeValue" (width/order confirmed,
/// semantic name not). Identified via the client Lua instead - tab_dominion.lua's "distribution" button
/// calls X2Hero:GiveDominionPoint(window.zoneGroup) with exactly one argument, matching this packet's
/// single field - so this is the zoneId of the dominion the Hero is distributing a point to, the same
/// "zoneGroup" CSUpdateDominionTaxRatePacket already resolves for the identical window.
/// </remarks>
public class CSHeroGiveDominionPointPacket() : GamePacket(CSOffsets.CSHeroGiveDominionPointPacket, 1)
{
    public ushort ZoneId { get; private set; }

    public override void Read(PacketStream stream)
    {
        ZoneId = stream.ReadUInt16();
        HeroManager.Instance.GiveDominionPoint(Connection.ActiveChar, ZoneId);
    }
}
