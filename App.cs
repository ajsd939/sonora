using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace Sonora;

public sealed class App : Application
{
	private readonly MainPage _mainPage;

	public App(MainPage mainPage)
	{
		_mainPage = mainPage;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(_mainPage);
	}
}
