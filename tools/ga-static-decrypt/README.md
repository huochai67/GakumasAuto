# ga-static-decrypt

针对 gakumas `GameAssembly.dll`（KONN 加壳）的**离线静态解密器**。Python 实现，Windows x64 + Python 3.9+
（stage-2b 的 body 解码需要调用该版本镜像里的 x64 helper，故必须 Windows）。

## 处理链

```text
stage 1   外层壳：0x1000 头的滚动异或解码 → key / 目标 RVA / split，
          节映射，0x6E0 dword 流解密（chunk）+ 尾部原样拷贝（tail）
stage 2   加载器自解密：ROL11/ROL13 指针记录 → helper 探针校验 → ROL3/XOR 区
          → MAP / FLAG 记录（嵌套前缀、16 B 子记录拷贝）
stage 2b  body 解码：每条记录 每 16 B 调 helper(key, 14) → 异或链 → sbox
          → pk == un 直接拷贝，否则按 3 B 码表做比特流解包
stage 3   六段 PE 重建（按记录数选择 per-build preset；见下）
```

与已退役的 .NET 版（`ga_static_decrypt.cs` / `Stage2.cs` / `FullBodyDecrypt.cs`）逐句对应；stage 1 的输出
已实测**字节级一致**。

## 密钥来源

stage 2b 需要五件输入，按固定优先级解析，先命中者胜：

| 输入 | 手工 | carve | profile | dump 扫描 | 镜像扫描 |
|------|------|-------|---------|-----------|----------|
| records（记录表） | `--records` | `records.bin`/`records.pkl` | guard blob `@0x2E0` | 结构判据 | ✗ 见下 |
| pass3（3 B 码表） | `--pass3` | `pass3.bin` | `pass3tab*.bin` | 结构判据 | 尝试 |
| key（240 B） | `--key` | `key.bin` | `*key*.bin` | pass3 + 0x10C6 | pass3 + 0x10C6 |
| helper（0x20000 窗口，入口 +0x8000） | `--helper` | `helper_blk.bin` | helper_tab + helper_fn | pass3 − 0xFB40 | pass3 − 0xFB40 |
| payload（打包流游标） | `--payload` | `manifest.json` | ✗ | 见下 | 见下 |
| sbox（256 B） | `--sbox` | `sbox.bin` | ✗（用内置表） | `--reference` 推导 | `--reference` 推导 |

**边界（实测，非猜测）**：明文记录表与码表**只存在于加载器的独立 MEM_IMAGE 工作区**，镜像内
`stage-1 dest` 处（如 `0xBD6C000`）的运行态与静态内容**始终是密文**（案卷 E-010 / §11.4）。所以
"从 packed 文件直接扫出记录表"不可行——自动扫描的对象是**工作区 dump**（`--dump`，即
`notes/stable-inputs.py locate` 的输入）。pass3/key/helper 在镜像里能否命中取决于构建（扫描失败时用
`--carve`/`--profile`/手工输入）。

**sbox 是逐构建的**：内置表（`rol8((v+1)^0xB0,7)+0x0E`）只对应冻结构建；2026-09-17 构建的 carve
`sbox.bin` 与内置表 **256/256 字节全不同**。给出 `--reference` 时工具会按多数投票重新推导，
`--sbox`/`--carve`/`--profile` 亦可直接指定——没有参考镜像也没有 sbox 时，不变量式 oracle 无法确认
payload，工具会回落到提示值并在门禁处失败（不会静默出错）。

**payload 与 sbox 的自证**：

- `--reference <已知镜像>` 时：payload 由"首条 `pk == un` 记录的 helper 输出与参考镜像零冲突"唯一确定；
  sbox 由全部 `pk == un` 记录的多数投票推导（并打印与内置表的异同）。
- 无参考镜像时：payload 由**流不变量**（每条压缩记录 `(bits + 7) // 8 == pk`）从提示值 `0xE3580`
  向外逐级搜索确认——逐候选先解 1 条粗筛、命中后再解全部 6 条采样记录；sbox 用内置表
  （`rol8((v+1)^0xB0,7)+0x0E`，只对冻结构建成表，其余构建须给 `--reference`/`--sbox`/carve）。
- 都不成立时回落到提示值，并在后续门禁处失败，不会静默产出垃圾。

## 用法

```powershell
# 1) 自动：从工作区 dump 扫描密钥（推荐；一条命令覆盖新构建）
python tools\ga-static-decrypt\ga_static_decrypt.py `
  E:\DMM\gakumas\GameAssembly.dll out\GameAssembly_static_exact.dll `
  --dump <loader-workspace-dump.bin> --reference <known-good-image.bin>

# 2) carve：notes/stable-inputs.py 的产物目录（key/sbox/pass3/records/helper_blk + manifest.json）
python tools\ga-static-decrypt\ga_static_decrypt.py <packed> <out> `
  --carve <carve-dir> --reference <known-good-image.bin>

# 3) profile：.NET 版风格的冻结目录（helper_tab/helper_fn/key/pass3tab/guard_s2）
python tools\ga-static-decrypt\ga_static_decrypt.py <packed> <out> --profile <profile-dir>

# 4) 手工：逐项指定（例如只有从别处拿到的 240 B key）
python tools\ga-static-decrypt\ga_static_decrypt.py <packed> <out> `
  --key key.bin --sbox sbox.bin --pass3 pass3.bin --records records.bin `
  --helper helper_blk.bin --payload 0xE3580

# 5) 诊断：只跑 stage 1（~1 s；产物是 packed 布局骨架，99.5% 为零，任何生成器都不接受）
python tools\ga-static-decrypt\ga_static_decrypt.py <packed> stage1.dll --stage1-only

# 6) 交付：冻结本次解析出的输入，供下次直接复用
python tools\ga-static-decrypt\ga_static_decrypt.py <packed> <out> --emit-profile out\carve
```

其它开关：`--no-scan`（禁用扫描，要求手工/profile/carve）、`--no-self-decrypt` / `--force-self-decrypt`
（stage 2 自解密）、`--no-rebuild-pe` / `--sections <preset|json>` / `--sections-from <同构建镜像>`
（stage 3：无 preset 时可直接沿用参考镜像的头部与节表）、
`--expect <image>` / `--expect-hash <sha256>` / `--expect-records <n>`（交付门禁）、
`--stage1-only` 的 `--header-key/--header-rva/--header-split`（手工覆盖外层头字段）。

## 门禁（任一不满足即失败退出）

- 每条记录 `0x1000 <= dest`、`pk/un <= 0x1000`、`dest + un` 在镜像内；
- 记录流游标不越过 packed 文件末尾；
- `(bits + 7) // 8 == pk` 对**所有**压缩记录成立（`invalid == 0`）；
- `--expect-records` 的条数完全一致（构建指纹：冻结版 47,706、2026-09-17 版 47,773）；
- 给了 `--reference` 时打印逐字节命中率；给了 `--key` 之外的来源时先跑采样 key oracle。

## 已验证事实

| 项目 | 结果 |
|------|------|
| stage 1 输出（当前构建，199,573,504 B） | 与 .NET 版产物**逐字节相同**（sha256 `fa496d35bfd51b10…`） |
| `--dump` 自动扫描（2026-09-17 工作区 dump，12,005,376 B） | records `@0xA8B2E0` **47,773 条**、pass3 `@0xB71000` **1,431 条**、key `@0xB720C6` sha256 `70cbf456603d4780…`、helper `@0xB614C0`；payload 参考 oracle 唯一命中 `0xE3990`；sbox 由参考镜像推导（coverage 256/256、conflicts 0、sha256 `674e1093…`） ⇒ 产物与部署镜像**逐字节相同**，`--emit-profile` 产物与案卷 carve 逐文件一致 |
| `--profile`（冻结构建，47,706 条） | 端到端产物 sha256 = README 文档值 `8f45c2b2…58e127`（`--expect-hash` 通过）；`streams=47,706/0`；重建前与 `run-old-20260919` 参考镜像逐字节命中 195,097,053/195,097,056（99.999998%，3 字节残差在重建前，重建后与文档哈希逐字节相同） |
| 当前构建（2026-09-17，47,773 条） | `--carve` + `--sections-from` 产物 sha256 = `7ccc9343…4709d`，与部署镜像**逐字节相同**（`--expect` differing=0）；key oracle 12/12；`streams=47,773/0` |
| 解码耗时 | 约 160 s（47,773 条记录；helper 逐 16 B 调用是瓶颈——Python 侧的异或链与 sbox 已用 big-int XOR / `bytes.translate` 做到 C 速度） |
| sbox | 逐构建；冻结版 = 内置表，2026-09-17 版与之 256/256 字节不同（由 carve/`--reference` 提供） |

## 版本更新

1. 启动一次游戏并捕获加载器工作区 dump（案卷 `work/` 下的做法），或复用上次的 `--emit-profile` 产物；
2. `--dump <新dump> --reference <新镜像> --emit-profile out\carve-<build>` ⇒ 冻结该构建的输入；
3. 若该构建的 **stage-2 自解密布局**或**六段 PE 布局**发生变化：自解密布局不匹配时会打印
   `[skip] stage-2 self-decrypt: ... does not fit this image` 并继续（不破坏镜像）；PE 重建会打印
   `no section preset for N records` 并保留 packed 头。两者都属于 per-build 数据：
   前者按新构建的 RVA 加一个 preset（`STAGE2_SELF_DECRYPT` 里的探针必须全部通过），
   后者用 `--sections <json>` 提供。

## 已删除的 .NET 实现

`ga_static_decrypt.cs` / `Stage2.cs` / `FullBodyDecrypt.cs` / `ga-static-decrypt.csproj` 已随本工具的 Python
重写一并删除（源码存于 git 历史）。保留对照用的两处事实：stage-2 自解密布局与六段 PE 布局的 per-build
常量，以及 `.NET` 版内置的 sbox 公式（= 本工具的内置表）。
