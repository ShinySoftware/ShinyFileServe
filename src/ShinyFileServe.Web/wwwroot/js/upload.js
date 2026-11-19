// Uploads with progress. HttpClient in the browser is fetch, and fetch says nothing about a request
// body on its way out - over plain http it reads the whole thing into memory before sending a byte.
// XMLHttpRequest reports upload progress and streams a File straight off the disk, so the bytes
// never pass through .NET at all.

const active = new Map();

// The files of the last drop anywhere on the window. Shiny's file drop service owns the overlay and
// tells .NET what was dropped, but keeps the File objects to itself; this listener runs beside its
// own, capturing too, so whatever it saw is here as well.
let dropped = [];
let watching = false;

export function watchDrops() {
    if (watching) return;
    watching = true;

    window.addEventListener('drop', e => {
        dropped = e.dataTransfer ? Array.from(e.dataTransfer.files || []) : [];
    }, true);
}

// Files set aside for an upload, by claim key. Uploads can queue behind one another, and by the
// time a queued one starts, the input may hold a later pick and the last drop may be a later drop -
// so each upload takes its own copy the moment it is asked for, and sends that.
const claimed = new Map();
let nextClaim = 0;

/**
 * Sets the files aside for one upload and returns the key to start it with. `source` is an
 * <input type=file>, which is cleared so picking the same file again still raises a change, or the
 * string 'drop'; `names` narrows a drop to the files .NET accepted.
 */
export async function claim(source, names) {
    let files;
    if (source === 'drop') {
        // Shiny's drop listener was attached first, so it runs first, and .NET can get from its
        // OnDrop all the way here while the same drop event is still being dispatched - before the
        // listener above has seen it. A macrotask runs only once that dispatch is over, so after it
        // `dropped` is this drop's files rather than nothing (or the last drop's).
        await new Promise(resolve => setTimeout(resolve));
        files = dropped.filter(f => names.includes(f.name));
        dropped = [];
    } else {
        files = Array.from(source.files || []);
        source.value = '';
    }

    const key = 'c' + (nextClaim++);
    claimed.set(key, files);
    return key;
}

/** Forgets a claim that will not be sent - refused, or cancelled while it waited its turn. */
export function release(key) {
    claimed.delete(key);
}

/**
 * Posts a claim's files as multipart/form-data under the field name "files". Resolves with the
 * status and body - 0 for a lost connection, -1 for a cancel - and never rejects, so .NET has one
 * shape to read.
 */
export function start(id, key, url, dotnet) {
    const files = claimed.get(key) || [];
    claimed.delete(key);

    const form = new FormData();
    for (const file of files)
        form.append('files', file, file.name);

    return new Promise(resolve => {
        const xhr = new XMLHttpRequest();
        active.set(id, xhr);

        // Throttled: progress fires every few kilobytes, and each one is a render on the .NET side.
        let last = 0;
        xhr.upload.onprogress = e => {
            const now = performance.now();
            if (now - last < 120 && e.loaded < e.total) return;
            last = now;
            dotnet.invokeMethodAsync('OnProgress', e.loaded, e.total);
        };

        const done = (status, body) => {
            active.delete(id);
            resolve({ status, body });
        };

        xhr.onload = () => done(xhr.status, xhr.responseText);
        xhr.onerror = () => done(0, '');
        xhr.onabort = () => done(-1, '');

        xhr.open('POST', url);
        xhr.send(form);
    });
}

export function abort(id) {
    const xhr = active.get(id);
    if (xhr) xhr.abort();
}
