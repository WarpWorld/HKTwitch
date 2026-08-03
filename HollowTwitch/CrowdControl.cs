using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ConnectorLib.JSON;
using CrowdControl;
using HollowTwitch.Clients;
using HollowTwitch.Commands;
using HollowTwitch.Entities;
using HollowTwitch.Entities.Attributes;
using HollowTwitch.Precondition;
using JetBrains.Annotations;
using Modding;
using UnityEngine;
using Camera = HollowTwitch.Commands.Camera;

namespace HollowTwitch
{
    [UsedImplicitly]
    public class CrowdControl : Mod, ITogglableMod, IGlobalSettings<Config>
    {
        private IClient _client;
        
        private Thread _currentThread;

        internal Config Config = new();

        internal CommandProcessor Processor { get; private set; }

        public static CrowdControl Instance;
        
        public void OnLoadGlobal(Config s) => Config = s;

        public Config OnSaveGlobal() => Config;

        public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects)
        {
            Instance = this;

            ObjectLoader.Load(preloadedObjects);
            ObjectLoader.LoadAssets();

            ModHooks.ApplicationQuitHook += OnQuit;
            //UnityEngine.SceneManagement.SceneManager.sceneLoaded += SceneManager_sceneLoaded;
            ReceiveCommands();
        }

        public override string GetVersion() => "1.3.0";

        public override List<(string, string)> GetPreloadNames() => ObjectLoader.ObjectList.Values.ToList();

        private void ReceiveCommands()
        {
            Processor = new CommandProcessor();

            Processor.RegisterCommands<Player>();
            Processor.RegisterCommands<Enemies>();
            Processor.RegisterCommands<Area>();
            Processor.RegisterCommands<Camera>();
            Processor.RegisterCommands<Game>();
            Processor.RegisterCommands<Meta>();

            ConfigureCooldowns();

            /*if (Config.TwitchToken is null)
            {
                Logger.Log("Token not found, relaunch the game with the fields in settings populated.");
                return;
            }*/

            _client = new CrowdControlClient(Config);

            _client.ChatMessageReceived += OnMessageReceived;
            _client.GameStateRequested += OnGameStateRequested;
            _client.MetadataRequested += OnMetadataRequested;
            _client.EffectStopRequested += OnEffectStopRequested;

            // Lets the processor push Paused/Resumed/Finished updates for timed effects.
            Processor.SendResponse = _client.Send;

            _client.ClientErrored += s => Log($"An error occured while receiving messages.\nError: {s}");

            _currentThread = new Thread(_client.StartReceive)
            {
                IsBackground = true
            };
            _currentThread.Start();

            GenerateHelpInfo();

            Log("Started receiving");
        }

        private IEnumerable<EffectResponseMetadata> OnMetadataRequested() => Processor.GetMetadata();

        private bool OnEffectStopRequested(string code, uint id) => Processor.RequestStop(code, id);

        private GameUpdate OnGameStateRequested() => Processor.GetGameState();

        private void ConfigureCooldowns()
        {
            // No cooldowns configured, let's populate the dictionary.
            if (Config.Cooldowns.Count == 0)
            {
                foreach (Command c in Processor.Commands)
                {
                    CooldownAttribute cd = c.Preconditions.OfType<CooldownAttribute>().FirstOrDefault();

                    if (cd == null)
                        continue;

                    Config.Cooldowns[c.Name] = (int) cd.Cooldown.TotalSeconds;
                }

                return;
            }

            foreach (Command c in Processor.Commands)
            {
                if (!Config.Cooldowns.TryGetValue(c.Name, out int time))
                    continue;

                CooldownAttribute cd = c.Preconditions.OfType<CooldownAttribute>().First();

                cd.Cooldown = TimeSpan.FromSeconds(time);
            }
        }

        private void SceneManager_sceneLoaded(UnityEngine.SceneManagement.Scene arg0, UnityEngine.SceneManagement.LoadSceneMode arg1)
            => Log("Scene changed: " + arg0.name);

        private void OnQuit()
        {
            _client?.Dispose();
            _currentThread?.Abort();
        }

        private (EffectStatus, Command) OnMessageReceived(string user, string message, long? duration, uint? requestId, uint quantity)
        {
            Log($"Twitch chat: [{user}: {message}]");

            string trimmed = message.Trim();
            int index = trimmed.IndexOf(Config.Prefix);

            if (index != 0) return (EffectStatus.Failure, null);

            string command = trimmed.Substring(Config.Prefix.Length).Trim();

            Logger.Log($"OnMessageReceived is calling Processor.Execute with duration " + duration);
            return Processor.Execute(user, command, duration, requestId, quantity);
        }

        private void GenerateHelpInfo()
        {
            var sb = new StringBuilder();

            sb.AppendLine("Twitch Mod Command List.\n");

            foreach (Command command in Processor.Commands)
            {
                string name = command.Name;
                sb.AppendLine($"Command: {name}");

                object[]           attributes = command.MethodInfo.GetCustomAttributes(false);
                string             args       = string.Join(" ", command.Parameters.Select(x => $"[{x.Name}]").ToArray());
                CooldownAttribute  cooldown   = attributes.OfType<CooldownAttribute>().FirstOrDefault();
                SummaryAttribute   summary    = attributes.OfType<SummaryAttribute>().FirstOrDefault();
                
                sb.AppendLine($"Usage: {Config.Prefix}{name} {args}");
                sb.AppendLine($"Cooldown: {(cooldown is null ? "This command has no cooldown" : $"{cooldown.MaxUses} use(s) per {cooldown.Cooldown}.")}");
                sb.AppendLine($"Summary:\n{(summary?.Summary ?? "No summary provided.")}\n");
            }

            File.WriteAllText(Application.dataPath + "/Managed/Mods/TwitchCommandList.txt", sb.ToString());
        }

        public void Unload() => OnQuit();
    }
}