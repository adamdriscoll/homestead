const figures = [...document.querySelectorAll('[data-mermaid]')];
if (figures.length) {
  const { default: mermaid } = await import('./vendor/mermaid.esm.min.mjs');
  const appearance = matchMedia('(prefers-color-scheme: dark)');
  let sequence = 0;
  async function renderAll() {
    mermaid.initialize({
      startOnLoad: false, securityLevel: 'strict', htmlLabels: false,
      fontFamily: 'sans-serif', theme: appearance.matches ? 'dark' : 'default',
      maxTextSize: 50000, maxEdges: 500, suppressErrorRendering: true,
      // Directives cannot weaken the site policy or add CSS/HTML/network resources.
      secure: ['secure', 'securityLevel', 'startOnLoad', 'htmlLabels', 'flowchart', 'fontFamily',
        'theme', 'themeCSS', 'themeVariables', 'dompurifyConfig', 'maxTextSize', 'maxEdges', 'suppressErrorRendering'],
      dompurifyConfig: { FORBID_TAGS: ['foreignObject', 'image', 'a'] }
    });
    for (const figure of figures) {
      const source = figure.querySelector('code').textContent;
      const details = figure.querySelector('details');
      figure.querySelector('[role="status"]')?.remove();
      try {
        if (source.length > 50000) throw new Error('Diagram too large');
        // Image shapes load their URL while measuring, before final SVG sanitization.
        if (/@\{[^}]*\bimg\s*:/is.test(source)) throw new Error('Image shapes are unsupported');
        const { svg } = await mermaid.render(`homestead-diagram-${++sequence}`, source);
        // SVG images isolate diagram styles and cannot execute scripts. Page CSP stays strict.
        const parsed = new DOMParser().parseFromString(svg, 'image/svg+xml');
        parsed.querySelectorAll('script, foreignObject, image').forEach(node => node.remove());
        parsed.querySelectorAll('a').forEach(link => link.replaceWith(...link.childNodes));
        for (const element of parsed.querySelectorAll('*')) {
          for (const attribute of [...element.attributes]) {
            if (attribute.name.toLowerCase().startsWith('on')) element.removeAttribute(attribute.name);
          }
        }
        const image = new Image();
        image.alt = [parsed.querySelector('title')?.textContent, parsed.querySelector('desc')?.textContent]
          .filter(Boolean).join('. ') || 'Mermaid diagram. Diagram source follows.';
        image.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(new XMLSerializer().serializeToString(parsed));
        await image.decode();
        figure.querySelector('img')?.remove();
        figure.prepend(image);
        details.open = false;
      } catch {
        figure.querySelector('img')?.remove();
        details.open = true;
        const status = document.createElement('p');
        status.setAttribute('role', 'status');
        status.textContent = 'This diagram could not be rendered. Its source is shown below.';
        figure.prepend(status);
      }
    }
  }
  // Serialize appearance changes; Mermaid keeps shared configuration.
  let pending = renderAll();
  appearance.addEventListener('change', () => { pending = pending.then(renderAll); });
}
