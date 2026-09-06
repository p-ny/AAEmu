using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

/// <summary>
/// The 3 factions' completed capital statues each additionally offer a "Start of Leap/War/Plunder"
/// skill (Nuia 40152/40176, Haranya 50165/50169, Pirate 43724 - real, confirmed via
/// doodad_func_groups/doodad_funcs, wired via DoodadFuncPersistentUse in the shipped data with no
/// item cost), each granting the matching empowerment buff (Leap 23717 "도약의 시간", Battle 32025
/// "전쟁의 시간", Plunder 26100 "강탈의 시간" - all 3 confirmed via buff_effects, distinct from buff
/// 32024 "전쟁의 준비"/War Preparation already granted unconditionally on statue completion by
/// DoodadFuncFactionStatueDevote). These skills have no gate anywhere in the shipped data - this is
/// the "Hero-declared Mobilization Order 'time' state" connection flagged as missing in an earlier
/// session (see aaemu_hero_system_completion memory): only usable while the caster's faction currently
/// has the matching MobilizationTimeStateType active (HeroManager.SetMobilizationTimeState, set by the
/// Mobilization Order flag's own declare skills - see DeclareMobilizationTimeState.cs).
/// </summary>
public class DoodadFuncFactionStatueTimeGatedUse : DoodadFuncPersistentUse
{
    private static readonly Dictionary<uint, MobilizationTimeStateType> RequiredStateBySkillId = new()
    {
        [40152] = MobilizationTimeStateType.Leap,
        [40176] = MobilizationTimeStateType.Leap,
        [50165] = MobilizationTimeStateType.Battle,
        [50169] = MobilizationTimeStateType.Battle,
        [43724] = MobilizationTimeStateType.Plunder,
    };

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        if (caster is not Character character)
            return;

        if (RequiredStateBySkillId.TryGetValue(SkillId, out var requiredState))
        {
            var factionId = character.Faction != null ? (uint)character.Faction.Id : 0;
            var activeState = factionId > 0 ? HeroManager.Instance.GetActiveMobilizationTimeState(factionId) : MobilizationTimeStateType.None;
            if (activeState != requiredState)
            {
                character.SendErrorMessage(ErrorMessageType.Invalid);
                Logger.Debug("DoodadFuncFactionStatueTimeGatedUse: {0} tried skill {1} (needs {2} active) but faction {3}'s active state is {4}",
                    character.Name, SkillId, requiredState, factionId, activeState);
                return;
            }
        }

        base.Use(caster, owner, skillId, nextPhase);
    }
}
