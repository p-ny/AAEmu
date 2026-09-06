using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// One "scores" row for SCHeroAllScorePacket - a DIFFERENT, wider layout than HeroCandidateEntry, confirmed via
/// disassembly 2026-08-14 (see aaemu-siege-castle-hero-nation memory): 56-byte client-side stride, not the
/// 48-byte candidate/hero-list shape. Reusing HeroCandidateEntry's layout here on a first attempt desynced the
/// client's per-entry vector push and crashed it on spawn - do not repeat that mistake.
/// </summary>
public readonly record struct HeroScoreEntry(ulong CharacterId, int Score, int PeriodScore, int MobilizationCount);

/// <summary>
/// Answers CSHeroAllScorePacket (0x1A8, real Lua binding name "RequestFactionScores(factionId)" per Ghidra).
/// Field layout confirmed via disassembly of FUN_39c8c380 (top-level) and FUN_39d276e0/FUN_39c8be70 (per-entry,
/// cross-checked three ways): charId (u64) at +0x00, an unresolved int32 at +0x08, "score" at +0x0c,
/// "periodScore" at +0x10, "mobilizationCount" at +0x14, then a nested field named "today" filling the rest of
/// the confirmed 0x38 (56-byte) client-side stride. A first pass assumed "today" was a wire string (matching
/// sizeof(std::string)=32 bytes on MSVC x64, which is what the in-memory struct actually holds) and crashed the
/// client - re-traced via FUN_39b4dd40/FUN_39cf7090/FUN_39b76760/FUN_3948d950 and confirmed it's really a
/// std::map&lt;int32,int32&gt; ("Size" count-prefix, then that many {"k","v"} int32 pairs) - the in-memory object is a
/// 32-byte map container, but the WIRE encoding for an empty map is just a single int32 Size=0, not a
/// string-shaped length prefix. Real key/value semantics still unconfirmed (possibly per-hour mobilization
/// tracking, per the field name) - sent empty (Size=0) until there's a reason to populate it.
/// </summary>
/// <remarks>
/// 2026-09-05: the top-level field was hardcoded 0 - changed to echo the requested factionId. Client-side
/// X2Hero:GetFactionScores(factionID) was independently confirmed (twice) to return nil regardless of the
/// per-entry data this packet carries; the client almost certainly caches this reply keyed by an id from
/// the reply itself (hero_mission.lua's ScoreUpdated(factionID) looks the cache up by the faction it asked
/// for), and this top-level field is the only candidate slot for that key - the exact bug shape already
/// found and fixed in SCHeroCandidateListPacket's own factionId/season header fields. Not decompile-
/// confirmed to be factionId specifically, but low-risk (SC packet, cosmetic-garbling risk only per this
/// class's own established model) given hardcoded 0 was definitely wrong for a per-faction cache key.
/// </remarks>
public sealed class SCHeroAllScorePacket(int factionId, IReadOnlyList<HeroScoreEntry> scores) : GamePacket(SCOffsets.SCHeroAllScorePacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(factionId);
        stream.Write(scores.Count);
        foreach (var score in scores)
        {
            stream.Write(score.CharacterId);
            stream.Write(0); // unresolved (+0x08, generic "type" key)
            stream.Write(score.Score);
            stream.Write(score.PeriodScore);
            stream.Write(score.MobilizationCount);
            stream.Write(0); // "today": map<int32,int32> Size=0 (empty)
        }
        return stream;
    }
}
