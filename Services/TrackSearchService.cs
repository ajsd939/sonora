using Sonora.Models;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models.Search;

namespace Sonora.Services;

/// <summary>
/// Track-level metadata, read from YouTube Music rather than from the video itself:
/// the same song is credited to "Kanye West" here and to "KanyeWestVEVO" on YouTube.
/// The identifiers stay ordinary YouTube video ids, which is all
/// <see cref="StrictAudioStreamResolver"/> needs to play them.
/// </summary>
public sealed class TrackSearchService
{
	/// <summary>Upper bound on cards returned to the grid.</summary>
	public const int DefaultResultLimit = 24;

	private readonly YouTubeMusicClient _music;

	public TrackSearchService(YouTubeMusicClient music)
	{
		_music = music;
	}

	public async Task<TrackDto[]> SearchAsync(string query, int limit = DefaultResultLimit)
	{
		if (string.IsNullOrWhiteSpace(query) || limit <= 0)
		{
			return [];
		}

		var results = await _music
			.SearchAsync(query, SearchCategory.Songs)
			.FetchItemsAsync(0, limit)
			.ConfigureAwait(false);

		return results.OfType<SongSearchResult>().Select(MusicCatalogService.ToTrack).ToArray();
	}

	/// <summary>
	/// Metadata for a single id or watch URL. Thrown-on-failure is intentional: the
	/// caller needs to distinguish "video unavailable" from a transient network loss.
	/// </summary>
	public async Task<TrackDto> GetTrackAsync(string videoUrlOrId)
	{
		var key = YouTubeRefs.ToKey(videoUrlOrId);
		var song = await _music.GetSongVideoInfoAsync(key).ConfigureAwait(false);
		var albumId = song.Album?.Id;
		var coverUrl = CoverArt.Square(song.Thumbnails, 300);

		// A song page only ever carries the video frame; the square cover lives on the
		// album, which the song already names by browse id.
		if (albumId is not null && albumId.StartsWith("MPRE", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				var album = await _music.GetAlbumInfoAsync(albumId).ConfigureAwait(false);
				coverUrl = CoverArt.Square(album.Thumbnails, 300) ?? coverUrl;
			}
			catch (Exception)
			{
				// Losing the artwork is not a reason to refuse to play the track.
			}
		}

		return new TrackDto
		{
			VideoId = song.Id ?? key,
			WatchUrl = YouTubeRefs.ToWatchUrl(song.Id ?? key),
			Title = song.Name,
			Artist = MusicCatalogService.JoinNames(song.Artists),
			DurationSeconds = song.Duration.TotalSeconds,
			ThumbnailUrl = coverUrl,
			AlbumId = albumId,
			AlbumName = song.Album?.Name,
		};
	}
}
