# DeepSeek（deepseek）

解析器：`Core/DeepSeekHarness.cs`（`HarnessLogs`，71 行）。余额：`Core/Core.cs:674-680`。

## 数据来源

- **扫描路径**：`~/.dsh/sessions/` 递归（`Core/DeepSeekHarness.cs:13`、
  `Core/DeepSeekHarness.cs:18-19`）。这是 **DeepSeek Harness（dsh）** 的会话目录——
  DeepSeek 平台的本机用量来自 Harness 而不是某个「DeepSeek CLI」
  （`Core/Core.cs:142` 的 `UsageSource` 也据此显示）。
- **文件格式**：`session.v*.jsonl` / `.jsonl.zstd` / `.jsonl.zst`。zstd 版本按**帧追加**：
  只解到最后一个完整帧，半帧留给下次扫描；尾部超过 512 MB 直接放弃本次
  （`Core/DeepSeekHarness.cs:26-36`，帧边界由自带 RFC 8878 解码器给出，
  `Core/Zstd.cs:15-37`）。
- **一次模型调用 = 两条记录**：`request/header`（发出时刻 + 请求模型，记入
  `state.StartMs` / `state.Model`）→ `assistant/message`（usage + 最后输出时刻）
  （`Core/DeepSeekHarness.cs:37-45`）。耗时 = 两者之差，进输出速度统计
  （`Core/DeepSeekHarness.cs:65-66`；准入阈值 `Core/Timing.cs:18`、`Core/Timing.cs:25-28`：
  输出 ≥50 token、0.2s ≤ 耗时 ≤ 600s）。
- **缓存口径修正**：若 `total == input + output` 而 cacheRead > 0，说明该记录把缓存命中
  算进了 inputTokens——从 input 里扣掉（`Core/DeepSeekHarness.cs:51-53`）。
- **去重**：键 `dsh|<session>|<seq 或 time>`（`Core/DeepSeekHarness.cs:56-58`）。
- **额度（可选）**：`GET api.deepseek.com/user/balance`（`Core/Core.cs:679`），
  解析 `balance_infos[].total_balance` + 币种，**多币种并列显示、绝不求和也不当百分比**
  （`Core/Core.cs:417-421`，自测
  `DeepSeek currencies are never summed or treated as percentages`）。

## 凭据

- 环境变量 `DEEPSEEK_API_KEY` 优先（`Core/Core.cs:172`），其次设置里保存的 key
  （钥匙串，`Core/Core.cs:334-337`）。
- **无 key 但本机在用 Harness** → 卡片显示「仅本机用量 · DeepSeek Harness」，
  不报 setup 错误：Harness 登录用的是 DeepSeek 平台授权令牌，**余额 API 不接受**，
  拿它查余额只会白错一次（`Core/Core.cs:675-678` 注释）。
- 有 key 才走 `Need` 检查并请求余额（`Core/Core.cs:679`）。

## 坑点

- zstd 帧仍在写时解码会截断：按 `complete` 偏移提交、半帧重读
  （`Core/DeepSeekHarness.cs:28-36`）——不要改成整文件解压。
- `state.StartMs` / `state.Model` 是跨读取状态：header 到了、message 没到时停在索引里，
  文件重写时随 `Reset` 清零（`Core/DeepSeekHarness.cs:37`、`Core/LocalLogs.cs:175-181`）。
- 只有平台价目表里有的模型才折算费用，否则记入 Unpriced（不冒充 $0，
  `Core/DeepSeekHarness.cs:60-63`）。
- 索引文件为 `deepseek-logs.json`，扫描入口注册在 `Core/UsageScan.cs:18`。

## 验证

```sh
# 本机用量链路（装了 dsh 并用过才有数据）
codeusage cost -p deepseek --refresh --days 7 --json

# 余额链路（需 DEEPSEEK_API_KEY 或已保存 key）
[ -n "$DEEPSEEK_API_KEY" ] && echo "env set" || echo "env unset"
codeusage usage -p deepseek

# 无 key + Harness 在用时：应显示「仅本机用量 · DeepSeek Harness」而不是 setup 错误
codeusage usage -p deepseek --json     # status=ready, plan="DeepSeek Harness"

# 回归
dotnet run --project src/CodeUsageMonit.Cli -- --self-test
# 相关用例：DeepSeek Harness sessions: zstd frames, model, cache and speed, read incrementally
#          DeepSeek currencies are never summed or treated as percentages
```
