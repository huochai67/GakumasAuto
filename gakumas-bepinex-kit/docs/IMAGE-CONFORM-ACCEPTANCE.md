# 路线 2 验收标准：镜像规范化（去 interop-gen）

> 适用：Unity 6000.0.77f1 IL2CPP + BepInEx 6.0.0-be.785 + 离线解密（`tools/ga-static-decrypt`）
> 目标：让 **stock LibCpp2IL** 直接解析解密镜像 → BepInEx 用自身管线生成 interop → 不再需要 `tools/interop-gen`、不再手工维护 codereg 常量、不改 loader
> 状态（2026-09-24 起）：目标已用 `tools/doorstop-shim/` 达成——interop 由 BepInEx 运行时自行生成、codereg 常量改为每次现算、`tools/interop-gen` 已删除。
> 实现方式是在进程内注入 codereg 常量（订阅 `OnRegistrationStructLocationFailure`），因此 **AC-1（无 hook 的 stock 解析）未达成**，其余判据保留为对照与回归记录。

## 1. 术语

| 名称 | 含义 |
|---|---|
| `GA_packed` | 游戏目录原始 `GameAssembly.dll`（KONN 加壳，不可解析） |
| `GA_static` | `ga-static-decrypt` 现有产物（分析/加载用镜像，ImageBase `0x180000000`） |
| `GA_conform` | **本路线新增产物**：供生成器输入的规范化镜像。推荐与 `GA_static` 分离；若合并，AC-8 变为必做项 |
| 真值 `derived-codereg-<build>.json` | 该 build 的注册数据真值：`genericMethodPointers(+Count)`、`codeGenModulesCount`、`addrCodeGenModulePtrs`，以及 185 个 codegen module 的 method pointer 表长度 |
| stock 解析 | 不订阅 `Il2CppBinary.OnRegistrationStructLocationFailure`、不打 patch 的 LibCpp2IL 初始化 |

## 2. 硬性通过条件（全部满足才算通过）

### AC-1 无 hook stock 解析

```powershell
# 常驻探针：tools/libcpp2il-probe（见 §7 生成方式）
dotnet tools\libcpp2il-probe\bin\Release\net8.0\libcpp2il-probe.dll `
  <GA_conform.dll> <global-metadata.dat>
```

判据：退出码 0；输出 `RESULT=OK`；`codeRegistration != null` 且 `metadataRegistration != null`。

### AC-2 注册数据语义正确（防"假成功"）

同 AC-1 的探针输出需满足：

1. `genericMethodPointers`、`genericMethodPointersCount`、`codeGenModulesCount`、`addrCodeGenModulePtrs` 与 `derived-codereg-<build>.json` **逐字段相等**（不是"能解析就行"）；
2. 每个 codegen module 的 method pointer 表长度 == metadata 中该 module 的方法数（不截断、不补零）；
3. 所有解析出的指针落在镜像 section 范围内，code 指针落在可执行段；
4. 185 个 module 全部解析出名字（`mscorlib`、`Assembly-CSharp`、`campus-submodule.Runtime` …）且与 `ScriptingAssemblies.json` 一致。

**若第 1 条靠"种一份合成 codereg"实现，则合成值必须来自真值推导，而不是为了绕过搜索而编造。**

### AC-3 反例仍失败（测试灵敏度）

```powershell
dotnet tools\libcpp2il-probe\...\libcpp2il-probe.dll `
  <GameAssembly_static_exact_new.dll> <global-metadata.dat>
```

判据：**必须失败**，且错误为下列之一（2026-09-24 实测原文）：

```
WARN Hit backtrack limit of 185 modules and still didn't find a valid pCodegenModules pointer.
Got Binary codereg: 0x0, metareg: 0x189C51400
LibCpp2ILInitializationException: Failed to find code registration or metadata registration!
```

运行时快照（`snapshot_t0.bin` / `t1000.bin`）则表现为更早失败：`Searching Binary for Required Data...` → `InvalidOperationException: Sequence contains no matching element`。

若反例也"通过"，说明探针、镜像或输入参数选错，本次验收无效。

### AC-4 BepInEx 自生成端到端

```powershell
# 1) 指向规范化镜像（gen 输入路径可被环境变量覆盖，IL 级已确认）
setx BEPINEX_GAME_ASSEMBLY_PATH "<abs>\GA_conform.dll"    # 或用启动脚本注入
# 2) 恢复自生成 + 清空既有 interop
#    BepInEx\config\BepInEx.cfg → [IL2CPP] UpdateInteropAssemblies = true
Remove-Item E:\DMM\gakumas\BepInEx\interop\* -Force
Remove-Item E:\DMM\gakumas\BepInEx\interop\assembly-hash.txt -Force
# 3) 启动游戏
```

判据（`BepInEx\LogOutput.log`）：

- 出现 `Running Cpp2IL to generate dummy assemblies from …` → `Cpp2IL finished in …` → `Generating interop assemblies` → `N interop assemblies in …`
- **不得**出现 `Failed to generate Il2Cpp interop assemblies`
- `BepInEx\interop\` 有产物、`assembly-hash.txt` 已写入（MD5 十六进制串，32 字符，无换行）

### AC-5 二次启动幂等 + 插件可用

关游戏 → 再启动（环境变量保持不变）：

- 不再触发生成（无 `Detected outdated interop assemblies`，无 `Interop assemblies are possibly out of date`）
- `GakumasAuto` 正常加载，MCP 工具可用：`state` 返回非空屏幕、`layout` 返回节点、`tap_at` 生效

### AC-6 loader 中立性（路线 1 与本路线的分界）

| 检查 | 判据 |
|---|---|
| `doorstop_config.ini` | `target_assembly` 仍为 `BepInEx\core\BepInEx.Unity.IL2CPP.dll` |
| `BepInEx\core\*` SHA-256 | 与官方 be.785 逐字节一致（无 Cecil patch、无替换） |
| BepInEx 配置 | `UpdateInteropAssemblies = true`（默认），无隐藏 shim/环境变量以外的手工干预 |

**存在任何 loader 侧改动（shim、Cecil patch、替换 core 程序集）→ 本路线不通过**（那属于路线 1）。

### AC-7 确定性 / 可复现

`ga-static-decrypt --conform`（或等价工具）连跑两次：输出 SHA-256 相同；全程离线、无手工十六进制编辑。

### AC-8 无加载回归（仅当 `GA_conform` 即加载件）

用 `latest/` 包启动到标题画面（判据同 E-017 / E-019：`Player.log` 出现 `Initialize engine version 6000.0.77f1` + `Campus.Title.<StartAsync>`，无新增异常）。若 `GA_conform` 与 `GA_static` 分离，则仅需对 `GA_static` 做一次回归确认未被改动。

### AC-X 交叉验证（推荐，非硬性）

用 MelonLoader 固定版 dumper 独立跑一遍，证明产物与 loader 无关：

```powershell
Cpp2IL.exe --game-path <任意目录> --exe-name gakumas `
  --force-binary-path <GA_conform.dll> `
  --force-metadata-path <global-metadata.dat> `
  --force-unity-version 6000.0.77f1 --output-as dummydll
```

判据：退出码 0；生成 dummy 程序集集合（`Assembly-CSharp` 等）；日志无 `backtrack`。

## 3. 失败模式（一律 fail-closed）

1. **假 codereg**：解析成功但字段与真值不符 → AC-2 失败。
2. **仍需手工常量**：新 build 必须人工改 RVA 才能过 → 未达成路线目标（应改为"脚本从运行期镜像机械推导"）。
3. **依赖 loader 改动** → AC-6 失败，退化为路线 1。
4. **靠裁剪数据换成功**（丢 module、截断 method pointer 表）→ AC-2② 失败。
5. **只在单一 build 成立**：验收必须同时在 2026-08 与 2026-09-17 两个 build 上通过（后者是当前常量已失效的那个）。

## 4. 必需证据（按 case 的 E-* 规范归档）

| 证据 | 内容 |
|---|---|
| E-0xx | AC-1 / AC-2 探针成功输出（含 codereg 全字段、185 module 表长度） |
| E-0xx | AC-3 反例失败输出（完整错误串） |
| E-0xx | `derived-codereg-<build>.json` + 推导脚本 + 推导来源（运行期镜像读取位置/RVA） |
| E-0xx | BepInEx `LogOutput.log` 自生成片段（AC-4）+ 二次启动片段（AC-5） |
| E-0xx | 插件烟测：`state` / `layout` / `tap_at` 各一条响应 |
| 清单 | 所有输入输出 SHA-256：`GA_packed`、`GA_conform`、interop 目录、`assembly-hash.txt`；AC-7 两次运行哈希相同 |

## 5. 已实测的关键事实（供实施时对照）

**stock 解析失败特征**（2026-09-24）：

| 输入 | 结果 |
|---|---|
| 解密静态镜像（2026-09-17 build） | `Hit backtrack limit of 185 modules` → `codereg: 0x0` → `Failed to find code registration` |
| 运行时快照 `t0` / `t1000` | `Sequence contains no matching element`（更早阶段） |

**2026-08 build 的 codereg 常量真值（原 `tools/interop-gen` 内置；该工具已删除，数值保留作对照）**：

```
genericMethodPointersCount = 496142   RVA 0x90B56E0
codeGenModulesCount        = 185      RVA 0xA6A2140
```

对 2026-09-17 镜像已失效（`Reading code gen modules...` 处抛 `Fatal Exception initializing LibCpp2IL`）。LibCpp2IL 自带搜索在该镜像上给出的候选为 `0x18A6E1A18`，但单换该 RVA 不足以通过（实测错误变化为 `image offset 0x41058D4C outside the range of the file`），说明需要按布局正规重推，而不是换一个地址。

**BepInEx be.785 引导与生成链（IL 级确认）**：

```
Doorstop.Entrypoint.Start()               ← doorstop_config.ini target_assembly
  └─ Preloader.Run()
       └─ Il2CppInteropManager.Initialize()      ← 生成在插件加载之前（插件无法 hook）
            ├─ GenerateInteropAssemblies()
            │    ├─ DownloadUnityAssemblies()
            │    ├─ RunCpp2Il()    → Cpp2IlApi.InitializeLibCpp2Il(GameAssemblyPath, metadata, UnityInfo.Version, false)
            │    │                    + AttributeInjectorProcessingLayer + AsmResolverDllOutputFormatDefault
            │    └─ RunIl2CppInteropGenerator() → Il2CppInteropGenerator.Create(opts).AddInteropAssemblyGenerator().Run()
            ├─ 写 interop\assembly-hash.txt（MD5: GameAssembly 文件 + unity-libs 名称与内容 + rename map + 两个程序集版本）
            └─ BaseHost.Start()
```

- 生成输入路径：`Environment.GetEnvironmentVariable("BEPINEX_GAME_ASSEMBLY_PATH") ?? Path.Combine(Paths.GameRootPath, "GameAssembly" + PlatformHelper.LibrarySuffix)`
- hash 不匹配且 `UpdateInteropAssemblies = false` 时只告警（`Interop assemblies are possibly out of date. To disable this message, create file …`）不生成；注意 `assembly-hash.txt` 需 32 字节无换行
- 因此环境变量必须长期保留（hash 会把它计入），否则每次启动都判定为过期

**工具版本**：BepInEx 6.0.0-be.785（`BepInEx\core\LibCpp2IL.dll`）、MelonLoader 固定 dumper `Cpp2IL 2022.1.0-pre-release.21`（CLI 支持 `--force-binary-path` / `--force-metadata-path` / `--force-unity-version`）。

## 6. 每版本刷新成本上限（验收的一部分）

游戏更新后唯一人工输入 = **解包 profile 刷新**（既有流程）。`derived-codereg-<build>.json` 必须由脚本产出，且该脚本能在两个已验收 build 上**复现**基准值：

```
2026-08 build: 496142 / 0x90B56E0 / 185 / 0xA6A2140
```

## 7. 交付物清单

1. `tools/libcpp2il-probe/`（net8 控制台，引用 `BepInEx\core\{LibCpp2IL,Cpp2IL.Core}.dll`；退出码 0 = 解析成功）。
   临时替代做法（原为复制 `tools/interop-gen/Program.cs`）已失效：该文件随工具一起删除，需要时从 git 历史取（删除前提交 `bcd7c91`）。
2. `ga-static-decrypt --conform`（或 `tools/ga-conform/`）+ 真值推导脚本
3. 一条命令跑完 AC-1…AC-7 的验收脚本（输出通过/失败表）
4. `tools/interop-gen` 处置：**已删除**（2026-09-24）。codereg 常量不再内置，改由 `tools/doorstop-shim/CodeRegScanner` 每次扫描镜像现算。

## 8. 非目标

- MelonLoader 支持：本路线使其**成为可能**（其 AGF 的 Cpp2IL 可直接解析），但不作为验收项
- 修改 `GA_packed`、改动游戏安装布局、安装/替换任何 loader 组件
