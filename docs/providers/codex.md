# Codex（codex）

解析器：`Core/LocalLogs.cs:281-500`（`CodexLogs`）。额度：`Core/Core.cs:640-653`。

## 数据来源

- **扫描路径**：`$CODEX_HOME`（未设置时 `~/.codex`，由 `auth.json` 所在目录推导，
  `Core/Core.cs:631`）下的 `sessions/` 与 `archived_sessions/` 两个目录，递归 `.jsonl`
  （`Core/LocalLogs.cs:290-293`）。文件状态按**文件名**索引——归档只是移动不改名，
  不算新文件（`Core/LocalLogs.cs:305-306`）。
- **文件格式**：Codex rollout 日志（JSONL）。用量来自 `event_msg` / `token_count`
  事件的 `last_token_usage` + `total_token_usage`（`Core/LocalLogs.cs:274-280`）；
  另认 headless `codex exec --json` 的 `usage` / `data.usage` / `result.usage` /
  `response.usage` 记录（`Core/LocalLogs.cs:442-449`）。
- **总量差分**：token_count 同时带累计值和单次值；累计值与上次相同 → 重复事件丢弃；
  只有累计时用 `本次 = 累计 − 上次`（`Core/LocalLogs.cs:433-436`）。
- **fork 重放跳过**（`Core/LocalLogs.cs:341-352`、`Core/LocalLogs.cs:466-483`）：
  - fork 的子会话头部重放父历史。先按 `match:` 前缀逐条对上父会话 fork 时刻前的用量；
  - 对不上时探测「文件头两条连续（≤1s）用量事件」——Codex 会把重放历史改写到 fork 时刻
    一次突发写出，命中则切 `burst:` 模式按时间窗跳过（`Core/LocalLogs.cs:366-384`）。
- **跨文件去重**：同一事件被复制进多个文件（resume / fork 链）只计一次，
  键 `x|<毫秒>|<model>|<五元组>`（`Core/LocalLogs.cs:486-488`）。
- **服务档位**：`thread_settings_applied` 的 `service_tier` = fast/priority 记为 fast，
  计费乘模型 fast 倍率（`Core/LocalLogs.cs:423-430`、`Core/LocalLogs.cs:490`）；
  `config.toml` 里 `service_tier = "fast"` 时默认 fast（`Core/LocalLogs.cs:308-317`）。
- **官方 / 中转区分**：`session_meta.model_provider` 记入 provider（official =
  `"openai"`，`Core/LocalLogs.cs:282`、`Core/LocalLogs.cs:418`、`Core/LocalLogs.cs:496`）；
  非官方端点按请求时刻归因到 `endpoints.json` 时间线（`Core/LocalLogs.cs:498`，
  时间线由 `Desktop/AppMonitor.cs:285-308` 监听 `config.toml` / `auth.json` 变化记录，
  CC Switch 的中转名只读自 `~/.cc-switch/cc-switch.db`，`Core/ThirdParty.cs:108-110`）。
- **模型回退**：无模型名 → `gpt-5` 并标 Fallback；`codex-auto-review` 按日期映射到
  当时的 auto-review 模型（回退表在 `Core/LocalLogs.cs:288`，逻辑
  `Core/LocalLogs.cs:455-458`）。
- **额度**：`auth.json` 的 `tokens.access_token` →
  `GET chatgpt.com/backend-api/wham/usage`（UA `codex-cli`，带 `ChatGPT-Account-Id`）；
  primary / secondary / additional_rate_limits 三档（`Core/Core.cs:640-652`、
  `Core/Core.cs:371-379`）。另有重置额度次数接口 `wham/rate-limit-reset-credits`
  （`Core/Core.cs:648-651`）。

## 凭据

- 与 Claude 同理，用 Codex 应用自己的登录态（`auth.json`），无本程序可填 key。
- 401 → 「运行一次 Codex 即可自动续期」（`Core/Core.cs:645`）。
- 账户邮箱从 `id_token` JWT 解出（`Core/Core.cs:646`；JWT 解析失败即空，
  `Core/Core.cs:204`）。

## 坑点

- 单行上限 **1 MB**（`Core/LocalLogs.cs:283`），比 Claude 的 16 MB 小得多。
- 重放的两段式状态（`match:` → `burst:`）依赖 `session_meta` 里的
  `forked_from_id` / `parent_thread_id`（`Core/LocalLogs.cs:324`）；改父会话文件会导致
  `ParentPrefix` 重算（`Core/LocalLogs.cs:297-304`）。
- `cached` / `cache_write` 都按「不超过 input」钳制，差分后同样钳制（负数归零，
  `Core/LocalLogs.cs:400-404`）。
- 订阅窗口只认官方 provider：第三方端点的请求不冒充官方额度消耗（自测
  `Codex buckets carry the session's provider and model; subscription windows skip relays`）。
- `reasoning_output_tokens` 是 output 的一部分，速度统计的分母含它
  （`Core/LocalLogs.cs:494-495`）。

## 验证

```sh
# 本机用量链路
codeusage cost -p codex --refresh --days 7

# 额度链路：需 Codex 应用 / CLI 已登录
codeusage usage -p codex

# 第三方（中转站）用量看板依赖 codex + claude 两个索引
codeusage thirdparty

# 解析 / 重放 / 归因回归
dotnet run --project src/CodeUsageMonit.Cli -- --self-test
# 相关用例：Codex log reader counts token_count once, skips repeats, keeps an unfinished tail
#          Output speed (Codex): last tool output / prompt → last model item, across reads; short replies skipped
#          Codex endpoint is read from model_provider, profiles and provider tables
#          Switching relay inside an hour attributes each request to the endpoint in effect then
```
