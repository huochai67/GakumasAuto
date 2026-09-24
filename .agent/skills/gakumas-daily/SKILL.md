---
name: gakumas-daily
description: >
  Run the gakumas home daily loop matching KAA: activity fee, outings, presents,
  money/AP shop, contest, guild, coin gasha, support-card upgrade, Daily/Weekly missions.
  Use when the user says 日常, 收活动费, 设置外出, 领取礼物, 领取任务, 竞赛, 商店,
  社团, 扭蛋, 支援卡, daily routine, or runs /gakumas-daily.
---

# Gakumas daily (KAA parity, MCP + plugin)

Drive the live game with **gakumas MCP tools** or directly via Node `node mcp/tool.js <tool> [key=value]`. For raw plugin file-channel commands, use `node mcp/cli.js <action>`.

Do not relaunch `gakumas.exe` from this agent terminal. If the game is down, ask the user to start it in a normal CMD.

Spending that needs `confirm:true`: `gift_receive`, `daily_set_outing`, `shop_buy_item`, `exchange_buy`, `pvp_challenge`, `club_request`, `club_donate`, `capsule_draw`, `support_upgrade`.

Per-task detail: `gakumas-shop`, `gakumas-contest`, `gakumas-club`, `gakumas-capsule`, `gakumas-support`. Produce is **not** daily — `gakumas-produce`.

## Global pitfalls (live)

- Title `TAP TO START`: `invoke_callback` `Canvas/FullScreen/BackgroundAsset/StartButton` **once** while not loading. Wait `userId` + `screen=home`. Spamming StartButton during loading stalls / crashes.
- `go_home` is MCP-only. CLI: `screen_goto screen=home`.
- Stuck NOW LOADING: `loading_hide` (plugin `LoadingManager.HideImmediate`) **before** `screen_goto` / `pvp_enter`. Footer `BackButton` often no-ops under the overlay.
- `invoke_callback` matches the **first** path. Stacked sheets: `SheetRoot/Buttons/ExecuteButton`, not bare `ExecuteButton`.
- After 受取完了 / 閉じる: `CancelButton` only. Extra `ExecuteButton` on an empty layer can NRE and dump to TAP TO START.
- `screen_goto` from `support_detail` (and some overlay-closed states) NRE (`GetCancellationTokenOnDestroy`) → title. Recover with one StartButton tap.

Default: run every step below. Skip only if the user disabled it or it is already done.

## 0. Ready

1. `state` — need `userId`, prefer `screen=home`.
2. Title: StartButton once, wait home.
3. `通信エラー`: `invoke_callback` `ErrorSheet(Clone)/Canvas/UIContentArea/SheetMoveRoot/SheetRoot/Buttons/ExecuteButton`.
4. `go_home` / `screen_goto` `home` if not on home (`loading_hide` first if overlay).

## 1. 活動費

`daily_collect_money`. `collected` / `no_receivable_money` both OK.

## 2. お仕事

`daily_state`. For `minilive` / `livestreaming`:

| `state` | Action |
|---|---|
| `Completed` | `daily_finish_outing` |
| `Acceptable` | `daily_set_outing` `confirm:true` (default 12h, reuse last character) |
| `Working` | Skip. `remainingSeconds=0` is still Working. |

## 3. プレゼント

`gift_receive` `confirm:true` (omit `gift_id` = 一括). Close 受取完了 with `CancelButton`.

## 4. 商店

Follow `gakumas-shop`. Money/AP = daily exchange (`exchange_enter` `daily` + recommend buys). Weekly free pack = jewel `isFree`. Then home.

## 5. 竞赛

Follow `gakumas-contest`. One successful skip-fight is enough for the daily mission; do not trust a stale `remainingDailyPlayCount=5` to loop.

## 6. 社团

Follow `gakumas-club`. Receive if `canReceive`. Request if `canRequest` (pick アノマリーノート, sheet Execute). Donate via DonateButton + `SheetRoot/Buttons/ExecuteButton`, not the plugin's immediate MoveNext.

## 7. 扭蛋机

Follow `gakumas-capsule`. Default **skip**. `capsule_enter` before `capsule_state`.

## 8. 支援卡升级一张

Follow `gakumas-support`. `support_upgrade` `confirm:true` three times (list → detail → Enhance) then CardEnhance `ExecuteButton`. Do not `screen_goto` from detail.

## 9. デイリー / ウィークリー

`mission_list` `category=Daily` / `Weekly` needs no UI. `summary.receivable` is ground truth.

1. `screen_goto` `mission` (or `mission_receive` — first call may only open MissionTop).
2. Daily: `invoke_callback` `ScreenHeaderFooterCanvas/ContentArea/SlideRoot/ReceiveAllButton`. Close 受取完了 with `CancelButton`.
3. Weekly: `find` ウィークリー / `CampusSimpleTabButton` (hardcoded `(1)` was **missing** this session). Then same ReceiveAllButton.
4. No popup after 一括受取 = nothing receivable. Continue.
5. `screen_goto` `home`.

Do not switch 期間限定 / ノーマル / アイドル unless asked.

Observed runtime baseline: Daily receivable drops from 4 to 0 after ReceiveAll (covering マニー回収 / マニー交換 / コンテスト挑戦 / サポート強化 missions). Weekly may already be 0.

## 10. Report

Table: each step → done / skipped / blocked. Leftover Working outings, remaining PvP tickets, still-Receivable missions. End on `screen=home` when possible.
---
