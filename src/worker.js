const securityHeaders = {
  'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
  'Referrer-Policy': 'no-referrer',
  'X-Content-Type-Options': 'nosniff',
  'X-Frame-Options': 'DENY',
  'Permissions-Policy': 'camera=(), microphone=(), geolocation=()',
  'Strict-Transport-Security': 'max-age=31536000',
};

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    let response;
    if (url.hostname === 'www.saidnest.com') {
      url.hostname = 'saidnest.com';
      url.protocol = 'https:';
      response = Response.redirect(url.href, 308);
    } else if (!['GET', 'HEAD'].includes(request.method)) {
      response = Response.json({ error: 'Method not allowed' }, { status: 405, headers: { Allow: 'GET, HEAD' } });
    } else if (url.pathname === '/api/health') {
      response = Response.json({ service: 'saidnest', version: '0.1.0', status: 'ok', release: 'preview', capabilities: { recording: false, transcription: false, repositoryPairing: false, relay: false } }, { headers: { 'Cache-Control': 'no-store' } });
    } else if (url.pathname === '/api' || url.pathname.startsWith('/api/')) {
      response = Response.json({ error: 'Endpoint not available in this preview' }, { status: 404, headers: { 'Cache-Control': 'no-store' } });
    } else {
      response = await env.ASSETS.fetch(request);
    }
    const secured = new Response(request.method === 'HEAD' ? null : response.body, response);
    for (const [name, value] of Object.entries(securityHeaders)) secured.headers.set(name, value);
    return secured;
  },
};
