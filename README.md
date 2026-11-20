# Sonora

**Status: beta (0.9.1).** Search, artist and album pages, playlists you build yourself,
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
- **Own files** — the music already on the device is listed under *On this device* and plays
  straight from the file, so a saved track of your own still works with no network.
- **Android** — a foreground service keeps audio alive off screen, and the Back button
  unwinds the page instead of leaving it.

## Layout

| Path | What lives there |
| --- | --- |
| `Resources/Raw/wwwroot` | the interface: `index.html`, `app.css`, `app.js` |
| `Services` | catalogue lookup, stream resolution, playback supervision, lyrics, the device's own music |
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

## Licence

GNU General Public License, version 3 only. See `LICENSE`.

That is not a stylistic choice: the YouTube Music client library this app builds against
is GPL v3.0 itself, so anything distributed with it has to carry the same terms.
