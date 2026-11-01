// Reproducible client latency comparison; fake LLM/TTS, real wall clock.
// node tests/voice_latency.cjs [path/to/baseline-chat.js]
const { performance } = require('node:perf_hooks');
const { setup } = require('./chat-harness.cjs');
const assert = require('node:assert/strict');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function measure(sourcePath) {
  let firstPlay, resolvePlay;
  const played = new Promise(resolve => { resolvePlay = resolve; });
  const h = setup({ sourcePath, onPlay: () => { firstPlay ??= performance.now(); resolvePlay(); } });
  h.get('#voiceToggle').fire('click');
  h.get('#messageInput').value = 'Explica em três frases.';
  const start = performance.now();
  const submitted = h.get('#chatForm').fire('submit');
  const chunks = ['Primeira frase', ' completa. ', 'Segunda frase', ' completa. ', 'Terceira frase', ' completa.'];
  let index = 0;
  h.requests[0].resolve({ ok: true, body: { getReader: () => ({
    async read() {
      if (index === chunks.length) return { done: true };
      await delay(150);
      return { done: false, value: new TextEncoder().encode(chunks[index++]) };
    }, releaseLock() {}
  }) } });
  const handled = new Set();
  let timeout;
  const poll = setInterval(() => {
    for (const request of h.requests.filter(r => r.url === '/audio/speak' && !handled.has(r))) {
      handled.add(request);
      setTimeout(() => request.resolve({ ok: true, blob: async () => new Blob(['wav']) }), 100);
    }
  }, 2);
  try {
    await Promise.race([played, new Promise((_,reject) => { timeout=setTimeout(() => reject(new Error('No playback')),5000); })]);
    const latency = firstPlay - start;
    await submitted;
    h.get('#stopSpeech').fire('click');
    return Math.round(latency);
  } finally { clearTimeout(timeout); clearInterval(poll); h.get('#newChat').fire('click'); }
}
(async () => {
  const baseline = process.argv[2];
  const before = baseline ? await measure(baseline) : null;
  const after = await measure('Lumi.Api/wwwroot/chat.js');
  console.log(JSON.stringify({ scenario: '6 chunks × 150ms; TTS 100ms', beforeMs: before, afterMs: after,
    reductionPercent: before ? Math.round((1 - after / before) * 100) : null }));
  if (before) assert.ok(after < before * .75, 'incremental playback must begin before stream completion');
})().catch(error => { console.error(error); process.exitCode = 1; });
