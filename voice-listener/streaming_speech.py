"""Segmentação e pipeline incremental de conversa falada."""

from __future__ import annotations

from dataclasses import dataclass
import queue
import re
import threading
from collections.abc import Callable


MAX_PENDING_SENTENCES = 2
MAX_READY_AUDIO = 1
MAX_SPEECH_TEXT_UNITS = 4000

_END = object()
_ENDINGS = re.compile(r"[.!?…]+[\"'»”’)\]`*]*")
_WORD_BEFORE_DOT = re.compile(r"([^\W_]+(?:\.[^\W_]+)*)\.$", re.UNICODE)
_INITIALS = re.compile(r"(?:[^\W_]\.)+[^\W_]$", re.UNICODE)
_ABBREVIATIONS = {
    "sr", "sra", "srta", "dr", "dra", "prof", "profa", "eng", "engº", "engª",
    "exmo", "exma", "av", "pág", "págs", "art", "arts", "n", "nº", "n.º", "n.ª",
    "no", "etc", "p.ex", "ex", "e.g", "i.e", "vs", "aprox", "tel", "ref", "vol",
    "fig", "séc", "jan", "fev", "mar", "abr", "jun", "jul", "ago", "set", "out",
    "nov", "dez",
}


def _utf16_units(value: str) -> int:
    return len(value.encode("utf-16-le")) // 2


def _prefix_with_units(value: str, limit: int) -> int:
    units = 0
    for index, character in enumerate(value):
        size = 2 if ord(character) > 0xFFFF else 1
        if units + size > limit:
            return index
        units += size
    return len(value)


def _split_long(value: str) -> tuple[list[str], str]:
    complete = []
    while _utf16_units(value) > MAX_SPEECH_TEXT_UNITS:
        maximum = _prefix_with_units(value, MAX_SPEECH_TEXT_UNITS)
        whitespace = [match.start() for match in re.finditer(r"\s+", value[: maximum + 1])]
        cut = whitespace[-1] if whitespace and whitespace[-1] >= maximum // 2 else maximum
        complete.append(value[:cut].strip())
        value = value[cut:].lstrip()
    return complete, value


class SentenceSegmenter:
    """Port fiel da segmentação incremental usada em ``chat.js``."""

    def __init__(self) -> None:
        self._buffer = ""

    def feed(self, chunk: str, final: bool = False) -> list[str]:
        text = self._buffer + chunk
        sentences = []
        start = 0

        for match in _ENDINGS.finditer(text):
            end = match.end()
            if end == len(text) and not final:
                continue
            if end < len(text) and not text[end].isspace():
                continue

            punctuation = re.match(r"[.!?…]+", match.group()).group()
            if punctuation == ".":
                before = text[start : match.start() + 1]
                word_match = _WORD_BEFORE_DOT.search(before)
                word = word_match.group(1) if word_match else ""
                if (
                    word.lower() in _ABBREVIATIONS
                    or (len(word) == 1 and word.isupper())
                    or _INITIALS.fullmatch(word)
                ):
                    continue
                if word.isdigit():
                    following = text[end:].lstrip()
                    if (
                        re.fullmatch(r"\s*\d+\.", before)
                        or (not final and not following)
                        or (following and (following[0].islower() or following[0].isdigit()))
                    ):
                        continue

            if punctuation.startswith(("..", "…")):
                following = text[end:].lstrip()
                if (not final and not following) or (
                    following and following[0].islower()
                ):
                    continue

            sentence = text[start:end].strip()
            if sentence:
                sentences.append(sentence)
            start = end

        rest = text[start:].lstrip()
        if final and rest.strip():
            sentences.append(rest.strip())
            rest = ""

        bounded = []
        for sentence in sentences:
            complete, tail = _split_long(sentence)
            bounded.extend(complete)
            if tail:
                bounded.append(tail)

        complete, rest = _split_long(rest)
        bounded.extend(complete)
        self._buffer = rest
        return bounded


@dataclass(frozen=True)
class StreamingSpeechResult:
    reply: str
    speech_error: Exception | None = None


def stream_and_speak(
    client,
    message: str,
    play_audio: Callable[[bytes, threading.Event], None],
    on_speaking: Callable[[], None] | None = None,
) -> StreamingSpeechResult:
    """Sobrepõe geração, síntese e reprodução com filas pequenas e ordenadas."""

    sentences: queue.Queue[object] = queue.Queue(MAX_PENDING_SENTENCES)
    audio: queue.Queue[object] = queue.Queue(MAX_READY_AUDIO)
    generation_errors: queue.Queue[Exception] = queue.Queue()
    speech_errors: queue.Queue[Exception] = queue.Queue()
    stop = threading.Event()
    stop_playback = threading.Event()
    speech_enabled = threading.Event()
    speech_enabled.set()
    reply_chunks: list[str] = []

    def put_while_active(target: queue.Queue, item, require_speech: bool) -> bool:
        while not stop.is_set():
            if require_speech and not speech_enabled.is_set():
                return False
            try:
                target.put(item, timeout=0.05)
                return True
            except queue.Full:
                continue
        return False

    def generate() -> None:
        segmenter = SentenceSegmenter()
        try:
            for chunk in client.chat_stream(message):
                if stop.is_set():
                    return
                reply_chunks.append(chunk)
                for sentence in segmenter.feed(chunk):
                    if speech_enabled.is_set():
                        put_while_active(sentences, sentence, require_speech=True)
            for sentence in segmenter.feed("", final=True):
                if speech_enabled.is_set():
                    put_while_active(sentences, sentence, require_speech=True)
            if speech_enabled.is_set():
                put_while_active(sentences, _END, require_speech=True)
        except Exception as error:
            generation_errors.put(error)
            stop.set()
            stop_playback.set()

    def synthesize() -> None:
        try:
            while not stop.is_set() and speech_enabled.is_set():
                # Não iniciar outra síntese enquanto já existe um WAV pronto.
                # Assim há, no máximo, o áudio atual e um áudio preparado.
                while audio.full() and not stop.is_set() and speech_enabled.is_set():
                    stop.wait(0.01)
                if stop.is_set() or not speech_enabled.is_set():
                    return
                try:
                    sentence = sentences.get(timeout=0.05)
                except queue.Empty:
                    continue
                if sentence is _END:
                    put_while_active(audio, _END, require_speech=True)
                    return
                try:
                    wave = client.speak(sentence)
                except Exception as error:
                    speech_errors.put(error)
                    speech_enabled.clear()
                    stop_playback.set()
                    return
                if not put_while_active(audio, wave, require_speech=True):
                    return
        finally:
            if not speech_enabled.is_set():
                stop_playback.set()

    generator = threading.Thread(target=generate, daemon=True)
    synthesizer = threading.Thread(target=synthesize, daemon=True)
    generator.start()
    synthesizer.start()
    started_speaking = False

    try:
        while not stop.is_set() and speech_enabled.is_set():
            try:
                item = audio.get(timeout=0.05)
            except queue.Empty:
                if not generator.is_alive() and not synthesizer.is_alive():
                    break
                continue
            if item is _END:
                break
            if not started_speaking:
                started_speaking = True
                if on_speaking is not None:
                    on_speaking()
            try:
                play_audio(item, stop_playback)
            except Exception as error:
                speech_errors.put(error)
                speech_enabled.clear()
                stop_playback.set()
                break

        generator.join()
        synthesizer.join()
    finally:
        stop.set()
        speech_enabled.clear()
        stop_playback.set()
        generator.join(timeout=0.2)
        synthesizer.join(timeout=0.2)

    if not generation_errors.empty():
        raise generation_errors.get()
    speech_error = speech_errors.get() if not speech_errors.empty() else None
    return StreamingSpeechResult("".join(reply_chunks).strip(), speech_error)
