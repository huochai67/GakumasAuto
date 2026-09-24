#!/usr/bin/env node
// Direct Node.js runner for MCP tools in GakumasAuto.
// Usage:
//   node mcp/tool.js                     (list all registered tools)
//   node mcp/tool.js <tool_name>         (call with empty args)
//   node mcp/tool.js <tool_name> key=val flag=true num=123
//   node mcp/tool.js <tool_name> '{"key": "value"}'
"use strict";
const { callTool, TOOLS } = require("./server");

const PRIMARY_ARG = {
    screen_goto: "screen",
    find: "pattern",
    click_path: "path",
    tap_path: "path",
    invoke_callback: "path",
    debug_button: "path",
    pvp_challenge: "rival",
    capsule_draw: "kind",
    mission_list: "category",
    navigate: "target",
    adv: "op",
    gift_receive: "gift_id",
    exchange_enter: "type",
    shop_enter: "target",
    exam_play: "index",
};

function parseArgs(toolName, argv) {
    if (argv.length === 0) return {};
    if (argv.length === 1 && argv[0].startsWith("{") && argv[0].endsWith("}")) {
        try {
            return JSON.parse(argv[0]);
        } catch (e) {
            throw new Error(`Invalid JSON argument: ${e.message}`);
        }
    }

    const extra = {};
    const positional = [];
    for (const a of argv) {
        const eq = a.indexOf("=");
        if (eq < 1) {
            positional.push(a);
            continue;
        }
        const k = a.slice(0, eq);
        let v = a.slice(eq + 1);
        if (v === "true") v = true;
        else if (v === "false") v = false;
        else if (v !== "" && !Number.isNaN(Number(v))) v = Number(v);
        extra[k] = v;
    }

    if (positional.length > 0 && PRIMARY_ARG[toolName] && extra[PRIMARY_ARG[toolName]] === undefined) {
        let val = positional[0];
        if (val === "true") val = true;
        else if (val === "false") val = false;
        else if (val !== "" && !Number.isNaN(Number(val))) val = Number(val);
        extra[PRIMARY_ARG[toolName]] = val;
    }
    return extra;
}

async function main() {
    const name = process.argv[2];
    if (!name || name === "--help" || name === "-h") {
        process.stdout.write("GakumasAuto MCP Tool Runner\n\nUsage:\n");
        process.stdout.write("  node mcp/tool.js <tool_name> [key=value ...] [json_string]\n\nAvailable tools:\n");
        for (const t of TOOLS) {
            process.stdout.write(`  - ${t.name.padEnd(24)} ${t.description.split("\n")[0]}\n`);
        }
        return;
    }

    const args = parseArgs(name, process.argv.slice(3));
    try {
        const result = await callTool(name, args);
        if (result === undefined || result === null) {
            process.stdout.write("(empty result)\n");
        } else if (typeof result === "string") {
            process.stdout.write(result + "\n");
        } else {
            process.stdout.write(JSON.stringify(result, null, 2) + "\n");
        }
    } catch (e) {
        process.stderr.write(`[ERROR] ${name}: ${e.message}\n`);
        if (e.code) process.stderr.write(`Code: ${e.code}\n`);
        process.exit(1);
    }
}

main();
