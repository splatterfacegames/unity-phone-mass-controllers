// Simulated lobby: phones join, one buzzes, one drops and rejoins, repeat.
// The CSS phone next to the TV buzzes in sync with the roster.
(function () {
  var roster = document.getElementById('sim-roster');
  var status = document.getElementById('sim-status');
  var phoneName = document.getElementById('sim-phone-name');
  var buzzBtn = document.getElementById('sim-buzz');
  var phoneStat = document.getElementById('sim-phone-stat');
  if (!roster || !status) return;

  var NAMES = [
    ['Ada', '#ff6b6b'], ['Bo', '#ffa94d'], ['Cy', '#ffd43b'], ['Dee', '#69db7c'],
    ['Eli', '#4dd4fa'], ['Fay', '#b197fc'], ['Gus', '#f783ac'], ['Han', '#63e6be']
  ];
  var players = [];
  var timers = [];
  var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function later(fn, ms) { timers.push(setTimeout(fn, ms)); }

  function chip(p) {
    var el = document.createElement('span');
    el.className = 'sim-player';
    var av = document.createElement('span');
    av.className = 'avatar';
    av.style.background = p.color;
    av.textContent = p.name[0];
    var nm = document.createElement('span');
    nm.textContent = p.name;
    var dot = document.createElement('i');
    dot.className = 'dot';
    el.appendChild(av); el.appendChild(nm); el.appendChild(dot);
    return el;
  }

  function label() {
    var on = players.filter(function (p) { return p.online; }).length;
    status.textContent = on === 0 ? 'Waiting for players…'
      : on + (on === 1 ? ' player in' : ' players in') + ' — phones are the controllers';
  }

  function phoneIdle() {
    if (phoneName) phoneName.textContent = '—';
    if (phoneStat) phoneStat.textContent = 'connecting…';
  }

  function join(p) {
    p.online = true;
    p.el = chip(p);
    roster.appendChild(p.el);
    label();
    // The first join "owns" the phone on screen.
    if (phoneStat && players.indexOf(p) === 0) {
      phoneName.textContent = p.name;
      phoneName.style.color = p.color;
      phoneStat.textContent = 'connected · ' + (18 + Math.floor(Math.random() * 30)) + ' ms';
    }
  }

  function buzz(p) {
    p.el.classList.add('buzz');
    setTimeout(function () { p.el.classList.remove('buzz'); }, 700);
    if (buzzBtn && phoneName) {
      phoneName.textContent = p.name;
      phoneName.style.color = p.color;
      buzzBtn.classList.add('hit');
      if (phoneStat) phoneStat.textContent = 'pmc.send({ type: "buzz" })';
      setTimeout(function () {
        buzzBtn.classList.remove('hit');
        if (phoneStat) phoneStat.textContent = 'connected · ' + (18 + Math.floor(Math.random() * 30)) + ' ms';
      }, 420);
    }
  }

  function cycle() {
    timers.forEach(clearTimeout);
    timers = [];
    roster.textContent = '';
    players = NAMES.map(function (n, i) {
      return { name: n[0], color: n[1], online: false, el: null, idx: i };
    });
    label();
    phoneIdle();

    players.forEach(function (p, i) {
      later(function () { join(p); }, 500 + i * 700);
    });

    var t = 500 + players.length * 700 + 900;

    // a few buzzes
    for (var b = 0; b < 3; b++) {
      later(function () {
        var on = players.filter(function (p) { return p.online && p.el; });
        if (!on.length) return;
        buzz(on[Math.floor(Math.random() * on.length)]);
      }, t + b * 1600);
    }
    t += 3 * 1600 + 400;

    // one phone drops, then rejoins inside the grace period
    later(function () {
      var p = players[2];
      if (!p || !p.el) return;
      p.online = false;
      p.el.classList.add('offline');
      label();
      if (phoneStat && phoneName && phoneName.textContent === p.name) {
        phoneStat.textContent = 'reconnecting…';
      }
    }, t);
    later(function () {
      var p = players[2];
      if (!p || !p.el) return;
      p.online = true;
      p.el.classList.remove('offline');
      label();
      if (phoneStat && phoneName && phoneName.textContent === p.name) {
        phoneStat.textContent = 'rejoined — seat kept';
      }
    }, t + 2400);

    if (!reduced) later(cycle, t + 2400 + 4200);
  }

  cycle();
})();
