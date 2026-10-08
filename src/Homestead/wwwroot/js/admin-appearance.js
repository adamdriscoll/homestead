(() => {
  let appearance = "system";
  try { appearance = localStorage.getItem("homestead-admin-appearance") ?? "system"; } catch { /* Storage is optional. */ }
  if (!["system", "light", "dark"].includes(appearance)) appearance = "system";
  const apply = () => {
    if (appearance === "system") document.documentElement.removeAttribute("data-theme");
    else document.documentElement.setAttribute("data-theme", appearance);
  };
  apply();
  document.addEventListener("DOMContentLoaded", () => {
    const controls = document.getElementById("admin-appearance");
    if (!controls) return;
    const buttons = controls.querySelectorAll("[data-admin-appearance]");
    const updateButtons = () => {
      buttons.forEach(button => button.setAttribute("aria-pressed", String(button.dataset.adminAppearance === appearance)));
    };
    updateButtons();
    controls.classList.replace("hidden", "flex");
    buttons.forEach(button => {
      button.addEventListener("click", () => {
        appearance = button.dataset.adminAppearance;
        apply();
        updateButtons();
        try { localStorage.setItem("homestead-admin-appearance", appearance); } catch { /* Storage is optional. */ }
      });
    });
  });
})();
