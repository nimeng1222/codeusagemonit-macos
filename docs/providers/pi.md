# Pi（pi）

解析器：`Core/MoreAgentLogs.cs:28-52`（`MoreAgentLogs.ScanPi` / `PiLine`）。

**11 个平台里唯一「仅本机用量」的**：没有账户额度，也不需要任何凭据
（`Core/Core.cs:137` `LocalOnly`、`Core/Core.cs:633-636`）。

## 数据来源

- **扫描路径**：`$PI_AGENT_DIR`（逗号分隔多根）→ 未设置时
  `~/.pi/agent/sessions/`，递归全部文件（`Core/MoreAgentLogs.cs:29`）。
- **文件格式**：会话 JSONL。只认 `type` 为空或 `"message"`、`message.role == "assistant"`
  且带 `message.usage` 的行（`Core/MoreAgentLogs.cs:35-39`）。
  usage 字段：`input` / `output` / `cacheRead` / `cacheWrite` / `totalTokens` /
  `cost.total`（`Core/MoreAgentLogs.cs:41-45`）。
- **计费**：记录的 `cost.total` 直接采用；否则按 `provider/model` 拼接再按裸模型名查表
  （`Core/MoreAgentLogs.cs:45`、`Core/MoreAgentLogs.cs:49-51`）。
- **去重**：键 `p|<毫秒>|<model>|<四元组+extra>`（`Core/MoreAgentLogs.cs:46-48`）。
  fork 出的会话会**原样重复父消息（同时间戳同数值）**，靠这个键只计一次
  （`Core/MoreAgentLogs.cs:24-27` 注释）。
- **总额兜底**：只有 `totalTokens` 没有 output 时缺口填进 output；已有 output 则填进
  other（`Core/MoreAgentLogs.cs:42`、`Core/MoreAgentLogs.cs:19-22`）。
- **额度**：无。`Fetch` 对 LocalOnly 平台直接返回「仅本机用量 · 本机日志」，
  不发任何网络请求（`Core/Core.cs:632-636`）；CLI `providers` 显示
  「不需要（只统计本机日志）」（`Core/Cli.cs:273`）。

## 凭据

- 不需要。无环境变量、无钥匙串条目、无登录文件。

## 坑点

- Pi 不在默认启用列表里（`Core/Core.cs:129` `DefaultEnabled` 是原六个平台）——
  需在设置里启用，或 CLI 用 `-p pi`（`-p` 不受启用开关限制，`Core/Cli.cs:42`）。
- 与 Copilot / OpenCode 同批「无样本数据编写」的解析器
  （`Core/MoreAgentLogs.cs:9-11`），真机数据核对以 VERIFICATION 记录为准。
- 行过滤要求同时含 `"usage"` 和 `"message"` 两个子串的字节级预筛
  （`Core/MoreAgentLogs.cs:32-34`），改字段名时要同步。

## 验证

```sh
# 本机用量链路（-p 绕过默认未启用）
codeusage cost -p pi --refresh --days 7 --json

# 额度查询：应返回 ready +「仅本机用量」，不联网
codeusage usage -p pi --json    # status=ready, plan="本机日志"

# 凭据列显示「不需要」
codeusage providers --all | grep pi

# 回归
dotnet run --project src/CodeUsageMonit.Cli -- --self-test
# 相关用例：Pi assistant messages: a total-only record becomes output, copies count once
```
