"""Cliente local para os contratos de voz e conversa da Lumi."""

from __future__ import annotations

import codecs
import http.client
import json
import os
from pathlib import Path
import tempfile
from collections.abc import Iterator
from urllib.parse import urlsplit
import uuid


DEFAULT_API_URL = "http://127.0.0.1:5194"
DEFAULT_SESSION_FILE = (
    Path(os.environ.get("XDG_STATE_HOME", Path.home() / ".local/state"))
    / "lumi"
    / "voice-listener-session"
)


class LumiApiError(RuntimeError):
    """Erro devolvido pela API local da Lumi."""


class LumiClient:
    def __init__(
        self,
        base_url: str = DEFAULT_API_URL,
        session_file: Path = DEFAULT_SESSION_FILE,
        timeout: float = 135,
        session_id: str | None = None,
    ) -> None:
        parsed = urlsplit(base_url.rstrip("/"))
        if parsed.scheme != "http" or parsed.hostname not in {
            "localhost",
            "127.0.0.1",
            "::1",
        }:
            raise ValueError("A API da Lumi tem de usar HTTP num endereço loopback.")
        if parsed.path or parsed.query or parsed.fragment:
            raise ValueError("O URL da API não pode conter caminho, query ou fragmento.")

        self._host = parsed.hostname
        self._port = parsed.port or 80
        self._timeout = timeout
        self.session_file = Path(session_file)
        self.session_id = (
            str(uuid.UUID(session_id))
            if session_id is not None
            else self._load_or_create_session_id()
        )

    def _load_or_create_session_id(self) -> str:
        try:
            value = self.session_file.read_text(encoding="utf-8").strip()
            return str(uuid.UUID(value))
        except (FileNotFoundError, OSError, ValueError):
            value = str(uuid.uuid4())

        self.session_file.parent.mkdir(parents=True, exist_ok=True)
        fd, temporary = tempfile.mkstemp(
            prefix=f".{self.session_file.name}-",
            dir=self.session_file.parent,
            text=True,
        )
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as stream:
                stream.write(value + "\n")
            os.chmod(temporary, 0o600)
            os.replace(temporary, self.session_file)
        finally:
            Path(temporary).unlink(missing_ok=True)
        return value

    def transcribe(self, filename: Path) -> str:
        boundary = f"lumi-{uuid.uuid4().hex}"
        audio = Path(filename).read_bytes()
        body = b"".join(
            [
                f"--{boundary}\r\n".encode(),
                (
                    'Content-Disposition: form-data; name="audio"; '
                    f'filename="{Path(filename).name}"\r\n'
                ).encode(),
                b"Content-Type: audio/wav\r\n\r\n",
                audio,
                f"\r\n--{boundary}--\r\n".encode(),
            ]
        )
        payload = self._request(
            "POST",
            "/audio/transcribe",
            body,
            {"Content-Type": f"multipart/form-data; boundary={boundary}"},
        )
        try:
            return str(json.loads(payload)["text"]).strip()
        except (json.JSONDecodeError, KeyError, TypeError) as error:
            raise LumiApiError("Resposta de transcrição inválida.") from error

    def chat(self, message: str) -> str:
        return "".join(self.chat_stream(message)).strip()

    def chat_stream(self, message: str) -> Iterator[str]:
        body = json.dumps(
            {"sessionId": self.session_id, "message": message}
        ).encode()
        connection = http.client.HTTPConnection(
            self._host, self._port, timeout=self._timeout
        )
        try:
            connection.request(
                "POST",
                "/chat/stream",
                body=body,
                headers={"Content-Type": "application/json"},
            )
            response = connection.getresponse()
            if response.status < 200 or response.status >= 300:
                payload = response.read()
                detail = _error_detail(payload)
                raise LumiApiError(
                    f"A API respondeu com HTTP {response.status}"
                    + (f": {detail}" if detail else ".")
                )

            decoder = codecs.getincrementaldecoder("utf-8")()
            read_chunk = getattr(response, "read1", response.read)
            while chunk := read_chunk(4096):
                text = decoder.decode(chunk)
                if text:
                    yield text
            tail = decoder.decode(b"", final=True)
            if tail:
                yield tail
        except UnicodeDecodeError as error:
            raise LumiApiError("Resposta de conversa inválida.") from error
        except OSError as error:
            raise LumiApiError(
                f"Não foi possível contactar a API da Lumi: {error}"
            ) from error
        finally:
            connection.close()

    def speak(self, text: str) -> bytes:
        return self._request(
            "POST",
            "/audio/speak",
            json.dumps({"text": text}).encode(),
            {"Content-Type": "application/json"},
        )

    def _request(
        self,
        method: str,
        path: str,
        body: bytes,
        headers: dict[str, str],
    ) -> bytes:
        connection = http.client.HTTPConnection(
            self._host, self._port, timeout=self._timeout
        )
        try:
            connection.request(method, path, body=body, headers=headers)
            response = connection.getresponse()
            payload = response.read()
            if response.status < 200 or response.status >= 300:
                detail = _error_detail(payload)
                raise LumiApiError(
                    f"A API respondeu com HTTP {response.status}"
                    + (f": {detail}" if detail else ".")
                )
            return payload
        except OSError as error:
            raise LumiApiError(f"Não foi possível contactar a API da Lumi: {error}") from error
        finally:
            connection.close()


def _error_detail(payload: bytes) -> str:
    try:
        value = json.loads(payload)
        if isinstance(value, dict) and isinstance(value.get("error"), str):
            return value["error"]
    except (UnicodeDecodeError, json.JSONDecodeError):
        pass
    return ""
