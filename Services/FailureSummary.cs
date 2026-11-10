namespace Sonora.Services;

/// <summary>
/// Turns an exception into a one-line string that is safe to render in the interface.
/// </summary>
internal static class FailureSummary
{
	private const int MaximumLength = 220;

	/// <summary>
	/// Transport exceptions quote the playability URL, whose signature is single-use and
	/// bound to the request that produced it. Cutting at the query string keeps the
	/// message readable without leaking a live URL into the UI and its logs.
	/// </summary>
	public static string Of(Exception exception)
	{
		var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();

		var queryStart = message.IndexOf('?');
		if (queryStart > 0)
		{
			message = message[..queryStart].TrimEnd() + " [signed parameters hidden]";
		}

		if (string.IsNullOrWhiteSpace(message))
		{
			message = exception.GetType().Name;
		}

		return message.Length > MaximumLength ? message[..MaximumLength] + "…" : message;
	}
}
