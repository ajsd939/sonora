namespace Sonora.Services;

/// <summary>
/// A track reference as it travels between the page and the host. A streamed track carries a
/// YouTube id or watch URL; a file that lives on the device carries <c>local:</c> followed by
/// the platform's own identifier. One shape means the queue, the library and the resume
/// bookkeeping need no idea which kind of track they are holding.
/// </summary>
internal static class TrackRefs
{
	private const string LocalPrefix = "local:";

	public static bool IsLocal(string? reference) =>
		reference is not null && reference.StartsWith(LocalPrefix, StringComparison.Ordinal);

	/// <summary>The platform identifier behind a local ref; empty when the ref is not a local one.</summary>
	public static string LocalId(string reference) =>
		IsLocal(reference) ? reference[LocalPrefix.Length..] : string.Empty;

	public static string ToLocalRef(string id) => LocalPrefix + id;

	/// <summary>
	/// Stable key for one track. A local ref is already its own key: running it through the
	/// YouTube parser would trim it toward an eleven-character video id and let two files collide.
	/// </summary>
	public static string ToKey(string reference) =>
		IsLocal(reference) ? reference.Trim() : YouTubeRefs.ToKey(reference);
}
