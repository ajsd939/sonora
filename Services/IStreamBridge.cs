namespace Sonora.Services;

/// <summary>
/// Outbound half of the host interface: everything the C# engine needs to tell the
/// web view. Implementations must be callable from any thread.
/// </summary>
public interface IStreamBridge
{
	void Send(string type, object payload);
}
