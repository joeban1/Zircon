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
            bool checkMaps = false;
            string checkTravel = null;
            string checkVendors = null;
            string checkTeleports = null;
            string checkProfiles = null;
            string checkExits = null;
            string checkSafe = null;
            string checkItems = null;
            int seconds = 0;   // 0 = run until Ctrl+C; bounded runs are for testing

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--dir" && i + 1 < args.Length) directory = args[i + 1];
                if (args[i] == "--log" && i + 1 < args.Length) logPath = args[i + 1];
                if (args[i] == "--console") console = true;
                if (args[i] == "--check-maps") { checkMaps = true; console = true; }
                if (args[i] == "--check-travel" && i + 1 < args.Length)
                { checkTravel = args[i + 1]; console = true; }
                if (args[i] == "--vendors")
                { checkVendors = i + 1 < args.Length ? args[i + 1] : ""; console = true; }
                if (args[i] == "--teleports")
                { checkTeleports = i + 1 < args.Length ? args[i + 1] : ""; console = true; }
                if (args[i] == "--maps")
                { checkProfiles = i + 1 < args.Length ? args[i + 1] : ""; console = true; }
                if (args[i] == "--exits")
                { checkExits = i + 1 < args.Length ? args[i + 1] : ""; console = true; }
                if (args[i] == "--safezones")
                { checkSafe = i + 1 < args.Length ? args[i + 1] : ""; console = true; }
                if (args[i] == "--items")
                { checkItems = i + 1 < args.Length ? args[i + 1] : ""; console = true; }
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

                // Diagnostic: is MapPath right, and do the grids parse? Worth its own mode because
                // a wrong path is silent at runtime - the bot just steers blind again.
                if (checkMaps) return host.CheckMaps() ? 0 : 3;

                // Diagnostic: can we actually route there from the starting town?
                if (checkTravel != null) return host.CheckTravel(checkTravel) ? 0 : 4;

                if (checkVendors != null) { host.DumpVendors(checkVendors); return 0; }

                if (checkTeleports != null) { host.DumpTeleports(checkTeleports); return 0; }

                if (checkProfiles != null) { host.DumpMapProfiles(checkProfiles); return 0; }

                if (checkExits != null) { host.DumpExits(checkExits); return 0; }

                if (checkSafe != null) { host.DumpSafeZones(checkSafe); return 0; }

                if (checkItems != null) { host.DumpItems(checkItems); return 0; }

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
