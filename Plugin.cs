using Dalamud.Game;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Excel.GeneratedSheets;
using OBSPlugin.Attributes;
using OBSPlugin.Objects;
using OBSWebsocketDotNet;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OBSPlugin
{
    public class Plugin : IDalamudPlugin
    {
        [PluginService]
        [RequiredVersion("1.0")]
        internal DalamudPluginInterface PluginInterface { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        internal ICommandManager Commands { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        internal IChatGui Chat { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        internal IClientState ClientState { get; init; }
        [PluginService]
        [RequiredVersion("1.0")]
        internal IFramework Framework { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        internal IGameGui GameGui { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        internal ISigScanner SigScanner { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        internal ICondition Condition { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        internal IDataManager Data { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        internal IGameInteropProvider GameInteropProvider { get; init; }
        
        [PluginService]
        [RequiredVersion("1.0")]
        internal IDutyState DutyState { get; init; }

        [PluginService]
        [RequiredVersion("1.0")]
        internal IPluginLog PluginLog { get; init; }

        internal readonly PluginCommandManager<Plugin> commandManager;
        internal Configuration config { get; private set; }
        internal readonly PluginUI ui;

        internal ObsService ObsService { get; }

        public bool ConnectionFailed => ObsService.ConnectionStatus == ConnectionStatus.Failed;

        internal readonly StopWatchHook stopWatchHook;
        internal CombatState state;
        internal float lastCountdownValue;

        private CancellationTokenSource _cts = new();
        private bool _stoppingRecord = false;

        public string Name => "OBS Plugin";

        public Plugin()
        {
            ObsService = new ObsService(this);
            ObsService.Connected += OnConnect;

            this.config = (Configuration)PluginInterface.GetPluginConfig() ?? new Configuration();
            this.config.Initialize(PluginInterface);

            this.ui = new PluginUI(this);
            PluginInterface.UiBuilder.DisableCutsceneUiHide = true;
            PluginInterface.UiBuilder.DisableAutomaticUiHide = true;
            PluginInterface.UiBuilder.DisableGposeUiHide = true;
            PluginInterface.UiBuilder.DisableUserUiHide = true;
            PluginInterface.UiBuilder.Draw += this.ui.Draw;
            PluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;


            state = new CombatState();
            state.InCombatChanged += this.InCombatChanged;
            state.CountingDownChanged += this.CountingDownChanged;
            this.stopWatchHook = new StopWatchHook(PluginInterface, state, SigScanner, Condition, GameInteropProvider);

            PluginLog.Information("stopWatchHook");
            this.commandManager = new PluginCommandManager<Plugin>(this, Commands);

            if (config.Password.Length > 0)
            {
                TryConnect(config.Address, config.Password);
            }

            DutyState.DutyStarted += OnDutyStarted;
            DutyState.DutyCompleted += OnDutyCompleted;
            DutyState.DutyWiped += OnWipe;
        }
        
        private void CountingDownChanged(object sender, EventArgs e)
        {
            if (!this.ObsService.IsConnected)
            {
                this.TryConnect(this.config.Address, this.config.Password);
                return;
            }
            if (this.state.CountDownValue > this.lastCountdownValue && this.config.StartRecordOnCountDown)
            {
                try
                {
                    this.PluginLog.Information("Auto start recording");
                    this.SetRecordingInformation();
                    this.ObsService.TryStartRecording();
                }
                catch (ErrorResponseException err)
                {
                    this.PluginLog.Warning("Start Recording Error: {0}", err);
                }
            }
            this.lastCountdownValue = this.state.CountDownValue;
        }
        
        private void InCombatChanged(object sender, EventArgs e)
        {

                this.TryConnect(this.config.Address, this.config.Password);

            if (this.state.InCombat && this.config.StartRecordOnCombat)
            {
                    if (this.config.CancelStopRecordOnResume && this._stoppingRecord)
                    {
                        this._cts.Cancel();
                    }
                    else
                    {
                        this.PluginLog.Information("Auto start recording");
                        this.SetRecordingInformation();
                        this.ObsService.TryStartRecording();
                    }
            }
            else if (!this.state.InCombat && this.config.StopRecordOnCombat)
            {
                new Task(() =>
                {
                    try
                    {
                        this._stoppingRecord = true;
                        var delay = this.config.StopRecordOnCombatDelay;
                        do
                        {
                            this._cts.Token.ThrowIfCancellationRequested();
                            Thread.Sleep(1000);
                            delay -= 1;
                        } while (delay > 0 || (this.config.DontStopInCutscene && (this.ClientState.LocalPlayer?.OnlineStatus.Id == 15)));
                        this.PluginLog.Information("Auto stop recording");
                        this.SetRecordingInformation();
                        this.ObsService.TryStopRecording();
                        this.ObsService.TrySaveReplayBuffer();
                    }
                    finally
                    {
                        this._stoppingRecord = false;
                        this._cts.Dispose();
                        this._cts = new();
                    }
                }, this._cts.Token).Start();
            }
        }

        public void SetRecordingInformation()
        {
            RecordingInformation recordingInformation;
            if (ClientState is null || ClientState.TerritoryType == 0)
                recordingInformation = new RecordingInformation("", "");
            else
            {
                var territoryIdx = ClientState.TerritoryType;
                var territoryName = Data.GetExcelSheet<TerritoryType>()?.GetRow(territoryIdx)?.Map.Value?.PlaceName.Value?.Name.ToString();

                var filenameFormat = config.FilenameFormat;
                var directory = config.RecordDir;
                if (!territoryName.IsNullOrWhitespace())
                {
                    if (config.ZoneAsSuffix && !filenameFormat.IsNullOrWhitespace())
                    {
                        filenameFormat += "_" + territoryName;
                    }
                    if (config.IncludeTerritory && !directory.IsNullOrWhitespace())
                    {
                        directory = Path.Combine(directory, territoryName);
                    }
                }
                recordingInformation = new RecordingInformation(directory, filenameFormat);
            }
            ObsService.SetRecordingLocation(recordingInformation);
        }

        private void OpenConfigUi()
        {
            this.ui.IsVisible = true;
        }

        public bool TryConnect(string url, string password)
        {
            ObsService.TryConnect(url, password);
            return ObsService.ConnectionStatus == ConnectionStatus.Connected;
        }

        private void OnConnect(object sender, EventArgs e)
        {
            if (config.RecordDir.IsNullOrWhitespace())
            {
                RecordingInformation recordDir = ObsService.GetRecordingLocation();
                config.RecordDir = recordDir.Directory;
                config.FilenameFormat = recordDir.FilenameFormat;
                config.Save();
            }
        }

        private void OnDutyStarted(object sender, ushort territoryId)
        {
            if (this.config.StartReplayBufferOnDutyEntrance)
            {
                this.ObsService.TryStartReplayBuffer();
            }
        }
        
        private void OnDutyCompleted(object sender, ushort territoryId)
        {
            if (this.config.StopReplayBufferOnDutyExit)
            {
                this.ObsService.TryStopReplayBuffer();
            }
        }
        
        private void OnWipe(object sender, ushort territoryId)
        {
            if (this.config.TriggerReplayBufferOnWipe)
            {
                this.ObsService.TryStopReplayBuffer();
            }
        }
        
        [Command("/obs")]
        [HelpMessage("Open OBSPlugin config panel.")]
        public unsafe void ObsCommand(string command, string args)
        {
            // You may want to assign these references to private variables for convenience.
            // Keep in mind that the local player does not exist until after logging in.
            if (args == "" || args == "config")
            {
                this.ui.IsVisible = !this.ui.IsVisible;
            }
            else if (args == "on")
            {
                this.config.Enabled = true;
                this.config.Save();
            }
            else if (args == "off")
            {
                this.config.Enabled = false;
                this.config.Save();
            }
            else if (args == "toggle")
            {
                this.config.Enabled = !this.config.Enabled;
                this.config.Save();
            }
            else if (args == "update")
            {
                this.ui.UpdateGameUI();
            }
        }

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            this.commandManager.Dispose();

            this.stopWatchHook.Dispose();

            PluginInterface.SavePluginConfig(this.config);

            PluginInterface.UiBuilder.Draw -= this.ui.Draw;
            
            DutyState.DutyStarted -= OnDutyStarted;
            DutyState.DutyCompleted -= OnDutyCompleted;
            DutyState.DutyWiped -= OnWipe;

            this.ui.Dispose();

            ObsService.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
