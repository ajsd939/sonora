using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using Microsoft.Extensions.DependencyInjection;

namespace Sonora;

[Activity(
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	LaunchMode = LaunchMode.SingleTop,
	ConfigurationChanges = ConfigChanges.ScreenSize
		| ConfigChanges.Orientation
		| ConfigChanges.UiMode
		| ConfigChanges.ScreenLayout
		| ConfigChanges.SmallestScreenSize
		| ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
	private const int NotificationRequest = 17;

	public static MainActivity? Current { get; private set; }

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		Current = this;
		base.OnCreate(savedInstanceState);
		AskForNotificationPermission();
	}

	protected override void OnDestroy()
	{
		if (ReferenceEquals(Current, this))
		{
			Current = null;
		}

		Platforms.Android.PlaybackService.End(this);
		base.OnDestroy();
	}

	/// <summary>
	/// Back is the page's story of where it is, not the activity's stack: the player
	/// closes first, then the trail of album and artist pages unwinds, and only the
	/// page itself can say when there is nothing left to undo.
	/// </summary>
	public override void OnBackPressed()
	{
		var bridge = IPlatformApplication.Current?.Services?.GetService<HybridWebViewBridge>();

		if (bridge is null || !bridge.NotifyBack())
		{
			base.OnBackPressed();
		}
	}

	/// <summary>
	/// The playback notification is what keeps the stream alive off screen, and from
	/// Android 13 it is silently dropped until the listener allows it.
	/// </summary>
	private void AskForNotificationPermission()
	{
		if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
		{
			return;
		}

		if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications)
				== Permission.Granted)
		{
			return;
		}

		ActivityCompat.RequestPermissions(
			this,
			new string[] { global::Android.Manifest.Permission.PostNotifications },
			NotificationRequest);
	}
}
