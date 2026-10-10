// codeusage — the command-line companion. The CLI project is a thin entry shim over
// CodeUsageMonit.CliProgram, which lives with the rest of the code in Core.
namespace CodeUsageMonit.Cli {
    public static class Program {
        public static int Main(string[] args) {
            return CodeUsageMonit.CliProgram.Main(args);
        }
    }
}
