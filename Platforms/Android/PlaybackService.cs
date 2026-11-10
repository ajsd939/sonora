using Android.App;
using Android.Content;
using Android.OS;
using Microsoft.Extensions.DependencyInjection;
using Sonora.Services;

namespace Sonora.Platforms.Android;

/// <summary>
/// The reason audio survives the app going off screen. Android stops a backgrounded
/// process from mixing sound unless an ongoing foreground service claims the playback,
/// and from API 34 that claim has to be typed as media playback.
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

	public static void Begin(Context context, string title, string artist)
	{
		_title = title;
		_artist = artist;

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

		// Restarted silently if the framework reclaims the process; the page re-reports
		// the position within a second and the notification catches up.
		return StartCommandResult.Sticky;
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
			.AddAction(global::Android.Resource.Drawable.IcMediaPause, "Pause", toggleIntent);

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
