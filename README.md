# GakumasAuto

《学园偶像大师》(gakumas) 进程内自动化工具链：让 AI agent（或脚本）通过 MCP 协议直接操作游戏。

```
agent (harness / Claude Code / 任意 MCP 客户端)
   │  stdio JSON-RPC 2.0 (2024-11-05)
   ▼
mcp/server.js            ← 零依赖 Node MCP 服务器
   │  文件指令通道（gakumas-ui-cmd.json → -resp.json，1Hz 轮询，无网络面）
   ▼
plugin/GakumasAuto.dll   ← BepInEx 6 / Il2CppInterop 进程内插件（v2.2.1）
   │  直调游戏 API
   ▼
gakumas.exe (Unity 6000.0.77f1, il2cpp)
```

- 不修改任何游戏原始文件（仅向 `BepInEx\plugins\` 新增 DLL）；不碰反作弊驱动；插件本身无网络通信。
- **改完 `mcp/server.js` 后必须重启 MCP / agent**，stdio 进程不会热加载。会话中途可用 `mcp/cli.js` 走同一文件通道。
- 封号风险使用者自担。

## 发布注意

不要提交：`.mcp.json`、本机 `Directory.Build.props`、登录参数（`viewer_id` / `open_id` / `pf_access_token`）、`plugin/bin`、`gakumas-bepinex-kit/game-root`、`gakumas-bepinex-kit/BepInEx/core`、`gakumas-bepinex-kit/BepInEx/interop`、`gakumas-bepinex-kit/BepInEx/unity-libs` 和插件 DLL。公开部署只使用用户自行安装的 BepInEx；复制 `.mcp.example.json` / `Directory.Build.props.example` 后本地改路径。

## 目录

| 路径 | 内容 |
|---|---|
| `plugin/GakumasAutoPlugin.cs` | BepInPlugin 入口 + Shared* 结果字段 |
| `plugin/AutoDriver.cs` | MonoBehaviour：热键、轮询、命令分发 |
| `plugin/Dtos.cs` | 全部响应 DTO |
| `plugin/Core/GameState.cs` | state / 界面识别 / 登录监视 |
| `plugin/Ui/` | layout、find、点击、截屏 |
| `plugin/Features/` | 按功能：Account / Daily / Shop / Exchange / Exam / Mission / Gift / Arena / Produce / Adv / Navigate |
| `plugin/GakumasAuto.csproj` | 插件离线编译（引用本机 `BepInEx\interop\*`） |
| `mcp/server.js` | MCP 服务器（38 tools + 4 resources） |
| `mcp/cli.js` | 文件通道 CLI：`node mcp/cli.js state` |
| `tools/ga-static-decrypt/` | packed GameAssembly 的版本绑定离线解密 / PE 重建工具 |
| `tools/interop-gen/` | 离线 Il2CppInterop 生成器 |
| `gakumas-bepinex-kit/` | 不捆绑二进制的部署脚本、配置示例、历史插件样例和开发手册 |

## 文档导航

| 文档 | 用途 |
|---|---|
| `README.md` | 当前仓库的构建、部署、MCP 和工具链总览 |
| `tools/ga-static-decrypt/README.md` | packed GameAssembly 解密、profile 和版本约束 |
| `tools/interop-gen/README.md` | 从重建 PE 生成 interop 的完整命令 |
| `gakumas-bepinex-kit/README.md` | BepInEx 部署快照的安装、验证和回滚 |
| `gakumas-bepinex-kit/docs/PLUGIN-DEV-GUIDE.md` | BepInEx 6 / Il2CppInterop 插件开发和 MCP 通道 |
| `.agent/skills/gakumas-daily/SKILL.md` | agent 执行日常任务时的操作顺序和安全边界 |

## 从 packed GameAssembly 生成 interop

盘上的 `GameAssembly.dll` 是 packed 文件，不能直接交给 `GakumasInteropGen`。完整流程如下：

```text
packed GameAssembly.dll
  ↓ tools/ga-static-decrypt（需要匹配版本 profile）
GameAssembly_static_exact.dll
  ↓ tools/interop-gen（需要 metadata + BepInEx unity-libs）
BepInEx\interop\*.dll
```

解密 profile 是版本绑定的游戏派生数据，不随仓库发布。工具会在调用 native helper 前校验文件尺寸和 SHA-256；缺失或版本不匹配时应停止，不要关闭校验。详细输入和命令见两个工具目录的 README。

## 部署

1. 编译插件并部署（游戏退出后才能覆盖 DLL）：
   ```powershell
   & 'C:\Program Files\dotnet\dotnet.exe' build plugin\GakumasAuto.csproj -c Release
   Copy-Item plugin\bin\Release\net6.0\GakumasAuto.dll E:\DMM\gakumas\BepInEx\plugins\GakumasAuto.dll -Force
   ```
2. 启动游戏：在**普通提权 CMD / 运行框**里直接跑（不要从本仓库的 agent 终端 `Start-Process`，子进程会被 Job Object 一起杀掉）：
   ```
   E:\DMM\gakumas\gakumas.exe /viewer_id=<v> /open_id=<o> /pf_access_token=<t>
   ```
3. 复制 `.mcp.example.json` 为 `.mcp.json`，把 `GAKUMAS_BEPINEX` 改成你的游戏 `BepInEx` 目录。不要把填好本机路径的 `.mcp.json` 提交进仓库。

## 不经 MCP 的冒烟

```powershell
node mcp/cli.js state
node mcp/cli.js account_state
node mcp/cli.js screenshot
node mcp/cli.js find path=HomeFooter
node mcp/cli.js invoke_callback "path=HomeFooter/UIContentArea/FrontRoot/ButtonRoot/Home"
```

## 工具分级

上层由下层组装；agent 优先用最高可用层，失败时降级。

### L0 感知

| 工具 | 说明 |
|---|---|
| `state` | user id/昵称、`screen`（home/shop/present/mission/produce/exam/pvp…）、topLayer、loading |
| `layout` / `layout2` / `find` / `screenshot` / `debug_button` | 同前 |

### L1 动作

`tap_path` / `click_path` / `invoke_callback` / `tap_at` / `adv`

path 含 `/` 按全路径后缀匹配；裸名先精确后子串。

### L2 数据

| 工具 | 说明 |
|---|---|
| `wait_until` | 服务端轮询 state |
| `account` | 等级/经验/粉丝/金币/钻石/AP |
| `item_list` | 背包道具（id/名称/类型/数量） |
| `daily_state` | 外出状态 + 未收活动费 + 钻石 |
| `mission_list` | 任务进度（可按 Daily/Weekly/… 过滤） |
| `gift_list` | 礼物箱（未开界面会先 `gift_enter`） |
| `pvp_state` | 竞技场剩余次数/段位/排名/rate；PvP 顶栏打开时尝试读对手 |
| `produce_state` / `produce_schedule` | 当前/最近培育：类型、角色、P 点、体力、步骤种类、日程 |
| `produce_shop` / `produce_outing` / `produce_cards` | 培育商店商品、外出选项、卡牌强化状态 |
| `shop_list` / `exchange_list` / `exam_hand` / `exam_deck` | 同前 |

### L3 任务

| 工具 | 说明 |
|---|---|
| `navigate` / `go_home` | 主页页签 story/card/home/pvp/gasha |
| `screen_goto` | `OutGameTransitionUtility.To`：home/shop/jewel/exchange/present/mission/work/produce/pvp 或 `Campus.ScreenState` 名 |
| `daily_set_outing` / `daily_finish_outing` / `daily_collect_money` | 外出与活动费 |
| `shop_enter` | `target=jewel\|exchange\|item\|daily` |
| `shop_buy_item` | 强制 `confirm:true` |
| `exchange_enter` | item/daily/event |
| `gift_receive` | 强制 `confirm:true`；省略 `gift_id` 则一括领取 |
| `mission_receive` | 开任务界面并点领取 |
| `pvp_enter` / `pvp_challenge` | 进竞技场；挑战 high/middle/low（`confirm:true`） |
| `produce_enter` | 进培育顶栏 |
| `exam_start` | 跳过考试开场转场 |

## 插件硬限制（改 DLL 必读）

1. MonoBehaviour 方法禁止托管类型签名（DTO/List/StringBuilder）；结果走 `GakumasAutoPlugin.Shared*`。
2. `as RectTransform` 恒 null，必须 `GetComponent<RectTransform>()`。
3. 文件通道：共享锁 + 客户端 tmp+rename。
4. `ScreenLayerManager` 在标题/主界面常报 `(none)`；`state.screen` 靠 presenter / 节点名识别。
5. `layout` 500/12；`layout2` 8000/32；`find` ≤300。
6. 不要给插件加 UniRx/UniTask 程序集引用——BepInEx 加载期会解析失败导致闪退。

## 编译

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build plugin\GakumasAuto.csproj -c Release
```

解密器和 interop 生成器使用 .NET 8 SDK；它们引用本机游戏安装中的 BepInEx 程序集：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build tools\ga-static-decrypt\ga-static-decrypt.csproj -c Release
& 'C:\Program Files\dotnet\dotnet.exe' build tools\interop-gen\GakumasInteropGen.csproj -c Release
# 默认游戏目录：E:\DMM\gakumas；其他目录设置 $env:GAKUMAS_ROOT
```

## 版本

| 组件 | 版本 | 说明 |
|---|---|---|
| GakumasAuto.dll | 2.2.1 | 拆分多文件；account/item/gift/pvp/produce；produce_shop/outing/cards；pvp_challenge |
| server.js | 0.4.0 | 38 tools + 4 resources |
| cli.js | — | 文件通道直连，不依赖 MCP 进程 |
| 目标环境 | BepInEx 6.0.0-be.785 / Unity 6000.0.77f1 / gakumas il2cpp | 游戏更新用 `tools/interop-gen/` 重跑 |
