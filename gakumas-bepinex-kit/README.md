# gakumas BepInEx 部署快照

> 本目录是 2026-08-15 的可复现部署快照：Unity 6000.0.77f1 / IL2CPP metadata v31.1 / BepInEx 6.0.0-be.785。
> 快照内插件版本：GakumasAuto v2.0.2；仓库根目录 `plugin/` 当前版本为 v2.2.1。两者不要混装。

该目录提供插件部署脚本和配置示例，不再公开捆绑 BepInEx 二进制、doorstop、interop 或 Unity 引用程序集。当前版本的插件源码、解密器和 interop 生成流程以仓库根目录为准。

## 内容

| 路径 | 说明 |
|------|------|
| `BepInEx/config/BepInEx.cfg.example` | 配置示例；安装后由用户复制/修改目标目录中的 BepInEx.cfg |
| `BepInEx/core/` | 本地私有快照目录，不属于公开发布内容 |
| `BepInEx/interop/` | 本地私有快照目录，不属于公开发布内容 |
| `BepInEx/plugins/` | 本地构建的插件 DLL，不属于公开发布内容 |
| `BepInEx/unity-libs/` | 本地私有快照目录，不属于公开发布内容 |
| `samples/GakumasAuto/` | v2.0.2 合并版插件源码样例 |
| `docs/PLUGIN-DEV-GUIDE.md` | 插件开发手册（含 MCP 集成说明） |
| `deploy.ps1` | 检查外部 BepInEx 并只部署 GakumasAuto.dll |

## 当前版本工具链

公开发布不携带 BepInEx 二进制。用户应先从官方渠道安装与游戏版本匹配的 BepInEx 6，再准备匹配的 interop：

1. 用 `tools/ga-static-decrypt/` 和匹配版本 profile 重建 `GameAssembly_static_exact.dll`。
2. 用 `tools/interop-gen/` 读取重建 PE、`global-metadata.dat` 和 Unity libs。
3. 将 interop 放入目标 `BepInEx/interop/`，复制仓库中的 `BepInEx/config/BepInEx.cfg.example` 为目标 `BepInEx/config/BepInEx.cfg`，保持 `UpdateInteropAssemblies = false`。
4. 构建插件，然后运行 `deploy.ps1 -TargetDir <游戏目录> -PluginPath <GakumasAuto.dll>`。

脚本只复制 `BepInEx/plugins/GakumasAuto.dll`，不会安装、覆盖或校验仓库内的 BepInEx 二进制快照。解密器和 interop 生成器的输入、版本约束和完整命令见各自 README。

> MCP 服务器在仓库根目录 `mcp/server.js`。运行时设置 `GAKUMAS_BEPINEX` 指向目标游戏的 `BepInEx` 目录。

## 部署

脚本不会安装或覆盖 BepInEx，只检查外部环境，然后复制一个插件 DLL：

```powershell
# 从仓库根目录执行；也可以用绝对路径
# 先构建根目录插件，或指定已有 DLL
powershell -NoProfile -ExecutionPolicy Bypass -File .\gakumas-bepinex-kit\deploy.ps1 `
  -TargetDir 'E:\DMM\gakumas' `
  -PluginPath '.\plugin\bin\Release\net6.0\GakumasAuto.dll'

# 只校验外部 BepInEx、interop 和已部署插件
powershell -NoProfile -ExecutionPolicy Bypass -File .\gakumas-bepinex-kit\deploy.ps1 `
  -TargetDir 'E:\DMM\gakumas' -VerifyOnly
```

部署前置：目标目录已有匹配版本的 BepInEx 6、doorstop、`BepInEx\core`、`BepInEx\interop` 和 `BepInEx.cfg`，且 `UpdateInteropAssemblies = false`。脚本只备份和替换 `BepInEx\plugins\GakumasAuto.dll`。
脚本失败时不会自动安装、下载或修补任何 BepInEx 二进制。

## 验证

```powershell
# 1. 在独立的 PowerShell/CMD 中启动游戏；不要从 agent 终端启动子进程
#    （登录参数向用户索取，token 会过期）
Start-Process -FilePath 'E:\DMM\gakumas\gakumas.exe' `
  -ArgumentList '/viewer_id=<user_id>','/open_id=<open_id>','/pf_access_token=<token>' `
  -WorkingDirectory 'E:\DMM\gakumas'

# 2. 等待约 30s，检查链加载
Get-Content 'E:\DMM\gakumas\BepInEx\LogOutput.log' -Tail 20
#   期望：日志显示所部署插件的版本已加载、AutoDriver 已附加、无 DllNotFound 或 interop 过期错误
```

指令通道冒烟使用仓库根目录 CLI，避免手写文件时发生编码或原子发布问题：

```powershell
node mcp\cli.js state
```

也可以直接写命令文件（必须 UTF-8），然后读取响应：

```powershell
Set-Content -Path 'E:\DMM\gakumas\BepInEx\gakumas-ui-cmd.json' `
  -Value '{"id":"manual-1","action":"state"}' -Encoding UTF8
Start-Sleep 3
Get-Content 'E:\DMM\gakumas\BepInEx\gakumas-ui-resp.json'
```

## 回滚

脚本只修改插件 DLL，回滚不会删除 BepInEx 或游戏文件：

```powershell
Remove-Item 'E:\DMM\gakumas\BepInEx\plugins\GakumasAuto.dll' -Force
# 或恢复 BepInEx\.gakumas-auto-backup-*\GakumasAuto.dll
```

## 已知限制

- interop 程序集与 **GameAssembly 版本绑定**：游戏更新后需用仓库里的 `tools/interop-gen/` 重跑离线生成器（输入必须是解密/重建后的 GameAssembly dump，不是盘上的 packed dll）
- BepInEx 不修改游戏文件；封号风险由使用者自担（进程内注入，AC 驱动未拦截但无法保证长期安全）
- 插件 MonoBehaviour 方法禁止暴露托管类型（DTO/List/StringBuilder）签名——Il2CppInterop 会替换为 substitute 并导致调用异常；一律经插件类静态字段路由（详见 PLUGIN-DEV-GUIDE §限制）
- layout 上限 500 节点/深度 12；主界面全量约 500+，超出部分截断（`find` 按需收敛）
