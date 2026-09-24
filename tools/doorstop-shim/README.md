# GakumasDoorstopShim — 让 BepInEx 在 packed 游戏上跑通 interop 生成

`GameAssembly.dll` 是静态加密壳镜像时，LibCpp2IL 找不到 `Il2CppCodeRegistration`
（日志：`Hit backtrack limit of 185 modules and still didn't find a valid pCodegenModules pointer`），
BepInEx 的 interop 生成（Cpp2IL + Il2CppInterop）就会失败，所以过去每次游戏更新都必须：

```
tools/ga-static-decrypt  →  tools/interop-gen  →  手工替换 BepInEx\interop\* 与 assembly-hash.txt
```

（该离线生成器 `tools/interop-gen` 已删除，仅存于 git 历史。）

本 shim 把这条链搬进游戏进程：doorstop 先加载 shim，shim 注入 codereg 常量并指好解密镜像，
再交给 BepInEx 自己的 `Doorstop.Entrypoint`，于是 interop 由 BepInEx 运行时自行生成，更新后不再需要离线生成器。

## 工作方式

```
gakumas.exe
 └─ winhttp.dll (doorstop 4.5.0)
     └─ doorstop_config.ini → target_assembly = BepInEx\core\GakumasDoorstopShim.dll
        （`BepInEx\core` 被扫描器/反作弊进程映射而无法替换时，自动改用 `BepInEx\core\shim\`）
         └─ Doorstop.Entrypoint.Start()                       (shim)
             ├─ 模块初始化器先装好程序集解析器（`core\shim` 部署时这一步是必需的：
             │  `Bootstrap` 的方法签名里就有 MonoMod 类型，JIT 会先于任何语句解析它）
             ├─ Assembly.LoadFrom(BepInEx\core\LibCpp2IL.dll) 把 LibCpp2IL 钉到 BepInEx 自己那份
             ├─ 订阅 Il2CppBinary.OnRegistrationStructLocationFailure → 注入手工 Il2CppCodeRegistration
             ├─ 探测进程里已加载的 IL2CPP 模块（按 il2cpp_domain_get 导出）→ runtimePath
             ├─ 反射 + MonoMod 钩住 Il2CppInteropManager.GameAssemblyPath / GenerateInteropAssemblies
             └─ Assembly.LoadFrom(BepInEx.Unity.IL2CPP.dll) + Doorstop.Entrypoint.Start()   (BepInEx 接管)
```

### 为什么不能只设 `BEPINEX_GAME_ASSEMBLY_PATH`

be.785 的 `BepInEx.Unity.IL2CPP` 用**同一个入口**干两件互斥的事（IL 实证）：

| 用途 | 调用点 |
|------|--------|
| 生成 interop 的输入 + `assembly-hash.txt` 的输入 | `RunCpp2Il()` → `Cpp2IlApi.InitializeLibCpp2Il(get_GameAssemblyPath(), …)`、`ComputeHash()` |
| 原生模块加载 | `Preloader.DllImportResolver` → 名字 `"GameAssembly"` → `NativeLibrary.Load(get_GameAssemblyPath(), …)` |

只设环境变量（第一版实现）会让第二步去 `LoadLibrary` 那个**解密镜像**：它不是可加载 PE，
于是 `0x8007045A ERROR_DLL_INIT_FAILED`——即使能加载，也会得到第二个未初始化的 il2cpp 运行时。

所以 shim 改为**分时复用**这个入口：

```
get_GameAssemblyPath():
    生成阶段（GenerateInteropAssemblies 内部）→ 解密镜像      ← Cpp2IL 的输入
    其余时刻（含 DllImportResolver）          → 运行期模块路径 ← 进程里已加载的那份
```

运行期模块路径来自进程模块探测（`Process.Modules` + `GetModuleHandleW` + `GetProcAddress("il2cpp_domain_get")`），
`NativeLibrary.Load` 对同一路径返回**已加载的模块句柄**，不重新初始化、不会出现第二个运行时。

`assembly-hash.txt` 这道门**完全交给 BepInEx**（shim 只读、只报告，绝不改写）：
BepInEx 在 `GenerateInteropAssemblies` 内部既做判定又做生成，而那个调用全程都落在
「生成阶段」窗口里——它哈希的是**解密镜像**，生成后写回的也是镜像口径的值。所以镜像不变时
每次启动都命中、直接跳过生成（实测生成步骤 0.37 s）；镜像/unity-libs/生成器任一变化时才会
重新生成一次。把该文件改写成运行期模块口径（曾经的实现）会让每次启动都不命中、**每次重跑
83 秒生成**——这正是「启动很慢」的成因，已移除。

hash 配方（从 IL 抄出，并用 BepInEx 自己写下的值逐字节验证）：

```
MD5( bytes(GameAssemblyPath)
   + 每个 <unity-libs>/*.dll：UTF8(文件名) + bytes(文件)
   + bytes(BepInEx/DeobfuscationMap.csv.gz)（存在时）
   + UTF8(Il2CppInterop.Generator 程序集版本)
   + UTF8(Cpp2IL.Core 程序集版本) ) → 小写十六进制
```

常量不是硬编码的，而是扫描镜像现算（`CodeRegScanner`）：

1. `genericMethodPointers`：全镜像最长的一段「指向可执行节」的 qword 连续区；首元素是空占位，
   所以数组起点要前移一格，长度 = 连续数 + 1。
2. `codeGenModules`：`Il2CppCodeGenModule` 结构体首字段是模块名字符串指针（`*.dll`）。
   先扫出名字字符串 → 匹配结构体（+8 方法数 < 5e6、+16 方法指针表在图内）→ 再找这些结构体指针的连续数组。

镜像内指针是**绝对 VA**（imageBase `0x180000000` + RVA），扫描时统一归一化成 RVA 后比较。

## 已验证的事实

| 检查 | 命令 | 结果 |
|------|------|------|
| 复现仓库里的手算常量（2026-08 版镜像） | `GakumasDoorstopShim --scan <run-old-20260919>\GameAssembly_static_exact.dll --expect gmpRva=0x90B56E0,gmpCount=496142,cgrRva=0xA6A2140,cgmCount=185` | 4/4 OK |
| 推导当前版本常量（2026-09-17 版镜像） | `--scan <run-new-20260919>\GameAssembly_static_exact_new.dll` | `gmp=0x90ED490(497076) cgm=0xA6E1500(185)` |
| 注入是否真的让 LibCpp2IL 跑通 | `--hook-test <new image> <global-metadata.dat>` | `After fallback, code registration is not null`；`Initialized Binary in 1087ms`；`Mapping pointers to Il2CppMethodDefinitions...Processed 339677 OK` |
| 反证：没有 shim 会怎样 | `--hook-test ... --no-hook` | `code registration is null` → `Failed to find code registration or metadata registration!`（`INVOCATIONS=0`） |
| hash 配方 | `--hash <解密镜像> <BepInEx 根>` 对照 BepInEx 运行时自己写的 `assembly-hash.txt` | `d7ac475f8fcb4ed404c2a6fdc60b90ba` 逐字节一致 |
| 运行期模块 hash | `--hash <GameRoot>\GameAssembly.dll <BepInEx 根>` | `17348e6b4c468fb80f8be8e614c3ab78` |
| 游戏内端到端（2026-09-24 实测） | 直接启动游戏 | BepInEx 报 `Detected outdated interop assemblies, will regenerate them now` → Cpp2IL 23.6 s + Il2CppInterop 生成完成 → `BepInEx\interop` 全套重写（`Assembly-CSharp.dll` 79.7 MB） |

## 文件

| 文件 | 说明 |
|------|------|
| `Entrypoint.cs` | doorstop 入口：钉 LibCpp2IL、装钩子、探测模块、装 interop 桥、交给 BepInEx |
| `CodeRegScanner.cs` | 镜像扫描 + 常量推导 + 缓存（按 大小/mtime/首尾 1MB 哈希 失效） |
| `CodeRegFallback.cs` | `OnRegistrationStructLocationFailure` 处理体；后台预扫，钩子触发时取结果 |
| `BepInExInteropBridge.cs` | `GameAssemblyPath` / `GenerateInteropAssemblies` 钩子 + hash 只读复算（判定交给 BepInEx）+ 预检（按 size/mtime 身份缓存，省掉每次启动 ~0.8 s 的两次 MD5） |
| `Il2CppModuleProbe.cs` | 找出进程里已加载、导出 `il2cpp_domain_get` 的模块及其文件路径 |
| `ShimConfig.cs` | `BepInEx\core\GakumasDoorstopShim.cfg`（`ImagePath` / `CachePath` / `RuntimePath` / `Verbose`） |
| 运行时缓存 | `BepInEx\gakumas-shim-codereg.cache`（常量）、`BepInEx\gakumas-shim-preflight.cache`（预检 hash，键=两文件 size+mtime） |
| `SelfTest.cs` | 离线自检：`--scan`、`--hook-test`、`--no-hook`、`--hash` |
| `host-probe/` | 复刻 doorstop 加载方式的自检宿主（按名从非探测目录装载已部署的 shim + 断言 `SetPlatform()` 可设） |
| `install.ps1` | 部署/回滚（`-DryRun` 不落盘；`core` 被映射时回退 `core\shim`；部署后自动跑 host-probe 自检） |

## 构建与自检

```powershell
dotnet build tools\doorstop-shim\GakumasDoorstopShim.csproj -c Release
# 输出：tools\doorstop-shim\bin\Release\GakumasDoorstopShim.dll / .exe

# 只扫镜像
tools\doorstop-shim\bin\Release\GakumasDoorstopShim.exe --scan <image.dll> --cache <cache>
# 走一遍 LibCpp2IL 初始化（含注入）
tools\doorstop-shim\bin\Release\GakumasDoorstopShim.exe --hook-test <image.dll> <global-metadata.dat> [--no-hook]

# 复刻 doorstop 的加载方式：把**已部署**的 shim 按名从非探测目录装载（宿主进程里没有任何
# shim 依赖，也没有 IL2CPP 运行时，所以 shim 内部用 GAKUMAS_SHIM_SKIP_HANDOVER=1 跳过移交）
dotnet build tools\doorstop-shim\host-probe -c Release
tools\doorstop-shim\host-probe\bin\Release\shim-host-probe.exe <gameRoot> <shim.dll> [logPath]
```

`--hook-test` 只依赖 `BepInEx\core\` 下的 `LibCpp2IL.dll` / `AssetRipper.Primitives.dll`（`--core` 可指定）。
`host-probe` 断言的关键行：`LibCpp2IL pinned`、`resolving MonoMod.RuntimeDetour from core`、
`hooked BepInEx.Unity.IL2CPP.Il2CppInteropManager`、`handover skipped`——它们一起证明
「shim 可以从 `BepInEx\core\shim` 这类非探测目录启动」；探针随后直接调用 BepInEx 的
`BepInEx.Preloader.Core.PlatformUtils.SetPlatform()`，必须打印 `[probe] PlatformUtils.SetPlatform() OK`
（这就是「平台锁」约束的离线复现，见下文；出问题时它会打印与游戏里同样的
`Cannot set the value of PlatformHelper.Current once it has been accessed.`）。
`GAKUMAS_SHIM_SKIP_HANDOVER=1` 仅供自检：探针进程没有 IL2CPP 运行时，BepInEx 的 preloader 会一直等它。

## 部署

前置：游戏目录已有 BepInEx 6 + doorstop 4.x，且有一份**解密镜像**（`tools/ga-static-decrypt`
的 `GameAssembly_static_exact*.dll`，或运行期捕获产物；packed 的 `GameAssembly.dll` 会被扫描器拒绝）。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\doorstop-shim\install.ps1 `
  -GameRoot 'E:\DMM\gakumas' `
  -ImagePath '<解密镜像>' `
  -RestoreDoorstopProxy `      # 若 winhttp.dll 被改名停用（如 winhttp2.dll），恢复它
  -EnableInteropUpdate `       # 置 UpdateInteropAssemblies = true，让 BepInEx 首次启动重新生成 interop
  -DryRun                      # 先看要改什么
```

脚本只写这几个位置，并且都留备份（`*.gakumas-shim.bak`）：`BepInEx\core\GakumasDoorstopShim.*`
（或回退目录 `BepInEx\core\shim\GakumasDoorstopShim.dll`）、`BepInEx\gakumas-decrypted\<镜像>`、
`doorstop_config.ini`、`BepInEx\config\BepInEx.cfg`、`winhttp.dll`。
不动游戏本体、BepInEx 二进制、`BepInEx\interop` 与插件 DLL。

安装结束后脚本会**自检**：用 `host-probe` 把刚部署的 shim 按 doorstop 的方式装载一遍，断言
`LibCpp2IL pinned` / `hooked BepInEx.Unity.IL2CPP.Il2CppInteropManager` / `handover skipped` 三条日志
（探针日志写临时目录，不污染 `BepInEx\gakumas-shim.log`）。失败即 `exit 6`——避免出现「游戏能开、BepInEx 静默不加载」
这种 doorstop 侧无法诊断的状态。探针未构建时只告警。

### 部署路径与移交的三条硬约束
1. **文件名（去扩展名）必须等于程序集名**。doorstop 4.5 用它去找托管入口：
   `src/bootstrap.c` → `il2cpp_doorstop_bootstrap()` 里
   `get_file_name(config.target_assembly, FALSE)` 取出的名字直接喂给
   `coreclr_create_delegate(host, domain, <name>, "Doorstop.Entrypoint", "Start")`，
   即按**程序集标识**加载。所以不能为了绕开文件占用而部署成
   `GakumasDoorstopShim.<时间戳>.dll`（程序集名仍是 `GakumasDoorstopShim`）——doorstop 会静默失败
   （发布版把 `LOG` 编译掉了，见 `util/logging.h` 的 `#if VERBOSE`），表现为「游戏正常启动但 BepInEx 不加载」。
2. **目录可以换**。doorstop 把 `APP_PATHS` 设为 `<corlib_dir>;<目标文件所在目录>`
   （同样是 `bootstrap.c`），目标按名从它自己所在目录解析——`BepInEx.Unity.IL2CPP.dll` 本来就
   在 `BepInEx\core` 而能被解析，正是这个机制。因此当 `BepInEx\core` 里的旧文件被某个进程
   **映射**（`replace failed: ... user-mapped section open`，改名/删除/截断都被拒）时，
   `install.ps1` 会退到 `BepInEx\core\shim\GakumasDoorstopShim.dll`，文件名不变、`doorstop_config.ini`
   同步指向新路径。shim 自己的依赖解析不受影响：`Entrypoint.ResolveFromCore` 始终按
   `<GameRoot>\BepInEx\core` 解析（并同时backstop BepInEx 自身程序集的解析）。
3. **shim 用了 MonoMod，就必须在移交前解开它的平台锁**。`MonoMod.Utils.PlatformHelper` 首次读取
   就把检测结果写进 `_current` 并把 `_currentLocked` 置 true，之后 `set_Current` 抛
   `Cannot set the value of PlatformHelper.Current once it has been accessed.`。
   而 BepInEx `PreloaderMain()` 的第一件事正是 `PlatformUtils.SetPlatform()`（设成真实的 IL2CPP）
   ——装 detour 读一次平台就意味着 preloader 直接抛错、游戏卡在 BepInEx 的报错弹窗上
   （实测：`preloader_*.log` 只有那一行异常，`LogOutput.log` 不新增）。所以
   `BepInExInteropBridge.ResetPlatformCache()` 在移交前把 `_currentLocked` 清回 false，
   把平台判定交还 BepInEx（它会按真实运行时设成 IL2CPP）。自检里对应
   `[shim] unlocked MonoMod platform cache (_currentLocked = false)`。

回滚：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\doorstop-shim\install.ps1 -Revert
```

## 启动后看什么

1. `BepInEx\gakumas-shim.log`：`[scan] ...`（常量）、`[shim] injecting Il2CppCodeRegistration: ...`、
   `[shim] unlocked MonoMod platform cache (_currentLocked = false)`、`[shim] handing over to ...`、
   `[shim] handover returned`（最后一行出现即表示 BepInEx 整条链跑完并正常返回）。
   第二行 hash 预检里：首次或镜像/模块变动时打印完整 `hash runtime=… image=… stored=… gate(…)`，
   未变动时打印 `preflight hashes reused (image and runtime module unchanged)`（省 ~0.8 s）；
   若镜像缺失或过期，会告警并提示 `refresh ImagePath`（此时若 BepInEx 决定重新生成就会失败）。
2. `BepInEx\LogOutput.log`：
   - 首次（`UpdateInteropAssemblies = true` 且镜像/unity-libs/生成器变化时）会出现
     `Detected outdated interop assemblies, will regenerate them now`，随后 Cpp2IL/Il2CppInterop 跑生成（约 83 s）；
   - 之后每次启动 hash 命中，生成步骤 1 秒内空转结束，直接加载 interop 与插件
     （判断依据：`BepInEx\interop\Assembly-CSharp.dll` 的 mtime 不变 + 日志里没有 `Detected outdated`）。
3. 若关了 `UpdateInteropAssemblies`，BepInEx 会打印
   `Interop assemblies are possibly out of date. To disable this message, create file <...assembly-hash.txt> with the following contents: <hash>`
   —— 把该 hash 写进 `BepInEx\interop\assembly-hash.txt`（**不带换行**，32 字节）即可静音；
   hash 的输入是：`BepInEx/interop` 对应的镜像文件 + `BepInEx/unity-libs` 下所有文件 + 生成器版本。

## 限制

- 必须是解密镜像；packed 镜像会被扫描器拒绝（`genericMethodPointers run not found ... image is probably still packed`）。
- 镜像必须**不早于** `<GameRoot>\GameAssembly.dll`（游戏更新后镜像会过期）。过期时 shim 会告警并停用镜像覆盖，
  让生成阶段读 packed 文件而**响亮失败**（日志出现 `Hit backtrack limit`），而不是静默生成错误 interop。
- 常量缓存按镜像大小/mtime/首尾 1MB 哈希失效；换了游戏版本要同时换镜像并重跑 `install.ps1 -ImagePath`。
- interop 运行时生成需要 `BepInEx\unity-libs\`（已有 6000.0.77 的 `*.zip` 与解包结果即可，不必联网）。
- 生成后的 interop 与镜像绑定；插件需要针对新生成的 `Assembly-CSharp.dll` 等重新编译。
- 部署时若目标 DLL 被占用：先尝试原地覆写，再退到 `BepInEx\core\shim\`（文件名不变，见上一节）；两者都失败就报错，
  **不会**改名部署。游戏运行中部署会被前置检查拦下（`gakumas.exe is running`）。
- `BepInEx\core\GakumasDoorstopShim.dll` 被某进程长期映射时会无法删除；它已不再是 doorstop 目标，属惰性残留
  （句柄释放后可手动删；`install.ps1` 与 `-Revert` 每次都会重试清理并告警）。
- 反作弊面不变：仍是进程内注入（doorstop 时代如此），不引入新的跨进程行为。
