# 部署文档

把 GakumasAuto 部署到 DMM 版 gakumas 客户端（Windows x64）。仓库不捆绑 BepInEx 二进制快照：
BepInEx 与 doorstop 由使用者从官方渠道安装；本仓库只提供解密器、interop shim、插件与部署脚本。

## 1. 组件与产物

| 组件 | 位置 | 作用 |
|------|------|------|
| 插件 | `plugin/` → `plugin/bin/Release/net6.0/GakumasAuto.dll` | 进程内 UI 自动化、指令通道、MCP 结构化回传 |
| 解密器 | `tools/ga-static-decrypt/` | 把 packed `GameAssembly.dll` 重建成可分析的 PE 镜像 |
| interop shim | `tools/doorstop-shim/` | doorstop 入口：注入 codereg 常量后把 interop 生成交回 BepInEx |
| 插件部署脚本 | `tools/deploy-plugin.ps1` | 只复制插件 DLL，不改动 BepInEx 与游戏文件 |
| MCP 服务 | `mcp/server.js`（配置 `.mcp.json`） | 对 agent 暴露工具；`mcp/cli.js`、`mcp/tool.js` 直连调试 |

## 2. 前置条件

- Windows 10 / 11 x64；下文以游戏目录 `E:\DMM\gakumas` 为例。
- 官方 BepInEx 6（Bleeding Edge 6.0.0-be.785+，Unity IL2CPP x64）与 doorstop 4.x 已装入游戏目录：
  `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`BepInEx\{core,config,interop,plugins}` 齐备。
- .NET SDK（构建插件，目标 net6.0；仓库以 .NET 8 SDK 验证）。
- Node.js v18+（MCP 服务与 CLI）。
- Python 3.9+（仅解密器需要；stage-2b 的 body 解码要调用镜像内的 x64 helper，故必须 Windows）。
- 首次生成 interop 需要 `BepInEx\unity-libs\`（Unity 6000.0.77 的 `*.zip` 或解包结果，离线即可）。

## 3. 部署步骤

### 3.1 构建插件

```powershell
dotnet build plugin\GakumasAuto.csproj -c Release
```

### 3.2 生成解密镜像

```powershell
python tools\ga-static-decrypt\ga_static_decrypt.py `
  E:\DMM\gakumas\GameAssembly.dll out\GameAssembly_static_exact.dll
```

密钥默认自动扫描（记录表/码表/key/helper/payload/sbox）；也可用 `--dump`/`--carve`/`--profile`
复用已捕获的输入。交付门禁用 `--expect`/`--expect-hash`/`--expect-records`。
细节见 `tools/ga-static-decrypt/README.md`。

### 3.3 部署 shim，让 BepInEx 自己生成 interop

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\doorstop-shim\install.ps1 `
  -GameRoot 'E:\DMM\gakumas' `
  -ImagePath '<3.2 产出的解密镜像>' `
  -RestoreDoorstopProxy `
  -EnableInteropUpdate `
  -DryRun
```

确认要改动的内容后去掉 `-DryRun` 重跑。脚本只写 `BepInEx\core\GakumasDoorstopShim.*`、
`BepInEx\gakumas-decrypted\<镜像>`、`doorstop_config.ini`、`BepInEx\config\BepInEx.cfg`、`winhttp.dll`，
并都留 `*.gakumas-shim.bak` 备份；安装后自动用 `host-probe` 自检，失败即 `exit 6`（避免「游戏能开、
BepInEx 静默不加载」）。细节与回滚见 `tools/doorstop-shim/README.md`。

### 3.4 首次启动游戏

在**独立的** PowerShell / CMD 里启动（不要从 agent 终端开子进程：登录参数会过期）：

```powershell
Start-Process -FilePath 'E:\DMM\gakumas\gakumas.exe' `
  -ArgumentList '/viewer_id=<user_id>','/open_id=<open_id>','/pf_access_token=<token>' `
  -WorkingDirectory 'E:\DMM\gakumas'
```

首次启动日志出现 `Detected outdated interop assemblies, will regenerate them now`，约 83 s 生成
`BepInEx\interop\`；之后每次启动 hash 命中，生成步骤 1 s 内空转结束。

生成完成后把 `BepInEx\config\BepInEx.cfg` 的 `UpdateInteropAssemblies` 置回 `false`（3.5 的前置检查
要求它为 `false`）；`docs/BepInEx.cfg.example` 是推荐基线，含该值与日志、缓存等设置。

### 3.5 部署插件

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy-plugin.ps1 `
  -TargetDir 'E:\DMM\gakumas' `
  -PluginPath '.\plugin\bin\Release\net6.0\GakumasAuto.dll'
```

`-PluginPath` 省略时默认取 `..\plugin\bin\Release\net6.0\GakumasAuto.dll`（相对脚本位置）；
`-VerifyOnly` 只校验目标目录完整性；脚本会先备份旧 DLL 到 `BepInEx\.gakumas-auto-backup-<时间戳>\`。
前置检查包括 `BepInEx\core` 三个运行库、`BepInEx\interop` 三个程序集、`assembly-hash.txt`（32 位十六
进制、无换行）以及 `UpdateInteropAssemblies = false`；缺任何一项即失败且不做任何改动。

### 3.6 接入 MCP

```powershell
Copy-Item .mcp.example.json .mcp.json    # 按需修改 GAKUMAS_BEPINEX 指向 <GameRoot>\BepInEx
```

MCP 客户端即可看到 `gakumas` 服务（stdio，`node mcp/server.js`）。直连调试：

```powershell
node mcp\cli.js state            # 文件通道调试工具
node mcp\tool.js <tool> [k=v]    # 直接调用 MCP 工具实现
```

## 4. 验证

1. `BepInEx\LogOutput.log`：插件版本已加载、`AutoDriver` 已附加，无 `DllNotFound`、无 interop 过期告警。
2. `BepInEx\gakumas-shim.log`：`[scan]` 常量、`injecting Il2CppCodeRegistration`、
   `unlocked MonoMod platform cache (… = false)`、`handing over to …`、`handover returned`。
3. `node mcp\cli.js state` 返回当前界面、用户与 Loading 状态。
4. 手工通道冒烟（文件必须 UTF-8）：

```powershell
Set-Content -Path 'E:\DMM\gakumas\BepInEx\gakumas-ui-cmd.json' `
  -Value '{"id":"manual-1","action":"state"}' -Encoding UTF8
Start-Sleep 3
Get-Content 'E:\DMM\gakumas\BepInEx\gakumas-ui-resp.json'
```

## 5. 游戏更新后的重新部署

1. 重新生成解密镜像（3.2）。镜像必须**不早于** `<GameRoot>\GameAssembly.dll`，否则 shim 会告警并
   停用镜像覆盖，让生成阶段读 packed 文件而**响亮失败**（日志 `Hit backtrack limit`），不会静默产出错误 interop。
2. `tools\doorstop-shim\install.ps1 -GameRoot … -ImagePath <新镜像> -EnableInteropUpdate`
   （codereg 常量按镜像大小/mtime/首尾 1 MB 哈希失效，无需手工清理缓存）。
3. 启动一次游戏，让 BepInEx 重新生成 interop。
4. 重新编译插件——interop 与镜像绑定，`Assembly-CSharp.dll` 等会随镜像变化。

## 6. 回滚

- 插件：`Remove-Item 'E:\DMM\gakumas\BepInEx\plugins\GakumasAuto.dll'`，或从
  `BepInEx\.gakumas-auto-backup-*\GakumasAuto.dll` 恢复。
- shim：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\doorstop-shim\install.ps1 -Revert`
  （还原 `doorstop_config.ini`、`BepInEx.cfg`、`winhttp.dll` 的备份）。
- `BepInEx\interop\` 与 BepInEx 本体不由本仓库安装，按需自行清理。

## 7. 已知限制与风险

- **反作弊/封号**：全程进程内注入（doorstop 时代即如此），未观察到 AC 驱动拦截，但**不保证长期安全**，风险自担。
- **版本绑定**：interop 程序集与解密镜像（= GameAssembly 版本）绑定；插件必须针对新生成的 interop 重编译。
- **插件签名约束**：MonoBehaviour 方法禁止暴露托管类型（DTO/`List<T>`/`StringBuilder`），一律经
  `GakumasAutoPlugin.Shared*` 静态字段路由——详见 `docs/PLUGIN-DEV-GUIDE.md`。
- **UI 抓取上限**：`layout` 最多 3000 节点 / 深度 12，`layout2` 无上限，`find` 最多 300 匹配。
- **部署时机**：游戏运行中部署会被脚本前置检查拦下（`gakumas.exe is running`）。

相关文档：`docs/PLUGIN-DEV-GUIDE.md`（插件开发约束）、`tools/ga-static-decrypt/README.md`（解密器）、
`tools/doorstop-shim/README.md`（shim 实现、自检与限制）。
