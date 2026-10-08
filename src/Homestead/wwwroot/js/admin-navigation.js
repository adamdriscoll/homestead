(() => {
  const menu = document.querySelector("[data-admin-menu]");
  if (!menu) return;
  const trigger = menu.querySelector("summary");
  document.addEventListener("click", event => {
    if (!menu.contains(event.target)) menu.open = false;
  });
  document.addEventListener("keydown", event => {
    if (event.key !== "Escape" || !menu.open) return;
    menu.open = false;
    trigger.focus();
    event.preventDefault();
  });
  menu.addEventListener("focusout", event => {
    if (event.relatedTarget && !menu.contains(event.relatedTarget)) menu.open = false;
  });
})();
