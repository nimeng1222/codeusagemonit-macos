# ZCode（zcode）

解析器：`Core/AgentLogs.cs:127-179`（`AgentLogs.ScanZcode`）。额度：`Core/Core.cs:722-729`。
凭据导入：`Core/Platform/LocalCliCredentials.cs`（ZCode 半边，57-106 行）。

## 数据来源

- **扫描路径**：`$ZCODE_HOME`（未设置时 `~/.zcode`）下 `cli/db/db.sqlite`
  （`Core/AgentLogs.cs:132-134`）。
- **读取方式**：SQLite **只读**连接（`SqliteOpenMode.ReadOnly`、`Pooling=false`、
  `busy_timeout=2000ms`），并发写库只读不锁（`Core/AgentLogs.cs:20-28`）。
- **查询**：`model_usage LEFT JOIN session`，**只计 `status='completed'`**，
  `started_at` ≥ 31 天窗口（毫秒）；可选列（`cache_creation_input_tokens`、
  `cache_read_input_tokens`、`computed_total_tokens`、`provider_id`、`completed_at`）
  缺了自适应降级（`Core/AgentLogs.cs:138-144`）。
- **每次全量重读**：与其他平台的增量扫描不同，zcode 每次扫描都新建索引整体重读
  （`Core/AgentLogs.cs:128`）——库是结构化单文件，没有「追加」概念。
- **z.ai 计费特例**：provider 是 z.ai 系（`zai`/`z.ai`/`zai-coding-plan`/
  `builtin:zai-coding-plan`/`builtin:bigmodel-coding-plan`，`Core/AgentLogs.cs:175-179`）
  或 provider 未知但模型名 `glm-` 前缀时，按 z.ai 价目；**z.ai 不对缓存写入单独计费，
  cache write 并入普通 input 计价**（`Core/AgentLogs.cs:154-163`）。
- **去重**：SQL 层天然无重复（一次一行），无跨文件 Seen 键。
- **额度**：`GET {api.z.ai | open.bigmodel.cn}/api/monitor/usage/quota/limit`
  （`ZaiRegion` 决定 host，`Core/Core.cs:83`、`Core/Core.cs:727`）。limits 的 `unit`:
  1=天 / 3=小时 / 5=分钟 / 6=周；**`TIME_LIMIT` 是每月 MCP 工具调用额度，不是时间窗**
  （窗口秒数记 0，`Core/Core.cs:524-548`）。

## 凭据

按优先级（`Core/Core.cs:709-726` 同款链路，zcode 分支 `Core/Core.cs:722-726`）：

1. **环境变量**（china 区域 5 个，global 区域只认 `Z_AI_API_KEY`，`Core/Core.cs:175`）：
   `Z_AI_API_KEY`、`BIGMODEL_API_KEY`、`ZHIPU_API_KEY`、`ZHIPUAI_API_KEY`、`GLM_API_KEY`。
2. **设置里保存的 key**：钥匙串 service `codeusagemonit`（`Core/Core.cs:347-353`）。
3. **本机 CLI 凭据静默导入**（`Core/Platform/LocalCliCredentials.cs:41-48`）：
   - 读 `~/.zcode/v2/credentials.json`，取键名以 `:api-key` 结尾的条目，
     **按字典序取最后一个**（与客户端 active provider 的简化对齐，
     `Core/Platform/LocalCliCredentials.cs:60-72`）；
   - 值形如 `enc:v1:<iv>.<tag>.<cipher>`：AES-256-GCM，key = SHA256(secret)；
     secret 优先环境变量 `ZCODE_CREDENTIAL_SECRET`（trim），否则客户端回退串
     `zcode-credential-fallback:<platform>:<homedir>:<username>`
     （`Core/Platform/LocalCliCredentials.cs:75-95`）。非 `enc:v1:` 前缀按明文原样采用
     （`Core/Platform/LocalCliCredentials.cs:76`）；
   - 导入成功即写入钥匙串（钥匙串不可写时本次仍使用导入值，
     `Core/Platform/LocalCliCredentials.cs:50-55`）。
- 任何一步失败都返回空串，走「请填写 API Key」提示，绝不抛出、不落日志
  （文件头注释 `Core/Platform/LocalCliCredentials.cs:1-22`）。

## 坑点

- **db.sqlite 是 WAL 库**：`-wal` / `-shm` 侧车会被客户端（甚至只读连接）重写。
  `CountsAsWork` 已把 `*-shm` / `*-wal` 排除在工作量之外（Phase 0.3，`Core/Pacing.cs:51-56`；
  代码注释引用的实测记录 `verification/phase0/shm-measurement.txt` 截至 2026-10-08 尚未入库，未核实），
  本程序自己的 `data/` 目录
  同样被排除（`Core/Pacing.cs:57-59`、`Core/Pacing.cs:66-74`）。watcher 目前只标记
  「在用」不触发扫描（`Desktop/AppMonitor.cs:469-471`）；ROADMAP Phase 0.1 的事件驱动
  调度器（`Core/ScanScheduler.cs`）截至本文写作尚未在 AppMonitor 接线。
- 每次全量重读意味着库很大时扫描耗时线性增长（`Core/AgentLogs.cs:128`）。
- `read`/`write` 都向 input 钳制：`read ≤ input`、`write ≤ input − read`
  （`Core/AgentLogs.cs:150`），脏数据不会算出负数。
- `computed_total_tokens` 缺列时用 `input + output` 兜底，缺口回填进 output / other
  （`Core/AgentLogs.cs:142-143`、`Core/AgentLogs.cs:117-120`）。
- 智谱接口 `success:false` 会显式报错而不是静默空额度（`Core/Core.cs:528`）。

## 验证

```sh
# 本机用量链路（装了 ZCode CLI 并用过才有 db.sqlite）
codeusage cost -p zcode --refresh --days 7 --json

# 额度链路：任一凭据来源可用即可
codeusage usage -p zcode

# 确认凭据来源（输出 env 变量名 / 已保存的密钥（钥匙串） / 未设置密钥）
codeusage providers | grep zcode

# 导入链路自测（合成凭据，不触真实文件）
dotnet run --project src/CodeUsageMonit.Cli -- --self-test
# 相关用例：ZCode local credentials.json: AES-256-GCM import of the last :api-key entry
#          GLM Coding Plan (ZCode) maps units, counts and the MCP allowance
#          Local CLI credential import stays silent when nothing is importable
```
