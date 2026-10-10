# Claude（claude）

解析器：`Core/LocalLogs.cs:506-655`（`ClaudeLogs`）。额度：`Core/Core.cs:654-662`。

## 数据来源

- **扫描路径**：`CLAUDE_CONFIG_DIR` 环境变量（逗号分隔多路径，`Core/LocalLogs.cs:512`、
  `Core/LocalLogs.cs:267-271`）；未设置时依次 `~/.claude` 和 `~/.config/claude`
  （`Core/LocalLogs.cs:514`）。其下 `projects/` **递归** 所有 `.jsonl`
  （`Core/LocalLogs.cs:517`、`Core/LocalLogs.cs:216-220`）。
- **文件格式**：Claude Code 会话 JSONL，每行一个事件；只认
  `message.usage`（及 `data.message.message.usage` 嵌套形态）里有
  `input_tokens` / `output_tokens` 的行（`Core/LocalLogs.cs:559-565`）。
  Token 分四类 + `cache_creation` 的 5m/1h 细分（`Core/LocalLogs.cs:597-600`）。
- **去重**（核心坑，`Core/LocalLogs.cs:502-505` 注释 + `Core/LocalLogs.cs:621-654`）：
  - 同一响应被记录多次共享 `message.id + requestId`（无 requestId 时用 session + 毫秒时间戳），
    精确键 `c|<messageId>|r|<requestId>`（`Core/LocalLogs.cs:624`）；
  - 多副本**取 token 最大者**，平局比 cost，再比谁带 speed 标记
    （`Core/LocalLogs.cs:645`）；
  - `/btw` 侧链会以新 requestId 重放父消息：`route|` / `side|` 别名键把副本折叠到首次记录
    （`Core/LocalLogs.cs:628-630`、`Core/LocalLogs.cs:637-638`）；主线副本压过侧链副本。
  - 自测：`Claude log entries are de-duplicated by message id + request id; the larger copy wins`。
- **计费**：日志自带 `costUSD` 时直接采用（`Core/LocalLogs.cs:575`、`Core/LocalLogs.cs:604`）；
  否则查价目表；`speed: "fast"` 行按 fast 倍率、模型显示为 `<model>-fast`
  （`Core/LocalLogs.cs:601`、`Core/LocalLogs.cs:607`）。
- **第三方归因**：Anthropic 总是返回 request-id，缺失即疑似中转站——provider 记为
  `no-request-id`（`Core/LocalLogs.cs:608-609`），第三方页显示「疑似中转站」
  （`Core/ThirdParty.cs:299`）。当前 endpoint 按**请求时刻**归因（切中转站当天内的小时能正确拆分，
  `Core/LocalLogs.cs:610-612`）。
- **advisor 迭代**：同一响应内嵌第二个模型（`usage.iterations`），单独计桶
  （`Core/LocalLogs.cs:586-594`）。
- **额度**：`~/.claude/.credentials.json` 的 `claudeAiOauth.accessToken` →
  `GET api.anthropic.com/api/oauth/usage`（header `anthropic-beta: oauth-2025-04-20`）；
  5 小时 / 每周 / Sonnet·每周 / Opus·每周 各一表（`Core/Core.cs:654-661`、`Core/Core.cs:380-392`）。

## 凭据

- 额度查询用 Claude Code 自己的 OAuth 令牌，**没有本程序可填的 key**：
  读 `ConfigPath("CLAUDE_CONFIG_DIR", ".claude", ".credentials.json")`
  （`Core/Core.cs:630`、`Core/Core.cs:655`）。
- 401 → 「登录令牌已过期，运行一次 Claude Code 即可自动续期」（`Core/Core.cs:658`）。
- 账户邮箱来自 `~/.claude.json` 的 `oauthAccount.emailAddress`（`Core/Core.cs:661`）。
- CLI 里凭据状态：`codeusage providers` 显示「Claude Code 登录 / 未登录」
  （`Core/Cli.cs:268`）。

## 坑点

- 单行上限 **16 MB**（`Core/LocalLogs.cs:508`）——巨型工具输出行整行吞下；
  Codex 只有 1 MB（`Core/LocalLogs.cs:283`），别照抄。
- 行内含 `null` 时做深度校验：null 的 `id`/`model`/token 数等字段视为坏行丢弃；
  `usage.iterations` 内的 null model 例外（`Core/LocalLogs.cs:533-547`、`Core/LocalLogs.cs:557`）。
- `version` 字段不是 semver 的行丢弃（伪 Claude 日志防御，`Core/LocalLogs.cs:567`、
  `Core/LocalLogs.cs:548-553`）。
- `requestId` 为**空串**（存在但空）同样丢弃；`sessionId`、`messageId`、`model` 同理
  （`Core/LocalLogs.cs:568-573`）。
- `<synthetic>` 模型不计价、不显示模型名（`Core/LocalLogs.cs:602-607`）。
- 去重状态（`Recent`/`Open`）跨增量读取保存在 `LogFile` 上，文件重写归零重读
  （`Core/LocalLogs.cs:37-39`、`Core/LocalLogs.cs:175-181`）。

## 验证

```sh
# 本机用量链路：重扫 claude 日志并按天列出（-p 不受启用开关限制）
codeusage cost -p claude --refresh --days 7

# 额度链路：需 Claude Code 已登录
codeusage usage -p claude

# 结构化检查（JSON 字段：tokens / inputTokens / cacheReadTokens / models）
codeusage cost -p claude --refresh --days 3 --json

# 数据落盘检查（索引文件 + 数据目录见 architecture.md）
ls "$HOME/Library/Application Support/codeusagemonit/claude-logs.json"

# 解析 / 去重回归
dotnet run --project src/CodeUsageMonit.Cli -- --self-test
# 相关用例：Claude log entries are de-duplicated by message id + request id; the larger copy wins
#          Output speed (Claude): parent entry → last block, across reads and files; quoted keys in text ignored
#          Third-party usage follows the switch timeline; history before tracking stays unattributed
```
