# 架构一页（codeusagemonit for macOS）

面向改代码的人：三工程分层、两条数据流、数据目录、同步拓扑。行号基于 2026-10-08 代码，
路径约定同 `docs/providers/README.md`（`Core/` = src/CodeUsageMonit.Core/ 等）。

## 三工程分层

见 `CodeUsageMonit.slnx`：

| 工程 | 职责 | 关键文件 |
|---|---|---|
| `CodeUsageMonit.Core` | 全部业务逻辑：11 平台日志解析、额度 API 适配、计价、索引、同步、CLI 逻辑 | `LocalLogs.cs`（解析框架 + Codex/Claude）、`AgentLogs.cs`（Sqlite/Proto + ZCode/Grok/Kimi/Antigravity）、`MoreAgentLogs.cs`（Pi/Copilot/OpenCode）、`DeepSeekHarness.cs`、`CursorUsage.cs`、`Core.cs`（模型 + ProviderService + Store）、`DataSync.cs`、`Cli.cs` |
| `CodeUsageMonit.Desktop` | Avalonia UI：面板、四种窗口尺寸、设置、托盘、watcher | `AppMonitor.cs`（主循环）、`Program.cs`、`App.axaml.cs` |
| `CodeUsageMonit.Cli` | 薄入口壳：转发到 Core 里的 `CliProgram.Main` | `Program.cs`（仅一个转发） |

CLI 是独立工程，薄壳引用 Core（`src/CodeUsageMonit.Cli/Program.cs` 头注释）。
打包：`build-macos.sh` → `dist/osx-arm64/CodeUsageMonit.app` + `dist/osx-arm64/cli/codeusage`，
GUI 与 CLI 共享同一数据目录。

## 数据流 A：本机用量（日志 → 索引 → history → UI / CLI）

```
各客户端会话日志（JSONL / zstd / SQLite / protobuf）
  → UsageScanner.ScanAll（并行扫启用的 agent，Core/UsageScan.cs:28-39）
      每个 agent 读 data/<id>-logs.json 里的增量状态（LogIndex v5），
      只解析新追加的行（Core/LocalLogs.cs:144-171）
  → data/<id>-logs.json   （小时桶：yyyyMMddHH|provider|model[|endpoint] → Bucket）
  → HistoryService.FromIndexes（UTC 小时桶按本地时区聚成日行，Core/UsageScan.cs:52-81）
  → HistoryService.Merge（过去日只增不减、今天永远替换，Core/Core.cs:860-868）
  → data/history.json
  → 面板 / codeusage cost / export / WebDAV
```

- 触发时机（`Desktop/AppMonitor.cs:403-420` `ScanHistory`）：手动刷新后、history 超过
  15 分钟未更新（`Desktop/AppMonitor.cs:374-376`）、或「有平台在用且 `FastInUse` 开启」
  时按 `InUseMinutes` 快扫（`Desktop/AppMonitor.cs:527-530`）。
- 「在用」由 FileSystemWatcher + 30 秒 LastWriteTime 轮询兜底标记，**只影响刷新节奏，
  不直接触发扫描**（`Desktop/AppMonitor.cs:462-481`、`Core/Pacing.cs:49-64`；
  `CountsAsWork` 已排除 `*-shm`/`*-wal` 侧车与本程序 `data/` 目录）。
  30 秒 tick 是唯一调度入口（`Desktop/AppMonitor.cs:130-132`、`Desktop/AppMonitor.cs:506-532`）。
- 导入的其他设备用量只在内存里 `Devices.Attach` 并进视图，重扫永不覆盖
  （`Core/DataSync.cs:90-127`、`Desktop/AppMonitor.cs:421-424`）。
- 中转站（第三方）看板：Claude / Codex 的 endpoint 时间线由 watcher 监听
  `settings.json` / `config.toml` / `auth.json` 变化记录（`Desktop/AppMonitor.cs:285-308`），
  用量按请求时刻归因（`Core/ThirdParty.cs:83-105`）。

## 数据流 B：账户额度（key → 钥匙串 → Fetch → quota-cache → UI）

```
凭据解析（Core/Core.cs:334-337 ProviderKey）：
  环境变量（Core/Core.cs:170-178 按平台列名） > data/<id>.key 里的 keychain:<id> 引用
  > （zcode / kimi 追加）本机 CLI 凭据静默导入（Core/Platform/LocalCliCredentials.cs:41-48）
  → ProviderService.Fetch(id)（Core/Core.cs:632-743，每平台一个 case；代理经
    ResolveProxy：设置 > scutil --proxy > HTTPS_PROXY/HTTP_PROXY，Core/Core.cs:569-575）
  → ProviderState {Quotas, Balances, ProductUsage, Status…}
  → data/quota-cache.json（每次刷新整表重写，Desktop/AppMonitor.cs:370）
  → 面板 / codeusage status（离线读缓存） / codeusage usage（实时联网）
```

- 保存密钥 = 写钥匙串成功才写 .key 引用文件；失败不落无效引用
  （`Core/Core.cs:347-353`）。钥匙串经 `security` 子进程，service
  `codeusagemonit`，4 秒超时（`Platform/Keychain.cs:14`、`48-65`）。
- 失败读数保留旧值并标 Stale（`Core/Core.cs:450-457` `RetainLastGood`）；
  同平台并发刷新 latest-wins（`Desktop/AppMonitor.cs:387-389`）。
- 刷新节奏：全局 `RefreshMinutes`，平台可覆盖（`ProviderMinutes`），在用时压到
  `InUseMinutes`（`Core/Pacing.cs:18-22`；配置项 `Core/Core.cs:113-117`）。

## 数据目录

选择逻辑 `Store.ResolveData`（`Core/Core.cs:235-243`），优先级从高到低：

1. exe 旁有 `portable.txt` → 强制 `<exe>/data`（**压过环境变量**）；
2. 环境变量 `CODEUSAGEMONIT_DATA`；
3. 安装标记（macOS 无注册表，`ReadInstallPath` 恒空，`Core/Core.cs:251-255`）；
4. macOS：`~/Library/Application Support/codeusagemonit`（GUI 与 CLI 共用；
   `SpecialFolder.ApplicationData` 在 mac 即 `~/Library/Application Support`，
   `Core/Core.cs:230-234`）；
5. 兜底 `<exe>/data`。

首次选择时若 exe 旁已有真实 data 目录而用户目录为空，自动迁移
（`Core/Core.cs:263-288`）。`--demo` 把数据目录指到 `verification/demo-data`，
绝不迁移真实目录（`Core/Core.cs:211-221`）。

目录内容（均在 `Store.Write` 下临时文件 + Replace 原子落盘，`Core/Core.cs:312-316`）：

| 文件 | 内容 | 写入方 |
|---|---|---|
| `settings.json` | AppConfig（启用平台、刷新、代理、区域、WebDAV…） | 设置保存 |
| `<id>-logs.json` ×11 | 各平台小时桶索引（LogIndex v5） | 扫描 |
| `history.json` | 30 天日历史 | 扫描 |
| `quota-cache.json` | 最近一次各平台额度读数 | 刷新 |
| `endpoints.json` | Claude/Codex 中转站切换时间线 | endpoint watcher |
| `device.json` | 本机设备 id（12 位 hex）+ 名称，首读生成 | `Devices.Self`（`Core/DataSync.cs:57-67`） |
| `devices/<id>.json` | 导入的其他设备小时用量 | 导入 / 同步 |
| `custom-providers.json` | 自定义平台定义串（不含密钥） | 设置 |
| `<id>.key` | 仅 `keychain:<id>` 引用，无密钥 | 保存密钥 |
| `webdav.key` | WebDAV 密码的钥匙串引用（KeyId=`webdav`，`Core/DataSync.cs:480`；设置里只存 URL/用户名，`Core/Core.cs:109-110`；`Cli.cs:230` 经 `SavedKey("webdav")` 取） | 设置 |
| `sync-state.json` / `errors.log` / `backups/` | 同步结果 / 未处理异常 / 备份 zip | 对应子系统 |

备份只含设置、索引、导入设备、背景图，**不含任何密钥**（`Core/DataSync.cs:406-413`）。

## WebDAV 同步拓扑（每设备单文件）

`Core/DataSync.cs:475-610`。设计目标：设备之间**互不覆盖**。

> 警告（Phase 4 初审 P2-2）：备份恢复会保持设备 id（`device.json` 随备份走）。**同一份备份不要恢复到两台同时在线的设备**——它们会上传同一个 `devices/<id>.sql` 互相覆盖对方的用量，且各自把对方的数据当「本机自己的行」跳过合并不进来；要并两台机器，各自保留（或重新生成）自己的设备 id。

```
<WebDAV 文件夹>/
  devices/<device id>.sql     ← 每设备一个文件（Core/DataSync.cs:475-477）
```

- `Run`（`Core/DataSync.cs:565-603`）：PROPFIND 探测 / MKCOL 建目录 →
  **PUT 只覆盖自己的 `<id>.sql`** → PROPFIND 列目录 → GET 其他每台设备的 sql →
  `ImportSql` 合并。文件名白名单 `^[A-Za-z0-9_-]{1,64}\.sql$`，最多 500 台
  （`Core/DataSync.cs:539-554`）。
- 导入决策（`Core/DataSync.cs:200-286`，逐小时自动判、不问用户）：
  本机自己的行跳过；同 (device, agent, hour, provider, model) 且 token/请求数与本地完全
  相等 = 同一份日志的副本，**去重不双计**（`SameUsage`，`Core/DataSync.cs:303-305`）；
  旧导出永不降低总数；同 token 不同 cost/耗时的新导出才更新。
- 下载按 64 MB / 文件、列表 4 MB 流式截断，空闲 120 秒断死连接
  （`Core/DataSync.cs:513-530`）；http 仅限本机/局网（`Core/DataSync.cs:481-488`）。
- Cursor 是账户级用量，**不导出也不导入**（`Core/DataSync.cs:52-53`）。
- SQL 导出 / 导入也可手动离线走：`codeusage export [文件]` / `codeusage import <文件>`
  （`Core/Cli.cs:213-222`；导出兼容 `sqlite3 usage.db < export.sql`，
  `Core/DataSync.cs:133`）。

## 平台差异速查

| 维度 | 差异点 | 位置 |
|---|---|---|
| 增量扫描 | codex/claude/grok/kimi/pi/deepseek 走 LogReader 增量；zcode/copilot/opencode/antigravity 每次整体重建 | 各 Scan 入口 |
| 账户级用量 | cursor（不进同步） | `Core/DataSync.cs:53` |
| 仅本机用量 | pi（Fetch 不联网） | `Core/Core.cs:137` |
| 免 key 导入 | zcode（credentials.json 解密）、kimi（config.toml api_key / OAuth 刷新） | `Core/Platform/LocalCliCredentials.cs` |
| 输出速度 | 各平台耗时口径不同（Codex 响应项、Claude 父条目链、Grok apiDurationMs…），统一准入：输出 ≥50 token、0.2–600s | `Core/Timing.cs:18-28` |
| 已知计划项 | 事件驱动定向扫描：调度器 `Core/ScanScheduler.cs`（Phase 0.1，debounce + 单飞合并）已就位但 AppMonitor 尚未接线；370 天归档等 | `ROADMAP.md` Phase 0/1（2026-10-08：Phase 0.3 侧车排除已落地 `Core/Pacing.cs:51-59`） |
