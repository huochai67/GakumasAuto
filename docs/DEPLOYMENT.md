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

- Windows 10 / 11 x64；下文以游戏目录 `X:\path\to\gakumas` 为例。
- 官方 BepInEx 6（Bleeding Edge 6.0.0-be.785+，Unity IL2CPP x64）与 doorstop 4.x 已装入游戏目录：
  `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、`BepInEx\{core,config,interop,plugins}` 齐备。
- .NET SDK（构建插件，目标 net6.0；仓库以 .NET 8 SDK 验证）。
- Node.js v18+（MCP 服务与 CLI）。
- Python 3.9+（仅解密器需要；stage-2b 的 body 解码要调用镜像内的 x64 helper，故必须 Windows）。
- 首次生成 interop 需要 `BepInEx\unity-libs\`（Unity 6000.0.77 的 `*.zip` 或解包结果，离线即可）。

## 3. 端到端部署流程

完整的部署流程遵循依赖递进关系：**先解密并部署 Shim → 启动游戏生成 Interop 绑定程序集 → 编译并安全部署插件 → 配置 MCP 客户端**。

```text
步骤 1: 生成解密镜像 (ga-static-decrypt)
   │
   ▼
步骤 2: 部署 Shim 并启用生成 (doorstop-shim install.ps1 -EnableInteropUpdate)
   │
   ▼
步骤 3: 启动游戏，BepInEx 运行时生成 interop (耗时约 83s，产出 BepInEx\interop\*.dll)
   │
   ▼
步骤 4: 编译插件 (dotnet build plugin，依赖刚生成的 interop 程序集)
   │
   ▼
步骤 5: 安全部署插件 (deploy-plugin.ps1，校验环境完整性与 hash，执行原子备份与复制)
   │
   ▼
步骤 6: 配置并启动 MCP 服务 (.mcp.json，对接 AI Agent 或本地 CLI 调试)
```

---

### 3.1 步骤 1：生成解密镜像

使用离线解密器将 packed `GameAssembly.dll` 重建为标准 PE 镜像：

```powershell
python tools\ga-static-decrypt\ga_static_decrypt.py `
  X:\path\to\gakumas\GameAssembly.dll out\GameAssembly_static_exact.dll
```

> **说明**：密钥默认通过内存与特征算法自动扫描（记录表/码表/key/helper/payload/sbox）；亦可通过 `--dump`、`--carve` 或 `--profile` 复用已捕获的输入。交付门禁用 `--expect`/`--expect-hash`/`--expect-records` 校验，完整用法参见 `tools/ga-static-decrypt/README.md`。

### 3.2 步骤 2：部署 Shim 并接管 Interop 生成

使用安装脚本将 Doorstop Shim 接入游戏加载链，并启用 Interop 自动更新：

```powershell
# 建议先带 -DryRun 预览改动
powershell -NoProfile -ExecutionPolicy Bypass -File tools\doorstop-shim\install.ps1 `
  -GameRoot 'X:\path\to\gakumas' `
  -ImagePath 'out\GameAssembly_static_exact.dll' `
  -RestoreDoorstopProxy `
  -EnableInteropUpdate `
  -DryRun
```

确认无误后**去掉 `-DryRun`** 正式执行。
脚本仅改动必要位置并均保留 `*.gakumas-shim.bak` 备份（包括 `BepInEx\core\GakumasDoorstopShim.*`、`BepInEx\gakumas-decrypted\<镜像>`、`doorstop_config.ini`、`BepInEx\config\BepInEx.cfg`、`winhttp.dll`）。安装完毕后脚本会自动调用 `host-probe` 进行无侵入宿主自检，若自检未通过将立即返回 `exit 6`，防止出现静默加载失败。

### 3.3 步骤 3：首次启动游戏生成 Interop 程序集

必须在**独立**的 PowerShell 或 CMD 窗口中启动游戏（禁止在 Agent 终端的子进程中运行，避免随会话结束被强制终止）：

```powershell
Start-Process -FilePath 'X:\path\to\gakumas\gakumas.exe' `
  -ArgumentList '/viewer_id=<user_id>','/open_id=<open_id>','/pf_access_token=<token>' `
  -WorkingDirectory 'X:\path\to\gakumas'
```

1. **观察生成过程**：首次启动时，`BepInEx\LogOutput.log` 会输出 `Detected outdated interop assemblies, will regenerate them now`。在 Shim 动态注入 codereg 常量后，BepInEx 自身管线开始执行 Cpp2IL 和 Il2CppInterop 生成（首次全量约 83 秒）。
2. **生成完毕后关闭游戏**。
3. **锁定配置**：生成完成后，打开 `BepInEx\config\BepInEx.cfg`，将 `[IL2CPP]` 节下的 `UpdateInteropAssemblies` 改回 `false`（后续插件部署脚本的前置检查项强依赖该值处于关闭状态；推荐基线配置可参考 `docs/BepInEx.cfg.example`）。

### 3.4 步骤 4：构建插件

在游戏生成完整的 `BepInEx\interop\*.dll` 之后编译插件（插件工程 `plugin/GakumasAuto.csproj` 直接引用游戏目录内的 interop 程序集）：

```powershell
dotnet build plugin\GakumasAuto.csproj -c Release
```

编译产物位于 `plugin\bin\Release\net6.0\GakumasAuto.dll`。

### 3.5 步骤 5：安全部署插件

使用仓库提供的部署脚本安装插件到游戏目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\deploy-plugin.ps1 `
  -TargetDir 'X:\path\to\gakumas' `
  -PluginPath '.\plugin\bin\Release\net6.0\GakumasAuto.dll'
```

- `-PluginPath` 省略时默认自动定位至 `..\plugin\bin\Release\net6.0\GakumasAuto.dll`。
- `-VerifyOnly` 可用于仅执行环境完整性校验，不复制文件。
- **前置防护门禁**：脚本执行前会严格校验目标目录的 3 个 `BepInEx\core` 运行库、3 个关键 `BepInEx\interop` 程序集、32 位无换行的 `assembly-hash.txt`，以及 `UpdateInteropAssemblies = false`。若游戏正在运行或任一项不满足，脚本将拒绝改动并立即退出。
- **自动备份**：部署成功前会自动将旧版本插件备份至 `BepInEx\.gakumas-auto-backup-<时间戳>\`。

### 3.6 步骤 6：配置 MCP 客户端

1. 复制配置文件模板：
   ```powershell
   Copy-Item .mcp.example.json .mcp.json
   ```
2. 修改 `.mcp.json` 中的 `GAKUMAS_BEPINEX` 路径指向实际的 `X:\path\to\gakumas\BepInEx`。
3. 在 Agent 宿主（Claude Code、Cursor、Roo Code 等）中挂载后，即可通过标准 stdio 协议调用全部 57 个 MCP 工具。

在不启动 Agent 的情况下，也可以随时使用本地命令行直连调试：
```powershell
node mcp\cli.js state             # 底层文件通道原始状态调试
node mcp\tool.js <tool> [k=v]     # 高层 MCP 工具逻辑直接调用
```
## 4. 验证

1. `BepInEx\LogOutput.log`：插件版本已加载、`AutoDriver` 已附加，无 `DllNotFound`、无 interop 过期告警。
2. `BepInEx\gakumas-shim.log`：`[scan]` 常量、`injecting Il2CppCodeRegistration`、
   `unlocked MonoMod platform cache (… = false)`、`handing over to …`、`handover returned`。
3. `node mcp\cli.js state` 返回当前界面、用户与 Loading 状态。
4. 手工通道冒烟（文件必须 UTF-8）：

```powershell
Set-Content -Path 'X:\path\to\gakumas\BepInEx\gakumas-ui-cmd.json' `
  -Value '{"id":"manual-1","action":"state"}' -Encoding UTF8
Start-Sleep 3
Get-Content 'X:\path\to\gakumas\BepInEx\gakumas-ui-resp.json'
```

## 5. 游戏更新后的重新部署

1. 重新生成解密镜像（3.2）。镜像必须**不早于** `<GameRoot>\GameAssembly.dll`，否则 shim 会告警并
   停用镜像覆盖，让生成阶段读 packed 文件而**响亮失败**（日志 `Hit backtrack limit`），不会静默产出错误 interop。
2. `tools\doorstop-shim\install.ps1 -GameRoot … -ImagePath <新镜像> -EnableInteropUpdate`
   （codereg 常量按镜像大小/mtime/首尾 1 MB 哈希失效，无需手工清理缓存）。
3. 启动一次游戏，让 BepInEx 重新生成 interop。
4. 重新编译插件——interop 与镜像绑定，`Assembly-CSharp.dll` 等会随镜像变化。

## 6. 回滚

- 插件：`Remove-Item 'X:\path\to\gakumas\BepInEx\plugins\GakumasAuto.dll'`，或从
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
