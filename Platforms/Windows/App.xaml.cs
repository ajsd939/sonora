using Microsoft.Maui;

namespace Sonora.WinUI;

/// <summary>
/// WinUI bootstrap only: the MAUI application, its services and the window come from
/// <see cref="MauiProgram"/> and <see cref="App"/>. The visible UI is the HTML bundle
/// hosted by the web view inside <c>MainPage</c>.
/// </summary>
public partial class App : MauiWinUIApplication
{
	public App()
	{
		// WebView2 defaults its data folder to beside the exe; under Program Files that
		// is read-only, so redirect it to a writable per-user path before the view loads.
		var userDataFolder = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Sonora", "WebView2");
		Directory.CreateDirectory(userDataFolder);
		Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);

		InitializeComponent();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
