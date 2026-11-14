using Sonora.Models;

namespace Sonora.Services;

/// <summary>
/// Owns the single live playback session. It keeps the authoritative copy of "where the
/// listener is", watches for a frozen or missing playback clock, and drives
/// <see cref="StrictAudioStreamResolver"/> so that a dropped stream is re-fetched and
/// handed back to the player with the exact timestamp it stopped at.
/// </summary>
public sealed class PlaybackSupervisor : IDisposable
{
	/// <summary>How often the watchdog evaluates the playback clock.</summary>
	private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(2);

	/// <summary>
	/// How long the clock may stay frozen while the player claims to be playing before
	/// the host treats the stream as dead without being told.
	/// </summary>
	private static readonly TimeSpan FrozenClockGrace = TimeSpan.FromSeconds(7);

	/// <summary>
	/// Breathing room granted to the player after a fresh URL is handed over: fetching
	/// and parsing a new container takes longer than <see cref="FrozenClockGrace"/>.
	/// </summary>
	private static readonly TimeSpan ResumeSettleWindow = TimeSpan.FromSeconds(30);

	/// <summary>Advances smaller than this between reports count as "not moving".</summary>
	private const double ClockResolutionSeconds = 0.25;

	private readonly StrictAudioStreamResolver _resolver;
	private readonly TrackSearchService _search;
	private readonly IStreamBridge _bridge;
	private readonly FallbackAudioCache _fallback;
	private readonly Lock _gate = new();
	private readonly Timer _watchdog;

	private CancellationTokenSource? _session;
	private string _videoRef = string.Empty;
	private TrackDto? _track;
	private double _positionSeconds;
	private double _lastAdvancedPositionSeconds;
	private bool _isPlaying;
	private bool _recovering;
	private DateTimeOffset _lastAdvancedUtc = DateTimeOffset.MinValue;
	private DateTimeOffset _watchdogSuspendedUntilUtc = DateTimeOffset.MinValue;

	public PlaybackSupervisor(
		StrictAudioStreamResolver resolver,
		TrackSearchService search,
		IStreamBridge bridge,
		FallbackAudioCache fallback)
	{
		_resolver = resolver;
		_search = search;
		_bridge = bridge;
		_fallback = fallback;

		_resolver.RetryScheduled += payload => _bridge.Send(HostMessageType.RetryStarted, payload);
		_watchdog = new Timer(_ => EvaluateClock(), null, WatchdogInterval, WatchdogInterval);
	}

	/// <summary>
	/// Starts a track from zero. Returns as soon as metadata is known so the interface
	/// can paint the "now playing" bar; the audio URL arrives separately as a
	/// <c>streamReady</c> message, which is the same path a recovery uses.
	/// Only the id is needed to start: the names, cover and album are read from YouTube
	/// Music here rather than carried over from whichever card was tapped.
	/// </summary>
	public async Task<TrackDto?> PlayAsync(string videoUrlOrId)
	{
		if (string.IsNullOrWhiteSpace(videoUrlOrId))
		{
			return null;
		}

		var key = YouTubeRefs.ToKey(videoUrlOrId);
		TrackDto track;

		try
		{
			track = await _search.GetTrackAsync(videoUrlOrId).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception exception)
		{
			// Metadata only decorates the player bar; the resolver needs nothing but
			// the id, so an unreadable catalogue page must not stop the music.
			System.Diagnostics.Debug.WriteLine($"[sonora] metadata lookup failed: {FailureSummary.Of(exception)}");
			track = new TrackDto
			{
				VideoId = key,
				WatchUrl = YouTubeRefs.ToWatchUrl(key),
				Title = string.Empty,
				Artist = string.Empty,
			};
		}

		CancellationToken cancellationToken;
		lock (_gate)
		{
			_session?.Cancel();
			_session?.Dispose();
			_session = new CancellationTokenSource();
			cancellationToken = _session.Token;

			_videoRef = videoUrlOrId;
			_track = track;
			_positionSeconds = 0;
			_lastAdvancedPositionSeconds = 0;
			_isPlaying = true;
			_recovering = false;
			_lastAdvancedUtc = DateTimeOffset.UtcNow;
			_watchdogSuspendedUntilUtc = DateTimeOffset.UtcNow + ResumeSettleWindow;
		}

		// The listener moved on: the previous track's local fallback copy is no longer needed.
		_fallback.Clear();

#if ANDROID
		// Claiming the playback slot here is what lets the sound outlast the screen.
		Platforms.Android.PlaybackService.Begin(
			Android.App.Application.Context,
			track.Title,
			track.Artist);
#endif

		_ = RunResolutionAsync(videoUrlOrId, resumeSeconds: 0, stallReason: null, autoResumed: false, cancellationToken);
		return track;
	}

	/// <summary>
	/// The player's heartbeat. It is the only source of truth about the listening
	/// position, so it is recorded even when nothing else is happening.
	/// </summary>
	public void ReportPosition(PositionReport report)
	{
		if (report.CurrentSeconds < 0 || !IsCurrentTrack(report.VideoId))
		{
			return;
		}

		lock (_gate)
		{
			_positionSeconds = report.CurrentSeconds;
			_isPlaying = report.IsPlaying;

			if (report.IsPlaying && report.CurrentSeconds > _lastAdvancedPositionSeconds + ClockResolutionSeconds)
			{
				_lastAdvancedPositionSeconds = report.CurrentSeconds;
				_lastAdvancedUtc = DateTimeOffset.UtcNow;
			}
		}

		NotifyServicePlaying(report.IsPlaying);
	}

	/// <summary>Explicit failure reported by the player: an audio error or its own stall timer.</summary>
	public void ReportStall(StallReport report)
	{
		if (!IsCurrentTrack(report.VideoId))
		{
			return;
		}

		string videoRef;
		double seconds;
		CancellationToken token;

		lock (_gate)
		{
			_positionSeconds = Math.Max(_positionSeconds, Math.Max(0, report.CurrentSeconds));
			_isPlaying = true;

			if (_videoRef.Length == 0 || _recovering)
			{
				// A cycle is already running for this track; starting a second one would
				// hand the player two competing URLs.
				return;
			}

			_recovering = true;
			videoRef = _videoRef;
			seconds = _positionSeconds;
			token = _session?.Token ?? CancellationToken.None;
		}

		_ = RunResolutionAsync(videoRef, seconds, report.Reason ?? "player reported a stalled stream", autoResumed: true, token);
	}

	public void Pause()
	{
		lock (_gate)
		{
			_isPlaying = false;
		}

		NotifyServicePlaying(false);
	}

	/// <summary>Deletes the local fallback copy, if any. Used when the app closes.</summary>
	public void ClearFallback() => _fallback.Clear();

	/// <summary>
	/// Mirrors the play/pause state into the Android notification and its wake locks.
	/// A no-op on every other platform and whenever the state has not actually changed.
	/// </summary>
	private static void NotifyServicePlaying(bool playing)
	{
#if ANDROID
		Platforms.Android.PlaybackService.NotifyPlaying(global::Android.App.Application.Context, playing);
#endif
	}

	public void Dispose()
	{
		_watchdog.Dispose();
		lock (_gate)
		{
			_session?.Cancel();
			_session?.Dispose();
			_session = null;
		}

		_fallback.Clear();
	}

	private void EvaluateClock()
	{
		string videoRef;
		double seconds;
		CancellationToken token;

		lock (_gate)
		{
			var now = DateTimeOffset.UtcNow;
			if (!_isPlaying || _recovering || _videoRef.Length == 0)
			{
				return;
			}

			// A hand-over was just pushed: the clock is allowed to be quiet while the
			// player binds and parses the new container.
			if (now < _watchdogSuspendedUntilUtc || now - _lastAdvancedUtc <= FrozenClockGrace)
			{
				return;
			}

			_recovering = true;
			videoRef = _videoRef;
			seconds = _positionSeconds;
			token = _session?.Token ?? CancellationToken.None;
		}

		_ = RunResolutionAsync(videoRef, seconds, "playback stopped advancing", autoResumed: true, token);
	}

	/// <summary>
	/// One resolution cycle at a time: a second concurrent cycle would only compete for
	/// the same manifest and could hand the player a URL for an older position.
	/// </summary>
	private async Task RunResolutionAsync(
		string videoRef,
		double resumeSeconds,
		string? stallReason,
		bool autoResumed,
		CancellationToken cancellationToken)
	{
		if (videoRef.Length == 0)
		{
			return;
		}

		TrackDto? track;
		lock (_gate)
		{
			_recovering = true;
			track = _track;
		}

		try
		{
			// A stream that stopped mid-song is replaced with a stable local copy first;
			// only if that download fails do we fall back to re-resolving another URL.
			if (autoResumed && await TryFallbackAsync(videoRef, resumeSeconds, track, cancellationToken).ConfigureAwait(false))
			{
				return;
			}

			cancellationToken.ThrowIfCancellationRequested();

			var result = await _resolver
				.ResolveAsync(videoRef, TimeSpan.FromSeconds(resumeSeconds), stallReason, cancellationToken)
				.ConfigureAwait(false);

			if (cancellationToken.IsCancellationRequested)
			{
				return;
			}

			if (result.Succeeded)
			{
				lock (_gate)
				{
					_recovering = false;
					_isPlaying = true;
					_lastAdvancedUtc = DateTimeOffset.UtcNow;
					_lastAdvancedPositionSeconds = result.ResumePosition.TotalSeconds;
					_watchdogSuspendedUntilUtc = DateTimeOffset.UtcNow + ResumeSettleWindow;
				}

				NotifyServicePlaying(true);

				_bridge.Send(HostMessageType.StreamReady, new StreamReadyPayload
				{
					VideoId = YouTubeRefs.ToKey(videoRef),
					Url = result.Url!,
					Container = result.Container,
					ResumePositionSeconds = result.ResumePosition.TotalSeconds,
					Attempt = result.AttemptsUsed,
					AutoResumed = autoResumed,
					Track = track,
				});
				return;
			}

			// A fresh play that could not be resolved gets one last resort: a local download.
			// A mid-song drop already tried the download before re-resolving, so skip it here.
			if (!autoResumed && await TryFallbackAsync(videoRef, result.ResumePosition.TotalSeconds, track, cancellationToken).ConfigureAwait(false))
			{
				return;
			}

			// The listener may have skipped during the download; a stale error would pause
			// the new track, so let the cancellation unwind instead of reporting failure.
			cancellationToken.ThrowIfCancellationRequested();

			lock (_gate)
			{
				_recovering = false;
				_isPlaying = false;
			}

			NotifyServicePlaying(false);

			_bridge.Send(HostMessageType.PlaybackError, new PlaybackErrorPayload
			{
				VideoId = YouTubeRefs.ToKey(videoRef),
				Attempts = result.AttemptsUsed,
				Reason = result.Error,
				ResumePositionSeconds = result.ResumePosition.TotalSeconds,
			});
		}
		catch (OperationCanceledException)
		{
			// The listener moved to another track; this result would be stale anyway.
		}
		catch (Exception exception)
		{
			lock (_gate)
			{
				_recovering = false;
				_isPlaying = false;
			}

			NotifyServicePlaying(false);

			_bridge.Send(HostMessageType.PlaybackError, new PlaybackErrorPayload
			{
				VideoId = YouTubeRefs.ToKey(videoRef),
				Attempts = StrictAudioStreamResolver.MaxAttempts,
				Reason = FailureSummary.Of(exception),
				ResumePositionSeconds = resumeSeconds,
			});
		}
	}

	/// <summary>
	/// Downloads the track once and hands the page a local copy. Returns false when the
	/// download or read fails, so the caller can fall through to re-resolving (mid-song) or
	/// the terminal error (fresh play). The recovery flag stays set throughout, which keeps
	/// the watchdog from starting a second competing cycle while the download is in flight.
	/// </summary>
	private async Task<bool> TryFallbackAsync(string videoRef, double resumeSeconds, TrackDto? track, CancellationToken cancellationToken)
	{
		try
		{
			var path = await _fallback.DownloadFallbackAudioAsync(videoRef, cancellationToken).ConfigureAwait(false);
			if (path is null || cancellationToken.IsCancellationRequested)
			{
				return false;
			}

			var audio = await _fallback.LoadAsync(path, cancellationToken).ConfigureAwait(false);
			if (audio is null || cancellationToken.IsCancellationRequested)
			{
				return false;
			}

			lock (_gate)
			{
				_recovering = false;
				_isPlaying = true;
				_lastAdvancedUtc = DateTimeOffset.UtcNow;
				_lastAdvancedPositionSeconds = resumeSeconds;
				_watchdogSuspendedUntilUtc = DateTimeOffset.UtcNow + ResumeSettleWindow;
			}

			NotifyServicePlaying(true);

			_bridge.Send(HostMessageType.FallbackReady, new FallbackReadyPayload
			{
				VideoId = YouTubeRefs.ToKey(videoRef),
				AudioBase64 = audio.Base64,
				Mime = audio.Mime,
				ResumePositionSeconds = resumeSeconds,
				Track = track,
			});
			return true;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] fallback recovery failed: {FailureSummary.Of(exception)}");
			return false;
		}
	}

	/// <summary>
	/// Guards against late events from a source that has already been swapped out. A
	/// report carrying no id is attributed to whatever track is loaded.
	/// </summary>
	private bool IsCurrentTrack(string? reportedVideoId)
	{
		lock (_gate)
		{
			if (_videoRef.Length == 0)
			{
				return false;
			}

			return string.IsNullOrEmpty(reportedVideoId) || YouTubeRefs.ToKey(reportedVideoId) == YouTubeRefs.ToKey(_videoRef);
		}
	}
}
