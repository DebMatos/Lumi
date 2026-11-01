const fs = require('node:fs');
const assert = require('node:assert/strict');

module.exports = async function measurePauses(send, evaluate, until) {
  await send('Page.navigate',{url:process.argv[2]});
  await until('document.readyState === "complete" && typeof prepareSpeech === "function"');
  const scenarios = [
    {name:'short',phrases:['Olá.','Tudo bem?','Vamos começar.']},
    {name:'long',phrases:[
      'Podemos organizar o teu dia com calma, começando pelas tarefas mais importantes e deixando tempo para descansar.',
      'Depois escolhemos uma tarefa de cada vez, para avançar sem pressa e manter a atenção no que interessa.',
      'Ao final do dia, podemos rever o que correu bem e ajustar o plano de amanhã.']}
  ];
  await evaluate(`window.diagnostics={requests:[],audio:[]};
    const originalFetch=window.fetch;
    window.fetch=async(url,options)=>{
      if(url==='/chat/stream')return new Response(window.testText);
      if(url!=='/audio/speak')return originalFetch(url,options);
      const item={text:JSON.parse(options.body).text,start:performance.now()};diagnostics.requests.push(item);
      const response=await originalFetch(url,options);item.status=response.status;item.headers=performance.now();
      const originalBlob=response.blob.bind(response);
      response.blob=async()=>{const blob=await originalBlob();item.ready=performance.now();
        const raw=await blob.arrayBuffer(),view=new DataView(raw);let pos=12,rate=0,channels=0,sampleWidth=0;
        while(pos+8<=raw.byteLength){const name=String.fromCharCode(...new Uint8Array(raw,pos,4));const length=view.getUint32(pos+4,true);
          if(name==='fmt '){channels=view.getUint16(pos+10,true);rate=view.getUint32(pos+12,true);sampleWidth=view.getUint16(pos+22,true);}
          if(name==='data' && sampleWidth===16){
            const count=length/2,windowSize=Math.max(1,Math.floor(rate*channels*.01));let first=-1,last=-1;
            for(let index=0;index<count;index+=windowSize){let sum=0,n=Math.min(windowSize,count-index);
              for(let j=0;j<n;j++){const sample=view.getInt16(pos+8+(index+j)*2,true)/32768;sum+=sample*sample;}
              if(Math.sqrt(sum/n)>.01){if(first<0)first=index;last=index+n;}}
            item.pcm={durationMs:1000*count/(rate*channels),leadingMs:1000*first/(rate*channels),trailingMs:1000*(count-last)/(rate*channels)};break;}
          pos+=8+length+length%2;}
        return blob;};return response;};
    const OriginalAudio=window.Audio;
    window.Audio=function(url){const audio=new OriginalAudio(url),item={created:performance.now()};diagnostics.audio.push(item);
      audio.addEventListener('playing',()=>{item.playing??=performance.now();item.durationMs=audio.duration*1000;});
      audio.addEventListener('ended',()=>{item.ended=performance.now();});return audio;};
  `);
  const reports=[];
  for(const scenario of scenarios) {
    await evaluate(`document.querySelector('#newChat').click();window.diagnostics={requests:[],audio:[]};
      if(!voiceEnabled)document.querySelector('#voiceToggle').click();
      window.testText=${JSON.stringify(scenario.phrases.join(' '))};document.querySelector('#messageInput').value='Diagnóstico de pausas';
      document.querySelector('#sendButton').click();`);
    let finished=false;
    for(let i=0;i<2400;i++){
      if(await evaluate('document.querySelector("#error").textContent !== ""'))throw new Error(await evaluate('document.querySelector("#error").textContent'));
      if(await evaluate(`diagnostics.audio.length === ${scenario.phrases.length} && diagnostics.audio.every(item=>item.ended!==undefined) && !chat && !speech`)){finished=true;break;}
      await new Promise(resolve=>setTimeout(resolve,50));
    }
    assert.ok(finished,'diagnostic scenario did not complete');
    const raw=await evaluate('diagnostics');
    const report={name:scenario.name,requests:raw.requests.map((r,index)=>({index:index+1,text:r.text,status:r.status,
      requestMs:Math.round(r.ready-r.start),requestStart:Math.round(r.start-raw.requests[0].start),...r.pcm})),
      playback:raw.audio.map((a,index)=>({index:index+1,playing:Math.round(a.playing-raw.requests[0].start),
        ended:Math.round(a.ended-raw.requests[0].start),durationMs:Math.round(a.durationMs),
        gapAfterPreviousMs:index?Math.round(a.playing-raw.audio[index-1].ended):null,
        nextRequestedBeforeEndMs:index<raw.audio.length-1?Math.round(a.ended-raw.requests[index+1].start):null,
        nextReadyBeforeEndMs:index<raw.audio.length-1?Math.round(a.ended-raw.requests[index+1].ready):null}))};
    reports.push(report);console.log(JSON.stringify(report));
  }
  // Demonstrate the endpoint limit while a real synthesis is in progress.
  const concurrency=await evaluate(`(async()=>{
    const body=JSON.stringify({text:'Podemos organizar o teu dia com calma, começando pelas tarefas mais importantes e deixando tempo para descansar.'}),options={method:'POST',headers:{'Content-Type':'application/json'},body};
    const first=fetch('/audio/speak',options);await new Promise(resolve=>setTimeout(resolve,100));
    const second=await fetch('/audio/speak',options);const firstResponse=await first;await firstResponse.blob();
    return {first:firstResponse.status,overlapping:second.status};})()`);
  fs.writeFileSync('/tmp/lumi-tts-after-persistent.json',JSON.stringify({reports,concurrency},null,2));
  console.log(JSON.stringify({concurrency}));
  assert.deepEqual(concurrency,{first:200,overlapping:200});
};
