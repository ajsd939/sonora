using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Sonora.Services;

namespace Sonora;

public sealed class App : Application
{
	private readonly MainPage _mainPage;
	private readonly PlaybackSupervisor _supervisor;

	public App(MainPage mainPage, PlaybackSupervisor supervisor)
	{
		_mainPage = mainPage;
		_supervisor = supervisor;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(_mainPage) { Title = "Sonora" };
		// Closing the app frees the local fallback copy; a track change already clears it.
		window.Destroying += (_, _) => _supervisor.ClearFallback();
		return window;
	}
}
