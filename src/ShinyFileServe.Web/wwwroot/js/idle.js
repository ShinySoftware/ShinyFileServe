// Idle watch. The screen is a browser concept, so this is the only place that can see it go quiet.
// One timer, reset by real input; passive listeners so scrolling stays smooth.
let timer = null;
let handler = null;
let minutes = 0;

const EVENTS = ['pointerdown', 'pointermove', 'keydown', 'wheel', 'touchstart'];

function reset() {
    if (timer) clearTimeout(timer);
    if (minutes <= 0) return;

    timer = setTimeout(() => {
        // Fires once. The app signs out and navigates; it re-arms the watch if it needs to.
        if (handler) handler.invokeMethodAsync('OnIdle');
    }, minutes * 60_000);
}

export function start(dotNetHandler, idleMinutes) {
    stop();
    handler = dotNetHandler;
    minutes = idleMinutes;
    if (minutes <= 0) return;

    for (const name of EVENTS) window.addEventListener(name, reset, { passive: true });
    document.addEventListener('visibilitychange', reset);
    reset();
}

export function stop() {
    if (timer) clearTimeout(timer);
    timer = null;
    for (const name of EVENTS) window.removeEventListener(name, reset);
    document.removeEventListener('visibilitychange', reset);
    handler = null;
    minutes = 0;
}
