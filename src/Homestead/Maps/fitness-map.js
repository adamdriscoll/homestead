(() => {
  document.querySelectorAll('[data-fitness-activity]').forEach((activity, index) => {
    const tablist = activity.querySelector('[data-fitness-tabs]');
    if (!tablist) return;
    const tabs = [...tablist.querySelectorAll('[data-fitness-tab]')];
    const panels = [...activity.querySelectorAll('[data-fitness-panel]')];
    const select = selected => {
      tabs.forEach(tab => {
        const active = tab === selected;
        tab.setAttribute('aria-selected', String(active));
        tab.tabIndex = active ? 0 : -1;
        tab.classList.toggle('tab-active', active);
      });
      panels.forEach(panel => { panel.hidden = panel.dataset.fitnessPanel !== selected.dataset.fitnessTab; });
    };
    tablist.setAttribute('role', 'tablist');
    tabs.forEach((tab, tabIndex) => {
      const panel = panels.find(panel => panel.dataset.fitnessPanel === tab.dataset.fitnessTab);
      tab.id = `fitness-${index}-${tabIndex}-tab`;
      panel.id = `fitness-${index}-${tabIndex}-panel`;
      tab.setAttribute('role', 'tab');
      tab.setAttribute('aria-controls', panel.id);
      panel.setAttribute('role', 'tabpanel');
      panel.setAttribute('aria-labelledby', tab.id);
      panel.tabIndex = 0;
      tab.addEventListener('click', () => select(tab));
      tab.addEventListener('keydown', event => {
        let next;
        if (event.key === 'ArrowRight') next = (tabIndex + 1) % tabs.length;
        else if (event.key === 'ArrowLeft') next = (tabIndex + tabs.length - 1) % tabs.length;
        else if (event.key === 'Home') next = 0;
        else if (event.key === 'End') next = tabs.length - 1;
        else return;
        event.preventDefault();
        select(tabs[next]);
        tabs[next].focus();
      });
    });
    select(tabs[0]);
    tablist.hidden = false;
  });
  if (!window.L) return;
  document.querySelectorAll('[data-fitness-route]').forEach(figure => {
    const element = figure.querySelector('[data-fitness-map]');
    if (!element) return;
    const toolbar = figure.querySelector('[data-fitness-map-toolbar]');
    const status = figure.querySelector('[data-fitness-map-status]');
    let segments;
    try { segments = JSON.parse(element.dataset.route); } catch { return; }
    if (!Array.isArray(segments) || !segments.length || segments.length > 100 ||
        segments.some(segment => !Array.isArray(segment) || !segment.length || segment.some(point =>
          !Array.isArray(point) || point.length !== 2 || !point.every(Number.isFinite) ||
          Math.abs(point[0]) > 90 || Math.abs(point[1]) > 180))) return;
    if (segments.reduce((count, segment) => count + segment.length, 0) > 50000) return;

    // Keep dateline crossings in the same world copy instead of drawing across the world.
    const reference = segments[0][0][1];
    segments = segments.map(segment => {
      let previous = reference;
      return segment.map(([latitude, longitude]) => {
        longitude += 360 * Math.round((previous - longitude) / 360);
        previous = longitude;
        return [latitude, longitude];
      });
    });
    const initialize = () => {
      if (element.dataset.initialized) return;
      element.dataset.initialized = 'true';
      element.hidden = false;
      const map = L.map(element, { scrollWheelZoom: false, maxZoom: 19 });
      const bounds = L.latLngBounds(segments.flat());
      const fit = () => map.fitBounds(bounds, { padding: [32, 32], maxZoom: 16, animate: false });
      fit();
      L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
      }).on('tileerror', () => { status.hidden = false; }).addTo(map);
      segments.forEach(points => {
        // A neutral halo preserves contrast over roads, water, and either site appearance.
        L.polyline(points, { color: 'var(--color-neutral)', weight: 8, opacity: 1, interactive: false }).addTo(map);
        L.polyline(points, { color: 'var(--color-primary)', weight: 4, opacity: 1, interactive: false, className: 'fitness-map-line' }).addTo(map);
      });
      const marker = (point, label, letter) => L.marker(point, {
        title: label, alt: label,
        icon: L.divIcon({ className: 'fitness-map-marker', html: letter, iconSize: [28, 28], iconAnchor: [14, 14] })
      }).bindPopup(label).addTo(map);
      marker(segments[0][0], 'Start', 'S');
      marker(segments.at(-1).at(-1), 'Finish', 'F');
      toolbar.querySelector('[data-fitness-fit-route]').addEventListener('click', fit);
      toolbar.hidden = false;
      new ResizeObserver(() => { if (element.clientWidth && element.clientHeight) map.invalidateSize({ pan: false }); }).observe(element);
    };
    if (element.dataset.privateMap === 'true') {
      const consent = figure.querySelector('[data-fitness-map-consent]');
      consent.hidden = false;
      consent.querySelector('button').addEventListener('click', () => { initialize(); consent.hidden = true; });
    } else if ('IntersectionObserver' in window) {
      // Load only maps the visitor is viewing, including feeds containing multiple routes.
      const observer = new IntersectionObserver(entries => {
        if (entries.some(entry => entry.isIntersecting)) { observer.disconnect(); initialize(); }
      });
      observer.observe(figure);
    } else initialize();
  });
})();
