---
name: gakumas-contest
description: >
  Play gakumas PvP contest (競技場/コンテスト): enter, auto-set unit if empty,
  challenge high/middle/low rival, skip the fight, collect rewards.
  Use when the user says 竞赛, コンテスト, PvP, 竞技场.
---

# Contest

Do not relaunch the game from this terminal. `confirm:true` on `pvp_challenge`.

## Flow

1. `pvp_state`. Skip if `remainingDailyPlayCount=0`. If `screenOpen=false`, `pvp_enter` then wait `state.screen=pvp`.
2. Missing unit / 未编成 → `pvp_auto_set`, wait, `pvp_enter` again.
3. `pvp_challenge` `rival=high` (or user pick) `confirm:true`. This only taps `PvpRateRivalInfo`. Next screen is the battle confirm (挑戦開始), not the fight.
4. Start: `invoke_callback` `MainButton_120px` (label 挑戦開始).
5. Skip overlay (`AllSkip` checkbox + Skip + TAP):
   - `AllSkipButton` has **no** CampusButton. Do not `invoke_callback` it.
   - `SkipButton` has gestureCallback SET — invoke it once.
   - TAP overlay: `tap_at` the TAP band (≈270,140 on 541×961) hits `BackgroundTap`.
6. Result (WIN/LOSE + 次へ): `invoke_callback` `ScreenHeaderFooterCanvas/ContentArea/MainButton`.
7. Close reward sheets with `CancelButton` only. Back on `screen=pvp`.

Repeat only while tickets remain. Then `screen_goto` `home` (call `loading_hide` first if NOW LOADING is stuck).

## Pitfalls (live)

- `pvp_challenge` ≠ 挑戦開始. Forgetting `MainButton_120px` leaves you on the confirm screen.
- `remainingDailyPlayCount` can stay 5 after a real match (rivals refresh, daily mission `IncrementPvpRatePlayCount` goes Receivable). Do **not** loop 5 times on that counter. Stop when rivals did not change, チケット UI is 0, or the daily contest mission is already Receivable/Cleared.
- Skip-spamming `SkipButton` while `topLayer=IScreenLayer` does nothing; need TAP / `BackgroundTap`.
- Empty unit slots still allow 挑戦開始.
---
