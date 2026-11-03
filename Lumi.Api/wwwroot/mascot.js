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
const container = document.getElementById("mascot-container");

let drag = null;

container.addEventListener("pointerdown", (event) => {
  const rect = container.getBoundingClientRect();

  drag = {
    x: event.clientX - rect.left,
    y: event.clientY - rect.top
  };

  container.setPointerCapture(event.pointerId);
});

container.addEventListener("pointermove", (event) => {
  if (!drag) return;

  const x = Math.max(
    0,
    Math.min(window.innerWidth - container.offsetWidth, event.clientX - drag.x)
  );

  const y = Math.max(
    0,
    Math.min(window.innerHeight - container.offsetHeight, event.clientY - drag.y)
  );

  container.style.left = `${x}px`;
  container.style.top = `${y}px`;
});
container.addEventListener("pointerup", () => {
  if (drag) {
    localStorage.setItem(
      "lumi-mascot-position",
      JSON.stringify({
        x: container.offsetLeft,
        y: container.offsetTop
      })
    );
  }

  drag = null;
});

container.addEventListener("pointercancel", () => {
  drag = null;
});

const savedPosition = localStorage.getItem("lumi-mascot-position");

if (savedPosition) {
  try {
    const { x, y } = JSON.parse(savedPosition);

    if (Number.isFinite(x) && Number.isFinite(y)) {
      container.style.left = `${Math.max(0, Math.min(x, window.innerWidth - container.offsetWidth))}px`;
      container.style.top = `${Math.max(0, Math.min(y, window.innerHeight - container.offsetHeight))}px`;
    }
  } catch {
    localStorage.removeItem("lumi-mascot-position");
  }
}

container.addEventListener("pointerup", () => {
  if (!drag) return;

  localStorage.setItem(
    "lumi-mascot-position",
    JSON.stringify({
      x: container.offsetLeft,
      y: container.offsetTop
    })
  );
});