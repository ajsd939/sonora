// Mirrors LrcLibService.GetLyricsAsync (exact call, then ranked search fallback) so the
// matching logic can be measured against LRCLIB without running the app.
const UA = 'Sonora/1.0 (MAUI HybridWebView player)';
const MAX_DRIFT = 20;

const esc = s => encodeURIComponent(String(s).trim());
const hasTimeCode = t => !!t && t.includes('[');

function rank(entry, albumName, duration) {
	let score = hasTimeCode(entry.syncedLyrics) ? 0 : 1000;
	if (albumName && entry.albumName && entry.albumName.toLowerCase() !== albumName.toLowerCase()) score += 60;
	if (duration > 1 && typeof entry.duration === 'number') score += Math.min(Math.abs(entry.duration - duration), MAX_DRIFT * 2);
	return score;
}

async function get(artist, title, album, duration) {
	let exactQuery = `api/get?track_name=${esc(title)}&artist_name=${esc(artist)}`;
	if (album) exactQuery += `&album_name=${esc(album)}`;
	if (duration > 1) exactQuery += `&duration=${Math.round(duration)}`;

	let exact = null;
	try {
		const r = await fetch(`https://lrclib.net/${exactQuery}`, { headers: { 'User-Agent': UA } });
		if (r.ok) exact = await r.json();
	} catch { /* transport error */ }

	if (exact && (hasTimeCode(exact.syncedLyrics) || (exact.plainLyrics || '').trim())) {
		return { via: 'exact', synced: hasTimeCode(exact.syncedLyrics), album: exact.albumName, dur: exact.duration };
	}

	const searchUrl = `https://lrclib.net/api/search?track_name=${esc(title)}&artist_name=${esc(artist)}`;
	const response = await fetch(searchUrl, { headers: { 'User-Agent': UA } });
	const hits = await response.json();
	const ranked = hits
		.filter(e => hasTimeCode(e.syncedLyrics) || (e.plainLyrics || '').trim())
		.map(e => ({ e, s: rank(e, album, duration) }))
		.sort((a, b) => a.s - b.s);
	const best = ranked[0];
	return best
		? { via: 'search', hits: hits.length, synced: hasTimeCode(best.e.syncedLyrics), album: best.e.albumName, dur: best.e.duration, name: best.e.trackName }
		: { via: 'none', hits: hits.length };
}

const SONGS = [
	['Adele', 'Easy On Me', '30', 214],
	['Arctic Monkeys', 'Do I Wanna Know', 'AM', 212],
	['Billie Eilish', 'ocean eyes', '', 213],
	['Daft Punk', 'Get Lucky', 'Random Access Memories', 248],
	['Eminem', 'Lose Yourself', '', 326],
	['Frank Ocean', 'Pink + White', 'Blonde', 215],
	['Imagine Dragons', 'Believer', 'Evolve', 204],
	['Kendrick Lamar', 'Not Like Us', '', 258],
	['Linkin Park', 'In The End', 'Hybrid Theory', 216],
	['Lorde', 'Royals', 'Pure Heroine', 180],
	['Muse', 'Hysteria', 'Absolution', 391],
	['Radiohead', 'Creep', 'Pablo Honey', 239],
	['Kanye West', 'Homecoming', '', 204],
	['Kanye West', 'Life Of The Party', '', 160],
	['The Weeknd', 'Blinding Lights', 'After Hours', 200],
	['Taylor Swift', 'Anti-Hero', 'Midnights', 200],
	['Travis Scott', 'FE!N', 'UTOPIA', 187],
	['SZA', 'Kill Bill', 'SOS', 154],
	['Dua Lipa', 'Levitating', 'Future Nostalgia', 203],
	['Queen', "Bohemian Rhapsody", 'A Night at the Opera', 354],
];

for (const [artist, title, album, duration] of SONGS) {
	try {
		const r = await get(artist, title, album, duration);
		console.log(`${(r.synced ? 'synced' : r.via === 'none' ? 'MISS  ' : 'plain ')} ${r.via.padEnd(6)} ${artist} — ${title}` +
			(r.via === 'none' ? `  (0 hits)` : `  [${r.name || title} / ${r.album} / ${r.dur}s]`));
	} catch (error) {
		console.log(`ERROR  ${artist} — ${title}: ${error.message}`);
	}
}
