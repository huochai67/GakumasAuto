---
name: gakumas-club
description: >
  Guild/club daily: claim note-request reward, start a new request, donate to members.
  Use when the user says 社团, 俱乐部, 送礼, 笔记请求.
---

# Club

Spending: `club_request` / `club_donate` need `confirm:true`.

## Enter

`club_enter` uses hamburger **menu LinkData** (メニュー→ギルド), not raw `ScreenState.GuildTop`. Wait `state.screen=guild` and `club_state.screenOpen`.

If `error` mentions loading or the model is empty: `club_enter` again (plugin dismisses NOW LOADING). Skip after ~15s still empty.

## Receive

If `canReceive`: `club_receive`, close 受取完了 with `CancelButton`.

## Request

If `canRequest`:

1. `club_request` `confirm:true` → taps `RequestButton`, opens アイテム募集 overlay (`topLayer=IScreenLayer`).
2. Cells are all named `GuildRequestItemListCell(Clone)` — no unique names. Confirm selection via `ItemName` text (`Canvas/UIContentArea/ContentRoot/ItemDetail/ItemName`).
3. Default note: アノマリーノート（ビジュアル）. Tap last-row-left on the grid (≈90,320 on 541×961) then re-read `ItemName`. If wrong, tap another cell.
4. Picker 決定: `invoke_callback` `Canvas/FrontUIContentArea/ButtonRoot/ExecuteButton` (or first `ExecuteButton` while picker is top).
5. Confirm sheet アイテム募集確認: `invoke_callback` `SheetRoot/Buttons/ExecuteButton`. **Bare `ExecuteButton` hits the picker button under the sheet and stacks a second confirm.** If two sheets are stacked, invoke `SheetRoot/Buttons/ExecuteButton` twice.
6. Success: `requestState=Requesting`, `canRequest=false`. `requestedItemName` may stay a raw id (`item-limitovermaterial-…`); trust the UI / `ItemName`.

## Donate

`club_donate` taps `DonateButton` then **immediately** `MoveNextRequest` — remaining count does **not** drop. Do not use it as a one-shot complete donate.

Working sequence (max 5):

1. `invoke_callback` `DonateButton` (寄付する). Wait `topLayer=IScreenLayer`.
2. `invoke_callback` `SheetRoot/Buttons/ExecuteButton`.
3. Close result with `CancelButton` only if a 閉じる sheet is up. **Do not Cancel the donate confirm** — that aborts.
4. `club_state`: `remainDonationCount` should decrement (5→4…). `donationState=Done` means this member is done; go to the next member (plugin `MoveNextRequest` / next `DonateButton`).
5. Stop on remain=0, `CLUB_DONATE_NOT_FOUND`, or no `DonateButton`.

UI 残り N/5回 is ground truth if `club_state` lags.

Then `screen_goto` `home`.
---
