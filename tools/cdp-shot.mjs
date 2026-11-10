// Grabs a screenshot of the running app's WebView2 page over the DevTools protocol.
import { writeFileSync } from 'node:fs';

const PORT = process.env.PORT || '9223';

const targets = await (await fetch(`http://127.0.0.1:${PORT}/json`)).json();
const page = targets.find(t => t.type === 'page');
if (!page) throw new Error('no page target');

const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
	ws.onopen = resolve;
	ws.onerror = () => reject(new Error('websocket failed'));
});

const result = await new Promise(resolve => {
	const handler = msg => {
		if (msg.id === 1) {
			ws.removeEventListener('message', handler);
			resolve(msg);
		}
	};
	ws.addEventListener('message', handler);
	ws.send(JSON.stringify({ id: 1, method: 'Page.captureScreenshot', params: { format: 'png' } }));
});

if (result.error) throw new Error(JSON.stringify(result.error));
writeFileSync(process.argv[2] || 'app-shot.png', Buffer.from(result.result.data, 'base64'));
console.log('saved', process.argv[2] || 'app-shot.png');
ws.close();
