import { mkdirSync, copyFileSync, readdirSync } from 'node:fs';

// Keep the browser runtime private until a generation references a diagram.
mkdirSync('Diagrams/vendor', { recursive: true });
copyFileSync('node_modules/mermaid/dist/mermaid.esm.min.mjs', 'Diagrams/vendor/mermaid.esm.min.mjs');
function copyModules(source, destination) {
  mkdirSync(destination, { recursive: true });
  for (const item of readdirSync(source, { withFileTypes: true })) {
    if (item.isDirectory()) copyModules(`${source}/${item.name}`, `${destination}/${item.name}`);
    else if (item.name.endsWith('.mjs')) copyFileSync(`${source}/${item.name}`, `${destination}/${item.name}`);
  }
}
copyModules('node_modules/mermaid/dist/chunks/mermaid.esm.min', 'Diagrams/vendor/chunks/mermaid.esm.min');
copyFileSync('node_modules/mermaid/LICENSE', 'Diagrams/vendor/LICENSE.txt');
