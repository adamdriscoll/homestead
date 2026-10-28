import { readFileSync, mkdirSync, writeFileSync, copyFileSync } from 'node:fs';

// Keep the administration sprite small; icons remain local and need no runtime library.
const source = 'node_modules/@fortawesome/fontawesome-free/';
const sprite = readFileSync(source + 'sprites-full/solid.svg', 'utf8');
const names = ['bold', 'italic', 'heading', 'link', 'list-ul', 'list-ol', 'quote-left', 'code', 'image', 'circle-check', 'spinner', 'circle-exclamation', 'triangle-exclamation', 'asterisk', 'pen', 'eye', 'images', 'gear', 'palette', 'user', 'house', 'screwdriver-wrench', 'box-open', 'chevron-right', 'right-from-bracket', 'desktop', 'sun', 'moon', 'location-dot', 'share-nodes', 'file-lines', 'star', 'dumbbell', 'expand', 'compress'];
const symbols = names.map(name => {
  const symbol = sprite.match(new RegExp(`<symbol id="${name}"[\\s\\S]*?</symbol>`));
  if (!symbol) throw new Error(`Missing Font Awesome icon: ${name}`);
  return symbol[0];
});
mkdirSync('wwwroot/icons', { recursive: true });
writeFileSync('wwwroot/icons/toolbar.svg', '<!-- Font Awesome Free 7.3.1 by Fonticons, Inc. https://fontawesome.com License: CC BY 4.0 -->\n' +
  `<svg xmlns="http://www.w3.org/2000/svg">${symbols.join('')}</svg>\n`);
copyFileSync(source + 'LICENSE.txt', 'wwwroot/icons/LICENSE.txt');
