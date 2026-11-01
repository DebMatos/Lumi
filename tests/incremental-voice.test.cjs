const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { setup, deferred } = require('./chat-harness.cjs');
const flush = () => new Promise(resolve => setImmediate(resolve));
const speechRequests = h => h.requests.filter(r => r.url === '/audio/speak');
const texts = h => speechRequests(h).map(r => JSON.parse(r.options.body).text);
async function liveChat(h) {
  h.get('#voiceToggle').fire('click');
  h.get('#messageInput').value = 'pergunta';
  const done = h.get('#chatForm').fire('submit');
  const reads = [];
  h.requests[0].resolve({ok:true,body:{getReader:()=>({
    read() { const d=deferred(); reads.push(d); return d.promise; }, releaseLock(){}
  })}});
  await flush();
  return {
    async chunk(text) { reads.shift().resolve({done:false,value:new TextEncoder().encode(text)}); await flush(); },
    async end() { reads.shift()?.resolve({done:true}); await done; await flush(); },
    async fail() { reads.shift().reject(new Error('stream failed')); await done; await flush(); }
  };
}
async function synth(h, index = 0) {
  speechRequests(h)[index].resolve({ok:true,blob:async()=>new Blob(['wav'])});
  await flush();
}
function segment(h,text,final=false) {
  return JSON.parse(vm.runInContext(`JSON.stringify(speechSegments(${JSON.stringify(text)},${final}))`,h.context));
}
test('sentence boundaries protect Portuguese abbreviations, initials, numbers, URLs and quotes',async()=>{
  const h=await setup();
  const cases=[
    ['O Sr. Silva falou com a Dra. Ana. Tudo bem?', ['O Sr. Silva falou com a Dra. Ana.','Tudo bem?']],
    ['O preço é 3.14 euros. Certo!', ['O preço é 3.14 euros.','Certo!']],
    ['Tenho 3.14. Segue.', ['Tenho 3.14.','Segue.']],
    ['O J. P. Silva chegou. Olá.', ['O J. P. Silva chegou.','Olá.']],
    ['Usa p.ex. arroz, etc. para o jantar. Sim.', ['Usa p.ex. arroz, etc. para o jantar.','Sim.']],
    ['Acede a https://exemplo.pt e escreve a a@b.pt. Depois sai.', ['Acede a https://exemplo.pt e escreve a a@b.pt.','Depois sai.']],
    ['Ela disse: “Olá!” Depois saiu.', ['Ela disse: “Olá!”','Depois saiu.']],
    ['Espera... ainda penso. Sim?! Tudo bem.', ['Espera... ainda penso.','Sim?!','Tudo bem.']],
    ['1. Primeiro ponto. 2. Segundo ponto.', ['1. Primeiro ponto.','2. Segundo ponto.']],
    ['O n.º 3 está pronto. Sim.', ['O n.º 3 está pronto.','Sim.']]
  ];
  for (const [text, expected] of cases) assert.deepEqual(segment(h,text,true).sentences,expected,text);
});
test('chunk boundaries never duplicate or lose sentences, punctuation or Unicode',async()=>{
  const h=await setup();
  const text='A Dra. Ana pagou 3.14 euros. “Certo?!” Sim... vamos avançar. Olá 🌞';
  const expected=['A Dra. Ana pagou 3.14 euros.','“Certo?!”','Sim... vamos avançar.','Olá 🌞'];
  for(let cut=0;cut<=text.length;cut++) {
    const first=segment(h,text.slice(0,cut));
    const last=segment(h,first.rest+text.slice(cut),true);
    assert.deepEqual([...first.sentences,...last.sentences],expected,`cut ${cut}`);
  }
  let rest='',sentences=[];
  for (const char of text) {const part=segment(h,rest+char);rest=part.rest;sentences.push(...part.sentences);}
  sentences.push(...segment(h,rest,true).sentences);
  assert.deepEqual(sentences,expected);
});
test('first sentence speaks before end; next is prepared with one request and one Blob ahead',async()=>{
  const h=await setup(),stream=await liveChat(h);
  await stream.chunk('Primeira frase. ');
  assert.deepEqual(texts(h),['Primeira frase.']);
  await synth(h);
  assert.equal(h.audios.length,1);
  assert.match(h.get('#status').textContent,/A gerar resposta · A falar/);
  await stream.chunk('Segunda frase. Terceira frase. Restante');
  assert.deepEqual(texts(h),['Primeira frase.','Segunda frase.']);
  await synth(h,1);
  assert.equal(h.audios.length,1,'prepared second sentence must not overlap');
  assert.equal(speechRequests(h).length,2,'no third synthesis while the ready slot is full');
  await stream.end();
  assert.equal(h.get('#sendButton').disabled,false);
  h.audios[0].onended();await flush();
  assert.equal(h.audios.length,2);
  assert.deepEqual(texts(h),['Primeira frase.','Segunda frase.','Terceira frase.']);
  await synth(h,2);h.audios[1].onended();await flush();
  assert.equal(h.audios.length,3);assert.equal(texts(h)[3],'Restante');
  await synth(h,3);h.audios[2].onended();await flush();
  assert.equal(h.audios.length,4);h.audios[3].onended();
  assert.equal(h.get('#status').textContent,'Inativo');assert.equal(h.revoked.length,4);
});
test('cancel stops playback, aborts next synthesis, and silences the rest of the streaming response',async()=>{
  for (const action of ['#stopSpeech','#voiceToggle','#newChat']) {
    const h=await setup(),stream=await liveChat(h);
    await stream.chunk('Primeira. Segunda. ');await synth(h);
    const pending=speechRequests(h)[1];assert.ok(pending);
    h.get(action).fire('click');
    assert.equal(h.audios[0].paused,true);assert.equal(pending.options.signal.aborted,true);
    pending.resolve({ok:true,blob:async()=>new Blob(['late'])});await flush();
    if (action==='#voiceToggle') h.get('#voiceToggle').fire('click');
    await stream.chunk('Terceira. ');await stream.end();
    assert.equal(h.audios.length,1);assert.equal(speechRequests(h).length,2);
    assert.equal(h.get('#error').textContent,'');
  }
});
test('cancel discards prepared audio and late Blob completion',async()=>{
  const h=await setup(),stream=await liveChat(h);
  await stream.chunk('Primeira. Segunda. Terceira. ');await synth(h);await synth(h,1);
  h.get('#stopSpeech').fire('click');await stream.end();
  assert.equal(h.audios.length,1);assert.equal(h.revoked.length,1);
  const other=await setup(),another=await liveChat(other);
  await another.chunk('Olá. ');
  const body=deferred();speechRequests(other)[0].resolve({ok:true,blob:()=>body.promise});await flush();
  other.get('#newChat').fire('click');body.resolve(new Blob(['old']));await flush();
  await another.end();assert.equal(other.audios.length,0);
});
test('short final remainder is synthesized exactly once, silent responses do not make requests',async()=>{
  const h=await setup(),stream=await liveChat(h);
  await stream.chunk('Olá');assert.equal(speechRequests(h).length,0);
  await stream.end();assert.deepEqual(texts(h),['Olá']);await synth(h);h.audios[0].onended();
  const empty=await setup(),emptyStream=await liveChat(empty);await emptyStream.end();assert.equal(speechRequests(empty).length,0);
});
test('long unpunctuated text respects the endpoint limit without cutting surrogate pairs',async()=>{
  const h=await setup(),text=('palavra 🌞 ').repeat(1000);
  const result=segment(h,text,true);assert.ok(result.sentences.length>1);
  assert.ok(result.sentences.every(s=>s.length<=4000 && !/[\uD800-\uDBFF]$/.test(s) && !/^[\uDC00-\uDFFF]/.test(s)));
  assert.equal(result.sentences.join(' '),text.trim());
});
test('synthesis failure does not block text streaming or repeatedly retry later sentences',async()=>{
  const h=await setup(),stream=await liveChat(h);
  await stream.chunk('Primeira. ');speechRequests(h)[0].resolve({ok:false,status:503});await flush();
  await stream.chunk('Segunda. ');await stream.end();
  assert.equal(h.get('#messages').children.at(-1).textContent,'Primeira. Segunda. ');
  assert.equal(speechRequests(h).length,1);assert.match(h.get('#error').textContent,/Não foi possível/);
  assert.equal(h.get('#sendButton').disabled,false);
});
test('failed streaming cancels its queued and playing audio',async()=>{
  const h=await setup(),stream=await liveChat(h);
  await stream.chunk('Primeira. Segunda. ');await synth(h);await stream.fail();
  assert.equal(h.audios[0].paused,true);assert.equal(speechRequests(h)[1].options.signal.aborted,true);
});
test('busy server retry is bounded and abortable',async()=>{
  const h=await setup(),stream=await liveChat(h);
  await stream.chunk('Primeira. ');speechRequests(h)[0].resolve({ok:false,status:429});await flush();
  h.get('#stopSpeech').fire('click');await new Promise(resolve=>setTimeout(resolve,180));
  await stream.end();assert.equal(speechRequests(h).length,1);assert.equal(h.get('#error').textContent,'');
});
test('long response drains all sentences in order with bounded preparation',async()=>{
  const h=await setup(),stream=await liveChat(h);
  const expected=Array.from({length:40},(_,i)=>`Frase número ${i+1}!`);
  await stream.chunk(expected.join(' ')+' ');await stream.end();
  for(let index=0;index<expected.length;index++) {
    if(h.audios.length<=index) await synth(h,index);
    assert.equal(h.audios.length,index+1);
    assert.ok(speechRequests(h).length<=index+2,'at most the next sentence is requested');
    if(index+1<expected.length && speechRequests(h)[index+1]) await synth(h,index+1);
    h.audios[index].onended();await flush();
  }
  assert.deepEqual(texts(h),expected);assert.equal(h.revoked.length,expected.length);
  assert.equal(h.get('#status').textContent,'Inativo');
});
test('server contention retries the same sentence and stops after four attempts',async()=>{
  const h=await setup(),stream=await liveChat(h);await stream.chunk('Olá. ');await stream.end();
  for(let index=0;index<4;index++) {
    assert.equal(texts(h)[index],'Olá.');
    speechRequests(h)[index].resolve({ok:false,status:429});
    await new Promise(resolve=>setTimeout(resolve,index<3 ? 150*(index+1)+20 : 10));
  }
  assert.equal(speechRequests(h).length,4);assert.equal(h.audios.length,0);
  assert.match(h.get('#error').textContent,/Não foi possível/);
});
test('voice off before the first sentence suppresses future chunks even when re-enabled',async()=>{
  const h=await setup(),stream=await liveChat(h);await stream.chunk('Ainda sem ponto');
  h.get('#voiceToggle').fire('click');h.get('#voiceToggle').fire('click');
  await stream.chunk('. Outra frase. ');await stream.end();
  assert.equal(speechRequests(h).length,0);assert.equal(h.audios.length,0);
});
test('transient contention retries the same sentence without losing order',async()=>{
  const h=await setup(),stream=await liveChat(h);await stream.chunk('Primeira. Segunda. ');
  speechRequests(h)[0].resolve({ok:false,status:429});
  await new Promise(resolve=>setTimeout(resolve,180));
  assert.deepEqual(texts(h),['Primeira.','Primeira.']);
  await synth(h,1);assert.equal(h.audios.length,1);assert.equal(texts(h)[2],'Segunda.');
  h.get('#stopSpeech').fire('click');await stream.end();
});
