// Auto Chapters review player: a video with the programme's break points on its scrub bar, which can be selected,
// dragged, nudged a frame at a time, added and removed. Loaded once by the review tab (eval of this file) and driven
// through window.schedularrChapterReview. The page keeps the queue and the approve buttons; this keeps the markers
// and tells the page when they change.
(() => {
  if (window.schedularrChapterReview && window.schedularrChapterReview.version === 1) return;

  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
  const round3 = v => Math.round(v * 1000) / 1000;

  function clock(t, precise) {
    if (!isFinite(t) || t < 0) t = 0;
    const h = Math.floor(t / 3600), m = Math.floor((t % 3600) / 60), s = t % 60;
    const sec = precise ? s.toFixed(3).padStart(6, '0') : String(Math.floor(s)).padStart(2, '0');
    return (h > 0 ? h + ':' + String(m).padStart(2, '0') : String(m)) + ':' + sec;
  }

  function el(tag, cls, html) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (html !== undefined) e.innerHTML = html;
    return e;
  }

  function button(label, title, onClick, cls) {
    const b = el('button', 'btn btn-sm' + (cls ? ' ' + cls : ''));
    b.type = 'button';
    b.innerHTML = label;
    if (title) { b.title = title; b.setAttribute('aria-label', title); }
    b.addEventListener('click', e => { e.preventDefault(); onClick(); });
    // Keeps focus off the button, so Space and the arrows keep driving the player after a click.
    b.addEventListener('mousedown', e => e.preventDefault());
    return b;
  }

  class Player {
    constructor(root, dotnet) {
      this.root = root;
      this.dotnet = dotnet;
      this.markers = [];
      this.detected = [];
      this.selected = -1;
      this.pos = 0;
      this.duration = 0;
      this.frame = 1 / 25;
      this.mode = 'direct';
      this.streamStart = 0;
      this.streaming = false;
      this.stillTimer = 0;
      this.stillToken = 0;
      this.notifyTimer = 0;
      this.drag = null;
      this.build();
      this.onKey = e => this.key(e);
      document.addEventListener('keydown', this.onKey);
      this.raf = requestAnimationFrame(() => this.tick());
    }

    build() {
      const r = this.root;
      r.innerHTML = '';
      r.classList.add('acr');

      this.stage = el('div', 'acr-stage');
      this.video = el('video');
      this.video.playsInline = true;
      this.video.preload = 'auto';
      this.still = el('img', 'acr-still');
      this.still.alt = '';
      this.still.hidden = true;
      this.badge = el('div', 'acr-badge');
      this.badge.hidden = true;
      this.spinner = el('div', 'acr-spinner');
      this.spinner.hidden = true;
      this.stage.append(this.video, this.still, this.badge, this.spinner);
      this.stage.addEventListener('click', () => this.toggle());

      this.timeline = el('div', 'acr-timeline');
      this.timeline.tabIndex = 0;
      this.timeline.setAttribute('role', 'slider');
      this.timeline.setAttribute('aria-label', 'Position');
      this.track = el('div', 'acr-track');
      this.played = el('div', 'acr-played');
      this.track.append(this.played);
      this.markerLayer = el('div', 'acr-marker-layer');
      this.playhead = el('div', 'acr-playhead');
      this.hover = el('div', 'acr-hover');
      this.hover.hidden = true;
      this.timeline.append(this.track, this.markerLayer, this.playhead, this.hover);
      this.timeline.addEventListener('pointerdown', e => this.pointerDown(e));
      this.timeline.addEventListener('pointermove', e => this.pointerMove(e));
      this.timeline.addEventListener('pointerup', e => this.pointerUp(e));
      this.timeline.addEventListener('pointercancel', e => this.pointerUp(e));
      this.timeline.addEventListener('pointerleave', () => { if (!this.drag) this.hover.hidden = true; });

      const controls = el('div', 'acr-controls');
      this.playButton = button('▶', 'Play / pause (Space)', () => this.toggle(), 'btn-primary acr-play');
      controls.append(
        button('⏮', 'Previous marker ([)', () => this.jumpMarker(-1)),
        button('−1s', 'Back one second (Shift+←)', () => this.step(-1)),
        button('◀|', 'Back one frame (←)', () => this.step(-this.frame)),
        this.playButton,
        button('|▶', 'Forward one frame (→)', () => this.step(this.frame)),
        button('+1s', 'Forward one second (Shift+→)', () => this.step(1)),
        button('⏭', 'Next marker (])', () => this.jumpMarker(1)));
      this.time = el('span', 'acr-time');
      controls.append(this.time, el('div', 'spacer'));
      controls.append(
        button('+ Marker here', 'Add a marker at the playhead (M)', () => this.add(), 'acr-add'),
        this.removeButton = button('Remove marker', 'Remove the selected marker (Delete)', () => this.remove(), 'btn-danger'),
        this.resetButton = button('Reset', 'Put back the markers analysis found', () => this.reset(), 'btn-ghost'));

      this.selectedBar = el('div', 'acr-selected');

      this.list = el('div', 'acr-list');

      const help = el('p', 'hint acr-help',
        '<kbd>Space</kbd> play/pause · <kbd>←</kbd>/<kbd>→</kbd> one frame · <kbd>Shift</kbd>+arrows one second · ' +
        '<kbd>[</kbd>/<kbd>]</kbd> previous/next marker · <kbd>M</kbd> add a marker · <kbd>Delete</kbd> remove it · ' +
        '<kbd>Alt</kbd>+arrows (or <kbd>,</kbd>/<kbd>.</kbd>) nudge the selected marker a frame (with <kbd>Shift</kbd>, a second). Drag markers on the bar to move them.');

      r.append(this.stage, this.timeline, controls, this.selectedBar, this.list, help);

      const v = this.video;
      v.addEventListener('play', () => this.render());
      v.addEventListener('pause', () => this.render());
      v.addEventListener('playing', () => { this.spinner.hidden = true; if (v.videoWidth) this.still.hidden = true; });
      v.addEventListener('waiting', () => { this.spinner.hidden = false; });
      v.addEventListener('seeked', () => { this.spinner.hidden = true; });
      v.addEventListener('error', () => {
        if (!v.getAttribute('src')) return;
        if (this.mode === 'direct') this.fallback(); else { this.spinner.hidden = true; this.streaming = false; this.render(); }
      });
      v.addEventListener('loadedmetadata', () => {
        if (this.mode === 'direct') {
          if (!v.videoWidth) { this.fallback(); return; }
          if (!this.duration && isFinite(v.duration)) this.duration = v.duration;
          this.render();
        }
      });
    }

    load(opts) {
      this.release();
      this.urls = opts.urls;
      this.duration = opts.duration || 0;
      this.frame = opts.frame || 1 / 25;
      this.markers = (opts.markers || []).slice().sort((a, b) => a - b);
      this.detected = (opts.detected || opts.markers || []).slice().sort((a, b) => a - b);
      this.selected = -1;
      this.pos = 0;
      this.streaming = false;
      this.still.hidden = true;
      this.mode = opts.direct ? 'direct' : 'convert';
      this.badge.hidden = this.mode === 'direct';
      this.badge.textContent = 'Converted preview';
      if (this.mode === 'direct') {
        this.video.src = this.urls.video;
        this.video.load();
      } else {
        this.video.removeAttribute('src');
        this.video.load();
        this.showStill(0);
      }
      this.renderMarkers();
      this.render();
      this.root.focus({ preventScroll: true });
    }

    // The browser can't play the file as it is: play a converted preview instead, from where it is now.
    fallback() {
      if (this.mode !== 'direct' || !this.urls) return;
      this.mode = 'convert';
      this.badge.hidden = false;
      this.badge.textContent = 'Converted preview';
      this.video.removeAttribute('src');
      this.video.load();
      this.streaming = false;
      this.showStill(this.pos);
      this.render();
    }

    release() {
      clearTimeout(this.stillTimer);
      clearTimeout(this.restartTimer);
      this.restartTimer = 0;
      try { this.video.pause(); } catch { }
      this.video.removeAttribute('src');
      try { this.video.load(); } catch { }
      this.streaming = false;
    }

    dispose() {
      this.release();
      cancelAnimationFrame(this.raf);
      document.removeEventListener('keydown', this.onKey);
      clearTimeout(this.notifyTimer);
      this.root.innerHTML = '';
    }

    get playing() { return !this.video.paused && !this.video.ended; }

    current() {
      if (this.mode === 'direct') return this.video.currentTime || 0;
      return this.streaming ? this.streamStart + (this.video.currentTime || 0) : this.pos;
    }

    tick() {
      if (this.playing) {
        this.pos = this.current();
        this.renderTime();
      }
      this.raf = requestAnimationFrame(() => this.tick());
    }

    toggle() {
      if (this.playing || this.restartTimer) this.pause(); else this.play();
    }

    play() {
      if (this.mode === 'direct') {
        if (this.duration && this.pos >= this.duration - 0.05) this.seek(0);
        this.video.play().catch(() => { });
        return;
      }
      // Converted: start a preview from here (the still stays up until the first frame arrives).
      this.streamStart = this.pos;
      this.streaming = true;
      this.spinner.hidden = false;
      this.video.src = this.urls.stream + '?start=' + this.pos.toFixed(3);
      this.video.play().catch(() => { });
    }

    pause() {
      if (this.mode === 'direct') { this.video.pause(); return; }
      clearTimeout(this.restartTimer);
      this.restartTimer = 0;
      this.pos = this.current();
      this.video.pause();
      this.render();
    }

    seek(t) {
      const end = this.duration > 0 ? this.duration : Number.MAX_VALUE;
      t = clamp(t, 0, end);
      this.pos = t;
      if (this.mode === 'direct') {
        this.video.currentTime = t;
      } else if (this.playing || this.restartTimer) {
        // Restart the preview from the new point, once scrubbing settles.
        this.video.pause();
        this.streaming = false;
        clearTimeout(this.restartTimer);
        this.restartTimer = setTimeout(() => { this.restartTimer = 0; this.play(); }, 250);
        this.showStill(t);
      } else {
        this.streaming = false;
        this.showStill(t);
      }
      this.renderTime();
    }

    step(by) {
      if (this.playing) this.pause();
      this.seek(this.current() + by);
    }

    // A single exact frame from the server, for converted previews (latest request wins).
    showStill(t) {
      clearTimeout(this.stillTimer);
      const token = ++this.stillToken;
      this.stillTimer = setTimeout(() => {
        const src = this.urls.frame + '?t=' + t.toFixed(3) + '&h=540';
        const img = new Image();
        this.spinner.hidden = false;
        img.onload = () => {
          if (token !== this.stillToken) return;
          this.still.src = src;
          this.still.hidden = false;
          this.spinner.hidden = true;
        };
        img.onerror = () => { if (token === this.stillToken) this.spinner.hidden = true; };
        img.src = src;
      }, 60);
    }

    // Markers ----------------------------------------------------------------------------------------------------

    changed() {
      this.renderMarkers();
      this.render();
      clearTimeout(this.notifyTimer);
      this.notifyTimer = setTimeout(() => {
        this.dotnet.invokeMethodAsync('MarkersChanged', this.markers.slice()).catch(() => { });
      }, 150);
    }

    sortKeepingSelection() {
      const value = this.selected >= 0 ? this.markers[this.selected] : null;
      this.markers.sort((a, b) => a - b);
      if (value !== null) this.selected = this.markers.indexOf(value);
    }

    select(i, jump) {
      this.selected = i;
      if (jump && i >= 0) this.seek(this.markers[i]);
      this.renderMarkers();
      this.render();
    }

    add() {
      const t = round3(this.current());
      if (this.markers.some(m => Math.abs(m - t) < this.frame / 2)) return;
      this.markers.push(t);
      this.markers.sort((a, b) => a - b);
      this.selected = this.markers.indexOf(t);
      this.changed();
    }

    remove(i) {
      if (i === undefined) i = this.selected;
      if (i < 0 || i >= this.markers.length) return;
      this.markers.splice(i, 1);
      this.selected = -1;
      this.changed();
    }

    reset() {
      this.markers = this.detected.slice();
      this.selected = -1;
      this.changed();
    }

    nudge(by) {
      if (this.selected < 0) return;
      const end = this.duration > 0 ? this.duration : Number.MAX_VALUE;
      this.markers[this.selected] = round3(clamp(this.markers[this.selected] + by, 0, end));
      this.sortKeepingSelection();
      if (this.playing) this.pause();
      this.seek(this.markers[this.selected]);
      this.changed();
    }

    moveToPlayhead() {
      if (this.selected < 0) return;
      this.markers[this.selected] = round3(this.current());
      this.sortKeepingSelection();
      this.changed();
    }

    jumpMarker(dir) {
      const t = this.current();
      const eps = this.frame / 2;
      let i = -1;
      if (dir > 0) i = this.markers.findIndex(m => m > t + eps);
      else for (let k = this.markers.length - 1; k >= 0; k--) if (this.markers[k] < t - eps) { i = k; break; }
      if (i >= 0) { if (this.playing) this.pause(); this.select(i, true); }
    }

    // Scrub bar --------------------------------------------------------------------------------------------------

    timeAt(e) {
      const box = this.timeline.getBoundingClientRect();
      const span = this.duration > 0 ? this.duration : 1;
      return clamp((e.clientX - box.left) / box.width, 0, 1) * span;
    }

    pointerDown(e) {
      if (e.button !== 0 || !this.duration) return;
      const markerEl = e.target.closest('.acr-marker');
      this.timeline.setPointerCapture(e.pointerId);
      this.wasPlaying = this.playing;
      if (this.playing) this.pause();
      if (markerEl) {
        const i = Number(markerEl.dataset.i);
        this.select(i, true);
        this.drag = { kind: 'marker', startX: e.clientX, moved: false };
      } else {
        this.drag = { kind: 'scrub' };
        this.seek(this.timeAt(e));
      }
      e.preventDefault();
    }

    pointerMove(e) {
      if (!this.duration) return;
      const t = this.timeAt(e);
      this.hover.hidden = false;
      this.hover.style.left = (t / this.duration * 100) + '%';
      this.hover.textContent = clock(t, false);
      if (!this.drag) return;
      if (this.drag.kind === 'scrub') {
        this.seek(t);
      } else if (this.drag.kind === 'marker' && this.selected >= 0) {
        if (!this.drag.moved && Math.abs(e.clientX - this.drag.startX) < 3) return;
        this.drag.moved = true;
        // Onto a whole frame, so nudging from here lands on frames too.
        const snapped = round3(Math.round(t / this.frame) * this.frame);
        this.markers[this.selected] = snapped;
        this.sortKeepingSelection();
        this.seek(snapped);
        this.renderMarkers();
      }
    }

    pointerUp(e) {
      if (!this.drag) return;
      try { this.timeline.releasePointerCapture(e.pointerId); } catch { }
      const moved = this.drag.kind === 'marker' && this.drag.moved;
      this.drag = null;
      if (moved) this.changed();
      if (this.wasPlaying && !moved) this.play();
    }

    key(e) {
      if (!this.root.isConnected || this.root.offsetParent === null) return;
      const target = e.target;
      if (target && (target.closest('input, textarea, select, [contenteditable="true"]') || (target.tagName === 'BUTTON' && (e.key === ' ' || e.key === 'Enter')))) return;
      if (e.ctrlKey || e.metaKey) return;
      const big = e.shiftKey ? 1 : this.frame;
      let handled = true;
      switch (e.key) {
        case ' ': case 'k': case 'K': this.toggle(); break;
        case 'ArrowLeft': if (e.altKey) this.nudge(-big); else this.step(e.shiftKey ? -1 : -this.frame); break;
        case 'ArrowRight': if (e.altKey) this.nudge(big); else this.step(e.shiftKey ? 1 : this.frame); break;
        case ',': case '<': this.nudge(-big); break;
        case '.': case '>': this.nudge(big); break;
        case '[': this.jumpMarker(-1); break;
        case ']': this.jumpMarker(1); break;
        case 'm': case 'M': this.add(); break;
        case 'Delete': case 'Backspace': this.remove(); break;
        case 'Home': this.seek(0); break;
        case 'End': this.seek(Math.max(0, this.duration - this.frame)); break;
        case 'Escape': this.select(-1, false); break;
        default: handled = false;
      }
      if (handled) e.preventDefault();
    }

    // Drawing ----------------------------------------------------------------------------------------------------

    renderTime() {
      const t = this.current();
      const pct = this.duration > 0 ? clamp(t / this.duration, 0, 1) * 100 : 0;
      this.playhead.style.left = pct + '%';
      this.played.style.width = pct + '%';
      this.time.textContent = clock(t, true) + ' / ' + clock(this.duration, false);
      this.timeline.setAttribute('aria-valuetext', clock(t, false));
    }

    render() {
      this.playButton.innerHTML = this.playing ? '❚❚' : '▶';
      this.removeButton.disabled = this.selected < 0;
      const same = this.markers.length === this.detected.length && this.markers.every((m, i) => Math.abs(m - this.detected[i]) < 0.0005);
      this.resetButton.disabled = same;
      this.renderTime();
      this.renderSelected();
    }

    renderMarkers() {
      this.markerLayer.innerHTML = '';
      this.markers.forEach((m, i) => {
        const mk = el('div', 'acr-marker' + (i === this.selected ? ' selected' : ''));
        mk.dataset.i = i;
        mk.style.left = (this.duration > 0 ? m / this.duration * 100 : 0) + '%';
        mk.title = 'Marker ' + (i + 1) + ' at ' + clock(m, true) + ' (drag to move)';
        mk.append(el('span', 'acr-marker-flag', String(i + 1)));
        this.markerLayer.append(mk);
      });

      this.list.innerHTML = '';
      if (this.markers.length === 0) {
        this.list.append(el('p', 'hint acr-none', 'No markers. Play or scrub to where a break belongs and press <strong>+ Marker here</strong> (or <kbd>M</kbd>).'));
        return;
      }
      this.markers.forEach((m, i) => {
        const card = el('div', 'acr-chip' + (i === this.selected ? ' selected' : ''));
        const img = el('img', 'acr-thumb');
        img.loading = 'lazy';
        img.alt = '';
        img.src = this.urls ? this.urls.frame + '?t=' + m.toFixed(3) + '&h=144' : '';
        const label = el('div', 'acr-chip-label', '<strong>' + (i + 1) + '</strong> <span>' + clock(m, true) + '</span>');
        const remove = el('button', 'acr-chip-remove', '×');
        remove.type = 'button';
        remove.title = 'Remove this marker';
        remove.setAttribute('aria-label', 'Remove marker ' + (i + 1));
        remove.addEventListener('click', e => { e.stopPropagation(); this.remove(i); });
        card.append(img, label, remove);
        card.title = 'Go to marker ' + (i + 1);
        card.addEventListener('click', () => { if (this.playing) this.pause(); this.select(i, true); });
        this.list.append(card);
      });
    }

    renderSelected() {
      const bar = this.selectedBar;
      bar.innerHTML = '';
      if (this.selected < 0) {
        bar.append(el('span', 'muted', 'Select a marker (click it on the bar or below) to nudge or move it.'));
        return;
      }
      const m = this.markers[this.selected];
      bar.append(el('span', 'acr-selected-label', 'Marker <strong>' + (this.selected + 1) + '</strong> at <strong>' + clock(m, true) + '</strong>'));
      bar.append(
        button('−1s', 'Nudge back one second (Alt+Shift+←)', () => this.nudge(-1)),
        button('−1 frame', 'Nudge back one frame (Alt+← or ,)', () => this.nudge(-this.frame)),
        button('+1 frame', 'Nudge forward one frame (Alt+→ or .)', () => this.nudge(this.frame)),
        button('+1s', 'Nudge forward one second (Alt+Shift+→)', () => this.nudge(1)),
        button('Move to playhead', 'Move this marker to where the playhead is', () => this.moveToPlayhead(), 'btn-ghost'),
        button('Go to it', 'Put the playhead on this marker', () => this.select(this.selected, true), 'btn-ghost'));
    }
  }

  window.schedularrChapterReview = {
    version: 1,
    mount(root, dotnet) {
      if (root._acr) root._acr.dispose();
      root._acr = new Player(root, dotnet);
      root.tabIndex = -1;
    },
    load(root, opts) { if (root && root._acr) root._acr.load(opts); },
    markers(root) { return root && root._acr ? root._acr.markers.slice() : []; },
    release(root) { if (root && root._acr) root._acr.release(); },
    dispose(root) { if (root && root._acr) { root._acr.dispose(); root._acr = null; } },
    pref(name, value) {
      try {
        if (value === undefined || value === null) return localStorage.getItem('acr.' + name);
        localStorage.setItem('acr.' + name, value);
      } catch { }
      return null;
    }
  };
})();
