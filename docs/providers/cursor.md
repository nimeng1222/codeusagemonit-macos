# Cursor（cursor）

`CursorUsage.cs` 全文（123 行）。额度：`Core/Core.cs:663-673`。**11 个平台里唯一没有本地日志的**：
用量直接来自账户 dashboard（`UsageScan.cs:15-16` 注释）。

## 数据来源

- **用量接口**：`POST cursor.com/api/dashboard/get-filtered-usage-events`，JSON body 按页拉取
  （`CursorUsage.cs:62-66`）：pageSize 100、最多 200 页、页间 sleep 150ms
  （`CursorUsage.cs:15`、`CursorUsage.cs:28-36`）。
- **增量游标**：完整扫过的窗口推进到 now；**页数封顶时游标停在最后读到的事件**
  而不是跳到 now，避免永久跳过未取历史（`CursorUsage.cs:41-46`、`CursorUsage.cs:55-61`，
  自测 `Cursor parks the completion cursor at the last read event when the page cap stops the scan`）。
  每次从上次游标**回卷 2 小时**，晚到事件和正在跑的小时靠 Seen 键吸收重叠
  （`CursorUsage.cs:23-24`）。
- **事件 → 请求**：`tokenUsage` 的 inputTokens/outputTokens/cacheReadTokens/cacheWriteTokens，
  一事件一请求记入所在小时（`CursorUsage.cs:74-95`）。去重键
  `cu|<毫秒>|<model>|<四元组>`（`CursorUsage.cs:82`）。
- **计费**：dashboard 自带的 `totalCents`（美分）÷100 优先，缺了才查价目表
  （`CursorUsage.cs:85-93`）。
- **额度**：`POST cursor.com/api/usage-summary`（套餐总量 / Auto / API·手动模型，
  `Core/Core.cs:663-672`、`Core/Core.cs:393-400`）；Grok Bot 是**独立周额度**，来自
  `get-sand-usage-status`，且它的失败不拖垮整个 provider（`Core/Core.cs:403-416`、
  `Core/Core.cs:669-671`）。
- **跨设备口径**：账户级用量，所有设备看到同一份——**不参与** SQL 导出 / 导入
  （`Core/DataSync.cs:52-53`、`Core/DataSync.cs:93-94`、`Core/DataSync.cs:171`）。

## 凭据

- 编辑器会话 Cookie：`~/Library/Application Support/Cursor/User/globalStorage/state.vscdb`
  （SQLite，`Platform/PlatformPaths.cs:12-15`；Windows 的 `%APPDATA%` 在 macOS 的对应路径）
  里的 `cursorAuth/accessToken`；取 JWT `sub` 的最后一段拼成
  `sub::token` 作为 `WorkosCursorSessionToken` Cookie（`Core/Core.cs:748-752`、
  `Core/Core.cs:616`）。
- 请求带 `User-Agent: Mozilla/5.0` + `Origin`/`Referer` cursor.com——按浏览器请求伪装
  （`Core/Core.cs:667`、`Core/Core.cs:754-758`）。
- 账户邮箱：同一 state.vscdb 的 `cursorAuth/cachedEmail`（`Core/Core.cs:672`）。
- 无 key 可填、无环境变量；未登录 → setup 提示（`Core/Core.cs:755`）。

## 坑点

- **事件无耗时字段 → Cursor 永远没有输出速度**（文件头注释 `CursorUsage.cs:7-13`）。
- `kind …INCLUDED_IN_PRO` 的请求型调用没有 `tokenUsage`：仍计一次请求，
  模型显示为 `auto`（`model` 空 / `"default"` 都映射为 `auto`，`CursorUsage.cs:80-81`）。
- 整个 scan 失败（未登录 / 离线 / 接口变了）时**保留上次索引**，不清零
  （`CursorUsage.cs:37-40`）。
- state.vscdb 是 WAL 库：走只读 + busy_timeout 2000ms 的 Sqlite 适配层
  （`Core/AgentLogs.cs:16-28`）；活动监视把 `state.vscdb*` 计为「在用」
  （`Core/Pacing.cs:60`），其 `-shm`/`-wal` 侧车已被排除（`Core/Pacing.cs:51-56`）；
  watcher 只影响刷新节奏，不直接触发扫描。
- dashboard 响应的事件数组键名有历史变化：`usageEventsDisplay` → `usageEvents` →
  `events` 依次探测（`CursorUsage.cs:67-72`）。
- 诊断命令 `codeusage cursor-fields` 会拉全窗口分页统计字段名与事件种类，
  **不打印任何值**（`CursorUsage.cs:98-121`、`Core/Cli.cs:54-55`）。

## 验证

```sh
# 额度 + 账户用量链路（需 Cursor 编辑器已登录）
codeusage usage -p cursor

# 接口字段诊断（无值输出；验证 cookie 与分页链路是否通）
codeusage cursor-fields

# 本机用量面板（账户用量折进 cursor-logs.json 后与其他平台同构）
codeusage cost -p cursor --refresh --days 7 --json

# 回归
dotnet run --project src/CodeUsageMonit.Cli -- --self-test
# 相关用例：Cursor dashboard events fold into hourly buckets per model, once each
#          Cursor parks the completion cursor at the last read event when the page cap stops the scan
#          Cursor total, Auto and API retain separate metrics
#          Cursor Grok Bot weekly quota is appended after the plan meters
```
