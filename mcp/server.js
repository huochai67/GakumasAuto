#!/usr/bin/env node
// GakumasAuto MCP server — zero-dependency stdio JSON-RPC 2.0 (protocol 2024-11-05)
// driving the GakumasAuto v2 in-process BepInEx plugin via its file command channel.
//
//   node server.js          (env GAKUMAS_BEPINEX overrides game BepInEx dir)
//
// Channel contract (see plugin/GakumasAutoPlugin.cs):
//   write <bepinex>/gakumas-ui-cmd.json {id, action, ...params} (UTF-8)
//   -> plugin polls at 1Hz, executes, writes gakumas-ui-resp.json {id, ok, result}
//   -> command file archived as *.done.json
//   result is a string (ok text / error) or a structured JSON object (v2 plugin).
//
// Tool tiers:
//   L0 perception    — state, layout, layout2, find, screenshot, debug_button
//   L1 action        — tap_path, click_path, invoke_callback, tap_at, adv
//   L2 orchestration — wait_until
//   L3 task          — navigate, go_home (navigation policies on top of L0/L1/L2)
"use strict";

const fs = require("fs");
const fsp = require("fs/promises");
const path = require("path");
const readline = require("readline");

const PROTOCOL_VERSION = "2024-11-05";
const SERVER_NAME = "gakumas-mcp";
const SERVER_VERSION = "0.5.0";

const BEPINEX = (process.env.GAKUMAS_BEPINEX || "E:/DMM/gakumas/BepInEx").replace(/[\\/]+$/, "");
// Override with GAKUMAS_BEPINEX. The E:/DMM/... default is the common DMM install path, not a secret.
const CMD_PATH = path.join(BEPINEX, "gakumas-ui-cmd.json");
const RESP_PATH = path.join(BEPINEX, "gakumas-ui-resp.json");
const SCREEN_PATH = path.join(BEPINEX, "gakumas-screen.png");
const LOG_PATH = path.join(BEPINEX, "LogOutput.log");

const CMD_TIMEOUT_MS = 8000;      // plugin polls at 1Hz -> needs >= ~2s headroom
const RESP_POLL_MS = 100;
const SCREEN_POLL_MS = 500;
const SCREEN_TIMEOUT_MS = 10000;

let seq = 0;
let cmdQueue = Promise.resolve(); // single-flight write discipline

// ---------------- caches ----------------
const cache = { state: null, layout: null, layout2: null, screen: null, log: null };

function log(...args) {
    process.stderr.write("[gakumas-mcp] " + args.map(String).join(" ") + "\n");
}

// ---------------- channel primitives ----------------

function nextId() {
    return `mcp-${Date.now()}-${++seq}`;
}

async function writeCmd(payload) {
    // Atomic publish: write a temp file, then rename into place. The plugin polls
    // the cmd path at 1Hz and never sees a partially-written file this way.
    const tmp = `${CMD_PATH}.tmp-${process.pid}`;
    await fsp.writeFile(tmp, JSON.stringify(payload), "utf8");
    for (let i = 0; i < 3; i++) {
        try {
            await fsp.rename(tmp, CMD_PATH);
            return;
        } catch (e) {
            if (i === 2) throw e;
            await sleep(100);
        }
    }
}

async function readResp() {
    try {
        const raw = await fsp.readFile(RESP_PATH, "utf8");
        return JSON.parse(raw);
    } catch {
        return null;
    }
}

function sleep(ms) {
    return new Promise((r) => setTimeout(r, ms));
}

// Send one command and wait for the matching response. Single-flight.
function sendCommand(action, extra = {}, attempt = 0) {
    const run = async () => {
        const id = nextId();
        const cmd = { id, action, ...extra };
        log("cmd ->", action, JSON.stringify(extra));
        await writeCmd(cmd);

        const deadline = Date.now() + CMD_TIMEOUT_MS;
        let resp = null;
        while (Date.now() < deadline) {
            resp = await readResp();
            if (resp && resp.id === id) break;
            await sleep(RESP_POLL_MS);
        }
        if (!resp || resp.id !== id) {
            if (attempt < 1) {
                log(`no resp for "${action}" (attempt ${attempt}); retrying once`);
                return sendCommand(action, extra, attempt + 1);
            }
            const err = new Error(
                `game not responding for action "${action}" within ${CMD_TIMEOUT_MS}ms ` +
                `(check game running, plugin loaded: grep "GakumasAuto v2" in ${LOG_PATH})`
            );
            err.code = "GAME_TIMEOUT";
            throw err;
        }
        if (!resp.ok) {
            // File-lock race: the plugin read the cmd while it was being (re)written.
            // Atomic publish makes this near-impossible; retry once anyway.
            if (attempt < 1 && typeof resp.result === "string" && /used by another process/.test(resp.result)) {
                log(`plugin hit file lock on "${action}"; retrying once`);
                return sendCommand(action, extra, attempt + 1);
            }
            const err = new Error(`plugin error for "${action}": ${JSON.stringify(resp.result)}`);
            err.code = "PLUGIN_ERROR";
            throw err;
        }
        return resp.result;
    };
    const p = cmdQueue.then(run, run);
    // keep the chain alive even after failures
    cmdQueue = p.catch(() => {});
    return p;
}

// ---------------- v1-string fallback parsing ----------------

// Old plugin returned strings like "user=X name=Y topLayer=Z layersActive=B loading=B".
function parseStateString(s) {
    if (typeof s !== "string") return null;
    const m = s.match(
        /user=(\S*)\s+name=(\S*)\s+topLayer=(\S+)\s+layersActive=(\S+)\s+loading=(\S+)/
    );
    if (!m) return null;
    return {
        userId: m[1], name: m[2], topLayer: m[3],
        layersActive: m[4] === "True", loading: m[5] === "True",
        maintenance: "", _legacyString: true,
    };
}

function normalizeState(result) {
    if (result && typeof result === "object" && !Array.isArray(result)) {
        cache.state = result;
        return result;
    }
    const parsed = parseStateString(result);
    if (parsed) { cache.state = parsed; return parsed; }
    return { raw: String(result) };
}

// ---------------- L3 navigation policy ----------------
// Verified live 2026-08-16: home footer tabs (CampusButton) switch the
// pre-instantiated pages under ScreenCanvas/FullScreen/HomePageGroup/UIContentArea/Pages/*.
// Tab targets use full path suffixes (plugin >= 2.0.3 matches "/"-queries by
// path, which is unambiguous across all canvases — bare names collide with
// e.g. HomeGashaBackgroundSwitcher on the gasha screen).
const NAV_POLICY = {
    story: { tab: "HomeFooter/UIContentArea/FrontRoot/ButtonRoot/Story", page: "StoryPage", label: "コミュ" },
    card: { tab: "HomeFooter/UIContentArea/FrontRoot/ButtonRoot/Card", page: "IdolPage", label: "アイドル" },
    home: { tab: "HomeFooter/UIContentArea/FrontRoot/ButtonRoot/Home", page: "HomePage", label: "ホーム" },
    pvp: { tab: "HomeFooter/UIContentArea/FrontRoot/ButtonRoot/Pvp", page: "PvpPage", label: "コンテスト" },
    gasha: { tab: "HomeFooter/UIContentArea/FrontRoot/ButtonRoot/Gasha", page: "GashaPage", label: "ガシャ" },
};

async function readLayout() {
    const result = await sendCommand("layout");
    if (result && typeof result === "object" && Array.isArray(result.nodes)) {
        cache.layout = result;
        return result;
    }
    return null;
}

async function isOnHome() {
    const lay = await readLayout();
    return !!(lay && lay.nodes.some((x) => x.name === "HomeFooter"));
}

async function pageActiveState(pageName) {
    const lay = await readLayout();
    if (!lay) return null;
    const n = lay.nodes.find((x) => x.name === pageName && x.path.includes("/Pages/"));
    return n ? n.active : null;
}

async function navigateTo(target) {
    const policy = NAV_POLICY[target];
    if (!policy) {
        throw new Error(`unknown target "${target}" (known: ${Object.keys(NAV_POLICY).join(", ")})`);
    }
    if (!(await isOnHome())) {
        const err = new Error(`not on home screen (HomeFooter not found); only home-tab navigation is mapped so far`);
        err.code = "NOT_ON_HOME";
        throw err;
    }
    const cur = await pageActiveState(policy.page);
    if (cur === true) return { screen: target, page: policy.page, alreadyThere: true };

    const res = await sendCommand("invoke_callback", { path: policy.tab });
    const deadline = Date.now() + 15000;
    for (;;) {
        await sleep(800);
        const active = await pageActiveState(policy.page);
        if (active === true) {
            return { screen: target, page: policy.page, viaTab: policy.tab, result: String(res) };
        }
        if (Date.now() >= deadline) {
            const err = new Error(
                `navigate(${target}): ${policy.page} not active within 15s (tap result: ${res})`
            );
            err.code = "NAV_TIMEOUT";
            throw err;
        }
    }
}

// ---------------- advanced feature orchestration (L3) ----------------

const WORK_TYPES = {
    minilive: { label: "ミニライブ", type: "MiniLive" },
    livestreaming: { label: "ライブ配信", type: "LiveStreaming" },
};

let _dataOk = null;

async function dataCommandsAvailable() {
    // The v2.1.0 plugin adds daily_state/shop_items/exam_state. The game loads
    // plugins only at startup, so a live game may still run the old DLL.
    if (_dataOk !== null) return _dataOk;
    try {
        await sendCommand("daily_state");
        _dataOk = true;
    } catch (e) {
        _dataOk = false;
        log("plugin data commands unavailable:", e.message);
    }
    return _dataOk;
}

function pluginOutdatedError(what) {
    return err(
        "PLUGIN_OUTDATED",
        `${what} requires a newer GakumasAuto.dll. The game loads plugins only at startup: ` +
        `restart gakumas to pick up the deployed DLL, then retry.`
    );
}

async function pluginDailySafe() {
    if (!(await dataCommandsAvailable())) return null;
    return pluginDaily();
}

const HOME_WORK_BTN = "ScreenCanvas/FullScreen/HomePageGroup/UIContentArea/Pages/HomePage/PageRoot/ContentRoot/LeftRoot/LeftButtons/ButtonsRoot/WorkButtonRoot/WorkButton";
const HOME_MONEY_BTN = "ScreenCanvas/FullScreen/HomePageGroup/UIContentArea/Pages/HomePage/PageRoot/ContentRoot/LeftRoot/LeftButtons/ButtonsRoot/MoneyButtonRoot/MoneyButton";
const HOME_SHOP_BTN = "ScreenCanvas/FullScreen/HomePageGroup/UIContentArea/Pages/HomePage/PageRoot/ContentRoot/BottomRoot/ShopButtonRoot/ShopButton";

async function pluginDaily() {
    const r = await sendCommand("daily_state");
    return r && typeof r === "object" ? r : null;
}

async function pluginShop() {
    const r = await sendCommand("shop_items");
    return r && typeof r === "object" ? r : null;
}

async function pluginExam() {
    const r = await sendCommand("exam_state");
    return r && typeof r === "object" ? r : null;
}

async function findNode(pred) {
    const lay = await readLayout();
    if (!lay || !Array.isArray(lay.nodes)) return null;
    return lay.nodes.find(pred) || null;
}

async function waitForNode(pred, timeoutMs = 15000, desc = "node") {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
        const n = await findNode(pred);
        if (n) return n;
        if (Date.now() >= deadline) {
            const err = new Error(`waiting for ${desc} timed out after ${timeoutMs}ms`);
            err.code = "WAIT_TIMEOUT";
            throw err;
        }
        await sleep(800);
    }
}

async function ensureHome() {
    if (await isOnHome()) return;
    const err = new Error("not on home screen (HomeFooter not found); navigate home first");
    err.code = "NOT_ON_HOME";
    throw err;
}

function sheetTexts(lay, sheetNode) {
    return (lay.nodes || [])
        .filter((n) => n.active && n.text && n.path.startsWith(sheetNode.path + "/"))
        .map((n) => n.text);
}

// Nodes under `node` and before the next sibling (walk order == UI order).
// Sibling nodes often share identical names/paths, so id ranges are the only
// reliable way to address one cell's subtree.
function subtreeOf(lay, node, nextSibling = null) {
    const hi = nextSibling ? nextSibling.id : Infinity;
    return (lay.nodes || []).filter((n) => n.id > node.id && n.id < hi);
}

function err(code, message) {
    return Object.assign(new Error(message), { code });
}

// -- daily --

async function toolDailyState() {
    if (!(await dataCommandsAvailable())) throw pluginOutdatedError("daily_state");
    return pluginDaily();
}

async function toolDailySetOuting(args) {
    const key = String(args.work || "").toLowerCase();
    const wt = WORK_TYPES[key];
    if (!wt) throw new Error(`work must be minilive or livestreaming (got "${args.work}")`);
    const wantConfirm = args.confirm === true;

    let daily = await pluginDailySafe();
    const entry = daily && Array.isArray(daily.works) ? daily.works.find((w) => w.type === wt.type) : null;
    if (entry && entry.state === "Working") return { status: "already_working", work: entry };
    if (entry && entry.state === "Completed") throw err("WORK_COMPLETED", `work ${wt.label} is Completed; claim it first via daily_finish_outing`);
    if (daily && !entry) throw new Error(`work type ${wt.type} not found in daily_state`);

    await ensureHome();
    await sendCommand("invoke_callback", { path: HOME_WORK_BTN });
    await waitForNode((n) => n.name === "WorkStateList" && n.active, 15000, "work list");

    const lay = await readLayout();
    const cells = (lay.nodes || []).filter((n) => n.active && n.name.includes("WorkStateListCell"));
    if (!cells.length) throw err("WORK_CELL_NOT_FOUND", "work cells not found in WorkStateList");
    const pos = key === "minilive" ? 0 : 1; // walk order == list order
    let cell = cells[Math.min(pos, cells.length - 1)];
    let nextCell = cells[pos + 1] || null;
    for (let ci = 0; ci < cells.length; ci++) {
        const s = subtreeOf(lay, cells[ci], cells[ci + 1] || null);
        if (s.some((t) => t.active && t.text && t.text.includes(wt.label))) {
            cell = cells[ci];
            nextCell = cells[ci + 1] || null;
            break;
        }
    }
    const sub = subtreeOf(lay, cell, nextCell);
    if (!daily) {
        // No plugin data commands: infer state from the cell's subtree (active only).
        if (sub.some((n) => n.active && n.name === "WorkingGroup")) {
            return { status: "already_working", work: { type: wt.type, uiInferred: true } };
        }
        const hasResult = (lay.nodes || []).some((n) => n.active && n.name.includes("WorkResultSheet"));
        if (hasResult) {
            throw err("WORK_COMPLETED", `work ${wt.label} has a pending result sheet; claim it first via daily_finish_outing`);
        }
    }
    await sendCommand("tap_at", { x: cell.sx, y: cell.sy });
    await waitForNode((n) => n.name.includes("WorkCharacterSelect") && n.active, 15000, "character select screen");
    const lay2 = await readLayout();
    const charCells = (lay2.nodes || []).filter((n) => n.active && n.name.includes("WorkCharacterSelectListCell"));
    const charArg = args.character != null ? String(args.character) : "";
    if (charCells.length) {
        let chosen = charCells[0];
        let chosenSub = null;
        if (charArg) {
            let byName = null;
            for (let ci = 0; ci < charCells.length; ci++) {
                const c = charCells[ci];
                const s = subtreeOf(lay2, c, charCells[ci + 1] || null);
                if (s.some((t) => t.active && t.text && t.text.includes(charArg))) { byName = c; chosenSub = s; break; }
            }
            if (byName) chosen = byName;
            else if (/^\d+$/.test(charArg)) chosen = charCells[Math.min(Number(charArg), charCells.length - 1)];
            else log(`set_outing: character "${charArg}" not matched; using first cell`);
        }
        if (!chosenSub) chosenSub = subtreeOf(lay2, chosen, charCells[charCells.indexOf(chosen) + 1] || null);
        const btn = chosenSub.find((n) => n.active && n.name === "Button");
        await sendCommand("tap_at", { x: btn ? btn.sx : chosen.sx, y: btn ? btn.sy : chosen.sy });
    }

    await waitForNode((n) => n.name === "ConfirmButton" && n.active, 15000, "work select confirm button");
    if (args.hours != null) {
        const lay3 = await readLayout();
        const timeCell = (lay3.nodes || []).find((n) => n.active && n.text && n.text.includes(String(args.hours) + "時間"));
        if (timeCell) {
            await sendCommand("tap_at", { x: timeCell.sx, y: timeCell.sy });
            await sleep(800);
        } else {
            log(`set_outing: ${args.hours}時間 not found; keeping default duration`);
        }
    }
    const confirmBtn = await findNode((n) => n.name === "ConfirmButton" && n.active);
    if (!confirmBtn) throw err("CONFIRM_NOT_FOUND", "work select ConfirmButton disappeared");
    await sendCommand("invoke_callback", { path: confirmBtn.path });

    // A chain of confirm sheets may follow (good-condition warning, start confirm...).
    // All use the same SimpleSheet structure with SheetRoot/Buttons/{Execute,Cancel}Button.
    const sheet = await waitForNode(
        (n) => n.active && (n.name.includes("WorkStartConfirm") || n.name.includes("SimpleSheet") || n.name.includes("ConfirmSheet")),
        12000, "work start confirm sheet"
    );
    const laySheet = await readLayout();
    const texts = (laySheet.nodes || [])
        .filter((n) => n.active && n.text && (n.path.includes("SheetRoot") || n.path.includes("SimpleSheet")))
        .map((n) => n.text);
    if (!wantConfirm) {
        await sendCommand("invoke_callback", { path: "SheetRoot/Buttons/CancelButton" });
        return { status: "dry_run", note: "reached confirm sheet; cancelled (pass confirm:true to start)", sheet: sheet.name, texts };
    }
    const pressed = [sheet.name];
    await sendCommand("invoke_callback", { path: "SheetRoot/Buttons/ExecuteButton" });
    for (let i = 0; i < 3; i++) {
        await sleep(1800);
        const layC = await readLayout();
        const next = (layC.nodes || []).find((n) => n.active && (n.name.includes("SimpleSheet") || n.name.includes("ConfirmSheet")));
        if (!next) break;
        const f = await sendCommand("find", { path: "Buttons/ExecuteButton" });
        if (!f || !f.count) break;
        await sendCommand("invoke_callback", { path: "SheetRoot/Buttons/ExecuteButton" });
        pressed.push(next.name);
    }
    await sleep(3000);
    daily = await pluginDailySafe();
    return {
        status: "started",
        pressed,
        work: daily && daily.works ? daily.works.find((w) => w.type === wt.type) : { type: wt.type, uiVerified: true },
        texts,
    };
}

async function toolDailyFinishOuting(args) {
    const key = args && args.work ? String(args.work).toLowerCase() : null;
    const wt = key ? WORK_TYPES[key] : null;
    if (key && !wt) throw new Error(`work must be minilive or livestreaming (got "${key}")`);

    let daily = await pluginDailySafe();
    if (daily) {
        const completed = (daily.works || []).filter((w) => w.state === "Completed" && (!wt || w.type === wt.type));
        if (!completed.length) return { status: "nothing_to_finish", works: daily.works };
    }

    await ensureHome();
    await sendCommand("invoke_callback", { path: HOME_WORK_BTN });
    await waitForNode((n) => n.name === "WorkStateList" && n.active, 15000, "work list");
    await sleep(1500); // let the work screen finish opening (button callbacks wire up async)

    const finished = [];
    for (let i = 0; i < 4; i++) {
        const lay = await readLayout();
        const sheets = (lay.nodes || []).filter((n) => n.active && n.name.includes("WorkResultSheet"));
        if (!sheets.length) break;
        const sheet = sheets[0];
        const sub = subtreeOf(lay, sheet, sheets[1] || null);
        const exec = sub.find((n) => n.active && n.name === "ExecuteButton");
        if (!exec) break;
        finished.push({ sheet: sheet.name, texts: sub.filter((n) => n.active && n.text).map((n) => n.text) });
        await sendCommand("invoke_callback", { path: exec.path });
        await sleep(2500);
    }

    daily = await pluginDailySafe();
    await sleep(1500); // settle before locating the back button
    const layBack = await readLayout();
    const back = (layBack.nodes || []).find((n) => n.active && n.name === "BackButton");
    if (back) await sendCommand("invoke_callback", { path: back.path });
    return {
        status: "done",
        finishedCount: finished.length,
        finished,
        remainingCompleted: daily ? (daily.works || []).filter((w) => w.state === "Completed").map((w) => w.type) : null,
    };
}

async function toolDailyCollectMoney() {
    let daily = await pluginDailySafe();
    await ensureHome();
    await sendCommand("invoke_callback", { path: HOME_MONEY_BTN });
    const sheet = await waitForNode((n) => n.active && n.name.includes("HomeMoneyReceivedSheet"), 15000, "money sheet");
    const lay = await readLayout();
    const texts = sheetTexts(lay, sheet);
    const exec = (lay.nodes || []).find(
        (n) => n.active && n.name === "ExecuteButton" && n.path.startsWith(sheet.path + "/") && !(n.flags || []).includes("not-interactable")
    );
    if (exec) {
        await sendCommand("invoke_callback", { path: exec.path });
        await sleep(2500);
        daily = await pluginDailySafe();
        return { status: "collected", texts, moneyUnreceivedAfter: daily ? daily.moneyUnreceived : null };
    }
    const cancel = (lay.nodes || []).find((n) => n.active && n.name === "CancelButton" && n.path.startsWith(sheet.path + "/"));
    if (cancel) await sendCommand("invoke_callback", { path: cancel.path });
    return { status: "no_receivable_money", texts, moneyUnreceived: daily ? daily.moneyUnreceived : null };
}

// -- shop --

async function toolShopEnter(args) {
    const target = String((args && args.target) || "jewel").toLowerCase();
    if (target === "exchange" || target === "item" || target === "daily") {
        const type = target === "daily" ? "daily" : "item";
        return toolExchangeEnter({ type });
    }
    await ensureHome();
    const shopReady = (n) => n.active && (n.name.includes("ShopTopScreen") || n.name === "JewelButton" || n.name === "ShopTopHeadingList");
    const already = await findNode(shopReady);
    if (already) return { screen: "shop", alreadyThere: true };
    await sendCommand("invoke_callback", { path: HOME_SHOP_BTN });
    await waitForNode(shopReady, 15000, "shop top screen");
    if (target === "jewel") {
        try { await sendCommand("screen_goto", { screen: "ShopJewel" }); } catch (e) { log("shop jewel tab:", e.message); }
    }
    return { screen: target === "jewel" ? "shop_jewel" : "shop" };
}

async function toolShopList() {
    if (!(await dataCommandsAvailable())) throw pluginOutdatedError("shop_list");
    let s = await pluginShop();
    if (!s) throw new Error("shop_items returned no data");
    let entered = false;
    if (!s.shopScreenOpen) {
        await toolShopEnter();
        entered = true;
        s = await pluginShop();
    }
    return { ...s, enteredShop: entered };
}

async function toolShopBuyItem(args) {
    if (args.confirm !== true) {
        throw err("CONFIRM_REQUIRED", "shop_buy_item requires confirm: true — purchases spend currency");
    }
    if (!(await dataCommandsAvailable())) throw pluginOutdatedError("shop_buy_item");
    let s = await pluginShop();
    if (!s || !s.shopScreenOpen) {
        await toolShopEnter();
        s = await pluginShop();
    }
    const items = s.items || [];
    const q = args.item_id ? String(args.item_id) : (args.name ? String(args.name) : "");
    if (!q) throw new Error("shop_buy_item needs item_id or name");
    const idx = items.findIndex((it) => q && (it.id === q || (it.name && it.name.includes(q))));
    if (idx < 0) throw new Error(`item not found: "${q}" (${items.length} items visible in shop)`);
    const item = items[idx];
    if (item.isSoldOut) throw err("SOLD_OUT", `item ${item.id} ${item.name} is sold out`);
    if (!item.unlocked) throw err("LOCKED", `item ${item.id} ${item.name} is locked`);
    if (item.purchaseLimit > 0 && item.purchasedCount >= item.purchaseLimit) throw err("LIMIT_REACHED", `item ${item.id} purchase limit reached`);

    const lay = await readLayout();
    const cells = (lay.nodes || []).filter((n) => n.active && n.name.includes("ShopProductMenuListCell"));
    if (!cells.length) throw err("SHOP_CELLS_NOT_FOUND", "shop product cells not found in layout (menu list must be open)");
    const cell = cells[Math.min(idx, cells.length - 1)];
    const cellSub = subtreeOf(lay, cell, cells[Math.min(idx, cells.length - 1) + 1] || null);
    const main = cellSub.find((n) => n.active && n.name === "MainButton_120px");
    await sendCommand("tap_at", { x: main ? main.sx : cell.sx, y: main ? main.sy : cell.sy });

    const sheet = await waitForNode(
        (n) => n.active && (n.name.includes("ShopProductConfirm") || (n.name.includes("ConfirmSheet") && n.path.includes("Shop"))),
        15000, "purchase confirm sheet"
    );
    const lay2 = await readLayout();
    const texts = sheetTexts(lay2, sheet);
    const exec = (lay2.nodes || []).find((n) => n.active && n.name === "ExecuteButton" && n.path.startsWith(sheet.path + "/"));
    if (!exec) throw err("BUY_BUTTON_NOT_FOUND", `purchase button not found on ${sheet.name}`);
    await sendCommand("invoke_callback", { path: exec.path });
    await sleep(2500);
    const s2 = await pluginShop();
    const after = s2 && s2.items ? s2.items.find((it) => it.id === item.id) : null;
    return {
        status: "purchased",
        item: { id: item.id, name: item.name, price: item.price, purchasedCountBefore: item.purchasedCount },
        texts,
        purchasedCountAfter: after ? after.purchasedCount : null,
        note: after && after.purchasedCount === item.purchasedCount ? "purchased count unchanged — verify purchase result" : "",
    };
}

// -- exchange (交換所) --

async function toolExchangeList() {
    return sendCommand("exchange_list");
}

async function toolMissionList(args) {
    const extra = {};
    if (args && args.category) extra.category = String(args.category);
    try {
        return await sendCommand("mission_list", extra);
    } catch (e) {
        if (e && /unknown action: mission_list/.test(String(e.message))) throw pluginOutdatedError("mission_list");
        throw e;
    }
}
async function toolExchangeEnter(args) {
    const type = String(args.type || "").trim();
    if (!type) throw err("BAD_ARGS", "exchange_enter requires type: item|daily|event");
    const extra = { type };
    if (args.exchange_id) extra.exchange_id = String(args.exchange_id);
    const result = await sendCommand("exchange_enter", extra);
    return { type, exchange_id: extra.exchange_id || "", result };
}


// -- exam (打牌) --

async function toolExamHand() {
    if (!(await dataCommandsAvailable())) throw pluginOutdatedError("exam_hand");
    const e = await pluginExam();
    return {
        active: !!(e && e.active),
        hand: e ? e.hand : [],
        selectCardIndex: e ? e.selectCardIndex : -1,
        isShowTurnEndButton: !!(e && e.isShowTurnEndButton),
        isBusy: !!(e && e.isBusy),
        error: e ? e.error : "no exam data",
    };
}

async function toolExamDeck() {
    if (!(await dataCommandsAvailable())) throw pluginOutdatedError("exam_deck");
    const e = await pluginExam();
    return {
        active: !!(e && e.active),
        deck: e ? e.deck : [],
        grave: e ? e.grave : [],
        lost: e ? e.lost : [],
        hold: e ? e.hold : [],
        futureDeck: e ? e.futureDeck : [],
        playingCard: e ? e.playingCard : null,
        isInProgress: !!(e && e.isInProgress),
        isPauseTurnEnd: !!(e && e.isPauseTurnEnd),
        error: e ? e.error : "no exam data",
    };
}

async function toolAccount() {
    return sendCommand("account_state");
}

async function toolItemList() {
    return sendCommand("item_list");
}

async function toolScreenGoto(args) {
    return sendCommand("screen_goto", { screen: String(args.screen || "") });
}

async function toolGiftList() {
    let g = await sendCommand("gift_list");
    if (g && g.screenOpen) return g;
    await sendCommand("gift_enter");
    const deadline = Date.now() + 15000;
    while (Date.now() < deadline) {
        await sleep(800);
        g = await sendCommand("gift_list");
        if (g && g.screenOpen) return g;
    }
    return g || { error: "present screen did not open" };
}

async function toolGiftReceive(args) {
    if (args.confirm !== true) throw err("CONFIRM_REQUIRED", "gift_receive requires confirm:true");
    let g = await sendCommand("gift_list");
    if (!g || !g.screenOpen) {
        await sendCommand("gift_enter");
        await sleep(1500);
    }
    const res = await sendCommand("gift_receive", { gift_id: args.gift_id || "" });
    await sleep(1000);
    try {
        await sendCommand("invoke_callback", {
            path: "Canvas/UIContentArea/SheetMoveRoot/SheetRoot/Buttons/CancelButton"
        });
    } catch {}
    return res;
}

async function toolMissionReceive() {
    return sendCommand("mission_receive");
}

async function toolPvpState() {
    return sendCommand("pvp_state");
}

async function toolPvpEnter() {
    return sendCommand("pvp_enter");
}

async function toolProduceState() {
    return sendCommand("produce_state");
}

async function toolProduceEnter() {
    return sendCommand("produce_enter");
}

async function toolProduceSchedule() {
    return sendCommand("produce_schedule");
}

async function toolProduceShop() {
    return sendCommand("produce_shop");
}

async function toolProduceOuting() {
    return sendCommand("produce_outing");
}

async function toolProduceCards() {
    return sendCommand("produce_cards");
}

async function toolPvpChallenge(args) {
    if (args.confirm !== true) throw err("CONFIRM_REQUIRED", "pvp_challenge requires confirm:true");
    return sendCommand("pvp_challenge", { rival: String(args.rival || ""), confirm: true });
}

async function toolExamStart() {
    if (!(await dataCommandsAvailable())) throw pluginOutdatedError("exam_start");
    const e = await pluginExam();
    if (!e || !e.active) {
        throw err(
            "EXAM_NOT_ACTIVE",
            "no exam screen is active. Start an exam first (produce exam or contest rehearsal); exam_start skips the exam opening transition once the screen is open."
        );
    }
    if (e.isInitialized && !e.isBusy && e.isInProgress) {
        return { status: "already_started", state: e };
    }
    const res = await sendCommand("exam_start");
    const e2 = await pluginExam();
    return { status: "started", skipResult: res, state: e2 };
}

async function waitPluginOpen(action, pred, enterAction, enterExtra = {}, timeoutMs = 15000) {
    let s = await sendCommand(action);
    if (pred(s)) return s;
    await sendCommand(enterAction, enterExtra);
    const deadline = Date.now() + timeoutMs;
    let retried = false;
    while (Date.now() < deadline) {
        await sleep(800);
        s = await sendCommand(action);
        if (pred(s)) return s;
        const err = s && s.error ? String(s.error) : "";
        if (!retried && err.indexOf("loading") >= 0) {
            retried = true;
            await sendCommand(enterAction, enterExtra);
        }
    }
    return s;
}

async function toolClubState() {
    return waitPluginOpen("club_state", (s) => s && s.screenOpen && !s.error, "club_enter");
}
async function toolClubEnter() { return sendCommand("club_enter"); }
async function toolClubReceive() { return sendCommand("club_receive"); }
async function toolClubRequest(args) {
    if (args.confirm !== true) throw err("CONFIRM_REQUIRED", "club_request requires confirm:true");
    return sendCommand("club_request");
}
async function toolClubDonate(args) {
    if (args.confirm !== true) throw err("CONFIRM_REQUIRED", "club_donate requires confirm:true");
    return sendCommand("club_donate");
}

async function toolCapsuleState() {
    return waitPluginOpen("capsule_state", (s) => s && s.screenOpen && !s.error, "capsule_enter");
}
async function toolCapsuleEnter() { return sendCommand("capsule_enter"); }
async function toolCapsuleDraw(args) {
    if (args.confirm !== true) throw err("CONFIRM_REQUIRED", "capsule_draw requires confirm:true — draws spend coins");
    return sendCommand("capsule_draw", { kind: String(args.kind || ""), confirm: true });
}

async function toolSupportList() { return sendCommand("support_list"); }
async function toolSupportEnter() { return sendCommand("support_enter"); }
async function toolSupportUpgrade(args) {
    if (args.confirm !== true) throw err("CONFIRM_REQUIRED", "support_upgrade requires confirm:true");
    return sendCommand("support_upgrade", { confirm: true });
}

async function toolExchangeItems() {
    return waitPluginOpen("exchange_items", (s) => s && s.listScreenOpen, "exchange_enter", { type: "daily" });
}

async function toolExchangeBuy(args) {
    if (args.confirm !== true) throw err("CONFIRM_REQUIRED", "exchange_buy requires confirm:true — spends money/AP");
    let s = await sendCommand("exchange_items");
    if (!s || !s.listScreenOpen) {
        await sendCommand("exchange_enter", { type: String(args.type || "daily") });
        await sleep(1500);
        s = await sendCommand("exchange_items");
    }
    const items = (s && s.items) || [];
    const q = args.item_id ? String(args.item_id) : (args.name ? String(args.name) : "");
    if (!q) throw new Error("exchange_buy needs item_id or name");
    const idx = items.findIndex((it) => it.id === q || (it.name && it.name.includes(q)));
    if (idx < 0) throw new Error(`exchange item not found: "${q}" (${items.length} visible)`);
    const item = items[idx];
    if (!item.unlocked) throw err("LOCKED", `item ${item.id} ${item.name} is locked`);
    if (item.exchangeLimit > 0 && item.exchangedCount >= item.exchangeLimit) throw err("LIMIT_REACHED", `item ${item.id} limit reached`);

    const lay = await readLayout();
    let cells = (lay.nodes || []).filter((n) =>
        n.active &&
        (n.name.includes("ExchangeItemMixGridListCell") ||
            n.name.includes("ExchangeProductMenuListCell") ||
            n.name.includes("ShopProductMenuListCell") ||
            n.name.includes("ProductMenuListCell")) &&
        !(n.path || "").includes("SizeCacheRoot")
    );
    let cellPath = null;
    let cellSx = null, cellSy = null;
    if (cells.length > 0) {
        const cell = cells[Math.min(idx, cells.length - 1)];
        cellSx = cell.sx;
        cellSy = cell.sy;
        cellPath = cell.path;
    } else {
        const found = await toolFind({ pattern: "ExchangeItemMixGridListCell" });
        const activeMatches = (found && found.matches || []).filter((m) => m.active && !(m.path || "").includes("SizeCacheRoot"));
        if (!activeMatches.length) throw err("EXCHANGE_CELLS_NOT_FOUND", "exchange product cells not found in layout or find");
        const match = activeMatches[Math.min(idx, activeMatches.length - 1)];
        cellPath = match.path;
    }

    if (cellSx == null || cellSy == null) {
        const col = idx % 4;
        const row = Math.floor(idx / 4);
        cellSx = 75 + col * 130;
        cellSy = 660 - row * 140;
    }
    await sendCommand("tap_at", { x: cellSx, y: cellSy });

    const sheet = await waitForNode(
        (n) => n.active && (n.name.includes("Confirm") || n.name.includes("Sheet")),
        15000, "exchange confirm sheet"
    );
    const lay2 = await readLayout();
    const exec = (lay2.nodes || []).find((n) => n.active && n.name === "ExecuteButton");
    if (!exec) throw err("BUY_BUTTON_NOT_FOUND", `confirm button not found on ${sheet && sheet.name}`);
    await sendCommand("invoke_callback", { path: exec.path });
    await sleep(2000);

    try {
        await sendCommand("invoke_callback", {
            path: "Canvas/UIContentArea/SheetMoveRoot/SheetRoot/Buttons/CancelButton"
        });
    } catch {}

    return { status: "purchased", item: { id: item.id, name: item.name, price: item.price } };
}

async function toolExamPlay(args) {
    const idx = args.index == null ? -1 : Number(args.index);
    return sendCommand("exam_play", { index: idx });
}

async function toolPvpAutoSet() {
    return sendCommand("pvp_auto_set");
}


// ---------------- tools ----------------

const TOOLS = [
    {
        name: "state",
        description: "[L0] Game state: user id/name, detected screen, top layer, loading, maintenance.",
        inputSchema: { type: "object", properties: {}, additionalProperties: false },
    },
    {
        name: "layout",
        description: "[L0] Current screen UI tree (up to 3000 nodes): name/path/screen coords/size/active/flags/text.",
        inputSchema: {
            type: "object",
            properties: { includeInactive: { type: "boolean", description: "Keep inactive nodes (default true)" } },
        },
    },
    {
        name: "layout2",
        description: "[L0] Compact complete UI tree (no node/depth cap). Lines: indent +/- name [#B|#U|#X] [sx,sy wxh] [\"text\"]. +active -inactive #B CampusButton #U uGUI #X not-interactable. Reconstruct path by joining ancestor names.",
        inputSchema: {
            type: "object",
            properties: { includeInactive: { type: "boolean", description: "Keep inactive nodes (default true)" } },
        },
    },
    {
        name: "find",
        description: "[L0] Find nodes in the current screen whose name contains a substring (max 300 matches, all canvases).",
        inputSchema: {
            type: "object",
            properties: { pattern: { type: "string", description: "Substring to match in node names" } },
            required: ["pattern"],
        },
    },
    {
        name: "screenshot",
        description: "[L0] Capture the game screen to PNG (in-process ScreenCapture). Returns file path, size, bytes.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "debug_button",
        description: "[L0] Button wiring diagnostics: callback SET/null, enabled/disabled state.",
        inputSchema: {
            type: "object",
            properties: { path: { type: "string" } },
            required: ["path"],
        },
    },
    {
        name: "tap_path",
        description: "[L1] Full pointer down/up/click sequence on a node found by path/name substring.",
        inputSchema: {
            type: "object",
            properties: { path: { type: "string", description: "Node name or /-separated path" } },
            required: ["path"],
        },
    },
    {
        name: "click_path",
        description: "[L1] Gesture-gated button click (CampusButton.OnClicked / uGUI onClick).",
        inputSchema: {
            type: "object",
            properties: { path: { type: "string" } },
            required: ["path"],
        },
    },
    {
        name: "invoke_callback",
        description: "[L1] Fire the wired click callback directly (only working path on the title screen; bypasses gesture gating).",
        inputSchema: {
            type: "object",
            properties: { path: { type: "string" } },
            required: ["path"],
        },
    },
    {
        name: "tap_at",
        description: "[L1] Raycast at screen pixel coordinates and execute pointer handlers on the hit UI.",
        inputSchema: {
            type: "object",
            properties: {
                x: { type: "number", description: "Screen X (pixels)" },
                y: { type: "number", description: "Screen Y (pixels)" },
            },
            required: ["x", "y"],
        },
    },
    {
        name: "adv",
        description: "[L1] ADV story controls: end_wait (skip to next message), set_ff (fast-forward), select_unselected (auto-pick choices).",
        inputSchema: {
            type: "object",
            properties: {
                op: { type: "string", enum: ["end_wait", "set_ff", "select_unselected"] },
                value: { type: "boolean", description: "for set_ff" },
            },
            required: ["op"],
        },
    },
    {
        name: "wait_until",
        description: "[L2] Poll game state until a condition holds. Server-side polling; conditions evaluated against the state tool.",
        inputSchema: {
            type: "object",
            properties: {
                cond: {
                    type: "string",
                    enum: ["loading_false", "loading_true", "layer_changed", "state_changed", "logged_in"],
                    description: "layer_changed/state_changed compare against the value at call start",
                },
                timeout_ms: { type: "number", description: "default 30000" },
            },
            required: ["cond"],
        },
    },
    {
        name: "navigate",
        description: "[L3] Switch home-screen tabs to a target page: story/card/home/pvp/gasha. Only works while the home screen (footer) is visible.",
        inputSchema: {
            type: "object",
            properties: {
                target: {
                    type: "string",
                    enum: ["story", "card", "home", "pvp", "gasha"],
                    description: "story=コミュ, card=アイドル, home=ホーム, pvp=コンテスト, gasha=ガシャ",
                },
            },
            required: ["target"],
        },
    },
    {
        name: "go_home",
        description: "[L3] Return to the home tab (navigate target=home).",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "daily_state",
        description: "[L2] Read daily data from the game: work status (mini live / live streaming), unreceived money, jewel balance.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "daily_set_outing",
        description:
            "[L3] Set an outing (work): open the work list, pick the work and character, choose duration, and confirm. " +
            "Pass confirm:true to actually start the work; without it the flow stops at the start-confirm sheet and cancels (dry run).",
        inputSchema: {
            type: "object",
            properties: {
                work: { type: "string", enum: ["minilive", "livestreaming"], description: "ミニライブ or ライブ配信" },
                character: { type: "string", description: "Character name substring or cell index (default: first cell)" },
                hours: { type: "number", description: "Work duration in hours (default: keep current selection)" },
                confirm: { type: "boolean", description: "true = press the final start button (default false = dry run)" },
            },
            required: ["work"],
        },
    },
    {
        name: "daily_finish_outing",
        description: "[L3] Finish completed outings: open the work list and press 完了 (ExecuteButton) on every work-result sheet.",
        inputSchema: {
            type: "object",
            properties: {
                work: { type: "string", enum: ["minilive", "livestreaming"], description: "Restrict to one work type (default: all)" },
            },
        },
    },
    {
        name: "daily_collect_money",
        description: "[L3] Harvest money: open the home money sheet and press the receive button if it is interactive.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "shop_enter",
        description: "[L3] Enter a shop. target=jewel (default, 钻石商店), exchange/item (道具兑换所), daily (每日交换所).",
        inputSchema: {
            type: "object",
            properties: {
                target: { type: "string", enum: ["jewel", "exchange", "item", "daily"], description: "jewel=钻石商店, exchange/item=道具兑换所, daily=每日交换所" },
            },
        },
    },
    {
        name: "shop_list",
        description: "[L3] Get the shop item list (enters the shop first if needed). Items include price, purchase count/limit, sold-out/locked flags.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "shop_buy_item",
        description:
            "[L3] Buy a shop item by id or name (requires confirm:true). Opens the product's purchase sheet and presses the buy button.",
        inputSchema: {
            type: "object",
            properties: {
                item_id: { type: "string", description: "Exact shop item id" },
                name: { type: "string", description: "Item name substring (used when item_id omitted)" },
                confirm: { type: "boolean", description: "true required — purchases spend currency" },
            },
            required: ["confirm"],
        },
    },
    {
        name: "exchange_list",
        description:
            "[L2] Current exchange picker/list: id/name/type plus selectScreenOpen/listScreenOpen/currentId. " +
            "Works on the exchange select screen or an open item/daily/event exchange shop.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "exchange_enter",
        description:
            "[L3] Open an exchange shop via OutGameTransitionUtility.To. " +
            "type=item|daily|event. Omit exchange_id to open the picker; pass it (e.g. exchange-piece-1) to jump to that shop.",
        inputSchema: {
            type: "object",
            properties: {
                type: { type: "string", enum: ["item", "daily", "event"], description: "item=アイテム交換所, daily=デイリー, event=イベント" },
                exchange_id: { type: "string", description: "Exact shop id from exchange_list; omit to open the select picker" },
            },
            required: ["type"],
        },
    },
    {
        name: "mission_list",
        description:
            "[L2] Mission completion by category (Daily/Weekly/Normal/Special/Event/Achievement/MainTask). " +
            "Reads UserDataManager + MissionMaster; no UI required. Returns summary counts and each mission's " +
            "progress/threshold/state (Receivable|InProgress|Lock|Cleared|MaxPoint).",
        inputSchema: {
            type: "object",
            properties: {
                category: {
                    type: "string",
                    enum: ["Daily", "Weekly", "Normal", "Special", "Event", "Achievement", "MainTask"],
                    description: "Optional category filter; omit for all valid missions",
                },
            },
        },
    },
    {
        name: "exam_start",
        description: "[L3] Start card play (打牌): skip the exam opening transition. Requires an active exam screen (produce exam or contest rehearsal).",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "exam_hand",
        description: "[L2] Get the exam hand (手牌): cards currently in hand plus selection/turn state.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "exam_deck",
        description: "[L2] Get the exam deck (底牌): deck, grave, lost, hold, upcoming draws (futureDeck) and the card being played.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "account",
        description: "[L2] Account snapshot: id/name, producer level/exp, fan, money, jewels, action/challenge points.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "item_list",
        description: "[L2] Inventory items (id/name/type/quantity/expiry) plus money total. Reads UserItemList; no UI required.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "screen_goto",
        description: "[L3] Jump to a screen via OutGameTransitionUtility. Aliases: home/shop/jewel/exchange/present/mission/work/produce/pvp/item. Or a Campus.ScreenState name (HomeTop, ShopTop, ProduceTop, ...).",
        inputSchema: {
            type: "object",
            properties: {
                screen: { type: "string", description: "Alias or ScreenState name" },
            },
            required: ["screen"],
        },
    },
    {
        name: "gift_list",
        description: "[L2] Present-box gifts. Opens PresentTop if needed, then reads the gift list.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "gift_receive",
        description: "[L3] Receive gifts. Omit gift_id to receive all. Requires confirm:true.",
        inputSchema: {
            type: "object",
            properties: {
                gift_id: { type: "string", description: "Single gift id; omit to receive all" },
                confirm: { type: "boolean", description: "true required" },
            },
            required: ["confirm"],
        },
    },
    {
        name: "mission_receive",
        description: "[L3] Open the mission screen and press a receive/receive-all button.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "pvp_state",
        description: "[L2] Arena / PvP rate: remaining daily plays, grade, rank, rate, rivals if the PvP top screen is open.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "pvp_enter",
        description: "[L3] Enter the PvP rate top screen.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "produce_state",
        description: "[L2] Current produce run: type, character, P-point, stamina, date/week, current step kind (exam/outing/shop/customize/lesson) and today's options.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "produce_enter",
        description: "[L3] Enter the produce top screen.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "produce_schedule",
        description: "[L2] Produce calendar: each day's selected step and available step types.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "produce_shop",
        description: "[L2] Produce shop goods (card/drink/item): price, purchased, lock, upgrade. Best when the produce shop screen is open.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "produce_outing",
        description: "[L2] Produce outing (おでかけ) options: type/name/stamina/P-point.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "produce_cards",
        description: "[L2] Produce deck cards and customize state (upgradeCount, canCustomize). Used for 特别指导.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "pvp_challenge",
        description: "[L3] Challenge a PvP rival. rival=high|middle|low. confirm:true required to tap; otherwise dry run.",
        inputSchema: {
            type: "object",
            properties: {
                rival: { type: "string", enum: ["high", "middle", "low"], description: "High/Middle/Low rival row" },
                confirm: { type: "boolean", description: "true required to actually tap the rival" },
            },
            required: ["rival", "confirm"],
        },
    },
    {
        name: "pvp_auto_set",
        description: "[L3] Open PvP unit edit and press the auto-set formation button (未编成时自动编成).",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "club_state",
        description: "[L2] Guild/club top: request state (CanRequest/Requesting/RequestingAndCanReceive), donation remaining, members. Opens GuildTop if needed.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "club_enter",
        description: "[L3] Open GuildTop (社团).",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "club_receive",
        description: "[L3] Claim guild note-request reward if receivable.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "club_request",
        description: "[L3] Start a new guild note request. Requires confirm:true. After this, pick the note in UI (layout/find) and confirm.",
        inputSchema: {
            type: "object",
            properties: { confirm: { type: "boolean", description: "true required" } },
            required: ["confirm"],
        },
    },
    {
        name: "club_donate",
        description: "[L3] Donate/send gift to the current guild request, then advance to the next member. Requires confirm:true.",
        inputSchema: {
            type: "object",
            properties: { confirm: { type: "boolean", description: "true required" } },
            required: ["confirm"],
        },
    },
    {
        name: "capsule_state",
        description: "[L2] Coin gasha (扭蛋机) list: kind=friend|sense|logic|anomaly, lock, consumption. Opens CoinGashaTop if needed.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "capsule_enter",
        description: "[L3] Open CoinGashaTop (コインガシャ).",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "capsule_draw",
        description: "[L3] Open the draw sheet for a coin gasha kind. Requires confirm:true. Then set count and press ExecuteButton.",
        inputSchema: {
            type: "object",
            properties: {
                kind: { type: "string", enum: ["friend", "sense", "logic", "anomaly"] },
                confirm: { type: "boolean", description: "true required — spends coins" },
            },
            required: ["kind", "confirm"],
        },
    },
    {
        name: "support_list",
        description: "[L2] Owned support cards (id/name/level/limit). Reads UserSupportCardList; no UI required.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "support_enter",
        description: "[L3] Open CardSupportCardList.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "support_upgrade",
        description: "[L3] Upgrade the lowest-level support card by one. Requires confirm:true. May need a retry after the detail screen opens.",
        inputSchema: {
            type: "object",
            properties: { confirm: { type: "boolean", description: "true required" } },
            required: ["confirm"],
        },
    },
    {
        name: "exchange_items",
        description: "[L2] Items on the current daily/item exchange tab (name, price, exchangedCount, recommend). Opens daily exchange if needed.",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "exchange_buy",
        description: "[L3] Buy an exchange item by id or name. Requires confirm:true.",
        inputSchema: {
            type: "object",
            properties: {
                item_id: { type: "string" },
                name: { type: "string", description: "Name substring" },
                type: { type: "string", enum: ["daily", "item"], description: "default daily" },
                confirm: { type: "boolean", description: "true required" },
            },
            required: ["confirm"],
        },
    },
    {
        name: "exam_play",
        description: "[L3] Play a hand card by index. Omit index to play the game's recommended card (GetNextPlayHandIndex).",
        inputSchema: {
            type: "object",
            properties: { index: { type: "number", description: "Hand index; omit or -1 = recommend" } },
        },
    },
];

async function toolState() {
    return normalizeState(await sendCommand("state"));
}

async function toolLayout(args = {}) {
    let result = await sendCommand("layout");
    if (result && typeof result === "object") {
        // Default: keep inactive nodes (screen context); filter only on explicit false.
        const includeInactive = args.includeInactive !== false;
        if (!includeInactive && Array.isArray(result.nodes)) {
            result = { ...result, nodes: result.nodes.filter((n) => n.active !== false) };
        }
        cache.layout = result;
        return result;
    }
    // v1 fallback: layout only went to the game log
    const tail = await readLogTail(400);
    const begin = tail.lastIndexOf("LAYOUT BEGIN");
    const end = tail.lastIndexOf("LAYOUT END");
    const text = begin >= 0 ? tail.slice(begin, end >= begin ? end + 32 : undefined) : "(no layout in recent log)";
    return { root: "(v1 log dump)", count: 0, nodes: [], logDump: text };
}

async function toolLayout2(args = {}) {
    const extra = {};
    if (args.includeInactive === false) extra.includeInactive = false;
    try {
        const result = await sendCommand("layout2", extra);
        if (result && typeof result === "object") cache.layout2 = result;
        return result;
    } catch (e) {
        if (e && /unknown action: layout2/.test(String(e.message))) throw pluginOutdatedError("layout2");
        throw e;
    }
}

async function toolFind(args) {
    const result = await sendCommand("find", { path: args.pattern });
    return result;
}

async function toolScreenshot() {
    const before = Date.now();
    const result = await sendCommand("screenshot");
    if (result && typeof result === "object" && result.pending) {
        // async capture fallback: poll for a fresh file
        const deadline = Date.now() + SCREEN_TIMEOUT_MS;
        for (;;) {
            try {
                const st = await fsp.stat(SCREEN_PATH);
                if (st.mtimeMs > before && st.size > 0) {
                    result.w = result.w || 0; result.h = result.h || 0;
                    result.file = SCREEN_PATH;
                    result.sizeBytes = st.size;
                    result.pending = false;
                    break;
                }
            } catch {}
            if (Date.now() >= deadline) {
                result.file = result.file || SCREEN_PATH;
                result.sizeBytes = -1;
                break;
            }
            await sleep(SCREEN_POLL_MS);
        }
    } else if (result && typeof result === "object") {
        try {
            const st = await fsp.stat(result.file || SCREEN_PATH);
            result.sizeBytes = st.size;
        } catch {}
    }
    cache.screen = result;
    return result;
}

async function toolAdv(args) {
    switch (args.op) {
        case "end_wait": return sendCommand("adv_end_wait");
        case "set_ff": return sendCommand("adv_set_ff", { value: !!args.value });
        case "select_unselected": return sendCommand("adv_select_unselected");
        default: throw new Error(`unknown adv op: ${args.op}`);
    }
}

async function toolWaitUntil(args) {
    const cond = args.cond;
    const timeout = args.timeout_ms || 30000;
    const deadline = Date.now() + timeout;
    const start = await toolState();
    const startLayer = start.topLayer;
    const startSnap = JSON.stringify(start);
    let last = start;
    for (;;) {
        const cur = await toolState();
        last = cur;
        switch (cond) {
            case "loading_false": if (cur.loading === false) return cur; break;
            case "loading_true": if (cur.loading === true) return cur; break;
            case "layer_changed": if (cur.topLayer !== startLayer) return cur; break;
            case "state_changed": if (JSON.stringify(cur) !== startSnap) return cur; break;
            case "logged_in": if (cur.userId) return cur; break;
            default: throw new Error(`unknown cond: ${cond}`);
        }
        if (Date.now() >= deadline) {
            const err = new Error(`wait_until(${cond}) timed out after ${timeout}ms; last state: ${JSON.stringify(last)}`);
            err.code = "WAIT_TIMEOUT";
            throw err;
        }
        await sleep(1000);
    }
}

const TOOL_IMPL = {
    state: toolState,
    layout: toolLayout,
    layout2: toolLayout2,
    find: toolFind,
    screenshot: toolScreenshot,
    debug_button: (a) => sendCommand("debug_button", { path: a.path }),
    tap_path: (a) => sendCommand("tap", { path: a.path }),
    click_path: (a) => sendCommand("click", { path: a.path }),
    invoke_callback: (a) => sendCommand("invoke_callback", { path: a.path }),
    tap_at: (a) => sendCommand("tap_at", { x: a.x, y: a.y }),
    adv: toolAdv,
    wait_until: toolWaitUntil,
    navigate: (a) => navigateTo(String(a.target || "").toLowerCase()),
    go_home: () => navigateTo("home"),
    daily_state: toolDailyState,
    daily_set_outing: toolDailySetOuting,
    daily_finish_outing: toolDailyFinishOuting,
    daily_collect_money: toolDailyCollectMoney,
    shop_enter: toolShopEnter,
    shop_list: toolShopList,
    shop_buy_item: toolShopBuyItem,
    exchange_list: toolExchangeList,
    exchange_enter: toolExchangeEnter,
    mission_list: toolMissionList,
    exam_start: toolExamStart,
    exam_hand: toolExamHand,
    exam_deck: toolExamDeck,
    account: toolAccount,
    item_list: toolItemList,
    screen_goto: toolScreenGoto,
    gift_list: toolGiftList,
    gift_receive: toolGiftReceive,
    mission_receive: toolMissionReceive,
    pvp_state: toolPvpState,
    pvp_enter: toolPvpEnter,
    produce_state: toolProduceState,
    produce_enter: toolProduceEnter,
    produce_schedule: toolProduceSchedule,
    produce_shop: toolProduceShop,
    produce_outing: toolProduceOuting,
    produce_cards: toolProduceCards,
    pvp_challenge: toolPvpChallenge,
    pvp_auto_set: toolPvpAutoSet,
    club_state: toolClubState,
    club_enter: toolClubEnter,
    club_receive: toolClubReceive,
    club_request: toolClubRequest,
    club_donate: toolClubDonate,
    capsule_state: toolCapsuleState,
    capsule_enter: toolCapsuleEnter,
    capsule_draw: toolCapsuleDraw,
    support_list: toolSupportList,
    support_enter: toolSupportEnter,
    support_upgrade: toolSupportUpgrade,
    exchange_items: toolExchangeItems,
    exchange_buy: toolExchangeBuy,
    exam_play: toolExamPlay,
};

// ---------------- resources ----------------

async function readLogTail(lines = 200) {
    try {
        const stat = await fsp.stat(LOG_PATH);
        const fd = await fsp.open(LOG_PATH, "r");
        try {
            const chunk = 65536;
            let pos = Math.max(0, stat.size - chunk);
            const parts = [];
            for (; pos >= 0; pos -= chunk) {
                const len = Math.min(chunk, stat.size - pos);
                const b = Buffer.alloc(len);
                await fd.read(b, 0, len, pos);
                parts.unshift(b.toString("utf8"));
                if (pos === 0) break;
            }
            const text = parts.join("");
            const arr = text.split(/\r?\n/).filter((l) => l.length > 0);
            return arr.slice(-lines).join("\n");
        } finally {
            await fd.close();
        }
    } catch (e) {
        return `(log unreadable: ${e.message})`;
    }
}

async function readScreenAsBase64() {
    const buf = await fsp.readFile(SCREEN_PATH);
    return buf.toString("base64");
}

// ---------------- JSON-RPC ----------------

function jsonResult(id, result) {
    return JSON.stringify({ jsonrpc: "2.0", id, result });
}

function jsonError(id, code, message) {
    return JSON.stringify({ jsonrpc: "2.0", id, error: { code, message } });
}

function mcpText(text) {
    return { content: [{ type: "text", text }] };
}

async function handleCall(id, name, args) {
    const impl = TOOL_IMPL[name];
    if (!impl) throw Object.assign(new Error(`unknown tool: ${name}`), { code: -32601 });
    try {
        const result = await impl(args || {});
        log("tool ok:", name);
        return mcpText(typeof result === "string" ? result : JSON.stringify(result, null, 2));
    } catch (e) {
        log("tool err:", name, e.message);
        const err = new Error(`${name} failed: ${e.message}`);
        err.data = { tool: name, code: e.code || "TOOL_ERROR" };
        throw err;
    }
}

async function handleRequest(msg) {
    const { id, method, params } = msg;
    if (id === undefined) {
        // notification — nothing to answer; ignore initialized/cancelled etc.
        return null;
    }

    switch (method) {
        case "initialize":
            return jsonResult(id, {
                protocolVersion: PROTOCOL_VERSION,
                capabilities: { tools: {}, resources: {} },
                serverInfo: { name: SERVER_NAME, version: SERVER_VERSION },
            });

        case "ping":
            return jsonResult(id, {});

        case "tools/list":
            return jsonResult(id, { tools: TOOLS });

        case "tools/call": {
            try {
                const result = await handleCall(id, params?.name, params?.arguments);
                return jsonResult(id, { ...result, isError: false });
            } catch (e) {
                return jsonResult(id, {
                    content: [{ type: "text", text: e.message }],
                    isError: true,
                });
            }
        }

        case "resources/list":
            return jsonResult(id, {
                resources: [
                    { uri: "gakumas://state", name: "Game state", mimeType: "application/json", description: "Last state snapshot" },
                    { uri: "gakumas://layout", name: "Screen layout", mimeType: "application/json", description: "Last UI tree capture" },
                    { uri: "gakumas://screen", name: "Game screenshot", mimeType: "image/png", description: "Latest screenshot file" },
                    { uri: "gakumas://log", name: "Game log tail", mimeType: "text/plain", description: "Last 200 lines of BepInEx LogOutput.log" },
                ],
            });

        case "resources/read": {
            const uri = params?.uri;
            try {
                if (uri === "gakumas://state") {
                    const s = cache.state || (await toolState());
                    return jsonResult(id, { contents: [{ uri, mimeType: "application/json", text: JSON.stringify(s, null, 2) }] });
                }
                if (uri === "gakumas://layout") {
                    const l = cache.layout || (await toolLayout({}));
                    return jsonResult(id, { contents: [{ uri, mimeType: "application/json", text: JSON.stringify(l, null, 2) }] });
                }
                if (uri === "gakumas://screen") {
                    const b64 = await readScreenAsBase64();
                    return jsonResult(id, { contents: [{ uri, mimeType: "image/png", blob: b64 }] });
                }
                if (uri === "gakumas://log") {
                    const text = await readLogTail(200);
                    return jsonResult(id, { contents: [{ uri, mimeType: "text/plain", text }] });
                }
                throw Object.assign(new Error(`unknown resource: ${uri}`), { code: -32602 });
            } catch (e) {
                return jsonError(id, -32602, `resource read failed: ${e.message}`);
            }
        }

        default:
            return jsonError(id, -32601, `method not found: ${method}`);
    }
}

async function main() {
    if (!fs.existsSync(BEPINEX)) {
        log(`WARN: BepInEx dir not found: ${BEPINEX} (set GAKUMAS_BEPINEX)`);
    } else {
        log(`channel: ${CMD_PATH}`);
    }

    const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
    for await (const line of rl) {
        if (!line.trim()) continue;
        let msg;
        try {
            msg = JSON.parse(line);
        } catch {
            log("unparseable input line (ignored)");
            continue;
        }
        try {
            const out = await handleRequest(msg);
            if (out !== null) process.stdout.write(out + "\n");
        } catch (e) {
            log("handler error:", e.message);
            if (msg.id !== undefined) {
                process.stdout.write(jsonError(msg.id, -32603, `internal error: ${e.message}`) + "\n");
            }
        }
    }
}

async function callTool(name, args = {}) {
    const impl = TOOL_IMPL[name];
    if (!impl) {
        const err = new Error(`unknown tool: ${name}`);
        err.code = -32601;
        throw err;
    }
    return await impl(args);
}

if (require.main === module) {
    main().catch((e) => {
        log("fatal:", e.message);
        process.exit(1);
    });
}

module.exports = {
    callTool,
    handleCall,
    handleRequest,
    TOOL_IMPL,
    TOOLS,
    sendCommand,
};
