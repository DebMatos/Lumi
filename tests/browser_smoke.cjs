// Real Chrome/DOM/playback smoke, with deterministic network responses.
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const assert = require('node:assert/strict');
const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'lumi-chrome-'));
const browser = spawn('google-chrome', ['--headless', '--no-sandbox', '--disable-gpu', '--no-first-run',
  '--autoplay-policy=no-user-gesture-required', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], {stdio:['ignore','ignore','pipe']});
let ws;
(async()=>{
  const endpoint = await new Promise((resolve,reject)=>{
    let log='';const timer=setTimeout(()=>reject(new Error('Chrome startup timeout')),15000);
    browser.stderr.on('data',chunk=>{log+=chunk;const match=log.match(/DevTools listening on (ws:\/\/\S+)/);if(match){clearTimeout(timer);resolve(match[1]);}});
    browser.once('error', reject);
  });
  const targets = await (await fetch(endpoint.replace('ws:', 'http:').replace(/\/devtools\/browser\/.*/, '/json'))).json();
  ws=new WebSocket(targets.find(t=>t.type==='page').webSocketDebuggerUrl);
  await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject;});
  let id=0;const pending=new Map();
  let scriptOverride;
  ws.onmessage=event=>{const value=JSON.parse(event.data);
    if(value.method === 'Fetch.requestPaused') {
      send('Fetch.fulfillRequest', {requestId:value.params.requestId,responseCode:200,
        responseHeaders:[{name:'Content-Type',value:'text/javascript'}],body:Buffer.from(scriptOverride).toString('base64')}).catch(console.error);
    }
    if(value.id){const waiter=pending.get(value.id);pending.delete(value.id);value.error?waiter.reject(value.error):waiter.resolve(value.result);}};
  function send(method,params={}){return new Promise((resolve,reject)=>{const key=++id;pending.set(key,{resolve,reject});ws.send(JSON.stringify({id:key,method,params}));});}
  async function evaluate(expression){const result=await send('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(result.exceptionDetails)throw new Error(JSON.stringify(result.exceptionDetails));return result.result.value;}
  async function until(expression){for(let i=0;i<100;i++){if(await evaluate(expression))return;await new Promise(r=>setTimeout(r,50));}throw new Error('Timed out: '+expression);}
  await send('Page.enable');
  if (process.argv[3] === '--pauses') {
    await require('./browser_voice_pauses.cjs')(send, evaluate, until);
    return;
  }
  if (process.argv[3] === '--latency') {
    await send('Page.navigate',{url:process.argv[2]});
    await until('document.readyState === "complete" && typeof prepareSpeech === "function"');
    const recording = await evaluate(`(async()=>{
      const start=performance.now(), chunks=[];
      const response=await fetch('/chat/stream',{method:'POST',headers:{'Content-Type':'application/json'},
        body:JSON.stringify({sessionId:crypto.randomUUID(),message:'Responde em português europeu, com exatamente três frases curtas, sem listas: explica por que uma caminhada diária ajuda.'})});
      if(!response.ok)throw new Error('LLM recording failed');
      const reader=response.body.getReader(), decoder=new TextDecoder();
      while(true){const {done,value}=await reader.read();if(done)break;chunks.push({at:performance.now()-start,text:decoder.decode(value,{stream:true})});}
      const tail=decoder.decode();if(tail)chunks.push({at:performance.now()-start,text:tail});
      return {chunks,duration:performance.now()-start};
    })()`);
    // Warm OS/Python caches once; the server still starts a new Piper per sentence.
    await evaluate(`fetch('/audio/speak',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({text:'Olá.'})}).then(async r=>{if(!r.ok)throw new Error('Piper warmup failed');await r.blob();})`);
    await send('Fetch.enable',{patterns:[{urlPattern:'*/chat.js'}]});
    const results=[];
    for(const [mode,source] of [['before',process.argv[4]],['after','Lumi.Api/wwwroot/chat.js']]) {
      scriptOverride=fs.readFileSync(source,'utf8');
      await send('Page.navigate',{url:'about:blank'});
      await send('Page.navigate',{url:process.argv[2]});
      await until('document.readyState === "complete" && document.querySelector("#status")?.textContent === "Inativo"');
      await evaluate(`window.metrics={};
        const originalPlay=HTMLMediaElement.prototype.play;
        HTMLMediaElement.prototype.play=function(){this.addEventListener('playing',()=>{metrics.firstVoiceMs??=performance.now()-metrics.sentAt;},{once:true});return originalPlay.call(this);};
        const originalFetch=window.fetch;
        window.fetch=async(url,options)=>{
          if(url!=='/chat/stream')return originalFetch(url,options);
          const chunks=${JSON.stringify(recording.chunks)},start=performance.now();
          return new Response(new ReadableStream({async start(controller){
            for(const chunk of chunks){await new Promise(resolve=>setTimeout(resolve,Math.max(0,start+chunk.at-performance.now())));controller.enqueue(new TextEncoder().encode(chunk.text));}
            controller.close();
          }}));
        };
        document.querySelector('#voiceToggle').click();document.querySelector('#messageInput').value='Caminhada diária';
        metrics.sentAt=performance.now();document.querySelector('#sendButton').click();`);
      for(let i=0;i<1800;i++) {
        if(await evaluate('metrics.firstVoiceMs !== undefined'))break;
        if(await evaluate('document.querySelector("#error").textContent !== ""'))throw new Error(await evaluate('document.querySelector("#error").textContent'));
        await new Promise(resolve=>setTimeout(resolve,50));
      }
      const time=await evaluate('metrics.firstVoiceMs');
      assert.ok(Number.isFinite(time), 'no real playback: '+mode);
      results.push({mode,firstVoiceMs:Math.round(time)});
      await evaluate('document.querySelector("#voiceToggle").click(); document.querySelector("#newChat").click();');
      await new Promise(resolve=>setTimeout(resolve,300));
    }
    const report={scenario:'recorded Ollama lumi-v3 stream, real Piper, Chrome playing event',
      firstTextMs:Math.round(recording.chunks[0]?.at || 0),llmStreamMs:Math.round(recording.duration),
      chunkCount:recording.chunks.length,results,
      reductionPercent:Math.round((1-results[1].firstVoiceMs/results[0].firstVoiceMs)*100)};
    fs.writeFileSync('/tmp/lumi-voice-real-latency.json',JSON.stringify({report,recording},null,2));
    console.log(JSON.stringify(report));
    return;
  }
  await send('Page.navigate',{url:process.argv[2]});
  await until('typeof prepareSpeech === "function" && document.querySelector("#status").textContent === "Inativo"');
  // A real playable WAV, long enough to submit another message while it plays.
  const wav=Buffer.alloc(44+16000*2*5);wav.write('RIFF');wav.writeUInt32LE(wav.length-8,4);wav.write('WAVEfmt ',8);wav.writeUInt32LE(16,16);wav.writeUInt16LE(1,20);wav.writeUInt16LE(1,22);wav.writeUInt32LE(16000,24);wav.writeUInt32LE(32000,28);wav.writeUInt16LE(2,32);wav.writeUInt16LE(16,34);wav.write('data',36);wav.writeUInt32LE(wav.length-44,40);
  await evaluate(`window.fetch = async (url, options) => {
    if(url === '/chat/stream') return new Response('Olá do navegador');
    if(url === '/audio/speak') return new Response(Uint8Array.from(atob('${wav.toString('base64')}'),c=>c.charCodeAt(0)),{headers:{'Content-Type':'audio/wav'}});
    throw new Error('unexpected request');
  };
  document.querySelector('#voiceToggle').click();
  document.querySelector('#messageInput').value='teste';
  document.querySelector('#sendButton').click();`);
  await until('document.querySelector("#status").textContent === "A falar"');
  assert.equal(await evaluate('document.querySelector("#sendButton").disabled'),false);
  await evaluate('document.querySelector("#messageInput").value="outra"; document.querySelector("#sendButton").click()');
  await until('document.querySelectorAll(".lumi").length === 2 && document.querySelector("#status").textContent === "A falar"');
  await evaluate('document.querySelector("#voiceToggle").click()');
  await until('document.querySelector("#status").textContent === "Inativo"');
  assert.equal(await evaluate('document.querySelector("#error").textContent'),'');
  await evaluate('document.querySelector("#messageInput").value="rascunho"; document.querySelector("#newChat").click()');
  assert.equal(await evaluate('document.querySelector("#messageInput").value'),'rascunho');
  for(const width of [1280,320]){
    await send('Emulation.setDeviceMetricsOverride',{width,height:800,deviceScaleFactor:1,mobile:false});
    assert.equal(await evaluate('document.documentElement.scrollWidth <= innerWidth'),true);
    const shot=await send('Page.captureScreenshot',{format:'png'});
    fs.writeFileSync(`/tmp/lumi-${width}.png`,Buffer.from(shot.data,'base64'));
  }
  console.log('PASS Chrome: script, actual playback, submit while speaking, replacement, voice off, draft, 320px/desktop layout');
})().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{
  ws?.close();browser.kill();
  await new Promise(resolve=>browser.exitCode!==null?resolve():browser.once('exit',resolve));
  fs.rmSync(profile,{recursive:true,force:true});
});
