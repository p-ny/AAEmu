using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>
/// The client's on-demand request for the Hero candidate/ranking/phase data - sent when opening the Hero UI
/// panel or interacting with an election voting machine doodad. Previously parsed but never answered (found
/// 2026-08-14: we were only ever pushing this data proactively at login/phase-change, never in response to
/// this explicit request, which is very likely why the voting machine interaction produced no UI at all even
/// after the phase-broadcast fix). Reuses HeroManager.SendHeroInfo, same data as the login push.
///
/// This is the ONE call site that should pass showUi=true - confirmed via a rejected community PR's
/// disassembly (github.com/AAEmu/AAEmu/pull/1516) that SCHeroCandidateListPacket's showUi flag is what makes
/// the native client raise the window-populating "HERO_ELECTION" Lua event; every other push (login, zone-
/// enter, phase-change broadcasts) must stay showUi=false or it repopulates - and silently resets the checked-
/// row state of - an already-open ballot window, which is exactly why a real vote click never actually
/// submitted anything even though the confirmation dialog showed the right candidate name. See HeroManager.
/// SendHeroInfo's doc comment for the full trace.
/// </summary>
public class CSHeroCandidateListPacket() : GamePacket(CSOffsets.CSHeroCandidateListPacket, 1)
{
    public override void Read(PacketStream stream)
    {
    }

    public override void Execute()
    {
        if (Connection?.ActiveChar != null)
            HeroManager.Instance.SendHeroInfo(Connection.ActiveChar, showUi: true);
    }
}
