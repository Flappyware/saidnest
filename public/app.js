const recordings = [
  { id: 'walk', type: 'voice', title: 'A good idea, on a walk', date: 'OCT 28', time: '10:42 AM', duration: '2 min', source: 'Voice note', description: 'Making room for ideas before they disappear.', segments: [
    ['00:00', 'You', 'A thought for the project: we should make keeping an idea feel as natural as saying it out loud.'],
    ['00:12', 'You', 'What if we made it easier to come back to the ideas we almost forgot?'],
    ['00:24', 'You', 'A timeline of conversations. Something we can actually call our own.'],
    ['00:41', 'You', 'Start with a simple recording. Keep the audio and the words together, so there is always context.'],
    ['01:08', 'You', 'Then I can find this thought next week, copy the part I need, and keep going.'],
  ] },
  { id: 'design', type: 'meeting', title: 'Making space for the important things', date: 'OCT 28', time: '9:00 AM', duration: '24 min', source: 'Meeting', description: 'A fictional design conversation about a calmer workspace.', segments: [
    ['00:08', 'Alex', 'The timeline should feel like a useful notebook. Simple enough that you want to return to it.'],
    ['00:23', 'Sam', 'Let the words be the focus. We can keep the recording details nearby without making everything compete.'],
    ['01:12', 'Alex', 'Search has to find the phrase, but playback should let you hear the moment around it.'],
    ['02:34', 'Sam', 'And copying a transcript should include the title and timestamps. That context makes it useful somewhere else.'],
    ['03:10', 'Alex', 'Let’s start there: find a conversation, hear it again, take the part you need.'],
  ] },
  { id: 'week', type: 'voice', title: 'Notes for a fresh week', date: 'OCT 27', time: '4:16 PM', duration: '4 min', source: 'Voice note', description: 'A few things to remember, all in one place.', segments: [
    ['00:03', 'You', 'Three things for the week. First, finish the capture experiment and make sure recording continues if transcription slows down.'],
    ['00:28', 'You', 'Second, compare models with the same sample conversations. Measure accuracy as well as speed.'],
    ['01:02', 'You', 'Third, make the first experience calm and clear. People should understand where their recordings live.'],
    ['01:44', 'You', 'The goal is a useful history that belongs to the person who made it.'],
  ] },
];
let selectedId = recordings[0].id;
const list = document.querySelector('#recording-list');
const transcript = document.querySelector('#transcript');
const search = document.querySelector('#search');
const filter = document.querySelector('#filter');
const escape = value => value.replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]));
const matches = record => (filter.value === 'all' || record.type === filter.value) && [record.title, record.description, ...record.segments.map(s => s[2])].join(' ').toLowerCase().includes(search.value.trim().toLowerCase());
function notify(message) {
  const status = document.querySelector('#announcement');
  status.textContent = message;
  status.hidden = false;
}
function render() {
  const visible = recordings.filter(matches);
  if (!visible.some(r => r.id === selectedId)) selectedId = visible[0]?.id;
  document.querySelector('#result-count').textContent = `${visible.length} recording${visible.length === 1 ? '' : 's'}`;
  list.innerHTML = visible.length ? visible.map(r => `<button class="recording-card ${r.id === selectedId ? 'selected' : ''}" data-id="${r.id}" aria-pressed="${r.id === selectedId}"><span class="recording-meta"><span>${r.date} <span class="meta-dot">·</span> ${r.time}</span><span aria-hidden="true">${r.type === 'meeting' ? '◫' : '≋'}</span></span><strong>${escape(r.title)}</strong><span class="recording-description">${escape(r.description)}</span><span class="recording-footer"><span class="tag">${r.source}</span><span>${r.duration} <span aria-hidden="true">↗</span></span></span></button>`).join('') : '<div class="empty-state"><strong>No matching moments.</strong><p>Try a different phrase or recording type.</p><button class="button small outline" id="clear-search">Clear filters</button></div>';
  const record = visible.find(r => r.id === selectedId);
  if (!record) { transcript.innerHTML = '<div class="empty-state"><p>Your matching transcript will appear here.</p></div>'; return; }
  transcript.innerHTML = `<div class="transcript-top"><span class="tiny-label">TRANSCRIPT</span><span class="tag">Fictional sample</span></div><h2>${escape(record.title)}</h2><p class="transcript-meta">${record.source} <span>·</span> ${record.duration} <span>·</span> ${record.date}, 2026</p><div class="transcript-actions"><button id="copy-transcript" class="button small outline">Copy transcript <span aria-hidden="true">⧉</span></button><button id="download-transcript" class="subtle-button">Download .txt <span aria-hidden="true">↓</span></button></div><div class="transcript-segments">${record.segments.map(([time, speaker, text]) => `<article class="segment"><span class="timestamp">${time}</span><div><span class="speaker">${speaker}</span><p>${escape(text)}</p></div></article>`).join('')}</div><div class="transcript-end"><span aria-hidden="true">✓</span> End of sample transcript</div>`;
}
function asText(record) {
  return `SaidNest — fictional sample transcript\n${record.title}\n${record.date}, 2026 · ${record.source}\n\n` + record.segments.map(([time, speaker, text]) => `[${time}] ${speaker}: ${text}`).join('\n\n');
}
list.addEventListener('click', event => {
  const button = event.target.closest('[data-id]');
  if (button) { selectedId = button.dataset.id; render(); }
  if (event.target.closest('#clear-search')) { search.value = ''; filter.value = 'all'; render(); search.focus(); }
});
transcript.addEventListener('click', async event => {
  const record = recordings.find(r => r.id === selectedId);
  if (!record) return;
  if (event.target.closest('#copy-transcript')) {
    try { await navigator.clipboard.writeText(asText(record)); notify('Sample transcript copied.'); }
    catch { notify('Clipboard access is unavailable. Select the transcript text or download it instead.'); }
  }
  if (event.target.closest('#download-transcript')) {
    const url = URL.createObjectURL(new Blob([asText(record)], { type: 'text/plain;charset=utf-8' }));
    const link = document.createElement('a'); link.href = url; link.download = `saidnest-sample-${record.id}.txt`; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000); notify('Sample transcript downloaded.');
  }
});
search.addEventListener('input', render);
filter.addEventListener('change', render);
render();
