"use strict";
const form = document.querySelector("#chatForm");
const input = document.querySelector("#messageInput");
const messages = document.querySelector("#messages");
const sendButton = document.querySelector("#sendButton");
const micButton = document.querySelector("#micButton");
const voiceToggle = document.querySelector("#voiceToggle");
const cancelButton = document.querySelector("#cancelCapture");
const stopButton = document.querySelector("#stopSpeech");
const status = document.querySelector("#status");
const errorBox = document.querySelector("#error");
let voiceEnabled = false;
let capture = null;
let speech = null;
let chat = null;
let sessionId = sessionStorage.getItem("lumi-session") || crypto.randomUUID();
sessionStorage.setItem("lumi-session", sessionId);

// Independent jobs can coexist: speaking never disables writing or submitting.
function render() {
  const states = [];
  if (capture) states.push(capture.state);
  if (chat) states.push("A gerar resposta");
  if (speech?.audio) states.push("A falar");
  else if (speech?.synthesizing || speech?.ready) states.push("A gerar áudio");
  status.textContent = states.join(" · ") || "Inativo";
  status.hidden = states.length === 0;
  stopButton.hidden = !speech?.audio;
  sendButton.disabled = !!chat || !!capture;
  micButton.disabled = !!chat || (!!capture && capture.state !== "A gravar");
  micButton.textContent = capture?.state === "A gravar" ? "Terminar gravação" : "🎙️";
  micButton.setAttribute("aria-label", capture?.state === "A gravar" ? "Terminar gravação" : "Gravar áudio");
  cancelButton.disabled = !capture;
  cancelButton.textContent = capture?.state === "A transcrever" ? "Cancelar transcrição" : "Cancelar gravação";
  stopButton.disabled = !speech;
  voiceToggle.textContent = voiceEnabled ? "Voz ligada" : "Voz desligada";
  voiceToggle.setAttribute("aria-pressed", String(voiceEnabled));
}
function showError(message) { errorBox.textContent = message; }
function releaseMicrophone(job) {
  clearTimeout(job.timer);
  job.stream?.getTracks().forEach(track => track.stop());
}
function cancelCapture() {
  const job = capture;
  if (!job) return;
  capture = null; // Invalidate before stop events or late permission results.
  job.controller.abort();
  if (job.recorder?.state !== "inactive" && job.recorder) job.recorder.stop();
  releaseMicrophone(job);
  render();
}
function releaseSpeechAudio(job) {
  if (job.audio) {
    job.audio.onended = job.audio.onerror = null;
    job.audio.pause();
    job.audio.removeAttribute("src");
    job.audio.load();
    job.audio = null;
  }
  if (job.url) URL.revokeObjectURL(job.url);
  job.url = null;
}
function cancelSpeech(job) {
  if (!job) return;
  job.controller.abort();
  clearTimeout(job.timer);
  job.pending.length = 0;
  job.buffer = "";
  job.ready = null;
  releaseSpeechAudio(job);
  if (speech === job) speech = null;
}
function stopSpeech() {
  // Silence this entire response, including sentences not yet received.
  if (chat) {
    chat.voiceStopped = true;
    cancelSpeech(chat.voice);
  }
  cancelSpeech(speech);
  render();
}
voiceToggle.addEventListener("click", () => {
  voiceEnabled = !voiceEnabled;
  if (!voiceEnabled) stopSpeech();
  render();
});
stopButton.addEventListener("click", stopSpeech);
cancelButton.addEventListener("click", cancelCapture);

async function transcribe(job) {
  releaseMicrophone(job);
  if (capture !== job) return;
  job.state = "A transcrever";
  render();
  const timer = setTimeout(() => { job.timedOut = true; job.controller.abort(); }, 135000);
  try {
    const blob = new Blob(job.chunks, { type: job.recorder.mimeType });
    if (!blob.size || blob.size > 10 * 1024 * 1024) throw new Error("invalid audio");
    const data = new FormData();
    data.append("audio", blob, "recording");
    const response = await fetch("/audio/transcribe", { method: "POST", body: data, signal: job.controller.signal });
    if (!response.ok) throw new Error("transcription failed");
    const result = await response.json();
    if (capture !== job) return;
    if (typeof result.text !== "string" || !result.text.trim()) {
      showError("Não foi detetada fala. Tenta gravar novamente.");
      return;
    }
    // Append to the CURRENT draft so typing during transcription is preserved.
    input.value += (input.value && !/\s$/.test(input.value) ? " " : "") + result.text.trim();
  } catch (error) {
    if (capture === job && (!job.controller.signal.aborted || job.timedOut))
      showError(job.timedOut ? "A transcrição demorou demasiado. Tenta novamente." : "Não foi possível transcrever. Usa um áudio até 60 segundos e 10 MB.");
  } finally {
    clearTimeout(timer);
    if (capture === job) { capture = null; render(); input.focus(); }
  }
}
micButton.addEventListener("click", async () => {
  if (chat) return;
  if (capture) {
    if (capture.state === "A gravar") {
      capture.state = "A transcrever";
      capture.recorder.stop();
      releaseMicrophone(capture);
      render();
    }
    return;
  }
  showError("");
  stopSpeech(); // Do not record Lumi's own voice.
  const job = { controller: new AbortController(), state: "A abrir microfone", chunks: [], bytes: 0 };
  capture = job;
  render();
  try {
    if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === "undefined")
      throw new Error("unsupported recording");
    job.stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    if (capture !== job) { releaseMicrophone(job); return; }
    const type = ["audio/webm;codecs=opus", "audio/ogg;codecs=opus", "audio/mp4"]
      .find(value => MediaRecorder.isTypeSupported(value));
    if (!type) throw new Error("unsupported format");
    job.recorder = new MediaRecorder(job.stream, { mimeType: type });
    job.recorder.addEventListener("dataavailable", event => {
      if (capture !== job || !event.data.size) return;
      job.bytes += event.data.size;
      if (job.bytes > 10 * 1024 * 1024) {
        cancelCapture();
        showError("A gravação excedeu 10 MB. Grava uma mensagem mais curta.");
      } else job.chunks.push(event.data);
    });
    job.recorder.addEventListener("error", () => {
      if (capture !== job) return;
      cancelCapture();
      showError("A gravação falhou. Verifica o microfone e tenta novamente.");
    });
    job.recorder.addEventListener("stop", () => { void transcribe(job); });
    job.recorder.start(250);
    job.state = "A gravar";
    job.timer = setTimeout(() => {
      if (capture !== job) return;
      job.state = "A transcrever";
      job.recorder.stop();
      releaseMicrophone(job);
      render();
    }, 60000);
    render();
  } catch (error) {
    releaseMicrophone(job);
    if (capture === job) {
      capture = null;
      showError("Não foi possível gravar. Verifica a permissão do microfone e usa localhost ou HTTPS num navegador compatível.");
      render();
    }
  }
});

function addMessage(content, role) {
  const element = document.createElement("div");
  element.className = `message ${role}`;
  element.textContent = content;
  messages.appendChild(element);
  return element;
}
document.querySelector("#newChat").addEventListener("click", () => {
  const oldChat = chat;
  chat = null;
  oldChat?.controller.abort();
  cancelSpeech(oldChat?.voice);
  cancelCapture();
  stopSpeech();
  sessionId = crypto.randomUUID();
  sessionStorage.setItem("lumi-session", sessionId);
  messages.replaceChildren();
  // Keep the draft; late completions may not change this session or its UI.
  showError("");
  render();
  input.focus();
});
form.addEventListener("submit", async event => {
  event.preventDefault();
  if (chat || capture || event.isComposing) return;
  const message = input.value.trim();
  if (!message) { showError("Escreve uma mensagem antes de enviar."); input.focus(); return; }
  showError("");
  const job = { controller: new AbortController(), sessionId };
  chat = job;
  addMessage(message, "user");
  input.value = "";
  const reply = addMessage("", "lumi");
  render();
  const timer = setTimeout(() => { job.timedOut = true; job.controller.abort(); }, 300000);
  try {
    const response = await fetch("/chat/stream", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ message, sessionId: job.sessionId }), signal: job.controller.signal
    });
    if (!response.ok || !response.body) throw new Error("chat failed");
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (chat !== job) return;
        if (done) break;
        const chunk = decoder.decode(value, { stream: true });
        reply.textContent += chunk;
        feedSpeech(job, chunk);
        reply.scrollIntoView({ block: "end" });
      }
      const tail = decoder.decode();
      reply.textContent += tail;
      feedSpeech(job, tail, true);
    } finally { reader.releaseLock(); }
  } catch (error) {
    cancelSpeech(job.voice);
    if (chat === job && (!job.controller.signal.aborted || job.timedOut)) {
      reply.textContent += "\nNão foi possível concluir a resposta. Tenta novamente.";
      if (!input.value) input.value = message;
    }
  } finally {
    clearTimeout(timer);
    if (chat === job) {
      chat = null;
      render();
      input.focus();
    }
  }
});
// Conservative boundaries: wait for whitespace after punctuation, protect
// decimals, initials and common Portuguese abbreviations across chunk edges.
function speechSegments(text, final = false) {
  const sentences = [];
  const abbreviations = new Set([
    "sr", "sra", "srta", "dr", "dra", "prof", "profa", "eng", "engº", "engª",
    "exmo", "exma", "av", "pág", "págs", "art", "arts", "n", "nº", "n.º", "n.ª", "no", "etc",
    "p.ex", "ex", "e.g", "i.e", "vs", "aprox", "tel", "ref", "vol", "fig", "séc",
    "jan", "fev", "mar", "abr", "jun", "jul", "ago", "set", "out", "nov", "dez"
  ]);
  let start = 0;
  const endings = /[.!?…]+["'»”’)\]`*]*/gu;
  for (const match of text.matchAll(endings)) {
    const end = match.index + match[0].length;
    if (end === text.length && !final) continue;
    if (end < text.length && !/\s/u.test(text[end])) continue;
    const punctuation = match[0].match(/^[.!?…]+/u)[0];
    if (punctuation === ".") {
      const before = text.slice(start, match.index + 1);
      const word = before.match(/([\p{L}\dºª]+(?:\.[\p{L}]+)*)\.$/u)?.[1] || "";
      if (abbreviations.has(word.toLowerCase()) || /^\p{Lu}$/u.test(word)
        || /^(?:\p{L}\.)+\p{L}$/u.test(word)) continue;
      if (/^\d+$/u.test(word)) {
        const next = text.slice(end).trimStart();
        if (/^\s*\d+\.$/u.test(before) || (!final && !next) || /^[\p{Ll}\d]/u.test(next)) continue;
      }
    }
    if (/^(?:\.{2,}|…)/u.test(punctuation)) {
      const next = text.slice(end).trimStart();
      if ((!final && !next) || /^\p{Ll}/u.test(next)) continue;
    }
    const sentence = text.slice(start, end).trim();
    if (sentence) sentences.push(sentence);
    start = end;
  }
  let rest = text.slice(start).trimStart();
  if (final && rest.trim()) { sentences.push(rest.trim()); rest = ""; }
  // Exceptional sentences without punctuation still respect the endpoint limit.
  // Split at whitespace where possible, never inside a UTF-16 surrogate pair.
  const bounded = [];
  function splitLong(value) {
    while (value.length > 4000) {
      let cut = value.slice(0, 4001).search(/\s+\S*$/u);
      if (cut < 2000) cut = 4000;
      if (/[\uD800-\uDBFF]/u.test(value[cut - 1])) cut--;
      bounded.push(value.slice(0, cut).trim());
      value = value.slice(cut).trimStart();
    }
    return value;
  }
  for (const sentence of sentences) {
    const tail = splitLong(sentence);
    if (tail) bounded.push(tail);
  }
  rest = splitLong(rest);
  return { sentences: bounded, rest };
}
function feedSpeech(chatJob, chunk, final = false) {
  if (!voiceEnabled || chatJob.voiceStopped || chatJob.sessionId !== sessionId) return;
  const job = chatJob.voice ||= {
    controller: new AbortController(), sessionId, buffer: "", pending: [],
    ready: null, audio: null, synthesizing: false, finished: false
  };
  if (job.controller.signal.aborted) return;
  const result = speechSegments(job.buffer + chunk, final);
  job.buffer = result.rest;
  job.finished = final;
  job.pending.push(...result.sentences);
  if (job.pending.length && speech !== job) {
    cancelSpeech(speech); // A new spoken response replaces the previous one.
    speech = job;
  }
  if (speech === job) { void prepareSpeech(job); finishSpeech(job); }
}
function currentSpeech(job) {
  return speech === job && job.sessionId === sessionId && !job.controller.signal.aborted;
}
function finishSpeech(job) {
  if (currentSpeech(job) && job.finished && !job.synthesizing && !job.audio && !job.ready && !job.pending.length)
    speech = null;
  render();
}
function speechFailure(job) {
  if (!currentSpeech(job)) return;
  cancelSpeech(job);
  showError("Não foi possível gerar ou reproduzir a voz. Verifica o serviço local e as permissões de reprodução.");
  render();
}
function playNextSpeech(job) {
  if (!currentSpeech(job) || job.audio || !job.ready) return;
  try {
    job.url = URL.createObjectURL(job.ready);
    job.ready = null;
    const audio = job.audio = new Audio(job.url);
    audio.onended = () => {
      if (!currentSpeech(job)) return;
      releaseSpeechAudio(job);
      playNextSpeech(job);
      void prepareSpeech(job);
      finishSpeech(job);
    };
    audio.onerror = () => speechFailure(job);
    // Do not await playback: visual streaming and synthesis have separate lifetimes.
    audio.play().then(() => { if (currentSpeech(job)) render(); }).catch(() => speechFailure(job));
    render();
  } catch (error) { speechFailure(job); }
}
async function prepareSpeech(job) {
  // One request at a time and at most one prepared Blob ahead of playback.
  if (!currentSpeech(job) || job.synthesizing || job.ready || !job.pending.length) return;
  job.synthesizing = true;
  const text = job.pending.shift();
  job.timer = setTimeout(() => speechFailure(job), 75000);
  render();
  try {
    let response;
    for (let attempt = 0; attempt < 4; attempt++) {
      response = await fetch("/audio/speak", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ text }), signal: job.controller.signal
      });
      if (response.status !== 429 || attempt === 3) break;
      await response.body?.cancel();
      // An aborted previous process may still be releasing the server's slot.
      await new Promise((resolve, reject) => {
        const signal = job.controller.signal;
        const abort = () => { clearTimeout(timer); reject(new DOMException("Aborted", "AbortError")); };
        const timer = setTimeout(() => { signal.removeEventListener("abort", abort); resolve(); }, 150 * (attempt + 1));
        signal.addEventListener("abort", abort, { once: true });
        if (signal.aborted) abort();
      });
    }
    if (!response.ok) throw new Error("synthesis failed");
    const blob = await response.blob();
    if (!currentSpeech(job)) return;
    job.ready = blob;
  } catch (error) {
    speechFailure(job);
  } finally {
    clearTimeout(job.timer);
    job.synthesizing = false;
    if (currentSpeech(job)) {
      playNextSpeech(job);
      void prepareSpeech(job);
      finishSpeech(job);
    }
  }
}
window.addEventListener("pagehide", () => { cancelCapture(); stopSpeech(); chat?.controller.abort(); });
render();
