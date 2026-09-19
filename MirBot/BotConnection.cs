using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Sockets;
using Library;
using Library.Network;
using C = Library.Network.ClientPackets;
using G = Library.Network.GeneralPackets;
using S = Library.Network.ServerPackets;

namespace MirBot
{
    public enum BotStage
    {
        Connecting,
        Versioning,
        LoggingIn,
        Selecting,
        InGame,
        Finished
    }

    /// <summary>
    /// A headless client connection. Speaks the same protocol as Client/Envir/CConnection.cs
    /// but has no dependency on the renderer - it derives straight from BaseConnection in
    /// LibraryCore, which is plain net10.0 with no UI references.
    ///
    /// Packet handlers are found by reflection: a public method named Process taking exactly
    /// one packet type. Anything without a handler goes to ProcessUnhandledPacket.
    /// </summary>
    public sealed partial class BotConnection : BaseConnection
    {
        private readonly BotConfig _config;
        private readonly byte[] _clientHash;
        private readonly BotLog _log;

        public BotStage Stage { get; internal set; } = BotStage.Connecting;
        public StartInformation Start { get; private set; }
        public string ExitReason { get; private set; }

        /// <summary>True when the failure is worth another attempt rather than terminal.</summary>
        public bool Retryable { get; private set; }

        public int PingCount;
        public int UnhandledCount;

        /// <summary>Set when the server confirms a logout.</summary>
        public bool LoggedOut;

        private readonly Dictionary<string, int> _unhandledByType = new Dictionary<string, int>();

        protected override TimeSpan TimeOutDelay => _config.TimeOut;

        public BotConnection(TcpClient client, BotConfig config, byte[] clientHash, BotLog log)
            : base(client)
        {
            _config = config;
            _clientHash = clientHash;
            _log = log;

            // BaseConnection invokes OnException directly, so it must not be null.
            OnException += (o, e) => Log("EXCEPTION: " + e);

            // Without this, BaseConnection.ReceiveData swallows every exception and just sets
            // Disconnecting, so a deserialization failure looks like a silent socket close.
            // CConnection sets it too.
            AdditionalLogging = true;

            UpdateTimeOut();
            BeginReceive();
        }

        private void Log(string message) => _log.Write(message);

        #region Packet budget

        // The server bans the whole IP for five minutes if any ONE connection queues more than
        // Config.MaxPacket (50) packets - and that ban disconnects EVERY connection from that IP.
        // With several bots on one machine a single runaway loop takes them all down, so outgoing
        // packets are rate limited here rather than trusted to be well behaved. The realistic
        // trigger is a bug in new code, not normal play: normal play sits around 1-2 packets/sec.
        private const double TokensPerSecond = 10;
        private const double BurstTokens = 15;

        private double _tokens = BurstTokens;
        private DateTime _tokensAt = DateTime.UtcNow;

        public int DroppedPackets { get; private set; }

        public override void Enqueue(Packet p)
        {
            if (p == null) return;

            DateTime now = DateTime.UtcNow;
            _tokens = Math.Min(BurstTokens, _tokens + (now - _tokensAt).TotalSeconds * TokensPerSecond);
            _tokensAt = now;

            if (_tokens < 1)
            {
                // Dropping is the right call: the alternative is queueing into an IP ban that takes
                // every other bot down with us.
                DroppedPackets++;

                if (DroppedPackets == 1 || DroppedPackets % 50 == 0)
                    Log($"PACKET BUDGET exceeded, dropping {p.GetType().Name} " +
                        $"({DroppedPackets} dropped) - something is looping.");

                return;
            }

            _tokens--;
            base.Enqueue(p);
        }

        #endregion

        public override void TryDisconnect()
        {
            Disconnect();
            Stage = BotStage.Finished;
        }

        /// <summary>
        /// Ask the pump to shut down. Packet handlers must call this rather than
        /// TryDisconnect/Disconnect: Disconnect() nulls ReceiveList, and handlers run inside
        /// BaseConnection.Process's loop over that very list, so tearing down from a handler
        /// throws a NullReferenceException on the next iteration. Setting Disconnecting makes
        /// the loop exit cleanly and lets the pump tear down afterwards - the same thing the
        /// real client does in CConnection.Process(G.Disconnect).
        /// </summary>
        private void RequestStop(string reason)
        {
            if (!string.IsNullOrEmpty(reason))
            {
                ExitReason = reason;
                Log("STOPPING: " + reason);
            }

            Disconnecting = true;
        }

        public override void TrySendDisconnect(Packet p)
        {
            Disconnecting = true;
            Enqueue(p);
        }

        // The base implementation throws NotImplementedException, and BaseConnection.Process
        // treats that as a reason to disconnect. A real client handles hundreds of packet
        // types; the bot only needs a handful, so everything else is counted and dropped.
        protected override void ProcessUnhandledPacket(Packet p)
        {
            UnhandledCount++;

            string name = p.GetType().Name;
            _unhandledByType.TryGetValue(name, out int count);
            _unhandledByType[name] = count + 1;
        }

        public IEnumerable<KeyValuePair<string, int>> UnhandledSummary()
        {
            return _unhandledByType.OrderByDescending(x => x.Value);
        }

        #region Handshake

        public void Process(G.Connected p)
        {
            Log("Connected. Waiting for version check.");
            Stage = BotStage.Versioning;
            Enqueue(new G.Connected());
        }

        public void Process(G.CheckVersion p)
        {
            if (_clientHash == null)
            {
                RequestStop("Server asked for a client version hash but none is configured " +
                            "(set VersionPath or ClientHashHex in bot.ini).");
                return;
            }

            Log($"Sending client hash ({BitConverter.ToString(_clientHash).Replace("-", "").Substring(0, 16)}...).");
            Enqueue(new G.Version { ClientHash = _clientHash });
        }

        public void Process(G.GoodVersion p)
        {
            Log($"Version accepted. System DB version: {p.SystemDatabaseVersion}");

            string warning = GameDatabase.WarnIfStale(p.SystemDatabaseVersion);
            if (warning != null) Log(warning);

            if (p.DatabaseKey != null && p.DatabaseKey.Length > 0)
                Log("NOTE: server sent a database key (EncryptionEnabled=True). The bot ignores it - " +
                    "it only affects client-side database files, not the packet stream.");

            Stage = BotStage.LoggingIn;

            Enqueue(new C.SelectLanguage { Language = _config.Language });
            Enqueue(new C.Login
            {
                EMailAddress = _config.EMailAddress,
                Password = _config.Password,
                CheckSum = string.Empty
            });

            Log($"Login sent for {_config.EMailAddress}.");
        }

        public void Process(G.Ping p)
        {
            PingCount++;
            Enqueue(new G.Ping());
        }

        public void Process(G.PingResponse p) { }

        public void Process(G.Disconnect p)
        {
            RequestStop($"Server disconnected us: {p.Reason}");
        }

        #endregion

        #region Login and character select

        public void Process(S.Login p)
        {
            if (p.Result != LoginResult.Success)
            {
                string failure = $"Login failed: {p.Result}" +
                                 (string.IsNullOrEmpty(p.Message) ? "" : $" ({p.Message})");

                // Not fatal: after a Stop the server can hold the previous connection for up to ten
                // seconds past combat, so an immediate Start legitimately hits this. Faulting here
                // is what made the Start button look broken.
                if (p.Result == LoginResult.AlreadyLoggedIn ||
                    p.Result == LoginResult.AlreadyLoggedInPassword ||
                    p.Result == LoginResult.AlreadyLoggedInAdmin)
                    Retryable = true;

                if (p.Result == LoginResult.AccountNotExists && _config.CreateAccountIfMissing)
                {
                    Log("Creating account (CreateAccountIfMissing=true)...");
                    Enqueue(new C.NewAccount
                    {
                        EMailAddress = _config.EMailAddress,
                        Password = _config.Password,
                        BirthDate = new DateTime(1990, 1, 1),
                        RealName = "Bot",
                        Referral = string.Empty,
                        CheckSum = string.Empty
                    });
                    return;
                }

                RequestStop(failure);
                return;
            }

            Stage = BotStage.Selecting;

            // S.Login.Items is ACCOUNT STORAGE, not the character's bag.
            Items.ResetStorage(p.Items);
            Log($"  Storage:   {Items.DescribeStorage()}");

            List<SelectInfo> characters = p.Characters ?? new List<SelectInfo>();
            Log($"Logged in. {characters.Count} character(s) on the account.");

            foreach (SelectInfo character in characters)
                Log($"  [{character.CharacterIndex}] {character.CharacterName} " +
                    $"- Level {character.Level} {character.Class}");

            SelectInfo chosen = string.IsNullOrWhiteSpace(_config.CharacterName)
                ? characters.FirstOrDefault()
                : characters.FirstOrDefault(x => string.Equals(x.CharacterName, _config.CharacterName,
                    StringComparison.OrdinalIgnoreCase));

            if (chosen == null)
            {
                if (characters.Count > 0 && !string.IsNullOrWhiteSpace(_config.CharacterName))
                {
                    RequestStop($"No character named '{_config.CharacterName}' on this account.");
                    return;
                }

                string name = string.IsNullOrWhiteSpace(_config.CharacterName)
                    ? "Bot" + DateTime.Now.ToString("HHmmss")
                    : _config.CharacterName;

                Log($"No characters found. Creating '{name}' ({_config.NewCharacterClass}).");

                Enqueue(new C.NewCharacter
                {
                    CharacterName = name,
                    Class = (MirClass)(int)_config.NewCharacterClass,
                    Gender = MirGender.Male,
                    HairType = 1,
                    HairColour = Color.White,
                    ArmourColour = Color.White,
                    CheckSum = string.Empty
                });
                return;
            }

            Log($"Starting game as '{chosen.CharacterName}' (index {chosen.CharacterIndex}).");
            Enqueue(new C.StartGame { CharacterIndex = chosen.CharacterIndex });
        }

        public void Process(S.NewAccount p)
        {
            if (p.Result != NewAccountResult.Success)
            {
                RequestStop($"Account creation failed: {p.Result}");
                return;
            }

            Log("Account created. Logging in.");
            Enqueue(new C.Login
            {
                EMailAddress = _config.EMailAddress,
                Password = _config.Password,
                CheckSum = string.Empty
            });
        }

        public void Process(S.NewCharacter p)
        {
            if (p.Result != NewCharacterResult.Success)
            {
                RequestStop($"Character creation failed: {p.Result}");
                return;
            }

            Log($"Character '{p.Character.CharacterName}' created (index {p.Character.CharacterIndex}).");
            Enqueue(new C.StartGame { CharacterIndex = p.Character.CharacterIndex });
        }

        public void Process(S.StartGame p)
        {
            if (p.Result != StartGameResult.Success)
            {
                RequestStop($"StartGame failed: {p.Result}" +
                            (string.IsNullOrEmpty(p.Message) ? "" : $" ({p.Message})"));
                return;
            }

            Start = p.StartInformation;
            Stage = BotStage.InGame;

            World.ApplyStart(Start);

            // The character's own items. S.Login.Items is account storage, a different list.
            Items.Reset(Start.Items);
            Log($"  Equipped:  {Items.DescribeEquipment()}");
            Log($"  Inventory: {Items.DescribeInventory()}");

            Log("=== IN GAME ===");
            Log($"  {Start.Name}, level {Start.Level} {Start.Class} ({Start.Gender})");
            Log($"  Map index {Start.MapIndex}, location {Start.Location.X},{Start.Location.Y}, facing {Start.Direction}");
            Log($"  ObjectID {Start.ObjectID}");
        }

        /// <summary>
        /// The only signal that a logout was accepted. The server answers nothing at all when it
        /// refuses one, so without this handler Stop could never tell success from silence.
        /// </summary>
        public void Process(S.GameLogout p)
        {
            Log("Logged out cleanly.");
            LoggedOut = true;
            Stage = BotStage.Selecting;
        }

        public void Process(S.SelectLogout p)
        {
            LoggedOut = true;
        }

        public void Process(S.MapChanged p)
        {
            Log($"Map changed to index {p.MapIndex}.");
            World.ApplyMapChanged(p.MapIndex);
        }

        #endregion
    }
}
