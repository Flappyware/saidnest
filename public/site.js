import { recordings } from './samples.js';

const choices = document.querySelector('#sample-choices');
const buttons = recordings.map(record => {
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = record.title;
  button.setAttribute('aria-pressed', String(record.id === recordings[0].id));
  button.addEventListener('click', () => {
    const [time, speaker, text] = record.segments[1];
    document.querySelector('#sample-kind').textContent = record.source;
    document.querySelector('#sample-title').textContent = record.title;
    document.querySelector('#sample-time').textContent = time;
    document.querySelector('#sample-speaker').textContent = speaker;
    document.querySelector('#sample-quote').textContent = text;
    document.querySelector('#sample-open').href = `/app/?sample=${record.id}`;
    buttons.forEach(item => item.setAttribute('aria-pressed', String(item === button)));
  });
  return button;
});
choices.replaceChildren(...buttons);
document.querySelector('.demo-passage').setAttribute('aria-live', 'polite');
