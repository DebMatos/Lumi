"""Testes do cliente HTTP do VoiceListener sem rede, áudio ou modelos reais."""

import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import threading
import time
import uuid


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "lumi_client", ROOT / "voice-listener" / "lumi_client.py"
)
assert SPEC and SPEC.loader
lumi_client = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(lumi_client)
sys.path.insert(0, str(ROOT / "voice-listener"))
import streaming_speech
import listener as voice_listener


class Response:
    def __init__(self, body, status=200):
        self.chunks = list(body) if isinstance(body, list) else [body]
        self.status = status

    def read(self, _size=-1):
        body = b"".join(self.chunks)
        self.chunks.clear()
        return body

    def read1(self, _size=-1):
        return self.chunks.pop(0) if self.chunks else b""


class Connection:
    requests = []
    responses = [
        Response('{"text":"olá"}'.encode()),
        Response("resposta completa".encode()),
        Response(b"RIFFvoice"),
    ]

    def __init__(self, host, port, timeout):
        assert host == "127.0.0.1"
        assert port == 5194
        assert timeout == 135

    def request(self, method, path, body, headers):
        assert method == "POST"
        self.requests.append((path, headers, body))

    def getresponse(self):
        return self.responses.pop(0)

    def close(self):
        pass


def main():
    Connection.requests = []
    Connection.responses = [
        Response('{"text":"olá"}'.encode()),
        Response(["resposta ".encode(), "completa".encode()]),
        Response(b"RIFFvoice"),
    ]
    lumi_client.http.client.HTTPConnection = Connection

    with tempfile.TemporaryDirectory(prefix="lumi-listener-test-") as directory:
        directory = Path(directory)
        session_file = directory / "session"
        wav = directory / "recording.wav"
        wav.write_bytes(b"RIFFtest")

        client = lumi_client.LumiClient(
            "http://127.0.0.1:5194", session_file
        )
        first_session = client.session_id
        assert session_file.stat().st_mode & 0o777 == 0o600
        assert client.transcribe(wav) == "olá"
        assert client.chat("olá") == "resposta completa"
        assert client.speak("resposta completa") == b"RIFFvoice"

        transcription = Connection.requests[0]
        assert transcription[0] == "/audio/transcribe"
        assert b'name="audio"' in transcription[2]
        assert b"RIFFtest" in transcription[2]

        chat = json.loads(Connection.requests[1][2])
        assert Connection.requests[1][0] == "/chat/stream"
        assert chat == {"sessionId": first_session, "message": "olá"}
        assert json.loads(Connection.requests[2][2]) == {
            "text": "resposta completa"
        }

        restored = lumi_client.LumiClient(
            "http://127.0.0.1:5194", session_file
        )
        assert restored.session_id == first_session

        session_file.write_text("inválido", encoding="utf-8")
        replaced = lumi_client.LumiClient(
            "http://127.0.0.1:5194", session_file
        )
        assert uuid.UUID(replaced.session_id)
        assert replaced.session_id != first_session

        managed_id = str(uuid.uuid4())
        managed_file = directory / "managed-session"
        managed = lumi_client.LumiClient(
            "http://127.0.0.1:5194", managed_file, session_id=managed_id
        )
        assert managed.session_id == managed_id
        assert not managed_file.exists()

        Connection.responses = [
            Response([b"Ol\xc3", b"\xa1. ", "Tudo bem?".encode()])
        ]
        chunks = list(client.chat_stream("teste incremental"))
        assert len(chunks) > 1
        assert "".join(chunks) == "Olá. Tudo bem?"

    try:
        lumi_client.LumiClient("http://example.com:5194", Path("unused"))
        raise AssertionError("URL remoto deveria ter sido recusado")
    except ValueError:
        pass

    test_sentence_segmentation()
    test_incremental_pipeline()
    test_generation_failure_cancels_playback()
    test_listener_reports_streamed_reply()

    print("PASS VoiceListener usa áudio, sessão persistente, chat e voz da API Lumi")


def test_sentence_segmentation():
    cases = [
        ("O Sr. Silva falou com a Dra. Ana. Tudo bem?", ["O Sr. Silva falou com a Dra. Ana.", "Tudo bem?"]),
        ("O preço é 3.14 euros. Certo!", ["O preço é 3.14 euros.", "Certo!"]),
        ("Tenho 3.14. Segue.", ["Tenho 3.14.", "Segue."]),
        ("O J. P. Silva chegou. Olá.", ["O J. P. Silva chegou.", "Olá."]),
        ("Usa p.ex. arroz, etc. para o jantar. Sim.", ["Usa p.ex. arroz, etc. para o jantar.", "Sim."]),
        ("Acede a https://exemplo.pt e escreve a a@b.pt. Depois sai.", ["Acede a https://exemplo.pt e escreve a a@b.pt.", "Depois sai."]),
        ("Ela disse: “Olá!” Depois saiu.", ["Ela disse: “Olá!”", "Depois saiu."]),
        ("Espera... ainda penso. Sim?! Tudo bem.", ["Espera... ainda penso.", "Sim?!", "Tudo bem."]),
        ("1. Primeiro ponto. 2. Segundo ponto.", ["1. Primeiro ponto.", "2. Segundo ponto."]),
        ("O n.º 3 está pronto. Sim.", ["O n.º 3 está pronto.", "Sim."]),
    ]
    for text, expected in cases:
        segmenter = streaming_speech.SentenceSegmenter()
        assert segmenter.feed(text, final=True) == expected, text

        for cut in range(len(text) + 1):
            segmenter = streaming_speech.SentenceSegmenter()
            actual = segmenter.feed(text[:cut])
            actual.extend(segmenter.feed(text[cut:], final=True))
            assert actual == expected, (text, cut, actual)


def test_incremental_pipeline():
    started = time.perf_counter()
    events = []
    playing = 0
    max_playing = 0
    generation_done = None

    class Client:
        def chat_stream(self, _message):
            nonlocal generation_done
            for chunk in ["Primeira frase. ", "Segunda frase. ", "Último fragmento"]:
                time.sleep(0.05)
                yield chunk
            generation_done = time.perf_counter() - started

        def speak(self, sentence):
            events.append(("synth", sentence, time.perf_counter() - started))
            time.sleep(0.03)
            return sentence.encode()

    def play(wave, stop_event):
        nonlocal playing, max_playing
        sentence = wave.decode()
        playing += 1
        max_playing = max(max_playing, playing)
        events.append(("play", sentence, time.perf_counter() - started))
        deadline = time.perf_counter() + 0.10
        while time.perf_counter() < deadline and not stop_event.wait(0.005):
            pass
        events.append(("ended", sentence, time.perf_counter() - started))
        playing -= 1

    result = streaming_speech.stream_and_speak(Client(), "pergunta", play)
    plays = [event for event in events if event[0] == "play"]
    syntheses = [event for event in events if event[0] == "synth"]
    assert result.reply == "Primeira frase. Segunda frase. Último fragmento"
    assert [event[1] for event in plays] == [
        "Primeira frase.", "Segunda frase.", "Último fragmento"
    ]
    assert max_playing == 1
    assert plays[0][2] < generation_done
    first_end = next(event[2] for event in events if event[:2] == ("ended", "Primeira frase."))
    second_synthesis = next(event[2] for event in syntheses if event[1] == "Segunda frase.")
    second_play = next(event[2] for event in plays if event[1] == "Segunda frase.")
    third_synthesis = next(event[2] for event in syntheses if event[1] == "Último fragmento")
    assert second_synthesis < first_end
    assert third_synthesis >= second_play
    assert sum(event[1] == "Último fragmento" for event in plays) == 1
    print({
        "scenario": "live-after; chunks=50ms; TTS=30ms; playback=100ms",
        "firstPlayMs": round(plays[0][2] * 1000),
        "generationCompleteMs": round(generation_done * 1000),
    })


def test_generation_failure_cancels_playback():
    plays = []
    stopped = threading.Event()

    class Client:
        def chat_stream(self, _message):
            yield "Primeira frase. Segunda frase. "
            time.sleep(0.05)
            raise lumi_client.LumiApiError("stream interrompido")

        def speak(self, sentence):
            return sentence.encode()

    def play(wave, stop_event):
        plays.append(wave.decode())
        assert stop_event.wait(0.5)
        stopped.set()

    try:
        streaming_speech.stream_and_speak(Client(), "pergunta", play)
        raise AssertionError("A falha do stream devia ser propagada")
    except lumi_client.LumiApiError:
        pass
    time.sleep(0.05)
    assert stopped.is_set()
    assert plays == ["Primeira frase."]


def test_listener_reports_streamed_reply():
    reports = []
    played = []

    class Client:
        def transcribe(self, _filename):
            return "pergunta aprovada automaticamente"

        def chat_stream(self, _message):
            yield "Primeira. "
            yield "Último fragmento"

        def speak(self, sentence):
            return sentence.encode()

    original_report = voice_listener.report
    original_log = voice_listener.log
    voice_listener.report = lambda state, **details: reports.append((state, details))
    voice_listener.log = lambda _message: None
    try:
        voice_listener.process_recording(
            Client(),
            Path("não-é-lido.wav"),
            player=lambda wave, _stop: played.append(wave.decode()),
        )
    finally:
        voice_listener.report = original_report
        voice_listener.log = original_log

    assert played == ["Primeira.", "Último fragmento"]
    speaking = [details for state, details in reports if state == "speaking"]
    assert speaking[0] == {"transcription": "pergunta aprovada automaticamente"}
    assert speaking[-1]["reply"] == "Primeira. Último fragmento"


if __name__ == "__main__":
    main()
