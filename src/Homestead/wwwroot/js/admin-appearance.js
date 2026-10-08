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
    const select = document.getElementById("admin-appearance");
    if (!select) return;
    select.value = appearance;
    select.addEventListener("change", () => {
      appearance = select.value;
      apply();
      try { localStorage.setItem("homestead-admin-appearance", appearance); } catch { /* Storage is optional. */ }
    });
  });
})();
