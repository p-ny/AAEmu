using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

/// <summary>
/// The Hero-election voting machine doodad (e.g. the Marianople one) - interacting with it opens the ballot.
/// </summary>
/// <remarks>
/// Confirmed missing entirely 2026-08-15 via a full mining pass of a rejected community PR
/// (github.com/AAEmu/AAEmu/pull/1516, closed for code shape not for wrong analysis) plus a direct query
/// against this server's own live `compact.sqlite3`: real `doodad_funcs` rows exist with
/// `actual_func_type='DoodadFuncHeroElection'` (doodad template ids 26323/26397), but no class of this name
/// existed anywhere in this codebase - DoodadManager's reflection-based loader finds a func by its
/// (type, actual_func_id) pair, so an unregistered type name means those rows can never be found and the
/// interaction was, until now, a silent no-op at the doodad-func layer specifically.
///
/// id-only in doodad_func_hero_elections (no config columns) - this doodad has nothing to configure, it just
/// needs to exist so the (type, id) lookup succeeds.
/// </remarks>
public class DoodadFuncHeroElection : DoodadFuncTemplate
{
    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Trace("DoodadFuncHeroElection");

        if (caster is not Character character)
            return;

        // Same push CSHeroCandidateListPacket's own handler uses (the client's on-demand ballot request) -
        // showUi:true is what actually raises the native "HERO_ELECTION" UI event that opens the window. See
        // HeroManager.SendHeroInfo's doc comment for why this flag matters at all.
        HeroManager.Instance.SendHeroInfo(character, showUi: true);
    }
}
