# OpenCode（opencode）

解析器：`Core/MoreAgentLogs.cs:144-194`（`MoreAgentLogs.ScanOpenCode` / `OpenCodeMessage`）。
额度：`Core/Core.cs:718-721`。

## 数据来源

- **扫描路径**：`$OPENCODE_DATA_DIR`（逗号分隔多根）→ 未设置时 `$XDG_DATA_HOME`
  （须为绝对路径）→ `~/.local/share`；其下 `opencode/`（`Core/MoreAgentLogs.cs:148-152`）。
- **两代存储并存**（`Core/MoreAgentLogs.cs:155-168`）：
  - 新：根目录 `opencode*.db`（SQLite），表 `message` 或 `session_message`
    （列名自适应：`id`/`session_id`/`data`，可选 `time_created`），
    `data` 列存消息 JSON；
  - 旧：`storage/message/**/*.json` 单文件。
  **同一 message id 数据库行赢过 legacy JSON**（键 `oc|<id>|<session>` 去重，
  `Core/MoreAgentLogs.cs:140-143` 注释、`Core/MoreAgentLogs.cs:180-181`）。
- **消息 JSON**：`tokens {input, output, reasoning, cache{read, write}}`、
  `model.id`/`modelID` + `model.providerID`/`providerID`（**两者都非空才收**，
  `Core/MoreAgentLogs.cs:176-179`）、可选 `cost`（USD）。**reasoning 计入 Other**
  不混进 output；`total` 缺口回填（`Core/MoreAgentLogs.cs:184-185`）。
- **计费**：记录的 `cost` > 0 时直接采用（`cost <= 0` 视为未记录），否则按
  `providerID/modelID` 再按裸模型名查价目表（`Core/MoreAgentLogs.cs:187-189`）。
- **时间与速度**：`time.created`（缺列用 `session_message.time_created`）为发生时刻；
  `time.completed − time.created` 给输出速度（`Core/MoreAgentLogs.cs:182-183`、
  `Core/MoreAgentLogs.cs:191-193`）。
- **额度**：`GET opencode.ai/zen/go/v1/usage`（OpenCode Go 订阅）。
  `usage.rolling` 缺失直接报错「接口未返回用量字段」；rolling/weekly/monthly 的百分比
  支持 7 种键名别名，重置时间支持绝对（`resetAt…`）和相对（`resetInSec…`）
  （`Core/Core.cs:509-523`）。

## 凭据

1. 环境变量 `OPENCODE_API_KEY`（`Core/Core.cs:174`）；
2. 设置里保存的 key（钥匙串，`Core/Core.cs:719`）；
3. 无免 key 导入；key 被拒（401）时按「API Key 无效或已过期」提示而非「登录过期」
   （`Core/Core.cs:761-767` `KeyedRequest` 对 API-key 平台的区分）。

## 坑点

- 数据库消息行没有独立时间列时用行内 `tokens` JSON 的 `time.created`；两者都没有则丢弃
  （`Core/MoreAgentLogs.cs:182-183`）。
- `data` 列不含 `"tokens"` 子串的行直接跳过（便宜的前置过滤，
  `Core/MoreAgentLogs.cs:174`）。
- 索引整体重建（非增量）：每次扫描新建单文件 `LogFile`（`Core/MoreAgentLogs.cs:145-147`），
  与 zcode / copilot / antigravity 同款。
- 帮助文案里的开通前提：OpenCode **Go 订阅**（`Core/Core.cs:163`）。

## 验证

```sh
# 本机用量链路（装了 OpenCode 并用过才有数据）
codeusage cost -p opencode --refresh --days 7 --json

# 额度链路（需 OPENCODE_API_KEY 或已保存 key）
[ -n "$OPENCODE_API_KEY" ] && echo "env set" || echo "env unset"
codeusage usage -p opencode

# 凭据来源
codeusage providers | grep opencode

# 回归
dotnet run --project src/CodeUsageMonit.Cli -- --self-test
# 相关用例：OpenCode Go reads percent aliases and relative resets
```

注：OpenCode 的解析代码与 Pi / Copilot 同批（`Core/MoreAgentLogs.cs:9-11` 注释：
编写时开发机上没有样本数据），真机数据回归以 VERIFICATION 记录为准。
