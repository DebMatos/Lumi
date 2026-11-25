from collections import deque
import argparse
from contextlib import redirect_stdout
from datetime import datetime
import json
from pathlib import Path
import os
import queue
import signal
import subprocess
import sys
import tempfile
import threading
import wave

from lumi_client import LumiApiError, LumiClient
from streaming_speech import stream_and_speak

# Configuração de áudio
SAMPLE_RATE = 16000
CHUNK_SIZE = 512
SILENCE_SECONDS = 2.0
PRE_ROLL_SECONDS = 0.3
MAX_RECORDING_SECONDS = 60

SPEECH_THRESHOLD = 0.5
SILENCE_THRESHOLD = 0.35

OUTPUT_DIR = Path("/tmp/lumi-voice")
OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

model = None
np = None
sd = None
torch = None

audio_queue = queue.Queue(maxsize=100)
managed = False


def log(message):
    print(message, file=sys.stderr if managed else sys.stdout, flush=True)


def report(state, **details):
    if managed:
        print(json.dumps({"event": "state", "state": state, **details}), flush=True)
    else:
        log({
            "listening": "Lumi à escuta. Ctrl+C para terminar.",
            "recording": "A gravar...",
            "transcribing": "A transcrever...",
            "responding": "Lumi a responder...",
            "speaking": "A reproduzir resposta...",
            "error": details.get("error", "Erro."),
            "stopped": "Escuta terminada.",
        }.get(state, state))


def audio_callback(indata, frames, time_info, status):
    if status:
        log(f"Áudio: {status}")

    try:
        audio_queue.put_nowait(indata[:, 0].copy())
    except queue.Full:
        pass


def save_wav(chunks):
    samples = np.concatenate(chunks)
    samples = np.clip(samples, -1, 1)
    pcm = (samples * 32767).astype(np.int16)

    filename = OUTPUT_DIR / (
        f"lumi-{datetime.now():%Y%m%d-%H%M%S-%f}.wav"
    )

    with wave.open(str(filename), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(SAMPLE_RATE)
        wav.writeframes(pcm.tobytes())

    duration = len(samples) / SAMPLE_RATE
    log(f"Gravação guardada: {filename.name}")
    log(f"Duração: {duration:.2f}s")

    return filename


def play_audio(audio, stop_event):
    with tempfile.NamedTemporaryFile(
        suffix=".wav", prefix="lumi-reply-", delete=False
    ) as audio_file:
        audio_path = Path(audio_file.name)
        audio_file.write(audio)

    process = None
    try:
        if stop_event.is_set():
            return
        process = subprocess.Popen(["pw-play", str(audio_path)])
        while process.poll() is None:
            if stop_event.wait(0.05):
                process.terminate()
                try:
                    process.wait(timeout=1)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                return
        if process.returncode:
            raise subprocess.CalledProcessError(
                process.returncode, process.args
            )
    except BaseException:
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        raise
    finally:
        audio_path.unlink(missing_ok=True)


def process_recording(client, filename, player=play_audio):
    import time

    report("transcribing")
    started = time.perf_counter()

    try:
        transcription = client.transcribe(filename)
        elapsed = time.perf_counter() - started

        log(f"Tempo de transcrição: {elapsed:.2f}s")
        log(f"Tu: {transcription or '(sem texto)'}")

        if not transcription:
            return

        report("responding", transcription=transcription)

        result = stream_and_speak(
            client,
            transcription,
            player,
            on_speaking=lambda: report(
                "speaking", transcription=transcription
            ),
        )
        reply = result.reply
        log(f"Lumi: {reply}\\n")

        if result.speech_error is not None:
            report(
                "error",
                transcription=transcription,
                reply=reply,
                error=f"Erro na reprodução: {result.speech_error}",
            )
        elif reply:
            # O reply final faz a interface atualizar o histórico apenas
            # depois de /chat/stream o ter persistido por completo.
            report("speaking", transcription=transcription, reply=reply)

    except LumiApiError as error:
        report("error", error=f"Erro na API da Lumi: {error}")


def main():
    global managed, model, np, sd, torch
    parser = argparse.ArgumentParser()
    parser.add_argument("--managed", action="store_true")
    parser.add_argument("--session-id")
    parser.add_argument("--api-url", default=os.environ.get(
        "LUMI_API_URL", "http://127.0.0.1:5194"))
    args = parser.parse_args()
    managed = args.managed
    if managed and not args.session_id:
        parser.error("--session-id é obrigatório em modo gerido")

    try:
        import numpy as numpy_module
        import sounddevice as sounddevice_module
        import torch as torch_module
        from silero_vad import load_silero_vad

        np = numpy_module
        sd = sounddevice_module
        torch = torch_module
        torch.set_num_threads(1)
        if managed:
            with redirect_stdout(sys.stderr):
                model = load_silero_vad()
        else:
            model = load_silero_vad()
        model.reset_states()
    except ModuleNotFoundError as error:
        report(
            "error",
            error=(
                f"Dependência Python em falta: {error.name}. "
                "Configura o ambiente do VoiceListener."
            ),
        )
        return
    except Exception as error:
        report("error", error=f"Não foi possível carregar o Silero VAD: {error}")
        return

    try:
        client = LumiClient(args.api_url, session_id=args.session_id)
    except (OSError, ValueError) as error:
        report("error", error=f"Configuração inválida: {error}")
        return

    if managed:
        def stop_on_parent_close():
            for _line in sys.stdin:
                pass
            os.kill(os.getpid(), signal.SIGTERM)

        def stop_signal(_signum, _frame):
            raise KeyboardInterrupt

        signal.signal(signal.SIGTERM, stop_signal)
        threading.Thread(target=stop_on_parent_close, daemon=True).start()

    pre_roll = deque(
        maxlen=max(
            1,
            int(np.ceil(
                PRE_ROLL_SECONDS * SAMPLE_RATE / CHUNK_SIZE
            ))
        )
    )

    recording = False
    chunks = []
    silence_samples = 0
    recorded_samples = 0

    report("listening")

    try:
        with sd.InputStream(
            samplerate=SAMPLE_RATE,
            channels=1,
            dtype="float32",
            blocksize=CHUNK_SIZE,
            callback=audio_callback,
        ):
            while True:
                chunk = audio_queue.get()

                with torch.inference_mode():
                    probability = model(
                        torch.from_numpy(chunk),
                        SAMPLE_RATE
                    ).item()

                if not recording:
                    pre_roll.append(chunk)

                    if probability >= SPEECH_THRESHOLD:
                        recording = True
                        chunks = list(pre_roll)
                        recorded_samples = sum(
                            len(c) for c in chunks
                        )
                        silence_samples = 0

                        report("recording")

                    continue

                chunks.append(chunk)
                recorded_samples += len(chunk)

                if probability < SILENCE_THRESHOLD:
                    silence_samples += len(chunk)
                else:
                    silence_samples = 0

                finished = (
                    silence_samples >=
                    SILENCE_SECONDS * SAMPLE_RATE
                )

                timeout = (
                    recorded_samples >=
                    MAX_RECORDING_SECONDS * SAMPLE_RATE
                )

                if finished or timeout:
                    if timeout:
                        log("Limite de gravação atingido.")

                    filename = save_wav(chunks)

                    recording = False
                    chunks = []
                    silence_samples = 0
                    recorded_samples = 0

                    pre_roll.clear()
                    model.reset_states()

                    # Parar a captação durante a transcrição
                    # para não acumular áudio antigo na fila.
                    while not audio_queue.empty():
                        try:
                            audio_queue.get_nowait()
                        except queue.Empty:
                            break

                    process_recording(client, filename)

                    # Descartar áudio captado enquanto
                    # o Whisper estava a processar.
                    while not audio_queue.empty():
                        try:
                            audio_queue.get_nowait()
                        except queue.Empty:
                            break

                    model.reset_states()

                    report("listening")

    except KeyboardInterrupt:
        report("stopped")

    except sd.PortAudioError as error:
        report("error", error=f"Erro no dispositivo de áudio: {error}")


if __name__ == "__main__":
    main()
