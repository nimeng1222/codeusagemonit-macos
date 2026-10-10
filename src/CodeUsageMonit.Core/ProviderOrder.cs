using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeUsageMonit {
    // 概览平台排序的共享口径（完整面板的平台卡与紧凑面板的 Urgent 共用，避免两处漂移）：
    //   Tier 0 = 有额度/余额读数（Quotas 或 Balances 非空；cached 态保留读数，同样算），
    //            按最紧剩余%升序——最先告急的排最前；
    //   Tier 1 = ready 但没有读数（仅本机用量，如 DeepSeek Harness）；
    //   Tier 2 = 其余状态（loading / setup / error / expired / 无状态）。
    // 同级内保持调用方给定的顺序（目录序）：排序只用稳定键（OrderBy 是稳定排序）。
    // 只排序，不过滤：调用方传入的全部 id 都会原样返回。
    public static class ProviderOrder {
        public static int Tier(ProviderState state) {
            if (state == null) return 2;
            if (state.Quotas.Count > 0 || state.Balances.Count > 0) return 0;
            return state.Status == "ready" ? 1 : 2;
        }
        // Tier 0 内部的紧度：自动主窗口的剩余%（越低越靠前）。纯余额平台没有剩余%口径，
        // 排在同级的额度平台之后（Double.MaxValue），其余按目录序稳定。
        public static double Tightness(ProviderState state) {
            Quota main = MainQuota(state);
            return main != null ? main.Remaining : Double.MaxValue;
        }
        // 自动主窗口：剩余最少的一个；Cursor 的 Grok Bot 行是附加 allowance，不参与自动
        // 挑选（与紧凑面板 Headline 的既有语义一致）。全是 Grok Bot 时退回全量挑选。
        public static Quota MainQuota(ProviderState state) {
            if (state == null) return null;
            IEnumerable<Quota> pool = state.Id == "cursor" ? state.Quotas.Where(q => q.Label != Parsers.CursorGrokLabel) : state.Quotas;
            return pool.OrderBy(q => q.Remaining).FirstOrDefault() ?? state.Quotas.OrderBy(q => q.Remaining).FirstOrDefault();
        }
    }
}
