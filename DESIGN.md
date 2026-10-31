---
colors:
  background: "#20232a"
  text: "#f5f5f5"
  userMessage: "#354052"
  lumiMessage: "#31483e"
  error: "#ffb4ab"
  focus: "#91caff"
typography:
  body:
    fontFamily: "system-ui, sans-serif"
spacing:
  controlGap: "8px"
  messageGap: "12px"
  pagePadding: "16px"
rounded:
  message: "12px"
  input: "8px"
omitted:
  - section: shadows
    reason: "A interface existente não usa elevação."
---

# Lumi — contexto visual

Ferramenta pessoal de conversa em pt-PT. Preservar a página escura existente, mensagens discretas em azul/cinza e verde e controlos nativos. Não introduzir uma página de marketing nem uma biblioteca de componentes.

O CSS em `Lumi.Api/wwwroot/chat.css` implementa os valores acima. Estrutura de conversação com altura de viewport dinâmica, largura máxima de 960px, cabeçalho compacto e compositor inferior sempre visíveis. Apenas a área de mensagens tem deslocação independente. O formulário adapta-se a ecrãs estreitos. Tipografia de sistema para títulos, conteúdo e controlos; sem fontes externas ou animações.

Feedback textual, foco visível e estados desativados comunicam as ações disponíveis. O estado surge discretamente apenas durante atividade. O cancelamento fica junto ao campo durante captura/transcrição; parar voz surge durante reprodução. A área de atividade reserva uma linha para evitar saltos no campo. Scrollbars pertencem à área de mensagens. Só existe este ecrã; não há um fluxo irmão para comparar.
