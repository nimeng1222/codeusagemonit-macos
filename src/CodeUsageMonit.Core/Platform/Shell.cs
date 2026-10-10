// POSIX shell 参数 quoting（macOS 适配）。Terminal 的 do script 把整条命令交给用户的
// shell 执行：路径或参数里含空格、单引号、双引号、$、反引号等时必须先按 POSIX 规则逐参数
// 包裹，之后才做 AppleScript 字符串转义——顺序颠倒（旧实现只做后者，review #8）保护不了
// shell 元字符，带空格的路径会被拆成多段。纯函数，供 Connect.LaunchCli 使用，Core 自测覆盖。
using System;

namespace CodeUsageMonit {
    public static class Shell {
        // 单引号包裹；内嵌单引号以 '\'' 关闭-转义-重开。包裹后整词按字面交给 shell，
        // 空格与元字符全部失去特殊含义；空串与 null 得到 ''（空参数占位）。
        public static string Quote(string argument) {
            return "'" + (argument ?? "").Replace("'", "'\\''") + "'";
        }
    }
}
