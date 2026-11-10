using Sonora.Models;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Sonora.Services;

/// <summary>Outcome of one recovery cycle, i.e. of a run of 1..5 resolution attempts.</summary>
public sealed class ResolutionResult
{
	public required bool Succeeded { get; init; }

	/// <summary>Number of attempts actually spent (1 on the happy path).</summary>
	public required int AttemptsUsed { get; init; }

	/// <summary>Playability URL of the audio-only stream, when <see cref="Succeeded"/> is true.</summary>
	public string? Url { get; init; }

	/// <summary>Container reported by YouTube, e.g. <c>webm</c> or <c>mp4</c>.</summary>
	public string? Container { get; init; }

	/// <summary>Timestamp the caller asked to resume from, echoed back for the player.</summary>
	public TimeSpan ResumePosition { get; init; }

	/// <summary>Failure summary, safe to show in the UI: never contains a signed URL.</summary>
	public string? Error { get; init; }

	public static ResolutionResult Ok(int attempts, string url, string container, TimeSpan resumePosition) =>
		new()
		{
			Succeeded = true,
			AttemptsUsed = attempts,
			Url = url,
			Container = container,
			ResumePosition = resumePosition,
		};

	public static ResolutionResult Fail(int attempts, string error, TimeSpan resumePosition) =>
		new()
		{
			Succeeded = false,
			AttemptsUsed = attempts,
			ResumePosition = resumePosition,
			Error = error,
		};
}

/// <summary>
/// Resolves YouTube audio-only streams and refuses to give up before five attempts.
/// Every attempt is bounded by a timeout, so a stalled or half-open connection counts
/// as a failure just like a thrown exception does. The caller-supplied resume position
/// travels with the result so the player can continue exactly where it stopped.
/// </summary>
public sealed class StrictAudioStreamResolver
{
	/// <summary>Hard ceiling on resolution attempts per recovery cycle, per spec.</summary>
	public const int MaxAttempts = 5;

	private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(500);
	private static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(8);

	/// <summary>A manifest request that exceeds this is treated as a dropped connection.</summary>
	private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(20);

	/// <summary>Containers the UI is allowed to decode natively.</summary>
	private static readonly string[] PreferredContainers = ["webm", "mp4", "m4a"];

	private readonly YoutubeClient _youtube;

	public StrictAudioStreamResolver(YoutubeClient youtube)
	{
		_youtube = youtube;
	}

	/// <summary>
	/// Raised immediately before each back-off wait, so the interface can show
	/// "reconnecting (n/5)" instead of appearing frozen.
	/// </summary>
	public event Action<RetryStartedPayload>? RetryScheduled;

	/// <summary>
	/// Runs the retry loop. Never returns null on failure: inspect
	/// <see cref="ResolutionResult.Succeeded"/>. Throws only when
	/// <paramref name="cancellationToken"/> is cancelled, which means the listener
	/// moved on to a different track and the result would be stale anyway.
	/// </summary>
	public async Task<ResolutionResult> ResolveAsync(
		string videoUrlOrId,
		TimeSpan resumePosition,
		string? stallReason = null,
		CancellationToken cancellationToken = default)
	{
		var watchUrl = YouTubeRefs.ToWatchUrl(videoUrlOrId);
		string? lastError = null;

		for (var attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				var (url, container) = await ResolveOnceAsync(watchUrl, cancellationToken).ConfigureAwait(false);
				return ResolutionResult.Ok(attempt, url, container, resumePosition);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				lastError = FailureSummary.Of(exception);
			}

			if (attempt == MaxAttempts)
			{
				break;
			}

			var backoff = BackoffFor(attempt);
			RetryScheduled?.Invoke(new RetryStartedPayload
			{
				VideoId = YouTubeRefs.ToKey(videoUrlOrId),
				Attempt = attempt + 1,
				MaxAttempts = MaxAttempts,
				DelayMs = (int)backoff.TotalMilliseconds,
				Reason = lastError ?? stallReason,
			});

			await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
		}

		return ResolutionResult.Fail(MaxAttempts, lastError ?? stallReason ?? "Stream could not be resolved.", resumePosition);
	}

	/// <summary>
	/// Single, non-retrying resolution: fetch the manifest, keep only audio-only
	/// streams, take the highest bitrate one.
	/// </summary>
	private async Task<(string Url, string Container)> ResolveOnceAsync(
		string watchUrl,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// The manifest call hands back a ValueTask; only a Task can race the timeout.
		var manifestTask = _youtube.Videos.Streams.GetManifestAsync(watchUrl).AsTask();
		var winner = await Task.WhenAny(manifestTask, Task.Delay(AttemptTimeout)).ConfigureAwait(false);

		if (winner != manifestTask)
		{
			// Nobody will await this any more; mark it observed so the eventual failure
			// does not surface as an UnobservedTaskException on the finalizer thread.
			ObserveLate(manifestTask);
			throw new TimeoutException($"YouTube did not answer the stream manifest request within {AttemptTimeout.TotalSeconds:N0}s.");
		}

		var manifest = await manifestTask.ConfigureAwait(false);
		var audioStreams = manifest.GetAudioOnlyStreams();
		if (!audioStreams.Any())
		{
			throw new InvalidOperationException("This video has no audio-only stream available.");
		}

		var preferred = audioStreams.Where(stream => IsPreferred(stream.Container.ToString()));
		var chosen = preferred.TryGetWithHighestBitrate() ?? audioStreams.TryGetWithHighestBitrate()
			?? throw new InvalidOperationException("No decodable audio stream in manifest.");

		return (chosen.Url, chosen.Container.ToString());
	}

	private static bool IsPreferred(string container) =>
		PreferredContainers.Any(candidate => container.Contains(candidate, StringComparison.OrdinalIgnoreCase));

	/// <summary>500ms, 1s, 2s, 4s ... capped at 8s, doubling per failed attempt.</summary>
	private static TimeSpan BackoffFor(int failedAttempt)
	{
		var milliseconds = InitialBackoff.TotalMilliseconds * Math.Pow(2, failedAttempt - 1);
		return TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaximumBackoff.TotalMilliseconds));
	}

	private static void ObserveLate(Task task) =>
		task.ContinueWith(
			completed => _ = completed.Exception,
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);
}
