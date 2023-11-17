﻿using Dalamud.Game;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using OBSPlugin.Attributes;
using OBSPlugin.Objects;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Types;
using System;
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
        internal IPluginLog PluginLog { get; init; }

        internal readonly PluginCommandManager<Plugin> commandManager;
        internal Configuration config { get; private set; }
        internal readonly PluginUI ui;

        internal OBSWebsocket obs;
        internal bool Connected = false;
        internal bool ConnectionFailed = false;
        internal StreamStatus streamStatus;
        internal OutputState obsStreamStatus = OutputState.Stopped;
        internal OutputState obsRecordStatus = OutputState.Stopped;
        internal readonly StopWatchHook stopWatchHook;
        internal CombatState state;
        internal float lastCountdownValue;

        private bool _connectLock;
        private CancellationTokenSource _cts = new();
        private bool _stoppingRecord = false;

        public string Name => "OBS Plugin";

        public Plugin()
        {
            obs = new OBSWebsocket();
            obs.Connected += onConnect;
            obs.Disconnected += onDisconnect;
            obs.StreamStatus += onStreamData;
            obs.StreamingStateChanged += onStreamingStateChange;
            obs.RecordingStateChanged += onRecordingStateChange;

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
            state.InCombatChanged += new EventHandler((Object sender, EventArgs e) =>
            {
                if (!Connected)
                {
                    TryConnect(config.Address, config.Password);
                    if (!Connected) return;
                }
                if (this.state.InCombat && config.StartRecordOnCombat)
                {
                    try
                    {
                        if (config.CancelStopRecordOnResume && _stoppingRecord)
                        {
                            _cts.Cancel();
                        }
                        else
                        {
                            PluginLog.Information("Auto start recroding");
                            this.ui.SetRecordingDir();
                            this.obs.StartRecording();
                        }
                    }
                    catch (ErrorResponseException err)
                    {
                        PluginLog.Warning("Start Recording Error: {0}", err);
                    }
                }
                else if (!this.state.InCombat && config.StopRecordOnCombat)
                {
                    new Task(() =>
                    {
                        try
                        {
                            _stoppingRecord = true;
                            var delay = config.StopRecordOnCombatDelay;
                            do
                            {
                                _cts.Token.ThrowIfCancellationRequested();
                                Thread.Sleep(1000);
                                delay -= 1;
                            } while (delay > 0 || (config.DontStopInCutscene && (this.ClientState.LocalPlayer.OnlineStatus.Id == 15)));
                            PluginLog.Information("Auto stop recroding");
                            this.ui.SetRecordingDir();
                            this.obs.StopRecording();
                        }
                        catch (ErrorResponseException err)
                        {
                            PluginLog.Warning("Stop Recording Error: {0}", err);
                        }
                        finally
                        {
                            _stoppingRecord = false;
                            _cts.Dispose();
                            _cts = new();
                        }
                    }, _cts.Token).Start();
                }
            });
            state.CountingDownChanged += new EventHandler((Object sender, EventArgs e) =>
            {
                if (!Connected)
                {
                    TryConnect(config.Address, config.Password);
                    return;
                }
                if (this.state.CountDownValue > lastCountdownValue && config.StartRecordOnCountDown)
                {
                    try
                    {
                        PluginLog.Information("Auto start recroding");
                        this.ui.SetRecordingDir();
                        this.obs.StartRecording();
                    }
                    catch (ErrorResponseException err)
                    {
                        PluginLog.Warning("Start Recording Error: {0}", err);
                    }
                }
                lastCountdownValue = this.state.CountDownValue;
            });
            this.stopWatchHook = new StopWatchHook(PluginInterface, state, SigScanner, Condition, GameInteropProvider);

            PluginLog.Information("stopWatchHook");
            this.commandManager = new PluginCommandManager<Plugin>(this, Commands);

            if (config.Password.Length > 0)
            {
                TryConnect(config.Address, config.Password);
            }
        }


        private void OpenConfigUi()
        {
            this.ui.IsVisible = true;
        }

        public async void TryConnect(string url, string password)
        {
            if (_connectLock)
            {
                return;
            }
            try
            {
                _connectLock = true;
                await Task.Run(() => obs.Connect(url, password));
                ConnectionFailed = false;
            }
            catch (AuthFailureException)
            {
                _ = Task.Run(() => obs.Disconnect());
                ConnectionFailed = true;
            }
            catch (Exception e)
            {
                PluginLog.Error("Connection error {0}", e);
            }
            finally
            {
                _connectLock = false;
            }
        }
        private void onConnect(object sender, EventArgs e)
        {
            Connected = true;
            PluginLog.Information("OBS connected: {0}", config.Address);
            var streamStatus = obs.GetStreamingStatus();
            if (streamStatus.IsStreaming)
                onStreamingStateChange(obs, OutputState.Started);
            else
                onStreamingStateChange(obs, OutputState.Stopped);
            if (streamStatus.IsRecording)
                onRecordingStateChange(obs, OutputState.Started);
            else
                onRecordingStateChange(obs, OutputState.Stopped);
            if (config.RecordDir.Equals(String.Empty))
            {
                var recordDir = obs.GetRecordingFolder();
                config.RecordDir = recordDir;
                config.FilenameFormat = obs.GetFilenameFormatting();
                config.Save();
            }
        }
        private void onDisconnect(object sender, EventArgs e)
        {
            PluginLog.Information("OBS disconnected: {0}", config.Address);
            Connected = false;
        }

        private void onStreamData(OBSWebsocket sender, StreamStatus data)
        {
            streamStatus = data;
        }

        private void onStreamingStateChange(OBSWebsocket sender, OutputState newState)
        {
            obsStreamStatus = newState;
        }

        private void onRecordingStateChange(OBSWebsocket sender, OutputState newState)
        {
            obsRecordStatus = newState;
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

            this.ui.Dispose();

            if (obs != null && this.Connected)
            {
                if (config.RecordDir.Length > 0)
                    obs.SetRecordingFolder(config.RecordDir);
                obs.Disconnect();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
