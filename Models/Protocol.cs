using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sonora.Models;

/// <summary>
/// Message types the C# host pushes down to the web view.
/// </summary>
public static class HostMessageType
{
	public const string StreamReady = "streamReady";
	public const string FallbackReady = "fallbackReady";
	public const string RetryStarted = "retryStarted";
	public const string PlaybackError = "playbackError";
}

/// <summary>
/// Envelope used for every C# -&gt; JavaScript message.
/// The whole envelope is serialized to one JSON string and handed to
/// <see cref="Microsoft.Maui.Controls.HybridWebView"/> as a raw message.
/// </summary>
public sealed class HostEnvelope
{
	[JsonPropertyName("type")]
	public required string Type { get; init; }

	[JsonPropertyName("payload")]
	public required object Payload { get; init; }
}

public sealed class TrackDto
{
	/// <summary>11-character YouTube id, or the full watch URL when it cannot be derived.</summary>
	[JsonPropertyName("videoId")]
	public required string VideoId { get; init; }

	[JsonPropertyName("url")]
	public required string WatchUrl { get; init; }

	[JsonPropertyName("title")]
	public required string Title { get; init; }

	[JsonPropertyName("artist")]
	public required string Artist { get; init; }

	[JsonPropertyName("durationSeconds")]
	public double DurationSeconds { get; init; }

	[JsonPropertyName("thumbnailUrl")]
	public string? ThumbnailUrl { get; init; }

	/// <summary>Album this track belongs to, when the catalogue knows it.</summary>
	[JsonPropertyName("albumId")]
	public string? AlbumId { get; init; }

	[JsonPropertyName("albumName")]
	public string? AlbumName { get; init; }

	/// <summary>Pre-formatted popularity string, e.g. "1.2B plays".</summary>
	[JsonPropertyName("playsInfo")]
	public string? PlaysInfo { get; init; }

	[JsonPropertyName("trackNumber")]
	public int? TrackNumber { get; init; }
}

/// <summary>
/// Sent whenever a playable audio URL exists — both for a fresh play and for every
/// automatic recovery. <c>ResumePositionSeconds</c> is the exact timestamp the player
/// reported when the previous stream stopped.
/// </summary>
public sealed class StreamReadyPayload
{
	[JsonPropertyName("videoId")]
	public required string VideoId { get; init; }

	[JsonPropertyName("url")]
	public required string Url { get; init; }

	[JsonPropertyName("container")]
	public string? Container { get; init; }

	[JsonPropertyName("resumePositionSeconds")]
	public double ResumePositionSeconds { get; init; }

	/// <summary>1-based index of the attempt that produced this URL.</summary>
	[JsonPropertyName("attempt")]
	public int Attempt { get; init; }

	/// <summary>False for a user-initiated play, true when this URL replaced a dropped stream.</summary>
	[JsonPropertyName("autoResumed")]
	public bool AutoResumed { get; init; }

	[JsonPropertyName("track")]
	public TrackDto? Track { get; init; }
}

/// <summary>
/// Self-healing recovery: the host downloaded the audio once and hands the page a local copy,
/// either to replace a stream that dropped mid-song or as a last resort when a fresh play
/// could not be resolved. The bytes travel as base64 because the page cannot read a
/// <c>file://</c> path from its secure origin; the player turns them into a same-origin blob
/// and resumes from <see cref="ResumePositionSeconds"/>.
/// </summary>
public sealed class FallbackReadyPayload
{
	[JsonPropertyName("videoId")]
	public required string VideoId { get; init; }

	[JsonPropertyName("audioBase64")]
	public required string AudioBase64 { get; init; }

	[JsonPropertyName("mime")]
	public required string Mime { get; init; }

	[JsonPropertyName("resumePositionSeconds")]
	public double ResumePositionSeconds { get; init; }

	[JsonPropertyName("track")]
	public TrackDto? Track { get; init; }
}

public sealed class RetryStartedPayload
{
	[JsonPropertyName("videoId")]
	public required string VideoId { get; init; }

	[JsonPropertyName("attempt")]
	public int Attempt { get; init; }

	[JsonPropertyName("maxAttempts")]
	public int MaxAttempts { get; init; }

	[JsonPropertyName("delayMs")]
	public int DelayMs { get; init; }

	[JsonPropertyName("reason")]
	public string? Reason { get; init; }
}

/// <summary>Terminal notification: all five resolution attempts failed.</summary>
public sealed class PlaybackErrorPayload
{
	[JsonPropertyName("videoId")]
	public required string VideoId { get; init; }

	[JsonPropertyName("attempts")]
	public int Attempts { get; init; }

	[JsonPropertyName("reason")]
	public string? Reason { get; init; }

	[JsonPropertyName("resumePositionSeconds")]
	public double ResumePositionSeconds { get; init; }
}

/// <summary>
/// Playback heartbeat reported by the JavaScript audio element. It is the only place
/// the host learns where the listener actually is, which is what makes an exact resume
/// possible when the stream dies between two reports.
/// </summary>
public sealed class PositionReport
{
	[JsonPropertyName("videoId")]
	public string VideoId { get; set; } = string.Empty;

	[JsonPropertyName("currentSeconds")]
	public double CurrentSeconds { get; set; }

	[JsonPropertyName("durationSeconds")]
	public double DurationSeconds { get; set; }

	[JsonPropertyName("isPlaying")]
	public bool IsPlaying { get; set; }
}

/// <summary>Explicit failure reported by the JavaScript player (audio error / frozen clock).</summary>
public sealed class StallReport
{
	[JsonPropertyName("videoId")]
	public string VideoId { get; set; } = string.Empty;

	[JsonPropertyName("currentSeconds")]
	public double CurrentSeconds { get; set; }

	[JsonPropertyName("reason")]
	public string? Reason { get; set; }
}

public sealed class LyricsDto
{
	/// <summary>True when <see cref="Text"/> is LRC time-coded, false for plain text.</summary>
	[JsonPropertyName("synced")]
	public required bool Synced { get; init; }

	[JsonPropertyName("text")]
	public required string Text { get; init; }
}

public static class Protocol
{
	public static JsonSerializerOptions Json { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public static string Serialize(HostEnvelope envelope) =>
		JsonSerializer.Serialize(envelope, Json);
}
