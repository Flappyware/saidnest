import { recordings } from './samples.js';

const requestedId = new URLSearchParams(location.search).get('sample');
let selectedId = recordings.find(record => record.id === requestedId)?.id || recordings[0].id;
const list = document.querySelector('#recording-list');
const transcript = document.querySelector('#transcript');
const search = document.querySelector('#search');
const filter = document.querySelector('#filter');
let downloadUrl;
const escape = value => value.replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]));
const matches = record => (filter.value === 'all' || record.type === filter.value) && [record.title, record.description, ...record.segments.map(segment => segment[2])].join(' ').toLowerCase().includes(search.value.trim().toLowerCase());

function highlight(text) {
  const query = search.value.trim().toLowerCase();
  if (!query) return escape(text);
  let cursor = 0;
  let result = '';
  let found;
  while ((found = text.toLowerCase().indexOf(query, cursor)) !== -1) {
    result += escape(text.slice(cursor, found)) + '<mark>' + escape(text.slice(found, found + query.length)) + '</mark>';
    cursor = found + query.length;
  }
  return result + escape(text.slice(cursor));
}

function notify(message) {
  const status = document.querySelector('#announcement');
  status.textContent = message;
  status.hidden = false;
}

function render() {
  document.querySelector('#announcement').hidden = true;
  if (downloadUrl) URL.revokeObjectURL(downloadUrl);
  const visible = recordings.filter(matches);
  if (!visible.some(record => record.id === selectedId)) selectedId = visible[0]?.id;
  document.querySelector('#result-count').textContent = `${visible.length} recording${visible.length === 1 ? '' : 's'}`;
  list.innerHTML = visible.length ? visible.map(record => `<button class="recording-card ${record.id === selectedId ? 'selected' : ''}" data-id="${record.id}" aria-pressed="${record.id === selectedId}"><span class="recording-meta"><span>${record.date}, ${record.time}</span><span>${record.duration}</span></span><strong>${highlight(record.title)}</strong><span class="recording-description">${highlight(record.description)}</span><span class="recording-footer">${record.source}<span class="read-affordance">Read transcript <span aria-hidden="true">↗</span></span></span></button>`).join('') : '<div class="empty-state"><h3>No matching conversations</h3><p>Try another phrase or recording type.</p><button class="button small secondary" id="clear-search">Clear filters</button></div>';
  const record = visible.find(item => item.id === selectedId);
  if (!record) {
    transcript.innerHTML = '<div class="empty-state"><h2>No transcript selected</h2><p>Clear the filters to return to the sample conversations.</p></div>';
    return;
  }
  downloadUrl = URL.createObjectURL(new Blob([asText(record)], { type: 'text/plain;charset=utf-8' }));
  transcript.innerHTML = `<button class="back-to-list subtle-button" id="back-to-list">← Conversations</button><div class="transcript-top"><span>${record.source}</span><span>Fictional sample</span></div><h2 id="transcript-title" tabindex="-1">${escape(record.title)}</h2><p class="transcript-meta">${record.date}, 2026 <span aria-hidden="true">·</span> ${record.time} <span aria-hidden="true">·</span> ${record.duration}</p><div class="transcript-actions"><button id="copy-transcript" class="button small">Copy transcript</button><a id="download-transcript" class="button small secondary" href="${downloadUrl}" download="saidnest-sample-${record.id}.txt">Download .txt</a></div><div class="transcript-segments">${record.segments.map(([time, speaker, text], index) => `<article class="segment"><span class="timestamp">${time}</span><div><div class="segment-heading"><span class="speaker">${speaker}</span><button class="copy-passage" data-segment="${index}" aria-label="Copy passage at ${time}">Copy</button></div><p>${highlight(text)}</p></div></article>`).join('')}</div><p class="transcript-end">End of sample excerpt. Audio playback is not available in this preview.</p>`;
}

function asText(record) {
  return `SaidNest — fictional sample transcript\n${record.title}\n${record.date}, 2026 · ${record.source}\n\n` + record.segments.map(([time, speaker, text]) => `[${time}] ${speaker}: ${text}`).join('\n\n');
}

list.addEventListener('click', event => {
  const button = event.target.closest('[data-id]');
  if (button) {
    selectedId = button.dataset.id;
    render();
    document.body.classList.add('detail-open');
    if (matchMedia('(max-width: 700px)').matches) document.querySelector('#transcript-title').focus();
    else list.querySelector(`[data-id="${selectedId}"]`).focus();
  }
  if (event.target.closest('#clear-search')) {
    search.value = '';
    filter.value = 'all';
    render();
    search.focus();
  }
});

transcript.addEventListener('click', async event => {
  if (event.target.closest('#back-to-list')) {
    document.body.classList.remove('detail-open');
    list.querySelector(`[data-id="${selectedId}"]`)?.focus();
    return;
  }
  const record = recordings.find(item => item.id === selectedId);
  if (!record) return;
  const passage = event.target.closest('[data-segment]');
  if (event.target.closest('#copy-transcript') || passage) {
    const segment = passage ? record.segments[Number(passage.dataset.segment)] : null;
    const text = segment ? `SaidNest — fictional sample passage\n${record.title}\n[${segment[0]}] ${segment[1]}: ${segment[2]}` : asText(record);
    try {
      await navigator.clipboard.writeText(text);
      notify(segment ? `Passage at ${segment[0]} copied with its context.` : 'Sample transcript copied with timestamps.');
    } catch { notify('Clipboard access is unavailable. Select the text or download the transcript instead.'); }
  }
  if (event.target.closest('#download-transcript')) {
    notify('Preparing sample transcript download.');
  }
});

search.addEventListener('input', render);
filter.addEventListener('change', render);
if (recordings.some(record => record.id === requestedId)) document.body.classList.add('detail-open');
render();
