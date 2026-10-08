(() => {
  const element = document.getElementById('location-map');
  const L = window.L;
  if (!element || !L) return;

  const posts = [...document.querySelectorAll('[data-map-post]')].map(item => ({
    item,
    latitude: Number(item.dataset.latitude),
    longitude: Number(item.dataset.longitude)
  })).filter(post => Number.isFinite(post.latitude) && Math.abs(post.latitude) <= 90 &&
    Number.isFinite(post.longitude) && Math.abs(post.longitude) <= 180);

  // Each post owns a marker. At shared coordinates every popup offers all posts there.
  const places = new Map();
  for (const post of posts) {
    const key = `${post.latitude},${post.longitude}`;
    if (!places.has(key)) places.set(key, []);
    places.get(key).push(post);
  }

  element.hidden = false;
  const map = L.map(element, { scrollWheelZoom: false });
  L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
  }).on('tileerror', () => {
    const status = document.querySelector('[data-map-status]');
    if (status) status.hidden = false;
  }).addTo(map);

  const markers = posts.map(post => {
    const popup = document.createElement('div');
    for (const neighbor of places.get(`${post.latitude},${post.longitude}`)) {
      // Clone already encoded DOM content; never interpret authored text as popup HTML.
      const entry = document.createElement('div');
      entry.append(neighbor.item.querySelector('a').cloneNode(true));
      entry.append(neighbor.item.querySelector('p').cloneNode(true));
      popup.append(entry);
    }
    const title = post.item.querySelector('a').textContent;
    return L.marker([post.latitude, post.longitude], { title, alt: title })
      .bindPopup(popup, {
        maxWidth: Math.min(280, element.clientWidth - 112), maxHeight: 240,
        autoPanPaddingTopLeft: [52, 52], autoPanPaddingBottomRight: [12, 28]
      }).addTo(map);
  });
  if (markers.length) map.fitBounds(L.featureGroup(markers).getBounds(), { padding: [32, 32], maxZoom: 12, animate: false });
  else map.setView([20, 0], 2);
})();
