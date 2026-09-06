namespace AAEmu.Game.Models.StaticValues;

/// <summary>
/// A faction's currently-declared Mobilization Order flag state - the flag's "2nd/3rd interaction"
/// (distinct from issuing a war/peace/choice order), each with its own real per-faction declaration
/// skill and 30-minute active window: Nuia's 도약의 시간/Time of Leap (skills 40150/40175), Haranya's
/// 전투의 시간/전쟁의 준비/Time of Battle-War Preparation (skills 50166/50168), and the Pirates'
/// 강탈의 시간/Time of Plunder (skill 43726). See <see cref="AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects.DeclareMobilizationTimeState"/>.
/// </summary>
public enum MobilizationTimeStateType : byte
{
    None = 0,
    Leap = 1,
    Battle = 2,
    Plunder = 3
}
