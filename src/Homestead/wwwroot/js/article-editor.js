(() => {
  const form = document.querySelector('#article-editor');
  if (!form) return;
  const body = form.querySelector('[name="Input.BodyMarkdown"]');
  const version = form.querySelector('[name="Input.Version"]');
  const status = document.querySelector('#save-status');
  const uploader = document.querySelector('#image-upload');
  const uploadStatus = document.querySelector('#upload-status');
  const preview = document.querySelector('#draft-preview');
  const previewLink = form.querySelector('[data-preview]');
  const previewToggle = form.querySelector('[data-toggle-preview]');
  let timer, revision = 0, saved = 0, conflict = false, submitting = false;
  let pending = Promise.resolve(true);

  function changed() {
    revision++;
    status.textContent = conflict ? 'Conflict: copy your changes before reloading.' : 'Unsaved changes';
    clearTimeout(timer);
    if (!conflict && !submitting) timer = setTimeout(() => save(), 900);
  }
  form.addEventListener('input', e => { if (e.target.name !== 'ConfirmDelete') changed(); });
  form.addEventListener('change', e => { if (e.target.tagName === 'SELECT') changed(); });
  function save() {
    clearTimeout(timer);
    pending = pending.then(async () => {
      if (conflict) return false;
      if (saved === revision) return true;
      const saving = revision;
      status.textContent = 'Saving…';
      try {
        const response = await fetch(form.action, { method: 'POST', body: new FormData(form), headers: { Accept: 'application/json' } });
        if (response.redirected) throw new Error('Sign in again before saving. Your changes are still in this tab.');
        const data = await response.json();
        if (!response.ok) { conflict = response.status === 409; throw new Error(data.message || 'Save failed.'); }
        version.value = data.version;
        saved = saving;
        status.textContent = saved === revision ? 'Saved' : 'Unsaved changes';
        return true;
      } catch (error) { status.textContent = error.message || 'Save failed. Check your connection and try again.'; return false; }
    });
    return pending;
  }
  function insert(text) {
    body.setRangeText(text, body.selectionStart, body.selectionEnd, 'end');
    body.focus(); changed();
  }
  const wrap = (start, end, fallback) => insert(start + (body.value.slice(body.selectionStart, body.selectionEnd) || fallback) + end);
  function format(action) {
    switch (action) {
      case 'bold': wrap('**', '**', 'bold text'); break;
      case 'italic': wrap('*', '*', 'italic text'); break;
      case 'heading': insert('\n\n## ' + (body.value.slice(body.selectionStart, body.selectionEnd) || 'Heading') + '\n\n'); break;
      case 'link': wrap('[', '](https://example.com)', 'link text'); break;
      case 'list': insert('\n\n' + (body.value.slice(body.selectionStart, body.selectionEnd) || 'List item').split('\n').map(x => '- ' + x).join('\n') + '\n\n'); break;
      case 'quote': insert('\n\n' + (body.value.slice(body.selectionStart, body.selectionEnd) || 'Quoted text').split('\n').map(x => '> ' + x).join('\n') + '\n\n'); break;
      case 'code': wrap('\n\n```\n', '\n```\n\n', 'code'); break;
      case 'image': uploader.querySelector('[name="Upload"]').focus(); uploader.scrollIntoView({ behavior: 'smooth', block: 'center' }); break;
    }
  }
  form.addEventListener('click', e => {
    const tool = e.target.closest('[data-format]');
    if (tool) format(tool.dataset.format);
    const image = e.target.closest('[data-insert-image]');
    if (image) {
      const alt = image.parentElement.querySelector('input').value.replace(/[\[\]\\\r\n]/g, '');
      insert(`\n\n![${alt}](${image.dataset.insertImage})\n\n`);
    }
  });
  body.addEventListener('keydown', e => {
    if ((e.ctrlKey || e.metaKey) && ['b', 'i'].includes(e.key.toLowerCase())) {
      e.preventDefault(); format(e.key.toLowerCase() === 'b' ? 'bold' : 'italic');
    }
  });
  form.addEventListener('submit', async e => {
    if (submitting) return;
    e.preventDefault(); clearTimeout(timer);
    const button = e.submitter;
    if (!await save()) return;
    submitting = true;
    // A completed autosave can resolve in the original submit event's microtask.
    // Browsers ignore reentrant submission; resume in the next event-loop task.
    setTimeout(() => form.requestSubmit(button), 0);
  });
  async function showPreview() {
    if (!await save()) return false;
    preview.src = previewLink.href + '?revision=' + version.value;
    preview.hidden = false; previewToggle.textContent = 'Hide preview'; previewToggle.setAttribute('aria-expanded', 'true');
    return true;
  }
  previewToggle.hidden = false;
  previewToggle.addEventListener('click', () => {
    if (!preview.hidden) { preview.hidden = true; previewToggle.textContent = 'Show preview'; previewToggle.setAttribute('aria-expanded', 'false'); }
    else showPreview();
  });
  previewLink.addEventListener('click', async e => { e.preventDefault(); await showPreview(); });

  async function upload(file) {
    if (!file) return;
    clearTimeout(timer);
    if (!await save()) { uploadStatus.textContent = 'Save the draft before uploading.'; return; }
    uploadStatus.textContent = 'Uploading…';
    const data = new FormData(uploader); data.set('Upload', file);
    try {
      const response = await fetch(uploader.action, { method: 'POST', body: data, headers: { Accept: 'application/json' } });
      if (response.redirected) throw new Error('Sign in again before uploading.');
      const image = await response.json();
      if (!response.ok) throw new Error(image.message || 'Upload failed.');
      const container = document.createElement('div'); container.className = 'border border-base-300 rounded-lg p-4 space-y-2';
      const img = document.createElement('img'); img.src = image.privateUrl; img.alt = image.altText; img.className = 'max-h-40 max-w-full';
      const label = document.createElement('label'); label.className = 'block'; label.textContent = 'Image description (alt text)';
      const input = document.createElement('input'); input.name = `Input.AltText[${image.id}]`; input.value = image.altText; input.maxLength = 1000; input.className = 'input w-full'; label.append(input);
      const button = document.createElement('button'); button.type = 'button'; button.className = 'btn'; button.dataset.insertImage = image.url; button.textContent = 'Insert image';
      container.append(img, label, button); document.querySelector('#article-images').append(container);
      const option = document.createElement('option'); option.value = image.id; option.textContent = file.name; form.querySelector('[name="Input.CoverMediaId"]')?.append(option);
      insert(`\n\n![${image.altText.replace(/[\[\]\\\r\n]/g, '')}](${image.url})\n\n`);
      uploader.querySelector('[name="Upload"]').value = ''; uploadStatus.textContent = 'Uploaded and inserted into the draft.';
    } catch (error) { uploadStatus.textContent = error.message || 'Upload failed. Try again.'; }
  }
  uploader.addEventListener('submit', e => { e.preventDefault(); upload(uploader.querySelector('[name="Upload"]').files[0]); });
  body.addEventListener('paste', e => { const file = [...e.clipboardData.files].find(x => x.type.startsWith('image/')); if (file) { e.preventDefault(); upload(file); } });
  body.addEventListener('dragover', e => { if (e.dataTransfer.types.includes('Files')) e.preventDefault(); });
  body.addEventListener('drop', e => { if (e.dataTransfer.files.length) { e.preventDefault(); upload(e.dataTransfer.files[0]); } });
  window.addEventListener('beforeunload', e => { if (saved !== revision && !submitting) { e.preventDefault(); e.returnValue = ''; } });
})();
