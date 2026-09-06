using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

/// <summary>
/// Server-only special effect (see SpecialType.DeclareMobilizationTimeState's own doc comment) added to
/// the 5 real Mobilization Order flag declaration skills. value1 is the declared MobilizationTimeStateType
/// (1 Leap/40150+40175, 2 Battle/50166+50168, 3 Plunder/43726); value2 is the active window in seconds
/// (1800 = 30 minutes, matching every "버닝 30분 남음"/burning-30-minutes-remaining phase group found in
/// the shipped data for all 3 factions).
/// </summary>
public class DeclareMobilizationTimeState : SpecialEffectAction
{
    public override void Execute(BaseUnit caster,
        SkillCaster casterObj,
        BaseUnit target,
        SkillCastTarget targetObj,
        CastAction castObj,
        Skill skill,
        SkillObject skillObject,
        DateTime time,
        int value1,
        int value2,
        int value3,
        int value4)
    {
        if (caster is not Character { Faction: not null } character || value1 <= 0 || value2 <= 0)
        {
            Logger.Debug("Special effects: DeclareMobilizationTimeState value1 {0}, value2 {1} (caster/faction missing or invalid values)", value1, value2);
            return;
        }

        HeroManager.Instance.SetMobilizationTimeState((uint)character.Faction.Id, (MobilizationTimeStateType)value1, TimeSpan.FromSeconds(value2));
    }
}
