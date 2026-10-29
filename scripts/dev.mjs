import http from 'node:http';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import worker from '../src/worker.js';

const mime = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'application/javascript', '.svg': 'image/svg+xml' };
const root = path.resolve(process.argv.includes('--built') ? 'dist' : 'public');
const assets = { async fetch(request) {
  const url = new URL(request.url);
  let relative;
  try { relative = decodeURIComponent(url.pathname); } catch { return new Response('Bad path', { status: 400 }); }
  let file = path.resolve(root, '.' + relative);
  if (file !== root && !file.startsWith(root + path.sep)) return new Response('Not found', { status: 404 });
  if (!path.extname(file)) file = path.join(file, 'index.html');
  try { return new Response(await readFile(file), { headers: { 'Content-Type': mime[path.extname(file)] || 'application/octet-stream' } }); }
  catch { return new Response(await readFile(path.join(root, '404.html')), { status: 404, headers: { 'Content-Type': mime['.html'] } }); }
} };
http.createServer(async (req, res) => {
  try {
    const response = await worker.fetch(new Request(`http://localhost:8787${req.url}`, { method: req.method }), { ASSETS: assets });
    res.writeHead(response.status, Object.fromEntries(response.headers));
    res.end(Buffer.from(await response.arrayBuffer()));
  } catch { res.writeHead(500); res.end('Local server error'); }
}).listen(8787, '127.0.0.1', () => console.log('SaidNest is at http://localhost:8787'));
