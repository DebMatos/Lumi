const vm = require('node:vm');
const fs = require('node:fs');
function deferred() { let resolve, reject; const promise = new Promise((a,b) => { resolve=a; reject=b; }); return { promise, resolve, reject }; }
function createStorage() {
  const items = new Map();
  return {
    get length() { return items.size; },
    key(index) { return [...items.keys()][index] ?? null; },
    getItem(key) { return items.get(String(key)) ?? null; },
    setItem(key, value) { items.set(String(key), String(value)); },
    removeItem(key) { items.delete(String(key)); },
    clear() { items.clear(); }
  };
}
async function setup({ sourcePath = "Lumi.Api/wwwroot/chat.js", onPlay = () => {}, pendingHistory = false,
  localStorage = createStorage(), sessionStorage = createStorage() } = {}) {
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
  const requests=[], historyRequests=[], audios=[], revoked=[], recorders=[], tracks=[];
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
    localStorage, sessionStorage,
    crypto:{randomUUID:require('node:crypto').randomUUID},
    fetch:(url, options) => {
      const d=deferred();
      if (url.startsWith('/conversations/')) {
        historyRequests.push({url,options,...d});
        if (!pendingHistory) d.resolve({ok:false,status:404});
      } else requests.push({url,options,...d});
      return d.promise;
    },
    Audio:class {constructor(url){this.url=url;audios.push(this);} play(){onPlay(this);return Promise.resolve();} pause(){this.paused=true;} removeAttribute(){} load(){}},
    URL:{createObjectURL:()=>`blob:${audios.length}`,revokeObjectURL:url=>revoked.push(url)},
    AbortController, Blob, FormData, TextDecoder, DOMException, console, setTimeout, clearTimeout
  });
  vm.runInContext(fs.readFileSync(sourcePath,'utf8'),context);
  // Let the default initial 404 settle before interaction-focused tests run.
  await new Promise(resolve => setImmediate(resolve));
  return {get,requests,historyRequests,audios,revoked,recorders,tracks,context,localStorage,sessionStorage,setPermission:d=>permission=d};
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

module.exports = { setup, createStorage, deferred, streamed, reply };
