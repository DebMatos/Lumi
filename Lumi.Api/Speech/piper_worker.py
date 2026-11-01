"""One PiperVoice per process. stdout is reserved for framed protocol v1.

Input: JSON line {"id": str, "text": str}.
Output: JSON line {"id": str, "ok": bool, "length": int}, then exactly
length WAV bytes on success. A ready/version header precedes all replies.
EOF on stdin shuts the worker down. Diagnostics go exclusively to stderr.
"""
import argparse
import contextlib
import io
import json
import sys
import traceback
import wave

MAX_AUDIO_BYTES = 32 * 1024 * 1024
MAX_MESSAGE_BYTES = 32 * 1024


class LimitedBuffer(io.BytesIO):
    def write(self, data):
        if self.tell() + len(data) > MAX_AUDIO_BYTES:
            raise ValueError("Audio output exceeds limit")
        return super().write(data)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--max-text-length", type=int, default=4000)
    args = parser.parse_args()
    output = sys.stdout.buffer

    def reply(header, payload=b""):
        output.write(json.dumps(header, separators=(",", ":")).encode("utf-8") + b"\n")
        if payload:
            output.write(payload)
        output.flush()

    # Library prints must never corrupt the protocol stream.
    with contextlib.redirect_stdout(sys.stderr):
        from piper import PiperVoice
        voice = PiperVoice.load(args.model)
    reply({"ready": True, "protocol": 1})

    while True:
        line = sys.stdin.buffer.readline(MAX_MESSAGE_BYTES + 1)
        if not line:
            return
        if len(line) > MAX_MESSAGE_BYTES or not line.endswith(b"\n"):
            raise ValueError("Invalid protocol message length")
        request_id = None
        try:
            request = json.loads(line)
            request_id = request["id"]
            text = request["text"]
            if not isinstance(request_id, str) or len(request_id) > 64:
                raise ValueError("Invalid request id")
            if not isinstance(text, str) or not text.strip() or len(text) > args.max_text_length:
                raise ValueError("Invalid text")
            with LimitedBuffer() as buffer:
                with contextlib.redirect_stdout(sys.stderr), wave.open(buffer, "wb") as wav:
                    voice.synthesize_wav(text.replace("\r", " ").replace("\n", " "), wav)
                payload = buffer.getvalue()
            reply({"id": request_id, "ok": True, "length": len(payload)}, payload)
        except Exception:
            traceback.print_exc(file=sys.stderr)
            reply({"id": request_id, "ok": False, "length": 0, "error": "synthesis_failed"})


if __name__ == "__main__":
    main()
