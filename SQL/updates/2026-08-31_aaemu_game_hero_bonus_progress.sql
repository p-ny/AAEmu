USE aaemu_game;

-- Per-character progress toward the daily-activity Hero reward box (hero_bonuses/hero_bonus_today_assignments,
-- confirmed via the client's own hero_mission.lua "todayQuests" curValue/targetCount fields) - one row per
-- (character, Hero-board today_quest_steps.id), reset to 0 once that step's threshold is reached and the
-- hero_bonuses reward (leadership + Mobilization Order charges + item box mail) is granted. See
-- HeroManager.OnHeroBoardQuestCompleted / TodayAssignmentManager.CompleteStep.
CREATE TABLE IF NOT EXISTS `character_hero_bonus_progress` (
  `character_id` INT unsigned NOT NULL,
  `today_quest_step_id` INT unsigned NOT NULL,
  `count` INT unsigned NOT NULL DEFAULT '0',
  PRIMARY KEY (`character_id`, `today_quest_step_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
