---
colors:
  background: "#171b21"
  surface: "#1d232b"
  text: "#f3f5f4"
  userMessage: "#303c4c"
  lumiMessage: "#293d36"
  accent: "#a8d8b9"
  error: "#ffb4ab"
  focus: "#91caff"
typography:
  body:
    fontFamily: "Inter, ui-sans-serif, system-ui, sans-serif"
spacing:
  controlGap: "8px"
  messageGap: "12px"
  pagePadding: "16px"
rounded:
  message: "15px"
  input: "14px"
  panel: "22px"
omitted:
  - section: shadows
    reason: "A interface existente não usa elevação."
---

# Lumi — contexto visual

Ferramenta pessoal de conversa em pt-PT. A interface é uma presença de secretária: a mascote permanece ancorada na zona superior do módulo, ao lado do estado e dos controlos agrupados, enquanto os instrumentos de texto ficam recolhidos até serem pedidos. Preservar a base escura, mensagens discretas em azul/cinza e verde e controlos nativos. Não introduzir uma página de marketing nem uma biblioteca de componentes.

O CSS em `Lumi.Api/wwwroot/chat.css` é a fonte runtime dos valores acima. A assinatura visual é o halo discreto em torno da mascote, que reage apenas através dos estados Rive já existentes. A página usa uma coluna central até 1080px; a zona superior não participa no scroll e a região inferior é o contentor expansível dos painéis independentes de transcrição e escrita, ambos recolhidos inicialmente. A mascote reduz de escala em larguras e alturas pequenas, mas permanece sempre no fluxo. A tipografia mantém fallbacks de sistema e não depende de fontes externas.

Feedback textual, foco visível e estados desativados comunicam as ações disponíveis. O estado real está sempre visível: parado, escuta, gravação, transcrição, resposta, fala ou erro. O controlo principal não inicia o microfone sem ação explícita. Scrollbars são globais e permanecem visíveis. O modo ao vivo e o modo com aprovação partilham sessão e transcrição; a diferença é apenas o momento do envio. Só existe este ecrã; não há um fluxo irmão para comparar.
