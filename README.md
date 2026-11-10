# Sonora

**Status: beta (0.9).** Search, artist and album pages, playlists you build yourself,
synced lyrics and background playback, all backed by YouTube Music.

## What it is

A .NET MAUI app whose entire interface is a plain HTML/CSS/JavaScript page hosted in a
`HybridWebView`. There is no Blazor, no Razor and no framework in the page: the web
assets in `Resources/Raw/wwwroot` are the whole UI, and everything that needs a network,
a key or a file happens on the C# side behind a small message bridge.

- **Catalogue** — every name, cover and album comes from YouTube Music, never from the
  public YouTube API.
- **Streams** — resolved with YoutubeExplode and re-resolved on failure, resuming at the
  exact second the sound stopped.
- **Lyrics** — time-coded from LRCLIB, following the sung line.
- **Android** — a foreground service keeps audio alive off screen, and the Back button
  unwinds the page instead of leaving it.

## Layout

| Path | What lives there |
| --- | --- |
| `Resources/Raw/wwwroot` | the interface: `index.html`, `app.css`, `app.js` |
| `Services` | catalogue lookup, stream resolution, playback supervision, lyrics |
| `WebViewInterop.cs` | both halves of the bridge between page and host |
| `Platforms/Android` | manifest, activity, playback service |
| `tools` | development helpers, including a Chrome DevTools probe for the live page |

## Building

Windows:

```
dotnet build -f net10.0-windows10.0.19041.0 -c Debug
```

Android, as a standalone APK that carries the .NET runtime with it:

```
dotnet build -f net10.0-android -c Debug -t:SignAndroidPackage -p:EmbedAssembliesIntoApk=true
```

Without `EmbedAssembliesIntoApk=true` the APK is a fast-deployment package and aborts on
launch unless the assemblies are pushed to the device separately.

## Before you use this code

The YouTube Music client library this app depends on is GPL v3.0, which is a claim about
the licence of anything built against it. Sort out what you want the licence of this
repository to be before depending on it in anything you ship.
