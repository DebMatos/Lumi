const vm = require('node:vm');
const fs = require('node:fs');
function deferred() { let resolve, reject; const promise = new Promise((a,b) => { resolve=a; reject=b; }); return { promise, resolve, reject }; }
function setup({ sourcePath = "Lumi.Api/wwwroot/chat.js", onPlay = () => {} } = {}) {
  class Element {
    constructor() { this.value=''; this.textContent=''; this.disabled=false; this.children=[]; this.events={}; }
    addEventListener(name, fn) { this.events[name]=fn; }
    setAttribute() {} focus() {} scrollIntoView() {}
    appendChild(child) { this.children.push(child); }
    replaceChildren() { this.children=[]; }
    fire(name) { return this.events[name]?.({ preventDefault() {} }); }
  }
  const elements = {};
  const get = id => elements[id] ||= new Element();
  const requests=[], audios=[], revoked=[], recorders=[], tracks=[];
  let permission;
  class Recorder {
    static isTypeSupported() { return true; }
    constructor(stream, options) { this.mimeType=options.mimeType; this.state='inactive'; this.events={}; recorders.push(this); }
    addEventListener(name, fn) { this.events[name]=fn; }
    start() { this.state='recording'; }
    stop() { this.state='inactive'; queueMicrotask(() => { this.events.dataavailable({ data: new Blob(['test']) }); this.events.stop(); }); }
  }
  const context = vm.createContext({
    document: { querySelector:get, createElement:() => new Element() },
    window: { addEventListener() {} },
    navigator: { mediaDevices: { getUserMedia: () => {
      if (permission) return permission.promise;
      const track={stopped:false,stop(){this.stopped=true;}}; tracks.push(track);
      return Promise.resolve({getTracks:()=>[track]});
    } } },
    MediaRecorder:Recorder,
    sessionStorage:{getItem(){return null;},setItem(){}},
    crypto:{randomUUID:require('node:crypto').randomUUID},
    fetch:(url, options) => {const d=deferred(); requests.push({url,options,...d}); return d.promise;},
    Audio:class {constructor(url){this.url=url;audios.push(this);} play(){onPlay(this);return Promise.resolve();} pause(){this.paused=true;} removeAttribute(){} load(){}},
    URL:{createObjectURL:()=>`blob:${audios.length}`,revokeObjectURL:url=>revoked.push(url)},
    AbortController, Blob, FormData, TextDecoder, DOMException, console, setTimeout, clearTimeout
  });
  vm.runInContext(fs.readFileSync(sourcePath,'utf8'),context);
  return {get,requests,audios,revoked,recorders,tracks,context,setPermission:d=>permission=d};
}
function streamed(text) {
  return {ok:true,body:{getReader(){let read=false;return {async read(){if(read)return{done:true};read=true;return{done:false,value:new TextEncoder().encode(text)};},releaseLock(){}};}}};
}
async function reply(h,text='Olá') {
  h.get('#messageInput').value='pergunta';
  const done=h.get('#chatForm').fire('submit');
  h.requests.at(-1).resolve(streamed(text));
  await done;
}

module.exports = { setup, deferred, streamed, reply };
