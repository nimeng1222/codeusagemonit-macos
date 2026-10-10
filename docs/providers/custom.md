# 自定义平台（custom-…）

`Core/CustomProviders.cs` 全文（199 行）。入口：`Core/Core.cs:730-734`（Fetch 的 default 分支）。

一个用户自定义平台 = **一次 GET 请求到 JSON 接口** + 点路径（dot-path）字段映射。
刻意收窄边界（与 CodexBar 的声明式 provider 同一条界线）：
GET-only、固定认证头形态、密钥绝不内联、不跟随重定向、响应有上限
（文件头注释 `Core/CustomProviders.cs:15-19`）。

## 数据来源

- **唯 一来源是接口**，没有本机日志、没有小时桶索引（`ProviderCatalog.All` 把
  `custom-…` id 追加在内置平台之后，`Core/Core.cs:134`）。
- **定义 JSON**（设置 → 自定义平台编辑，存 `custom-providers.json` 的定义串列表，
  `Core/CustomProviders.cs:40-45`）：
  - `url`：完整接口地址；`auth`：`none` / `bearer` / `x-api-key` / `token` / `header`
    （`header` 需合法头名，禁止 host/cookie 等 7 个危险头，`Core/CustomProviders.cs:36`、
    `66-68`）；
  - `windows[]`（最多 6 个）：每个窗口**三种写法三选一**——`usedPercent`、
    `remainingPercent`、`used`/`remaining` + `limit`；可选 `resetsAt` / `resetInSeconds`、
    `ratio`（0–1 比例）、`windowHours`（`Core/CustomProviders.cs:69-83`）；
  - `balance`（`amount` 路径 + 固定 `currency` 或 `currencyPath`）、`plan`、`account`
    （`Core/CustomProviders.cs:84-92`）；
  - `id` 强制加 `custom-` 前缀，只许小写字母数字连字符（`Core/CustomProviders.cs:57-60`）。
  模板见 `Core/CustomProviders.cs:38`。
- **映射语义**：缺失字段保持未知、**绝不当作 0%**（`Core/CustomProviders.cs:149`）；
  全部字段都没映射到 → 显式报错（`Core/CustomProviders.cs:160`）。
- 演示模式有一个仅内存的自定义平台样例，不落盘（`Desktop/AppMonitor.cs:573-577`）。

## 凭据

- **密钥只进声明的认证头**，不进 URL、不进日志（`Core/CustomProviders.cs:175-179`）。
- 密钥来源：`Fetch` 走 `Store.SavedKey(id)`——即钥匙串（`Core/Core.cs:733`）；
  `auth != "none"` 而没有密钥 → setup 提示（`Core/CustomProviders.cs:170`）。
- 定义串（含 url / 字段路径）明文存 `custom-providers.json`，**不含密钥**；
  备份包含该文件（`Core/DataSync.cs:411`）。

## 坑点

- **安全边界**（改代码前先读懂）：
  - 公网必须 https；http 仅限本机 / 局网（loopback、`.local`、10/8、172.16/12、
    192.168/16、169.254/16、IPv6 link/site/ULA；`Core/CustomProviders.cs:109-123`）；
  - **不跟随重定向**——防密钥被 3xx 转发泄漏（`Core/CustomProviders.cs:180-182`）；
  - 响应上限 1 MB，超限中止（`Core/CustomProviders.cs:185-189`）；超时 15 秒
    （`Core/CustomProviders.cs:171`）；
  - URL 禁带 userinfo 与 `#` 片段（`Core/CustomProviders.cs:112`）。
- 路径语法只支持 `a.b.c` 与 `[0]`，深度 ≤31 段，长度 ≤256
  （`Core/CustomProviders.cs:95-100`）；`J.Get` 走的是精确键名查找，JSON 键含点号时
  路径写不出来。
- `ratio: true` 时 used/remaining 数值按 0–1 处理乘 100（`Core/CustomProviders.cs:142-143`）。
- `epoch 毫秒`重置时间由 `J.Iso` 统一识别（≥12 位当毫秒，`Core/Core.cs:195-203`）。
- 401/403 报「密钥无效或无权限」（expired 态），其他非 2xx 报 HTTP 码
  （`Core/CustomProviders.cs:183-184`）。

## 验证

```sh
# 平台出现在列表里（启用后）；凭据列三态：环境变量/已保存的密钥（钥匙串）/无需密钥
codeusage providers | grep custom

# 额度链路：接口可达 + 字段路径正确才返回 ready
codeusage usage -p custom-<你的id>

# 定义校验不联网：设置里保存非法定义会被 Parse 拒绝（错误文案即返回值）；
# 映射回归用自测覆盖
dotnet run --project src/CodeUsageMonit.Cli -- --self-test
# 相关用例：Custom provider definitions are validated before any request
#          Custom provider mapping: paths, ratios, counts, resets and missing fields
```

联调建议：先用 `curl -s <url> | head -c 2000` 肉眼确认字段路径，再在设置里保存——
CLI 的 `usage -p custom-…` 失败时 `message` 字段会带具体原因（JSON 格式错误 / 密钥无效 /
找不到字段）。
