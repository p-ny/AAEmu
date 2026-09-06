using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

/// <summary>
/// No `special_effects` row anywhere in the shipped design data actually uses type 71 (this effect) - the
/// founding quest's completion in NewQuestCode.cs is the real trigger for now (see NationManager's doc comment).
/// Implemented here too so a future data fix (wiring skill 32992/buff 17947 to this type) converges on the same
/// logic without further server changes, rather than leaving a dead stub next to a working duplicate path.
/// </summary>
public class DeclareIndependence : SpecialEffectAction
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
        if (caster is Character) { Logger.Debug("Special effects: DeclareIndependence value1 {0}, value2 {1}, value3 {2}, value4 {3}", value1, value2, value3, value4); }

        if (caster is not Character character)
            return;

        NationManager.Instance.DeclareIndependence(character);
    }
}
