"use strict";

const canvas = document.getElementById("mascote");

if (canvas && window.rive) {
  const mascot = new rive.Rive({
    src: "/assets/rive/mascote.riv",
    canvas,
    artboard: "SOBO-Motion-V01",
    animations: "Idle",
    autoplay: true,

    onLoad: () => {
      mascot.resizeDrawingSurfaceToCanvas();
      console.log("Mascote Lumi carregada");
    },

    onLoadError: (error) => {
      console.error("Erro ao carregar a mascote:", error);
    }
  });

  let currentAnimation = "Idle";

  window.setMascotAnimation = (name) => {
    if (!mascot.loaded || currentAnimation === name) return;

    mascot.stop();
    mascot.play(name);

    currentAnimation = name;
  };
}
