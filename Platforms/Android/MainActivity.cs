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
	private const int AudioRequest = 18;

	private TaskCompletionSource<bool>? _audioAccess;

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

	/// <summary>
	/// Reading the device's music is a permission of its own, and from Android 13 it is no longer
	/// covered by the broad storage one.
	/// </summary>
	private bool HasAudioAccess() => CheckSelfPermission(AudioPermission) == Permission.Granted;

	/// <summary>
	/// Asks once and waits for the answer. The dialog belongs to the activity, so the page's
	/// request parks until the listener actually responds to it.
	/// </summary>
	public Task<bool> RequestAudioAccessAsync()
	{
		if (HasAudioAccess())
		{
			return Task.FromResult(true);
		}

		if (_audioAccess is not null)
		{
			return _audioAccess.Task;
		}

		var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		_audioAccess = pending;

		ActivityCompat.RequestPermissions(this, new[] { AudioPermission }, AudioRequest);
		return pending.Task;
	}

	public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
	{
		if (requestCode == AudioRequest)
		{
			var granted = grantResults.Length > 0 && grantResults[0] == Permission.Granted;
			var pending = _audioAccess;
			_audioAccess = null;
			pending?.TrySetResult(granted);
		}

		base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
	}

	// The API level is tested where the permission is named, which the compatibility analyzer does not follow.
#pragma warning disable CA1416
	private static string AudioPermission =>
		Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
			? global::Android.Manifest.Permission.ReadMediaAudio
			: global::Android.Manifest.Permission.ReadExternalStorage;
#pragma warning restore CA1416
}
