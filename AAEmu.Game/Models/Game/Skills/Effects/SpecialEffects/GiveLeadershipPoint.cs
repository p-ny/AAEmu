using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

/// <summary>
/// Was declared in <see cref="SpecialEffectType"/> with no implementation at all - the enum value fired but
/// nothing happened, and Character had no leadership stat to change regardless. Backing field is
/// Character.LeadershipPoint (see GamePointKind.Leadership), needed by the Hero/leadership election system
/// (hero_conditions.votable_leadership_point / hero_candidate_min_point) - see
/// D:\aa\siege-castle-hero-nation-brief.md.
/// </summary>
/// <remarks>
/// Applies to the TARGET, not the caster - confirmed via a rejected community PR's own explicit fix comment
/// for this exact effect (github.com/AAEmu/AAEmu/pull/1516): retail's leadership-granting skills are cast BY
/// a commander ON the subordinate being credited, not self-cast. The first implementation here awarded the
/// caster instead, which would silently pay the wrong player. Also guards non-positive amounts - this effect
/// grants, it doesn't deduct; a real deduction path would be a different, deliberate effect.
/// </remarks>
public class GiveLeadershipPoint : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.GiveLeadershipPoint;

    public override void Execute(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
        CastAction castObj,
        Skill skill, SkillObject skillObject, DateTime time, int amount, int value2, int value3, int value4)
    {
        if (target is Character) { Logger.Debug("Special effects: GiveLeadershipPoint amount {0}, value2 {1}, value3 {2}, value4 {3}", amount, value2, value3, value4); }

        if (amount <= 0)
            return;

        if (target is not Character character)
            return;

        character.ChangeGamePoints(GamePointKind.Leadership, amount);
    }
}
