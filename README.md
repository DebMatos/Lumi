# Lumi — voz V0.1

ASP.NET Core 10, HTML/JavaScript, Ollama, whisper.cpp, ffmpeg e Piper instalados no anfitrião. Sem novos motores ou frameworks.

## Executar e configurar

```bash
dotnet run --project Lumi.Api --launch-profile http
```

Abrir `http://localhost:5194/chat.html`. Permitir microfone no navegador. Ligar «Voz desligada» para receber respostas faladas. «Terminar gravação» envia para transcrição; «Cancelar gravação/transcrição» descarta/aborta e conserva o rascunho. A transcrição acrescenta ao texto atual; não envia automaticamente. «Parar voz» interrompe reprodução ou síntese pendente sem desligar a preferência.

`Lumi.Api/appsettings.json` configura `Whisper` e `Speech`. Os caminhos `~/Projetos/...` são exemplos de desenvolvimento, expandidos a partir do diretório pessoal. Ajustar executáveis e modelos à instalação; ffmpeg deve estar no PATH ou ter caminho absoluto. Piper requer também o ficheiro de configuração do modelo `.onnx.json`. É possível usar `appsettings.Development.json` (ignorado pelo Git) ou variáveis como `Whisper__Executable`, `Whisper__ModelPath`, `Speech__PiperExecutable`, `Speech__ModelPath`.

Limites do servidor: upload até 10 MiB, duração descodificada até 60s, texto TTS até 4000 unidades UTF-16. Os valores configuráveis podem reduzir estes limites. Limite HTTP/multipart de 11 MiB, incluindo overhead. Formatos aceites: WebM, Ogg, WAV, MP4 e MP3; o navegador escolhe WebM/Opus, Ogg/Opus ou MP4 consoante suporte. O conteúdo é realmente descodificado por ffmpeg, limitado a contentores permitidos, uma faixa mono PCM 16kHz e 61s no máximo para rejeitar excesso. Não se confia na extensão ou na duração declarada pelo cliente.

Timeouts por operação: Whisper (inclui conversão) 120s; Piper 60s. O frontend aborta após 135s e 75s, respetivamente; chat após 5 minutos. Se aumentar os timeouts do servidor, ajustar também o frontend. Há um único pedido de áudio ativo, partilhado entre síntese e transcrição; pedidos concorrentes recebem 429 e podem ser repetidos. Cancelamentos matam a árvore dos processos e aguardam saída antes de remover temporários. Temporários ficam em pastas privadas no Linux; uma terminação abrupta do sistema/processo anfitrião pode deixar resíduos no diretório temporário.

`ISpeechService` conserva a assinatura independente do motor. `ChatService` e o protocolo de streaming não foram alterados.

## Contratos HTTP

- `POST /chat`: `{ "message": "..." }` → `{ "reply": "..." }`.
- `POST /chat/stream`: `{ "message": "...", "sessionId": "..." }` → texto UTF-8 em streaming.
- `POST /audio/transcribe`: multipart campo `audio` → `{ "text": "..." }`.
- `POST /audio/speak`: `{ "text": "..." }` → `audio/wav`.

Erros de áudio devolvem mensagens genéricas: 400 validação/descodificação, 413 pedido demasiado grande, 429 serviço ocupado, 503 falha de motor, 504 timeout. Cancelamento pelo cliente termina o trabalho sem resposta útil (499 se ainda possível). Diagnósticos não incluem stdout/stderr dos motores no HTTP. O limite de saída TTS é 32 MiB; saídas de diagnóstico são drenadas com retenção limitada.

## Segurança local

A V0.1 não tem autenticação. Manter bind em loopback, conforme `launchSettings.json`, e não expor portas via encaminhamento/proxy público. O middleware rejeita peers fora de loopback, origens de navegador diferentes e `Sec-Fetch-Site: cross-site`. `AllowedHosts` limita nomes locais para reduzir DNS rebinding. Não ativar CORS permissivo ou forwarded headers de proxies não confiáveis. Não usar a API com ficheiros de origem não confiável: ffmpeg e motores são processos nativos com acesso aos ficheiros do utilizador; mantê-los atualizados e executar com permissões mínimas.

Antes de expor a API na rede: autenticação e autorização (incluindo isolamento/autorização das sessões de chat), HTTPS, política explícita de origens/CSRF, limites por utilizador e de concorrência também no chat, retenção/remoção de histórico, política de logs/dados pessoais, isolamento dos processos e diretórios, revisão de uploads e configuração segura de proxy/firewall. A atual proteção local deve ser substituída deliberadamente; mudar apenas AllowedHosts não habilita acesso remoto. Swagger/erros de desenvolvimento não devem estar públicos. Cancelar uma conversa não apaga o histórico antigo em memória do ChatService.

## Verificação automatizada

```bash
dotnet build Lumi.Api/Lumi.Api.csproj
dotnet run --project tests/Lumi.Voice.Tests
node tests/chat.test.cjs
python3 tests/http_smoke.py
python3 tests/http_smoke.py --browser
```

O executável de testes .NET não requer um framework adicional. Testa limites, cancelamento antes/depois de spawn, descendentes, pipes, timeout/limpeza do Piper com processo simulado e duração real com ffmpeg + Whisper simulado. Casos de processo/fixtures requerem Linux e ffmpeg. Os testes JS usam DOM/MediaRecorder/Audio/fetch simulados; não substituem o navegador nem os modelos reais.

Os testes HTTP iniciam a API e um Ollama simulado em portas loopback temporárias, sem usar os modelos instalados. A opção `--browser` requer `google-chrome` e Node 24: verifica DOM e reprodução de WAV no Chrome headless, com respostas de rede simuladas e autoplay permitido. O perfil temporário é removido; capturas de 320px e 1280px ficam em `/tmp/lumi-320.png` e `/tmp/lumi-1280.png`. O Chrome de teste usa `--no-sandbox` apenas para esta página local controlada; não usar este comando para navegar em sites externos.

## Testes manuais no navegador

1. Em localhost, gravar, terminar, confirmar texto acrescentado ao rascunho e enviar. Confirmar resposta em streaming e voz apenas no fim.
2. Cancelar durante gravação, permissão pendente e transcrição; confirmar indicador do microfone desligado, rascunho preservado, ausência de erro e de texto atrasado.
3. Durante fala, escrever e enviar outra mensagem; confirmar formulário utilizável e interrupção da voz anterior quando chega nova resposta falada. Usar «Parar voz», desligar voz e repetir durante síntese; não deve tocar áudio atrasado.
4. Iniciar nova conversa durante stream, gravação, transcrição, síntese e fala. Confirmar rascunho preservado, sessão nova e ausência de resultados antigos. Repetir rapidamente cancelar/iniciar.
5. Recusar microfone; desligar/parar motores; testar ficheiro inválido, vazio, >10 MiB, >60s e texto >4000. Confirmar feedback, recuperação e ausência de detalhes internos em HTTP.
6. Gravar até ao limite, confirmar paragem automática e tracks libertadas. Testar Chrome/Firefox e Safari conforme disponíveis (incluindo bloqueio de autoplay).
7. Testar teclado, foco visível, leitor de ecrã para estados/erros e viewport de 320px/zoom de 200%. Verificar estados combinados, como «A gerar resposta · A falar».
8. Confirmar que chamadas com Origin externo/Host inválido são rejeitadas e que continua a funcionar sem Origin em clientes locais como curl.

Não há validação perceptiva automatizada da qualidade/pronúncia dos modelos. O cancelamento de chat usa o token existente e não reescreve o histórico do ChatService.
