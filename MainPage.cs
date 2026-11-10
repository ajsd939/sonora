using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Sonora.Services;

namespace Sonora;

/// <summary>
/// The whole native surface is one <see cref="HybridWebView"/>: no Razor, no XAML, just
/// the plain HTML/CSS/JavaScript bundle under <c>Resources/Raw/wwwroot</c> with a
/// JavaScript bridge wired to the C# services.
/// </summary>
public sealed class MainPage : ContentPage
{
	private readonly PlaybackSupervisor _supervisor;

	public MainPage(
		SonoraBridgeTarget bridgeTarget,
		PlaybackSupervisor supervisor,
		HybridWebViewBridge bridge)
	{
		_supervisor = supervisor;

		// Dark chrome first: the web view paints over it, and this is what is visible
		// during the very first frame on Android.
		BackgroundColor = Color.FromRgb(0x0A, 0x0A, 0x0A);

		var webView = new HybridWebView
		{
			HybridRoot = "wwwroot",
			DefaultFile = "index.html",
		};

		bridge.Attach(webView);
		webView.SetInvokeJavaScriptTarget(bridgeTarget);

		Content = webView;
	}

	protected override void OnDisappearing()
	{
		_supervisor.Pause();
		base.OnDisappearing();
	}
}
