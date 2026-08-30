---
name: gakumas-produce
description: >
  Run a gakumas produce (育成) loop via MCP+plugin: enter produce, pick schedule
  steps, play exam cards from exam_hand/exam_play, shop, outing, customize, skip ADV.
  Use when the user says 培育, 育成, produce, 考试打牌, or runs /gakumas-produce.
---

# Gakumas produce

No vision. Use `produce_state` / `produce_schedule` / `exam_hand` / `exam_play` / `layout` / `invoke_callback` / `adv`.

Do not relaunch the game from this terminal. Do not buy jewel continues.

## 0. Ready

`produce_state` works **without** the produce screen (reads user data). Live leftover after a finished run: `inProgress=false`, `status=Finished`, last character/week still filled in.

- `inProgress=false`: `produce_enter`. On ProduceTop, resume (`続きから`) if a run exists; else start with おまかせ/auto deck unless the user named an idol. Confirm start sheets with `ExecuteButton`.
- `inProgress=true`: continue from `stepKind`.

`exam_start` off-exam returns `EXAM_NOT_ACTIVE`. CLI has no `exam_hand` action (MCP tool name); plugin action is `exam_play` / exam DTOs via MCP `exam_hand` / `exam_deck`.

## 1. Loop

Each iteration: `produce_state` (and `screenshot` if the next click is ambiguous). Dispatch on `stepKind` / `state.screen`:

| stepKind / screen | Action |
|---|---|
| `exam` / `exam` | `exam_start` if not `isInProgress`. Then `exam_hand`. `exam_play` with no index (recommended). If `BUSY`, wait 1s and retry. If `isShowTurnEndButton`, invoke turn-end/Next. Loop until screen leaves exam. |
| `lesson` | Tap the recommended / first active lesson cell (`find` Lesson/`おすすめ`). |
| `shop` / `produce_shop` | `produce_shop`. Buy only if user asked or a card is cheap vs remaining P-point. Else skip/leave (`BackButton` / skip). |
| `outing` / `produce_outing` | `produce_outing`. Low stamina → rest/refresh; else highest P-point. Tap, confirm. |
| `customize` / `produce_customize` | `produce_cards`. If `remainingCustomizeCount>0` and a `canCustomize` card exists, tap it; else skip. |
| `refresh` / `interval` | Confirm rest. |
| `event` / ADV | `adv` `set_ff` true, `end_wait`, `select_unselected`. |
| overlay / 通信エラー | `ExecuteButton` retry or `CancelButton` close. |

Stop when `inProgress=false` and a result screen is up. Skip result ADV the same way, then `screen_goto` `home`.

## 2. Exam card policy

1. `exam_play` without `index` uses plugin `recommendIndex`.
2. If that fails, `exam_play` `index=0`.
3. Never play a card whose `staminaCost` exceeds remaining exam stamina; fall back to cheapest.
4. Empty hand + turn-end shown → end the turn.

## 3. Report

Character, produce type, week/day, final `produceScore` / `status`, whether the run finished or blocked (and on which `stepKind`).
---
