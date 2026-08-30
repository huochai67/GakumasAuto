---
name: gakumas-shop
description: >
  Buy gakumas daily money/AP exchange goods and weekly free packs via MCP.
  Use when the user says 商店, 兑换, 买笔记, AP商店, 周礼包.
---

# Shop / daily exchange

Money+AP shop is **daily exchange**, not jewel shop. `exchange_buy` / `shop_buy_item` need `confirm:true`.

## Money shop

1. `exchange_enter` `type=daily`. If that opens the jewel shop or a picker, `screen_goto` `ItemExchangeTop` (or `exchange_enter` again with `exchange_id`, e.g. `exchange-piece-1`).
2. Wait until `exchange_list` / `exchange_items` has rows. Buy `recommend=true` first, then any names the user listed (レッスンノート, ベテランノート, サポート強化Pt, センス/ロジック/アノマリーノート, 再挑戦チケット, 記録の鍵).
3. `exchange_buy` `confirm:true` `name=...`. Skip sold-out / limit / insufficient funds.
4. Confirm sheet: `SheetRoot/Buttons/ExecuteButton`. Close 購入完了 with `CancelButton`.
5. If `manualResettable` and reset still free: find 更新/Reset, invoke, buy recommend again.

## AP tab

Find tab text `AP` / `スタミナ`, tap, `exchange_items`, buy user-selected AP goods via `exchange_buy`.

## Weekly free pack

`shop_enter` `jewel` or `shop_list`. `shop_buy_item` `confirm:true` on `isFree` and not sold out.

## Pitfalls (live)

- `shop_enter` default is jewel. Daily money shop ≠ ダイヤショップ.
- Footer `BackButton` invoke often no-ops while NOW LOADING is up. `loading_hide` then `screen_goto` `home`.
- If the user did not name items, still buy **recommended** money-shop rows and any `isFree` jewel pack.
---
