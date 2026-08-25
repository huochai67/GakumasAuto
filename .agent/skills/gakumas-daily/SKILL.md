---
name: gakumas-daily
description: >
  Run the gakumas home daily loop: collect activity fee (活動費), finish/set outings (お仕事),
  claim the present box, then claim Daily/Weekly mission rewards. Use when the user says
  日常, 收活动费, 设置外出, 领取礼物, 领取任务, daily routine, or runs /gakumas-daily.
---

# Gakumas daily

Drive the live game with **gakumas MCP tools**. If MCP is stale (tool missing / old list), fall back to `node mcp/cli.js <plugin-action>` on the same file channel.

Do not relaunch `gakumas.exe` from this agent terminal (Job Object kills the child). If the game is down, ask the user to start it.

Spending actions that need `confirm:true`: `gift_receive`, `daily_set_outing` (to actually start), `shop_buy_item`, `pvp_challenge`. This skill uses confirm on gifts and outing start.

## 0. Ready

1. `state` — need `userId` and preferably `screen=home`.
2. Title (`TAP TO START`): `invoke_callback` path `Canvas/FullScreen/BackgroundAsset/StartButton`, then wait until `state.userId` is set and `screen=home`.
3. `通信エラー` sheet: `invoke_callback` path `ErrorSheet(Clone)/Canvas/UIContentArea/SheetMoveRoot/SheetRoot/Buttons/ExecuteButton` to retry.
4. Overlay result sheets (`受取完了` / 閉じる): `invoke_callback` path `SheetRoot/Buttons/CancelButton`.
5. After every overlay, `screenshot` if the next click would be ambiguous.

## 1. 活動費

`daily_collect_money`.

- `collected` — done.
- `no_receivable_money` — already empty; continue.
- Sheet left open (`閉じる` in texts): close with `CancelButton`, then continue.

## 2. お仕事

`daily_state`. For each of `minilive` / `livestreaming`:

| `state` | Action |
|---|---|
| `Completed` | `daily_finish_outing` (optionally `work` to restrict) |
| `Acceptable` | `daily_set_outing` with `confirm:true`. Default 12 hours if `hours` omitted and the UI still has a duration picker. Reuse last character unless the user named one. |
| `Working` | Skip. `remainingSeconds` may be 0 while still Working — do not treat 0 as Completed. |

If finish produced a result sheet, close it, then set any now-Acceptable slot.

## 3. プレゼント

`gift_receive` with `confirm:true` (omit `gift_id` = 一括受取).

- Success looks like `invoked … AllReceiveButton` plus a `受取完了` sheet. Close the sheet.
- Empty box: continue.
- `gift_list` first only if you need to report what will be claimed.

## 4. デイリー / ウィークリー

`mission_list` `Daily` and `Weekly` to know if anything is `Receivable`. Claim via UI anyway (login/money collect can flip state after step 1).

1. `screen_goto` `mission` (or `mission_receive` once). If the first `mission_receive` only opened the screen, wait and continue on the live UI.
2. Daily tab (default): `invoke_callback` path `ScreenHeaderFooterCanvas/ContentArea/SlideRoot/ReceiveAllButton`. Close any `受取完了` sheet.
3. Weekly tab: `invoke_callback` path `CampusSimpleTabButtonGroup/CampusSimpleTabButton (1)` (label ウィークリー), then the same `ReceiveAllButton`.
4. No popup after 一括受取 = nothing receivable on that tab. Continue.
5. `screen_goto` `home`.

Do not switch to 期間限定 / ノーマル / アイドル unless the user asked.

## 5. Report

One short table: each step → done / skipped / blocked, plus leftover Working outings and any still-Receivable missions. End on `screen=home` when possible.
