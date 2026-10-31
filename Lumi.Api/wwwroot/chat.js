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
  if (speech) states.push(speech.audio ? "A falar" : "A gerar áudio");
  status.textContent = states.join(" · ") || "Inativo";
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
function stopSpeech() {
  const job = speech;
  if (!job) return;
  speech = null;
  job.controller.abort();
  if (job.audio) {
    job.audio.onended = job.audio.onerror = null;
    job.audio.pause();
    job.audio.removeAttribute("src");
    job.audio.load();
  }
  if (job.url) URL.revokeObjectURL(job.url);
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
  let completed = false;
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
        reply.textContent += decoder.decode(value, { stream: true });
        reply.scrollIntoView({ block: "end" });
      }
      reply.textContent += decoder.decode();
      completed = true;
    } finally { reader.releaseLock(); }
  } catch (error) {
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
      // Deliberately detached: TTS does not hold the form or chat job open.
      if (completed && voiceEnabled && reply.textContent.trim()) void speakText(reply.textContent);
    }
  }
});
async function speakText(text) {
  stopSpeech();
  if (!voiceEnabled || !text.trim()) return;
  if (text.length > 4000) { showError("A resposta excede o limite de voz de 4000 caracteres. Podes lê-la no chat."); return; }
  const job = { controller: new AbortController() };
  speech = job;
  render();
  const timer = setTimeout(() => { job.timedOut = true; job.controller.abort(); }, 75000);
  try {
    const response = await fetch("/audio/speak", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text }), signal: job.controller.signal
    });
    if (!response.ok) throw new Error("synthesis failed");
    const blob = await response.blob();
    if (speech !== job) return;
    clearTimeout(timer);
    job.url = URL.createObjectURL(blob);
    job.audio = new Audio(job.url);
    job.audio.onended = () => { if (speech === job) stopSpeech(); };
    job.audio.onerror = () => {
      if (speech === job) { stopSpeech(); showError("Não foi possível reproduzir a voz. Tenta enviar outra mensagem."); }
    };
    await job.audio.play();
    if (speech === job) render();
  } catch (error) {
    if (speech === job) {
      const cancelled = job.controller.signal.aborted && !job.timedOut;
      stopSpeech();
      if (!cancelled) showError("Não foi possível gerar ou reproduzir a voz. Verifica o serviço local e as permissões de reprodução.");
    }
  } finally { clearTimeout(timer); }
}
window.addEventListener("pagehide", () => { cancelCapture(); stopSpeech(); chat?.controller.abort(); });
render();
