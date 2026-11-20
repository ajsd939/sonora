using Sonora.Models;
using Sonora.Services;

namespace Sonora;

/// <summary>
/// Inbound half of the JavaScript interface. Public methods on the object registered
/// here are reachable from the page through
/// <c>window.HybridWebView.InvokeDotNet('Name', [args])</c>.
/// </summary>
public sealed class SonoraBridgeTarget
{
	private readonly TrackSearchService _search;
	private readonly MusicCatalogService _catalog;
	private readonly PlaybackSupervisor _supervisor;
	private readonly LrcLibService _lyrics;
	private readonly LocalMusicService _local;

	public SonoraBridgeTarget(
		TrackSearchService search,
		MusicCatalogService catalog,
		PlaybackSupervisor supervisor,
		LrcLibService lyrics,
		LocalMusicService local)
	{
		_search = search;
		_catalog = catalog;
		_supervisor = supervisor;
		_lyrics = lyrics;
		_local = local;
	}

	/// <summary>Catalogue lookup for the search grid. Errors surface as an empty grid.</summary>
	public async Task<TrackDto[]> Search(string query, int limit)
	{
		try
		{
			return await _search.SearchAsync(query, limit).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			// A failed search must not look like a playback failure to the player.
			System.Diagnostics.Debug.WriteLine($"[sonora] search failed: {FailureSummary.Of(exception)}");
			return [];
		}
	}

	/// <summary>Songs, albums and artists for one query, each in its own shelf.</summary>
	public async Task<SearchPageDto> SearchMusic(string query, int limit)
	{
		try
		{
			return await _catalog.SearchAsync(query, songLimit: limit).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] music search failed: {FailureSummary.Of(exception)}");
			return new SearchPageDto { Query = query };
		}
	}

	/// <summary>Artist profile: header, playable highlights, discography, similar artists.</summary>
	public async Task<ArtistProfileDto?> OpenArtist(string channelId, string? avatarUrl)
	{
		try
		{
			return await _catalog.GetArtistAsync(channelId, avatarUrl).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] artist profile failed: {FailureSummary.Of(exception)}");
			return null;
		}
	}

	/// <summary>Album or single with its full tracklist.</summary>
	public async Task<AlbumDetailDto?> OpenAlbum(string id)
	{
		try
		{
			return await _catalog.GetAlbumAsync(id).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] album tracklist failed: {FailureSummary.Of(exception)}");
			return null;
		}
	}

	/// <summary>
	/// The music already on the device, asking for access the first time the shelf is opened.
	/// A refusal is an answer of its own, not an empty shelf.
	/// </summary>
	public async Task<LocalShelfDto> LocalTracks()
	{
		try
		{
			if (!await _local.RequestAccessAsync().ConfigureAwait(false))
			{
				return new LocalShelfDto { Allowed = false };
			}

			return new LocalShelfDto { Allowed = true, Tracks = await _local.ScanAsync().ConfigureAwait(false) };
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] local shelf failed: {FailureSummary.Of(exception)}");
			return new LocalShelfDto { Allowed = true };
		}
	}

	/// <summary>
	/// Begins playback; the audio follows as a <c>streamReady</c> message, or as <c>audioBytes</c>
	/// when the track is one of the listener's own files.
	/// </summary>
	/// <remarks>
	/// Only the reference crosses the bridge. A whole <see cref="TrackDto"/> cannot: the page's
	/// library rows are a smaller shape than the DTO, and binding one here would reject the
	/// call before playback ever started.
	/// </remarks>
	public Task<TrackDto?> Play(string videoUrlOrId) => _supervisor.PlayAsync(videoUrlOrId);

	/// <summary>
	/// Time-coded lyrics for the player. Null simply means LRCLIB has nothing for this
	/// song, which the page shows as an empty lyrics panel.
	/// </summary>
	public async Task<LyricsDto?> Lyrics(string artistName, string trackName, string? albumName, double durationSeconds)
	{
		try
		{
			return await _lyrics.GetLyricsAsync(artistName, trackName, albumName, durationSeconds).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] lyrics failed: {FailureSummary.Of(exception)}");
			return null;
		}
	}

	/// <summary>Playback heartbeat, also the host's only knowledge of the listening position.</summary>
	public void ReportPosition(PositionReport report) => _supervisor.ReportPosition(report);

	/// <summary>The player saw the stream break and is asking the host to re-resolve it.</summary>
	public void ReportStall(StallReport report) => _supervisor.ReportStall(report);

	public void Pause() => _supervisor.Pause();

#if ANDROID
	/// <summary>
	/// Back walked the page all the way to its root, so the page is asking to be closed.
	/// </summary>
	public void Exit() => MainActivity.Current?.Finish();
#endif
}

/// <summary>
/// Sends host messages down to the page. The web view control may only be touched from
/// the UI thread, while the resolver runs on the thread pool, so every send is
/// marshalled before it becomes a raw message.
/// </summary>
public sealed class HybridWebViewBridge : IStreamBridge
{
	private Microsoft.Maui.Controls.HybridWebView? _webView;

	public void Attach(Microsoft.Maui.Controls.HybridWebView webView) => _webView = webView;

	public void Send(string type, object payload)
	{
		var json = Protocol.Serialize(new HostEnvelope { Type = type, Payload = payload });
		Deliver(json);
	}

	/// <summary>
	/// Hands a Back press to the page. False means no page is listening yet, which lets
	/// the activity fall back to its own idea of what Back does.
	/// </summary>
	public bool NotifyBack()
	{
		if (_webView is null)
		{
			return false;
		}

		Send("back", new { });
		return true;
	}

	private void Deliver(string json)
	{
		var webView = _webView;
		if (webView is null)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] dropped message before the web view attached: {json}");
			return;
		}

		if (webView.Dispatcher.IsDispatchRequired)
		{
			webView.Dispatcher.Dispatch(() => Push(webView, json));
			return;
		}

		Push(webView, json);
	}

	private static void Push(Microsoft.Maui.Controls.HybridWebView webView, string json)
	{
		try
		{
			webView.SendRawMessage(json);
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] raw message delivery failed: {FailureSummary.Of(exception)}");
		}
	}
}
