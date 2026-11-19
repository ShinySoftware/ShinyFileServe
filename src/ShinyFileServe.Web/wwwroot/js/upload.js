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

/**
 * Posts files as multipart/form-data under the field name "files". `source` is an <input type=file>
 * or the string 'drop'; `names` narrows a drop to the files .NET accepted. Resolves with the status
 * and body - 0 for a lost connection, -1 for a cancel - and never rejects, so .NET has one shape to read.
 */
export function start(id, source, names, url, dotnet) {
    const files = source === 'drop'
        ? dropped.filter(f => names.includes(f.name))
        : Array.from(source.files || []);

    if (source === 'drop')
        dropped = [];

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

            // Cleared so picking the same file again still raises a change.
            if (source !== 'drop')
                source.value = '';

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
