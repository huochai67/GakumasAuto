---
name: gakumas-support
description: >
  Upgrade the lowest-level gakumas support card by one via plugin.
  Use when the user says 支援卡, 升级支援卡, support card.
---

# Support card upgrade

`support_list` needs no UI. Skip if the user disabled it.

## Flow

1. `support_list` — pick lowest `level` with `level < levelLimit` (live: `s_card-1-0000` 念入りにストレッチ lv1).
2. Without `confirm:true`: dry-run string only (`would upgrade … pass confirm:true`).
3. `support_upgrade` `confirm:true` is **multi-step**. Wait `state.screen` between calls:
   1. Opens `CardSupportCardList` (`screen=support`). Retry.
   2. Opens detail (`screen=support_detail`, `opened detail for …`). Retry.
   3. Taps `EnhanceButton` (`support upgrade: … invoked … EnhanceButton`).
   4. Confirm: `invoke_callback` `ScreenHeaderFooterCanvas/ContentArea/CardEnhance/MoveRoot/ButtonRoot/ExecuteButton`.
4. Verify with `support_list` (lv1→lv2). Close enhance with `CancelButton` if still on the panel.

## Pitfalls (live)

- `screen_goto` / `OutGameTransitionUtility.To` from `support_detail` can NRE (`GetCancellationTokenOnDestroy`) and dump to **TAP TO START**. Recover: `invoke_callback` `Canvas/FullScreen/BackgroundAsset/StartButton` **once**, wait `userId` + `screen=home`. Do not spam StartButton.
- Leave detail via Cancel/Back after enhance, **then** `screen_goto` `home`.
---
