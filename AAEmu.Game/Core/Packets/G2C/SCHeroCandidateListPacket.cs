using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>One hero_candidates row for the client's Hero candidate list UI.</summary>
public readonly record struct HeroCandidateEntry(
    uint SeasonId, uint CharacterId, uint TopFactionId, uint ExpeditionId,
    int Ranking, int Score, int AccumPoint, int VoteCount, int Reputation);

/// <summary>
/// Recovered 2026-08-14 via Ghidra (see aaemu-siege-castle-hero-nation memory) - the candidate list with
/// leadership-derived ranking data the client's Hero panel needs; previously this opcode didn't exist anywhere
/// in this codebase at all, which is why that panel showed empty regardless of server-side election state.
/// Per-entry field names confirmed via a literal debug-dump sprintf_s format string in the client
/// ("seasonId(%lu), charId(%lu), topFactionId(%lu), expeditionId(%lu), ranking(%u), score(%u), accumP(%u),
/// voteCount(%u), reputation(%d)") - a first pass here had one extra placeholder field before charId that
/// turned out to be native struct alignment padding, not a real wire field, which likely explains why the list
/// showed empty even after the opcode/request-wiring was correct (an extra field shifted every subsequent read).
///
/// The two top-level header int32s (factionId, season) were unconfirmed here and sent as hardcoded 0 - fixed
/// 2026-08-14 using a rejected community PR's independent reverse-engineering of the same opcode
/// (github.com/AAEmu/AAEmu/pull/1516, closed for code-quality reasons but the disassembly is real and worth
/// trusting): confirmed order factionId then season, though that PR itself flags this specific pair as its own
/// least-certain finding ("if the window comes up empty they are the first thing to swap").
/// </summary>
public sealed class SCHeroCandidateListPacket(bool showUi, int factionId, int season, IReadOnlyList<HeroCandidateEntry> candidates)
    : GamePacket(SCOffsets.SCHeroCandidateListPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(showUi);
        stream.Write(factionId);
        stream.Write(season);
        stream.Write(candidates.Count);
        foreach (var candidate in candidates)
        {
            stream.Write(candidate.SeasonId);
            stream.Write((ulong)candidate.CharacterId);
            stream.Write(candidate.TopFactionId);
            stream.Write(candidate.ExpeditionId);
            stream.Write(candidate.Ranking);
            stream.Write(candidate.Score);
            stream.Write(candidate.AccumPoint);
            stream.Write(candidate.VoteCount);
            stream.Write(candidate.Reputation);
        }
        return stream;
    }
}
