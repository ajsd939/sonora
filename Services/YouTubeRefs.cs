using System.Text.RegularExpressions;

namespace Sonora.Services;

/// <summary>
/// Normalisation helpers for the "video id or watch URL" strings that travel between
/// the web view and YoutubeExplode.
/// </summary>
internal static class YouTubeRefs
{
	private static readonly Regex VideoIdInUrl = new(
		@"[?&]v=(?<id>[A-Za-z0-9_-]{11})",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	/// <summary>
	/// YoutubeExplode accepts either form, but the fully qualified URL is the one its
	/// parsers are documented against, so bare ids are expanded before any call.
	/// </summary>
	public static string ToWatchUrl(string videoUrlOrId) =>
		videoUrlOrId.StartsWith("http", StringComparison.OrdinalIgnoreCase)
			? videoUrlOrId
			: $"https://www.youtube.com/watch?v={videoUrlOrId}";

	/// <summary>
	/// Stable key for a track. Falls back to the original reference when no id can be
	/// derived (shorts, embeds), which keeps the resume bookkeeping consistent.
	/// </summary>
	public static string ToKey(string videoUrlOrId)
	{
		var match = VideoIdInUrl.Match(videoUrlOrId);
		if (match.Success)
		{
			return match.Groups["id"].Value;
		}

		return videoUrlOrId.Length == 11 ? videoUrlOrId : videoUrlOrId.Trim();
	}
}
