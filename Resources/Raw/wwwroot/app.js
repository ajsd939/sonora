/* Sonora web shell.
 *
 * Talks to the C#/.NET 10 host through the HybridWebView bridge only. This file never
 * touches YouTube itself: it asks the host for a track, receives a playability URL plus
 * the timestamp to continue from, and keeps the host informed of where the listener is.
 */

'use strict';

/* ------------------------------------------------------------------ configuration */

const HEARTBEAT_MS = 1000;
/** JS waits this long for a frozen clock before asking the host to re-resolve. */
const JS_STALL_MS = 9000;
/** A handed-over URL that never produces audio is itself treated as a stall. */
const RESUME_DEADLINE_MS = 22000;
/** Host runs 5 attempts (each up to 20s) plus backoff; this is the outer backstop. */
const HOST_ANSWER_DEADLINE_MS = 150000;
const SEARCH_DEBOUNCE_MS = 450;
const LIBRARY_LIMIT = 100;
const LIBRARY_KEY = 'sonora.library.v1';
const PLAYLISTS_KEY = 'sonora.playlists.v1';
const SETTINGS_KEY = 'sonora.settings.v1';

const QUICK_PICKS = [
	{ label: 'Lofi beats', query: 'lofi hip hop radio' },
	{ label: 'Deep focus', query: 'deep focus instrumental playlist' },
	{ label: 'Jazz', query: 'jazz piano classics' },
	{ label: 'Classical', query: 'classical music for study' },
	{ label: 'Synthwave', query: 'synthwave mix' },
	{ label: 'Ambient', query: 'ambient soundscapes long set' },
	{ label: 'Afrobeats', query: 'afrobeats mix' },
	{ label: 'Live sets', query: 'live dj set full' },
];

/* ------------------------------------------------------------------ icons (lucide) */

const STROKE = 'fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"';
const SOLID = 'fill="currentColor" stroke="none"';

const ICONS = {
	logo: ['<path d="M6 15V9" />', '<path d="M10 19V5" />', '<path d="M14 17V7" />', '<path d="M18 14v-4" />', '<path d="M22 12h-1" />', '<path d="M3 12h1" />'],
	home: ['<path d="M3 9.5 12 3l9 6.5V20a1 1 0 0 1-1 1h-5v-7H9v7H4a1 1 0 0 1-1-1z" />'],
	back: ['<path d="M19 12H5" />', '<path d="m12 19-7-7 7-7" />'],
	search: ['<circle cx="11" cy="11" r="7" />', '<path d="m20.5 20.5-4-4" />'],
	library: ['<rect x="3" y="3" width="5" height="18" rx="1.2" />', '<rect x="10" y="3" width="5" height="18" rx="1.2" />', '<path d="m17.6 4.6 3.2 16.8" />'],
	music: ['<path d="M9 18V5l11-2v13" />', '<circle cx="6" cy="18" r="3" />', '<circle cx="18" cy="16" r="3" />'],
	play: ['<path d="M7 4.5 19.5 12 7 19.5z" />'],
	pause: ['<path d="M8 4.5h3.2v15H8z" />', '<path d="M12.8 4.5H16v15h-3.2z" />'],
	prev: ['<path d="M19 5.5 9.5 12 19 18.5z" />', '<path d="M6 5v14" />'],
	next: ['<path d="M5 5.5 14.5 12 5 18.5z" />', '<path d="M18 5v14" />'],
	loader: ['<path d="M21 12a9 9 0 1 1-6.2-8.6" />'],
	alert: ['<circle cx="12" cy="12" r="9" />', '<path d="M12 7.5v5" />', '<path d="M12 16.5h.01" />'],
	close: ['<path d="M18 6 6 18" />', '<path d="m6 6 12 12" />'],
	volume: ['<path d="M11 5 6.5 9H3v6h3.5L11 19z" />', '<path d="M15.5 9.2a4 4 0 0 1 0 5.6" />', '<path d="M18.6 6.2a8 8 0 0 1 0 11.6" />'],
	volumeMuted: ['<path d="M11 5 6.5 9H3v6h3.5L11 19z" />', '<path d="m16 9.5 5 5" />', '<path d="m21 9.5-5 5" />'],
	trash: ['<path d="M3.5 6.5h17" />', '<path d="M9.5 6.5V4h5v2.5" />', '<path d="M6 6.5 7 20h10l1-13.5" />'],
	chevronUp: ['<path d="m6 15 6-6 6 6" />'],
	chevronDown: ['<path d="m6 9 6 6 6-6" />'],
	plus: ['<path d="M12 5v14" />', '<path d="M5 12h14" />'],
	expand: ['<path d="M9 4H4v5" />', '<path d="M15 4h5v5" />', '<path d="M9 20H4v-5" />', '<path d="M15 20h5v-5" />'],
	collapse: ['<path d="M4 9h5V4" />', '<path d="M20 9h-5V4" />', '<path d="M4 15h5v5" />', '<path d="M20 15h-5v5" />'],
};

function iconMarkup(name, solid) {
	const paths = ICONS[name] || [];
	return `<svg viewBox="0 0 24 24" ${solid ? SOLID : STROKE} aria-hidden="true">${paths.join('')}</svg>`;
}

/** Icons whose artwork is a filled shape rather than a stroked outline. */
const SOLID_ICONS = new Set(['play', 'pause', 'prev', 'next']);

function setIcon(element, name) {
	if (!element) return;
	element.dataset.icon = name;
	element.innerHTML = iconMarkup(name, SOLID_ICONS.has(name));
}

function hydrateIcons(root) {
	root.querySelectorAll('[data-icon]').forEach((element) => {
		setIcon(element, element.dataset.icon);
	});
}

/* ------------------------------------------------------------------ dom */

const els = {};
['greeting', 'quickPicks', 'homeGrid', 'homeStatus', 'homeContinue', 'homeContinueGrid',
	'searchInput', 'searchSubmit', 'searchTabs', 'searchResults', 'searchStatus', 'searchEmpty',
	'albumHero', 'albumTracks', 'artistHero', 'artistBody', 'btnBack',
	'libraryList', 'libraryEmpty', 'libraryClear', 'viewTitle', 'scroller',
	'libraryPlaylistGrid', 'libraryPlaylistNote', 'playlistNew',
	'playlistHero', 'playlistTracks',
	'playlistModal', 'playlistPickerSong', 'playlistPickerList', 'playlistNameInput',
	'playlistCreate', 'playlistPickerDismiss',
	'npCover', 'npTitle', 'npArtist', 'timeCurrent', 'timeTotal', 'seek',
	'volume', 'btnMute', 'btnPlay', 'btnPrev', 'btnNext', 'recoverState', 'recoverStateText',
	'toasts', 'errorModal', 'errorModalTitle', 'errorModalBody', 'errorModalDismiss', 'errorModalRetry',
	'openPlayer', 'playerExpand', 'audio',
].forEach((id) => { els[id] = document.getElementById(id); });

/* The bar and the full-screen player show the same thing, so both are painted from one
   place and both accept the same gestures. */
['playerView', 'playerWash', 'playerArt', 'playerCover', 'playerTitle', 'playerArtist', 'playerAlbum',
	'playerTimeCurrent', 'playerTimeTotal', 'playerSeek', 'playerPlay', 'playerPrev', 'playerNext',
	'playerVolume', 'playerMute', 'playerClose', 'playerLyrics', 'playerLyricsSource',
	'playerInner', 'lyricsFullToggle', 'lyricsFullIcon',
].forEach((id) => { els[id] = document.getElementById(id); });

const pairs = {
	seek: [els.seek, els.playerSeek],
	timeCurrent: [els.timeCurrent, els.playerTimeCurrent],
	timeTotal: [els.timeTotal, els.playerTimeTotal],
	volume: [els.volume, els.playerVolume],
	muteIcon: [els.btnMute, els.playerMute],
	playIcon: [els.btnPlay, els.playerPlay],
};

const views = {
	home: document.getElementById('view-home'),
	search: document.getElementById('view-search'),
	library: document.getElementById('view-library'),
	album: document.getElementById('view-album'),
	artist: document.getElementById('view-artist'),
	playlist: document.getElementById('view-playlist'),
};

const VIEW_TITLES = { home: 'Home', search: 'Search', library: 'Your Library' };

/** Views the tab bar can open; anything else was reached by tapping a card. */
const TAB_VIEWS = new Set(['home', 'search', 'library']);

/* ------------------------------------------------------------------ host bridge */

const Bridge = {
	get available() {
		return typeof window.HybridWebView !== 'undefined'
			&& window.HybridWebView
			&& typeof window.HybridWebView.InvokeDotNet === 'function';
	},

	/** Awaitable call of a C# method exposed by SonoraBridgeTarget. */
	invoke(name, args) {
		if (!this.available) {
			return Promise.reject(new Error('Sonora host bridge is unavailable'));
		}
		return window.HybridWebView.InvokeDotNet(name, args || []);
	},

	/** Fire-and-forget call, used for the high-frequency heartbeat. */
	fire(name, args) {
		if (!this.available) return;
		this.invoke(name, args).catch((error) => console.warn(`[bridge] ${name} failed`, error));
	},

	search(query, limit) { return this.invoke('Search', [query, limit]); },
	searchMusic(query, limit) { return this.invoke('SearchMusic', [query, limit]); },
	openArtist(channelId, avatarUrl) { return this.invoke('OpenArtist', [channelId, avatarUrl]); },
	openAlbum(id) { return this.invoke('OpenAlbum', [id]); },
	play(videoRef) { return this.invoke('Play', [videoRef]); },
	lyrics(artist, title, album, durationSeconds) { return this.invoke('Lyrics', [artist, title, album ?? null, durationSeconds ?? 0]); },
	reportPosition(report) { this.fire('ReportPosition', [report]); },
	reportStall(report) { this.fire('ReportStall', [report]); },
	pause() { this.fire('Pause', []); },
	exit() { this.fire('Exit', []); },
};

/* ------------------------------------------------------------------ state */

const state = {
	view: 'home',
	videoId: null,
	track: null,
	queue: [],
	queueIndex: -1,
	duration: 0,

	/** true between setting audio.src and the element actually reporting a clock. */
	awaitingResume: false,
	resumeAt: 0,
	resumeDeadline: 0,

	/** a stall has been reported and the host has not answered yet. */
	recovering: false,

	userPaused: false,
	lastClock: 0,
	lastKnownSeconds: 0,
	frozenMs: 0,
	isScrubbing: false,
	searchTimer: 0,

	/** the last result page, so switching a tab never costs another request. */
	searchTab: 'all',
	searchPage: null,

	/** where the listener came from before opening an album or an artist. */
	history: [],

	/** the own-playlist page currently on screen. */
	openPlaylistId: null,

	/** the song the plus was pressed on, while the picker sheet is open. */
	pickerTrack: null,

	/** true while the full-screen player is covering the app. */
	playerOpen: false,
};

/**
 * Requests overtake each other (typing, then tapping). Each new request takes the token,
 * and a late answer that no longer holds it is thrown away instead of painting the wrong
 * page.
 */
let renderToken = 0;

/* ------------------------------------------------------------------ persistence */

/**
 * DOM storage is not guaranteed inside every native web view, so every access is
 * contained: the library degrades to this session only rather than breaking playback.
 */
const store = {
	read(key, fallback) {
		try {
			const raw = window.localStorage.getItem(key);
			return raw ? JSON.parse(raw) : fallback;
		} catch (error) {
			console.warn('[storage] read failed', error);
			return fallback;
		}
	},
	write(key, value) {
		try {
			window.localStorage.setItem(key, JSON.stringify(value));
			return true;
		} catch (error) {
			console.warn('[storage] write failed', error);
			return false;
		}
	},
};

let library = store.read(LIBRARY_KEY, []);
let playlists = store.read(PLAYLISTS_KEY, []).filter((list) => list && list.id && Array.isArray(list.items));
let settings = store.read(SETTINGS_KEY, { volume: 1, muted: false });

/* ------------------------------------------------------------------ helpers */

function clamp(value, max) {
	if (!Number.isFinite(value) || value < 0) return 0;
	return max && Number.isFinite(max) && value > max ? max : value;
}

function formatTime(seconds) {
	const total = Math.max(0, Math.floor(Number(seconds) || 0));
	const h = Math.floor(total / 3600);
	const m = Math.floor((total % 3600) / 60);
	const s = total % 60;
	return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`;
}

function toast(message, kind) {
	const node = document.createElement('div');
	node.className = `toast toast-${kind || 'info'}`;
	node.textContent = message;
	els.toasts.appendChild(node);
	setTimeout(() => {
		node.classList.add('toast-leaving');
		setTimeout(() => node.remove(), 260);
	}, 3000);
}

/** Element with a class and, optionally, literal text — text never comes from markup. */
function el(tag, className, text) {
	const node = document.createElement(tag);
	if (className) node.className = className;
	if (text !== undefined && text !== null) node.textContent = text;
	return node;
}

const GRID_CARDS = 'grid-cards';
const GRID_ARTISTS = 'grid-artists';
const STACK_ROWS = 'rows';

function imageNode(url, className) {
	const image = document.createElement('img');
	image.loading = 'lazy';
	image.decoding = 'async';
	image.alt = '';
	image.className = className || 'art-fill';
	image.src = url;
	return image;
}

/** Artwork frame with a music note standing in for a missing cover. */
function coverNode(url, classes) {
	const frame = el('div', `cover ${classes || 'row-art'}`);
	if (url) {
		frame.appendChild(imageNode(url));
		return frame;
	}
	const glyph = el('span', 'ic');
	frame.appendChild(glyph);
	setIcon(glyph, 'music');
	return frame;
}

function spinnerNode() {
	const wrap = el('div', 'loading');
	const glyph = el('span', 'ic spin');
	setIcon(glyph, 'loader');
	wrap.append(glyph, el('span', '', 'Loading…'));
	return wrap;
}

function noticeNode(text) {
	return el('p', 'notice', text);
}

/** A named block of cards or rows. An empty shelf is no shelf at all, so it is skipped. */
function shelf(heading, nodes, layout) {
	if (!nodes.length) return null;
	const wrap = el('div', 'shelf');
	wrap.appendChild(el('h2', 'shelf-h', heading));
	const container = el('div', layout || GRID_CARDS);
	nodes.forEach((node) => container.appendChild(node));
	wrap.appendChild(container);
	return wrap;
}

function appendShelves(container, shelves) {
	container.append(...shelves.filter(Boolean));
}

function joinBits(bits) {
	return bits.filter((bit) => bit !== undefined && bit !== null && bit !== '').join(' · ');
}

function showRecovery(text) {
	els.recoverStateText.textContent = text;
	els.recoverState.classList.remove('hidden');
}

function hideRecovery() {
	els.recoverState.classList.add('hidden');
}

/* ------------------------------------------------------------------ audio */

function setTrack(track) {
	if (!track) return;
	state.track = track;
	state.duration = track.durationSeconds || state.duration;
	els.npTitle.textContent = track.title || 'Unknown title';
	els.npArtist.textContent = track.artist || 'Unknown artist';
	document.title = `${track.title} · Sonora`;
	renderCover(track.thumbnailUrl);
	paintTotal();
	paintPlayer(track);
	if ('mediaSession' in navigator) {
		navigator.mediaSession.metadata = new MediaMetadata({
			title: track.title || '',
			artist: track.artist || '',
			album: track.albumName || '',
			artwork: track.thumbnailUrl ? [{ src: track.thumbnailUrl, sizes: '512x512', type: 'image/jpeg' }] : [],
		});
	}
	ensureLyrics(track);
}

function renderCover(url) {
	els.npCover.innerHTML = '';
	if (!url) {
		const placeholder = document.createElement('span');
		placeholder.className = 'ic';
		setIcon(placeholder, 'music');
		els.npCover.appendChild(placeholder);
		return;
	}
	const image = document.createElement('img');
	image.src = url;
	image.alt = '';
	image.className = 'art-fill';
	els.npCover.appendChild(image);
}

/** Fills the full-screen player: names, artwork, and the colour wash behind it. */
function paintPlayer(track) {
	els.playerTitle.textContent = track.title || 'Unknown title';
	els.playerArtist.textContent = track.artist || 'Unknown artist';
	els.playerAlbum.textContent = joinBits([track.albumName, track.playsInfo]);
	if (track.thumbnailUrl) {
		els.playerCover.src = track.thumbnailUrl;
		els.playerCover.hidden = false;
		els.playerWash.style.backgroundImage = `url("${track.thumbnailUrl}")`;
	} else {
		// An empty src would make the browser reload the host page as if it were a picture.
		els.playerCover.removeAttribute('src');
		els.playerCover.hidden = true;
		els.playerWash.style.backgroundImage = '';
	}
}

/**
 * The one message that carries sound: bind the new URL, seek straight to the timestamp
 * the host was told about, and resume. Identical code path for a first play and for a
 * recovery after the previous stream dropped.
 */
function applyStream(payload) {
	if (!payload || !payload.url) return;

	state.recovering = false;
	hideRecovery();
	state.awaitingResume = true;
	state.resumeAt = clamp(payload.resumePositionSeconds);
	state.resumeDeadline = Date.now() + RESUME_DEADLINE_MS;
	state.videoId = payload.videoId || state.videoId;
	state.frozenMs = 0;
	state.lastClock = 0;

	if (payload.track) setTrack(payload.track);

	if (payload.autoResumed) {
		toast(state.resumeAt > 0.5 ? `Back at ${formatTime(state.resumeAt)}` : 'Up and playing again', 'ok');
	}
	hideErrorModal();

	els.audio.src = payload.url;
	els.audio.load();
}

function playFromResumePoint() {
	const audio = els.audio;
	const target = clamp(state.resumeAt, Number.isFinite(audio.duration) ? audio.duration : 0);

	if (target > 0.5) {
		try {
			audio.currentTime = target;
		} catch (error) {
			// YouTube ranges are usually seekable at loadedmetadata; if this one is not,
			// continuing from zero beats stopping the listener entirely.
			console.warn('[player] resume seek failed', error);
		}
	}

	state.awaitingResume = false;
	state.resumeAt = 0;

	if (!state.userPaused) {
		// Calling play() before the element reports ready is fine: play() returns a
		// promise and the element starts as soon as it can.
		els.audio.play().catch((error) => {
			console.warn('[player] autoplay refused', error);
			state.userPaused = true;
			paintTransport();
			toast('Tap play to continue', 'warn');
		});
	}
	paintTransport();
}

/**
 * Reports a broken stream to the host and waits for it to hand back a fresh URL.
 * Unless `force` is set, a single failure produces exactly one recovery cycle.
 */
function reportStall(reason, force) {
	if (!state.videoId) return;
	if (!force && (state.recovering || state.awaitingResume)) return;

	state.awaitingResume = false;
	state.recovering = true;
	state.frozenMs = 0;
	showRecovery('Reconnecting…');
	Bridge.reportStall({
		videoId: state.videoId,
		currentSeconds: bestKnownSeconds(),
		reason,
	});

	// The host is expected to answer with either a new URL or a terminal error. If that
	// answer never lands, clear the flag so a later stall can still be reported.
	setTimeout(() => {
		if (state.recovering) {
			state.recovering = false;
			hideRecovery();
			toast('Still quiet? Tap play to try again.', 'warn');
		}
	}, HOST_ANSWER_DEADLINE_MS);
}

/** The element clock is authoritative; fall back to the last position ever observed. */
function bestKnownSeconds() {
	const clock = Number.isFinite(els.audio.currentTime) ? els.audio.currentTime : 0;
	if (clock > 0) return clock;
	return Math.max(state.lastKnownSeconds || 0, state.resumeAt || 0);
}

function userPlay() {
	if (!state.videoId) return;
	state.userPaused = false;
	paintTransport();

	if (!els.audio.src) {
		Bridge.play(state.videoId).catch(() => toast('Give that a tap again in a second.', 'warn'));
		return;
	}
	els.audio.play().catch(() => reportStall('resume refused by the element'));
}

function userPause() {
	state.userPaused = true;
	els.audio.pause();
	Bridge.pause();
	paintTransport();
}

function togglePlay() {
	if (state.userPaused || els.audio.paused) userPlay(); else userPause();
}

function seekTo(seconds) {
	const audio = els.audio;
	if (!state.videoId || audio.readyState < 1) return;
	const duration = Number.isFinite(audio.duration) ? audio.duration : state.duration;
	const target = clamp(seconds, duration ? Math.max(0, duration - 0.5) : 0);
	state.resumeAt = target;
	try {
		audio.currentTime = target;
	} catch (error) {
		console.warn('[player] seek failed', error);
	}
	paintProgress();
}

/** Plays a track through the host; index is remembered so next/previous work. */
async function playTrack(track, queue, index) {
	if (!track) return;
	if (!Bridge.available) {
		showErrorModal(track.title, 'Music only plays inside the Sonora app, and this page is not running in it.');
		return;
	}

	state.queue = Array.isArray(queue) ? queue : [track];
	state.queueIndex = Number.isInteger(index) ? index : 0;
	state.userPaused = false;
	state.recovering = false;
	hideRecovery();
	showRecovery('Loading…');
	paintTransport();

	try {
		const resolved = await Bridge.play(track.videoId);
		// The host answers with the YouTube Music page for this id, so its names win.
		// The tapped card is kept as the fallback for everything the page left blank and
		// for the artwork it already showed, so the cover never changes under the listener.
		const playing = {
			...track,
			...(resolved || {}),
			title: resolved?.title || track.title,
			artist: resolved?.artist || track.artist,
			durationSeconds: resolved?.durationSeconds || track.durationSeconds,
			thumbnailUrl: track.thumbnailUrl || resolved?.thumbnailUrl,
			albumName: track.albumName || resolved?.albumName,
			albumId: track.albumId || resolved?.albumId,
		};
		setTrack(playing);
		saveToLibrary(playing);
	} catch (error) {
		console.warn('[player] start failed', error);
		hideRecovery();
		showErrorModal(track.title, "We couldn't start this one. It may not be available right now.");
	}
}

function playRelative(delta) {
	const next = state.queueIndex + delta;
	if (!state.queue.length || next < 0 || next >= state.queue.length) {
		if (delta > 0) toast('That was the last track.', 'info');
		return;
	}
	playTrack(state.queue[next], state.queue, next);
}

/* ------------------------------------------------------------------ full-screen player */

function openPlayer() {
	if (state.playerOpen) return;
	state.playerOpen = true;
	els.playerView.hidden = false;
	// The panel was painted while hidden, so re-run the clock to put the listener on
	// the line that is being sung right now.
	lyricIndex = -1;
	lyricHoldUntil = 0;
	paintProgress();
	ensureLyrics(state.track);
}

function closePlayer() {
	if (!state.playerOpen) return;
	state.playerOpen = false;
	els.playerView.hidden = true;
	showLyricsInline();
}

/**
 * On a phone the words can fill the screen on their own, which leaves the transport one
 * tap away. A wide screen already shows both halves, so the control is hidden there and
 * the class would only be left over from a narrow window.
 */
function setLyricsFullscreen(on) {
	els.playerView.classList.toggle('lyrics-full', on);
	setIcon(els.lyricsFullIcon, on ? 'collapse' : 'expand');
	els.lyricsFullToggle.setAttribute(
		'aria-label',
		on ? 'Show the cover and controls' : 'Show lyrics full screen');

	// The screen just changed shape under the listener, so put the sung line back in it.
	const node = lyricNodes[lyricIndex];
	if (node) scrollLyricLineIntoView(node);
}

function showLyricsInline() {
	if (els.playerView.classList.contains('lyrics-full')) setLyricsFullscreen(false);
}

function toggleLyricsFullscreen() {
	setLyricsFullscreen(!els.playerView.classList.contains('lyrics-full'));
}

/* ------------------------------------------------------------------ lyrics */

const LYRIC_HOLD_MS = 4000;
let lyricLines = [];
let lyricNodes = [];
let lyricIndex = -1;
/** the track the pane is showing words for */
let lyricVideoId = null;
/** the track a request is in flight for, so a double tap does not ask twice */
let lyricBusyFor = null;
let lyricsToken = 0;
let lyricHoldUntil = 0;

/**
 * Only the track on screen gets lyrics. A pane that came back empty is not a pane worth
 * keeping, so reopening the player asks the host again instead of holding the failure.
 */
function ensureLyrics(track) {
	if (!state.playerOpen || !Bridge.available || !track || !track.videoId) return;
	if (track.videoId === lyricVideoId || track.videoId === lyricBusyFor) return;
	loadLyrics(track);
}

/** The length LRCLIB needs: the catalogue value, else whatever the stream reported. */
function lyricDuration(track) {
	const fromAudio = Number.isFinite(els.audio.duration) ? els.audio.duration : 0;
	return track.durationSeconds || state.duration || fromAudio || 0;
}

async function loadLyrics(track) {
	const token = ++lyricsToken;
	lyricBusyFor = track.videoId;
	resetLyrics();
	lyricsNote('Looking for lyrics…');

	let data = null;
	try {
		data = await Bridge.lyrics(track.artist || '', track.title || '', track.albumName, lyricDuration(track));
	} catch (error) {
		console.warn('[lyrics] lookup failed', error);
	}

	if (lyricBusyFor === track.videoId) lyricBusyFor = null;

	// A newer track has taken the player in the meantime; this answer is stale.
	if (token !== lyricsToken) return;

	if (!data || !data.text) {
		lyricsNote('No lyrics for this one.');
		return;
	}

	// Leaving the note layout behind: words are a scrolling column, not a centred line.
	els.playerLyrics.classList.remove('showing-note');

	if (data.synced) {
		lyricLines = parseLrc(data.text);
		if (lyricLines.length) {
			els.playerLyrics.innerHTML = '';
			els.playerLyrics.classList.add('is-synced');
			lyricNodes = lyricLines.map((line) => {
				const node = el('div', 'lyric', line.text);
				node.addEventListener('click', () => seekTo(line.t));
				return node;
			});
			els.playerLyrics.append(...lyricNodes);
			els.playerLyricsSource.textContent = 'LRCLIB · tap a line to jump';
			lyricVideoId = track.videoId;
			paintProgress();
			return;
		}
	}

	// Plain text, or time codes that turned out to cover no words at all.
	els.playerLyrics.innerHTML = '';
	lyricLines = [];
	lyricNodes = data.text.split(/\r?\n/).filter((line) => line.trim()).map((line) => el('div', 'lyric', line.trim()));
	els.playerLyrics.append(...lyricNodes);
	els.playerLyricsSource.textContent = 'LRCLIB';
	lyricVideoId = track.videoId;
}

function resetLyrics() {
	lyricLines = [];
	lyricNodes = [];
	lyricIndex = -1;
	els.playerLyrics.innerHTML = '';
	els.playerLyrics.classList.remove('is-synced');
	els.playerLyrics.classList.remove('showing-note');
	els.playerLyricsSource.textContent = '';
}

function lyricsNote(text) {
	els.playerLyrics.innerHTML = '';
	els.playerLyrics.classList.add('showing-note');
	els.playerLyrics.appendChild(el('p', 'lyrics-note', text));
}

/** LRC: one line, any number of stamps, and repeated stamps all point at the same words. */
function parseLrc(text) {
	const stamp = /\[(\d{1,2}):([0-5]?\d)(?:[.:](\d{1,3}))?\]/g;
	const lines = [];

	text.split(/\r?\n/).forEach((raw) => {
		const times = [];
		let match;
		while ((match = stamp.exec(raw)) !== null) {
			const fraction = match[3] ? Number(match[3]) / 10 ** match[3].length : 0;
			times.push(Number(match[1]) * 60 + Number(match[2]) + fraction);
		}
		if (!times.length) return;

		const words = raw.replace(stamp, '').trim();
		if (!words) return;
		times.forEach((t) => lines.push({ t, text: words }));
	});

	return lines.sort((a, b) => a.t - b.t);
}

/**
 * Walks out from the highlighted line rather than scanning the whole page, so a
 * three-minute song with sixty lines costs two comparisons per second.
 */
function paintLyricClock(position) {
	if (!lyricLines.length) return;

	let index = lyricIndex;
	while (index + 1 < lyricLines.length && lyricLines[index + 1].t <= position) index += 1;
	while (index >= 0 && lyricLines[index].t > position) index -= 1;
	if (index === lyricIndex) return;

	lyricIndex = index;
	lyricNodes.forEach((node, i) => {
		node.classList.toggle('on', i === index);
		node.classList.toggle('past', i < index);
	});

	// Scrrolling by hand wins until the listener stops touching the panel.
	if (index >= 0 && Date.now() > lyricHoldUntil) scrollLyricLineIntoView(lyricNodes[index]);
}

/**
 * The words scroll inside their own panel on a wide screen and inside the whole player
 * column on a phone, so the clock asks which of the two actually has spare height.
 */
function scrollLyricLineIntoView(node) {
	const panel = els.playerLyrics;
	const inPanel = panel.scrollHeight > panel.clientHeight + 8;
	const scroller = inPanel ? panel : els.playerInner;

	// Measured against the scroller's own box rather than offsetTop, because the player
	// column carries top padding for the status bar and offsetTop ignores it.
	const offset = scroller.scrollTop
		+ node.getBoundingClientRect().top - scroller.getBoundingClientRect().top;

	scroller.scrollTo({ top: offset - (scroller.clientHeight - node.offsetHeight) / 2, behavior: 'smooth' });
}

function holdLyrics() {
	lyricHoldUntil = Date.now() + LYRIC_HOLD_MS;
}

/* ------------------------------------------------------------------ host messages */

function handleHostMessage(raw) {
	let message;
	try {
		message = JSON.parse(raw);
	} catch (error) {
		console.warn('[bridge] unparsable message', raw);
		return;
	}

	const payload = message.payload || {};

	switch (message.type) {
		case 'streamReady':
			applyStream(payload);
			break;

		case 'retryStarted':
			showRecovery(payload.attempt > 1 ? 'Still looking…' : 'Looking for another copy…');
			break;

		case 'playbackError':
			state.recovering = false;
			state.awaitingResume = false;
			state.userPaused = true;
			hideRecovery();
			paintTransport();
			showErrorModal(
				state.track ? state.track.title : 'This track',
				"We tried several times and couldn't get this playing. It may be private, blocked where you are, or gone.",
			);
			break;

		case 'back':
			handleBack();
			break;

		case 'remoteToggle':
			togglePlay();
			break;

		default:
			console.warn('[bridge] unknown message type', message.type);
	}
}

/**
 * Android's Back button unwinds the interface instead of leaving it, in the same order
 * Escape does, and only asks the host to close the app once nothing is left to undo.
 */
function handleBack() {
	if (!els.errorModal.classList.contains('hidden')) {
		hideErrorModal();
		return;
	}
	if (!els.playlistModal.classList.contains('hidden')) {
		hidePlaylistPicker();
		return;
	}
	if (state.playerOpen) {
		if (els.playerView.classList.contains('lyrics-full')) {
			toggleLyricsFullscreen();
			return;
		}
		closePlayer();
		return;
	}
	if (state.history.length) {
		goBack();
		return;
	}
	Bridge.exit();
}

function showErrorModal(title, body) {
	els.errorModalTitle.textContent = title || '';
	els.errorModalBody.textContent = body || '';
	els.errorModal.classList.remove('hidden');
}

function hideErrorModal() {
	els.errorModal.classList.add('hidden');
}

/* ------------------------------------------------------------------ rendering */

/** Creates a circular icon button with the glyph centred inside it. */
function iconButton(classes, iconName, ariaLabel) {
	const button = document.createElement('button');
	button.type = 'button';
	button.className = classes;
	if (ariaLabel) button.setAttribute('aria-label', ariaLabel);
	const glyph = document.createElement('span');
	glyph.className = 'ic';
	setIcon(glyph, iconName);
	button.appendChild(glyph);
	return button;
}

function cardNode(track, queue, index) {
	const button = document.createElement('button');
	button.type = 'button';
	button.className = 'card';

	const frame = el('div', 'cover card-art');
	if (track.thumbnailUrl) frame.appendChild(imageNode(track.thumbnailUrl));

	// Always visible on touch, revealed on hover for pointer devices.
	frame.appendChild(iconButton('play-badge', 'play', `Play ${track.title || 'track'}`));

	button.append(
		frame,
		el('p', 'card-title', track.title || 'Untitled'),
		el('p', 'card-sub', track.artist || 'Unknown artist'),
		el('p', 'card-note', track.durationSeconds ? formatTime(track.durationSeconds) : ''),
	);
	button.addEventListener('click', () => playTrack(track, queue, index));
	return button;
}

function renderGrid(container, tracks, statusNode, emptyLabel) {
	container.innerHTML = '';
	if (statusNode) statusNode.textContent = '';

	if (!tracks.length) {
		if (statusNode) statusNode.textContent = emptyLabel;
		return;
	}

	const fragment = document.createDocumentFragment();
	tracks.forEach((track, index) => fragment.appendChild(cardNode(track, tracks, index)));
	container.appendChild(fragment);
}

/**
 * A single playable line. The album view asks for track numbers; everywhere else the
 * column stays empty so the rows still line up.
 */
function songRow(track, queue, index, numbered, hideAdd) {
	const row = el('div', 'row');
	row.setAttribute('role', 'button');
	row.tabIndex = 0;

	if (numbered) {
		row.appendChild(el('span', 'row-num', String(track.trackNumber || index + 1)));
	}

	row.appendChild(coverNode(track.thumbnailUrl));

	const meta = el('div', 'row-meta');
	meta.appendChild(el('p', 'row-title', track.title || 'Untitled'));
	meta.appendChild(el('p', 'row-sub', joinBits([track.artist || 'Unknown artist', numbered ? null : track.albumName])));
	row.appendChild(meta);

	if (track.playsInfo) {
		row.appendChild(el('span', 'row-note', track.playsInfo));
	}
	if (track.durationSeconds) {
		row.appendChild(el('span', 'row-time', formatTime(track.durationSeconds)));
	}

	if (!hideAdd) {
		const add = iconButton('icon-round row-add', 'plus', `Add ${track.title || 'track'} to a playlist`);
		add.addEventListener('click', (event) => {
			event.stopPropagation();
			showPlaylistPicker(track);
		});
		row.appendChild(add);
	}

	const play = iconButton('play-badge', 'play', `Play ${track.title || 'track'}`);
	play.addEventListener('click', (event) => {
		event.stopPropagation();
		playTrack(track, queue, index);
	});
	row.appendChild(play);
	row.addEventListener('click', () => playTrack(track, queue, index));
	row.addEventListener('keydown', (event) => {
		if (event.key === 'Enter' || event.key === ' ') {
			event.preventDefault();
			playTrack(track, queue, index);
		}
	});
	return row;
}

function albumCard(album) {
	const button = el('button', 'card');
	button.type = 'button';
	button.appendChild(coverNode(album.coverUrl, 'card-art'));
	button.appendChild(el('p', 'card-title', album.title || 'Untitled'));
	button.appendChild(el('p', 'card-sub', joinBits([album.artist || 'Unknown artist', album.year ? String(album.year) : null])));
	button.appendChild(el('p', 'card-note', album.isSingle ? 'Single' : 'Album'));
	button.addEventListener('click', () => openAlbum(album.id, album.title));
	return button;
}

function artistCard(artist) {
	const button = el('button', 'card card-centred');
	button.type = 'button';
	button.appendChild(coverNode(artist.avatarUrl, 'avatar'));
	button.appendChild(el('p', 'card-title', artist.name || 'Artist'));
	button.appendChild(el('p', 'card-sub', joinBits([artist.popularityInfo, artist.subscribersInfo]) || 'Artist'));
	button.addEventListener('click', () => openArtist(artist.channelId, artist.name, artist.avatarUrl));
	return button;
}

/** The large card at the top of a mixed search result. */
function featuredNode(track, queue) {
	const button = el('button', 'featured');
	button.type = 'button';
	button.appendChild(coverNode(track.thumbnailUrl, 'featured-art'));

	const meta = el('div', 'row-meta');
	meta.appendChild(el('p', 'hero-eyebrow', 'Song'));
	meta.appendChild(el('h3', 'featured-title', track.title || 'Untitled'));
	meta.appendChild(el('p', 'row-sub', joinBits([track.artist, track.albumName, track.playsInfo])));
	button.appendChild(meta);

	const playCircle = el('span', 'play-md');
	const glyph = el('span', 'ic-lg');
	setIcon(glyph, 'play');
	playCircle.appendChild(glyph);
	button.appendChild(playCircle);
	button.addEventListener('click', () => playTrack(track, queue, 0));
	return button;
}

/**
 * One request per keystroke, three result sets per request. The plain track search is the
 * fallback: it answers when the music catalogue is having a bad day, and its rows carry
 * artwork too.
 */
async function fetchPage(query) {
	let page = null;
	try {
		page = await Bridge.searchMusic(query, 18);
	} catch (error) {
		console.warn('[search] catalogue lookup failed', error);
	}

	let songs = page && Array.isArray(page.songs) ? page.songs : [];
	if (!songs.length) {
		try {
			songs = await Bridge.search(query, 18) || [];
		} catch (error) {
			console.warn('[search] track lookup failed', error);
			songs = [];
		}
	}

	return {
		query,
		songs,
		albums: (page && page.albums) || [],
		artists: (page && page.artists) || [],
	};
}

/**
 * `home` keeps the old flat grid for the quick-pick chips; the search view gets the full
 * tabbed page instead.
 */
async function runSearch(query, options) {
	const opts = options || {};
	const grid = opts.grid || null;
	const status = opts.status || els.searchStatus;
	const trimmed = (query || '').trim();
	if (!trimmed) return;
	if (!Bridge.available) {
		status.textContent = 'Music only plays inside the Sonora app.';
		return;
	}

	const token = ++renderToken;
	status.textContent = `Looking for “${trimmed}”…`;
	if (grid) grid.innerHTML = '';
	else els.searchResults.innerHTML = '';

	const page = await fetchPage(trimmed);
	if (token !== renderToken) return;
	status.textContent = '';

	if (grid) {
		renderGrid(grid, page.songs, status, `Nothing found for “${trimmed}”.`);
		return;
	}

	els.searchEmpty.hidden = true;
	els.searchTabs.hidden = false;
	state.searchPage = page;
	state.searchTab = 'all';
	renderSearchPage();
}

/** Draws the active tab out of the page already held in state — no new request. */
function renderSearchPage() {
	const page = state.searchPage;
	const target = els.searchResults;
	target.innerHTML = '';
	if (!page) return;

	paintSearchTabs(page);

	const songRows = page.songs.slice(0, 8).map((track, index) => songRow(track, page.songs, index, false));
	const albumCards = page.albums.map((album) => albumCard(album));
	const artistCards = page.artists.map((artist) => artistCard(artist));

	if (state.searchTab === 'all') {
		const featured = page.songs.length ? featuredNode(page.songs[0], page.songs) : null;
		const topSongs = page.songs.slice(1, 6).map((track, index) => songRow(track, page.songs, index + 1, false));
		target.append(...[featured, shelf('Songs', topSongs, STACK_ROWS)].filter(Boolean));
		appendShelves(target, [
			shelf('Albums', albumCards, GRID_CARDS),
			shelf('Artists', artistCards, GRID_ARTISTS),
		]);
		if (!featured && !albumCards.length && !artistCards.length) {
			target.appendChild(noticeNode(`Nothing found for “${page.query}”.`));
		}
		return;
	}

	if (state.searchTab === 'songs') {
		appendShelves(target, [shelf('Songs', songRows, STACK_ROWS)]);
		return;
	}
	if (state.searchTab === 'albums') {
		appendShelves(target, [shelf('Albums', albumCards, GRID_CARDS)]);
		return;
	}
	appendShelves(target, [shelf('Artists', artistCards, GRID_ARTISTS)]);
}

/** A tab with nothing in it is a tab that should not invite a dead end. */
function paintSearchTabs(page) {
	const counts = { all: 1, songs: page.songs.length, albums: page.albums.length, artists: page.artists.length };
	els.searchTabs.querySelectorAll('.search-tab').forEach((button) => {
		const tab = button.dataset.tab;
		button.hidden = !counts[tab];
		const active = tab === state.searchTab;
		button.classList.toggle('on', active);
	});
}

function renderLibrary() {
	els.libraryList.innerHTML = '';
	els.libraryEmpty.hidden = library.length > 0;

	library.forEach((entry, index) => {
		const row = el('div', 'row');
		row.setAttribute('role', 'button');
		row.tabIndex = 0;

		row.appendChild(coverNode(entry.thumbnailUrl));

		const meta = el('div', 'row-meta');
		meta.appendChild(el('p', 'row-title', entry.title || 'Untitled'));
		meta.appendChild(el('p', 'row-sub', entry.lastPositionSeconds > 5
			? `${entry.artist || 'Unknown'} · stopped at ${formatTime(entry.lastPositionSeconds)}`
			: (entry.artist || 'Unknown')));
		row.appendChild(meta);

		if (entry.durationSeconds) {
			row.appendChild(el('span', 'row-time', formatTime(entry.durationSeconds)));
		}

		const play = iconButton('play-badge', 'play', `Play ${entry.title || 'track'}`);
		play.addEventListener('click', (event) => {
			event.stopPropagation();
			playTrack(entry, library, index);
		});
		row.appendChild(play);

		const add = iconButton('icon-round row-add', 'plus', `Add ${entry.title || 'track'} to a playlist`);
		add.addEventListener('click', (event) => {
			event.stopPropagation();
			showPlaylistPicker(entry);
		});
		row.appendChild(add);

		const remove = iconButton('icon-round', 'trash', 'Remove from library');
		remove.addEventListener('click', (event) => {
			event.stopPropagation();
			library.splice(index, 1);
			store.write(LIBRARY_KEY, library);
			renderLibrary();
			renderHomeContinue();
		});
		row.appendChild(remove);

		const open = () => playTrack(entry, library, index);
		row.addEventListener('click', open);
		row.addEventListener('keydown', (event) => {
			if (event.key === 'Enter' || event.key === ' ') {
				event.preventDefault();
				open();
			}
		});

		els.libraryList.appendChild(row);
	});
}

function renderHomeContinue() {
	const recent = library.slice(0, 5);
	els.homeContinue.hidden = recent.length === 0;
	els.homeContinueGrid.innerHTML = '';
	const fragment = document.createDocumentFragment();
	recent.forEach((entry, index) => fragment.appendChild(cardNode(entry, recent, index)));
	els.homeContinueGrid.appendChild(fragment);
}

function renderQuickPicks() {
	els.quickPicks.innerHTML = '';
	QUICK_PICKS.forEach((pick) => {
		const chip = el('button', 'chip', pick.label);
		chip.type = 'button';
		chip.addEventListener('click', () => {
			els.scroller.scrollTop = 0;
			runSearch(pick.query, { grid: els.homeGrid, status: els.homeStatus });
		});
		els.quickPicks.appendChild(chip);
	});
}

/* ------------------------------------------------------------------ album and artist pages */

const albumCache = new Map();
const artistCache = new Map();

function playAll(tracks) {
	if (!tracks.length) return;
	toast(`Playing ${tracks.length} tracks`, 'info');
	playTrack(tracks[0], tracks, 0);
}

/** The large play button plus a summary of what it is about to play. */
function actionRow(tracks) {
	const row = el('div', 'actions');

	const play = el('button', 'play-lg');
	play.type = 'button';
	play.setAttribute('aria-label', 'Play');
	const glyph = el('span', 'ic-xl');
	setIcon(glyph, 'play');
	play.appendChild(glyph);
	play.addEventListener('click', () => playAll(tracks));
	row.appendChild(play);

	const total = tracks.reduce((sum, track) => sum + (Number(track.durationSeconds) || 0), 0);
	if (tracks.length) {
		row.appendChild(el('span', 'actions-note', `${tracks.length} tracks${total ? ` · ${formatTime(total)}` : ''}`));
	}
	return row;
}

async function openAlbum(id, title) {
	if (!id) return;
	const token = ++renderToken;
	pushView('album', title || 'Album');
	els.albumHero.innerHTML = '';
	els.albumTracks.innerHTML = '';

	const cached = albumCache.get(id);
	if (cached) {
		renderAlbum(cached);
		return;
	}

	els.albumHero.appendChild(spinnerNode());

	let detail = null;
	try {
		detail = await Bridge.openAlbum(id);
	} catch (error) {
		console.warn('[album] load failed', error);
	}
	if (token !== renderToken) return;

	if (!detail) {
		els.albumHero.innerHTML = '';
		els.albumTracks.appendChild(noticeNode("We couldn't open that album. Try again in a moment."));
		return;
	}
	albumCache.set(id, detail);
	renderAlbum(detail);
}

function renderAlbum(detail) {
	const album = detail.album || {};
	const tracks = detail.tracks || [];

	els.albumHero.innerHTML = '';
	els.albumTracks.innerHTML = '';
	els.viewTitle.textContent = album.title || 'Album';

	const hero = el('div', 'hero');
	hero.appendChild(coverNode(album.coverUrl, 'hero-art'));

	const meta = el('div', 'row-meta');
	meta.appendChild(el('p', 'hero-eyebrow', album.isSingle ? 'Single' : 'Album'));
	meta.appendChild(el('h2', 'hero-title', album.title || 'Untitled'));
	meta.appendChild(el('p', 'hero-sub', joinBits([
		album.artist,
		album.year ? String(album.year) : null,
		album.songCount ? `${album.songCount} songs` : null,
	])));
	hero.appendChild(meta);

	els.albumHero.appendChild(hero);
	els.albumHero.appendChild(actionRow(tracks));

	if (!tracks.length) {
		els.albumTracks.appendChild(noticeNode('No tracks in this one.'));
		return;
	}

	tracks.forEach((track, index) => els.albumTracks.appendChild(songRow(track, tracks, index, true)));
}

async function openArtist(channelId, name, avatarUrl) {
	if (!channelId) return;
	const token = ++renderToken;
	pushView('artist', name || 'Artist');
	els.artistHero.innerHTML = '';
	els.artistBody.innerHTML = '';

	const cached = artistCache.get(channelId);
	if (cached) {
		renderArtist(cached);
		return;
	}

	els.artistHero.appendChild(spinnerNode());

	let detail = null;
	try {
		detail = await Bridge.openArtist(channelId, avatarUrl);
	} catch (error) {
		console.warn('[artist] load failed', error);
	}
	if (token !== renderToken) return;

	if (!detail) {
		els.artistHero.innerHTML = '';
		els.artistBody.appendChild(noticeNode(`We couldn't open ${name || 'that artist'}. Try again in a moment.`));
		return;
	}
	artistCache.set(channelId, detail);
	renderArtist(detail);
}

function renderArtist(detail) {
	const artist = detail.artist || {};
	const tracks = detail.tracks || [];

	els.artistHero.innerHTML = '';
	els.artistBody.innerHTML = '';
	els.viewTitle.textContent = artist.name || 'Artist';

	const hero = el('div', 'artist-banner');
	if (artist.bannerUrl) {
		hero.appendChild(imageNode(artist.bannerUrl, 'wash'));
	}

	const stack = el('div', 'hero hero-on');
	stack.appendChild(coverNode(artist.avatarUrl, 'avatar'));

	const meta = el('div', 'row-meta');
	meta.appendChild(el('p', 'hero-eyebrow', 'Artist'));
	meta.appendChild(el('h2', 'hero-title', artist.name || 'Artist'));
	meta.appendChild(el('p', 'hero-sub', joinBits([artist.subscribersInfo, artist.popularityInfo])));
	stack.appendChild(meta);

	hero.appendChild(stack);
	els.artistHero.appendChild(hero);
	els.artistHero.appendChild(actionRow(tracks));

	if (detail.description) {
		els.artistHero.appendChild(el('p', 'description', detail.description));
	}

	appendShelves(els.artistBody, [
		tracks.length ? shelf('Popular tracks', tracks.map((track, index) => songRow(track, tracks, index, false)), STACK_ROWS) : null,
		shelf('Albums', (detail.albums || []).map(albumCard), GRID_CARDS),
		shelf('Singles and EPs', (detail.singles || []).map(albumCard), GRID_CARDS),
		shelf('Fans also like', (detail.related || []).map(artistCard), GRID_ARTISTS),
	]);
}

function paintTransport() {
	const paused = state.userPaused || els.audio.paused;
	pairs.playIcon.forEach((button) => {
		setIcon(button.querySelector('[data-icon]'), paused ? 'play' : 'pause');
		button.disabled = !state.videoId && state.queue.length === 0;
	});
	// Apple shrinks the artwork while paused, so the pause is felt as well as seen.
	els.playerArt.classList.toggle('is-paused', paused);
	if ('mediaSession' in navigator) {
		navigator.mediaSession.playbackState = paused ? 'paused' : 'playing';
	}
}

function paintProgress() {
	const audio = els.audio;
	const duration = Number.isFinite(audio.duration) && audio.duration > 0 ? audio.duration : state.duration;
	const position = Number.isFinite(audio.currentTime) ? audio.currentTime : 0;

	pairs.seek.forEach((slider) => {
		if (!state.isScrubbing) {
			slider.value = String(duration > 0 ? Math.round((position / duration) * 1000) : 0);
		}
		// The slider runs 0..1000 while the CSS fill is a percentage: divide by ten.
		slider.style.setProperty('--pct', `${slider.value / 10}%`);
	});
	pairs.timeCurrent.forEach((node) => { node.textContent = formatTime(position); });
	if (duration > 0) {
		pairs.timeTotal.forEach((node) => { node.textContent = formatTime(duration); });
	}

	paintLyricClock(position);

	if (position > 0) state.lastKnownSeconds = position;

	if (state.videoId && position > 5 && !state.awaitingResume) {
		rememberPosition(state.videoId, position);
	}
}

function paintTotal() {
	const duration = state.duration || els.audio.duration || 0;
	pairs.timeTotal.forEach((node) => { node.textContent = formatTime(duration); });
}

function paintVolume() {
	const level = settings.muted ? 0 : settings.volume;
	pairs.volume.forEach((slider) => {
		slider.value = String(level);
		slider.style.setProperty('--pct', `${level * 100}%`);
	});
	els.audio.volume = level;
	const icon = settings.muted || settings.volume === 0 ? 'volumeMuted' : 'volume';
	pairs.muteIcon.forEach((button) => { setIcon(button.querySelector('[data-icon]'), icon); });
}

function showView(name, title) {
	state.view = name;
	Object.entries(views).forEach(([key, node]) => { node.hidden = key !== name; });
	els.viewTitle.textContent = title || VIEW_TITLES[name] || 'Sonora';
	els.btnBack.hidden = state.history.length === 0;
	document.querySelectorAll('[data-nav]').forEach((button) => {
		button.classList.toggle('nav-active', TAB_VIEWS.has(name) && button.dataset.nav === name);
	});
	els.scroller.scrollTop = 0;
}

/** Tab navigation always starts a fresh page, so it clears where you came from. */
function navigateTo(name) {
	state.history = [];
	showView(name);
	if (name === 'search' && window.matchMedia('(min-width: 768px)').matches) els.searchInput.focus();
}

/** Opening an album or an artist remembers the page behind it. */
function pushView(name, title) {
	state.history.push({ view: state.view, title: els.viewTitle.textContent });
	showView(name, title);
}

function goBack() {
	const previous = state.history.pop();
	if (!previous) {
		showView('home');
		return;
	}
	showView(previous.view, previous.title);
}

/* ------------------------------------------------------------------ library bookkeeping */

function saveToLibrary(track) {
	if (!track || !track.videoId) return;
	const index = library.findIndex((entry) => entry.videoId === track.videoId);
	const entry = {
		videoId: track.videoId,
		watchUrl: track.watchUrl || track.url || '',
		title: track.title || 'Untitled',
		artist: track.artist || 'Unknown',
		durationSeconds: track.durationSeconds || 0,
		thumbnailUrl: track.thumbnailUrl || null,
		albumName: track.albumName || null,
		lastPositionSeconds: index >= 0 ? library[index].lastPositionSeconds || 0 : 0,
		addedAt: Date.now(),
	};
	if (index >= 0) library.splice(index, 1);
	library.unshift(entry);
	library = library.slice(0, LIBRARY_LIMIT);
	store.write(LIBRARY_KEY, library);
	renderLibrary();
	renderHomeContinue();
}

function rememberPosition(videoId, seconds) {
	const entry = library.find((item) => item.videoId === videoId);
	if (!entry) return;
	if (Math.abs((entry.lastPositionSeconds || 0) - seconds) < 3) return;
	entry.lastPositionSeconds = seconds;
	// Deferred write: this runs once per second while playing.
	if (rememberPosition.timer) return;
	rememberPosition.timer = setTimeout(() => {
		rememberPosition.timer = 0;
		store.write(LIBRARY_KEY, library);
	}, 5000);
}

/* ------------------------------------------------------------------ own playlists */

function savePlaylists() {
	store.write(PLAYLISTS_KEY, playlists);
}

function findPlaylist(id) {
	return playlists.find((list) => list.id === id) || null;
}

function playlistCountLabel(count) {
	return `${count} song${count === 1 ? '' : 's'}`;
}

/** The stored shape is the row shape, so a playlist item plays exactly like a saved one. */
function playlistEntry(track) {
	return {
		videoId: track.videoId,
		watchUrl: track.watchUrl || track.url || '',
		title: track.title || 'Untitled',
		artist: track.artist || 'Unknown',
		durationSeconds: track.durationSeconds || 0,
		thumbnailUrl: track.thumbnailUrl || null,
		albumName: track.albumName || null,
	};
}

function createPlaylist(name) {
	const list = {
		id: `pl${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`,
		name: (name || '').trim() || 'New playlist',
		createdAt: Date.now(),
		items: [],
	};
	playlists.unshift(list);
	savePlaylists();
	return list;
}

function addToPlaylist(id, track) {
	const list = findPlaylist(id);
	if (!list || !track || !track.videoId) return null;
	if (list.items.some((item) => item.videoId === track.videoId)) return 'dupe';
	list.items.push(playlistEntry(track));
	savePlaylists();
	return list.items.length;
}

/** Every playlist surface is repainted together: the grid, and the page if one is open. */
function refreshPlaylists() {
	renderLibraryPlaylists();
	if (state.view === 'playlist') {
		const list = findPlaylist(state.openPlaylistId);
		if (list) renderPlaylist(list);
	}
}

function playlistCard(list) {
	const button = el('button', 'card');
	button.type = 'button';
	button.appendChild(coverNode(list.items.length ? list.items[0].thumbnailUrl : null, 'card-art'));
	button.appendChild(el('p', 'card-title', list.name));
	button.appendChild(el('p', 'card-sub', playlistCountLabel(list.items.length)));
	button.addEventListener('click', () => openPlaylist(list.id));
	return button;
}

function renderLibraryPlaylists() {
	els.libraryPlaylistGrid.innerHTML = '';
	els.libraryPlaylistNote.hidden = playlists.length > 0;
	playlists.forEach((list) => els.libraryPlaylistGrid.appendChild(playlistCard(list)));
}

function openPlaylist(id) {
	const list = findPlaylist(id);
	if (!list) return;
	state.openPlaylistId = id;
	pushView('playlist', list.name);
	renderPlaylist(list);
}

/** The name is editable where it is shown, so a rename never needs a dialog. */
function playlistNameInput(list) {
	const input = el('input', 'hero-title-input');
	input.type = 'text';
	input.maxLength = 60;
	input.value = list.name;
	input.setAttribute('aria-label', 'Playlist name');
	input.addEventListener('change', () => {
		const next = input.value.trim();
		if (!next || next === list.name) {
			input.value = list.name;
			return;
		}
		list.name = next;
		savePlaylists();
		els.viewTitle.textContent = next;
		renderLibraryPlaylists();
		toast('Playlist renamed', 'info');
	});
	return input;
}

function renderPlaylist(list) {
	els.playlistHero.innerHTML = '';
	els.playlistTracks.innerHTML = '';

	const hero = el('div', 'hero');
	hero.appendChild(coverNode(list.items.length ? list.items[0].thumbnailUrl : null, 'hero-art'));

	const meta = el('div', 'row-meta');
	meta.appendChild(el('p', 'hero-eyebrow', 'Playlist'));
	meta.appendChild(playlistNameInput(list));
	meta.appendChild(el('p', 'hero-sub', joinBits([playlistCountLabel(list.items.length), 'By you'])));
	hero.appendChild(meta);

	els.playlistHero.appendChild(hero);

	if (list.items.length) {
		els.playlistHero.appendChild(actionRow(list.items));
		list.items.forEach((item, index) => {
			const row = songRow(item, list.items, index, false, true);
			const remove = iconButton('icon-round row-add', 'close', `Remove ${item.title} from this playlist`);
			remove.addEventListener('click', (event) => {
				event.stopPropagation();
				list.items.splice(index, 1);
				savePlaylists();
				renderPlaylist(findPlaylist(list.id) || list);
				renderLibraryPlaylists();
			});
			row.appendChild(remove);
			els.playlistTracks.appendChild(row);
		});
	} else {
		els.playlistTracks.appendChild(noticeNode('Nothing in here yet — press the plus on any song.'));
	}

	// An empty list still has to be removable, so the delete sits outside the tracklist.
	els.playlistHero.appendChild(armDeletePlaylist(list));
}

function armDeletePlaylist(list) {
	const button = el('button', 'btn-text', 'Delete playlist');
	button.type = 'button';
	let armed = false;
	button.addEventListener('click', () => {
		if (!armed) {
			armed = true;
			button.textContent = 'Tap again to delete';
			setTimeout(() => {
				armed = false;
				button.textContent = 'Delete playlist';
			}, 3000);
			return;
		}
		playlists = playlists.filter((item) => item.id !== list.id);
		savePlaylists();
		state.openPlaylistId = null;
		renderLibraryPlaylists();
		goBack();
		toast('Playlist deleted', 'info');
	});
	return button;
}

/* ------------------------------------------------------------------ playlist picker */

function showPlaylistPicker(track) {
	if (!track || !track.videoId) return;
	state.pickerTrack = track;
	els.playlistPickerSong.textContent = `${track.title || 'Untitled'} · ${track.artist || 'Unknown'}`;
	els.playlistNameInput.value = '';
	renderPlaylistPicker();
	els.playlistModal.classList.remove('hidden');
	els.playlistNameInput.focus();
}

function hidePlaylistPicker() {
	els.playlistModal.classList.add('hidden');
	state.pickerTrack = null;
}

function renderPlaylistPicker() {
	els.playlistPickerList.innerHTML = '';
	if (!playlists.length) {
		els.playlistPickerList.appendChild(el('p', 'modal-body', 'No playlists yet — name the first one below.'));
		return;
	}
	playlists.forEach((list) => {
		const item = el('button', 'picker-item');
		item.type = 'button';
		item.appendChild(el('span', 'picker-name', list.name));
		item.appendChild(el('span', 'picker-count', playlistCountLabel(list.items.length)));
		item.addEventListener('click', () => {
			const result = addToPlaylist(list.id, state.pickerTrack);
			const name = list.name;
			hidePlaylistPicker();
			toast(result === 'dupe' ? `Already in ${name}` : `Added to ${name}`, 'info');
			refreshPlaylists();
		});
		els.playlistPickerList.appendChild(item);
	});
}

function createPlaylistFromPicker() {
	const name = els.playlistNameInput.value.trim();
	if (!name) {
		els.playlistNameInput.focus();
		return;
	}
	const track = state.pickerTrack;
	const list = createPlaylist(name);
	hidePlaylistPicker();
	if (track) addToPlaylist(list.id, track);
	toast(`${list.name} started`, 'info');
	refreshPlaylists();
}

/* ------------------------------------------------------------------ wiring */

function wireAudioElement() {
	const audio = els.audio;

	// The resume seek is only valid once the container is known.
	audio.addEventListener('loadedmetadata', () => {
		if (state.awaitingResume) playFromResumePoint();
		else paintTotal();
	});

	audio.addEventListener('error', () => {
		if (!state.videoId || state.userPaused) return;
		const code = audio.error ? audio.error.code : '?';
		// A failure while applying a hand-over means the URL just issued is itself dead.
		reportStall(`audio element error (code ${code})`, state.awaitingResume);
	});

	audio.addEventListener('playing', () => {
		state.awaitingResume = false;
		state.recovering = false;
		state.frozenMs = 0;
		hideRecovery();
		state.userPaused = false;
		paintTransport();
	});

	audio.addEventListener('pause', () => paintTransport());
	audio.addEventListener('resume', () => paintTransport());
	audio.addEventListener('timeupdate', paintProgress);
	audio.addEventListener('durationchange', paintTotal);
	audio.addEventListener('ended', () => {
		if (state.queueIndex >= 0 && state.queueIndex + 1 < state.queue.length) playRelative(1);
		else paintTransport();
	});
}

/** Where a slider that runs 0..1000 says the listener is, in seconds. */
function sliderSeconds(slider) {
	const duration = Number.isFinite(els.audio.duration) && els.audio.duration > 0
		? els.audio.duration
		: state.duration;
	return (Number(slider.value) / 1000) * duration;
}

function bindSeek(slider) {
	slider.addEventListener('pointerdown', () => { state.isScrubbing = true; });
	slider.addEventListener('input', () => {
		state.isScrubbing = true;
		slider.style.setProperty('--pct', `${slider.value / 10}%`);
		pairs.timeCurrent.forEach((node) => { node.textContent = formatTime(sliderSeconds(slider)); });
	});
	slider.addEventListener('change', () => {
		seekTo(sliderSeconds(slider));
		state.isScrubbing = false;
	});
}

function setVolume(level) {
	settings.volume = level;
	settings.muted = level === 0;
	els.audio.volume = level;
	paintVolume();
	store.write(SETTINGS_KEY, settings);
}

function wireControls() {
	// Bar and player are two faces of the same transport, so both drive the same code.
	pairs.playIcon.forEach((button) => button.addEventListener('click', togglePlay));
	[els.btnPrev, els.playerPrev].forEach((button) => button.addEventListener('click', () => playRelative(-1)));
	[els.btnNext, els.playerNext].forEach((button) => button.addEventListener('click', () => playRelative(1)));
	pairs.seek.forEach(bindSeek);
	pairs.volume.forEach((slider) => slider.addEventListener('input', () => setVolume(Number(slider.value))));
	pairs.muteIcon.forEach((button) => button.addEventListener('click', () => {
		settings.muted = !settings.muted;
		store.write(SETTINGS_KEY, settings);
		paintVolume();
	}));

	els.openPlayer.addEventListener('click', openPlayer);
	els.openPlayer.addEventListener('keydown', (event) => {
		if (event.key === 'Enter' || event.key === ' ') {
			event.preventDefault();
			openPlayer();
		}
	});
	els.playerExpand.addEventListener('click', openPlayer);
	els.playerClose.addEventListener('click', closePlayer);
	els.lyricsFullToggle.addEventListener('click', toggleLyricsFullscreen);
	document.addEventListener('keydown', (event) => {
		if (event.key !== 'Escape') return;
		if (!els.playlistModal.classList.contains('hidden')) hidePlaylistPicker();
		else closePlayer();
	});

	// Scrolling the panel by hand wins over the clock until the listener stops.
	['wheel', 'pointerdown'].forEach((type) => {
		[els.playerLyrics, els.playerInner].forEach((node) => node.addEventListener(type, holdLyrics, { passive: true }));
	});

	document.querySelectorAll('[data-nav]').forEach((button) => {
		button.addEventListener('click', () => navigateTo(button.dataset.nav));
	});

	els.btnBack.addEventListener('click', goBack);

	els.searchTabs.querySelectorAll('.search-tab').forEach((button) => {
		button.addEventListener('click', () => {
			state.searchTab = button.dataset.tab;
			renderSearchPage();
		});
	});

	els.searchSubmit.addEventListener('click', () => runSearch(els.searchInput.value));
	els.searchInput.addEventListener('input', () => {
		clearTimeout(state.searchTimer);
		const value = els.searchInput.value;
		if (!value.trim()) {
			++renderToken;
			state.searchPage = null;
			els.searchResults.innerHTML = '';
			els.searchStatus.textContent = '';
			els.searchTabs.hidden = true;
			els.searchEmpty.hidden = false;
			return;
		}
		state.searchTimer = setTimeout(() => runSearch(value), SEARCH_DEBOUNCE_MS);
	});
	els.searchInput.addEventListener('keydown', (event) => {
		if (event.key === 'Enter') {
			event.preventDefault();
			clearTimeout(state.searchTimer);
			runSearch(els.searchInput.value);
		}
	});

	els.libraryClear.addEventListener('click', () => {
		library = [];
		store.write(LIBRARY_KEY, library);
		renderLibrary();
		renderHomeContinue();
		toast('Library cleared', 'info');
	});

	els.playlistNew.addEventListener('click', () => {
		const list = createPlaylist(`Playlist ${playlists.length + 1}`);
		renderLibraryPlaylists();
		openPlaylist(list.id);
		const input = els.playlistHero.querySelector('.hero-title-input');
		if (input) {
			input.focus();
			input.select();
		}
	});

	els.playlistCreate.addEventListener('click', createPlaylistFromPicker);
	els.playlistNameInput.addEventListener('keydown', (event) => {
		if (event.key === 'Enter') {
			event.preventDefault();
			createPlaylistFromPicker();
		}
	});
	els.playlistPickerDismiss.addEventListener('click', hidePlaylistPicker);
	els.playlistModal.addEventListener('click', (event) => {
		if (event.target === els.playlistModal) hidePlaylistPicker();
	});

	els.errorModalDismiss.addEventListener('click', hideErrorModal);
	els.errorModalRetry.addEventListener('click', () => {
		hideErrorModal();
		state.userPaused = false;
		if (state.videoId) {
			reportStall('retry requested by the listener', true);
		} else if (state.track) {
			playTrack(state.track, state.queue, state.queueIndex);
		}
	});
	els.errorModal.addEventListener('click', (event) => {
		if (event.target === els.errorModal) hideErrorModal();
	});
}

function wireMediaSession() {
	if (!('mediaSession' in navigator)) return;
	const actions = {
		play: () => { state.userPaused = false; els.audio.play().catch(() => {}); },
		pause: () => userPause(),
		previoustrack: () => playRelative(-1),
		nexttrack: () => playRelative(1),
		seekto: (details) => { if (typeof details.seekTime === 'number') seekTo(details.seekTime); },
	};
	Object.entries(actions).forEach(([action, handler]) => {
		try {
			navigator.mediaSession.setActionHandler(action, handler);
		} catch (error) {
			console.warn('[mediaSession] unsupported action', action);
		}
	});
}

/**
 * The clock probe that backs both stall reporters: it sends the heartbeat the host needs
 * to know where the listener is, and raises a stall when the host would otherwise never
 * learn that the stream went silent.
 */
function startHeartbeat() {
	setInterval(() => {
		const audio = els.audio;
		const clock = Number.isFinite(audio.currentTime) ? audio.currentTime : 0;
		const playing = !audio.paused && !!state.videoId;

		if (playing) {
			state.lastKnownSeconds = clock;
			Bridge.reportPosition({
				videoId: state.videoId,
				currentSeconds: clock,
				durationSeconds: Number.isFinite(audio.duration) ? audio.duration : state.duration,
				isPlaying: true,
			});
		}

		if (state.awaitingResume && state.resumeDeadline && Date.now() > state.resumeDeadline) {
			// A URL arrived but never produced a single sample of audio.
			state.resumeDeadline = 0;
			reportStall('resumed stream never started playing', true);
			return;
		}

		if (playing && !state.awaitingResume && !state.recovering) {
			if (Math.abs(clock - state.lastClock) < 0.05) {
				state.frozenMs += HEARTBEAT_MS;
				if (state.frozenMs >= JS_STALL_MS) {
					state.frozenMs = 0;
					reportStall(`playback clock frozen for ${Math.round(JS_STALL_MS / 1000)}s`);
				}
			} else {
				state.frozenMs = 0;
			}
		}

		state.lastClock = clock;
	}, HEARTBEAT_MS);
}

function greet() {
	const hour = new Date().getHours();
	els.greeting.textContent = hour < 5
		? 'Late session? Pick something calm.'
		: hour < 12
			? 'Good morning. Start with something light.'
			: hour < 18
				? 'Good afternoon. Ready when you are.'
				: 'Good evening. Anything in mind?';
}

function init() {
	hydrateIcons(document);
	els.audio.volume = settings.muted ? 0 : settings.volume;

	greet();
	renderQuickPicks();
	renderLibrary();
	renderLibraryPlaylists();
	renderHomeContinue();
	navigateTo('home');
	paintTransport();
	paintVolume();
	wireAudioElement();
	wireControls();
	wireMediaSession();
	startHeartbeat();

	window.addEventListener('HybridWebViewMessageReceived', (event) => {
		handleHostMessage(event.detail.message);
	});
}

if (document.readyState === 'loading') {
	document.addEventListener('DOMContentLoaded', init);
} else {
	init();
}
