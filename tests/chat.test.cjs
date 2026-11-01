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
const { setup, deferred, streamed, reply } = require('./chat-harness.cjs');
test('chat unlocks during synthesis; playback replacement and voice off release resources',async()=>{
  const h=setup();h.get('#voiceToggle').fire('click');
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
  const h=setup();h.get('#messageInput').value='rascunho';
  await h.get('#micButton').fire('click');h.get('#cancelCapture').fire('click');await flush();
  assert.equal(h.requests.length,0);assert.equal(h.tracks[0].stopped,true);
  assert.equal(h.get('#messageInput').value,'rascunho');assert.equal(h.get('#status').textContent,'Inativo');
});
test('cancel transcription aborts fetch and ignores late result',async()=>{
  const h=setup();await h.get('#micButton').fire('click');await h.get('#micButton').fire('click');await flush();
  const request=h.requests[0];assert.equal(request.url,'/audio/transcribe');
  h.get('#messageInput').value='texto';h.get('#cancelCapture').fire('click');
  assert.equal(request.options.signal.aborted,true);request.resolve({ok:true,json:async()=>({text:'antigo'})});await flush();
  assert.equal(h.get('#messageInput').value,'texto');assert.equal(h.get('#error').textContent,'');
});
test('successful transcription appends to current text',async()=>{
  const h=setup();await h.get('#micButton').fire('click');await h.get('#micButton').fire('click');await flush();
  h.get('#messageInput').value='escrito entretanto';h.requests[0].resolve({ok:true,json:async()=>({text:'voz'})});await flush();
  assert.equal(h.get('#messageInput').value,'escrito entretanto voz');assert.equal(h.tracks[0].stopped,true);
});
test('new conversation invalidates old chat and preserves draft',async()=>{
  const h=setup();h.get('#voiceToggle').fire('click');h.get('#messageInput').value='pergunta';
  const done=h.get('#chatForm').fire('submit');const old=h.requests[0];
  h.get('#messageInput').value='próxima';h.get('#newChat').fire('click');
  assert.equal(old.options.signal.aborted,true);old.resolve(streamed('antigo'));await done;
  assert.equal(h.get('#messages').children.length,0);assert.equal(h.requests.length,1);
  assert.equal(h.get('#messageInput').value,'próxima');assert.equal(h.get('#status').textContent,'Inativo');
});
test('late microphone permission after cancel releases tracks',async()=>{
  const h=setup(),d=deferred();h.setPermission(d);
  const done=h.get('#micButton').fire('click');h.get('#cancelCapture').fire('click');
  let stopped=false;d.resolve({getTracks:()=>[{stop(){stopped=true;}}]});await done;
  assert.equal(stopped,true);assert.equal(h.recorders.length,0);
});
test('playback ended frees URL; synthesis errors preserve chat and unlock form',async()=>{
  const h=setup();h.get('#voiceToggle').fire('click');await reply(h);
  h.requests.at(-1).resolve({ok:true,blob:async()=>new Blob(['wav'])});await flush();
  h.audios[0].onended();assert.equal(h.revoked.length,1);assert.equal(h.get('#status').textContent,'Inativo');
  await reply(h);h.requests.at(-1).resolve({ok:false});await flush();
  assert.match(h.get('#error').textContent,/Não foi possível/);assert.equal(h.get('#sendButton').disabled,false);
  assert.equal(h.get('#messages').children.at(-1).textContent,'Olá');
});
