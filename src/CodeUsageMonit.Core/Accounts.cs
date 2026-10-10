using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeUsageMonit {
    // ROADMAP Phase 5（每平台多账户）。数据模型只有两条新状态，都在 settings.json 的
    // config.Accounts 里；额度缓存、刷新通道（LimitsRuntime）、健康状态（SourceHealth）
    // 与导出保持 provider 粒度不变——切换活动账户 = 凭据变更，走 ResetFor 定向刷新，
    // 不改键粒度（评估结论：粒度变更没有正确性收益，只会放大缓存与诊断的改动面）。
    //
    //   ProviderAccounts { Active, Items[] }   每平台一条；字典缺省 = 单个隐式默认账户。
    //   ProviderAccount  { Id, Name }          Id 稳定不变（"default" / 生成的 "a2"…），
    //                                          Name 可选显示名；空 = 「账户 N」（N 从 1 起）。
    //
    // 隐式默认账户：config.Accounts 没有该平台时视为单个账户 { Id="default", Name="" }。
    // 旧版本配置由此零迁移即可读；用户添加第二个账户时才把该平台物化成显式条目。
    //
    // 密钥存储键 = providerId + ":" + accountId（钥匙串 account 同名）；引用文件为
    // data/<providerId>_<accountId>.key（'_' 不出现在两者的合法字符集里，无歧义，
    // 且避免 ':' 进入文件名——Windows 血统上 ':' 是盘符分隔符）。旧 data/<id>.key 与
    // 钥匙串 <id> 是默认账户的遗留位置，由 Store.SavedKey 惰性迁移（见 Core.cs）。
    public sealed class ProviderAccount {
        public string Id = "", Name = "";
    }
    public sealed class ProviderAccounts {
        public string Active = "";
        public List<ProviderAccount> Items = new List<ProviderAccount>();
    }
    public static class Accounts {
        public const string DefaultId = "default";
        // 账户 id 字符集：与 provider id 一样只允许字母数字与连字符（生成值只用小写字母数字），
        // 保证引用文件名与钥匙串 account 无路径歧义。
        private static bool ValidId(string id) {
            return !String.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => (Char.IsLetterOrDigit(c) && c < 128) || c == '-');
        }
        // 平台的账户清单；字典缺省时返回单个隐式默认账户（不落盘）。
        public static List<ProviderAccount> List(AppConfig config, string id) {
            ProviderAccounts entry;
            if (config != null && config.Accounts != null && config.Accounts.TryGetValue(id ?? "", out entry) && entry != null) {
                var items = (entry.Items ?? new List<ProviderAccount>()).Where(a => a != null && ValidId(a.Id)).ToList();
                if (items.Count > 0) return items;
            }
            return new List<ProviderAccount> { new ProviderAccount { Id = DefaultId } };
        }
        // 活动账户 id：Active 有效则用之，否则第一个账户；config 为空（部分纯函数调用方）也是 default。
        public static string ActiveId(AppConfig config, string id) {
            List<ProviderAccount> items = List(config, id);
            string active = null;
            if (config != null && config.Accounts != null && config.Accounts.TryGetValue(id ?? "", out var entry) && entry != null) active = entry.Active;
            return items.Any(a => a.Id == active) ? active : items[0].Id;
        }
        // 显示名：显式 Name 优先，否则「账户 N」（N = 清单中的序号，从 1 起）。
        public static string DisplayName(AppConfig config, string id, string accountId) {
            List<ProviderAccount> items = List(config, id);
            for (int i = 0; i < items.Count; i++) {
                if (items[i].Id != accountId) continue;
                return items[i].Name.Trim().Length > 0 ? items[i].Name.Trim() : I18n.T("账户 {0}", i + 1);
            }
            return accountId ?? "";
        }
        // 取物化条目（没有就创建：单默认账户 + Active 指向它）。
        public static ProviderAccounts Ensure(AppConfig config, string id) {
            if (config.Accounts == null) config.Accounts = new Dictionary<string, ProviderAccounts>();
            ProviderAccounts entry;
            if (!config.Accounts.TryGetValue(id ?? "", out entry) || entry == null) {
                entry = new ProviderAccounts { Active = DefaultId, Items = new List<ProviderAccount> { new ProviderAccount { Id = DefaultId } } };
                config.Accounts[id ?? ""] = entry;
            }
            return entry;
        }
        // 添加账户：id 取现有 aN 的最大号 +1（删除后编号可能复用——删除已清掉该账户的
        // 引用文件与钥匙串键，读取以引用文件为准，复用不会读到旧密钥）。新账户不自动激活。
        // 返回新账户 id。调用方负责把返回条目物化（Ensure 已由本方法完成）。
        public static string Add(AppConfig config, string id, string name) {
            ProviderAccounts entry = Ensure(config, id);
            int max = 0;
            foreach (ProviderAccount account in entry.Items) {
                if (account.Id.Length > 1 && account.Id[0] == 'a' && Int32.TryParse(account.Id.Substring(1), out int n) && n > max) max = n;
            }
            var added = new ProviderAccount { Id = "a" + (max + 1), Name = (name ?? "").Trim() };
            entry.Items.Add(added);
            return added.Id;
        }
        // 删除账户并清理它的密钥（钥匙串 + 引用文件，含默认账户的遗留位置）。
        // 清单至少保留一个账户：删最后一个返回 false。被删的是活动账户时，活动落到第一个。
        public static bool Remove(AppConfig config, string id, string accountId) {
            ProviderAccounts entry = Ensure(config, id);
            if (entry.Items.Count <= 1) return false;
            ProviderAccount victim = entry.Items.FirstOrDefault(a => a.Id == accountId);
            if (victim == null) return false;
            entry.Items.Remove(victim);
            try { Store.SetProviderKey(id, accountId, ""); } catch { }
            if (entry.Active == accountId || !entry.Items.Any(a => a.Id == entry.Active)) entry.Active = entry.Items[0].Id;
            return true;
        }
        public static void Rename(AppConfig config, string id, string accountId, string name) {
            ProviderAccounts entry = Ensure(config, id);
            ProviderAccount account = entry.Items.FirstOrDefault(a => a.Id == accountId);
            if (account != null) account.Name = (name ?? "").Trim();
        }
        // 切换活动账户：目标不存在时返回 false（不改动）。
        public static bool SetActive(AppConfig config, string id, string accountId) {
            if (!List(config, id).Any(a => a.Id == accountId)) return false;
            Ensure(config, id).Active = accountId;
            return true;
        }
        // 启动/保存前的修复：剔除非法与重复 id 的条目、空清单补默认账户、
        // Active 失效时回落到第一个。对合法配置是空操作（幂等）。
        public static void Normalize(AppConfig config) {
            if (config == null || config.Accounts == null) return;
            foreach (string key in config.Accounts.Keys.ToList()) {
                ProviderAccounts entry = config.Accounts[key];
                if (entry == null) { config.Accounts.Remove(key); continue; }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                entry.Items = (entry.Items ?? new List<ProviderAccount>())
                    .Where(a => a != null && ValidId(a.Id) && seen.Add(a.Id)).ToList();
                if (entry.Items.Count == 0) entry.Items.Add(new ProviderAccount { Id = DefaultId });
                if (!entry.Items.Any(a => a.Id == entry.Active)) entry.Active = entry.Items[0].Id;
            }
        }
        // 设置页保存后的定向刷新依据：活动账户发生变化（含新增后激活）的平台集合。
        // before/after 是「平台 → 活动账户 id」快照。
        public static HashSet<string> ChangedProviders(IEnumerable<KeyValuePair<string, string>> before, IEnumerable<KeyValuePair<string, string>> after) {
            var changed = new HashSet<string>();
            var previous = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in before ?? Enumerable.Empty<KeyValuePair<string, string>>()) previous[pair.Key] = pair.Value;
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in after ?? Enumerable.Empty<KeyValuePair<string, string>>()) current[pair.Key] = pair.Value;
            foreach (string id in previous.Keys.Union(current.Keys)) {
                string was, now;
                previous.TryGetValue(id, out was);
                current.TryGetValue(id, out now);
                if (!String.Equals(was, now, StringComparison.Ordinal)) changed.Add(id);
            }
            return changed;
        }
    }
}
