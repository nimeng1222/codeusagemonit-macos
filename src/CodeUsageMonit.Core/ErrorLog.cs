using System;
using System.IO;
using System.Text;

namespace CodeUsageMonit {
    // errors.log 的统一读写口（review P2-2）。此前 MonitorPanel.LogError 与未处理异常
    // 处理器各自无限追加、诊断报告整读文件取尾：长跑进程日志无界增长，报告读取代价
    // 也随文件变大。现在：
    //   · Append —— 写入前检查大小，超过 MaxBytes（512KB）时截断保留尾部约一半
    //    （换行边界对齐，半行丢弃），再追加本条；
    //   · ReadTail —— 从文件尾向前定位最近 maxLines 行，绝不整读大文件。
    // 写日志自身失败必须吞掉（诊断手段不能变成新错误）。
    public static class ErrorLog {
        public const long MaxBytes = 512 * 1024;

        public static void Append(string context, Exception e) {
            try {
                Directory.CreateDirectory(Store.Data);
                string path = Path.Combine(Store.Data, "errors.log");
                try { TruncateIfNeeded(path); } catch { }
                File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + context + " " + e.GetType().Name + " " + e.Message + "\n" + e + "\n", new UTF8Encoding(false));
            } catch { }
        }

        // 超上限：保留尾部约一半。截断点 = 文件长度一半之后最近的换行（含）之后——
        // 保证截剩的内容仍从一行的行首开始。找不到换行的极端情况按保留空处理。
        internal static void TruncateIfNeeded(string path) {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxBytes) return;
            long from = info.Length / 2;
            string tail;
            using (FileStream stream = info.OpenRead()) {
                int b;
                do { b = stream.ReadByte(); from++; } while (b > 0 && b != '\n');
                long remaining = info.Length - from;
                if (remaining <= 0) tail = "";
                else {
                    var bytes = new byte[remaining];
                    int read = 0;
                    while (read < remaining) { int n = stream.Read(bytes, read, (int)(remaining - read)); if (n <= 0) break; read += n; }
                    tail = Encoding.UTF8.GetString(bytes, 0, read);
                }
            }
            File.WriteAllText(path, tail, new UTF8Encoding(false));
        }

        // 最近 maxLines 行。UTF-8 按字节从尾向前找 '\n'（换行是单字节，多字节字符不会
        // 被切开），找到 maxLines+1 个换行后的第一个字节即窗口起点；整文件不足这么多
        // 行时返回全文。以 FileShare.ReadWrite 打开，不阻塞并发的追加写。
        public static string ReadTail(string path, int maxLines) {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                long end = stream.Length;
                if (end == 0) return "";
                long pos = end, start = 0;
                int newlines = 0;
                var buffer = new byte[4096];
                while (pos > 0 && newlines <= maxLines) {
                    int size = (int)Math.Min(buffer.Length, pos);
                    pos -= size;
                    int filled = 0;
                    while (filled < size) { int n = stream.Read(buffer, filled, size - filled); if (n <= 0) throw new IOException("unexpected EOF"); filled += n; }
                    for (int i = size - 1; i >= 0 && newlines <= maxLines; i--) {
                        if (buffer[i] == '\n') { newlines++; if (newlines > maxLines) { start = pos + i + 1; break; } }
                    }
                }
                long count = end - start;
                var tail = new byte[count];
                stream.Seek(start, SeekOrigin.Begin);
                int done = 0;
                while (done < count) { int n = stream.Read(tail, done, (int)(count - done)); if (n <= 0) break; done += n; }
                return Encoding.UTF8.GetString(tail, 0, done);
            }
        }
    }
}
