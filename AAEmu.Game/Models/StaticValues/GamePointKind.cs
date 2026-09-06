namespace AAEmu.Game.Models.StaticValues;

public enum GamePointKind : byte
{
    Honor = 0,
    Vocation = 1,

    /// <summary>
    /// Touches Character.LeadershipPoint (current period) and, on a real gain, AccumulatedLeadershipPoint
    /// (lifetime) and DailyLeadershipPoint - see ChangeGamePoints' Leadership case. Deliberately does NOT
    /// touch LeadershipPeriodPoint (the previous, frozen period) - only HeroManager's per-cycle roll may
    /// write that. UnitReqsKindType.LeadershipTotal/Current (77/78) gates Hero candidacy/voting
    /// (hero_conditions.hero_candidate_min_point/votable_leadership_point). Delivered via the same generic
    /// SCGamePointChangedPacket as Honor/Vocation; unconfirmed whether the 10.0.2.13 client actually expects
    /// that generic delivery for leadership specifically, or one of the dedicated LEADERSHIP_POINT/
    /// PLAYER_LEADERSHIP_POINT client messages seen in x2game-dev.dll strings instead - worth checking
    /// against a live packet capture. See D:\aa\hero-vote-bug-report.txt for the 4-figure leadership model
    /// this is part of.
    /// </summary>
    Leadership = 2
}
