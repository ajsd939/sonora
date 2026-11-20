using System.Text.Json.Serialization;

namespace Sonora.Models;

/// <summary>
/// Compact card model for the shelves the music catalogue returns. Everything the web
/// view needs to draw a cover is already resolved server-side, so the page never has to
/// reason about image sizes or YouTube's thumbnail syntax.
/// </summary>
public sealed class AlbumDto
{
	/// <summary>Identifier accepted by <c>OpenAlbum</c>: an album browse id or a playlist id.</summary>
	[JsonPropertyName("id")]
	public required string Id { get; init; }

	[JsonPropertyName("title")]
	public required string Title { get; init; }

	[JsonPropertyName("artist")]
	public required string Artist { get; init; }

	[JsonPropertyName("year")]
	public int Year { get; init; }

	[JsonPropertyName("coverUrl")]
	public string? CoverUrl { get; init; }

	[JsonPropertyName("songCount")]
	public int SongCount { get; init; }

	[JsonPropertyName("durationSeconds")]
	public double DurationSeconds { get; init; }

	[JsonPropertyName("isSingle")]
	public bool IsSingle { get; init; }
}

public sealed class ArtistDto
{
	[JsonPropertyName("channelId")]
	public required string ChannelId { get; init; }

	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("avatarUrl")]
	public string? AvatarUrl { get; init; }

	[JsonPropertyName("bannerUrl")]
	public string? BannerUrl { get; init; }

	[JsonPropertyName("subscribersInfo")]
	public string? SubscribersInfo { get; init; }

	[JsonPropertyName("popularityInfo")]
	public string? PopularityInfo { get; init; }
}

/// <summary>One request from the search box, three shaped result sets.</summary>
public sealed class SearchPageDto
{
	[JsonPropertyName("query")]
	public required string Query { get; init; }

	[JsonPropertyName("songs")]
	public TrackDto[] Songs { get; init; } = [];

	[JsonPropertyName("albums")]
	public AlbumDto[] Albums { get; init; } = [];

	[JsonPropertyName("artists")]
	public ArtistDto[] Artists { get; init; } = [];
}

public sealed class ArtistProfileDto
{
	[JsonPropertyName("artist")]
	public required ArtistDto Artist { get; init; }

	[JsonPropertyName("description")]
	public string? Description { get; init; }

	/// <summary>Playable highlights shown at the top of the profile.</summary>
	[JsonPropertyName("tracks")]
	public TrackDto[] Tracks { get; init; } = [];

	[JsonPropertyName("albums")]
	public AlbumDto[] Albums { get; init; } = [];

	[JsonPropertyName("singles")]
	public AlbumDto[] Singles { get; init; } = [];

	[JsonPropertyName("related")]
	public ArtistDto[] Related { get; init; } = [];
}

public sealed class AlbumDetailDto
{
	[JsonPropertyName("album")]
	public required AlbumDto Album { get; init; }

	[JsonPropertyName("tracks")]
	public TrackDto[] Tracks { get; init; } = [];
}

/// <summary>
/// The device's own music, as the page sees it. An empty shelf and a refused one look nothing
/// alike to the listener, so the answer carries which of the two it is.
/// </summary>
public sealed class LocalShelfDto
{
	/// <summary>False when the listener turned down access, which the page has to say plainly.</summary>
	[JsonPropertyName("allowed")]
	public required bool Allowed { get; init; }

	[JsonPropertyName("tracks")]
	public TrackDto[] Tracks { get; init; } = [];
}
