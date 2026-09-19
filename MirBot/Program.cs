using System;
using System.IO;

namespace MirBot
{
    // The host: owns N bot instances and the state they share. Each bot runs on its own thread;
    // see BotInstance for the threading contract.
    //
    // Bots are configured one file per bot - bot-<id>.ini beside the executable. A lone bot.ini
    // still works, so an existing single-bot setup keeps running unchanged.
    public static class Program
    {
        public static int Main(string[] args)
        {
            string directory = AppContext.BaseDirectory;
            string logPath = null;
            bool console = false;
            int seconds = 0;   // 0 = run until Ctrl+C; bounded runs are for testing

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--dir" && i + 1 < args.Length) directory = args[i + 1];
                if (args[i] == "--log" && i + 1 < args.Length) logPath = args[i + 1];
                if (args[i] == "--console") console = true;
                if (args[i] == "--seconds" && i + 1 < args.Length &&
                    int.TryParse(args[i + 1], out int parsed)) seconds = parsed;
            }

            logPath ??= Path.Combine(directory, "mirbot.log");

            // Console output is opt-in: Console.Out is synchronized, so N bot threads writing to it
            // serialize on a process-global lock, and a stalled SSH pipe would block a bot thread
            // indefinitely. The file is the reliable channel anyway - stdout does not stream back
            // over the SSH/ProxyCommand chain to this VM.
            BotLog log = new BotLog(logPath, "host", console);

            try
            {
                BotHost host = new BotHost(log);

                host.Load(directory);
                if (!host.Prepare()) return 2;

                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    host.RequestShutdown();
                };

                host.Run(seconds);
                return 0;
            }
            catch (Exception ex)
            {
                log.Write("Host failed: " + ex);
                return 1;
            }
            finally
            {
                // AutoFlush is off, so the buffered tail - including any summary - is lost
                // without this.
                log.Dispose();
            }
        }
    }
}
