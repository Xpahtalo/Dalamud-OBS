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

        public bool IsConnected => this._obsWebsocket.IsConnected;
        public ConnectionStatus ConnectionStatus { get; private set; }
        public OutputState RecordState { get; private set; } = OutputState.Stopped;
        public OutputState StreamState { get; private set; } = OutputState.Stopped;
        public StreamStatus StreamStatus { get; private set; }

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
            this._obsWebsocket.RecordingStateChanged += (_, recordStatus) => this.RecordState = recordStatus;
            this._obsWebsocket.StreamingStateChanged += (_, streamStatus) => this.StreamState = streamStatus;
            this._obsWebsocket.StreamStatus += (_, streamStatus) => this.StreamStatus = streamStatus;
        }

        public async void TryConnect(string url, string password)
        {
            if (this._connectionLock)
            {
                return;
            }
            try
            {
                this._connectionLock = true;
                await Task.Run(() => this._obsWebsocket.Connect(url, password));
            }
            catch (AuthFailureException)
            {
                _ = Task.Run(() => this._obsWebsocket.Disconnect());
                this.SetConnectionFailed(true);
            }
            catch (Exception e)
            {
                this.Plugin.PluginLog.Error(e, "Connection error: {0}", e.Message);
            }
            finally
            {
                this._connectionLock = false;
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
                if (this.IsConnected)
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
                if (this.IsConnected)
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

        private void OnConnected(object sender, EventArgs e)
        {
            this.Plugin.PluginLog.Information("OBS connected");
            this._previousRecordingInformation = this.GetRecordingLocation();
            this.SetConnectionFailed(false);
            this.ConnectionStatus = ConnectionStatus.Connected;
        }

        private void OnDisconnected(object sender, EventArgs e)
        {
            this.Plugin.PluginLog.Information("OBS disconnected");
        }

        private void RestoreRecordingLocation()
        {
            this.SetRecordingLocation(this._previousRecordingInformation);
        }

        private void SetConnectionFailed(bool value)
        {
            if (value) this.ConnectionStatus = ConnectionStatus.Failed;
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
    Connected,
    Connecting,
    Disconnected,
    Failed,
}
