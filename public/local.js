// This module is served by the local repository only. The cloud preview keeps app.js.
import {QualityPanel} from './local-quality.js';
const list = document.querySelector('#recording-list');
const reader = document.querySelector('#transcript');
const search = document.querySelector('#search');
const filter = document.querySelector('#filter');
const notice = document.querySelector('.preview-notice');
const status = document.querySelector('#announcement');
const fixture = document.body.dataset.fixture === 'true';
let records = [], selectedId, selectedTrack = 'mic', selectedWindow=0, displayedRecord, audioUrl, audioKey, downloadUrl, audioRequest, pollTimer, refreshing=false;
const liveActive = record => record.state === 'recording' || ['starting','running','finishing'].includes(record.live?.state);
const active = record => liveActive(record) || record.refinement?.state==='running';
const escape = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
const trackName = id => id === 'mic' ? 'Microphone' : 'System output';
const clock = seconds => { const value = Math.max(0, Math.floor(seconds)); return `${Math.floor(value / 60).toString().padStart(2, '0')}:${(value % 60).toString().padStart(2, '0')}`; };
const title = record => `Recording from ${new Date(record.startedUtc).toLocaleTimeString([], {hour:'numeric',minute:'2-digit'})}`;
const segments = record => record.transcript?.tracks.flatMap(track => track.segments) || [];
const visible = record => (filter.value === 'all' || (filter.value === 'duplex' ? record.tracks.length === 2 : record.tracks.length === 1 && record.tracks[0] === filter.value)) &&
  [title(record), ...segments(record).map(segment => segment.text)].join(' ').toLowerCase().includes(search.value.trim().toLowerCase());
const notify = text => { status.textContent = text; status.hidden = false; };
const quality=new QualityPanel({api,notify,escape,clock,autoReview:new URLSearchParams(location.search).has('review'),onRecord:updated=>{records=records.map(r=>r.id===updated.id?updated:r);render();}});

document.title = 'Your local timeline — SaidNest';
document.querySelector('.preview-label').textContent = fixture ? 'Synthetic test archive' : 'Local archive';
document.querySelector('.page-heading > a')?.remove();
document.querySelector('.recordings-panel').setAttribute('aria-label','Saved recordings');
const refresh = document.createElement('button'); refresh.type='button'; refresh.className='subtle-button'; refresh.textContent='Refresh recordings';
document.querySelector('.page-heading').append(refresh); refresh.addEventListener('click',()=>start());
document.querySelector('.workspace-footnote').textContent = 'Audio and transcripts stay encrypted on this computer. Playback decrypts in memory. Nothing is uploaded.';
document.querySelector('.search-box .sr-only').textContent = 'Search recordings and transcripts';
document.querySelector('.filter-label .sr-only').textContent = 'Filter source configuration';
filter.innerHTML = '<option value="all">All recordings</option><option value="duplex">Microphone + output</option><option value="mic">Microphone only</option><option value="output">Output only</option>';
const archiveNotice=fixture ? 'Synthetic integration fixture. No private recording is shown.' : 'Your archive on this computer. Local English transcription uses an experimental model; check important passages against the audio.';
notice.textContent=archiveNotice;

let token;
try {
  if (location.hostname !== '127.0.0.1' || document.body.dataset.local !== 'true') throw new Error('Open this interface through the local SaidNest service.');
  const supplied = new URLSearchParams(location.hash.slice(1)).get('key');
  if (supplied && /^[A-F0-9]{64}$/.test(supplied)) sessionStorage.setItem('saidnest-local-key', supplied);
  history.replaceState(null, '', location.pathname);
  token = sessionStorage.getItem('saidnest-local-key');
  if (!token) throw new Error('Open the private timeline using the local SaidNest launcher.');
} catch (error) { notice.textContent = error.message; }

async function api(path, options = {}) {
  const response = await fetch(path, {...options, cache:'no-store', credentials:'omit', headers:{...options.headers,Authorization:`Bearer ${token}`}});
  if (response.status === 401) { sessionStorage.removeItem('saidnest-local-key'); throw new Error('Local access expired. Reopen the timeline from the local launcher.'); }
  if (!response.ok) throw new Error(response.status === 409 ? 'Another local operation is running. Try again shortly.' : 'The local operation could not finish. Saved audio is unchanged.');
  return response;
}

function highlighted(text) {
  const query = search.value.trim().toLowerCase();
  let rest = String(text), result = '';
  if (!query) return escape(rest);
  while (true) {
    const index = rest.toLowerCase().indexOf(query);
    if (index < 0) return result + escape(rest);
    result += escape(rest.slice(0,index)) + '<mark>' + escape(rest.slice(index,index+query.length)) + '</mark>';
    rest = rest.slice(index+query.length);
  }
}

function transcriptText(record, track) {
  return `SaidNest — local transcript${liveActive(record) ? ' (provisional live text)' : ''}\n${title(record)}\n${new Date(record.startedUtc).toLocaleString()}\n${trackName(track)}\n\n` +
    (record.transcript?.tracks.find(item => item.track === track)?.segments || []).map(item => `[${clock(item.start)}] ${trackName(item.track)}: ${item.text}`).join('\n\n');
}

function renderList() {
  const matching = records.filter(visible);
  if (!matching.some(record => record.id === selectedId)) selectedId = matching[0]?.id;
  document.querySelector('#result-count').textContent = `${matching.length} recording${matching.length === 1 ? '' : 's'}`;
  list.innerHTML = matching.length ? matching.map(record => `<button class="recording-card ${record.id === selectedId ? 'selected' : ''}" data-id="${escape(record.id)}" aria-pressed="${record.id === selectedId}"><span class="recording-meta"><span>${escape(new Date(record.startedUtc).toLocaleDateString([], {month:'short',day:'numeric'}))}</span><span>${clock(record.durationSeconds)}</span></span><strong>${escape(title(record))}</strong><span class="recording-description">${record.transcript ? `${segments(record).length} transcript passages` : 'Ready for local transcription'}</span><span class="recording-footer"><span>${record.tracks.length === 2 ? 'Microphone + output' : trackName(record.tracks[0])}</span><span>${record.state === 'complete' ? 'Saved' : 'Interrupted'}</span></span></button>`).join('') : '<div class="empty-state"><h3>No matching recordings</h3><p>Record a conversation locally, or clear your filters.</p><button id="clear-local-search" class="button small secondary">Clear filters</button></div>';
  return matching.find(record => record.id === selectedId);
}

async function loadAudio(record, track) {
  audioRequest?.abort();
  if (record.state==='recording') { document.querySelector('#audio-status').textContent='Playback becomes available after stopping, to keep it out of the system-audio recording.'; return; }
  const request = new AbortController(); audioRequest = request;
  try {
    const key = `${record.id}/${track}/${selectedWindow}`;
    const ready = () => { document.querySelector('#audio-status').textContent=`Ready to play from ${clock(selectedWindow)}. Passage times follow the full recording clock.`; };
    if (audioUrl && audioKey === key) { document.querySelector('#local-audio').src=audioUrl; ready(); return; }
    const response = await api(`/api/local/sessions/${record.id}/audio/${track}?start=${selectedWindow}&seconds=60`, {signal:request.signal});
    const blob = await response.blob();
    if (request.signal.aborted) return;
    if (audioUrl) URL.revokeObjectURL(audioUrl);
    audioUrl = URL.createObjectURL(blob);
    audioKey = key;
    const audio = document.querySelector('#local-audio');
    if (audio && selectedId === record.id && selectedTrack === track) {
      audio.src = audioUrl;
      ready();
    }
  } catch (error) { if (error.name !== 'AbortError') { const note = document.querySelector('#audio-status'); if (note) note.textContent = error.message; } }
}

function renderReader(record) {
  audioRequest?.abort();
  reader.querySelectorAll('audio').forEach(audio=>audio.pause());
  displayedRecord=record;
  if (!record) { reader.innerHTML = '<div class="empty-state"><h2>No recording selected</h2><p>Your matching recording will appear here.</p></div>'; return; }
  if (!record.tracks.includes(selectedTrack)) selectedTrack = record.tracks[0];
  const track = record.transcript?.tracks.find(item => item.track === selectedTrack);
  const parts = track?.segments || [];
  if (downloadUrl) URL.revokeObjectURL(downloadUrl);
  downloadUrl = URL.createObjectURL(new Blob([transcriptText(record,selectedTrack)], {type:'text/plain;charset=utf-8'}));
  const pauses = record.events.filter(event => event.kind === 'paused').map(event => `Paused at ${clock(event.seconds)}`);
  const flagged = record.diagnosticFlags.filter(item => item.flags & 1).length;
  reader.innerHTML = `<button class="back-to-list subtle-button" id="back-local">← Conversations</button><div class="transcript-top"><span>${record.state === 'complete' ? 'Audio saved' : 'Interrupted recording'}</span><span>Local only</span></div><h2 id="transcript-title" tabindex="-1">${escape(title(record))}</h2><p class="transcript-meta">${escape(new Date(record.startedUtc).toLocaleString())} <span>·</span> ${clock(record.durationSeconds)}</p><div class="local-player"><label for="audio-track">Audio source</label><select id="audio-track">${record.tracks.map(id=>`<option value="${escape(id)}" ${id===selectedTrack?'selected':''}>${trackName(id)}</option>`).join('')}</select><audio id="local-audio" controls preload="metadata" aria-label="Saved recording playback"></audio><p id="audio-status" role="status">Loading audio locally…</p></div><p class="recording-diagnostics">${pauses.length ? escape(pauses.join('. ')) + '. ' : ''}${flagged ? `${flagged} device flags retained for review. ` : ''}Timestamps use the saved recording clock.</p><div class="transcript-actions"><button id="transcribe-local" class="button small">${record.transcript ? 'Transcribe again' : 'Transcribe locally'}</button>${parts.length ? `<button id="copy-local" class="button small secondary">Copy transcript</button><a class="subtle-button" href="${downloadUrl}" download="saidnest-${record.id}-${selectedTrack}.txt">Download .txt</a>` : ''}</div><p class="local-model-note">${escape(track?.notice || 'Run the local experimental English model to create a transcript. Audio is never sent to a cloud service.')}</p><div class="transcript-segments">${parts.map((part,index)=>`<article class="segment"><button class="timestamp seek-passage" data-seek="${part.start}" aria-label="Play at ${clock(part.start)}">${clock(part.start)}</button><div><div class="segment-heading"><span class="speaker">${trackName(selectedTrack)}${record.live && record.live.state!=="complete" && part.start >= (record.live.tracks.find(t=>t.track===selectedTrack)?.finalizedSeconds ?? Infinity) ? " · Draft" : ""}</span><button class="copy-passage" data-copy="${index}" aria-label="Copy passage at ${clock(part.start)}">Copy</button></div><p>${highlighted(part.text)}</p></div></article>`).join('') || '<p class="local-empty-transcript">No transcript passages for this source yet. The saved audio is available above.</p>'}</div><p class="transcript-end">${record.transcript ? 'Transcript revision saved encrypted. Original audio is unchanged.' : 'Capture is complete. Transcription is a separate local operation.'}</p>`;
  loadAudio(record,selectedTrack);
  if(record.captureConditions?.speakerBleedReported) { const condition=document.createElement('p');condition.className='recording-diagnostics';condition.textContent=record.captureConditions.notice;document.querySelector('.local-player').before(condition); }
  const playbackWindow=document.createElement('select'); playbackWindow.id='audio-window'; playbackWindow.setAttribute('aria-label','Playback minute');
  for(let second=0;second<Math.max(1,record.durationSeconds);second+=60) { const option=document.createElement('option'); option.value=second; option.textContent=`From ${clock(second)}`; option.selected=second===selectedWindow; playbackWindow.append(option); }
  document.querySelector('.local-player').insertBefore(playbackWindow,document.querySelector('#local-audio'));
  if(record.state==='recording') {
    document.querySelector('.transcript-top > span').textContent='Recording now';
    const stop=document.createElement('button'); stop.id='stop-local-recording'; stop.className='button small'; stop.textContent='Stop recording'; document.querySelector('.transcript-actions').prepend(stop);
    document.querySelector('#transcribe-local').hidden=true;
    document.querySelectorAll('[data-seek]').forEach(button=>button.disabled=true);
  }
  if(record.live) {
    const liveNote=document.createElement('p'); liveNote.id='live-progress'; liveNote.className='local-live-progress'; liveNote.setAttribute('role','status');
    liveNote.textContent=`Live transcription: ${record.live.state}. `+record.live.tracks.map(t=>`${trackName(t.track)}: ${clock(t.finalizedSeconds)} finalized, ${t.finalPassages} passages, ${t.peakDbfs > -45 ? 'audio detected' : 'quiet in latest window'}`).join(' · ')+(liveActive(record)?'. Current draft may change.':'.');
    document.querySelector('.transcript-actions').before(liveNote);
    document.querySelector('.local-model-note').textContent=record.live.notice;
    if(liveActive(record)) document.querySelector('.transcript-end').textContent='Recording and transcription are separate. Live text updates automatically; the current window is provisional.';
  }
  if (record.state !== 'complete') {
    document.querySelector('#transcribe-local').disabled=true;
    if(!record.live) document.querySelector('.local-model-note').textContent=record.state !== 'complete' ? 'Transcription requires a finalized recording. Retained audio can still be reviewed.' : 'Full-recording retranscription currently supports up to ten minutes. Live transcripts and minute-by-minute playback are retained.';
  }
  document.querySelector('#transcribe-local').hidden=true;
  quality.attach(record,reader);
}

// A detached or second player must not keep sounding during source/reference review.
document.addEventListener('play',event=>{if(event.target instanceof HTMLMediaElement)document.querySelectorAll('audio').forEach(audio=>{if(audio!==event.target)audio.pause();});},true);

function render() { status.hidden = true; renderReader(renderList()); }
list.addEventListener('click', event => {
  const selected = event.target.closest('[data-id]');
  if (selected) {
    if(selectedId!==selected.dataset.id) selectedWindow=0;
    selectedId = selected.dataset.id; render(); document.body.classList.add('detail-open');
    if (matchMedia('(max-width:700px)').matches) document.querySelector('#transcript-title').focus();
    else list.querySelector(`[data-id="${selectedId}"]`)?.focus();
  }
  if (event.target.closest('#clear-local-search')) { search.value=''; filter.value='all'; render(); search.focus(); }
});
reader.addEventListener('change',event=>{ if(['audio-track','audio-window'].includes(event.target.id)) { if(event.target.id==='audio-track') selectedTrack=event.target.value; else selectedWindow=Number(event.target.value); renderReader(records.find(record=>record.id===selectedId)); } });
reader.addEventListener('click',async event=>{
  if (event.target.closest('#back-local')) { document.body.classList.remove('detail-open'); list.querySelector(`[data-id="${selectedId}"]`)?.focus(); return; }
  const record = displayedRecord; if (!record) return;
  const stop=event.target.closest('#stop-local-recording');
  if(stop) { stop.disabled=true; stop.textContent='Stopping recording…'; try { await api(`/api/local/sessions/${record.id}/stop`,{method:'POST'}); notify('Stop requested. Audio will finish saving before transcription drains.'); } catch(error) { stop.disabled=false; stop.textContent='Stop recording'; notify(error.message); } return; }
  const seek = event.target.closest('[data-seek]');
  if(seek) {
    const position=Number(seek.dataset.seek), minute=Math.floor(position/60)*60;
    if(minute!==selectedWindow) { selectedWindow=minute; document.querySelector('#audio-window').value=minute; await loadAudio(record,selectedTrack); }
    const audio=document.querySelector('#local-audio');
    if(audio?.src) { const play=async()=>{ audio.currentTime=position-selectedWindow; try { await audio.play(); } catch { notify('Use the audio player to start playback.'); } }; if(audio.readyState) await play(); else audio.addEventListener('loadedmetadata',play,{once:true}); }
    else notify('Audio is still loading locally.');
  }
  if (event.target.closest('#copy-local') || event.target.closest('[data-copy]')) {
    const copy = event.target.closest('[data-copy]');
    const part = copy ? record.transcript.tracks.find(item=>item.track===selectedTrack).segments[Number(copy.dataset.copy)] : null;
    const text = part ? `${title(record)}\n[${clock(part.start)}] ${trackName(part.track)}: ${part.text}` : transcriptText(record,selectedTrack);
    try { await navigator.clipboard.writeText(text); notify(part ? 'Passage copied with its recording and timestamp.' : 'Local transcript copied with timestamps.'); }
    catch { notify('Clipboard unavailable. Select the text or download the transcript.'); }
  }
  const transcribe = event.target.closest('#transcribe-local');
  if (transcribe) {
    transcribe.disabled=true; transcribe.textContent='Transcribing locally…';
    try {
      const response=await api(`/api/local/sessions/${record.id}/transcribe`,{method:'POST'});
      const updated=await response.json(); records=records.map(item=>item.id===record.id?updated:item); render(); notify('Local transcript revision saved.');
    } catch(error) { transcribe.disabled=false; transcribe.textContent='Transcribe locally'; notify(error.message); }
  }
});
search.addEventListener('input',render); filter.addEventListener('change',render);

async function start(silent=false) {
  if(refreshing) return; refreshing=true; clearTimeout(pollTimer);
  if(!silent) { list.innerHTML='<p class="empty-state">Opening local archive…</p>'; document.querySelector('#result-count').textContent=''; }
  if (!token) { list.innerHTML='<p class="empty-state">Local archive locked. Reopen it from the launcher.</p>'; refreshing=false; return; }
  try {
    const saved = silent ? await Promise.all(records.map(async record=>record.refinement?.state==='running' && !liveActive(record) ? {...record,...await (await api(`/api/local/sessions/${record.id}/progress`)).json()} : active(record) ? await (await api(`/api/local/sessions/${record.id}`)).json() : record)) : await (await api('/api/local/sessions')).json();
    if (saved.some(record=>record.state==='corrupt')) notice.textContent='A recording failed verification and was not opened. Its original files are retained for recovery.';
    else if (saved.some(record=>record.state==='recording')) notice.textContent=(fixture?'Synthetic recording fixture. ':'')+'Recording now. Live local text updates automatically. Use Stop recording to finish before the fixed duration.';
    else if (saved.length===100) notice.textContent='Showing the 100 most recently updated recordings. A larger archive browser is planned.';
    else notice.textContent=archiveNotice;
    const previous=records.find(record=>record.id===selectedId);
    records=saved.filter(record=>record.state!=='corrupt'); records.sort((a,b)=>new Date(b.startedUtc)-new Date(a.startedUtc));
    const selected=renderList();
    const holdingControl=reader.contains(document.activeElement) || Boolean(getSelection()?.toString());
    if(!silent || !holdingControl || selected?.state!==previous?.state) renderReader(selected);
    list.querySelectorAll('[data-id]').forEach(button=>{ const record=records.find(item=>item.id===button.dataset.id); if(record.state==='recording') button.querySelector('.recording-footer > span:last-child').textContent='Recording'; });
    if(records.some(active)) pollTimer=setTimeout(()=>start(true),2000);
  }
  catch(error) { notice.textContent=error.message; list.innerHTML='<p class="empty-state">The local archive could not be opened.</p>'; }
  finally { refreshing=false; }
}
start();
