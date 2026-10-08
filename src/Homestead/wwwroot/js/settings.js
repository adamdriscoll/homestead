(() => {
  const settings = document.querySelector('[data-settings]');
  if (!settings) return;

  const navigation = settings.querySelector('[data-settings-navigation]');
  const tabs = [...navigation.querySelectorAll('[data-settings-tab]')];
  const panels = [...settings.querySelectorAll('[data-settings-panel]')];
  const form = settings.querySelector('#settings-form');
  const sectionInput = form.querySelector('[name="Section"]');
  const desktop = window.matchMedia('(min-width: 48rem)');

  function selectSection(id, focus = false) {
    if (!panels.some(panel => panel.id === id)) id = 'general';
    panels.forEach(panel => { panel.hidden = panel.id !== id; });
    tabs.forEach(tab => {
      const selected = tab.hash === `#${id}`;
      tab.classList.toggle('menu-active', selected);
      tab.setAttribute('aria-selected', String(selected));
      tab.tabIndex = selected ? 0 : -1;
      if (selected && focus) tab.focus();
    });
    sectionInput.value = id;
    form.hidden = id === 'images';
    history.replaceState(null, '', `#${id}`);
  }

  navigation.setAttribute('role', 'tablist');
  const updateOrientation = () => navigation.setAttribute('aria-orientation', desktop.matches ? 'vertical' : 'horizontal');
  updateOrientation();
  desktop.addEventListener('change', updateOrientation);
  navigation.querySelector('ul').setAttribute('role', 'presentation');
  tabs.forEach(tab => {
    tab.parentElement.setAttribute('role', 'presentation');
    tab.setAttribute('role', 'tab');
    tab.setAttribute('aria-controls', tab.hash.slice(1));
    tab.addEventListener('click', event => {
      if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
      event.preventDefault();
      selectSection(tab.hash.slice(1));
    });
    tab.addEventListener('keydown', event => {
      const previous = desktop.matches ? 'ArrowUp' : 'ArrowLeft';
      const next = desktop.matches ? 'ArrowDown' : 'ArrowRight';
      let index = tabs.indexOf(tab);
      if (event.key === previous) index = (index - 1 + tabs.length) % tabs.length;
      else if (event.key === next) index = (index + 1) % tabs.length;
      else if (event.key === 'Home') index = 0;
      else if (event.key === 'End') index = tabs.length - 1;
      else if (event.key === ' ') {
        event.preventDefault();
        selectSection(tab.hash.slice(1));
        return;
      } else return;
      event.preventDefault();
      selectSection(tabs[index].hash.slice(1), true);
    });
  });
  panels.forEach(panel => {
    panel.setAttribute('role', 'tabpanel');
    panel.setAttribute('aria-labelledby', `tab-${panel.id}`);
    panel.tabIndex = 0;
  });

  // Reveal the first invalid field before the browser tries to focus it.
  form.addEventListener('invalid', () => {
    const invalidPanel = form.querySelector(':invalid')?.closest('[data-settings-panel]');
    if (invalidPanel) selectSection(invalidPanel.id);
  }, true);
  window.addEventListener('hashchange', () => selectSection(location.hash.slice(1)));
  const errorPanel = form.querySelector('.input-validation-error, [data-settings-error="true"]')?.closest('[data-settings-panel]');
  selectSection(errorPanel?.id || location.hash.slice(1) || sectionInput.value);
})();
