# Copilot（copilot）

解析器：`Core/MoreAgentLogs.cs:58-138`（`MoreAgentLogs.ScanCopilot`，GitHub Copilot CLI）。
额度：`Core/Core.cs:701-707`。

**注意**：解析代码编写时开发机上没有样本数据（`Core/MoreAgentLogs.cs:9-11` 注释），
真机验证优先级最高的一批。

## 数据来源

- **扫描路径**：`$COPILOT_HOME`（未设置时 `~/.copilot`）下两处
  （`Core/MoreAgentLogs.cs:62`）：
  - `session-state/<session>/events.jsonl`：每会话一个目录；
  - `otel/*.jsonl`：OpenTelemetry 导出；环境变量 `COPILOT_OTEL_FILE_EXPORTER_PATH`
    指定的文件会**追加**进列表（`Core/MoreAgentLogs.cs:93-95`）。
- **session.shutdown 口径**：`type == "session.shutdown"` 的 `data.modelMetrics.<model>.usage`
  是**会话级累计值**——按 (session, model) 分组按时间排序，每条 shutdown 只计
  **相对上一次的增长**（`Core/MoreAgentLogs.cs:63-91`）。直接把累计值当用量会翻倍。
- **otel 口径**：`attributes` 含 `gen_ai.usage.*`、`name` 以 `chat` 开头的 span 按请求计；
  **已被更晚 shutdown 汇总的 (session, model) 区间内的 span 跳过**，避免双计
  （`Core/MoreAgentLogs.cs:96-117`，判重 `Core/MoreAgentLogs.cs:104-107`）。
- **去重**：otel 键 `o|<spanId>`（缺 spanId 用毫秒+模型兜底，
  `Core/MoreAgentLogs.cs:108-110`）。
- **时间解析**：otel 时间戳兼容 [秒, 纳秒] 数组、epoch 秒/毫秒/微秒/纳秒数值、ISO 字符串
  五档量级判断（`Core/MoreAgentLogs.cs:121-132`）。
- **额度**：`GET api.github.com/copilot_internal/user`（带
  `Editor-Version` / `Editor-Plugin-Version` / `X-Github-Api-Version` 头，
  `Core/Core.cs:701-706`）。`quota_snapshots` 的 `percent_remaining` 转成已用百分比；
  **unlimited 池没有表**，全 unlimited 时拼进套餐名（`Core/Core.cs:460-478`）。

## 凭据

1. **应用内 GitHub 设备码登录**（桌面版设置 → 使用 GitHub 登录）：
   OAuth device flow，用 VS Code Copilot 的公开 client id、scope `read:user`
   （`Core/Core.cs:780-800`）；轮询处理 `authorization_pending` / `slow_down` /
   `expired_token` / `access_denied`；成功后 token 存钥匙串（`Core/Cli.cs:272` 的
   「GitHub 授权（本程序保存）」即 `HasSavedKey("copilot")`）。
2. **回退（只读）**：官方 Copilot 客户端已保存的授权——`~/LocalApplicationData`（macOS 为
   `~/.local/share`... 实际取 `SpecialFolder.LocalApplicationData`）与
   `~/.config/github-copilot/` 下的 `apps.json` / `hosts.json`，只挑 `github.com` 开头的
   键的 `oauth_token`，**只读不写**（`Core/Core.cs:770-779`）。
3. 账户名另调 `GET api.github.com/user`（失败不影响主读数，`Core/Core.cs:705`）。

## 坑点

- 索引整体重建（非增量扫描），单文件 `LogFile`（`Core/MoreAgentLogs.cs:59-61`）。
- shutdown 差分的「上次值」跨扫描不持久——每次扫描都从头重放该会话目录的
  events.jsonl（文件小，可接受；`Core/MoreAgentLogs.cs:82-91`）。
- entitlement 0 + remaining 0 + percent 0 是**占位符**不是耗尽的配额，不显示
  （`Core/Core.cs:472-473`）。
- events.jsonl / otel 文件整读进内存再逐行过滤（`SafeLines`，读失败静默空，
  `Core/MoreAgentLogs.cs:134-138`）——与 LogReader 的增量流式不同，别混用。
- 401 → 「GitHub 授权已失效，请重新使用 GitHub 登录」（`Core/Core.cs:765`）。

## 验证

```sh
# 本机用量链路（装了 GitHub Copilot CLI 并用过才有 session-state/otel）
codeusage cost -p copilot --refresh --days 7 --json

# 额度链路：应用内设备码登录过，或本机有官方 Copilot 客户端授权
codeusage usage -p copilot

# 凭据来源（三态：本程序保存 / Copilot 客户端的授权 / 未登录）
codeusage providers | grep copilot

# 回归
dotnet run --project src/CodeUsageMonit.Cli -- --self-test
# 相关用例：Copilot maps percent_remaining, skips placeholders and unlimited pools
```
