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

O CSS em `Lumi.Api/wwwroot/chat.html` implementa os valores acima. Página com largura máxima de 650px, margem de 40px e fluxo natural do documento. O formulário adapta-se a ecrãs estreitos. Tipografia de sistema para títulos, conteúdo e controlos; sem fontes externas ou animações.

Feedback textual, foco visível e estados desativados comunicam as ações disponíveis. Estado e erro têm espaço reservado. Scrollbars pertencem ao documento. Só existe este ecrã; não há um fluxo irmão para comparar.
