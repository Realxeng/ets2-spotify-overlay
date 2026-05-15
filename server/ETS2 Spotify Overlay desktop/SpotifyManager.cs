using System;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;
using SpotifyAPI.Web.Enums;
using SpotifyAPI.Web.Models;

namespace ETS2_Spotify_Overlay
{
    public class SpotifyManager
    {
        private const string ExchangeServerUri = "https://spotify-token-swap.camefrom.space/";
        private static SpotifyWebAPI _spotify;
        private static readonly TokenSwapWebAPIFactory _spotifyAuth = new TokenSwapWebAPIFactory(
            ExchangeServerUri,
            Scope.UserReadPlaybackState,
            "http://127.0.0.1:4002",
            60,
            true,
            true
        );

        private PlaybackContext _playback;
        private string _currentTrackId;
        private string _cachedLyricsTrackId;
        private string _cachedSyncedLyrics = "";
        private readonly object _lyricsLock = new object();
        private bool _authorized = false;
        private bool _isUpdating = false;
        private const string LyricsApiUrl = "https://lrclib.net/api/get?track_name={0}&artist_name={1}";

        public event EventHandler<TrackInfo> TrackChanged;
        public event EventHandler<string> StatusChanged;

        public class TrackInfo
        {
            public string TrackName { get; set; }
            public string Artists { get; set; }
            public string Album { get; set; }
            public string AlbumCoverUrl { get; set; }
            public bool IsPlaying { get; set; }
            public int ProgressMs { get; set; }
            public int DurationMs { get; set; }
            public string SyncedLyrics { get; set; }
        }

        public bool IsConnected => _authorized && _spotify != null;

        public async Task ConnectAsync()
        {
            try
            {
                OnStatusChanged("Connecting...");
                
                _spotifyAuth.OnAuthSuccess += OnAuthSuccess;
                _spotifyAuth.OnAuthFailure += OnAuthFailure;
                _spotifyAuth.OnAccessTokenExpired += OnAccessTokenExpired;
                
                _spotify = await _spotifyAuth.GetWebApiAsync();
                _isUpdating = true;
                UpdateTrack();
            }
            catch (Exception ex)
            {
                OnStatusChanged("Connection failed: " + ex.Message);
                // Log.Write("Spotify connection error: " + ex.ToString());
            }
        }

        private void OnAuthSuccess(object sender, object e)
        {
            _authorized = true;
            OnStatusChanged("Connected");
            // Log.Write("Spotify auth successful");
        }

        private void OnAuthFailure(object sender, object e)
        {
            _authorized = false;
            OnStatusChanged("Auth failed");
            // Log.Write("Spotify auth failed");
        }

        private async void OnAccessTokenExpired(object sender, object e)
        {
            _authorized = false;
            try
            {
                // Log.Write("Refreshing Spotify auth...");
                await _spotifyAuth.RefreshAuthAsync();
                _authorized = true;
                OnStatusChanged("Reconnected");
            }
            catch (Exception ex)
            {
                OnStatusChanged("Reconnection failed: " + ex.Message);
                // Log.Write("Spotify refresh error: " + ex.ToString());
            }
        }

        private async void UpdateTrack()
        {
            if (!_isUpdating) return;

            if (_spotify == null)
            {
                await Task.Delay(5000);
                UpdateTrack();
                return;
            }

            if (!_authorized)
            {
                await Task.Delay(5000);
                UpdateTrack();
                return;
            }

            try
            {
                _playback = _spotify.GetPlayback();

                if (_playback.HasError())
                {
                    // Log.Write("Spotify error: " + _playback.Error.Status + " - " + _playback.Error.Message);
                    if (_playback.Error.Status == 401)
                    {
                        OnAccessTokenExpired(null, null);
                    }
                    await Task.Delay(5000);
                    UpdateTrack();
                    return;
                }

                if (_playback.Item == null)
                {
                    await Task.Delay(5000);
                    UpdateTrack();
                    return;
                }

                var trackInfo = new TrackInfo
                {
                    TrackName = _playback.Item.Name ?? "",
                    Artists = string.Join(" - ", _playback.Item.Artists.Select(a => a.Name)),
                    Album = _playback.Item.Album?.Name ?? "",
                    AlbumCoverUrl = _playback.Item.Album?.Images?.Count > 0 
                        ? _playback.Item.Album.Images[0].Url 
                        : null,
                    IsPlaying = _playback.IsPlaying,
                    ProgressMs = _playback.ProgressMs,
                    DurationMs = _playback.Item.DurationMs,
                    SyncedLyrics = GetCachedSyncedLyrics(_playback.Item.Uri)
                };

                // Check if track changed
                if (_currentTrackId == _playback.Item.Uri)
                {
                    // Track hasn't changed, just update progress (similar to VB code's fake seconds)
                    if (_playback.IsPlaying)
                    {
                        for (int fakeSeconds = 0; fakeSeconds <= 6; fakeSeconds++)
                        {
                            if (!_playback.IsPlaying) break;
                            var progressInfo = new TrackInfo
                            {
                                TrackName = trackInfo.TrackName,
                                Artists = trackInfo.Artists,
                                Album = trackInfo.Album,
                                AlbumCoverUrl = trackInfo.AlbumCoverUrl,
                                IsPlaying = trackInfo.IsPlaying,
                                ProgressMs = trackInfo.ProgressMs + (fakeSeconds * 1000),
                                DurationMs = trackInfo.DurationMs,
                                SyncedLyrics = trackInfo.SyncedLyrics
                            };
                            TrackChanged?.Invoke(this, progressInfo);
                            await Task.Delay(1000);
                        }
                        UpdateTrack();
                        return;
                    }
                    UpdateTrack();
                    return;
                }

                // Track changed
                _currentTrackId = _playback.Item.Uri;
                FetchAndCacheSyncedLyrics(_currentTrackId, trackInfo.TrackName, trackInfo.Artists);
                trackInfo.SyncedLyrics = GetCachedSyncedLyrics(_currentTrackId);
                TrackChanged?.Invoke(this, trackInfo);

                await Task.Delay(1000);
                UpdateTrack();
            }
            catch (Exception ex)
            {
                // Log.Write("Spotify GetPlayback error: " + ex.ToString());
                await Task.Delay(5000);
                UpdateTrack();
            }
        }

        public void Stop()
        {
            _isUpdating = false;
            _authorized = false;
            _spotify = null;
            lock (_lyricsLock)
            {
                _cachedLyricsTrackId = null;
                _cachedSyncedLyrics = "";
            }
            OnStatusChanged("Disconnected");
        }

        public TrackInfo GetCurrentTrack()
        {
            if (_playback == null || _playback.Item == null)
                return null;

            return new TrackInfo
            {
                TrackName = _playback.Item.Name ?? "",
                Artists = string.Join(" - ", _playback.Item.Artists.Select(a => a.Name)),
                Album = _playback.Item.Album?.Name ?? "",
                AlbumCoverUrl = _playback.Item.Album?.Images?.Count > 0 
                    ? _playback.Item.Album.Images[0].Url 
                    : null,
                IsPlaying = _playback.IsPlaying,
                ProgressMs = _playback.ProgressMs,
                DurationMs = _playback.Item.DurationMs,
                SyncedLyrics = GetCachedSyncedLyrics(_playback.Item.Uri)
            };
        }

        private string GetCachedSyncedLyrics(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return "";

            lock (_lyricsLock)
            {
                return _cachedLyricsTrackId == trackId ? (_cachedSyncedLyrics ?? "") : "";
            }
        }

        private void FetchAndCacheSyncedLyrics(string trackId, string trackName, string artists)
        {
            if (string.IsNullOrWhiteSpace(trackId) || string.IsNullOrWhiteSpace(trackName) || string.IsNullOrWhiteSpace(artists))
            {
                lock (_lyricsLock)
                {
                    _cachedLyricsTrackId = trackId;
                    _cachedSyncedLyrics = "";
                }
                return;
            }

            lock (_lyricsLock)
            {
                if (_cachedLyricsTrackId == trackId)
                    return;
            }

            string syncedLyrics = "";
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add(HttpRequestHeader.UserAgent, "ETS2-Spotify-Overlay");
                    string primaryArtist = artists.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? artists;
                    string url = string.Format(
                        LyricsApiUrl,
                        Uri.EscapeDataString(trackName),
                        Uri.EscapeDataString(primaryArtist)
                    );
                    string response = client.DownloadString(url);
                    var json = JObject.Parse(response);
                    syncedLyrics = json["syncedLyrics"]?.ToString() ?? "";
                }
            }
            catch
            {
                syncedLyrics = "";
            }

            lock (_lyricsLock)
            {
                _cachedLyricsTrackId = trackId;
                _cachedSyncedLyrics = syncedLyrics;
            }
        }

        private void OnStatusChanged(string status)
        {
            StatusChanged?.Invoke(this, status);
        }
    }
}
