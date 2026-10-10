# Antigravity（antigravity）

解析器：`Core/AgentLogs.cs:292-497`（`AgentLogs.ScanAntigravity`，protobuf）。
额度：`Core/LocalAntigravity.cs`（本机 language-server 优先）+ `Core/Core.cs:686-700`（回退）。

## 数据来源

- **扫描路径**：`$ANTIGRAVITY_DATA_DIR`（逗号分隔多根）→ 未设置时 5 个默认根：
  `~/.gemini/antigravity`、`-cli`、`-ide`、`-backup`、`~/.config/antigravity`
  （`Core/AgentLogs.cs:108-110`；macOS 路径合法性有专门自测）。各根下
  `conversations/*.db`（无 conversations 子目录时整根找 `*.db`），
  **按 mtime 过滤 31 天窗口**（`Core/AgentLogs.cs:318-321`）。
- **文件格式**：每对话一个 SQLite 库，内容是 **protobuf**（自解 varint / fixed /
  length-delimited 裸线格式，`Core/AgentLogs.cs:66-101`）：
  - `gen_metadata`（生成元数据，field 4 = usage、17 = 重试、9 = 时间、19/21 = 模型名、
    3 = 模型 id）与 `steps`（field 9 = usage、28 = 重试、24 = 模型信息、8/1 = 时间）
    （`Core/AgentLogs.cs:408-436`）；
  - `trajectory_metadata_blob` 首行时间戳兜底（`Core/AgentLogs.cs:342-343`）。
- **时间回退链**：事件自带时间 → 同 identity 已知时间 → trajectory 时间 → 文件 mtime，
  带置信秩（rank），高秩覆盖低秩（`Core/AgentLogs.cs:363-384`）。
- **跨 id / 跨库去重**（本平台最大的坑，`Core/AgentLogs.cs:385-407`）：同一 response 会以
  多个 id（`response:` / `provider:` / `message:`）存在多个库里；按 identities 并查集式
  合并，**各 token 维度取最大值、时间取最好秩**。自测覆盖
  `Antigravity default data roots are legal multi-segment paths on this platform` 等。
- **模型名规范化**：数字模型 id 映射表（246=gemini-2.5-pro、333/334=claude-4.5-sonnet 等，
  `Core/AgentLogs.cs:449-459`）+ 别名归一表（`gemini 3 pro thinking` → `gemini-3-pro` 等，
  `Core/AgentLogs.cs:460-486`）；未识别 id 生成占位名。
- **计费**：effort 变体（`gemini-3.8-flash-high` 等）**只做精确匹配**，不许借邻居价
  （`Core/AgentLogs.cs:487-496`）；Google provider（3/24/30）加 `google/`、`vertex_ai/` 等
  前缀候选。
- **额度**：两级链路（`Core/Core.cs:686-699`）：
  1. **本机 language-server**（`Core/LocalAntigravity.cs:26-44`）：`ps` / `lsof`
     （`Platform/ProcessDiscovery.cs`）找到 Antigravity 进程与其监听端口，POST
     `127.0.0.1:<port>/exa.language_server_pb.LanguageServerService/` 的
     `RetrieveUserQuotaSummary` / `GetUserStatus` / `GetCommandModelConfigs`，带
     `X-Codeium-Csrf-Token`；总预算 7 秒、单请求 1.5 秒（`Core/LocalAntigravity.cs:31-33`、
     `54`）。
  2. **回退**：钥匙串条目 `gemini:antigravity`（JSON 含 token）→ Google
     `cloudcode-pa.googleapis.com/v1internal:loadCodeAssist` +
     `retrieveUserQuotaSummary` / `fetchAvailableModels`（`Core/Core.cs:689-698`）。

## 凭据

- 首选不需要：本机 language-server 用的是应用自己进程内的会话（应用需在运行）。
- 回退凭据存 **macOS 钥匙串**，target `gemini:antigravity`（`Core/Core.cs:689`、
  `Platform/Keychain.cs:19-23`）；`data/` 下无对应 .key 文件——这条不在
  SetProviderKey 体系里，是钥匙串 target 的直接查找。
- CLI 凭据列固定显示「Antigravity 桌面应用 / 钥匙串」（`Core/Cli.cs:270`）。

## 坑点

- **全 0 额度 ≠ 有额度**：`fetchAvailableModels` 只返回模型可用性时，报
  setup「仅检测到模型可用性，尚未取得真实额度。请打开 Antigravity 后刷新」
  （`Core/Core.cs:695-698`）。
- **TLS 例外只限 loopback**：自签名证书回调仅对 `127.0.0.1:<port>` 放行，外网请求
  照常校验（`Core/LocalAntigravity.cs:49-53` 注释）。
- 默认根特意用正斜杠多段路径——反斜杠字面量在 mac 上会被 `Path.Combine`
  拼成含 `\` 的非法单段（`Core/AgentLogs.cs:106-108` 注释，有自测防回归）。
- `VisibleOutput` / `Reasoning` / `TotalOutput` 三者互相钳制，防重复计
  （`Core/AgentLogs.cs:373-375`）。
- 索引整体重建（`Core/AgentLogs.cs:313-316`），单文件 `LogFile`。
- 额度解析两形态：`groups[].buckets[]`（weekly/5h）与 models 的 `quotaInfo`
  兜底（`Core/Core.cs:549-557`、`Core/LocalAntigravity.cs:69-87`）。

## 验证

```sh
# 本机用量链路（用过 Antigravity 才有 conversations/*.db）
codeusage cost -p antigravity --refresh --days 7 --json

# 额度链路：应用在运行 → 本机 language-server；否则走钥匙串回退
codeusage usage -p antigravity

# 钥匙串条目存在性（不打印值）
security find-generic-password -s codeusagemonit -a "gemini:antigravity" >/dev/null 2>&1 && echo "keychain entry exists" || echo "no keychain entry"

# 回归
dotnet run --project src/CodeUsageMonit.Cli -- --self-test
# 相关用例：Antigravity default data roots are legal multi-segment paths on this platform
#          Antigravity desktop response parses measured model quotas
#          Antigravity fractions map correctly and null buckets stay absent
#          Antigravity model fallback preserves each model's quota
```
