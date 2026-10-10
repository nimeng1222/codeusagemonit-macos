# Kimi（kimi）

解析器：`Core/AgentLogs.cs:234-290`（`AgentLogs.ScanKimi` / `KimiLine`）。
额度：`Core/Core.cs:708-717`。凭据导入：`Core/Platform/LocalCliCredentials.cs`（Kimi 半边，108-276 行）。

## 数据来源

- **扫描路径**：`$KIMI_DATA_DIR`（逗号分隔多根，`Core/LocalLogs.cs:267-271`），
  未设置时 `~/.kimi` 和 `~/.kimi-code` 两根；其下 `sessions/` 递归，**只认文件名
  `wire.jsonl` 且目录深度为 3 或 5**（普通会话 / agents 子会话两种布局，
  `Core/AgentLogs.cs:236-243`）。
- **文件格式**：两种记录形态并存（`Core/AgentLogs.cs:263-280`）：
  - 新：`type == "usage.record"`，只认 `usageScope == "turn"`；字段 camelCase
    （`inputOther` / `output` / `inputCacheCreation` / `inputCacheRead`），模型带
    `kimi-code/` 前缀时剥掉（`Core/AgentLogs.cs:266-271`）；
  - 旧：`message.type == "StatusUpdate"`，`payload.token_usage` snake_case +
    `total` 兜底回填；模型取会话 `config.json` 的 `model`（缺省
    `kimi-for-coding`，`Core/AgentLogs.cs:250-257`、`Core/AgentLogs.cs:274-279`）。
- **模型定价**：`kimi-for-coding` 按时间阈值（epoch 毫秒常量 `1776698890072`，
  `Core/AgentLogs.cs:286`；业务含义未核实，按常量换算约 2026-04）映射
  `moonshot/kimi-k2.5` 或 `moonshot/kimi-k2.6`；再依次试 `moonshot/`、`kimi/`、裸名
  （`Core/AgentLogs.cs:285-288`）。
- **去重**：键 `k|<session>:<messageId>:<毫秒>:<model>:<tokens…>`（`Core/AgentLogs.cs:282-284`）。
- **额度**：`GET {api.kimi.com | api.kimi.ai}/coding/v1/usages`
  （`KimiRegion` china/international 决定 host，`Core/Core.cs:83`、`Core/Core.cs:715`）。
  解析：`usages` 里 **0–1 比例池（`used_ratio`）优先**（5 小时 / 每周 / 每月总量），
  没有比例时回退 legacy 计数响应与速率限制窗口；会员等级映射 Adagio→Allegro
  （`Core/Core.cs:479-506`，自测
  `Kimi prefers ratio pools and falls back to counts and rate-limit windows`）。

## 凭据

按优先级（`Core/Core.cs:709-714`）：

1. 环境变量 `KIMI_CODE_API_KEY`（`Core/Core.cs:173`）；
2. 设置里保存的 key（钥匙串）；
3. **本机 CLI 导入链**（`Core/Platform/LocalCliCredentials.cs:156-166`）：
   - `~/.kimi-code/config.toml` 里 **只认 `[providers."managed:kimi-code"]` 表内**的
     内联 `api_key`（表内后一行覆盖前一行；键名必须与 `api_key` **精确相等**，
     `api_key_v2` 不会误读，`Core/Platform/LocalCliCredentials.cs:110-132`）。
     命中即经 `SetProviderKey` 持久化进钥匙串——**这是唯一会被持久化的导入路径**；
   - 否则 `~/.kimi-code/credentials/kimi-code.json` 的 OAuth `access_token`：
     **短寿命凭据，绝不写进钥匙串**，每次 Fetch 重新解析；
     `expires_at <= now + 30s` 时用 `refresh_token` 刷新——POST 固定端点
     `auth.kimi.com/api/oauth/token`（源码常量，凭据文件里的值**不能**覆盖它），
     form 为 `client_id`（客户端内置公开常量）/ `grant_type=refresh_token` /
     `refresh_token`（RFC 3986 百分号编码，不用 `WebUtility.UrlEncode`——它把空格编成
     `+`，`Core/Platform/LocalCliCredentials.cs:135-140`、`212-225`）。

## 坑点

- **refresh_token 会轮转，刷新成功必须原子回写凭据文件**，否则 Kimi Code 本体的登录态
  失效（`Core/Platform/LocalCliCredentials.cs:14-19`、`230-251`）。回写细节：
  保留未知键、**0600 权限在创建时就位**、临时文件 + `File.Move` 原子覆盖；
  回写失败不阻断本次使用（新 token 已在内存）。
- `expires_in` 异常巨大（如 1e300）时跳过 `expires_at` 回写防溢出——只损失主动刷新时机
  （`Core/Platform/LocalCliCredentials.cs:226-239`）。
- 刷新只认 **200 且 access_token 非空**；400/401 视为登录态失效，其余状态与网络错误
  一律静默回空串（走「请填写 API Key」提示），不抛出、不落日志
  （`Core/Platform/LocalCliCredentials.cs:196-210`）。
- 刷新 HTTP 客户端：同步 `Send`（不经异步延续，UI 线程至多阻塞 10s，无 sync-over-async
  死锁）、不跟随重定向、无 Cookie（`Core/Platform/LocalCliCredentials.cs:255-276`）。
- wire.jsonl 的 `metadata` 类型行直接忽略（`Core/AgentLogs.cs:272`）。
- 时间戳缺省时用文件 mtime 兜底（`Core/AgentLogs.cs:270`、`Core/AgentLogs.cs:278`）。

## 验证

```sh
# 本机用量链路
codeusage cost -p kimi --refresh --days 7 --json

# 额度链路：任一凭据来源可用即可（含本机 OAuth 静默导入）
codeusage usage -p kimi

# 确认凭据来源
codeusage providers | grep kimi

# 导入链路自测（覆盖 api_key 优先、OAuth 刷新、轮转回写、失败静默）
dotnet run --project src/CodeUsageMonit.Cli -- --self-test
# 相关用例：Kimi local config.toml: api_key read only from the managed kimi-code table
#          Kimi OAuth refresh: posts the client grant, rotates the file, keeps unknown keys at 0600, never persists
#          Kimi OAuth failures stay silent: no refresh token, 400/401/500, network error, empty access
#          Kimi refresh with an absurd expires_in skips the expires_at write-back instead of overflowing
#          Kimi import chain: inline api_key wins over OAuth and is the only persisted path
#          Kimi wire records: old StatusUpdate and new turn-scoped usage.record
```
