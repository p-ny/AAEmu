using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSUpdateDominionTaxRatePacket() : GamePacket(CSOffsets.CSUpdateDominionTaxRatePacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var id = stream.ReadUInt16();
        var taxRate = stream.ReadInt32();

        Logger.Debug("UpdateDominionTaxRate, Id: {0}, TaxRate: {1}", id, taxRate);

        // 2026-08-27 fix: zone groups 54/56 (Exeloch/Sungold) live in GuildDominionManager since the
        // 2026-08-24 split, not DominionManager - calling DominionManager unconditionally silently no-op'd
        // for those two (lookup miss). Same guild-first-then-Hero/faction disjoint-check pattern
        // HousingManager.Build already uses.
        if (GuildDominionManager.Instance.GetByZoneId(id) != null)
            GuildDominionManager.Instance.UpdateTaxRate(Connection, id, taxRate);
        else
            DominionManager.Instance.UpdateTaxRate(Connection, id, taxRate);
    }
}
