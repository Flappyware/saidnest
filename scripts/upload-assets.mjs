// The upload session is short-lived, scoped to these assets, and never committed.
import { readFile, writeFile } from 'node:fs/promises';
const session = JSON.parse(await readFile('.deploy/upload-session.json', 'utf8'));
const bundle = JSON.parse(await readFile('.deploy/bundle.json', 'utf8'));
const account = process.env.CLOUDFLARE_ACCOUNT_ID;
if (!account || !/^[a-f0-9]{32}$/.test(account)) throw new Error('Set CLOUDFLARE_ACCOUNT_ID');
let completionToken = session.jwt;
for (const bucket of session.buckets) {
  const form = new FormData();
  for (const hash of bucket) {
    const asset = bundle.assets.find(a => a.hash === hash);
    if (!asset) throw new Error('Upload requested an unknown asset');
    form.append(hash, new Blob([asset.base64], { type: asset.mime }), hash);
  }
  const response = await fetch(`https://api.cloudflare.com/client/v4/accounts/${account}/workers/assets/upload?base64=true`, {
    method: 'POST', headers: { Authorization: `Bearer ${session.jwt}` }, body: form,
  });
  const body = await response.json();
  if (!body.success) throw new Error(`Asset upload failed: HTTP ${response.status}`);
  if (body.result?.jwt) completionToken = body.result.jwt;
  console.log(`Uploaded ${bucket.length} assets: HTTP ${response.status}`);
}
await writeFile('.deploy/completion.json', JSON.stringify({ jwt: completionToken }));
console.log('Asset upload complete. Completion token saved in ignored deployment directory.');
