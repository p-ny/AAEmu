using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// Fills X2Player:GetGamePoints() on the client - the table the character sheet AND the Hero-election
/// voter-eligibility check both read.
/// </summary>
/// <remarks>
/// The slots are fourteen dwords at ClientPlayer+0xec0. Confirmed via a rejected community PR's independent
/// reverse-engineering (github.com/AAEmu/AAEmu/pull/1516, closed for code shape not for wrong analysis) -
/// slot 12 was empirically confirmed in-game by that PR's author (wrote index*1000 into all 14 slots, watched
/// the character sheet's "Last Season Leadership" row render 12000):
///
///   slot 0   honorPoint
///   slot 1   livingPoint (vocation)
///   slot 11  leadershipPoint (lifetime total)
///   slot 12  periodLeadershipPoint - what X2Hero:IsVoter() actually compares against hero_conditions
///
/// Previously all 12 non-honor/vocation slots went out as 0 - including slot 12, which meant the Hero-election
/// vote checkbox could never enable for anyone regardless of real leadership standing. This codebase had
/// already (wrongly) tried fixing a superficially similar field in SCCharacterStatePacket's
/// "dailyLeadershipPoint" - that field is a genuinely different, unrelated calendar-daily leadership cap (per
/// the same PR), not this one. See Character.ChangeGamePoints/HeroManager.SendHeroInfo for where this now gets
/// (re)sent - SCHeroSeasonOffPacket carries the same slot-12 value via a second, older client code path and
/// must always accompany this one (see that packet's own doc comment).
/// </remarks>
public class SCCharacterGamePointsPacket(Character character) : GamePacket(SCOffsets.SCCharacterGamePointsPacket, 1)
{
    private const int SlotCount = 14;
    private const int LifetimeLeadershipSlot = 11;
    private const int PeriodLeadershipSlot = 12;

    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(character.HonorPoint);    // 0 honorPoint
        stream.Write(character.VocationPoint); // 1 livingPoint (vocation)

        for (var i = 2; i < LifetimeLeadershipSlot; i++)
            stream.Write(0);

        stream.Write(character.AccumulatedLeadershipPoint); // 11 leadershipPoint (lifetime)
        stream.Write(character.LeadershipPeriodPoint);      // 12 periodLeadershipPoint (previous period, frozen)

        for (var i = PeriodLeadershipSlot + 1; i < SlotCount; i++)
            stream.Write(0);

        return stream;
    }
}
