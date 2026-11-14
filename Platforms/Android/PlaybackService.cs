using Android.App;
using Android.Content;
using Android.Net.Wifi;
using Android.OS;
using Microsoft.Extensions.DependencyInjection;
using Sonora.Services;

namespace Sonora.Platforms.Android;

/// <summary>
/// The reason audio survives the app going off screen. Android stops a backgrounded
/// process from mixing sound unless an ongoing foreground service claims the playback,
/// and from API 34 that claim has to be typed as media playback. The wake and Wi-Fi
/// locks keep the CPU and the radio awake while the screen is off; without them the
/// stream dies a few seconds into the first buffer gap.
/// </summary>
[Service(
	ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaPlayback,
	Exported = false)]
public sealed class PlaybackService : Service
{
	internal const string ChannelId = "sonora.playback";
	internal const string ToggleAction = "in.sonora.app.action.TOGGLE";
	internal const int NotificationId = 4201;

	private static string _title = string.Empty;
	private static string _artist = string.Empty;
	private static bool _playing = true;
	private static PlaybackService? _instance;

	private PowerManager.WakeLock? _wakeLock;
	private WifiManager.WifiLock? _wifiLock;

	public static void Begin(Context context, string title, string artist)
	{
		_title = title;
		_artist = artist;
		_playing = true;

		var intent = ServiceIntent(context);
		if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
		{
			context.StartForegroundService(intent);
		}
		else
		{
			context.StartService(intent);
		}
	}

	/// <summary>
	/// Keeps the notification's one button honest ("Pause" while playing, "Play" while
	/// paused) and lets the CPU sleep again while paused. Idempotent: the heartbeat may
	/// call it every second, and only a real change re-posts the notification.
	/// </summary>
	public static void NotifyPlaying(Context context, bool playing)
	{
		if (_playing == playing)
		{
			return;
		}

		_playing = playing;
		_instance?.HoldLocks(playing);

		if (_instance is not null)
		{
			NotificationManager.FromContext(context)!.Notify(NotificationId, NotificationFor(context));
		}
	}

	public static void End(Context context)
	{
		context.StopService(ServiceIntent(context));
		NotificationManager.FromContext(context)!.Cancel(NotificationId);
	}

	private static Intent ServiceIntent(Context context) =>
		new(context, Java.Lang.Class.FromType(typeof(PlaybackService)));

	public override IBinder? OnBind(Intent? intent) => null;

	public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
	{
		EnsureChannel(this);

		if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
		{
			StartForeground(NotificationId, NotificationFor(this), global::Android.Content.PM.ForegroundService.TypeMediaPlayback);
		}
		else
		{
			StartForeground(NotificationId, NotificationFor(this));
		}

		_instance = this;
		HoldLocks(_playing);

		// Restarted silently if the framework reclaims the process; the page re-reports
		// the position within a second and the notification catches up.
		return StartCommandResult.Sticky;
	}

	public override void OnDestroy()
	{
		HoldLocks(false);
		_instance = null;
		base.OnDestroy();
	}

	/// <summary>
	/// A partial wake lock keeps the CPU mixing audio with the screen off; the high-perf
	/// Wi-Fi lock stops the radio from dozing mid-stream. Both are dropped while paused
	/// so a parked player does not drain the battery.
	/// </summary>
	private void HoldLocks(bool held)
	{
		if (held)
		{
			if (_wakeLock is null)
			{
				var power = (PowerManager)GetSystemService(PowerService)!;
				_wakeLock = power.NewWakeLock(global::Android.OS.WakeLockFlags.Partial, "Sonora:Playback");
				_wakeLock.SetReferenceCounted(false);
			}

			if (_wifiLock is null)
			{
				var wifi = (WifiManager)GetSystemService(WifiService)!;
				_wifiLock = wifi.CreateWifiLock(global::Android.Net.WifiMode.FullHighPerf, "Sonora:Stream");
				_wifiLock.SetReferenceCounted(false);
			}

			if (!_wakeLock.IsHeld)
			{
				_wakeLock.Acquire();
			}

			if (!_wifiLock.IsHeld)
			{
				_wifiLock.Acquire();
			}

			return;
		}

		if (_wakeLock?.IsHeld == true)
		{
			_wakeLock.Release();
		}

		if (_wifiLock?.IsHeld == true)
		{
			_wifiLock.Release();
		}
	}

	private static void EnsureChannel(Context context)
	{
		if (Build.VERSION.SdkInt < BuildVersionCodes.O)
		{
			return;
		}

		var manager = NotificationManager.FromContext(context)!;
		if (manager.GetNotificationChannel(ChannelId) is not null)
		{
			return;
		}

		var channel = new NotificationChannel(ChannelId, "Playing", NotificationImportance.Low);
		channel.SetShowBadge(false);
		channel.EnableLights(false);
		channel.EnableVibration(false);
		manager.CreateNotificationChannel(channel);
	}

	private static Notification NotificationFor(Context context)
	{
		var open = new Intent(context, Java.Lang.Class.FromType(typeof(MainActivity)));
		open.SetFlags(ActivityFlags.SingleTop);
		var contentIntent = PendingIntent.GetActivity(
			context,
			0,
			open,
			PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

		var toggle = new Intent(context, Java.Lang.Class.FromType(typeof(PlaybackToggleReceiver)));
		toggle.SetAction(ToggleAction);
		var toggleIntent = PendingIntent.GetBroadcast(
			context,
			1,
			toggle,
			PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

		var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
			? new Notification.Builder(context, ChannelId)
			: new Notification.Builder(context);

		builder
			.SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
			.SetContentTitle(string.IsNullOrWhiteSpace(_title) ? "Sonora" : _title)
			.SetContentText(string.IsNullOrWhiteSpace(_artist) ? "Playing" : _artist)
			.SetOngoing(true)
			.SetOnlyAlertOnce(true)
			.SetContentIntent(contentIntent)
			.AddAction(
				_playing ? global::Android.Resource.Drawable.IcMediaPause : global::Android.Resource.Drawable.IcMediaPlay,
				_playing ? "Pause" : "Play",
				toggleIntent);

		return builder.Build();
	}
}

/// <summary>
/// The notification's only control. It cannot touch the audio element, so it asks the
/// page to toggle, which is the same path the play button takes.
/// </summary>
[BroadcastReceiver(Exported = false)]
public sealed class PlaybackToggleReceiver : BroadcastReceiver
{
	public override void OnReceive(Context? context, Intent? intent)
	{
		if (intent?.Action != PlaybackService.ToggleAction)
		{
			return;
		}

		var services = IPlatformApplication.Current?.Services;
		var bridge = services?.GetService<HybridWebViewBridge>();
		bridge?.Send("remoteToggle", new { });
	}
}
