using System;
using System.IO;
using System.Reflection;
using Library;
using Library.SystemModels;
using MirDB;

namespace MirBot
{
    /// <summary>
    /// Loads the client-side game database, mirroring CEnvir.LoadDatabase.
    ///
    /// This is NOT optional. Packet deserialization calls [CompleteObject] methods on the
    /// objects it builds, and ClientUserItem.Complete resolves its ItemInfo through
    /// Globals.ItemInfoList (LibraryCore/Globals.cs:511). With that collection null, the
    /// first packet carrying an item - the inventory sent during StartGame - throws a
    /// NullReferenceException inside BaseConnection.ReceiveData, which the base class
    /// swallows into a silent disconnect.
    ///
    /// The bot points at its OWN copy of System.db rather than the client's Data folder:
    /// Session.Initialize can write migrations, and the live folder belongs to the real
    /// client. Only System.db is needed - the multi-gigabyte .Zl sprite archives beside it
    /// are for the renderer.
    /// </summary>
    public static class GameDatabase
    {
        public static Session Session { get; private set; }
        public static string Version { get; private set; }

        private static readonly object LoadLock = new object();
        private static bool _loaded;

        /// <summary>
        /// Loads the database exactly once for the whole process. It assigns the STATIC Globals.*
        /// collections, so a second load would swap those references out from under live
        /// connections. Every bot in a host therefore shares one System.db - and must, since the
        /// server checks its version.
        /// </summary>
        public static void EnsureLoaded(string dataPath)
        {
            lock (LoadLock)
            {
                if (_loaded) return;

                Load(dataPath);
                _loaded = true;
            }
        }

        public static void Load(string dataPath)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
                throw new InvalidOperationException("DataPath is not set in bot.ini.");

            // MirDB builds its paths by plain concatenation, so the trailing separator matters:
            // without it the session looks for "...DataSystem.db".
            if (!dataPath.EndsWith(Path.DirectorySeparatorChar.ToString()) &&
                !dataPath.EndsWith("/"))
                dataPath += Path.DirectorySeparatorChar;

            string systemPath = Path.Combine(dataPath, "System.db");
            if (!File.Exists(systemPath))
                throw new FileNotFoundException(
                    $"No System.db at {systemPath}. Copy it from the client's Data folder " +
                    "(only System.db is needed, not the .Zl sprite archives).");

            Session = new Session(SessionMode.Users, dataPath) { BackUp = false };

            // The client also passes its own assembly for KeyBindInfo/WindowSetting. Those are
            // UI settings living in Users.db; the bot has no use for them and cannot reference
            // the Client project without dragging in the renderer.
            Session.Initialize(Assembly.GetAssembly(typeof(ItemInfo)));

            if (!Session.SystemDatabaseExists)
                throw new InvalidOperationException($"System.db at {systemPath} did not load.");

            Version = Session.SystemDatabaseVersion;

            Globals.ItemInfoList = Session.GetCollection<ItemInfo>();
            Globals.MagicInfoList = Session.GetCollection<MagicInfo>();
            Globals.MapInfoList = Session.GetCollection<MapInfo>();
            Globals.CurrencyInfoList = Session.GetCollection<CurrencyInfo>();
            Globals.InstanceInfoList = Session.GetCollection<InstanceInfo>();
            Globals.NPCPageList = Session.GetCollection<NPCPage>();
            Globals.MonsterInfoList = Session.GetCollection<MonsterInfo>();
            Globals.FishingInfoList = Session.GetCollection<FishingInfo>();
            Globals.StoreInfoList = Session.GetCollection<StoreInfo>();
            Globals.NPCInfoList = Session.GetCollection<NPCInfo>();
            Globals.MovementInfoList = Session.GetCollection<MovementInfo>();
            Globals.QuestInfoList = Session.GetCollection<QuestInfo>();
            Globals.QuestTaskList = Session.GetCollection<QuestTask>();
            Globals.CompanionInfoList = Session.GetCollection<CompanionInfo>();
            Globals.CompanionLevelInfoList = Session.GetCollection<CompanionLevelInfo>();
            Globals.DisciplineInfoList = Session.GetCollection<DisciplineInfo>();
            Globals.FameInfoList = Session.GetCollection<FameInfo>();
            Globals.BundleInfoList = Session.GetCollection<BundleInfo>();
            Globals.LootBoxInfoList = Session.GetCollection<LootBoxInfo>();
            Globals.HelpInfoList = Session.GetCollection<HelpInfo>();
            Globals.MilestoneInfoList = Session.GetCollection<MilestoneInfo>();
            Globals.MilestoneTaskInfoList = Session.GetCollection<MilestoneInfoTask>();
            Globals.CraftingLevelInfoList = Session.GetCollection<CraftingLevelInfo>();
            Globals.CraftingRecipeInfoList = Session.GetCollection<CraftingRecipeInfo>();
        }

        /// <summary>
        /// The server rejects a client whose System.db version differs from its own. Comparing
        /// here turns a confusing mid-session failure into a clear message at startup.
        /// </summary>
        public static string WarnIfStale(string serverVersion)
        {
            if (string.IsNullOrEmpty(serverVersion) || string.IsNullOrEmpty(Version)) return null;

            if (!string.Equals(serverVersion, Version, StringComparison.Ordinal))
                return $"WARNING: System.db version mismatch - bot has {Version}, server has " +
                       $"{serverVersion}. Re-copy System.db from the client's Data folder.";

            return null;
        }
    }
}
