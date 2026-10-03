// The Channels page's live preview: plays a channel's HLS stream from ErsatzTV (relayed by the scheduler) in a
// <video>. Browsers without built-in HLS use hls.js, fetched from a CDN the first time a preview opens.

const HLS_MODULE = 'https://cdn.jsdelivr.net/npm/hls.js@1/dist/hls.mjs';
const players = new WeakMap();
let hlsLoad;

function loadHls() {
    hlsLoad ??= import(HLS_MODULE).then(module => module.default).catch(() => null);
    return hlsLoad;
}

function say(status, text, kind) {
    if (status) {
        status.textContent = text;
        status.dataset.kind = kind || '';
    }
}

/** Why ErsatzTV won't hand over the channel, in words, from the playlist request's answer. */
function explain(number, code, body) {
    const detail = body ? ` ErsatzTV said: ${body.trim().slice(0, 300)}` : '';
    if (code === 404) {
        return `ErsatzTV has no channel ${number}. Rebuild the schedule (Dashboard → Rebuild) so it's in the lineup; `
            + `if ErsatzTV isn't started by SchedularrTV, restart it so it reads the new lineup.${detail}`;
    }
    if (code === 502 || code === 504) {
        return `${body || 'ErsatzTV isn\'t answering.'} Check that it's running (App settings → ErsatzTV Next) and that the streaming port is right.`;
    }
    return `The channel didn't start (${code}). The scheduler's log has ErsatzTV's output saying why, `
        + `for example a channel.json it refuses or a media file it can't open.${detail}`;
}

export async function start(video, url, number, status) {
    stop(video);
    const player = { stopped: false, hls: null };
    players.set(video, player);
    say(status, `Tuning to channel ${number}… ErsatzTV can take up to 30 seconds to start a channel.`, 'busy');

    // Asked once ourselves first, so a failure comes with ErsatzTV's reason rather than a bare player error.
    let response;
    try {
        response = await fetch(url, { cache: 'no-store' });
    } catch (error) {
        say(status, `Couldn't reach the scheduler: ${error.message}`, 'error');
        return;
    }
    if (player.stopped) return;
    if (!response.ok) {
        say(status, explain(number, response.status, await response.text().catch(() => '')), 'error');
        return;
    }

    const Hls = await loadHls();
    if (player.stopped) return;

    const playing = () => say(status, `Live: channel ${number}.`, 'ok');
    video.addEventListener('playing', playing, { once: true });

    if (Hls && Hls.isSupported()) {
        const hls = new Hls({ liveSyncDurationCount: 3, enableWorker: true });
        player.hls = hls;
        hls.on(Hls.Events.ERROR, (_, data) => {
            if (!data.fatal) return;
            if (data.type === Hls.ErrorTypes.NETWORK_ERROR) {
                say(status, 'The stream stopped answering; trying again…', 'busy');
                hls.startLoad();
            } else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) {
                hls.recoverMediaError();
            } else {
                say(status, `The player stopped: ${data.details}.`, 'error');
                hls.destroy();
            }
        });
        hls.loadSource(url);
        hls.attachMedia(video);
    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
        video.src = url;
        video.addEventListener('error', () => say(status, 'The browser couldn\'t play the stream.', 'error'), { once: true });
    } else {
        say(status, 'This browser can\'t play HLS by itself, and the HLS player (from cdn.jsdelivr.net) couldn\'t be loaded. '
            + 'Open the stream address below in VLC instead.', 'error');
        return;
    }

    // Started from a click, so sound is normally allowed; if the browser says no, start muted rather than not at all.
    try {
        await video.play();
    } catch {
        if (player.stopped) return;
        video.muted = true;
        try { await video.play(); } catch { /* the controls are there to start it */ }
    }
}

/** Stops playback and the downloads behind it, so ErsatzTV stops counting a viewer. */
export function stop(video) {
    const player = video && players.get(video);
    if (!player) return;
    player.stopped = true;
    player.hls?.destroy();
    players.delete(video);
    video.pause();
    video.removeAttribute('src');
    video.load();
}
