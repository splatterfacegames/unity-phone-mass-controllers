// Buzzer Party phone controller. Plain ES module; the SDK is served by the Unity host.
//
// Game messages (JSON `d`):
//   host -> phone  {type:"state", phase, round, target, winner, match_winner, players:[{id,name,color,emoji,score,connected,admin,rtt}]}
//                  {type:"secret", round, for, symbol:{id,label,color,shape}}   (private, only to `for`)
//                  {type:"flash", round, symbol, seq}                          (what the big screen shows)
//                  {type:"buzz", result:"win"|"wrong"|"locked"|"idle", locked_ms} (private)
//   phone -> host  {type:"buzz", at}     (at = pmc.timestamp(): host-clock tap time, RTT-bounded)
//                  {type:"admin", action:"start"|"next"|"reset"|"kick", id?}
//   binary         any bytes are echoed back to the sender
import { connect, feedback, keepScreenOn } from '/pmc/pmc.js';

const COLORS = ['#ff5a5f', '#ff8c42', '#ffc53d', '#2ec27e', '#26c6da', '#3d8bfd', '#a371f7', '#ff6fb5'];
const EMOJIS = ['🦊', '🐸', '🐙', '🦉', '🐼', '🦄', '🐝', '🐢', '🌵', '🍉', '🚀', '👾', '🎸', '🍩', '⚡', '🌈'];
const PROFILE_KEY = 'buzzer.profile';

const $ = (id) => document.getElementById(id);
const show = (screen) => { for (const s of ['join', 'play', 'ended']) $(s).hidden = s !== screen; };

let saved = load();
let pmc = null;
let state = { phase: 'lobby', players: [] };
let lockTimer = 0;
let shownRound = -1;

// --- join screen --------------------------------------------------------------

let pick = { color: saved?.color ?? COLORS[Math.floor(Math.random() * COLORS.length)], emoji: saved?.emoji ?? EMOJIS[Math.floor(Math.random() * EMOJIS.length)] };

for (const c of COLORS) {
  const b = el('button', { type: 'button', className: 'swatch', ariaLabel: c });
  b.style.setProperty('--c', c);
  b.dataset.color = c;
  b.onclick = () => { pick.color = c; paintPicker(); };
  $('colors').append(b);
}
for (const e of EMOJIS) {
  const b = el('button', { type: 'button', className: 'emoji', textContent: e });
  b.dataset.emoji = e;
  b.onclick = () => { pick.emoji = e; paintPicker(); };
  $('emojis').append(b);
}

function paintPicker() {
  for (const b of $('colors').children) b.classList.toggle('on', b.dataset.color === pick.color);
  for (const b of $('emojis').children) b.classList.toggle('on', b.dataset.emoji === pick.emoji);
  paintAvatar($('preview-avatar'), pick);
  document.documentElement.style.setProperty('--me', pick.color);
}

$('join-form').onsubmit = (ev) => {
  ev.preventDefault();
  const name = $('name').value.trim().slice(0, 16);
  if (!name) return;
  saved = { name, color: pick.color, emoji: pick.emoji };
  try { localStorage.setItem(PROFILE_KEY, JSON.stringify(saved)); } catch {}
  if (pmc && pmc.status !== 'closed') pmc.setProfile({ name, profile: { color: saved.color, emoji: saved.emoji } });
  else start();
  paintMe();
  show('play');
  keepScreenOn(); // wake lock where allowed; NoSleep-style video fallback elsewhere
};

$('edit-btn').onclick = () => {
  $('name').value = saved?.name ?? '';
  $('join-btn').textContent = 'Save';
  paintPicker();
  show('join');
};

// --- connection -----------------------------------------------------------------

function start() {
  pmc = connect({ name: saved.name, profile: { color: saved.color, emoji: saved.emoji } });
  window.pmc = pmc; // handy for debugging and tests

  pmc.on('status', (s) => {
    $('banner').hidden = s !== 'reconnecting';
    $('me-status').dataset.s = s;
    $('me-status-text').textContent = s === 'open' ? 'connected' : s;
    renderBuzzer();
  });
  pmc.on('welcome', (w) => {
    if (!w.rejoined) $('secret').hidden = true; // a fresh identity (e.g. the host restarted)
    $('pin-form').hidden = w.admin;
    $('admin-tools').hidden = !w.admin;
    paintMe();
  });
  pmc.on('message', onMessage);
  pmc.on('moved', () => { $('moved').hidden = false; }); // join URL changed: ask for a re-scan
  pmc.on('reject', ({ code, reason }) => {
    const badCode = code === 'bad_code';
    end(badCode ? 'Room code needed' : 'Could not join', badCode ? 'Enter the code shown on the big screen.' : reason || code, badCode);
  });
  pmc.on('kicked', (reason) => end('Removed by the host', reason || 'You were removed from the game.'));
  pmc.on('replaced', () => end('Opened somewhere else', 'This player is now controlled from another tab or device.'));
}

setInterval(() => {
  $('latency').textContent = pmc?.rttMs ? `${pmc.rttMs} ms` : '– ms';
}, 1000);

function end(title, text, askCode = false) {
  $('ended-title').textContent = title;
  $('ended-text').textContent = text;
  $('code-form').hidden = !askCode;
  $('ended-btn').hidden = askCode;
  show('ended');
}

$('ended-btn').onclick = () => { show('play'); pmc.reconnect(); };
$('code-form').onsubmit = (ev) => {
  ev.preventDefault();
  pmc.code = $('code').value.trim().toUpperCase();
  show('play');
  pmc.reconnect();
};
$('leave-btn').onclick = () => {
  pmc.leave();
  end('You left', 'Thanks for playing!');
};

// --- game -------------------------------------------------------------------------

function onMessage(d) {
  switch (d?.type) {
    case 'state':
      state = d;
      if (d.phase !== 'round') clearSecretIfOld();
      renderState();
      break;
    case 'secret':
      if (d.for !== pmc.id) return; // never expected; the host sends secrets privately
      $('secret').hidden = false;
      $('secret-shape').innerHTML = shapeSvg(d.symbol.shape, d.symbol.color);
      $('secret-label').textContent = d.symbol.label;
      $('secret-label').style.color = d.symbol.color;
      feedback('buzz');
      break;
    case 'buzz':
      onBuzzResult(d);
      break;
  }
}

function clearSecretIfOld() {
  if (state.phase === 'lobby' || state.phase === 'over') $('secret').hidden = true;
}

function onBuzzResult({ result, locked_ms }) {
  const fb = $('feedback');
  fb.className = 'feedback ' + result;
  if (result === 'win') { fb.textContent = 'Got it! +1'; $('buzzer').classList.add('won'); feedback('success'); }
  else if (result === 'wrong' || result === 'locked') {
    fb.textContent = 'Not your symbol! Wait…';
    feedback('error');
    clearTimeout(lockTimer);
    $('buzzer').classList.add('locked');
    lockTimer = setTimeout(() => { $('buzzer').classList.remove('locked'); fb.textContent = ' '; }, locked_ms);
  } else fb.textContent = ' ';
}

function renderState() {
  const me = state.players.find((p) => p.id === pmc.id);
  $('me-score').textContent = me?.score ?? 0;
  const winner = state.players.find((p) => p.id === state.winner);
  const champ = state.players.find((p) => p.id === state.match_winner);
  const phaseText = {
    lobby: `Waiting for the host to start… first to ${state.target ?? 5} wins`,
    round: `Round ${state.round}: watch the big screen and buzz on your symbol!`,
    reveal: winner ? (winner.id === pmc.id ? 'You won the round!' : `${winner.name} won the round`) : 'Nobody got it this time',
    over: champ ? (champ.id === pmc.id ? 'You won the match!' : `${champ.name} wins the match!`) : 'Match over',
  }[state.phase] ?? '';
  $('phase').textContent = phaseText;
  $('phase').dataset.phase = state.phase;
  if (state.round !== shownRound || state.phase === 'lobby') {
    shownRound = state.round;
    $('feedback').textContent = ' ';
    $('buzzer').classList.remove('won');
  }

  const list = $('scores');
  list.replaceChildren(...[...state.players].sort((a, b) => b.score - a.score || a.id - b.id).map((p) => {
    const li = el('li', { className: (p.connected ? '' : 'away ') + (p.id === pmc.id ? 'mine' : '') });
    const av = el('span', { className: 'avatar small' });
    paintAvatar(av, p);
    li.append(av, el('span', { className: 'n', textContent: p.name || `Player ${p.id}` }), el('b', { textContent: p.score }));
    return li;
  }));

  const adm = $('adm-players');
  adm.replaceChildren(...state.players.filter((p) => p.id !== pmc.id).map((p) => {
    const li = el('li');
    const kick = el('button', { type: 'button', className: 'danger', textContent: 'Kick' });
    kick.dataset.kick = p.id;
    kick.onclick = () => pmc.send({ type: 'admin', action: 'kick', id: p.id });
    li.append(el('span', { textContent: `${p.emoji ?? ''} ${p.name}` }), kick);
    return li;
  }));
  renderBuzzer();
}

function renderBuzzer() {
  $('buzzer').disabled = !(pmc?.status === 'open' && state.phase === 'round');
}

$('buzzer').addEventListener('pointerdown', (ev) => {
  ev.preventDefault();
  if ($('buzzer').disabled) return;
  // Stamp the tap on the host clock: the host credits it bounded by our RTT, so a
  // remote player isn't beaten by a local one just because the tunnel is slower.
  pmc.send({ type: 'buzz', at: pmc.timestamp() });
  feedback('buzz');
});

$('pin-form').onsubmit = async (ev) => {
  ev.preventDefault();
  const ok = await pmc.auth($('pin').value.trim());
  $('pin').value = '';
  $('pin-form').hidden = ok;
  $('admin-tools').hidden = !ok;
  if (!ok) { $('pin').placeholder = 'Wrong PIN'; $('pin-form').classList.add('shake'); setTimeout(() => $('pin-form').classList.remove('shake'), 400); }
};
for (const action of ['start', 'next', 'reset']) $('adm-' + action).onclick = () => pmc.send({ type: 'admin', action });

// --- helpers --------------------------------------------------------------------

function paintMe() {
  const p = saved ?? {};
  paintAvatar($('me-avatar'), p);
  $('me-name').textContent = p.name ?? '';
  document.documentElement.style.setProperty('--me', p.color ?? COLORS[0]);
}

function paintAvatar(node, p) {
  node.textContent = p.emoji ?? '';
  node.style.setProperty('--c', p.color ?? '#555');
}

function el(tag, props = {}) {
  return Object.assign(document.createElement(tag), props);
}

function load() {
  try { return JSON.parse(localStorage.getItem(PROFILE_KEY)); } catch { return null; }
}

function shapeSvg(shape, color) {
  const f = `fill="${color}"`;
  const body = {
    circle: `<circle cx="50" cy="50" r="42" ${f}/>`,
    square: `<rect x="12" y="12" width="76" height="76" rx="10" ${f}/>`,
    triangle: `<path d="M50 8 L94 88 H6 Z" ${f} stroke="${color}" stroke-width="6" stroke-linejoin="round"/>`,
    star: `<path d="M50 5 L61.8 36 L95 38 L69 59 L78 92 L50 73 L22 92 L31 59 L5 38 L38.2 36 Z" ${f} stroke="${color}" stroke-width="4" stroke-linejoin="round"/>`,
    diamond: `<path d="M50 4 L92 50 L50 96 L8 50 Z" ${f}/>`,
    hexagon: `<path d="M27 10 H73 L96 50 L73 90 H27 L4 50 Z" ${f}/>`,
    heart: `<path d="M50 90 C20 68 5 52 5 33 C5 18 17 8 30 8 C39 8 46 13 50 21 C54 13 61 8 70 8 C83 8 95 18 95 33 C95 52 80 68 50 90 Z" ${f}/>`,
    ring: `<circle cx="50" cy="50" r="34" fill="none" stroke="${color}" stroke-width="16"/>`,
  }[shape] ?? '';
  return `<svg viewBox="0 0 100 100" aria-hidden="true">${body}</svg>`;
}

// --- boot -----------------------------------------------------------------------

paintPicker();
if (saved?.name) {
  // Returning player (or a reload): skip the form and rejoin straight away.
  paintMe();
  show('play');
  start();
} else {
  show('join');
  $('name').focus();
}
