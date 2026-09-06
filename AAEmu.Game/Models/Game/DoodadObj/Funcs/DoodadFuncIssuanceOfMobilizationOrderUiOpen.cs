using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

/// <summary>
/// The Mobilization Order doodad (present at 10 template ids per `doodad_func_issuance_of_mobilization_order_ui_opens`,
/// spread across multiple faction locations) - interacting with it opens the Mobilization Order dialog.
/// Mirrors DoodadFuncHeroElection's pattern exactly.
/// </summary>
/// <remarks>
/// This doodad has no config columns - it just needs to exist so the (type, id) reflection lookup in
/// DoodadManager succeeds. Client-side this raises DoodadFuncIssuanceOfMobilizationOrderUiOpenDesc's UI
/// event (X2::GameClient::MobilizationOrderCallDlgTask); the actual submit happens via a separate packet
/// (CSFactionIssuanceOfMobilizationOrderPacket), not this interaction itself - Use() only needs to prime
/// the client's current counters, matching DoodadFuncHeroElection's SendHeroInfo(showUi:true) precedent.
///
/// The client gates the whole dialog on an exact match between its cached zoneGroupId and this packet's
/// own zoneGroupId field (live-confirmed via Ghidra+Frida 2026-09-06 - see HeroManager's
/// MobilizationOrderZoneGroups) - every call site hardcoding 0 here made the dialog permanently
/// unreachable, always falling through to the generic "used all your orders" message regardless of the
/// real count. This was NOT a client bug (an earlier session's TODO here concluding that was wrong).
/// </remarks>
public class DoodadFuncIssuanceOfMobilizationOrderUiOpen : DoodadFuncTemplate
{
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Trace("DoodadFuncIssuanceOfMobilizationOrderUiOpen");

        if (caster is not Character character)
            return;

        var zoneGroupId = HeroManager.Instance.ResolveMobilizationOrderZoneGroupId(character);
        character.SendPacket(new Core.Packets.G2C.SCHeroMobilizationOrderUpdatedPacket(
            0, zoneGroupId, character.Id, (uint)character.MobilizationOrderTodayCount, (uint)character.MobilizationOrderTotalCount));
    }
}
