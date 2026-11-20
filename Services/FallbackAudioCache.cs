using Microsoft.Maui.Storage;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Sonora.Services;

/// <summary>Audio the host holds as bytes rather than as a URL, encoded for the page to play.</summary>
public sealed record AudioBytes(string Base64, string Mime);

/// <summary>
/// Self-healing recovery. A stream that stops mid-song is replaced with a local copy downloaded
/// once to a private temp file, so playback continues from disk instead of a flaky network
/// stream; a fresh play that cannot be resolved uses the same download as a last resort. The
/// page cannot read a <c>file://</c> path from its secure origin, so the bytes are handed over
/// as a same-origin blob. The file lives in the cache directory and is deleted as soon as the
/// listener moves on or the app closes.
/// </summary>
public sealed class FallbackAudioCache
{
	/// <summary>Containers every target WebView decodes natively, most compatible first.</summary>
	private static readonly string[] PreferredContainers = ["m4a", "mp4", "webm"];

	private readonly YoutubeClient _youtube;
	private readonly Lock _gate = new();
	private string? _currentPath;

	public FallbackAudioCache(YoutubeClient youtube) => _youtube = youtube;

	/// <summary>
	/// Downloads the highest-bitrate audio-only stream to a temp file and returns its path,
	/// or null when no audio stream exists or the download fails. All I/O is asynchronous and
	/// runs off the UI thread, so a slow download never freezes the interface.
	/// </summary>
	public async Task<string?> DownloadFallbackAudioAsync(string videoUrlOrId, CancellationToken cancellationToken = default)
	{
		var watchUrl = YouTubeRefs.ToWatchUrl(videoUrlOrId);
		var key = YouTubeRefs.ToKey(videoUrlOrId);
		string? path = null;

		try
		{
			var manifest = await _youtube.Videos.Streams.GetManifestAsync(watchUrl, cancellationToken).ConfigureAwait(false);
			var audioStreams = manifest.GetAudioOnlyStreams();
			var preferred = audioStreams.Where(stream => IsPreferred(stream.Container.ToString()));
			var chosen = preferred.TryGetWithHighestBitrate() ?? audioStreams.TryGetWithHighestBitrate();
			if (chosen is null)
			{
				return null;
			}

			var extension = chosen.Container.ToString().ToLowerInvariant();
			path = Path.Combine(FileSystem.CacheDirectory, $"sonora-fallback-{key}-{Guid.NewGuid():N}.{extension}");

			await _youtube.Videos.Streams.DownloadAsync(chosen, path, progress: null, cancellationToken).ConfigureAwait(false);

			lock (_gate)
			{
				// A newer fallback may have replaced this one while the download was running.
				TryDelete(_currentPath);
				_currentPath = path;
			}

			return path;
		}
		catch (OperationCanceledException)
		{
			TryDelete(path);
			throw;
		}
		catch (Exception exception)
		{
			// A half-written file is never tracked, so remove it here rather than leak it.
			TryDelete(path);
			System.Diagnostics.Debug.WriteLine($"[sonora] fallback download failed: {FailureSummary.Of(exception)}");
			return null;
		}
	}

	/// <summary>Reads a downloaded file into the base64 payload and MIME type the page needs.</summary>
	public async Task<AudioBytes?> LoadAsync(string path, CancellationToken cancellationToken = default)
	{
		try
		{
			var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
			return new AudioBytes(Convert.ToBase64String(bytes), MimeFor(path));
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] fallback read failed: {FailureSummary.Of(exception)}");
			return null;
		}
	}

	/// <summary>Deletes the temp file, if any. Called on a track change and on app close.</summary>
	public void Clear()
	{
		lock (_gate)
		{
			TryDelete(_currentPath);
			_currentPath = null;
		}
	}

	private static void TryDelete(string? path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return;
		}

		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] fallback cleanup failed: {FailureSummary.Of(exception)}");
		}
	}

	private static bool IsPreferred(string container) =>
		PreferredContainers.Any(candidate => container.Contains(candidate, StringComparison.OrdinalIgnoreCase));

	private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
	{
		".webm" => "audio/webm",
		".ogg" or ".opus" => "audio/ogg",
		_ => "audio/mp4",
	};
}