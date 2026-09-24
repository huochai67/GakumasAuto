# GakumasAuto

面向《学园偶像大师》（学マス / Gakumas）的进程内自动化与 MCP（Model Context Protocol）桥接工具链。让 AI Agent（Claude Code、Roo Code、Cursor 等）或外部自动化脚本通过标准 MCP 协议直接感知与操作游戏。
> [!NOTE]
> **项目状态说明**：本项目目前已停止主动维护与后续开发。仓库代码、逆向工具链与 MCP 协议实现保持归档状态，供技术交流与学习参考。非常欢迎社区开发者自行 **Fork** 并继续二次开发或演进！

```
┌──────────────────────────────────────────────────────────────────┐
│             AI Agent (Claude Code / Cursor / Harness)            │
└────────────────────────────────┬─────────────────────────────────┘
                                 │ stdio JSON-RPC 2.0 (MCP)
                                 ▼
┌──────────────────────────────────────────────────────────────────┐
│  mcp/server.js (Node.js 零依赖 MCP 服务, 57 Tools + 4 Resources)  │
└────────────────────────────────┬─────────────────────────────────┘
                                 │ 本地文件通道 (1Hz 轮询, 零网络面, tmp+rename 原子读写)
                                 ▼
┌──────────────────────────────────────────────────────────────────┐
│  plugin/GakumasAuto.dll (BepInEx 6 / Il2CppInterop 进程内插件)     │
└────────────────────────────────┬─────────────────────────────────┘
                                 │ 内存直读 & Presenter / UI API 直调
                                 ▼
┌──────────────────────────────────────────────────────────────────┐
│  gakumas.exe (Unity 6000.0.77f1, IL2CPP 64-bit)                  │
└──────────────────────────────────────────────────────────────────┘
```

---

## 核心特性

- **非侵入式设计**：不篡改游戏本体二进制与资产文件，不修改反作弊驱动，以独立 DLL 形式运行于 BepInEx 插件层。
- **纯本地零网络面**：MCP 服务器与游戏插件之间通过本地临时文件进行原子化指令交互（`gakumas-ui-cmd.json` / `gakumas-ui-resp.json`），无对外开放的 HTTP / WebSocket 端口。
- **完善的分级工具集（57 Tools + 4 Resources）**：
  - **L0 感知**：游戏状态、UI 树全量/紧凑解析、节点搜索、内存截屏、按钮绑定诊断。
  - **L1 动作**：绝对坐标点击、节点路径模拟、回调直调、ADV 剧情快进与自动选支。
  - **L2 数据**：账号资产、背包道具、任务进度、礼物列表、社团状态、竞技场排位、培育状态与日程、支援卡列表、考试手牌与牌堆全览。
  - **L3 任务**：界面路由跳转、日常外出与活动费、商店/兑换所购买、竞技场挑战与自动编成、社团求助/捐赠、硬币扭蛋、支援卡强化、考试打牌。
- **全套逆向与 Interop 工具链**：针对 packed `GameAssembly.dll` 的离线多阶段解密/PE 重建工具（`ga-static-decrypt`），以及把 interop 生成搬进游戏进程的 doorstop shim（`doorstop-shim`：现算 codereg 常量并交给 BepInEx 自身管线，游戏更新后无需离线生成器）。
- **开箱即用的 Agent 技能体系**：内置 `.agent/skills/` 自动化技能，涵盖日常签到、考试打牌、竞技场、社团公会、硬币扭蛋等常用业务。

---

## 仓库结构

```text
GakumasAuto/
├── plugin/                     # BepInEx 6 / Il2CppInterop 游戏内插件源码
│   ├── GakumasAutoPlugin.cs    # 插件入口 BasePlugin，挂载 AutoDriver，共享 DTO 结果字段
│   ├── AutoDriver.cs           # MonoBehaviour：热键监听、1Hz 指令轮询与任务分发
│   ├── Dtos.cs                 # 前后端交互的数据传输对象定义
│   ├── GakumasAuto.csproj      # 插件 C# 项目文件（Target net6.0）
│   ├── Core/                   # 核心底层：GameState (状态识别), PresenterUtil (UI 查找)
│   ├── Ui/                     # UI 操作：Layout (UI树解析), Input (点击与输入), Screenshot (截屏)
│   └── Features/               # 业务模块：Account, Daily, Shop, Exchange, Exam, Produce,
│                               #           Arena, Club, Capsule, SupportCard, Gift, Mission, Adv, Navigate
├── mcp/                        # MCP 服务端与调试工具
│   ├── server.js               # MCP 服务器实现（57 个 Tools + 4 个 Resources）
│   └── cli.js                  # 命令行直连调试工具（绕过 MCP 协议，直接向文件通道发指令）
├── tools/                      # 逆向、interop 与部署工具链
│   ├── ga-static-decrypt/      # packed GameAssembly 静态解密与 PE 重建工具
│   ├── doorstop-shim/          # 进程内 interop 生成：注入 codereg 常量后交给 BepInEx（含 install.ps1）
│   └── deploy-plugin.ps1       # 插件安全部署脚本（仅复制 DLL，不改动游戏基础环境）
├── docs/                       # 参考文档
│   ├── DEPLOYMENT.md           # 部署、验证、更新与回滚流程
│   ├── PLUGIN-DEV-GUIDE.md     # 插件开发与 IL2CPP 避坑指南
│   ├── IMAGE-CONFORM-ACCEPTANCE.md # 镜像规范化验收判据与实测记录
│   └── BepInEx.cfg.example     # BepInEx 推荐配置基线
├── .agent/skills/              # 面向 AI Agent 的业务技能定义（Daily, Produce, Contest 等）
├── Directory.Build.props.example # 本地构建路径配置示例
└── .mcp.example.json           # MCP 客户端配置示例
```

---

## 快速开始

### 1. 环境准备

- **操作系统**：Windows 10 / 11 (x64)
- **运行环境**：
  - [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（用于编译插件与工具链）
  - [Node.js](https://nodejs.org/) v18+（用于运行 MCP 服务与 CLI）
  - 已安装 BepInEx 6 (Bleeding Edge 6.0.0-be.785+, Unity IL2CPP x64) 的 DMM 游戏客户端

### 2. 配置与编译

1. **设置本地游戏路径**：
   复制 `Directory.Build.props.example` 为 `Directory.Build.props`，并修改其中的 `GakumasRoot`：
   ```xml
   <Project>
     <PropertyGroup>
       <GakumasRoot>X:\path\to\gakumas</GakumasRoot>
     </PropertyGroup>
   </Project>
   ```

2. **编译插件**：
   ```powershell
   dotnet build plugin/GakumasAuto.csproj -c Release
   ```

3. **部署插件**：
   使用提供的部署脚本将生成的 DLL 安装到游戏的 `BepInEx\plugins` 目录：
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\deploy-plugin.ps1 `
     -TargetDir 'X:\path\to\gakumas' `
     -PluginPath '.\plugin\bin\Release\net6.0\GakumasAuto.dll'
   ```

### 3. 配置 MCP 客户端

在你的 MCP 宿主（例如 Claude Code、Cursor、Roo Code）中添加服务配置。
复制 `.mcp.example.json` 为 `.mcp.json` 并修改环境变量：

```json
{
  "mcpServers": {
    "gakumas": {
      "type": "stdio",
      "command": "node",
      "args": ["E:/code/GakumasAuto/mcp/server.js"],
      "env": {
        "GAKUMAS_BEPINEX": "X:/path/to/gakumas/BepInEx"
      }
    }
  }
}
```

> **注意**：修改 `mcp/server.js` 后需重启 MCP 客户端/Agent 进程生效。

### 4. 启动游戏并验证

> **安全提示**：请在普通终端或系统运行框中启动游戏，不要在 Agent 终端的子进程中通过 `Start-Process` 启动，以避免被宿主进程树回收。

```powershell
# 在独立终端启动游戏（参数从 DMM 客户端启动链接中获取）
X:\path\to\gakumas\gakumas.exe /viewer_id=<user_id> /open_id=<open_id> /pf_access_token=<token>
```

游戏启动后，检查 `BepInEx\LogOutput.log`：
```text
[Info   :   Gakumas Auto] GakumasAuto v2.3.0 loaded. F8/F9 = ADV, F10 = layout capture
[Info   :   Gakumas Auto] AutoDriver attached; overlay AUTOMATION (top-left)
```

---

## 命令行直连调试（CLI）

无需通过 MCP 客户端，可使用 `mcp/cli.js` 直接向游戏发送命令验证通路：

```powershell
# 查看游戏当前状态（界面、用户、Loading）
node mcp/cli.js state

# 查看当前账号资产与体力
node mcp/cli.js account

# 截取当前画面（保存为 BepInEx/gakumas-screen.png）
node mcp/cli.js screenshot

# 查找包含指定名称的 UI 节点
node mcp/cli.js find pattern=HomeFooter

# 模拟点击主页底部按钮
node mcp/cli.js invoke_callback "path=HomeFooter/UIContentArea/FrontRoot/ButtonRoot/Home"

# 快捷返回主界面
node mcp/cli.js go_home
```

### 游戏内热键

| 快捷键 | 功能 | 说明 |
|---|---|---|
| **F8** | ADV 推进 | 模拟点击当前剧情文本框，推进到下一句 |
| **F9** | ADV 快进 | 切换剧情快进模式（Fast Forward） |
| **F10** | UI 树转储 | 立即捕获当前 UI 布局并写入 `BepInEx\gakumas-layout.json` |

---

## MCP 工具与资源参考

工具按抽象层级划分为 **L0 ~ L3**，AI Agent 在执行任务时应优先调用高层（L3）任务，失败或遇到特殊弹窗时降级使用底层（L0/L1）原子操作。

### L0: 感知层 (Perception)

| 工具名称 | 参数 | 说明 |
|---|---|---|
| `state` | — | 读取游戏实时状态：用户 ID/昵称、当前界面识别（`screen`）、顶层弹窗（`topLayer`）、Loading 与维护状态 |
| `layout` | `includeInactive?: bool` | 获取当前 UI 树结构（最多 3000 节点 / 深度 12，含屏幕坐标、尺寸、文本与激活状态） |
| `layout2` | `includeInactive?: bool` | 获取紧凑型完整 UI 树文本（无节点/深度上限，适合长列表/深层界面解析） |
| `find` | `pattern: string` | 按子串模糊搜索节点（全 Canvas 范围，最多返回 300 个匹配节点及其路径） |
| `screenshot` | — | 游戏内调用 `ScreenCapture` 捕获画面为 PNG（返回保存路径与尺寸） |
| `debug_button` | `path: string` | 检查指定按钮节点的交互状态与回调函数绑定情况 |

### L1: 动作层 (Action)

| 工具名称 | 参数 | 说明 |
|---|---|---|
| `tap_path` | `path: string` | 查找指定节点并模拟完整的 PointerDown -> PointerUp -> PointerClick 序列 |
| `click_path` | `path: string` | 触发受手势门控的按钮点击（`CampusButton.OnClicked` 或 `uGUI.onClick`） |
| `invoke_callback`| `path: string` | 直接触发节点上绑定的点击回调（绕过手势检测，标题画面等不可穿透场景必备） |
| `tap_at` | `x: number, y: number` | 在屏幕指定绝对像素坐标发射射线检测并触发点击 |
| `adv` | `op: string, value?: bool` | 剧情 ADV 流程控制（`end_wait`: 推进对话; `set_ff`: 开关快进; `select_unselected`: 自动选分支） |

### L2: 数据与状态查询 (Data / Read)

直接读取游戏内存管理器或 Presenter，大多数无需切换特定 UI 界面：

| 工具名称 | 参数 | 说明 |
|---|---|---|
| `wait_until` | `cond: string, timeout_ms?: number` | 轮询等待状态变化（`loading_false` / `loading_true` / `layer_changed` / `state_changed` / `logged_in`） |
| `account` | — | 读取当前账号数据：等级、经验、粉丝数、金币、钻石、AP/CP |
| `item_list` | — | 直读 `UserItemList` 背包道具（ID、名称、类别、数量、到期时间）及金币总额 |
| `daily_state` | — | 读取日常状态：外出工作状态（mini live / live streaming）、未领活动费、钻石余额 |
| `shop_list` | — | 获取当前商店商品列表（价格、限购量、售罄与锁定标记，自动打开商店） |
| `exchange_list` | — | 获取兑换所总览与当前激活的兑换所 ID/名称 |
| `exchange_items`| — | 读取当前兑换所标签下的商品列表（含兑换次数、推荐标记） |
| `mission_list` | `category?: string` | 读取各分类任务进度与状态（`Daily` / `Weekly` / `Normal` / `Special` / `Event` / `Achievement` / `MainTask`） |
| `gift_list` | — | 打开礼物箱并读取待领取礼物列表 |
| `pvp_state` | — | 读取竞技场排位、剩余挑战次数、Rate 评分及当前候选对手列表 |
| `club_state` | — | 读取社团状态：求助状态（可发起/求助中/可领取）、剩余捐赠次数、社团成员列表 |
| `capsule_state` | — | 读取硬币扭蛋机状态（`friend` / `sense` / `logic` / `anomaly`）及锁定状态 |
| `support_list` | — | 直读 `UserSupportCardList` 持有支援卡列表（ID、名称、等级、上限） |
| `produce_state` | — | 读取当前培育进度：偶像、类型、P点、体力、周数/日程、当天可选步骤（考试/外出/商店/特别指导/课程） |
| `produce_schedule` | — | 获取当前培育 run 的完整日程计划日历 |
| `produce_shop` | — | 获取培育中商店商品（卡牌、饮品、道具、强化状态） |
| `produce_outing`| — | 获取培育中外出选项及体力/P点恢复预期 |
| `produce_cards` | — | 获取培育牌组卡牌列表与特别指导强化状态 |
| `exam_hand` | — | 获取考试/竞技场手牌、元气/好印象/干劲状态及推荐出牌索引（`recommendIndex`） |
| `exam_deck` | — | 获取考试卡组底牌、弃牌堆、除外区、预抽牌队列（`futureDeck`）及当前打出卡 |

### L3: 业务流程与任务 (Task / Write)

| 工具名称 | 参数 | 说明 |
|---|---|---|
| `navigate` | `target: string` | 切换底部主导航栏（`story` / `card` / `home` / `pvp` / `gasha`） |
| `go_home` | — | 快捷返回游戏主页（等价于 `navigate target=home`） |
| `screen_goto` | `screen: string` | 调用 `OutGameTransitionUtility.To` 跳转界面（支持别名 `shop`/`present`/`produce` 或 `Campus.ScreenState`） |
| `daily_set_outing` | `work, character?, hours?, confirm` | 派遣日常外出工作（支持 dry run 预览，需 `confirm: true` 实际执行） |
| `daily_finish_outing` | `work?` | 结算已完成的外出工作并点击结果确认 |
| `daily_collect_money` | — | 打开并一键领取主页活动费（金币） |
| `shop_enter` | `target?: string` | 进入指定商店（`jewel` / `exchange` / `item` / `daily`） |
| `shop_buy_item` | `item_id?, name?, confirm` | 购买普通商店商品（需 `confirm: true` 防止意外消耗资产） |
| `exchange_enter` | `type, exchange_id?` | 进入指定兑换所界面（`type: item\|daily\|event`） |
| `exchange_buy` | `item_id?, name?, type?, confirm` | 购买兑换所商品（需 `confirm: true`） |
| `gift_receive` | `gift_id?, confirm` | 领取礼物（省略 `gift_id` 则执行一键全领，需 `confirm: true`） |
| `mission_receive` | — | 打开任务面板并一键领取已完成任务奖励 |
| `pvp_enter` | — | 进入竞技场 Rate 顶层界面 |
| `pvp_challenge` | `rival: string, confirm` | 挑战指定竞技场对手（`high` / `middle` / `low`，需 `confirm: true`） |
| `pvp_auto_set` | — | 进入编成界面并执行自动编队 |
| `club_enter` | — | 进入社团主界面 |
| `club_receive` | — | 领取已完成的社团笔记请求奖励 |
| `club_request` | `confirm` | 发起新的社团笔记求助（需 `confirm: true`） |
| `club_donate` | `confirm` | 向社团成员捐赠笔记并翻至下一位（需 `confirm: true`） |
| `capsule_enter` | — | 打开硬币扭蛋机界面 |
| `capsule_draw` | `kind, confirm` | 抽取硬币扭蛋（`friend` / `sense` / `logic` / `anomaly`，需 `confirm: true`） |
| `support_enter` | — | 打开支援卡列表界面 |
| `support_upgrade` | `confirm` | 自动强化当前持有中最低等级的一张支援卡（需 `confirm: true`） |
| `produce_enter` | — | 进入偶像培育选择顶层界面 |
| `exam_start` | — | 跳过考试/竞技场演出动画，直接进入出牌阶段 |
| `exam_play` | `index?: number` | 打出指定手牌（省略或 `-1` 时打出游戏官方算法推荐的手牌） |

### MCP Resources

MCP 客户端可以通过标准 URI 读取实时快照：

- `gakumas://state`：最新游戏状态快照（JSON）
- `gakumas://layout`：最新全屏 UI 树结构（JSON）
- `gakumas://screen`：最新游戏截屏（`image/png`）
- `gakumas://log`：BepInEx `LogOutput.log` 尾部 200 行实时日志（`text/plain`）

---

## Agent 技能体系

在 `.agent/skills/` 中封装了高频复合业务的标准工作流：

- **`gakumas-daily`**：完整的每日 routine（收取活动费、结算与派发出发外出、领取礼物箱、清空每日金币/AP商店、打竞技场、社团领取与捐赠、硬币扭蛋、升级最低级支援卡、领取每日/每周任务）。
- **`gakumas-produce`**：偶像培育工作流（自动选择最佳日程、培育商店选购、外出恢复体力、特别指导卡牌强化、考试自动打牌、跳过 ADV 演出）。
- **`gakumas-contest`**：竞技场 PvP 流程（进入、未编成时自动编队、选择指定对手挑战、跳过战斗演出、收集报酬）。
- **`gakumas-shop`**：每日金币/AP 道具兑换与每周免费礼包自动领购。
- **`gakumas-club`**：社团日常（笔记报酬领取、发起新笔记请求、向社员送礼）。
- **`gakumas-capsule`**：硬币扭蛋机自动抽取。
- **`gakumas-support`**：自动检索并升级当前最低等级的支援卡。

---

## 逆向与 Interop 工具链

当游戏版本更新导致 `GameAssembly.dll` 与 IL2CPP 元数据变更时，可使用 `tools/` 中的工具链重新生成 `BepInEx\interop` 绑定程序集：

```text
packed GameAssembly.dll (游戏盘上原始二进制)
       │
       ▼ tools/ga-static-decrypt (需匹配版本的逆向 profile)
GameAssembly_static_exact.dll (重建的标准 PE 分析镜像)
       │
       ▼ tools/doorstop-shim (进程内注入 codereg 常量、指定生成输入)
BepInEx\interop\*.dll (由 BepInEx 自身管线运行时生成)
```

> `ga-static-decrypt` 为 Python 实现（Windows x64 + Python 3.9+）：密钥可自动扫描（`--dump <加载器工作区 dump>`）
> 或手工/`--carve`/`--profile` 指定，逐项来源与门禁见该目录 README。

1. **解密与 PE 重建**（`tools/ga-static-decrypt`，Python 实现）：
   ```powershell
   python tools\ga-static-decrypt\ga_static_decrypt.py `
     "X:\path\to\gakumas\GameAssembly.dll" `
     "tools\ga-static-decrypt\out\GameAssembly_static_exact.dll"
   # 密钥默认自动扫描（记录表/pass3/key/helper/payload）；手工输入与 profile 见该目录 README
   ```
2. **部署 shim 并接管 interop 生成**（`tools/doorstop-shim`）：
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\doorstop-shim\install.ps1 `
     -ImagePath tools\ga-static-decrypt\out\GameAssembly_static_exact.dll -EnableInteropUpdate
   ```
   之后 BepInEx 每次启动自己判定 hash：仅当镜像 / `unity-libs` / 生成器版本变化时才重新生成（首次约 83 s）。

> 镜像规范化的验收判据（AC-1…AC-8）与 2026-09-24 实测记录见 `docs/IMAGE-CONFORM-ACCEPTANCE.md`。

详细逆向约束与哈希校验机制参见各工具目录下的 `README.md`。

---

## 插件开发规范与 IL2CPP 避坑指南

修改或扩展 `plugin/` 源码时必须严格遵守以下约束（详见 `docs/PLUGIN-DEV-GUIDE.md`）：

1. **禁止在 MonoBehaviour 方法签名中暴露托管类型**：
   在 Il2CppInterop 环境下，MonoBehaviour 的方法若带有自定义托管类（DTO、`List<T>`、`StringBuilder`）作为参数或返回值，会被生成为无法正常调用的 substitute 类型导致崩溃。所有指令的响应结果必须路由到 `GakumasAutoPlugin.Shared*` 静态字段中统一序列化。
2. **`RectTransform` 类型转换**：
   IL2CPP 对象不能使用 `transform as RectTransform`（永远返回 `null`），必须使用 `transform.GetComponent<RectTransform>()`。
3. **UniRx / UniTask 依赖限制**：
   严禁给插件工程添加 UniRx / UniTask 程序集引用。BepInEx 加载期解析这些程序集会导致游戏闪退；异步与延时逻辑统一使用 Unity 原生协程或轮询计数器。
4. **指令通道原子写入规范**：
   外部向 `gakumas-ui-cmd.json` 写入指令时，必须先写入 `.tmp` 临时文件再执行原子 `rename`，避免插件在 1Hz 轮询中读到未写完的截断内容。

---

## 安全与免责声明

- **敏感信息防护**：严禁将包含个人凭证的 `.mcp.json`、`Directory.Build.props`、登录令牌（`viewer_id` / `open_id` / `pf_access_token`）提交至公开仓库。
- **风险提示**：本项目为第三方研究与自动化实验工具，通过进程内注入执行操作。虽未直接篡改游戏原始资产，但使用者需自行承担因自动化操作带来的潜在封号风险。
