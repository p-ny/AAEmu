using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>One finalized/ranked Hero roster row (the 6-slot ranked list per faction: rank 1 = Erenor tier, 2-3 = Ayanad, 4-6 = Delphinad).</summary>
public readonly record struct HeroRankingEntry(uint CharacterId, int Leadership, int Score, uint ExpeditionId);

/// <summary>
/// Recovered 2026-08-14 via Ghidra (see aaemu-siege-castle-hero-nation memory). Top-level carries the viewing
/// character's own leadership/score alongside the ranked roster (a "your stats + the table" shape). Found the
/// real client Lua source later the same day (D:\aa\AA-CN\game\scripts\x2ui\hero\hero_rank.lua /
/// CreateHeroFactionCombobox flow): selecting a faction in the Candidates panel calls
/// X2Hero:RequestRankData(selFactionId) -> CSHeroRankingListPacket, and the reply fires a
/// "HERO_RANK_DATA_RETRIEVED(factionID)" event that the Lua callback keys off of - so the leading top-level
/// int32 (previously hardcoded 0) is almost certainly that factionId, not a throwaway field. Every faction
/// selection needs an actual reply (even an empty one) or the panel's tabWindow:WaitPage(true) loading overlay
/// never clears - see HeroManager.SendHeroInfoForFaction, which no longer gates this send on rankings.Count>0.
/// </summary>
public sealed class SCHeroRankingListPacket(uint factionId, int myLeadership, int myScore, IReadOnlyList<HeroRankingEntry> rankings)
    : GamePacket(SCOffsets.SCHeroRankingListPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write((int)factionId);
        stream.Write(myLeadership);
        stream.Write(myScore);
        stream.Write(rankings.Count);
        foreach (var ranking in rankings)
        {
            stream.Write((ulong)ranking.CharacterId);
            stream.Write(ranking.Leadership);
            stream.Write(ranking.Score);
            // Was hardcoded 0 - the real client Lua (common.lua GetExpeidtionColumnInfoRelatedHero) reads a
            // plain "expedition" string for this column with no async cache-query fallback shown (unlike the
            // name column's nameCacheQueryId pattern), so this needs to actually carry the real expedition id
            // for the client's native side to resolve into a name - trailing field, mirrors
            // HeroCandidateEntry's confirmed ExpeditionId slot in the sibling SCHeroCandidateListPacket.
            stream.Write((int)ranking.ExpeditionId);
        }
        return stream;
    }
}
