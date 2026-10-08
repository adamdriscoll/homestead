import { mkdirSync, copyFileSync } from 'node:fs';

// Bundle the pinned Leaflet release locally; generated files stay outside wwwroot.
const files = ['leaflet.js', 'leaflet.css', 'images/marker-icon.png', 'images/marker-icon-2x.png',
  'images/marker-shadow.png', 'images/layers.png', 'images/layers-2x.png'];
mkdirSync('Maps/leaflet/images', { recursive: true });
for (const file of files) copyFileSync(`node_modules/leaflet/dist/${file}`, `Maps/leaflet/${file}`);
copyFileSync('node_modules/leaflet/LICENSE', 'Maps/leaflet/LICENSE.txt');
