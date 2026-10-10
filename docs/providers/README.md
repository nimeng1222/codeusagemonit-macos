# docs/providers — 平台数据来源与凭据口径索引

每个平台一份文档：数据来源（扫哪些路径 / 调哪些接口）、凭据（key 从哪来、存哪）、
坑点（从代码里挖出来的真实陷阱）、验证（真实存在的命令）。

本目录基于 2026-10-08 的代码写成；行号会随改动漂移，以引用处代码为准。

## 路径引用约定

- `Core/…` = `src/CodeUsageMonit.Core/`
- `Desktop/…` = `src/CodeUsageMonit.Desktop/`
- `Platform/…` = `src/CodeUsageMonit.Core/Platform/`
- 引用格式 `文件:行号`。

## 平台一览

11 个内置平台注册在 `Core/UsageScan.cs:12-26`（`UsageScanner.Agents`），每平台一个小时桶索引文件；
自定义平台（`custom-…` id）没有本地索引，见 [custom.md](custom.md)。

| 平台 | id | 索引文件 | 本机用量来源 | 账户额度来源 | 凭据 | 文档 |
|---|---|---|---|---|---|---|
| Codex | `codex` | `codex-logs.json` | 本地日志（`~/.codex` rollout JSONL） | ChatGPT backend usage API | Codex 应用登录（`auth.json`） | [codex.md](codex.md) |
| Claude | `claude` | `claude-logs.json` | 本地日志（`~/.claude/projects` JSONL） | Anthropic OAuth usage API | Claude Code 登录（`.credentials.json`） | [claude.md](claude.md) |
| Cursor | `cursor` | `cursor-logs.json` | **无本地日志**——账户用量 API | Cursor dashboard API | 编辑器会话（`state.vscdb`） | [cursor.md](cursor.md) |
| DeepSeek | `deepseek` | `deepseek-logs.json` | 本地日志（`~/.dsh` zstd JSONL） | API 余额接口（可选，填 key 才查） | `DEEPSEEK_API_KEY` / 钥匙串 | [deepseek.md](deepseek.md) |
| Antigravity | `antigravity` | `antigravity-logs.json` | 本地数据库（protobuf `.db`） | 本机 language-server → Google API 回退 | 应用登录 / 钥匙串 `gemini:antigravity` | [antigravity.md](antigravity.md) |
| ZCode | `zcode` | `zcode-logs.json` | 本地 SQLite（`~/.zcode` db.sqlite） | GLM 编码套餐额度 API | 环境变量 / 钥匙串 / 本机 CLI 凭据导入 | [zcode.md](zcode.md) |
| Kimi | `kimi` | `kimi-logs.json` | 本地日志（`wire.jsonl`） | Kimi Code usages API | 环境变量 / 钥匙串 / CLI api_key / OAuth | [kimi.md](kimi.md) |
| Grok | `grok` | `grok-logs.json` | 本地日志（`updates.jsonl`） | Grok Build billing API | Grok CLI 登录（`auth.json`） | [grok.md](grok.md) |
| Copilot | `copilot` | `copilot-logs.json` | 本地日志（events.jsonl / otel） | GitHub copilot_internal API | GitHub 设备码授权 | [copilot.md](copilot.md) |
| OpenCode | `opencode` | `opencode-logs.json` | 本地 SQLite + legacy JSON | OpenCode Go usage API | `OPENCODE_API_KEY` / 钥匙串 | [opencode.md](opencode.md) |
| Pi | `pi` | `pi-logs.json` | 本地日志（`~/.pi` JSONL） | **无**（只有本机用量） | 不需要 | [pi.md](pi.md) |
| 自定义 | `custom-…` | 无 | 无 | 用户自定义 GET JSON | 钥匙串（按定义可选） | [custom.md](custom.md) |

数据来源方式三种口径：

- **本地日志**：解析客户端自己写的会话 / 事件文件（JSONL、zstd JSONL、SQLite、protobuf），
  用量是本机观察值，费用按官方价目表折算（API-equivalent，不是订阅账单）。
- **API**：调用服务商接口（Cursor 的 dashboard、各家额度 / 余额接口）。
- **混合**：本机用量走本地日志，额度走 API；ZCode / Kimi 另有本机 CLI 凭据导入链路。

## 通用机制（所有本地日志平台共享）

- **增量扫描**：文件 Length + LastWriteTime 都没变就跳过；文件变短或开头 256 KB
  指纹（SHA-256）不匹配则整体重读（`Core/LocalLogs.cs:156`、`Core/LocalLogs.cs:160`、
  `Core/LocalLogs.cs:196-204`）。以 `FileShare.ReadWrite | FileShare.Delete` 只读打开，
  不锁客户端正在写的文件（`Core/LocalLogs.cs:162`）。
- **小时桶索引**：桶键 `yyyyMMddHH|provider|model[|endpoint]`（UTC 小时，
  `Core/LocalLogs.cs:113`）；索引版本 `LogIndex.CurrentVersion = 5`
  （`Core/LocalLogs.cs:50`），版本或价目表变化即重建。
- **去重**：`index.Seen` 字典，键带小时前缀便于过期、带属主文件便于截断文件归还
  （`Core/LocalLogs.cs:55-56`、`Core/LocalLogs.cs:117`）。各平台键格式见各自文档。
- **保留窗口**：扫描窗口 `HorizonDays = 31`，索引裁剪 `RetainDays = 33`
  （`Core/LocalLogs.cs:131`）；**已删除的源文件保留已计入的小时**，客户端清理旧会话不缩水
  （`Core/LocalLogs.cs:207-215`）；日历史 Merge 对过去日只增不减、今天永远替换
  （`Core/Core.cs:860-868`）。
- **凭据顺序**：环境变量优先于已保存密钥（`Core/Core.cs:334-337`，变量名见
  `Core/Core.cs:170-178`）；已保存密钥存 macOS 钥匙串（service `codeusagemonit`，
  `Platform/Keychain.cs:14`），`data/<id>.key` 只存 `keychain:<id>` 引用，备份不含密钥
  （`Core/Core.cs:331-333`、`Core/Core.cs:347-353`）。
- **在用感知**：每平台一个 FileSystemWatcher + 30 秒 LastWriteTime 轮询兜底，命中只记
  「在用」标记（不直接触发扫描）；`CountsAsWork` 排除 `*-shm`/`*-wal` 侧车与本程序自己的
  `data/` 目录（Phase 0.3），在用平台的刷新间隔压到 `InUseMinutes`（默认 2 分钟）
  （`Desktop/AppMonitor.cs:462-481`、`Core/Pacing.cs:49-64`）；在用平台的刷新间隔压到
  `InUseMinutes`（默认 2 分钟，`Core/Pacing.cs:18-22`）。

## 验证总则

CLI 是 `dist/osx-arm64/cli/codeusage`（与桌面版共用数据目录，见 README）。
所有子命令定义在 `Core/Cli.cs:44-59`：

```sh
codeusage                          # status：读桌面版缓存，不联网
codeusage usage -p <id>            # 实时查询该平台额度（联网）
codeusage cost -p <id> --refresh --days 7   # 重新扫描本机日志并按天列出
codeusage providers                # 平台列表 + 凭据来源（env / 钥匙串 / 未登录）
codeusage --self-test              # 全量自测（94 项，2026-10-08 基线）
```

`-p` 指定的平台不受「是否启用」限制（`Core/Cli.cs:42`、`Core/Cli.cs:172`）。
自测逐项结果在 `src/CodeUsageMonit.Cli/bin/Debug/net8.0/verification/tests.json`
（`dotnet run --project src/CodeUsageMonit.Cli -- --self-test`），各平台相关用例名
在各自文档的验证节给出。
