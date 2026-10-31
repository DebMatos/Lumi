# Contrato de interação — voz V0.1

Fonte: requisitos da tarefa V0.1 e contratos dos endpoints em `Lumi.Api/Program.cs`.

| Capacidade | Responsável | Contrato | Verificação |
| --- | --- | --- | --- |
| Formulário | `chat.js`: submit/render | Um envio por vez; captura bloqueia envio, reprodução não; preservar rascunho | `tests/chat.test.cjs` |
| Feedback | `chat.js`: render/showError | Estado em região status e erro em alert; cancelamento voluntário silencioso | `tests/chat.test.cjs` |
| Scrollbar | CSS global em `chat.css` | Visível, cores do documento, preferência forced-colors respeitada | Inspeção manual |
| Captura | `chat.js`: capture/transcribe | Cancelar liberta tracks e aborta pedido; transcrição acrescenta ao texto atual | `tests/chat.test.cjs` |
| Reprodução | `chat.js`: speech/stopSpeech | Uma reprodução; desligar/parar aborta síntese e liberta URL; nova resposta substitui anterior | `tests/chat.test.cjs` |
| Sessão | `chat.js`: newChat | Abortar pedidos antigos e ignorar conclusões atrasadas; preservar rascunho | `tests/chat.test.cjs` |

As operações de chat, captura e reprodução têm identidades próprias. Não reutilizar `finally` de um pedido antigo para alterar um pedido novo. O frontend mantém os tetos V0.1: 60s/10MB na captura e 4000 caracteres em TTS; o servidor pode impor limites menores.

Voz desligada por omissão. Parar voz mantém a preferência ligada. Ao gravar, parar voz para evitar capturar a própria Lumi. Permissão de microfone pendente é cancelável: se chegar depois, libertar imediatamente as tracks. Nova conversa cria uma sessão nova; o histórico antigo não é apagado no servidor.
