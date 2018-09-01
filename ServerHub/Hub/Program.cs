﻿using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using ServerHub.Misc;
using ServerHub.Rooms;
using System.IO;
using System.Collections.Generic;
using NetTools;
using ServerHub.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#if DEBUG
using System.IO.Pipes;
using System.Diagnostics;
#endif

namespace ServerHub.Hub
{
    class Program {
        private static string BeatSaverURL = "https://beatsaver.com";
        private static string IP { get; set; }

        public static List<IPAddressRange> blacklistedIPs;
        public static List<ulong> blacklistedIDs;
        public static List<string> blacklistedNames;
        
        public static List<IPAddressRange> whitelistedIPs;
        public static List<ulong> whitelistedIDs;
        public static List<string> whitelistedNames;

        static void Main(string[] args) => new Program().Start(args);
        
        private Thread listenerThread { get; set; }

#if !DEBUG
        private void HandleUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Instance.Exception(e.ExceptionObject.ToString());
        }
#endif

        private void OnShutdown(ShutdownEventArgs obj) {
            HubListener.Stop();
        }

        void Start(string[] args) {
#if !DEBUG
            AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
#endif
            UpdateLists();

            if (args.Length > 0)
            {
                if (args[0].StartsWith("--"))
                {
                    string comName = args[0].ToLower().TrimStart('-');
                    string[] comArgs = args.Skip(1).ToArray();
                    Logger.Instance.Log(ProcessCommand(comName, comArgs), true);
                }
                else
                {

                }
            }

            ShutdownEventCatcher.Shutdown += OnShutdown;

            Settings.Instance.Server.Tickrate = Misc.Math.Clamp(Settings.Instance.Server.Tickrate, 5, 150);
            HighResolutionTimer.LoopTimer.Interval = 1000/Settings.Instance.Server.Tickrate;
            HighResolutionTimer.LoopTimer.Start();
#if DEBUG
            InitializePipe();
            HighResolutionTimer.LoopTimer.Elapsed += LoopTimer_Elapsed;
#endif

            IP = GetPublicIPv4();

            Logger.Instance.Log($"Beat Saber Multiplayer ServerHub v{Assembly.GetEntryAssembly().GetName().Version}");

            VersionChecker.CheckForUpdates();

            Logger.Instance.Log($"Hosting ServerHub @ {IP}:{Settings.Instance.Server.Port}");

            HubListener.Start();

            if (Settings.Instance.TournamentMode.Enabled)
                CreateTournamentRooms();

            Logger.Instance.Warning($"Use [Help] to display commands");
            Logger.Instance.Warning($"Use [Quit] to exit");
            
            while (HubListener.Listen)
            {
                var x = Console.ReadLine();
                if (x == string.Empty) continue;

                var parsedArgs = ParseLine(x);

                Logger.Instance.Log(ProcessCommand(parsedArgs[0], parsedArgs.Skip(1).ToArray()));
            }
        }

#if DEBUG
        DateTime lastTick;

        NamedPipeServerStream pipeServer;
        StreamWriter pipeWriter;

        private async void InitializePipe()
        {
            if (pipeServer != null)
            {
                pipeServer.Close();
            }
            pipeServer = new NamedPipeServerStream("ServerHubLoopPipe", PipeDirection.Out);
            await pipeServer.WaitForConnectionAsync();

            pipeWriter = new StreamWriter(pipeServer);
            pipeWriter.AutoFlush = true;
            lastTick = DateTime.Now;

        }

        private void LoopTimer_Elapsed(object sender, HighResolutionTimerElapsedEventArgs e)
        {
            if (pipeServer.IsConnected)
            {
                try
                {
                    float milliseconds = (DateTime.Now.Subtract(lastTick).Ticks) / TimeSpan.TicksPerMillisecond;
                    lastTick = DateTime.Now;
                    List<byte> buffer = new List<byte>();
                    buffer.AddRange(BitConverter.GetBytes(milliseconds));
                    buffer.AddRange(BitConverter.GetBytes((float)e.Delay));
                    pipeWriter.BaseStream.Write(buffer.ToArray(), 0, buffer.Count);
                }catch(Exception)
                {
                    pipeServer.Dispose();

                    InitializePipe();
                }
            }
        }
#endif
        private void CreateTournamentRooms()
        {
            for (int i = 0; i < Settings.Instance.TournamentMode.Rooms; i++)
            {
                List<SongInfo> songs = Settings.Instance.TournamentMode.SongHashes.ConvertAll(x => new SongInfo() { levelId = x.ToUpper(), songName = "" });

                RoomSettings settings = new RoomSettings()
                {
                    Name = $"Tournament Room {i + 1}",
                    UsePassword = false,
                    Password = "",
                    NoFail = true,
                    MaxPlayers = 0,
                    SelectionType = Data.SongSelectionType.Manual,
                    AvailableSongs = songs,
                };

                PlayerInfo host = new PlayerInfo("server", 76561198047255564);
                uint id = RoomsController.CreateRoom(settings, host);

                Logger.Instance.Log("Created tournament room with ID " + id);
            }
        }

        string ProcessCommand(string comName, string[] comArgs)
        {
            switch (comName.ToLower())
            {
                case "help":
                    {
                        string commands = "";
                        foreach (var com in new[] {
                        "help",
                        "version",
                        "quit",
                        "clients",
                        "blacklist [add/remove] [nick/playerID/IP]",
                        "whitelist [enable/disable/add/remove] [nick/playerID/IP]",
                        "tickrate [5-150]",
                        //"createroom",
                        "cloneroom [roomId]",
                        "destroyroom [roomId]" })
                        {
                            commands += $"{Environment.NewLine}> {com}";
                        }
                        return $"Commands:{commands}";
                    }
                case "version":
                    return $"{Assembly.GetEntryAssembly().GetName().Version}";
                case "quit":
                    Environment.Exit(0);
                    return "";
                case "clients":
                    string clientsStr = "";
                    if (HubListener.Listen)
                    {
                        clientsStr += $"{Environment.NewLine}┌─Lobby:";
                        if (HubListener.hubClients.Where(x => x.socket != null && x.socket.Connected).Count() == 0)
                        {
                            clientsStr += $"{Environment.NewLine}│ No Clients";
                        }
                        else
                        {
                            List<Client> clients = new List<Client>(HubListener.hubClients.Where(x => x.socket != null && x.socket.Connected));
                            foreach (var client in clients)
                            {
                                IPEndPoint remote = (IPEndPoint)client.socket.RemoteEndPoint;
                                clientsStr += $"{Environment.NewLine}│ [{client.playerInfo.playerState}] {client.playerInfo.playerName}({client.playerInfo.playerId}) @ {remote.Address}:{remote.Port}";
                            }
                        }

                        if (RoomsController.GetRoomsList().Count > 0)
                        {
                            foreach (var room in RoomsController.GetRoomsList())
                            {
                                clientsStr += $"{Environment.NewLine}├─Room {room.roomId} \"{room.roomSettings.Name}\":";
                                if (room.roomClients.Count == 0)
                                {
                                    clientsStr += $"{Environment.NewLine}│ No Clients";
                                }
                                else
                                {
                                    List<Client> clients = new List<Client>(room.roomClients);
                                    foreach (var client in clients)
                                    {
                                        IPEndPoint remote = (IPEndPoint)client.socket.RemoteEndPoint;
                                        clientsStr += $"{Environment.NewLine}│ [{client.playerInfo.playerState}] {client.playerInfo.playerName}({client.playerInfo.playerId}) @ {remote.Address}:{remote.Port}";
                                    }
                                }
                            }
                        }

                        clientsStr += $"{Environment.NewLine}└";
                        
                        return clientsStr;

                    }
                    break;
                case "blacklist":
                    {
                        if (comArgs.Length == 2 && !string.IsNullOrEmpty(comArgs[1]))
                        {
                            switch (comArgs[0])
                            {
                                case "add":
                                    {
                                        if (!Settings.Instance.Access.Blacklist.Contains(comArgs[1]))
                                        {
                                            Settings.Instance.Access.Blacklist.Add(comArgs[1]);
                                            Settings.Instance.Save();
                                            UpdateLists();

                                            return $"Successfully banned {comArgs[1]}";
                                        }
                                        else
                                        {
                                            return $"{comArgs[1]} is already blacklisted";
                                        }
                                    }
                                case "remove":
                                    {
                                        if (Settings.Instance.Access.Blacklist.Remove(comArgs[1]))
                                        {
                                            Settings.Instance.Save();
                                            UpdateLists();
                                            return $"Successfully unbanned {comArgs[1]}";
                                        }
                                        else
                                        {
                                            return $"{comArgs[1]} is not banned";
                                        }
                                    }
                                default:
                                    {
                                        return $"Command usage: blacklist [add/remove] [nick/playerID/IP]";
                                    }
                            }
                        }
                        else
                        {
                            return $"Command usage: blacklist [add/remove] [nick/playerID/IP]";
                        }
                    }
                case "whitelist":
                    {
                        if (comArgs.Length >= 1)
                        {
                            switch (comArgs[0])
                            {
                                case "enable":
                                    {
                                        Settings.Instance.Access.WhitelistEnabled = true;
                                        Settings.Instance.Save();
                                        UpdateLists();
                                        return $"Whitelist enabled";
                                    }
                                case "disable":
                                    {
                                        Settings.Instance.Access.WhitelistEnabled = false;
                                        Settings.Instance.Save();
                                        UpdateLists();
                                        return $"Whitelist disabled";
                                    }
                                case "add":
                                    {
                                        if (comArgs.Length == 2 && !string.IsNullOrEmpty(comArgs[1]))
                                        {
                                            if (!Settings.Instance.Access.Whitelist.Contains(comArgs[1]))
                                            {
                                                Settings.Instance.Access.Whitelist.Add(comArgs[1]);
                                                Settings.Instance.Save();
                                                UpdateLists();
                                                return $"Successfully whitelisted {comArgs[1]}";
                                            }
                                            else
                                            {
                                                return $"{comArgs[1]} is already whitelisted";
                                            }
                                        }
                                        else
                                        {
                                            return $"Command usage: whitelist [enable/disable/add/remove] [nick/playerID/IP]";
                                        }
                                    }
                                case "remove":
                                    {
                                        if (comArgs.Length == 2 && !string.IsNullOrEmpty(comArgs[1]))
                                        {
                                            if (Settings.Instance.Access.Whitelist.Remove(comArgs[1]))
                                            {
                                                Settings.Instance.Save();
                                                UpdateLists();
                                                return $"Successfully removed {comArgs[1]} from whitelist";
                                            }
                                            else
                                            {
                                                return $"{comArgs[1]} is not whitelisted";
                                            }
                                        }
                                        else
                                        {
                                            return $"Command usage: whitelist [enable/disable/add/remove] [nick/playerID/IP]";
                                        }
                                    }
                                default:
                                    {
                                        return $"Command usage: whitelist [enable/disable/add/remove] [nick/playerID/IP]";
                                    }
                            }
                        }
                        else
                        {
                            return $"Command usage: whitelist [enable/disable/add/remove] [nick/playerID/IP]";
                        }
                    }
                case "tickrate":
                    {
                        int tickrate = 30;
                        if (int.TryParse(comArgs[0], out tickrate))
                        {
#if !DEBUG
                            tickrate = Misc.Math.Clamp(tickrate, 5, 150);
#endif
                            Settings.Instance.Server.Tickrate = tickrate;
                            HighResolutionTimer.LoopTimer.Interval = 1000f / tickrate;

                            return $"Set tickrate to {Settings.Instance.Server.Tickrate}";
                        }
                        else
                        {
                            return $"Command usage: tickrate [5-150]";
                        }
                    }
                    /*
                case "createroom":
                    {
                        if (!exitAfterPrint)
                        {
                            Logger.Instance.Log("Creating new room:");

                            Logger.Instance.Log("Host Nickname:");
                            string roomHostName = Console.ReadLine();
                            if (string.IsNullOrEmpty(roomHostName))
                            {
                                Logger.Instance.Error($"Room host nickname can't be empty!");
                                return;
                            }

                            Logger.Instance.Log("Host SteamID/OculusID:");
                            ulong roomHostID;
                            string buffer = Console.ReadLine();
                            if (!ulong.TryParse(buffer, out roomHostID))
                            {
                                Logger.Instance.Error($"Can't parse \"{buffer}\"!");
                                return;
                            }

                            PlayerInfo roomHost = new PlayerInfo(roomHostName, roomHostID);

                            Logger.Instance.Log("Name:");
                            string roomName = Console.ReadLine();
                            if (string.IsNullOrEmpty(roomName))
                            {
                                Logger.Instance.Error($"Room name can't be empty!");
                                return;
                            }

                            Logger.Instance.Log("Password (blank = no password): ");
                            string roomPass = Console.ReadLine();
                            bool usePassword = !string.IsNullOrEmpty(roomPass);

                            Logger.Instance.Log("Song selection type (Manual, Random, Voting):");
                            SongSelectionType roomSongSelectionType;
                            buffer = Console.ReadLine();
                            if (!Enum.TryParse(buffer, true, out roomSongSelectionType))
                            {
                                Logger.Instance.Error($"Can't parse \"{buffer}\"!");
                                return;
                            }

                            Logger.Instance.Log("Max players:");
                            int roomMaxPlayers;
                            buffer = Console.ReadLine();
                            if (!int.TryParse(buffer, out roomMaxPlayers))
                            {
                                Logger.Instance.Error($"Can't parse \"{buffer}\"!");
                                return;
                            }

                            Logger.Instance.Log("No Fail mode (true, false):");
                            bool roomNoFail;
                            buffer = Console.ReadLine();
                            if (!bool.TryParse(buffer, out roomNoFail))
                            {
                                Logger.Instance.Error($"Can't parse \"{buffer}\"!");
                                return;
                            }

                            Logger.Instance.Log("Songs (BeatDrop Playlist Path):");

                            buffer = Console.ReadLine();
                            if (!buffer.EndsWith(".json"))
                                buffer += ".json";

                            if (File.Exists(buffer))
                            {
                                Logger.Instance.Log("Loading playlist...");
                                string playlistString = File.ReadAllText(buffer);
                                Playlist playlist = JsonConvert.DeserializeObject<Playlist>(playlistString);

                                Logger.Instance.Log($"Loaded playlist: \"{playlist.playlistTitle}\" by {playlist.playlistAuthor}:");
                                Logger.Instance.Log($"{playlist.songs.Count} songs");

                                List<SongInfo> songs = new List<SongInfo>();

                                using (WebClient client = new WebClient())
                                {
                                    client.Headers.Add("user-agent", $"BeatSaberMultiplayerServer-{Assembly.GetEntryAssembly().GetName().Version}");

                                    foreach (PlaylistSong song in playlist.songs)
                                    {
                                        Logger.Instance.Log($"Trying to get levelId for {song.key} \"{song.songName}\"");
                                        try
                                        {
                                            string response = client.DownloadString(BeatSaverURL + "/api/songs/detail/" + song.key);

                                            JObject parsedObject = JObject.Parse(response);
                                            songs.Add(new SongInfo() { levelId = parsedObject["song"]["hashMd5"].ToString().ToUpper(), songName = song.songName, songDuration = 0f });
                                        }
                                        catch
                                        {
                                            Logger.Instance.Exception($"Can't get info for song \"{song.songName}\"!");
                                        }
                                    }
                                }
                                Logger.Instance.Log($"Creating room...");

                                RoomSettings settings = new RoomSettings() { Name = roomName, UsePassword = usePassword, Password = roomPass, SelectionType = roomSongSelectionType, NoFail = roomNoFail, MaxPlayers = roomMaxPlayers, AvailableSongs = songs };
                                uint roomId = RoomsController.CreateRoom(settings, roomHost);

                                Logger.Instance.Log($"Done! New room ID is " + roomId);

                            }
                            else
                            {
                                Logger.Instance.Error($"File \"{buffer}\" doesn't exists!");
                            }
                        }
                    }
                    break;
                    */
                case "cloneroom":
                    {
                        if (HubListener.Listen)
                        {
                            if (comArgs.Length == 1)
                            {
                                uint roomId;
                                if (uint.TryParse(comArgs[0], out roomId))
                                {
                                    Room room = RoomsController.GetRoomsList().FirstOrDefault(x => x.roomId == roomId);
                                    if (room != null)
                                    {
                                        uint newRoomId = RoomsController.CreateRoom(room.roomSettings, room.roomHost);
                                        return "Cloned room roomId is " + newRoomId;
                                    }
                                    else
                                    {
                                        return "Room with ID " + roomId + " not found!";
                                    }
                                }
                                else
                                {
                                    return $"Command usage: cloneroom [roomId]";
                                }
                            }
                            else
                            {
                                return $"Command usage: cloneroom [roomId]";
                            }
                        }
                    }
                    break;
                case "destroyroom":
                    {
                        if (HubListener.Listen)
                        {
                            if (comArgs.Length == 1)
                            {
                                uint roomId;
                                if (uint.TryParse(comArgs[0], out roomId))
                                {
                                    if (RoomsController.DestroyRoom(roomId))
                                        return "Destroyed room " + roomId;
                                    else
                                        return "Room with ID " + roomId + " not found!";
                                }
                                else
                                {
                                    return $"Command usage: destroyroom [roomId]";
                                }
                            }
                            else
                            {
                                return $"Command usage: destroyroom [roomId]";
                            }
                        }
                    }
                    break;
#if DEBUG
                case "testroom":
                    {
                        uint id = RoomsController.CreateRoom(new RoomSettings() { Name = "Debug Server", UsePassword = true, Password = "test", NoFail = true, MaxPlayers = 4, SelectionType = Data.SongSelectionType.Manual, AvailableSongs = new System.Collections.Generic.List<Data.SongInfo>() { new Data.SongInfo() { levelId = "07da4b5bc7795b08b87888b035760db7".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "451ffd065cf0e6adc01b2c3eda375794".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "97b35d13bac139c089a0c9d9306c9d76".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "a0d040d1a4fe833d5f9838d35777d302".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "61e3b11c1a4cd9185db46b1f7bb7ea54".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "e2d35a81fc0c54c326b09892c8d5c038".ToUpper(), songName = "" } } }, new Data.PlayerInfo("andruzzzhka", 76561198047255564));
                        return "Created room with ID " + id;
                    }
                case "testroomwopass":
                    {
                        uint id = RoomsController.CreateRoom(new RoomSettings() { Name = "Debug Server", UsePassword = false, Password = "test", NoFail = false, MaxPlayers = 4, SelectionType = Data.SongSelectionType.Manual, AvailableSongs = new System.Collections.Generic.List<Data.SongInfo>() { new Data.SongInfo() { levelId = "07da4b5bc7795b08b87888b035760db7".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "451ffd065cf0e6adc01b2c3eda375794".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "97b35d13bac139c089a0c9d9306c9d76".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "a0d040d1a4fe833d5f9838d35777d302".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "61e3b11c1a4cd9185db46b1f7bb7ea54".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "e2d35a81fc0c54c326b09892c8d5c038".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "a8f8f95869b90a288a9ce4bdc260fa17".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "7dce2ba59bc69ec59e6ac455b98f3761".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "fbd77e71ce31329e5ebacde40c7401e0".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "7014f67926d216a6e2df026fa67017b0".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "51d0e56ecea0a98637c0323e7a3af7cf".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "9d1e4315971f6644ac94babdbd20e36a".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "9812c675def22f7405e0bf3422134756".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "1d46797ccb24acb86d0403828533df61".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "6ffccb03d75106c5911dd876dfd5f054".ToUpper(), songName = "" }, new Data.SongInfo() { levelId = "e3a97c826fab2ce5993dc2e71443b9aa".ToUpper(), songName = "" } } }, new Data.PlayerInfo("andruzzzhka", 76561198047255564));
                        return "Created room with ID " + id;
                    }
#endif
            }

            if (!string.IsNullOrEmpty(comName))
            {
                return $"{comName}: command not found";
            }
            else
            {
                return $"command not found";
            }
        }

        /// <summary>
        ///     Returns arguments parsed from line.
        ///     https://github.com/Subtixx/source-rcon-library/blob/master/RCONServerLib/Utils/ArgumentParser.cs
        /// </summary>
        /// <remarks>
        ///     Matches words and multiple words in quotation.
        /// </remarks>
        /// <example>
        ///     arg0 arg1 arg2 -- 3 args: "arg0", "arg1", and "arg2"
        ///     arg0 arg1 "arg2 arg3" -- 3 args: "arg0", "arg1", and "arg2 arg3"
        /// </example>
        public static IList<string> ParseLine(string line)
        {
            var args = new List<string>();
            var quote = false;
            for (int i = 0, n = 0; i <= line.Length; ++i)
            {
                if ((i == line.Length || line[i] == ' ') && !quote)
                {
                    if (i - n > 0)
                        args.Add(line.Substring(n, i - n).Trim(' ', '"'));

                    n = i + 1;
                    continue;
                }

                if (line[i] == '"')
                    quote = !quote;
            }

            return args;
        }

        private void UpdateLists()
        {
            IPAddressRange tryIp;
            ulong tryId;

            blacklistedIPs = Settings.Instance.Access.Blacklist.Where(x => IPAddressRange.TryParse(x, out tryIp)).Select(y => IPAddressRange.Parse(y)).ToList();
            blacklistedIDs = Settings.Instance.Access.Blacklist.Where(x => ulong.TryParse(x, out tryId)).Select(y => ulong.Parse(y)).ToList();
            blacklistedNames = Settings.Instance.Access.Blacklist.Where(x => !IPAddressRange.TryParse(x, out tryIp) && !ulong.TryParse(x, out tryId)).ToList();

            whitelistedIPs = Settings.Instance.Access.Whitelist.Where(x => IPAddressRange.TryParse(x, out tryIp)).Select(y => IPAddressRange.Parse(y)).ToList();
            whitelistedIDs = Settings.Instance.Access.Whitelist.Where(x => ulong.TryParse(x, out tryId)).Select(y => ulong.Parse(y)).ToList();
            whitelistedNames = Settings.Instance.Access.Whitelist.Where(x => !IPAddressRange.TryParse(x, out tryIp) && !ulong.TryParse(x, out tryId)).ToList();

            List<Client> clientsToKick = new List<Client>();
            clientsToKick.AddRange(HubListener.hubClients);
            foreach (var room in RoomsController.GetRoomsList())
            {
                clientsToKick.AddRange(room.roomClients);
            }

            foreach (var client in clientsToKick)
            {
                if (Settings.Instance.Access.WhitelistEnabled && !client.IsWhitelisted())
                {
                    client.KickClient("You are not whitelisted!");
                }
                if (client.IsBlacklisted())
                {
                    client.KickClient("You are banned!");
                }
            }
        }

        public static string GetPublicIPv4() {
            using (var client = new WebClient()) {
                return client.DownloadString("https://api.ipify.org");
            }
        }
    }
}