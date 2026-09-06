using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>One "currently holds Hero rank N" entry - near-identical shape to HeroCandidateEntry but with heroGrade instead of vote/reputation.</summary>
public readonly record struct HeroListEntry(
    uint SeasonId, uint CharacterId, uint TopFactionId, uint ExpeditionId,
    int Ranking, int Score, int AccumPoint, byte HeroGrade);

/// <summary>
/// Recovered 2026-08-14 via Ghidra (see aaemu-siege-castle-hero-nation memory). Field names confirmed via the
/// same literal debug-dump format string as SCHeroCandidateListPacket's ("Hero Character List" dump), which
/// uses the identical field set except heroGrade replaces voteCount/reputation - matching this codebase's own
/// hero_grade_id concept (4/3/3/2/2/2 = Erenor/Ayanad/Ayanad/Delphinad/Delphinad/Delphinad). A first pass here
/// had one extra placeholder field before charId that turned out to be native struct alignment padding, not a
/// real wire field - fixed to the confirmed 8-field order.
/// </summary>
public sealed class SCHeroListPacket(IReadOnlyList<HeroListEntry> heroes) : GamePacket(SCOffsets.SCHeroListPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(0); // unresolved (top-level, not covered by this recovery)
        stream.Write(heroes.Count);
        foreach (var hero in heroes)
        {
            stream.Write(hero.SeasonId);
            stream.Write((ulong)hero.CharacterId);
            stream.Write(hero.TopFactionId);
            stream.Write(hero.ExpeditionId);
            stream.Write(hero.Ranking);
            stream.Write(hero.Score);
            stream.Write(hero.AccumPoint);
            stream.Write(hero.HeroGrade);
        }
        return stream;
    }
}
