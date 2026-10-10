# Grok（grok）

解析器：`Core/AgentLogs.cs:184-231`（`AgentLogs.ScanGrok` / `GrokLine`）。
额度：`Core/Core.cs:681-685`。

## 数据来源

- **扫描路径**：`$GROK_HOME`（未设置时 `~/.grok`）下 `sessions/` 递归，
  **只认文件名 `updates.jsonl`**（`Core/AgentLogs.cs:185-187`）。
- **文件格式**：Grok Build CLI 的事件流。只处理 `sessionUpdate == "turn_completed"`
  的事件；`usage.modelUsage` 按**模型分列**（一回合可能调多个模型，逐模型记桶；
  无 modelUsage 时整包记在默认模型下，`Core/AgentLogs.cs:197-207`）。
- **计费口径**：Grok 自记成本 `costUsdTicks`（**定点数 1e-10 USD**，ticks/1e10），
  有记录值就用它；缺了才回退价目表（`Core/AgentLogs.cs:181-183` 注释、
  `Core/AgentLogs.cs:216-219`）。
- **时间戳**：`_meta.agentTimestampMs` 优先，否则 `timestamp`（秒）；两者都缺用 epoch 0
  （`Core/AgentLogs.cs:201-202`）。会话 id 取 `summary.json` 的 `info.id`，
  默认模型取 `current_model_id`（`Core/AgentLogs.cs:190`）。
- **去重**：有 `eventId` 时键 `g|<eventId>|<model>`；无 eventId 用
  全部 token 数拼键兜底（`Core/AgentLogs.cs:213-215`）。
- **速度**：Grok 记录的是一回合内模型调用的 API 累计时间
  （`apiDurationMs` / `modelCalls`），outputTokens 含 reasoning
  （`Core/AgentLogs.cs:220-222`）。
- **额度**：`GET cli-chat-proxy.grok.com/v1/billing?format=credits`。
  `creditUsagePercent` 是**一个共享额度**；`productUsage`（Build / Chat）只是它内部的
  消耗拆分，**不是独立配额**，各产品占比之和对不齐（>1 个百分点）时整组丢弃
  （`Core/Core.cs:422-439`，自测
  `Grok product shares split the one allowance and are not quotas`）。

## 凭据

- Grok CLI 登录态：`$GROK_HOME`（默认 `~/.grok`）的 `auth.json`。多条目里认
  key 为 `https://accounts.x.ai/sign-in` 或 `https://auth.x.ai::…` 的项，
  **取 `expires_at` 最新且 `key` 非空的一条**（`Core/Core.cs:682`）。
- 请求头伪装 CLI：`X-XAI-Token-Auth: xai-grok-cli`、`x-grok-client-mode: cli`、
  `x-grok-client-version`、`x-userid`（`Core/Core.cs:683`）。
- 无 key 可填、无环境变量；未登录 → setup 提示「执行 grok login」
  （`Core/Core.cs:160` Help 文案）。

## 坑点

- 模型名定价候选：剥 `[grok] ` 前缀、剥 `-build` 后缀，依次试裸名 / `xai/` / `x-ai/`
  前缀（`Core/AgentLogs.cs:226-231`）——模型名带装饰时不能直接查表。
- 旧版本把 Build 占比的补数（「Build 剩余 45%」）当配额缓存过：读取时清除
  `Build 占比` 标签的旧缓存（`Core/Core.cs:441-448`）。
- 刷新失败保留上次读数（含消耗拆分）并标 Stale，不清空
  （`Core/Core.cs:450-457`）。
- 更新检查 / 缓存序列化用旧格式读入时经 `Parsers.Normalize` 统一
  （`Core/Cli.cs:96`、`Desktop/AppMonitor.cs:88`）。

## 验证

```sh
# 本机用量链路（装了 Grok Build CLI 并用过才有 sessions）
codeusage cost -p grok --refresh --days 7 --json

# 额度链路：需 grok login 过
codeusage usage -p grok

# 凭据状态
codeusage providers | grep grok        # Grok CLI 登录 / 未登录 Grok CLI

# 回归
dotnet run --project src/CodeUsageMonit.Cli -- --self-test
# 相关用例：Grok turns: per-model usage, cached input split out, recorded cost wins, event ids dedupe
#          Output speed (Grok, history): recorded API time per turn; timing reaches the daily rows
#          Grok product shares split the one allowance and are not quotas
#          Legacy Grok cache removes the fake Build quota and keeps shared usage
```
