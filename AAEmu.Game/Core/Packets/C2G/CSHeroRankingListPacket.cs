using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>
/// The Candidates panel's per-faction request - found via the real client Lua source 2026-08-14
/// (hero_rank.lua's comboBox:SelectedProc -> X2Hero:RequestRankData(selFactionId)). The single field is that
/// selected factionId, not a generic TypeValue - same pattern as CSHeroAllScorePacket's real "factionId"
/// parameter. Answering with the full unscoped SendHeroInfo (as this used to) meant the reply never actually
/// corresponded to whichever faction tab was clicked, which is why the picker always seemed to show Nuia's data
/// regardless of selection.
/// </summary>
public class CSHeroRankingListPacket() : GamePacket(CSOffsets.CSHeroRankingListPacket, 1)
{
    public int FactionId { get; private set; }

    public override void Read(PacketStream stream)
    {
        FactionId = stream.ReadInt32();
    }

    public override void Execute()
    {
        if (Connection?.ActiveChar != null)
            HeroManager.Instance.SendHeroInfoForRequestedFaction(Connection.ActiveChar, (uint)FactionId);
    }
}
