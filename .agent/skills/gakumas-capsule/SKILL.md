---
name: gakumas-capsule
description: >
  Draw gakumas coin gasha (扭蛋机): friend/sense/logic/anomaly.
  Use when the user says 扭蛋, 硬币扭蛋, ガシャ, capsule.
---

# Coin gasha

**Daily default: skip.** Do not spend coins unless the user gave counts.

## Read

`capsule_state` with the screen closed returns `screenOpen=false` and `error: coin gasha screen not open (call capsule_enter first)`. Always `capsule_enter` first, wait `state.screen=coin_gasha`, then `capsule_state`.

Kinds on CoinGashaTop: `friend` / `sense` / `logic` / `anomaly` / `feature`. Typical `consumptionQuantity=10`.

## Draw (only if user specified)

1. `capsule_draw` `kind=...` `confirm:true`.
2. Count sheet: tap add/`CountAdd`/`Plus` `count` times (or max then confirm). ExecuteButton disabled → not enough coins; close.
3. `invoke_callback` ExecuteButton. Close result with Close/`CancelButton`.
4. `screen_goto` `home`.
---
