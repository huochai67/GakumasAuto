# gakumas 插件开发手册（BepInEx 6 / Il2CppInterop）

> 适用：Unity 6000.0.77f1 IL2CPP + BepInEx 6.0.0-be.785 + 离线 interop（部署快照中的插件为 v2.0.2；仓库根目录当前插件为 v2.2.1）
> 前置：`deploy.ps1` 已部署并验证；插件使用 .NET 6 SDK，解密器和 interop 生成器使用 .NET 8 SDK

## 1. 架构总览

```
gakumas.exe (IL2CPP, 壳启动器自解压)
  └─ winhttp.dll (doorstop preloader 4.5.0, 游戏目录优先加载)
       └─ doorstop_config.ini → target_assembly = BepInEx\core\BepInEx.Unity.IL2CPP.dll
            └─ BepInEx chainloader → plugins\*.dll
                 └─ 插件 Load() → AddComponent<T>() 注入 Unity MonoBehaviour
                      └─ Update() 循环 → 热键 / 观察器 / 文件指令通道
```

关键点：
- **游戏原始文件零改动**；全部新增物在游戏根 3 个文件 + `BepInEx\` 目录
- 反作弊驱动只拦跨进程 RPM，进程内注入不受影响
- interop 程序集是**离线预生成**的（`UpdateInteropAssemblies = false`），编译插件时直接引用 `BepInEx\interop\*.dll` 即可获得游戏全部类型

## 2. 插件骨架（最小可跑）

### 2.1 项目文件（csproj）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <AssemblyName>MyPlugin</AssemblyName>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion>10.0</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <!-- BepInEx 运行时（必引） -->
    <Reference Include="BepInEx.Core">
      <HintPath>E:\DMM\gakumas\BepInEx\core\BepInEx.Core.dll</HintPath>
    </Reference>
    <Reference Include="BepInEx.Unity.IL2CPP">
      <HintPath>E:\DMM\gakumas\BepInEx\core\BepInEx.Unity.IL2CPP.dll</HintPath>
    </Reference>
    <Reference Include="Il2CppInterop.Runtime">
      <HintPath>E:\DMM\gakumas\BepInEx\core\Il2CppInterop.Runtime.dll</HintPath>
    </Reference>
    <Reference Include="Il2CppInterop.Common">
      <HintPath>E:\DMM\gakumas\BepInEx\core\Il2CppInterop.Common.dll</HintPath>
    </Reference>
    <Reference Include="Il2Cppmscorlib">
      <HintPath>E:\DMM\gakumas\BepInEx\interop\Il2Cppmscorlib.dll</HintPath>
    </Reference>
    <!-- Unity 引擎（按需引） -->
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>E:\DMM\gakumas\BepInEx\interop\UnityEngine.CoreModule.dll</HintPath>
    </Reference>
    <Reference Include="UnityEngine.InputLegacyModule">
      <HintPath>E:\DMM\gakumas\BepInEx\interop\UnityEngine.InputLegacyModule.dll</HintPath>
    </Reference>
    <!-- 游戏代码（按需引：Assembly-CSharp / campus-submodule.Runtime / ADV.Runtime / Qua.Utility / quaunity-ui.Runtime / ...） -->
    <Reference Include="Assembly-CSharp">
      <HintPath>E:\DMM\gakumas\BepInEx\interop\Assembly-CSharp.dll</HintPath>
    </Reference>
    <!-- 日志（NuGet 不可达时的替代：游戏自带 dotnet 目录 6.0 库） -->
    <Reference Include="Microsoft.Extensions.Logging.Abstractions">
      <HintPath>E:\DMM\gakumas\dotnet\Microsoft.Extensions.Logging.Abstractions.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

参考样例：`samples/GakumasAuto/`（合并版插件：UI 交互 + ADV 自动化 + MCP 结构化回传）。
截图动作需要额外引用：`UnityEngine.ScreenCaptureModule` + `UnityEngine.ImageConversionModule`（`BepInEx\interop\` 下同名 dll）；
ADV 需要 `ADV.Runtime` + `Uguiss.Runtime` + `Uguiss-Timeline.Runtime`。

### 2.2 插件入口

```csharp
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace MyPlugin
{
    [BepInPlugin("dev.my.plugin", "My Plugin", "1.0.0")]
    public class MyPlugin : BasePlugin   // 注意：是 BepInEx.Unity.IL2CPP.BasePlugin
    {
        internal static ManualLogSource SharedLog;  // 传给组件用（见 2.4）

        public override void Load()
        {
            SharedLog = Log;
            Log.LogInfo("MyPlugin loaded");
            try
            {
                var driver = AddComponent<MyDriver>();   // 注入 Unity 组件
                driver.Attach();                          // Attach() 无参数
            }
            catch (Exception e) { Log.LogError($"attach failed: {e}"); }
        }
    }

    public class MyDriver : MonoBehaviour
    {
        public void Attach() { }          // 无参数；初始化放这里，不要放 Awake
        public void Update() { }          // Unity 主循环，每帧调用
    }
}
```

### 2.3 BepInEx 6 IL2CPP API 要点

| 项 | BepInEx 5（旧） | BepInEx 6 IL2CPP（本包） |
|---|---|---|
| 基类 | `BaseUnityPlugin` | `BepInEx.Unity.IL2CPP.BasePlugin` |
| 入口 | `Awake()` | `Load()` |
| 日志 | 实例属性 `Logger` | 实例属性 `Log`（`ManualLogSource`） |
| 配置 | `Config` | `Config` |
| Unity 组件注入 | — | `AddComponent<T>()`（插件基类方法，把自定义 MonoBehaviour 注入 il2cpp 域） |
| 运行时路径 | — | `Paths.BepInExRootPath` / `Paths.PluginPath` 等 |

1. **组件类方法不得带托管类型参数或返回值**。`Attach(ManualLogSource)`、`List<T>` 参数、DTO 返回值、`StringBuilder` 参数都会触发 Il2CppInterop "unsupported parameter/return type" 警告并**替换为 substitute 调用路径**——被替换方法即使托管互调也会出现数据错乱（实测：layout 树丢节点、rect 全零）。**所有 DTO 形状的结果一律经插件类静态字段路由**（`internal static LayoutRespDto SharedLayoutResp;` 等），方法签名只留基本类型/游戏类型/无参。
2. **初始化放 `Attach()`（无参），不要放构造函数/Awake**；组件由 AddComponent 创建，Awake 时机不可控。
3. **组件类的所有公共方法都会被注入 il2cpp 域**：保持方法签名简单（基本类型/游戏类型/无参），避免泛型、委托、`out/ref`。
4. **Unity API 与游戏 API 的对象不要混用托管包装**：游戏类型（Il2Cpp 对象）经 interop 程序集访问，`new` 游戏对象必须用 `Il2CppSystem` 类型或游戏工厂方法。
5. **异常处理**：游戏内部抛的 NRE 会以 `Il2CppException` 回流到托管代码，`try/catch (Exception)` 有效——观察循环里必须包 try/catch，否则一次异常打爆 Update。
6. **所有游戏 API 调用假定可失败**：`ScreenLayerManager.Instance`、`UserDataManager.User` 在启动早期是 null，每次访问都要判空。

## 3. 游戏 API 速查（已验证可用）

```csharp
// 账号（全静态访问）
Campus.Common.User.UserDataManager.User          // ServerUserId / PublicUserId / DmmGamesId / TutorialClearedTime
Campus.Common.User.UserDataManager.UserProfile   // Name / TotalFanCount / Exp / Comment

// 加载/维护/教程（全静态）
Campus.Common.LoadingManager.IsActive            // bool
Campus.Common.Account.AccountManager.IsInitialized / IsCreatedUser
Campus.Common.TutorialManager.IsPlayingAnyTutorial
Campus.Common.MaintenanceInfoHolder.Instance.MaintenanceInfo   // 实例；未拉取为 null

// 屏幕层（实例）
Campus.Common.ScreenLayerManager.Instance
    .HasActive           // 注意：主界面时可为 False（语义=过渡层，非"顶层可见"）
    .GetTopLayer()       // 顶层 layer（标题屏/过渡中有效），.GetParent() 取场景根
// 布局遍历用 topLayer.GetParent() 作根；拿不到时回退 FindObjectsOfType<Canvas>()

// ADV 剧情自动化
UnityEngine.Object.FindObjectsOfType<ADVEngine>()  // 发现场景引擎
engine.Timeline.IsWaiting / IsChoiceLoop / IsFastForward
engine.Timeline.EndWait(true)          // 跳过消息等待
engine.Timeline.ToggleFastForward(true)
engine.Branch.ChoiceCount / SelectUnselectedChoices()   // 自动选分支
```

## 4. UI 交互模式（GakumasAuto v2 参考实现）

### 4.1 按钮点击机制（重要）

| 方式 | 效果 |
|------|------|
| `ButtonBase.OnClicked()` 反射直调 | **无效**——动作回调不在 button 侧，被手势状态机静默忽略 |
| `ExecuteEvents.Execute<IPointerClickHandler>` click-only | **无效**——手势状态机要求完整按→抬序列 |
| 完整指针序列（down→up→click，PointerEventData 带屏幕坐标） | 正常路径（`tap` 动作） |
| **直接 invoke `LongTapGesture.onClickedCallback`** | **标题屏唯一生效路径**（`invoke_callback` 动作）；直接触发已挂委托，绕过手势校验 |

诊断按钮回调挂载：`debug_button` 动作输出 `IsEnabled / gestureCallback / buttonCallback / pressedCallback` 三处回调是否 SET。
若三处回调都不 SET：按钮动作还没挂（未初始化），等屏幕就绪再试。

### 4.2 指令通道协议（文件，无网络）

```
写 <BepInEx>\gakumas-ui-cmd.json   (UTF8 JSON, 1s 轮询内执行)
读 <BepInEx>\gakumas-ui-resp.json  (响应，覆盖写)
命令文件改名 <原始名>.<UtcNow.Ticks>.done.json 留档
```

```jsonc
// 请求（新增动作见下表）
{"id":"可选任意字符串","action":"layout|state|screenshot|tap_at|click|tap|invoke_callback|debug_button|find|adv_end_wait|adv_set_ff|adv_select_unselected","path":"...","x":0,"y":0,"value":true}
// 响应：result 为字符串（文本结果/错误）或结构化对象（layout/state/screenshot/find）
{"id":"同请求","ok":true|false,"result":"文本 或 {\"root\":...,\"count\":...,\"nodes\":[...]}"}
```

| 动作 | 参数 | resp.result |
|---|---|---|
| `layout` | — | `{root, count, nodes:[{id,path,name,sx,sy,w,h,active,flags[],text}]}`（屏幕坐标 sx/sy） |
| `state` | — | `{userId, name, topLayer, layersActive, loading, maintenance}` |
| `screenshot` | — | `{file, w, h, pending}`；sync 路径立即落盘，失败回退 async（pending=true 时轮询文件出现） |
| `tap_at` | `x`,`y` 屏幕像素 | 文本：raycast 命中的 GameObject + down/up/click 结果 |
| `adv_end_wait` / `adv_set_ff`(`value`) / `adv_select_unselected` | — | 文本结果 |
| 其余（click/tap/invoke_callback/debug_button/find） | `path` | 文本；find 为 `{count, matches:[{path,name,active,text}]}` |

`path` 匹配规则：先在当前顶层 screen-layer 树内找，再遍历全部 Canvas；精确匹配优先，然后子串匹配；匹配到的 GameObject 上找不到按钮组件时**向上遍历最多 5 层祖先**找 CampusButton/uGUI Button。
`find` 遍历**全部 Canvas**（不限于首个），列出名字含 `path` 子串的节点（含 inactive），上限 300。
### 4.3 布局捕获

- F10 手动 / 屏幕顶层 layer 变化自动 / `layout` 动作
- 输出：GameObject 树，每节点 `名称 rect(anchoredPosition, size) [inactive] [CampusButton] [UIButton] [not-interactable] [TMP:"文本"]`，上限 500 节点 / 深度 12
- **RectTransform 获取必须用 `t.GetComponent<RectTransform>()`**——`t as RectTransform` 转型在 interop 下恒返回 null（rect 全零）；size 读 `rt.rect`，≤0 时回退 `rt.sizeDelta`
- 按钮识别：`Campus.Common.CampusButton`（游戏按钮体系）+ `UnityEngine.UI.Button`（uGUI 原生）+ TMP_Text/Text 文本内容
- 屏幕坐标：`RectTransformUtility.WorldToScreenPoint(cam, rt.position)`（overlay canvas 传 null camera）
### 4.4 自动化模式

- 热键：`Input.GetKeyDown(KeyCode.F8)` 开关 + 每 200ms tick（`Time.realtimeSinceStartup` 节流）
- 观察器：1Hz 轮询 + 状态变化边沿触发日志（LOGIN/LOADING/MAINTENANCE）

## 5. 构建 / 部署 / 验证流程

插件开发前提：目标游戏已由用户安装匹配版本的 BepInEx、Il2CppInterop 和离线 interop。公开仓库不携带这些二进制。

```powershell
# 构建插件
& 'C:\Program Files\dotnet\dotnet.exe' build <插件目录> -c Release
# 产物：<插件目录>\bin\Release\net6.0\<插件名>.dll

# 只部署插件，不覆盖 BepInEx 或 interop
powershell -NoProfile -ExecutionPolicy Bypass -File `
  <repo>\gakumas-bepinex-kit\deploy.ps1 `
  -TargetDir 'E:\DMM\gakumas' `
  -PluginPath '<插件目录>\bin\Release\net6.0\<插件名>.dll'
```

```powershell
# 在独立的 PowerShell/CMD 中重启游戏；不要从 agent 终端启动子进程
powershell -NoProfile -Command "Stop-Process -Name gakumas -Force; Start-Sleep 2;
Start-Process -FilePath 'E:\DMM\gakumas\gakumas.exe' -ArgumentList '/viewer_id=...','/open_id=...','/pf_access_token=...' -WorkingDirectory 'E:\DMM\gakumas'"

# 验证（LogOutput.log 顺序出现即成功）
#   1. "N plugins to load"
#   2. "Loading [<插件名> <版本>]"
#   3. "<插件名> loaded. ..."
#   4. "<组件名> component attached"
#   5. 无 "DllNotFound" / "Interop assemblies are possibly out of date"
```

## 6. 踩坑清单（全部实踩）

| # | 症状 | 原因 | 解法 |
|---|------|------|------|
| 1 | 链加载报 "support 23-29, got 31" | BepInEx 版本旧 | 用 6.0.0-be.785 |
| 2 | "Hit backtrack limit of 185 modules" / interop 生成失败 | codereg 16B 步进布局 vs LibCpp2IL u64×17 解析 | 离线生成器 + `OnRegistrationStructLocationFailure` 注入手工 codereg |
| 3 | 启动刷 "Interop assemblies are possibly out of date" | assembly-hash.txt 带尾随换行（33 字节） | 32 字节无换行 |
| 4 | `DllNotFound GameAssembly_decrypted.dll` | 残留 dumper 代理 dll | 从游戏目录移除 dumper 物 |
| 5 | 点击"成功"但游戏无反应 | OnClicked()/click-only 被手势状态机忽略 | tap 完整序列或 invoke gesture.onClickedCallback |
| 6 | 组件注入警告 "unsupported parameter" | 方法带托管类型参数 | 无参方法 + 静态字段传 logger |
| 7 | `resp write failed: Cannot create a file when that file already exists` | done 文件名碰撞（同 tick 两命令） | done 名加 `UtcNow.Ticks` |
| 8 | 读命令文件报 "not valid UTF-8" | PowerShell `Set-Content` 默认 UTF-16 | `-Encoding UTF8` |
| 9 | 类图生成 TypeLoadException | interop 程序集泛型约束不可解析 | 按成员/类型 try/catch 容错 |
| 10 | layout 树丢节点、`rect` 全零 | 方法签名带 DTO/List 参数被 substitute；`t as RectTransform` 转型恒 null | 静态字段路由 DTO；`GetComponent<RectTransform>()` |
| 11 | 指令通道偶发 "file is being used by another process"（resp id 为空） | 客户端直写 cmd 与插件 1Hz 读档竞态 | 客户端**原子发布**（写临时文件+rename）；插件 `FileShare.ReadWrite\|Delete` 读 + 重试 |
| 12 | MCP layout 返回的 nodes 比 count 少 | 服务端 `includeInactive` 缺省值写反（误过滤 inactive 节点） | 缺省保留 inactive，仅显式 `false` 才过滤 |
| 13 | find 找不到别的 canvas 里的节点 | 只搜了 `canvases[0]` | 遍历全部 Canvas |

## 7. 开发新玩法自动化的建议顺序

1. `find` 定位目标界面按钮名 → `layout` 看树结构
2. `debug_button` 确认回调挂载点 → `invoke_callback`/`tap` 试验
3. 观察器日志确认状态迁移（SCREEN/LOADING 边沿）
4. 状态机固化成插件：进入界面 → 等待就绪 → 点击 → 校验结果 → 循环
5. 每步留日志行（带目标名+结果），失败可经文件通道人工接管

## 8. MCP 集成（让 AI agent 玩）

独立项目 `<repo>\mcp\server.js`（Node.js、零 npm 依赖）把指令通道包装成 stdio MCP 服务器：

```json
// .mcp.json（harness / Claude Code 式客户端）
{ "mcpServers": { "gakumas": {
    "type": "stdio", "command": "node",
    "args": ["mcp/server.js"],
    "env": { "GAKUMAS_BEPINEX": "<game>/BepInEx" } } } }
```

- **Tools（38，分四级）**：L0 感知、L1 动作、L2 编排、L3 任务；完整工具表以根目录 README 和 `mcp/server.js` 为准。
- **Resources（4）**：`gakumas://state`、`gakumas://layout`（缓存）、`gakumas://screen`（PNG base64）、`gakumas://log`（日志尾 200 行）。
- 服务器职责：原子发布（临时文件+rename）、单飞写、id 配对、8s 超时 + 一次重试、`wait_until` 服务端轮询。
- 延迟：常规动作 1–2s、截图 2–4s（插件约 1Hz 轮询 cmd）。
- 协议版本：`2024-11-05`；插件侧无网络面。

## 9. 升级 interop（游戏更新后）


游戏版本更新或 GameAssembly 重编后：
1. 准备新的 packed `GameAssembly.dll`、`global-metadata.dat` 和匹配版本的解密 profile。
2. 运行 `tools/ga-static-decrypt/`，生成并校验 `GameAssembly_static_exact.dll`。
3. 部署 `tools/doorstop-shim/`：`install.ps1 -ImagePath <新镜像> -EnableInteropUpdate`（细节见该目录 README）。
4. 启动一次游戏：BepInEx 在自己的管线里生成并替换 `BepInEx\interop\*` 与 `assembly-hash.txt`（首次约 83 s）。
5. 重编译全部插件，部署后重启游戏并检查日志。
