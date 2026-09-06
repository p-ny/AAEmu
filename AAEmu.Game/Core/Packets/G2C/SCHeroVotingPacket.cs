using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// Tells the client this character has already voted this cycle.
/// </summary>
/// <remarks>
/// Confirmed via a rejected community PR's disassembly (github.com/AAEmu/AAEmu/pull/1516, closed for code
/// shape not for wrong analysis): X2Hero:IsAlreadyVoted() reads a byte that ONLY this packet's handler ever
/// writes natively - nothing in SCHeroCandidateListPacket's own fields feeds it. election.lua reads that flag
/// as the voting-machine window builds, to grey the vote button, hide the checkboxes, and show "you have
/// already voted." This class existed with the right field layout but was never sent by anything ("nothing
/// constructs this packet yet") - fixed 2026-08-14, see HeroManager.SendHeroInfoForFaction/Vote.
/// </remarks>
public class SCHeroVotingPacket(int @type, sbyte voteInfo) : GamePacket(SCOffsets.SCHeroVotingPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(@type);
        stream.Write(voteInfo);
        return stream;
    }
}
