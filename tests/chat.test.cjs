const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const flush = () => new Promise(resolve => setImmediate(resolve));
test('HTML wires the tested script and controls', () => {
  const html = fs.readFileSync('Lumi.Api/wwwroot/chat.html', 'utf8');
  assert.match(html, /<script src="\/chat.js" defer><\/script>/);
  for (const id of ['sendButton', 'cancelCapture', 'stopSpeech', 'status', 'error'])
    assert.ok(html.includes(`id="${id}"`), id);
  assert.match(html, /<form[^>]*novalidate/);
});
const { setup, createStorage, deferred, streamed, reply } = require('./chat-harness.cjs');
test('session ID persists after a new browser session with fresh sessionStorage', async () => {
  const first = await setup();
  await reply(first);
  const originalId = JSON.parse(first.requests[0].options.body).sessionId;
  assert.match(originalId, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
  assert.equal(first.localStorage.getItem('lumi-session'), originalId);
  first.sessionStorage.setItem('browser-session-only', 'old');

  const reopened = await setup({ localStorage: first.localStorage });
  assert.equal(reopened.sessionStorage.getItem('browser-session-only'), null);
  await reply(reopened);
  assert.equal(JSON.parse(reopened.requests[0].options.body).sessionId, originalId);
  assert.notEqual((await setup()).localStorage.getItem('lumi-session'), originalId);
});
test('legacy sessionStorage ID migrates to localStorage and survives a new browser session', async () => {
  const legacyId = '11776f6c-f3e4-4501-b1c3-8923141869cb';
  const sessionStorage = createStorage();
  sessionStorage.setItem('lumi-session', legacyId);
  sessionStorage.setItem('unrelated', 'keep');
  const first = await setup({ sessionStorage });
  await reply(first);
  assert.equal(JSON.parse(first.requests[0].options.body).sessionId, legacyId);
  assert.equal(sessionStorage.getItem('lumi-session'), null);
  assert.equal(sessionStorage.getItem('unrelated'), 'keep');
  assert.equal(first.localStorage.getItem('lumi-session'), legacyId);

  const reopened = await setup({ localStorage: first.localStorage });
  await reply(reopened);
  assert.equal(JSON.parse(reopened.requests[0].options.body).sessionId, legacyId);
});
test('new conversation remains active after reloading and reopening the browser', async () => {
  const sessionStorage = createStorage();
  sessionStorage.setItem('lumi-session', '11776f6c-f3e4-4501-b1c3-8923141869cb');
  const first = await setup({ sessionStorage });
  const originalId = first.localStorage.getItem('lumi-session');
  first.get('#newChat').fire('click');
  await reply(first);
  const newId = JSON.parse(first.requests[0].options.body).sessionId;
  assert.notEqual(newId, originalId);
  assert.match(newId, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
  assert.equal(first.localStorage.getItem('lumi-session'), newId);
  assert.equal(first.sessionStorage.getItem('lumi-session'), null);

  const reloaded = await setup({ localStorage: first.localStorage, sessionStorage: first.sessionStorage });
  await reply(reloaded);
  assert.equal(JSON.parse(reloaded.requests[0].options.body).sessionId, newId);

  const reopened = await setup({ localStorage: first.localStorage });
  await reply(reopened);
  assert.equal(JSON.parse(reopened.requests[0].options.body).sessionId, newId);
});
test('chat unlocks during synthesis; playback replacement and voice off release resources',async()=>{
  const h=await setup();h.get('#voiceToggle').fire('click');
  await reply(h);
  assert.equal(h.requests.at(-1).url,'/audio/speak');
  assert.equal(h.get('#sendButton').disabled,false);
  h.requests.at(-1).resolve({ok:true,blob:async()=>new Blob(['wave'])});await flush();
  assert.match(h.get('#status').textContent,/A falar/);
  await reply(h,'Segunda');
  assert.equal(h.audios[0].paused,true);assert.equal(h.revoked.length,1);
  const synth=h.requests.at(-1);h.get('#voiceToggle').fire('click');
  assert.equal(synth.options.signal.aborted,true);
  synth.resolve({ok:true,blob:async()=>new Blob(['old'])});await flush();
  assert.equal(h.audios.length,1);assert.equal(h.get('#error').textContent,'');
});
test('cancel recording does not upload and keeps draft',async()=>{
  const h=await setup();h.get('#messageInput').value='rascunho';
  await h.get('#micButton').fire('click');h.get('#cancelCapture').fire('click');await flush();
  assert.equal(h.requests.length,0);assert.equal(h.tracks[0].stopped,true);
  assert.equal(h.get('#messageInput').value,'rascunho');assert.equal(h.get('#status').textContent,'Inativo');
});
test('cancel transcription aborts fetch and ignores late result',async()=>{
  const h=await setup();await h.get('#micButton').fire('click');await h.get('#micButton').fire('click');await flush();
  const request=h.requests[0];assert.equal(request.url,'/audio/transcribe');
  h.get('#messageInput').value='texto';h.get('#cancelCapture').fire('click');
  assert.equal(request.options.signal.aborted,true);request.resolve({ok:true,json:async()=>({text:'antigo'})});await flush();
  assert.equal(h.get('#messageInput').value,'texto');assert.equal(h.get('#error').textContent,'');
});
test('successful transcription appends to current text',async()=>{
  const h=await setup();await h.get('#micButton').fire('click');await h.get('#micButton').fire('click');await flush();
  h.get('#messageInput').value='escrito entretanto';h.requests[0].resolve({ok:true,json:async()=>({text:'voz'})});await flush();
  assert.equal(h.get('#messageInput').value,'escrito entretanto voz');assert.equal(h.tracks[0].stopped,true);
});
test('new conversation invalidates old chat and preserves draft',async()=>{
  const h=await setup();h.get('#voiceToggle').fire('click');h.get('#messageInput').value='pergunta';
  const done=h.get('#chatForm').fire('submit');const old=h.requests[0];
  h.get('#messageInput').value='próxima';h.get('#newChat').fire('click');
  assert.equal(old.options.signal.aborted,true);old.resolve(streamed('antigo'));await done;
  assert.equal(h.get('#messages').children.length,0);assert.equal(h.requests.length,1);
  assert.equal(h.get('#messageInput').value,'próxima');assert.equal(h.get('#status').textContent,'Inativo');
});
test('late microphone permission after cancel releases tracks',async()=>{
  const h=await setup(),d=deferred();h.setPermission(d);
  const done=h.get('#micButton').fire('click');h.get('#cancelCapture').fire('click');
  let stopped=false;d.resolve({getTracks:()=>[{stop(){stopped=true;}}]});await done;
  assert.equal(stopped,true);assert.equal(h.recorders.length,0);
});
test('playback ended frees URL; synthesis errors preserve chat and unlock form',async()=>{
  const h=await setup();h.get('#voiceToggle').fire('click');await reply(h);
  h.requests.at(-1).resolve({ok:true,blob:async()=>new Blob(['wav'])});await flush();
  h.audios[0].onended();assert.equal(h.revoked.length,1);assert.equal(h.get('#status').textContent,'Inativo');
  await reply(h);h.requests.at(-1).resolve({ok:false});await flush();
  assert.match(h.get('#error').textContent,/Não foi possível/);assert.equal(h.get('#sendButton').disabled,false);
  assert.equal(h.get('#messages').children.at(-1).textContent,'Olá');
});
