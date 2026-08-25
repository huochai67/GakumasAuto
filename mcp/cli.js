#!/usr/bin/env node
// File-channel CLI for the in-process plugin. Independent of the MCP stdio session.
//   node mcp/cli.js state
//   node mcp/cli.js account_state
//   node mcp/cli.js screenshot
//   node mcp/cli.js find HomeFooter
"use strict";

const fs = require("fs");
const fsp = require("fs/promises");
const path = require("path");

const BEPINEX = (process.env.GAKUMAS_BEPINEX || "E:/DMM/gakumas/BepInEx").replace(/[\\/]+$/, "");
const CMD_PATH = path.join(BEPINEX, "gakumas-ui-cmd.json");
const RESP_PATH = path.join(BEPINEX, "gakumas-ui-resp.json");
const TIMEOUT_MS = 10000;

function sleep(ms) {
    return new Promise((r) => setTimeout(r, ms));
}

function parseExtra(argv) {
    const extra = {};
    for (const a of argv) {
        const eq = a.indexOf("=");
        if (eq < 1) continue;
        const k = a.slice(0, eq);
        let v = a.slice(eq + 1);
        if (v === "true") v = true;
        else if (v === "false") v = false;
        else if (v !== "" && !Number.isNaN(Number(v))) v = Number(v);
        extra[k] = v;
    }
    return extra;
}

async function send(action, extra) {
    const id = `cli-${Date.now()}-${process.pid}`;
    const tmp = `${CMD_PATH}.tmp-${process.pid}`;
    await fsp.writeFile(tmp, JSON.stringify({ id, action, ...extra }), "utf8");
    await fsp.rename(tmp, CMD_PATH);

    const deadline = Date.now() + TIMEOUT_MS;
    while (Date.now() < deadline) {
        try {
            const raw = await fsp.readFile(RESP_PATH, "utf8");
            const resp = JSON.parse(raw);
            if (resp && resp.id === id) return resp;
        } catch { /* not ready */ }
        await sleep(120);
    }
    throw new Error(`timeout waiting for "${action}" (${TIMEOUT_MS}ms)`);
}

async function main() {
    const action = process.argv[2];
    if (!action) {
        process.stderr.write("usage: node mcp/cli.js <action> [k=v ...]\n");
        process.exit(2);
    }
    const extra = parseExtra(process.argv.slice(3));
    if (process.argv[3] && !process.argv[3].includes("=") && extra.path === undefined) {
        extra.path = process.argv[3];
    }
    const resp = await send(action, extra);
    const out = resp.result !== undefined ? resp.result : resp;
    process.stdout.write(typeof out === "string" ? out + "\n" : JSON.stringify(out, null, 2) + "\n");
    if (!resp.ok) process.exit(1);
}

main().catch((e) => {
    process.stderr.write(e.message + "\n");
    process.exit(1);
});
