using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Sonora.Models;

namespace Sonora.Services;

internal sealed record LrcLibEntry(
	[property: JsonPropertyName("trackName")] string? TrackName,
	[property: JsonPropertyName("albumName")] string? AlbumName,
	[property: JsonPropertyName("duration")] double? Duration,
	[property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics,
	[property: JsonPropertyName("plainLyrics")] string? PlainLyrics);

/// <summary>
/// Time-coded lyrics from the public LRCLIB index.
/// The exact-match endpoint needs the album and the length to line up — YouTube Music
/// supplies both — and the ranked search is the fallback for releases LRCLIB filed under
/// a different album, where the closest recording wins.
/// </summary>
public sealed class LrcLibService
{
	/// <summary>A lyrics lookup must never hold the player open waiting on a third party.</summary>
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

	/// <summary>Anything further from the real length than this ranks as a different recording.</summary>
	private const double MaximumDurationDrift = 20;

	private readonly HttpClient _http;

	public LrcLibService(HttpClient http)
	{
		_http = http;
	}

	/// <summary>
	/// Returns null when LRCLIB has nothing, and also on any transport failure: absent
	/// lyrics are a quiet blank in the player, never an error the listener is told about.
	/// </summary>
	public async Task<LyricsDto?> GetLyricsAsync(
		string artistName,
		string trackName,
		string? albumName,
		double durationSeconds,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(trackName))
		{
			return null;
		}

		// Either leg failing on its own must not cost the listener the other leg.
		var exact = await TryRequestAsync<LrcLibEntry>(
			() => ExactQuery(artistName, trackName, albumName, durationSeconds),
			cancellationToken).ConfigureAwait(false);

		var lyrics = ToDto(exact);
		if (lyrics is not null)
		{
			return lyrics;
		}

		var candidates = await TryRequestAsync<LrcLibEntry[]>(
			() => $"api/search?track_name={Value(trackName)}&artist_name={Value(artistName)}",
			cancellationToken).ConfigureAwait(false);

		return candidates?
			.Where(entry => (HasTimeCode(entry.SyncedLyrics) || !string.IsNullOrWhiteSpace(entry.PlainLyrics))
				&& SameSong(entry.TrackName, trackName))
			.OrderBy(entry => Rank(entry, albumName, durationSeconds))
			.Select(entry => ToDto(entry)!)
			.FirstOrDefault();
	}

	/// <summary>
	/// Runs one request with its own budget. A timeout, a rate limit or a body that is not
	/// the shape promised all answer "nothing here" rather than reaching the player.
	/// </summary>
	private async Task<T?> TryRequestAsync<T>(Func<string> relativeUri, CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(RequestTimeout);

		try
		{
			return await RequestAsync<T>(relativeUri(), timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] lyrics request failed: {FailureSummary.Of(exception)}");
			return default;
		}
	}

	private async Task<T?> RequestAsync<T>(string relativeUri, CancellationToken cancellationToken)
	{
		using var response = await _http.GetAsync(new Uri(_http.BaseAddress!, relativeUri), cancellationToken)
			.ConfigureAwait(false);

		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			return default;
		}

		response.EnsureSuccessStatusCode();
		return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
	}

	private static LyricsDto? ToDto(LrcLibEntry? entry)
	{
		if (entry is null)
		{
			return null;
		}

		if (HasTimeCode(entry.SyncedLyrics))
		{
			return new LyricsDto { Synced = true, Text = entry.SyncedLyrics! };
		}

		return string.IsNullOrWhiteSpace(entry.PlainLyrics)
			? null
			: new LyricsDto { Synced = false, Text = entry.PlainLyrics };
	}

	/// <summary>Time codes are what make the line scroll, so a "[mm:ss.xx]" anywhere is required.</summary>
	private static bool HasTimeCode(string? lrc) => !string.IsNullOrWhiteSpace(lrc) && lrc.Contains('[');

	/// <summary>
	/// The ranked search answers loosely, so a candidate only counts when the words of its
	/// title agree with ours once the "(feat. …)" and " - Edit" decoration is stripped.
	/// Without this, a song LRCLIB has misfiled gets sung in the player as another song.
	/// </summary>
	private static bool SameSong(string? candidateTitle, string trackName)
	{
		return string.Equals(CoreTitle(candidateTitle), CoreTitle(trackName), StringComparison.Ordinal);
	}

	private static string CoreTitle(string? title)
	{
		if (string.IsNullOrWhiteSpace(title))
		{
			return string.Empty;
		}

		var text = title;
		foreach (var marker in new[] { "(", "[", "feat", "ft.", " - ", " – ", " — " })
		{
			var cut = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
			if (cut > 0)
			{
				text = text[..cut];
			}
		}

		var builder = new StringBuilder(text.Length);
		foreach (var character in text)
		{
			if (char.IsLetterOrDigit(character))
			{
				builder.Append(char.ToLowerInvariant(character));
			}
		}

		return builder.ToString();
	}

	/// <summary>Lower ranks higher: time-coded first, then the same album, then the same length.</summary>
	private static double Rank(LrcLibEntry entry, string? albumName, double durationSeconds)
	{
		double score = HasTimeCode(entry.SyncedLyrics) ? 0 : 1_000;

		if (!string.IsNullOrWhiteSpace(albumName)
			&& !string.IsNullOrWhiteSpace(entry.AlbumName)
			&& !string.Equals(entry.AlbumName, albumName, StringComparison.OrdinalIgnoreCase))
		{
			score += 60;
		}

		if (durationSeconds > 1 && entry.Duration is double length)
		{
			score += Math.Min(Math.Abs(length - durationSeconds), MaximumDurationDrift * 2);
		}

		return score;
	}

	private static string ExactQuery(string artistName, string trackName, string? albumName, double durationSeconds)
	{
		var query = $"api/get?track_name={Value(trackName)}&artist_name={Value(artistName)}";

		if (!string.IsNullOrWhiteSpace(albumName))
		{
			query += $"&album_name={Value(albumName)}";
		}

		if (durationSeconds > 1)
		{
			query += $"&duration={(int)Math.Round(durationSeconds)}";
		}

		return query;
	}

	private static string Value(string text) => Uri.EscapeDataString(text.Trim());
}
