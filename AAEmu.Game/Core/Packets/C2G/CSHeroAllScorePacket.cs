using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>
/// The main Hero-menu "Candidates" panel's own request - separate from CSHeroCandidateListPacket/
/// CSHeroRankingListPacket (which the voting-machine doodad window uses). Found 2026-08-14: this was parsed but
/// never answered, which is why that panel always showed "No Hero candidates have been selected yet" even while
/// the voting-machine window correctly rendered real candidate data at the same time - two different opcodes,
/// only one of them was ever wired up. The single field is NOT a generic TypeValue - confirmed via Ghidra as the
/// real `factionId` parameter of the client's "RequestFactionScores(factionId)" Lua binding, i.e. this fires once
/// per faction-tab click in the Candidates panel. Scoping the reply to that specific faction (instead of always
/// dumping every faction's data) is what actually lets the tab-switch work - previously every tab got answered
/// identically and the client always ended up showing whichever faction had real data (Nuia) regardless of which
/// tab was clicked.
/// </summary>
public class CSHeroAllScorePacket() : GamePacket(CSOffsets.CSHeroAllScorePacket, 1)
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
