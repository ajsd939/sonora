using Sonora.Models;
#if ANDROID
using Android.Content;
using Android.Database;
using Android.Provider;
#endif

namespace Sonora.Services;

/// <summary>
/// The listener's own music — files that already live on the device, as opposed to a stream
/// fetched from YouTube Music. Both platforms keep an index of their audio and its tags, so the
/// shelf is a query rather than a crawl: Android through MediaStore, Windows through the shell's
/// music properties. Files are addressed by a <c>local:</c> ref and read whole, because the page
/// cannot open a native path from its secure origin.
/// </summary>
public sealed class LocalMusicService
{
	/// <summary>Cap on a shelf that could otherwise hold thousands of rows.</summary>
	private const int MaxTracks = 400;

	/// <summary>
	/// A file crosses the bridge as one base64 message, which costs time and memory on both
	/// sides at roughly four thirds its size. Past this limit the track is refused rather than
	/// risk a frozen player; serving the bytes without loading them is the way past it.
	/// </summary>
	private const long MaxReadableBytes = 50L * 1024 * 1024;

#if WINDOWS
	/// <summary>Containers the shell's decoders handle without a plugin.</summary>
	private static readonly string[] PlayableExtensions = [".mp3", ".m4a", ".mp4", ".aac", ".flac", ".ogg", ".wav", ".wma"];
#endif

	private readonly Lock _gate = new();

	/// <summary>Local id to wherever the platform keeps the bytes: a content URI, or a path.</summary>
	private Dictionary<string, string> _locations = new(StringComparer.Ordinal);

	/// <summary>The shelf the page was last handed, so a tap can be answered without a rescan.</summary>
	private TrackDto[] _tracks = [];

	/// <summary>
	/// The shelf row behind a local ref, or null when the ref came from a scan this host has
	/// already forgotten. The bytes live under the platform's own id, so the row is the only place
	/// the title and the length exist.
	/// </summary>
	public TrackDto? Find(string reference)
	{
		lock (_gate)
		{
			return Array.Find(_tracks, row => row.VideoId == reference);
		}
	}

	/// <summary>
	/// Asks for access if it is still missing. False means the listener said no, and the shelf has
	/// to say so in plain words rather than show itself as an empty library.
	/// </summary>
	public Task<bool> RequestAccessAsync()
	{
#if ANDROID
		var activity = global::Sonora.MainActivity.Current;
		return activity is null ? Task.FromResult(false) : activity.RequestAudioAccessAsync();
#else
		return Task.FromResult(true);
#endif
	}

	/// <summary>
	/// Rebuilds the shelf and remembers where every row lives, which is what makes a later read
	/// possible. A failed scan is reported as an empty shelf.
	/// </summary>
	public async Task<TrackDto[]> ScanAsync(CancellationToken cancellationToken = default)
	{
		try
		{
#if ANDROID
			return await Task.Run(() => ScanAndroid(cancellationToken), cancellationToken).ConfigureAwait(false);
#elif WINDOWS
			return await ScanWindowsAsync(cancellationToken).ConfigureAwait(false);
#else
			return [];
#endif
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] local scan failed: {FailureSummary.Of(exception)}");
			return [];
		}
	}

	/// <summary>
	/// Reads one local track into the bytes and MIME type the page plays, or null when the ref is
	/// unknown, the file went away, or it is too large to hand over in one message.
	/// </summary>
	public async Task<AudioBytes?> ReadAsync(string reference, CancellationToken cancellationToken = default)
	{
		var id = TrackRefs.LocalId(reference);
		if (id.Length == 0)
		{
			return null;
		}

		string location;
		lock (_gate)
		{
			if (!_locations.TryGetValue(id, out location!))
			{
				return null;
			}
		}

		try
		{
#if ANDROID
			return ReadAndroid(location);
#elif WINDOWS
			return await ReadWindowsAsync(location, cancellationToken).ConfigureAwait(false);
#else
			return null;
#endif
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] local read failed: {FailureSummary.Of(exception)}");
			return null;
		}
	}

	/// <summary>Maps a filename onto the MIME type the audio element expects.</summary>
	internal static string MimeForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
	{
		".mp3" => "audio/mpeg",
		".flac" => "audio/flac",
		".ogg" or ".opus" => "audio/ogg",
		".wav" => "audio/wav",
		".aac" => "audio/aac",
		_ => "audio/mp4",
	};

#if ANDROID
	/// <summary>
	/// MediaStore already indexes every audio file the device knows about, tags included, so the
	/// shelf is one query. Untitled files are skipped: a wall of blank rows is worse than not
	/// listing them.
	/// </summary>
	private TrackDto[] ScanAndroid(CancellationToken cancellationToken)
	{
		var resolver = Android.App.Application.Context.ContentResolver;
		if (resolver is null)
		{
			return [];
		}

		var columns = new[]
		{
			Android.Provider.IBaseColumns.Id,
			MediaStore.IMediaColumns.Title,
			MediaStore.Audio.IAudioColumns.Artist,
			MediaStore.Audio.IAudioColumns.Album,
			MediaStore.Audio.IAudioColumns.Duration,
		};

		using var cursor = resolver.Query(
			MediaStore.Audio.Media.ExternalContentUri!,
			columns,
			$"{MediaStore.Audio.IAudioColumns.IsMusic} != 0",
			null,
			$"{MediaStore.IMediaColumns.Title} COLLATE NOCASE ASC");

		if (cursor is null)
		{
			return [];
		}

		var titleColumn = cursor.GetColumnIndexOrThrow(MediaStore.IMediaColumns.Title);
		var idColumn = cursor.GetColumnIndexOrThrow(Android.Provider.IBaseColumns.Id);
		var artistColumn = cursor.GetColumnIndex(MediaStore.Audio.IAudioColumns.Artist);
		var albumColumn = cursor.GetColumnIndex(MediaStore.Audio.IAudioColumns.Album);
		var durationColumn = cursor.GetColumnIndex(MediaStore.Audio.IAudioColumns.Duration);

		var tracks = new List<TrackDto>();
		var locations = new Dictionary<string, string>(StringComparer.Ordinal);

		while (cursor.MoveToNext() && tracks.Count < MaxTracks)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var title = ReadText(cursor, titleColumn);
			if (title is null)
			{
				continue;
			}

			var mediaId = cursor.GetLong(idColumn);
			var key = mediaId.ToString(System.Globalization.CultureInfo.InvariantCulture);
			var reference = TrackRefs.ToLocalRef(key);

			locations[key] = ContentUris.WithAppendedId(MediaStore.Audio.Media.ExternalContentUri!, mediaId).ToString()!;
			tracks.Add(new TrackDto
			{
				VideoId = reference,
				WatchUrl = reference,
				Title = title,
				Artist = ReadText(cursor, artistColumn) ?? "Unknown artist",
				AlbumName = ReadText(cursor, albumColumn),
				DurationSeconds = durationColumn >= 0 ? cursor.GetLong(durationColumn) / 1000d : 0,
			});
		}

		lock (_gate)
		{
			_locations = locations;
			_tracks = [.. tracks];
			return _tracks;
		}
	}

	private static string? ReadText(ICursor cursor, int column) =>
		column < 0 || cursor.IsNull(column) ? null : NullIfSilent(cursor.GetString(column));

	private static string? NullIfSilent(string? value) =>
		string.IsNullOrWhiteSpace(value) || value == "<unknown>" ? null : value;

	/// <summary>
	/// The content URI is the only handle kept for a file: the resolver opens it, names its
	/// container and reports its size, and no path ever reaches the page.
	/// </summary>
	private AudioBytes? ReadAndroid(string location)
	{
		var resolver = Android.App.Application.Context.ContentResolver;
		var uri = Android.Net.Uri.Parse(location);
		if (resolver is null || uri is null)
		{
			return null;
		}

		using var descriptor = resolver.OpenAssetFileDescriptor(uri, "r");
		if (descriptor is null || descriptor.Length <= 0)
		{
			return null;
		}

		if (descriptor.Length > MaxReadableBytes)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] local file too large to hand over: {descriptor.Length} bytes");
			return null;
		}

		using var stream = resolver.OpenInputStream(uri);
		if (stream is null)
		{
			return null;
		}

		var buffer = new MemoryStream();
		stream.CopyTo(buffer);
		var mime = resolver.GetType(uri);

		return new AudioBytes(
			Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length),
			string.IsNullOrWhiteSpace(mime) ? "audio/mp4" : mime);
	}
#endif

#if WINDOWS
	/// <summary>
	/// An unpackaged Windows app reads the Music folders directly. Tags come from the shell, which
	/// is what Explorer shows, so the names match what the listener already sees.
	/// </summary>
	private async Task<TrackDto[]> ScanWindowsAsync(CancellationToken cancellationToken)
	{
		var tracks = new List<TrackDto>();
		var locations = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var path in EnumerateWindowsFiles())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (tracks.Count >= MaxTracks)
			{
				break;
			}

			string title;
			string artist;
			string? album;
			double seconds;

			try
			{
				var file = await Windows.Storage.StorageFile
					.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false);
				var music = await file.Properties
					.GetMusicPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);

				title = Pick(music.Title, Path.GetFileNameWithoutExtension(path));
				artist = Pick(music.Artist, "Unknown artist");
				album = string.IsNullOrWhiteSpace(music.Album) ? null : music.Album.Trim();
				seconds = music.Duration.TotalSeconds;
			}
			catch (Exception exception)
			{
				// One unreadable file must not empty the shelf.
				System.Diagnostics.Debug.WriteLine($"[sonora] local tag read skipped: {FailureSummary.Of(exception)}");
				continue;
			}

			var key = WindowsKey.For(path);
			var reference = TrackRefs.ToLocalRef(key);
			locations[key] = path;
			tracks.Add(new TrackDto
			{
				VideoId = reference,
				WatchUrl = reference,
				Title = title,
				Artist = artist,
				AlbumName = album,
				DurationSeconds = seconds,
			});
		}

		tracks.Sort((left, right) => string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase));

		lock (_gate)
		{
			_locations = locations;
			_tracks = [.. tracks];
			return _tracks;
		}
	}

	private static string Pick(string? tagged, string otherwise) =>
		string.IsNullOrWhiteSpace(tagged) ? otherwise : tagged.Trim();

	private static IEnumerable<string> EnumerateWindowsFiles()
	{
		var options = new System.IO.EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.System,
		};

		var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
			Environment.GetFolderPath(Environment.SpecialFolder.CommonMusic),
		};

		foreach (var root in roots)
		{
			if (root.Length == 0 || !Directory.Exists(root))
			{
				continue;
			}

			IEnumerable<string> enumerated;
			try
			{
				enumerated = Directory.EnumerateFiles(root, "*.*", options);
			}
			catch (Exception exception)
			{
				System.Diagnostics.Debug.WriteLine($"[sonora] local folder unreadable: {FailureSummary.Of(exception)}");
				continue;
			}

			foreach (var path in enumerated)
			{
				if (PlayableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
				{
					yield return path;
				}
			}
		}
	}

	private async Task<AudioBytes?> ReadWindowsAsync(string path, CancellationToken cancellationToken)
	{
		var info = new FileInfo(path);
		if (!info.Exists || info.Length == 0)
		{
			return null;
		}

		if (info.Length > MaxReadableBytes)
		{
			System.Diagnostics.Debug.WriteLine($"[sonora] local file too large to hand over: {info.Length} bytes");
			return null;
		}

		var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
		return new AudioBytes(Convert.ToBase64String(bytes), MimeForPath(path));
	}

	/// <summary>
	/// A short, stable handle for a path. The page never sees the path itself, and a whole hash
	/// would only bloat every message that carries the ref.
	/// </summary>
	private static class WindowsKey
	{
		public static string For(string path) =>
			Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..16];
	}
#endif
}
