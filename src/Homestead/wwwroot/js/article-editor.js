(() => {
  const form = document.querySelector('#article-editor');
  if (!form) return;
  let body = form.querySelector('[name="Input.BodyMarkdown"]');
  const structured = ['Review', 'Guide', 'Gallery', 'Fitness'].includes(form.dataset.postType);
  const version = form.querySelector('[name="Input.Version"]');
  const status = document.querySelector('#save-status');
  const statusText = document.querySelector('#save-status-text');
  const saveError = document.querySelector('#save-error');
  const uploader = document.querySelector('#image-upload');
  const filePicker = document.querySelector('input[type="file"][form="image-upload"]');
  const uploadStatus = document.querySelector('#upload-status');
  const inlineUploadStatus = document.querySelector('#inline-upload-status');
  const preview = document.querySelector('#draft-preview');
  const tabs = form.querySelector('#editor-tabs');
  const writeTab = form.querySelector('#write-tab');
  const previewTab = form.querySelector('#preview-tab');
  const imagesTab = form.querySelector('#images-tab');
  const editorTabs = [...tabs.querySelectorAll('[role="tab"]')];
  const editorPanels = editorTabs.map(tab => form.querySelector('#' + tab.getAttribute('aria-controls')));
  const selectedEditorTab = form.querySelector('[name="EditorTab"]');
  form.querySelectorAll('[data-guide-step]').forEach(step => {
    const key = 'homestead:guide-step:' + step.dataset.guideStep;
    try { step.open = sessionStorage.getItem(key) !== 'closed'; } catch { /* Storage may be disabled. */ }
    step.addEventListener('toggle', () => {
      try { sessionStorage.setItem(key, step.open ? 'open' : 'closed'); } catch { /* Native collapse still works. */ }
    });
    step.querySelector('[data-step-title]').addEventListener('input', event => {
      const title = event.target.value.trim();
      step.querySelector('[data-step-heading]').textContent = title ? ' — ' + title : '';
    });
    const duration = step.querySelector('[data-step-duration]');
    function validateDuration() {
      const parts = duration.value.split(':').map(Number);
      const seconds = (parts[0] || 0) * 3600 + (parts[1] || 0) * 60 + (parts[2] || 0);
      duration.setCustomValidity(duration.value && seconds === 0 ? 'Enter a duration greater than zero, or leave the time blank.' : '');
    }
    if (duration) { duration.addEventListener('input', validateDuration); validateDuration(); }
  });
  const locationButton = form.querySelector('#add-location');
  const locationStatus = form.querySelector('#location-status');
  const deleteDialog = document.querySelector('#delete-post-dialog');
  const deleteButton = form.querySelector('[data-delete-post]');
  const confirmDelete = document.createElement('input');
  confirmDelete.type = 'hidden'; confirmDelete.name = 'ConfirmDelete'; confirmDelete.value = 'false'; form.append(confirmDelete);
  let timer, revision = 0, saved = 0, conflict = false, submitting = false, uploading = false;
  let uploadTask = Promise.resolve();
  let locationTask = Promise.resolve();
  let pending = Promise.resolve(true);

  function setStatus(state, message) {
    const states = {
      empty: ['Not saved', 'badge-neutral', 'pen'],
      saved: ['Saved', 'badge-success', 'circle-check'],
      saving: ['Saving…', 'badge-info', 'spinner'],
      unsaved: ['Unsaved', 'badge-warning', 'circle-exclamation'],
      error: ['Save failed', 'badge-error', 'triangle-exclamation'],
      conflict: ['Conflict', 'badge-error', 'triangle-exclamation']
    };
    const [label, color, icon] = states[state];
    status.classList.remove('badge-neutral', 'badge-success', 'badge-info', 'badge-warning', 'badge-error'); status.classList.add(color);
    status.dataset.state = state; statusText.textContent = label;
    status.querySelector('use').setAttribute('href', `/icons/toolbar.svg#${icon}`);
    status.querySelector('svg').classList.toggle('animate-spin', state === 'saving');
    status.querySelector('svg').classList.add('motion-reduce:animate-none');
    saveError.hidden = !message; saveError.textContent = message || '';
  }
  setStatus(form.dataset.newPost === 'true' ? 'empty' : 'saved');
  function persisted(editorUrl) {
    if (form.dataset.newPost !== 'true') return;
    form.dataset.newPost = 'false';
    document.querySelectorAll('input[name="NewPostType"]').forEach(input => input.remove());
    history.replaceState(null, '', editorUrl);
  }
  function setUploadStatus(message, inline = false) {
    uploadStatus.textContent = message;
    inlineUploadStatus.textContent = message; inlineUploadStatus.hidden = !inline;
  }

  function changed() {
    revision++;
    setStatus(conflict ? 'conflict' : 'unsaved', conflict ? 'Conflict: copy your changes before reloading.' : null);
    clearTimeout(timer);
    if (!conflict && !submitting && !uploading) timer = setTimeout(() => save(), 900);
  }
  function setLocationStatus(message, error = false) {
    locationStatus.hidden = false; locationStatus.textContent = message;
    locationStatus.classList.toggle('text-error', error);
  }
  async function addLocation() {
    if (!window.isSecureContext) { setLocationStatus('Location requires HTTPS. Enter coordinates in Details, or open the editor over HTTPS.', true); return; }
    if (!navigator.geolocation) { setLocationStatus('Location is unavailable in this browser. Enter coordinates in Details.', true); return; }
    locationButton.disabled = true;
    locationButton.setAttribute('aria-busy', 'true');
    locationButton.querySelector('span').textContent = 'Locating…';
    setLocationStatus('Finding your location…');
    try {
      const position = await new Promise((resolve, reject) => navigator.geolocation.getCurrentPosition(resolve, reject, {
        enableHighAccuracy: true, timeout: 15000, maximumAge: 0
      }));
      form.querySelector('[name="Input.Latitude"]').value = position.coords.latitude;
      form.querySelector('[name="Input.Longitude"]').value = position.coords.longitude;
      changed();
      setLocationStatus('Location added.');
    } catch (error) {
      const messages = {
        1: 'Location permission was denied. Allow location access in your browser, or enter coordinates in Details.',
        2: 'Your location is unavailable. Try again or enter coordinates in Details.',
        3: 'Finding your location timed out. Try again or enter coordinates in Details.'
      };
      setLocationStatus(messages[error.code] || 'Your location could not be found. Try again or enter coordinates in Details.', true);
    } finally {
      locationButton.disabled = false;
      locationButton.setAttribute('aria-busy', 'false');
      locationButton.querySelector('span').textContent = 'Add Location';
    }
  }
  locationButton.hidden = false;
  locationButton.addEventListener('click', () => { if (!submitting) locationTask = addLocation(); });
  form.addEventListener('input', e => { if (e.target.form === form && e.target.name !== 'ConfirmDelete') changed(); });
  form.addEventListener('change', e => { if (e.target.tagName === 'SELECT') changed(); });
  form.addEventListener('focusin', e => {
    if (e.target.tagName === 'TEXTAREA' && (e.target.name === 'Input.BodyMarkdown' || e.target.name.endsWith('.BodyMarkdown'))) body = e.target;
  });
  function save() {
    clearTimeout(timer);
    pending = pending.then(async () => {
      if (conflict) return false;
      if (saved === revision) return true;
      const saving = revision;
      setStatus('saving');
      try {
        const response = await fetch(form.action, { method: 'POST', body: new FormData(form), headers: { Accept: 'application/json' } });
        if (response.redirected) throw new Error('Sign in again before saving. Your changes are still in this tab.');
        const data = await response.json();
        if (!response.ok) { conflict = response.status === 409; throw new Error(data.message || 'Save failed.'); }
        version.value = data.version;
        saved = saving;
        if (data.persisted) persisted(data.editorUrl);
        setStatus(saved === revision ? (data.persisted ? 'saved' : 'empty') : 'unsaved');
        return true;
      } catch (error) { setStatus(conflict ? 'conflict' : 'error', error.message || 'Save failed. Check your connection and try again.'); return false; }
    });
    return pending;
  }
  const postTypeNavigation = document.querySelector('#post-type-navigation');
  if (postTypeNavigation) {
    let switchingType = false;
    postTypeNavigation.querySelectorAll('[data-post-type-link]').forEach(link => {
      link.addEventListener('click', async event => {
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        if (submitting || switchingType || link.getAttribute('aria-current') === 'page') return;
        switchingType = true;
        postTypeNavigation.setAttribute('aria-busy', 'true');
        await locationTask;
        await uploadTask;
        do {
          if (!await save()) { switchingType = false; postTypeNavigation.removeAttribute('aria-busy'); return; }
        } while (saved !== revision);
        submitting = true; window.location.assign(link.href);
      });
    });
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
      case 'ordered-list': insert('\n\n' + (body.value.slice(body.selectionStart, body.selectionEnd) || 'List item').split('\n').map((x, i) => `${i + 1}. ${x}`).join('\n') + '\n\n'); break;
      case 'quote': insert('\n\n' + (body.value.slice(body.selectionStart, body.selectionEnd) || 'Quoted text').split('\n').map(x => '> ' + x).join('\n') + '\n\n'); break;
      case 'code': wrap('\n\n```\n', '\n```\n\n', 'code'); break;
      case 'image': inlinePicker.click(); break;
    }
  }
  form.addEventListener('click', e => {
    const tool = e.target.closest('[data-format]');
    if (tool) {
      body = tool.closest('[data-markdown-editor]').querySelector('textarea');
      format(tool.dataset.format);
    }
    const image = e.target.closest('[data-insert-image]');
    if (image) {
      previewRequest++; selectTab(writeTab);
      const alt = image.closest('[data-media]').querySelector('[name^="Input.AltText"]').value.replace(/[\[\]\\\r\n]/g, '');
      insert(`\n\n![${alt}](${image.dataset.insertImage})\n\n`);
    }
  });
  form.addEventListener('keydown', e => {
    if (isMarkdown(e.target) && (e.ctrlKey || e.metaKey) && ['b', 'i'].includes(e.key.toLowerCase())) {
      body = e.target;
      e.preventDefault(); format(e.key.toLowerCase() === 'b' ? 'bold' : 'italic');
    }
  });
  form.addEventListener('submit', async e => {
    if (submitting) return;
    e.preventDefault(); clearTimeout(timer);
    const button = e.submitter;
    if (button === deleteButton && confirmDelete.value !== 'true') { deleteDialog.showModal(); return; }
    await locationTask;
    await uploadTask;
    if (!await save()) { confirmDelete.value = 'false'; return; }
    submitting = true;
    // A completed autosave can resolve in the original submit event's microtask.
    // Browsers ignore reentrant submission; resume in the next event-loop task.
    setTimeout(() => form.requestSubmit(button), 0);
  });
  let previewRequest = 0;
  function selectTab(selectedTab) {
    selectedEditorTab.value = selectedTab.id.replace('-tab', '');
    if (selectedTab === imagesTab) inlineUploadStatus.hidden = true;
    editorTabs.forEach((tab, index) => {
      const selected = tab === selectedTab;
      editorPanels[index].hidden = !selected;
      tab.setAttribute('aria-selected', String(selected));
      tab.classList.toggle('tab-active', selected); tab.tabIndex = selected ? 0 : -1;
    });
  }
  async function showPreview() {
    const request = ++previewRequest;
    await locationTask;
    await uploadTask;
    if (!await save()) return false;
    if (form.dataset.newPost === 'true') { setStatus('empty', 'Add content to preview this post.'); return false; }
    if (request !== previewRequest) return false;
    preview.src = form.dataset.previewUrl + '?revision=' + version.value;
    selectTab(previewTab);
    return true;
  }
  tabs.hidden = false;
  editorTabs.forEach((tab, index) => {
    editorPanels[index].setAttribute('role', 'tabpanel'); editorPanels[index].setAttribute('aria-labelledby', tab.id);
  });
  selectTab(editorTabs.find(tab => tab.id === selectedEditorTab.value + '-tab') || writeTab);
  editorTabs.filter(tab => tab !== previewTab).forEach(tab => tab.addEventListener('click', () => { previewRequest++; selectTab(tab); }));
  previewTab.addEventListener('click', showPreview);
  form.addEventListener('invalid', () => {
    const invalidField = form.querySelector('input:invalid, select:invalid, textarea:invalid');
    const step = invalidField?.closest('[data-guide-step]');
    if (step) step.open = true;
    const index = editorPanels.findIndex(panel => panel.contains(invalidField));
    if (index >= 0) { previewRequest++; selectTab(editorTabs[index]); }
  }, true);
  tabs.addEventListener('keydown', e => {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) return;
    e.preventDefault();
    const index = editorTabs.indexOf(e.target);
    const target = e.key === 'Home' ? editorTabs[0] : e.key === 'End' ? editorTabs.at(-1) : editorTabs[(index + (e.key === 'ArrowRight' ? 1 : -1) + editorTabs.length) % editorTabs.length];
    target.focus(); target.click();
  });
  deleteDialog.querySelector('[data-cancel-delete]').addEventListener('click', () => deleteDialog.close('cancel'));
  deleteDialog.querySelector('[data-confirm-delete]').addEventListener('click', () => {
    confirmDelete.value = 'true'; deleteDialog.close('confirm'); form.requestSubmit(deleteButton);
  });
  deleteDialog.addEventListener('close', () => {
    if (deleteDialog.returnValue !== 'confirm') confirmDelete.value = 'false';
  });
  deleteDialog.addEventListener('cancel', () => { confirmDelete.value = 'false'; });

  const isMarkdown = target => target.tagName === 'TEXTAREA' && (target.name === 'Input.BodyMarkdown' || target.name.endsWith('.BodyMarkdown'));
  const inlinePicker = document.createElement('input'); inlinePicker.type = 'file'; inlinePicker.multiple = true;
  inlinePicker.accept = filePicker.accept; inlinePicker.hidden = true; form.append(inlinePicker);
  inlinePicker.addEventListener('change', () => startUpload([...inlinePicker.files], body));

  function addImage(image, filename, inlineOnly) {
    const container = document.createElement('div'); container.className = 'border border-base-300 rounded-lg p-4 space-y-2';
    container.dataset.media = image.url;
    if (inlineOnly) {
      const hint = document.createElement('p'); hint.className = 'text-sm';
      hint.textContent = 'Inline image · appears only while referenced in Markdown or selected as the cover.'; container.append(hint);
    }
    const order = document.createElement('input'); order.type = 'hidden'; order.name = 'Input.MediaOrder'; order.value = image.id;
    const img = document.createElement('img'); img.src = image.privateUrl; img.alt = image.altText; img.className = 'max-h-40 max-w-full';
    container.append(order, img);
    function textField(name, text, value) {
      const label = document.createElement('label'); label.className = 'block'; label.textContent = text;
      const input = document.createElement('input'); input.name = `Input.${name}[${image.id}]`; input.value = value; input.maxLength = 1000; input.className = 'input w-full'; label.append(input); container.append(label);
    }
    textField('AltText', 'Image description (alt text)', image.altText);
    if (structured) textField('Captions', 'Caption', '');
    if (form.dataset.postType === 'Guide') {
      const label = document.createElement('label'); label.className = 'block'; label.textContent = 'Photo placement';
      const select = document.createElement('select'); select.name = `Input.StepImages[${image.id}]`; select.className = 'select w-full';
      select.add(new Option('Introduction', ''));
      [...form.querySelectorAll('input[name^="Input.Steps"][name$=".Id"]')].forEach((step, i) => select.add(new Option(`Step ${i + 1}`, step.value)));
      label.append(select); container.append(label);
    }
    const actions = document.createElement('div'); actions.className = 'flex flex-wrap gap-2';
    if (image.retainedOriginal) {
      const download = document.createElement('a'); download.className = 'btn'; download.href = `${image.privateUrl}?original=true`; download.textContent = 'Download original'; actions.append(download);
    }
    const insertButton = document.createElement('button'); insertButton.type = 'button'; insertButton.className = 'btn'; insertButton.dataset.insertImage = image.url; insertButton.textContent = 'Insert image'; actions.append(insertButton);
    for (const [command, text] of [['photo-up', 'Move photo up'], ['photo-down', 'Move photo down'], ['remove-photo', 'Remove photo']]) {
      const button = document.createElement('button'); button.type = 'submit'; button.className = 'btn'; button.name = 'command'; button.value = `${command}:${image.id}`;
      const arrangeUrl = new URL(form.action); arrangeUrl.searchParams.set('handler', 'Arrange');
      button.formAction = arrangeUrl.href; button.textContent = text; actions.append(button);
    }
    container.append(actions); document.querySelector('#article-images').append(container);
    form.querySelector('[name="Input.CoverMediaId"]')?.add(new Option(filename, image.id));
  }

  async function upload(file, target) {
    setUploadStatus('Uploading…', !!target);
    const data = new FormData(uploader); data.delete('Uploads'); data.set('Upload', file);
    data.set('InlineUpload', String(!!target));
    // The marker follows edits made during the request, and lets the author cancel
    // insertion by deleting it. It is removed before the next draft save.
    const marker = target ? `[Uploading image ${crypto.randomUUID()}…]` : null;
    const selectedText = target?.value.slice(target.selectionStart, target.selectionEnd);
    if (target) { target.setRangeText(marker, target.selectionStart, target.selectionEnd, 'end'); changed(); }
    try {
      const response = await fetch(uploader.action, { method: 'POST', body: data, headers: { Accept: 'application/json' } });
      if (response.redirected) throw new Error('Sign in again before uploading.');
      const image = await response.json();
      if (!response.ok) throw new Error(image.message || 'Upload failed.');
      persisted(image.editorUrl);
      addImage(image, file.name, !!target);
      if (target) {
        const position = target.value.indexOf(marker);
        if (position >= 0) target.setRangeText(`![${image.altText.replace(/[\[\]\\\r\n]/g, '')}](${image.url})`, position, position + marker.length, 'end');
      } else if (!structured) {
        insert(`\n\n![${image.altText.replace(/[\[\]\\\r\n]/g, '')}](${image.url})\n\n`);
      }
      changed();
      setUploadStatus(target ? 'Uploaded and inserted into the draft.' : 'Photo uploaded.', !!target); return true;
    } catch (error) {
      if (target && target.value.includes(marker)) { target.value = target.value.replace(marker, selectedText); changed(); }
      setUploadStatus(error.message || 'Upload failed. Try again.', !!target); return false;
    }
    finally {
      if (target && target.value.includes(marker)) { target.value = target.value.replace(marker, ''); changed(); }
    }
  }
  function startUpload(files, target = null) {
    if (uploading || submitting) { setUploadStatus('Wait for the current upload to finish.', !!target); return; }
    if (files.length < 1 || files.length > 50) { setUploadStatus('Choose between one and 50 photos per upload.', !!target); return; }
    uploading = true;
    clearTimeout(timer);
    uploadTask = uploadFiles(files, target);
  }
  async function uploadFiles(files, target) {
    try {
      if (!await save()) { setUploadStatus('Save the draft before uploading.', !!target); return; }
      let failed = false;
      for (const file of files) {
        if (!await upload(file, target)) { failed = true; break; }
      }
      if (!failed) { filePicker.value = ''; inlinePicker.value = ''; }
      await save();
    } finally { uploading = false; }
  }
  uploader.addEventListener('submit', e => { e.preventDefault(); startUpload([...filePicker.files]); });
  const gpxUploader = document.querySelector('#gpx-upload');
  gpxUploader?.addEventListener('submit', event => {
    event.preventDefault();
    const gpxStatus = document.querySelector('#gpx-status');
    if (uploading || submitting) { gpxStatus.textContent = 'Wait for the current operation to finish.'; return; }
    const remove = event.submitter?.name === 'remove';
    if (!remove && !document.querySelector('[name="GpxUpload"]').files.length) { gpxStatus.textContent = 'Choose a GPX file.'; return; }
    uploading = true; clearTimeout(timer);
    uploadTask = (async () => {
      let controls = [];
      try {
        await locationTask;
        if (!await save()) return;
        // Include edits made during the initial save, then freeze fields until the reload.
        while (saved !== revision) if (!await save()) return;
        const data = new FormData(gpxUploader); data.set('Input.Version', version.value);
        if (remove) data.set('remove', 'true');
        controls = [...form.querySelectorAll('input, textarea, select, button')].map(control => [control, control.disabled]);
        controls.forEach(([control]) => { control.disabled = true; });
        gpxStatus.textContent = remove ? 'Removing GPX…' : 'Importing GPX…';
        const response = await fetch(gpxUploader.action, { method: 'POST', body: data, headers: { Accept: 'application/json' } });
        if (response.redirected) throw new Error('Sign in again before importing.');
        const result = await response.json();
        if (!response.ok) { conflict = response.status === 409; throw new Error(result.message || 'GPX import failed.'); }
        submitting = true;
        window.location.assign(result.editorUrl);
      } catch (error) { gpxStatus.textContent = error.message || 'GPX import failed. Check your connection and try again.'; }
      finally { controls.forEach(([control, disabled]) => { control.disabled = disabled; }); uploading = false; }
    })();
  });
  form.addEventListener('paste', e => {
    if (!isMarkdown(e.target)) return;
    const files = [...e.clipboardData.files].filter(x => x.type.startsWith('image/'));
    if (files.length) { e.preventDefault(); body = e.target; startUpload(files, e.target); }
  });
  form.addEventListener('dragover', e => { if (isMarkdown(e.target) && e.dataTransfer.types.includes('Files')) e.preventDefault(); });
  form.addEventListener('drop', e => {
    if (isMarkdown(e.target) && e.dataTransfer.files.length) { e.preventDefault(); body = e.target; startUpload([...e.dataTransfer.files], e.target); }
  });
  window.addEventListener('beforeunload', e => { if ((uploading || saved !== revision) && !submitting) { e.preventDefault(); e.returnValue = ''; } });
})();
