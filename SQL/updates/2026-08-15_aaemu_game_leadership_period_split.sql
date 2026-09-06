USE aaemu_game;

-- Splits leadership into the four figures the client actually reads separately (see the PR the fix was
-- ported from, github.com/AAEmu/AAEmu/pull/1516, and D:\aa\hero-vote-bug-report.txt for the investigation):
--
--   leadership_point          current period - what candidacy/leaderboard ranks on. Previously this
--                             column held the LIFETIME total (wrong role); the old current-period column
--                             (leadership_point_period) is renamed into this slot instead.
--   leadership_period_point  the PREVIOUS period's final figure, frozen at each LeadershipRanking roll -
--                             what the client's vote-eligibility check and "Last Season Leadership" row
--                             actually read (SCCharacterGamePointsPacket slot 12). New, starts at 0 - we
--                             have no historical prior-period data to backfill it from.
--   accumulated_leadership_point  lifetime total, never reset. Takes over the OLD leadership_point
--                             column's data via rename, so nobody's existing total is lost.
--   daily_leadership_point / last_daily_leadership_point_time  retail's daily cap tracker (not enforced
--                             yet). New, starts at 0 / never-accrued.
ALTER TABLE `characters`
  CHANGE COLUMN `leadership_point` `accumulated_leadership_point` int NOT NULL DEFAULT '0'
    COMMENT 'Lifetime leadership, never reset - client "Current Record" right-hand figure',
  CHANGE COLUMN `leadership_point_period` `leadership_point` int NOT NULL DEFAULT '0'
    COMMENT 'Current period leadership - ranks the leaderboard and gates Hero candidacy. Reset to 0 by HeroManager''s roll at each LeadershipRanking phase, after leadership_period_point below is snapshotted from it',
  ADD COLUMN `leadership_period_point` int NOT NULL DEFAULT '0'
    COMMENT 'Previous period''s final leadership, frozen at the LeadershipRanking roll - client "Last Season Leadership" row (SCCharacterGamePointsPacket slot 12), and the Hero-vote eligibility gate. NOT the same field as leadership_point above'
    AFTER `leadership_point`,
  ADD COLUMN `daily_leadership_point` int unsigned NOT NULL DEFAULT '0'
    COMMENT 'Leadership earned since last_daily_leadership_point_time, for the retail daily cap (not enforced yet)'
    AFTER `accumulated_leadership_point`,
  ADD COLUMN `last_daily_leadership_point_time` datetime NOT NULL DEFAULT '0001-01-01 00:00:00'
    COMMENT 'When the daily leadership counter last rolled over'
    AFTER `daily_leadership_point`;
