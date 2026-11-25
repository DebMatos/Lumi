# Contrato de interação — voz V0.1

Fonte: requisitos da tarefa V0.1 e contratos dos endpoints em `Lumi.Api/Program.cs`.

| Capacidade | Responsável | Contrato | Verificação |
| --- | --- | --- | --- |
| Formulário | `chat.js`: submit/render | Um envio por vez; captura bloqueia envio, reprodução não; preservar rascunho | `tests/chat.test.cjs` |
| Feedback | `chat.js`: render/showError | Estado em região status e erro em alert; cancelamento voluntário silencioso | `tests/chat.test.cjs` |
| Scrollbar | CSS global em `chat.css` | Visível, cores do documento, preferência forced-colors respeitada | Inspeção manual |
| Captura | `chat.js`: capture/transcribe | Cancelar liberta tracks e aborta pedido; transcrição acrescenta ao texto atual | `tests/chat.test.cjs` |
| Reprodução | `chat.js`: feedSpeech/prepareSpeech/stopSpeech | Fila por resposta/sessão: uma reprodução, uma síntese e uma frase preparada; desligar/parar aborta e descarta também as frases futuras dessa resposta; nova resposta substitui anterior | `tests/chat.test.cjs` |
| Sessão | `chat.js`: newChat | Abortar pedidos antigos e ignorar conclusões atrasadas; preservar rascunho | `tests/chat.test.cjs` |
| Modo ao vivo | `VoiceListenerManager` + `listener.py` | Início explícito; VAD termina, transcreve, envia, fala e regressa à escuta; uma instância por API | `tests/Lumi.Voice.Tests`, `tests/chat.test.cjs` |
| Modo com aprovação | `chat.js`: capture/transcribe | Gravação manual; transcrição editável; nenhum chat antes de confirmação | `tests/chat.test.cjs` |
| Estado do listener | `/voice-listener/status` + polling de 800ms enquanto ativo | Snapshot real versionado; parado/escuta/gravação/transcrição/resposta/fala/erro | `tests/chat.test.cjs` |
| Painéis | `chat.html` + `chat.js`: togglePanel | Transcrição e texto recolhidos por omissão e controlados independentemente; abrir não interrompe voz; apenas a região inferior desloca | `tests/chat.test.cjs`, `tests/browser_smoke.cjs` |

As operações de chat, captura e reprodução têm identidades próprias. Não reutilizar `finally` de um pedido antigo para alterar um pedido novo. O frontend mantém os tetos V0.1: 60s/10MB na captura e 4000 caracteres em TTS; o servidor pode impor limites menores.

A saída falada do compositor de texto mantém-se desligada por omissão; o modo ao vivo fala sempre através do Piper. Parar voz mantém a preferência do compositor. Ao gravar no navegador, parar voz para evitar capturar a própria Lumi. Permissão de microfone pendente é cancelável: se chegar depois, libertar imediatamente as tracks. Nova conversa para o listener atual, cria uma sessão nova e não apaga o histórico antigo no servidor.

Alternar modos nunca altera o UUID de sessão. O Python recebe esse UUID do backend e usa os mesmos endpoints `/audio/transcribe`, `/chat/stream` e `/audio/speak`; não possui base de dados nem histórico próprios. Fechar a página pede paragem do listener, e o encerramento da API fecha `stdin`, aguarda o processo e termina a árvore apenas após timeout.

Voz incremental: os chunks visuais alimentam uma fila independente por frases. O fim do stream esvazia o fragmento restante. A fila mantém ordem e não bloqueia o formulário nem a leitura do stream. Verificação adicional: `tests/incremental-voice.test.cjs` (segmentação, limites, concorrência, ordem, cancelamento e erros) e medições descritas no README.
