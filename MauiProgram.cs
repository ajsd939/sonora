using Microsoft.Extensions.DependencyInjection;
#if ANDROID
using Microsoft.Maui.Handlers;
#endif
#if WINDOWS
using Microsoft.Maui.Handlers;
#endif
using Sonora.Services;
using YoutubeExplode;
using YouTubeMusicAPI.Client;

namespace Sonora;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder.UseMauiApp<App>();

		builder.Services.AddSingleton(_ => new YoutubeClient());
		builder.Services.AddSingleton(_ => new YouTubeMusicClient());

		builder.Services.AddSingleton(_ =>
		{
			// LRCLIB asks lyrics clients to say who they are; no key is required.
			var http = new HttpClient
			{
				BaseAddress = new Uri("https://lrclib.net/"),
			};
			http.DefaultRequestHeaders.UserAgent.ParseAdd("Sonora/1.0 (MAUI HybridWebView player)");
			return http;
		});

		builder.Services.AddSingleton<TrackSearchService>();
		builder.Services.AddSingleton<MusicCatalogService>();
		builder.Services.AddSingleton<LrcLibService>();
		builder.Services.AddSingleton<StrictAudioStreamResolver>();
		builder.Services.AddSingleton<FallbackAudioCache>();
		builder.Services.AddSingleton<LocalMusicService>();
		builder.Services.AddSingleton<HybridWebViewBridge>();

		// The engine depends on the abstraction, the page depends on the concrete
		// control wrapper: one instance, two registrations.
		builder.Services.AddSingleton<IStreamBridge>(services =>
			services.GetRequiredService<HybridWebViewBridge>());

		builder.Services.AddSingleton<SonoraBridgeTarget>();
		builder.Services.AddSingleton<PlaybackSupervisor>();
		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		builder.Services.AddHybridWebViewDeveloperTools();
#endif

#if ANDROID
		// Android's WebView will not start sound unless the tap itself landed on the view.
		// That is harmless for the first play and fatal for queue autoplay and for the
		// resume at the end of a recovery cycle, neither of which follows a touch.
		HybridWebViewHandler.Mapper.AppendToMapping(
			"sonoraMediaPlayback",
			(handler, view) => handler.PlatformView.Settings.MediaPlaybackRequiresUserGesture = false);
#endif

#if WINDOWS
		// WinUI's unpackaged title bar starts blank: no icon until the AppWindow is given
		// one. The window handle only exists once the window first activates.
		WindowHandler.Mapper.AppendToMapping("sonoraWindowChrome", (handler, window) =>
		{
			if (handler.PlatformView is not Microsoft.UI.Xaml.Window platformWindow)
			{
				return;
			}

			void OnActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
			{
				platformWindow.Activated -= OnActivated;
				platformWindow.AppWindow?.SetIcon(Path.Combine(AppContext.BaseDirectory, "appicon.ico"));
			}

			platformWindow.Activated += OnActivated;
		});
#endif

		return builder.Build();
	}
}
