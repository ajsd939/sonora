// Attaches to the running app's WebView2 over the Chrome DevTools Protocol and runs
// Runtime.evaluate inside the Sonora page, so the bridge can be probed for real.
const PORT = process.env.PORT || '9223';
const EXPR = process.argv[2];
const AWAIT = process.argv[3] !== 'no';

async function main() {
	const targets = await (await fetch(`http://127.0.0.1:${PORT}/json`)).json();
	const page = targets.find(t => t.type === 'page' && /wwwroot|index\.html/.test(t.url)) || targets.find(t => t.type === 'page');
	if (!page) throw new Error('no page target: ' + JSON.stringify(targets.map(t => [t.type, t.url])));

	const ws = new WebSocket(page.webSocketDebuggerUrl);
	await new Promise((resolve, reject) => {
		ws.onopen = resolve;
		ws.onerror = () => reject(new Error('websocket failed'));
	});

	let id = 0;
	const pending = new Map();
	ws.onmessage = event => {
		const msg = JSON.parse(event.data);
		if (msg.id && pending.has(msg.id)) {
			pending.get(msg.id)(msg);
			pending.delete(msg.id);
		}
	};
	const send = (method, params) => new Promise(resolve => {
		const n = ++id;
		pending.set(n, resolve);
		ws.send(JSON.stringify({ id: n, method, params: params || {} }));
	});

	const result = await send('Runtime.evaluate', {
		expression: EXPR,
		awaitPromise: AWAIT,
		returnByValue: true,
		quiet: false,
	});
	console.log(JSON.stringify(result, null, 2));
	ws.close();
}

main().catch(error => {
	console.error('PROBE FAILED:', error.message);
	process.exit(1);
});
