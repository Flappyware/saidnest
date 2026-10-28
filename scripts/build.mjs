import { cp, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';

await mkdir('dist', { recursive: true });
await cp('public', 'dist', { recursive: true });
const assets = [];
async function walk(dir, prefix = '') {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const relative = path.posix.join(prefix, entry.name);
    if (entry.isDirectory()) await walk(path.join(dir, entry.name), relative);
    else {
      const bytes = await readFile(path.join(dir, entry.name));
      const mime = { '.html': 'text/html', '.css': 'text/css', '.js': 'application/javascript', '.svg': 'image/svg+xml', '.txt': 'text/plain' }[path.extname(entry.name)] || 'application/octet-stream';
      assets.push({ path: '/' + relative, hash: createHash('sha256').update(bytes).digest('hex').slice(0, 32), size: bytes.length, mime, base64: bytes.toString('base64') });
    }
  }
}
await walk('dist');
await mkdir('.deploy', { recursive: true });
await writeFile('.deploy/bundle.json', JSON.stringify({ assets, worker: await readFile('src/worker.js', 'utf8'), metadata: JSON.parse(await readFile('src/deployment.json', 'utf8')) }));
console.log(`Built SaidNest: ${assets.length} assets, ${assets.reduce((n, a) => n + a.size, 0)} bytes.`);
