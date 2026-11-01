# Lumi — voz V0.1

ASP.NET Core 10, HTML/JavaScript, Ollama, whisper.cpp, ffmpeg e Piper instalados no anfitrião. Sem novos motores ou frameworks.

## Executar e configurar

```bash
dotnet run --project Lumi.Api --launch-profile http
```

Abrir `http://localhost:5194/chat.html`. Permitir microfone no navegador. Ligar «Voz desligada» para receber respostas faladas. «Terminar gravação» envia para transcrição; «Cancelar gravação/transcrição» descarta/aborta e conserva o rascunho. A transcrição acrescenta ao texto atual; não envia automaticamente. «Parar voz» interrompe reprodução ou síntese pendente sem desligar a preferência.

`Lumi.Api/appsettings.json` configura `Whisper` e `Speech`. Os caminhos `~/Projetos/...` são exemplos de desenvolvimento, expandidos a partir do diretório pessoal. Ajustar executáveis e modelos à instalação; ffmpeg deve estar no PATH ou ter caminho absoluto. Piper requer também o ficheiro de configuração do modelo `.onnx.json`. `Speech:PythonExecutable` deve apontar para o Python do ambiente em que Piper está instalado. `Speech:WorkerScript` é relativo ao diretório dos binários (`Speech/piper_worker.py` por omissão), copiado também na publicação; pode ser um caminho absoluto. A antiga opção `Speech:PiperExecutable` foi substituída por `Speech:PythonExecutable`. É possível usar `appsettings.Development.json` (ignorado pelo Git) ou variáveis como `Whisper__Executable`, `Whisper__ModelPath`, `Speech__PythonExecutable`, `Speech__ModelPath`.

Limites do servidor: upload até 10 MiB, duração descodificada até 60s, texto TTS até 4000 unidades UTF-16 por pedido. Os valores configuráveis podem reduzir estes limites. Limite HTTP/multipart de 11 MiB, incluindo overhead. Formatos aceites: WebM, Ogg, WAV, MP4 e MP3; o navegador escolhe WebM/Opus, Ogg/Opus ou MP4 consoante suporte. O conteúdo é realmente descodificado por ffmpeg, limitado a contentores permitidos, uma faixa mono PCM 16kHz e 61s no máximo para rejeitar excesso. Não se confia na extensão ou na duração declarada pelo cliente.

Timeouts por operação: Whisper (inclui conversão) 120s; Piper 60s. O frontend aborta após 135s e 75s, respetivamente; chat após 5 minutos. Se aumentar os timeouts do servidor, ajustar também o frontend. O timeout Piper inclui a espera na fila, inicialização e síntese. A transcrição mantém o seu limite de um pedido ativo (429 em concorrência). O TTS tem um único consumidor e `Speech:QueueCapacity` pedidos em espera (3 por omissão, configurável entre 1 e 16); ao esgotar a fila devolve 503, sem acumulação ilimitada. Cancelar um pedido em espera conserva o worker ativo. Cancelar uma inferência ativa ou exceder o timeout termina e recolhe o worker antes de processar o pedido seguinte; esse pedido volta a carregar o modelo. Pedidos cancelados na fila são descartados pelo consumidor. Só a transcrição usa ficheiros temporários privados; o TTS troca WAVs em memória. Uma terminação abrupta do anfitrião pode deixar temporários da transcrição.

`ISpeechService` conserva a assinatura independente do motor. `ChatService` e o protocolo de streaming não foram alterados.

## Contratos HTTP

- `POST /chat`: `{ "message": "..." }` → `{ "reply": "..." }`.
- `POST /chat/stream`: `{ "message": "...", "sessionId": "..." }` → texto UTF-8 em streaming.
- `POST /audio/transcribe`: multipart campo `audio` → `{ "text": "..." }`.
- `POST /audio/speak`: `{ "text": "..." }` → `audio/wav`.

Erros de áudio devolvem mensagens genéricas: 400 validação/descodificação, 413 pedido demasiado grande, 429 transcrição ocupada, 503 fila TTS cheia/falha de motor, 504 timeout. Cancelamento pelo cliente termina o trabalho sem resposta útil (499 se ainda possível). Diagnósticos não incluem stdout/stderr dos motores no HTTP. O limite de saída TTS é 32 MiB; saídas de diagnóstico são drenadas com retenção limitada.

## Segurança local

A V0.1 não tem autenticação. Manter bind em loopback, conforme `launchSettings.json`, e não expor portas via encaminhamento/proxy público. O middleware rejeita peers fora de loopback, origens de navegador diferentes e `Sec-Fetch-Site: cross-site`. `AllowedHosts` limita nomes locais para reduzir DNS rebinding. Não ativar CORS permissivo ou forwarded headers de proxies não confiáveis. Não usar a API com ficheiros de origem não confiável: ffmpeg e motores são processos nativos com acesso aos ficheiros do utilizador; mantê-los atualizados e executar com permissões mínimas.

Antes de expor a API na rede: autenticação e autorização (incluindo isolamento/autorização das sessões de chat), HTTPS, política explícita de origens/CSRF, limites por utilizador e de concorrência também no chat, retenção/remoção de histórico, política de logs/dados pessoais, isolamento dos processos e diretórios, revisão de uploads e configuração segura de proxy/firewall. A atual proteção local deve ser substituída deliberadamente; mudar apenas AllowedHosts não habilita acesso remoto. Swagger/erros de desenvolvimento não devem estar públicos. Cancelar uma conversa não apaga o histórico antigo em memória do ChatService.

## Verificação automatizada

```bash
dotnet build Lumi.Api/Lumi.Api.csproj
dotnet run --project tests/Lumi.Voice.Tests
node tests/chat.test.cjs
node tests/incremental-voice.test.cjs
PYTHONDONTWRITEBYTECODE=1 python3 tests/test_piper_worker.py
node tests/voice_latency.cjs
python3 tests/voice_pauses.py
python3 tests/http_smoke.py
python3 tests/http_smoke.py --browser
```

O executável de testes .NET não requer um framework adicional. Testa limites, cancelamento antes/depois de spawn, descendentes, pipes, fila limitada/FIFO, reutilização, cancelamento e timeout na fila ou durante inferência, falhas de protocolo, reinício e encerramento do worker Piper simulado, e duração real com ffmpeg + Whisper simulado. Os testes Python exercitam o script real com uma classe PiperVoice simulada, incluindo carregamento único e delimitação binária. Casos de processo/fixtures requerem Linux e ffmpeg. Os testes JS usam DOM/MediaRecorder/Audio/fetch simulados; não substituem o navegador nem os modelos reais.

Os testes HTTP iniciam a API e um Ollama simulado em portas loopback temporárias, sem usar os modelos instalados. A opção `--browser` requer `google-chrome` e Node 24: verifica DOM e reprodução de WAV no Chrome headless, com respostas de rede simuladas e autoplay permitido. O perfil temporário é removido; capturas de 320px e 1280px ficam em `/tmp/lumi-320.png` e `/tmp/lumi-1280.png`. O Chrome de teste usa `--no-sandbox` apenas para esta página local controlada; não usar este comando para navegar em sites externos.

O harness JS mantém `localStorage` e `sessionStorage` separados e permite simular a reabertura do navegador partilhando apenas `localStorage`. Os testes HTTP e de latência real usam SQLite num diretório temporário, preparado pelas migrations existentes através do executável de testes .NET; a API arranca nesse diretório e mantém o content root original para configuração e ficheiros estáticos. Os três scripts Python que arrancam a API usam diretórios temporários, removidos após o encerramento, sem escrever na base de desenvolvimento.

O teste HTTP exige 400 para um `sessionId` que não seja GUID. Os testes JS confirmam também que «Nova conversa» guarda o novo UUID em `localStorage`, mantendo-o ativo após recarregar a página ou reabrir o navegador, e preserva a migração anterior de `sessionStorage`.

## Testes manuais no navegador

1. Em localhost, gravar, terminar, confirmar texto acrescentado ao rascunho e enviar. Confirmar resposta em streaming e início da voz assim que chega a primeira frase completa.
2. Cancelar durante gravação, permissão pendente e transcrição; confirmar indicador do microfone desligado, rascunho preservado, ausência de erro e de texto atrasado.
3. Durante fala, escrever e enviar outra mensagem; confirmar formulário utilizável e interrupção da voz anterior quando chega a primeira frase da nova resposta falada. Usar «Parar voz», desligar voz e repetir durante síntese; não deve tocar áudio atrasado.
4. Iniciar nova conversa durante stream, gravação, transcrição, síntese e fala. Confirmar rascunho preservado, sessão nova e ausência de resultados antigos. Repetir rapidamente cancelar/iniciar.
5. Recusar microfone; desligar/parar motores; testar ficheiro inválido, vazio, >10 MiB, >60s e texto enviado diretamente ao TTS >4000. Confirmar feedback, recuperação e ausência de detalhes internos em HTTP.
6. Gravar até ao limite, confirmar paragem automática e tracks libertadas. Testar Chrome/Firefox e Safari conforme disponíveis (incluindo bloqueio de autoplay).
7. Testar teclado, foco visível, leitor de ecrã para estados/erros e viewport de 320px/zoom de 200%. Verificar estados combinados, como «A gerar resposta · A falar».
8. Confirmar que chamadas com Origin externo/Host inválido são rejeitadas e que continua a funcionar sem Origin em clientes locais como curl.

Não há validação perceptiva automatizada da qualidade/pronúncia dos modelos. O cancelamento de chat usa o token existente e não reescreve o histórico do ChatService.

## Voz incremental por frases

O frontend deteta frases completas no stream e usa o mesmo `POST /audio/speak`. O texto continua a aparecer independentemente do TTS. Há apenas um pedido de síntese em curso, um áudio a tocar e, no máximo, uma frase preparada em memória. As frases de texto aguardam em ordem; o último fragmento é processado no fim do stream. Se o servidor devolver 429 enquanto liberta um processo cancelado, há até quatro tentativas, com esperas canceláveis de 150/300/450ms.

«Parar voz», desligar a voz ou iniciar nova conversa descarta a fila, interrompe o áudio e aborta o pedido em curso. Parar/desligar durante o stream silencia também o restante dessa resposta; voltar a ligar a voz não recupera as frases canceladas. Falhas de áudio não interrompem o texto e cancelam a fila dessa resposta. Falhas do stream cancelam a respetiva voz pendente.

A segmentação é conservadora: protege abreviaturas portuguesas comuns, iniciais, decimais, URLs, reticências e aspas, mesmo quando a pontuação atravessa chunks. Abreviaturas desconhecidas e pontuação ambígua continuam a depender de heurísticas; por vezes duas frases são agrupadas para evitar um corte incorreto. Uma resposta curta sem pontuação só é falada no fim. Frases com mais de 4000 caracteres são divididas em espaços quando possível (sem cortar pares UTF-16), respeitando o limite do endpoint; a resposta visual não é truncada. O worker Piper agora conserva o modelo entre frases. As medições de streaming abaixo são históricas, anteriores ao worker persistente; a medição atual de pausas está na secção seguinte.

Para comparar versões, conservar o `chat.js` anterior fora da pasta pública e indicar o caminho:

```bash
node tests/voice_latency.cjs /tmp/lumi-chat-before-incremental.js
python3 tests/voice_real_latency.py /tmp/lumi-chat-before-incremental.js
```

O primeiro teste usa seis chunks de 150ms e síntese simulada de 100ms, medindo do submit à chamada de reprodução. O segundo requer Ollama/Piper configurados e Chrome: grava um stream real do modelo configurado, repete os mesmos chunks e tempos nas duas versões, usa Piper real e mede do submit ao evento `playing`. Faz um aquecimento do Piper antes da comparação; a gravação do LLM pode incluir carregamento inicial. O relatório e os chunks ficam em `/tmp/lumi-voice-real-latency.json`. São observações de um cenário, não uma garantia de latência em todas as respostas.

Medições nesta implementação (31/10/2026):

| Cenário | Antes | Incremental | Redução |
| --- | ---: | ---: | ---: |
| Stream/TTS simulados, última passagem | 1005 ms | 406 ms | 60% |
| Stream real gravado + Piper real + Chrome, primeira passagem | 27946 ms | 23545 ms | 16% |
| Mesmo método, segunda passagem | 31083 ms | 27673 ms | 11% |

Cada linha real compara o mesmo stream nas duas versões, mas as duas linhas usam respostas gravadas diferentes. Na segunda passagem, o primeiro chunk chegou aos 2160 ms, a primeira frase terminou por volta dos 23244 ms (incluindo o contexto seguinte) e o stream terminou aos 26194 ms. A espera por uma frase completa no LLM limita a melhoria; TTS incremental não reduz o tempo que o modelo leva a produzir essa frase. A medição do Chrome usa reprodução headless com autoplay permitido; não mede o instante acústico numa coluna física.

Nos testes manuais, usar também abreviaturas («Dra.», «Sr.»), números («3.14»), reticências e respostas com muitas frases. Durante a primeira frase, usar «Parar voz» e deixar o texto terminar: nenhuma frase seguinte deve tocar. Repetir desligando/ligando voz e mudando de sessão durante síntese. Confirmar que a frase seguinte é preparada durante a reprodução, que se mantém a ordem e que o último fragmento sem ponto é falado apenas uma vez.

## Worker Piper persistente

`PiperSpeechService` é o mesmo singleton exposto por `ISpeechService` e registado como serviço alojado. O consumidor único arranca o worker no primeiro pedido, aguarda `{"ready":true,"protocol":1}` e mantém a instância `PiperVoice` carregada até ao encerramento da Lumi. `ISpeechService` e o contrato `{ "text": "..." }` → `audio/wav` de `/audio/speak` mantêm-se.

Protocolo local: um pedido JSON por linha com `id` e `text`; uma resposta JSON por linha com o mesmo `id`, `ok` e `length`, seguida de exatamente `length` bytes WAV. Mensagens têm limites; stderr é drenado continuamente e stdout é reservado ao protocolo. Não se cria um serviço HTTP adicional, nem novos motores ou dependências Python além do Piper já instalado.

O modelo só volta a carregar após reinício do worker. Cancelamento/timeout durante inferência mata a árvore do processo e impede que uma resposta parcial seja atribuída a outro pedido. Erros de síntese com resposta corretamente delimitada conservam a instância; crash, ID inválido, cabeçalho inválido, comprimento excessivo, WAV inválido ou frame truncado reiniciam o worker no pedido seguinte. Não há repetição automática da frase que falhou. Ao encerrar, cancelar pedidos ativos/em espera, fechar stdin e aguardar a saída, com terminação forçada se necessário.

A fila limitada pertence ao serviço; o antigo limite que devolvia 429 imediatamente foi retirado apenas de `/audio/speak`. O frontend mantém a antecipação de uma frase e os controlos existentes. Um worker ainda frio (primeiro pedido ou após cancelamento ativo/falha) paga o carregamento inicial. Pausas naturais e silêncio dentro dos WAVs continuam dependentes do modelo de voz.

```bash
dotnet build Lumi.Api/Lumi.Api.csproj
python3 tests/voice_pauses.py
```

Esta medição usa as mesmas três frases curtas e três longas do diagnóstico, com Piper real e eventos `playing`/`ended` do Chrome headless. O texto fica disponível imediatamente para isolar síntese e reprodução, sem chamar Ollama. O teste confirma também dois pedidos HTTP sobrepostos com sucesso, um único PID carregado e recolha do worker após encerrar a API. O JSON detalhado fica em `/tmp/lumi-tts-after-persistent.json`.

Medição após o worker persistente, com o mesmo modelo e frases do diagnóstico:

| Pedido | HTTP antes | HTTP persistente | Intervalo após áudio anterior |
| --- | ---: | ---: | ---: |
| Olá. | 4713 ms | 5005 ms (arranque frio) | — |
| Tudo bem? | 4438 ms | 89 ms | 28 ms (antes: 3824 ms) |
| Vamos começar. | 4546 ms | 126 ms | 10 ms (antes: 3853 ms) |
| Longa 1 | 4697 ms | 435 ms | — |
| Longa 2 | 4657 ms | 419 ms | 39 ms (antes: 30 ms) |
| Longa 3 | 4704 ms | 302 ms | 15 ms (antes: 26 ms) |

As passagens usam o mesmo conjunto de frases e o mesmo método, em momentos diferentes; a duração dos WAVs e os eventos do navegador variam ligeiramente. Nas frases longas a preparação já escondia o carregamento antigo; as pausas estavam próximas das atuais. Um único worker tratou os oito pedidos (seis frases + dois sobrepostos), ambos os pedidos concorrentes devolveram 200 e o processo foi recolhido ao encerrar a API. Eventos headless não medem a saída acústica de uma coluna física. O silêncio natural no WAV permanece.
