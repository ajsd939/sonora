using Sonora.Models;
using YouTubeMusicAPI.Client;
using YouTubeMusicAPI.Models;
using YouTubeMusicAPI.Models.Info;
using YouTubeMusicAPI.Models.Search;

namespace Sonora.Services;

/// <summary>
/// Catalogue reads against YouTube Music: typed search, artist profiles and album
/// tracklists. Playback is deliberately not handled here — the ids this returns are
/// ordinary YouTube video ids that <see cref="StrictAudioStreamResolver"/> resolves.
/// </summary>
public sealed class MusicCatalogService
{
	/// <summary>Upper bound for a tracklist; no real album is anywhere near this.</summary>
	private const int TracklistLimit = 100;

	/// <summary>Rows in the "Popular tracks" shelf of an artist profile.</summary>
	private const int PopularTrackLimit = 10;

	/// <summary>
	/// A songs search is only trusted as the popular-tracks shelf when it actually
	/// returns something for this artist; below this many rows the profile's own
	/// videos are used instead.
	/// </summary>
	private const int PopularTrackFloor = 3;

	private readonly YouTubeMusicClient _music;

	public MusicCatalogService(YouTubeMusicClient music)
	{
		_music = music;
	}

	/// <summary>
	/// The unfiltered search endpoint returns nothing useful, so the three shelves are
	/// fetched concurrently and merged into one page.
	/// </summary>
	public async Task<SearchPageDto> SearchAsync(
		string query,
		int songLimit = 12,
		int shelfLimit = 8,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(query))
		{
			return new SearchPageDto { Query = string.Empty };
		}

		var songsTask = _music.SearchAsync(query, SearchCategory.Songs).FetchItemsAsync(0, songLimit, cancellationToken);
		var albumsTask = _music.SearchAsync(query, SearchCategory.Albums).FetchItemsAsync(0, shelfLimit, cancellationToken);
		var artistsTask = _music.SearchAsync(query, SearchCategory.Artists).FetchItemsAsync(0, shelfLimit, cancellationToken);

		await Task.WhenAll(songsTask, albumsTask, artistsTask).ConfigureAwait(false);

		return new SearchPageDto
		{
			Query = query,
			Songs = songsTask.Result.OfType<SongSearchResult>().Select(ToTrack).ToArray(),
			Albums = albumsTask.Result.OfType<AlbumSearchResult>().Select(ToAlbum).ToArray(),
			Artists = artistsTask.Result.OfType<ArtistSearchResult>().Select(ToArtist).ToArray(),
		};
	}

	/// <summary>
	/// Artist header plus its shelves. <paramref name="avatarUrl"/> is whatever the caller
	/// already has from a search card, because the profile payload only carries banners.
	/// </summary>
	public async Task<ArtistProfileDto> GetArtistAsync(
		string channelId,
		string? avatarUrl = null,
		CancellationToken cancellationToken = default)
	{
		var info = await _music.GetArtistInfoAsync(channelId, cancellationToken).ConfigureAwait(false);
		var artistName = string.IsNullOrWhiteSpace(info.Name) ? channelId : info.Name;

		var albumsTask = GetReleasesAsync(info.AlbumsEndpoint, info.Albums, cancellationToken);
		var singlesTask = GetReleasesAsync(info.SinglesAndEpsEndpoint, info.SinglesAndEps, cancellationToken);
		var tracksTask = GetPopularTracksAsync(artistName, info.Videos, cancellationToken);

		await Task.WhenAll(albumsTask, singlesTask, tracksTask).ConfigureAwait(false);
		var tracks = await tracksTask.ConfigureAwait(false);

		return new ArtistProfileDto
		{
			Artist = new ArtistDto
			{
				ChannelId = channelId,
				Name = artistName,
				AvatarUrl = avatarUrl ?? CoverArt.Square(info.Thumbnails, 240),
				BannerUrl = CoverArt.Wide(info.Thumbnails),
				SubscribersInfo = info.SubscribersInfo,
			},
			Description = info.Description,
			Tracks = tracks,
			Albums = (await albumsTask.ConfigureAwait(false)).Select(album => ToAlbum(album, artistName)).ToArray(),
			Singles = (await singlesTask.ConfigureAwait(false)).Select(album => ToAlbum(album, artistName)).ToArray(),
			Related = info.Related.Select(ToArtist).ToArray(),
		};
	}

	/// <summary>
	/// The profile payload only carries the first ten releases of each shelf; the full
	/// discography has to be browsed again through the endpoint the shelf itself points at.
	/// </summary>
	private async Task<ArtistAlbum[]> GetReleasesAsync(
		BrowseEndpoint? endpoint,
		ArtistAlbum[] firstPage,
		CancellationToken cancellationToken)
	{
		if (endpoint is null || string.IsNullOrEmpty(endpoint.BrowseId))
		{
			return firstPage;
		}

		try
		{
			var releases = await _music
				.GetArtistAlbumsAsync(endpoint.BrowseId, endpoint.Params, cancellationToken)
				.ConfigureAwait(false);

			return releases.Length > 0 ? releases : firstPage;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			// A missing discography page is not worth losing the ten rows already in hand.
			return firstPage;
		}
	}

	/// <summary>
	/// YouTube Music puts music videos, not songs, in this shelf, and video rows only ever
	/// carry a widescreen frame. A songs search for the artist name is the same list with
	/// real square cover art, a play count and an album link, so it is preferred and the
	/// videos are the fallback.
	/// </summary>
	private async Task<TrackDto[]> GetPopularTracksAsync(
		string artistName,
		ArtistVideo[] videos,
		CancellationToken cancellationToken)
	{
		try
		{
			var results = await _music
				.SearchAsync(artistName, SearchCategory.Songs)
				.FetchItemsAsync(0, PopularTrackLimit * 2, cancellationToken)
				.ConfigureAwait(false);

			var songs = results
				.OfType<SongSearchResult>()
				.Where(song => JoinNames(song.Artists).Contains(artistName, StringComparison.OrdinalIgnoreCase))
				.Take(PopularTrackLimit)
				.Select(ToTrack)
				.ToArray();

			return songs.Length >= PopularTrackFloor ? songs : videos.Select(ToTrack).ToArray();
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			return videos.Select(ToTrack).ToArray();
		}
	}

	/// <summary>
	/// Album plus tracklist. Browse ids (MPRE…) carry proper metadata; catalogue search
	/// hands back playlist ids instead, and those only answer through the playlist
	/// endpoints — whose own metadata is unreliable, so the tracklist becomes the source
	/// of truth for count and duration.
	/// </summary>
	public async Task<AlbumDetailDto> GetAlbumAsync(string id, CancellationToken cancellationToken = default)
	{
		if (id.StartsWith("MPRE", StringComparison.OrdinalIgnoreCase))
		{
			var album = await _music.GetAlbumInfoAsync(id, cancellationToken).ConfigureAwait(false);
			var artistLine = JoinNames(album.Artists);
			var coverUrl = CoverArt.Square(album.Thumbnails);

			return new AlbumDetailDto
			{
				Album = new AlbumDto
				{
					Id = id,
					Title = album.Name,
					Artist = artistLine,
					Year = album.ReleaseYear,
					CoverUrl = coverUrl,
					SongCount = album.SongCount,
					DurationSeconds = album.Duration.TotalSeconds,
					IsSingle = album.IsSingle || album.IsEp,
				},
				// A track row inside an album wears that album's cover: the payload
				// itself carries no artwork.
				Tracks = album.Songs
					.OrderBy(song => song.SongNumber ?? int.MaxValue)
					.Select(song => ToTrack(song, artistLine, album.Name, id, coverUrl))
					.ToArray(),
			};
		}

		var browseId = id.StartsWith("VL", StringComparison.OrdinalIgnoreCase)
			? id
			: _music.GetCommunityPlaylistBrowseId(id);

		var playlistTask = _music.GetCommunityPlaylistInfoAsync(browseId, cancellationToken);
		var songsEnumerable = _music.GetCommunityPlaylistSongsAsync(browseId);
		var tracks = await songsEnumerable.FetchItemsAsync(0, TracklistLimit, cancellationToken).ConfigureAwait(false);
		var playlist = await playlistTask.ConfigureAwait(false);

		// The playlist's own artwork is only ever a widescreen frame, while the first row
		// carries the square cover the album actually has.
		var playlistCover = tracks.Count > 0
			? CoverArt.Square(tracks[0].Thumbnails) ?? CoverArt.Square(playlist.Thumbnails)
			: CoverArt.Square(playlist.Thumbnails);

		return new AlbumDetailDto
		{
			Album = new AlbumDto
			{
				Id = id,
				Title = playlist.Name,
				Artist = tracks.Count > 0 ? JoinNames(tracks[0].Artists) : playlist.Creator?.Name ?? string.Empty,
				// No year here: a playlist reports the year it was made, not the year the
				// album was released, and a wrong date is worse than none.
				CoverUrl = playlistCover,
				SongCount = tracks.Count,
				DurationSeconds = tracks.Sum(song => song.Duration.TotalSeconds),
			},
			Tracks = tracks
				.Select((song, index) => ToTrack(song, index + 1))
				.ToArray(),
		};
	}

	internal static TrackDto ToTrack(SongSearchResult song) => new()
	{
		VideoId = song.Id,
		WatchUrl = YouTubeRefs.ToWatchUrl(song.Id),
		Title = song.Name,
		Artist = JoinNames(song.Artists),
		DurationSeconds = song.Duration.TotalSeconds,
		ThumbnailUrl = CoverArt.Square(song.Thumbnails, 300),
		AlbumId = song.Album?.Id,
		PlaysInfo = song.PlaysInfo,
	};

	private static TrackDto ToTrack(ArtistVideo video) => new()
	{
		VideoId = video.Id,
		WatchUrl = YouTubeRefs.ToWatchUrl(video.Id),
		Title = video.Name,
		Artist = JoinNames(video.Artists),
		ThumbnailUrl = CoverArt.Wide(video.Thumbnails),
		PlaysInfo = video.ViewsInfo,
	};

	// The playlist and album row models inherit their identifier from NamedEntity, where
	// it is nullable, unlike the search results.
	private static TrackDto ToTrack(AlbumSong song, string artistLine, string albumName, string albumId, string? coverUrl)
	{
		var videoId = song.Id ?? string.Empty;

		return new TrackDto
		{
			VideoId = videoId,
			WatchUrl = YouTubeRefs.ToWatchUrl(videoId),
			Title = song.Name,
			Artist = artistLine,
			DurationSeconds = song.Duration.TotalSeconds,
			ThumbnailUrl = coverUrl,
			AlbumId = albumId,
			AlbumName = albumName,
			PlaysInfo = song.PlaysInfo,
			TrackNumber = song.SongNumber,
		};
	}

	private static TrackDto ToTrack(CommunityPlaylistSong song, int position)
	{
		var videoId = song.Id ?? string.Empty;

		return new TrackDto
		{
			VideoId = videoId,
			WatchUrl = YouTubeRefs.ToWatchUrl(videoId),
			Title = song.Name,
			Artist = JoinNames(song.Artists),
			DurationSeconds = song.Duration.TotalSeconds,
			ThumbnailUrl = CoverArt.Square(song.Thumbnails, 300),
			AlbumId = song.Album?.Id,
			AlbumName = song.Album?.Name,
			TrackNumber = position,
		};
	}

	private static AlbumDto ToAlbum(AlbumSearchResult album) => new()
	{
		Id = album.Id,
		Title = album.Name,
		Artist = JoinNames(album.Artists),
		Year = album.ReleaseYear,
		CoverUrl = CoverArt.Square(album.Thumbnails, 400),
		IsSingle = album.IsSingle || album.IsEp,
	};

	// A profile's own albums carry no artist field; the profile is the artist.
	private static AlbumDto ToAlbum(ArtistAlbum album, string artistName) => new()
	{
		Id = album.Id,
		Title = album.Name,
		Artist = artistName,
		Year = album.ReleaseYear,
		CoverUrl = CoverArt.Square(album.Thumbnails, 400),
		IsSingle = album.IsSingle || album.IsEp,
	};

	private static ArtistDto ToArtist(ArtistSearchResult artist) => new()
	{
		ChannelId = artist.Id,
		Name = artist.Name,
		AvatarUrl = CoverArt.Square(artist.Thumbnails, 240),
		PopularityInfo = artist.PopularityInfo,
	};

	private static ArtistDto ToArtist(ArtistsRelated related) => new()
	{
		ChannelId = related.Id,
		Name = related.Name,
		AvatarUrl = CoverArt.Square(related.Thumbnails, 240),
		SubscribersInfo = related.SubscribersInfo,
	};

	internal static string JoinNames(NamedEntity[]? entities) =>
		entities is null || entities.Length == 0
			? string.Empty
			: string.Join(", ", entities.Select(entity => entity.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
}
