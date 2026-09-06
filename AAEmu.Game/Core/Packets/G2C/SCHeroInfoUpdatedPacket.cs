using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// Recovered 2026-08-14 via Ghidra (see aaemu-siege-castle-hero-nation memory) - a single-record
/// upsert/delta counterpart to SCHeroListPacket, sharing the exact same 40-byte record reader client-side.
/// Strong circumstantial evidence (not a directly-observed write instruction) that this is what populates the
/// client's local per-character-id Hero map - the map IsHero/IsTopLevelHero actually check, which is separate
/// from and not fed by the list packets (SCHeroListPacket/SCHeroRankingListPacket) despite the identical wire
/// shape. Send this whenever a character's Hero status changes (election finalized, or the /makehero test
/// toggle) - sending only the list packets was confirmed NOT to update this map.
/// </summary>
public sealed class SCHeroInfoUpdatedPacket(HeroListEntry hero) : GamePacket(SCOffsets.SCHeroInfoUpdatedPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(hero.SeasonId);
        stream.Write((ulong)hero.CharacterId);
        stream.Write(hero.TopFactionId);
        stream.Write(hero.ExpeditionId);
        stream.Write(hero.Ranking);
        stream.Write(hero.Score);
        stream.Write(hero.AccumPoint);
        stream.Write(hero.HeroGrade);
        return stream;
    }
}
