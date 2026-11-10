using Microsoft.Maui;

namespace Sonora.WinUI;

/// <summary>
/// WinUI bootstrap only: the MAUI application, its services and the window come from
/// <see cref="MauiProgram"/> and <see cref="App"/>. The visible UI is the HTML bundle
/// hosted by the web view inside <c>MainPage</c>.
/// </summary>
public partial class App : MauiWinUIApplication
{
	public App() => InitializeComponent();

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
