using System.Text.RegularExpressions;
using YouTubeMusicAPI.Models;

namespace Sonora.Services;

/// <summary>
/// Turns the thumbnail sets YouTube Music returns into one usable URL per card.
/// The catalogue hands back the same image at several sizes, and the Google CDN
/// accepts any size in the URL suffix, so a 120px search result can be asked for
/// as a 544px cover without another request.
/// </summary>
internal static class CoverArt
{
	/// <summary>The largest square cover Google serves natively.</summary>
	private const int CoverWidth = 544;

	private static readonly Regex GoogleSize = new(
		@"=w\d+-h\d+",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	/// <summary>Square artwork for album and song cards.</summary>
	public static string? Square(Thumbnail[]? thumbnails, int size = CoverWidth)
	{
		if (thumbnails is null || thumbnails.Length == 0)
		{
			return null;
		}

		var url = Largest(PreferSquare(thumbnails));
		if (url is null)
		{
			return null;
		}

		return GoogleSize.IsMatch(url) ? GoogleSize.Replace(url, $"=w{size}-h{size}", 1) : url;
	}

	/// <summary>Widescreen art for banners and video thumbnails, left at its native aspect.</summary>
	public static string? Wide(Thumbnail[]? thumbnails) => Largest(thumbnails);

	/// <summary>
	/// A set mixes the square cover with a widescreen banner, and cropping 16:9 into a
	/// cover loses the artwork itself, so any square source wins.
	/// </summary>
	private static IEnumerable<Thumbnail> PreferSquare(Thumbnail[] thumbnails)
	{
		var squares = thumbnails
			.Where(thumbnail => thumbnail.Width > 0 && Math.Abs(thumbnail.Width - thumbnail.Height) <= thumbnail.Width / 10)
			.ToArray();

		return squares.Length > 0 ? squares : thumbnails;
	}

	private static string? Largest(IEnumerable<Thumbnail>? thumbnails)
	{
		var winner = thumbnails?.OrderByDescending(thumbnail => (long)thumbnail.Width * thumbnail.Height).FirstOrDefault();
		return winner is null ? null : StripTrackerQuery(winner.Url);
	}

	/// <summary>
	/// i.ytimg.com URLs carry a tracking query that is not needed for display and makes
	/// the cache key needlessly volatile.
	/// </summary>
	private static string? StripTrackerQuery(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return null;
		}

		if (!url.Contains("ytimg.com", StringComparison.OrdinalIgnoreCase))
		{
			return url;
		}

		var query = url.IndexOf('?');
		return query > 0 ? url[..query] : url;
	}
}
