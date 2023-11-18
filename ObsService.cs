using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Newtonsoft.Json.Linq;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
namespace OBSPlugin
{
    public sealed class ObsService : IDisposable
    {
        private readonly OBSWebsocket _obsWebsocket;
        private bool _connectionLock;
        private RecordingInformation _previousRecordingInformation;
        private Plugin Plugin { get; }
        private IPluginLog PluginLog => Plugin.PluginLog;
        
        /// Was using <see cref="OBSWebsocket.IsConnected"/>, but that sends a a blocking ping and can cause delays.
        public bool IsConnected => ConnectionStatus == ConnectionStatus.Connected;
        public ConnectionStatus ConnectionStatus { get; private set; }

        public StreamStatus StreamStatus { get; private set; }
        public OutputState StreamState { get; private set; } = OutputState.Stopped;
        public OutputState RecordState { get; private set; } = OutputState.Stopped;
        public OutputState ReplayState { get; private set; } = OutputState.Stopped;

        public event EventHandler Connected
        {
            add => this._obsWebsocket.Connected += value;
            remove => this._obsWebsocket.Connected -= value;
        }

        public ObsService(Plugin plugin)
        {
            this.Plugin = plugin;
            this._obsWebsocket = new OBSWebsocket();
            this._obsWebsocket.Connected += this.OnConnected;
            this._obsWebsocket.Disconnected += this.OnDisconnected;
            
            this._obsWebsocket.StreamStatus += (_, streamStatus) => this.StreamStatus = streamStatus;
            this._obsWebsocket.RecordingStateChanged += (_, recordStatus) => this.RecordState = recordStatus;
            this._obsWebsocket.StreamingStateChanged += (_, streamStatus) => this.StreamState = streamStatus;
            this._obsWebsocket.ReplayBufferStateChanged += (_, replayStatus) => this.ReplayState = replayStatus;
        }

        public async void TryConnectAsync(string url, string password)
        {
            if(!this.IsConnected)
            {
                this.ConnectionStatus = ConnectionStatus.Connecting;
                
                if (this._connectionLock)
                {
                    return;
                }
                // Exceptions don't propagate out of an async void Task, so the exception handlers need to be part of the task.
                // This should probably be changed to a Task<bool> and raise events in the future. But I've been limiting the
                // amount of major implementation choices due to more reworks coming.
                await Task.Run(delegate
                {
                    try
                    {
                        this._connectionLock = true;
                        this._obsWebsocket.Connect(url, password);
                    }
                    catch (AuthFailureException)
                    {
                        this.PluginLog.Error("Failed to connect to OBS: Authentication failure");
                        this._obsWebsocket.Disconnect();
                    }
                    catch (ArgumentException e)
                    {
                        this.PluginLog.Error("Failed to connect to OBS: {0}", e.Message);
                    }
                    catch (Exception e)
                    {
                        this.Plugin.PluginLog.Error(e, "Connection error: {0}", e.Message);
                    }
                    finally
                    {
                        if (!this.IsConnected)
                            this.ConnectionStatus = ConnectionStatus.Failed;
                        this._connectionLock = false;
                    }
                });
            }
            else
            {
                PluginLog.Information("Cannot try connecting. Already connected.");
            }
        }

        public bool TryRemoveFilterFromSource(string sourceName, string filterName)
        {
            if (!IsConnected)
                return false;
            bool removed;
            try
            {
                removed = this._obsWebsocket.RemoveFilterFromSource(sourceName, filterName);
                Plugin.PluginLog.Debug("Deleted blur: {0}", filterName);
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Error("Failed deleting blur: {0}", e);
                return false;
            }
            return removed;
        }

        public FilterSettings GetSourceFilterInfo(string sourceName, string filterName)
        {
            return this._obsWebsocket.GetSourceFilterInfo(sourceName, filterName);
        }

        public void AddFilterToSource(string sourceName, string filterName, string filterType, JObject settings)
        {
            this._obsWebsocket.AddFilterToSource(sourceName, filterName, filterType, settings);
        }

        public void SetSourceFilterSettings(string sourceName, string filterName, JObject settings)
        {
            this._obsWebsocket.SetSourceFilterSettings(sourceName, filterName, settings);
        }

        public void SetSourceFilterVisibility(string sourceName, string filterName, bool visibility)
        {
            this._obsWebsocket.SetSourceFilterVisibility(sourceName, filterName, visibility);
        }

        public bool RemoveFiltersWithPrefixFromSource(string sourceName, string prefix)
        {
            if (!IsConnected) return false;

            try
            {
                IEnumerable<FilterSettings> filtersToRemove = from filters in this._obsWebsocket.GetSourceFilters(sourceName)
                                                              where filters.Name.StartsWith(prefix)
                                                              select filters;

                foreach (var filter in filtersToRemove)
                {
                    TryRemoveFilterFromSource(sourceName, filter.Name);
                }
                PluginLog.Debug("Deleted all blurs start with {0}", prefix);
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Error("Failed deleting blurs: {0}", e);
                return false;
            }
            return true;
        }

        public void TryDisconnect()
        {
            if (this.IsConnected)
            {
                this.RestoreRecordingLocation();
                this._obsWebsocket.Disconnect();
                this.ConnectionStatus = ConnectionStatus.Disconnected;
            }
        }

        public void SetRecordingLocation(RecordingInformation information)
        {
            if (this.IsConnected && this.RecordState == OutputState.Stopped)
            {
                if (!information.Directory.IsNullOrWhitespace())
                {
                    this._obsWebsocket.SetRecordingFolder(information.Directory);
                }
                if (!information.FilenameFormat.IsNullOrWhitespace())
                {
                    this._obsWebsocket.SetFilenameFormatting(information.FilenameFormat);
                }
            }
        }

        public RecordingInformation GetRecordingLocation()
        {
            if (this.IsConnected)
            {
                return new RecordingInformation(this._obsWebsocket.GetRecordingFolder(), this._obsWebsocket.GetFilenameFormatting());
            }
            return RecordingInformation.None;
        }

        public bool TryToggleStreaming()
        {
            if (!this.IsConnected) return false;
            try
            {
                this._obsWebsocket.ToggleStreaming();
                return true;
            }
            catch (Exception e)
            {
                this.Plugin.PluginLog.Error("Error on toggle streaming: {0}", e);
                this.Plugin.Chat.PrintError("[OBSPlugin] Error on toggle streaming, check log for details.");
            }
            return false;
        }

        public bool TryToggleRecording()
        {
            try
            {
                if (this.IsConnected)
                {
                    this._obsWebsocket.ToggleRecording();
                    return true;
                }
            }
            catch (Exception e)
            {
                this.Plugin.PluginLog.Error("Error on toggle recording: {0}", e);
                this.Plugin.Chat.PrintError("[OBSPlugin] Error on toggle recording, check log for details.");
            }
            return false;
        }

        public bool TryStartRecording()
        {
            try
            {
                if (this.IsConnected && this.RecordState == OutputState.Stopped)
                {
                    this._obsWebsocket.StartRecording();
                    return true;
                }
            }
            catch (Exception e)
            {
                this.Plugin.PluginLog.Error("Error on start recording: {0}", e);
                this.Plugin.Chat.PrintError("[OBSPlugin] Error on start recording, check log for details.");
            }
            return false;
        }

        public bool TryStopRecording()
        {
            try
            {
                if (this.IsConnected && this.RecordState == OutputState.Started)
                {
                    this._obsWebsocket.StopRecording();
                    return true;
                }
            }
            catch (Exception e)
            {
                this.Plugin.PluginLog.Error("Error on stop recording: {0}", e);
                this.Plugin.Chat.PrintError("[OBSPlugin] Error on stop recording, check log for details.");
            }
            return false;
        }

        public bool TryToggleReplayBuffer()
        {
            try
            {
                if (this.IsConnected)
                {
                    this._obsWebsocket.ToggleReplayBuffer();
                    return true;
                }
            }
            catch (Exception e)
            {
                this.Plugin.PluginLog.Error("Error on toggle replay buffer: {0}", e);
                this.Plugin.Chat.PrintError("[OBSPlugin] Error on toggle replay buffer, check log for details.");
            }
            return false;
        }
        
        public bool TryStartReplayBuffer()
        {
            try
            {
                if (this.IsConnected && this.ReplayState == OutputState.Stopped)
                {
                    this._obsWebsocket.StartReplayBuffer();
                    return true;
                }
            }
            catch (Exception e)
            {
                this.Plugin.PluginLog.Error("Error on start replay buffer: {0}", e);
                this.Plugin.Chat.PrintError("[OBSPlugin] Error on start replay buffer, check log for details.");
            }
            return false;
        }
        
        public bool TryStopReplayBuffer()
        {
            try
            {
                if (this.IsConnected && this.ReplayState == OutputState.Started)
                {
                    this._obsWebsocket.StopReplayBuffer();
                    return true;
                }
            }
            catch (Exception e)
            {
                this.Plugin.PluginLog.Error("Error on stop replay buffer: {0}", e);
                this.Plugin.Chat.PrintError("[OBSPlugin] Error on stop replay buffer, check log for details.");
            }
            return false;
        }
        
        public bool TrySaveReplayBuffer()
        {
            try
            {
                if (this.IsConnected && this.ReplayState == OutputState.Started)
                {
                    this._obsWebsocket.SaveReplayBuffer();
                    return true;
                }
            }
            catch (Exception e)
            {
                this.Plugin.PluginLog.Error("Error on save replay buffer: {0}", e);
                this.Plugin.Chat.PrintError("[OBSPlugin] Error on save replay buffer, check log for details.");
            }
            return false;
        }

        private void OnConnected(object sender, EventArgs e)
        {
            this.ConnectionStatus = ConnectionStatus.Connected;
            this.Plugin.PluginLog.Information("OBS connected");
            this._previousRecordingInformation = this.GetRecordingLocation();
        }

        private void OnDisconnected(object sender, EventArgs e)
        {
            this.Plugin.PluginLog.Information("OBS disconnected");
        }

        private void RestoreRecordingLocation()
        {
            this.SetRecordingLocation(this._previousRecordingInformation);
        }

        public void Dispose()
        {
            this.TryDisconnect();
            this._obsWebsocket.Connected -= this.OnConnected;
            this._obsWebsocket.Disconnected -= this.OnDisconnected;
        }
    }
}

public record RecordingInformation(string Directory, string FilenameFormat)
{
    public static RecordingInformation None { get; } = new("", "");
}

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}
