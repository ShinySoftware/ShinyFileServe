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

// A request whose body has stopped moving for this long is treated as a dropped connection. An
// XMLHttpRequest has no idle timeout of its own, so a connection that hangs without closing (a
// stalled HTTP/2 stream, a proxy holding a dead socket) used to leave the progress bar frozen
// forever without the retry logic ever starting. Only counted while the body is still going up:
// once every byte is sent the server may be scanning the file, and that wait is legitimate.
const STALL_SECONDS = 60;

// The same for an ordinary multipart upload, which is longer because the server stops reading it
// while it scans each finished file: the next file's bytes wait until the scan is done, and a scan
// may take up to its own two-minute timeout. A minute would cut off a batch that is fine.
const MULTIPART_STALL_SECONDS = 180;

// Every wait in this file, in one place. Only the tests change them, so a retry that waits a minute
// in the browser waits milliseconds there.
const timing = {
    stallSeconds: STALL_SECONDS,
    multipartStallSeconds: MULTIPART_STALL_SECONDS,
    retryDelays: null,
    finishingSeconds: null,
    pollSeconds: 3
};

/** For the tests only: shortens the waits. Never called by the app. */
export function __setTimingForTests(overrides) {
    Object.assign(timing, overrides);
}

/** Aborts `xhr` if its upload makes no progress for STALL_SECONDS. Returns a function that reports whether it did. */
function watchStall(xhr, seconds = timing.stallSeconds) {
    let stalled = false;
    let timer = null;
    let sentAll = false;

    const arm = () => {
        clearTimeout(timer);
        if (sentAll) return;
        timer = setTimeout(() => {
            stalled = true;
            xhr.abort();
        }, seconds * 1000);
    };

    xhr.upload.addEventListener('loadstart', arm);
    xhr.upload.addEventListener('progress', e => {
        if (e.lengthComputable && e.loaded >= e.total) sentAll = true;
        arm();
    });
    xhr.upload.addEventListener('load', () => {
        sentAll = true;
        clearTimeout(timer);
    });
    xhr.addEventListener('loadend', () => clearTimeout(timer));

    return () => stalled;
}

// Throttled: progress fires every few kilobytes, and each report is a render on the .NET side.
function throttled(dotnet) {
    let last = 0;
    return (loaded, total) => {
        const now = performance.now();
        if (now - last < 120 && loaded < total) return;
        last = now;
        dotnet.invokeMethodAsync('OnProgress', loaded, total);
    };
}

/**
 * Posts some of a claim's files - `indexes` into it - as multipart/form-data under the field name
 * "files". Resolves with the status and body - 0 for a lost connection, -1 for a cancel - and never
 * rejects, so .NET has one shape to read.
 */
export function start(id, key, indexes, url, dotnet) {
    const files = claimed.get(key) || [];

    const form = new FormData();
    for (const index of indexes)
        form.append('files', files[index], files[index].name);

    const report = throttled(dotnet);

    return new Promise(resolve => {
        const xhr = new XMLHttpRequest();
        active.set(id, { abort: () => xhr.abort() });

        xhr.upload.onprogress = e => report(e.loaded, e.total);
        const stalled = watchStall(xhr, timing.multipartStallSeconds);

        const done = (status, body) => {
            active.delete(id);
            resolve({ status, body });
        };

        xhr.onload = () => done(xhr.status, xhr.responseText);
        xhr.onerror = () => done(0, '');
        xhr.onabort = () => (stalled()
            ? done(0, 'The connection stopped moving and the upload was abandoned. Try again.')
            : done(-1, ''));

        xhr.open('POST', url);
        xhr.send(form);
    });
}

export function abort(id) {
    const job = active.get(id);
    if (job) job.abort();
}

// ---------------------------------------------------------------------------
// Resumable uploads (tus 1.0.0)
// ---------------------------------------------------------------------------
//
// A large file goes up as a series of PATCHes against an upload the server creates for it, so a
// dropped connection costs only the chunk in flight - and not even that, since the server keeps
// whatever part of it arrived. On a failure the upload waits, asks the server how far it got, and
// carries on from there. The upload's address is remembered in this browser too, so picking the
// same file again after a reload resumes it rather than starting over.

const TUS_VERSION = '1.0.0';

// Seconds to wait before each retry. About four and a half minutes in all: long enough for Wi-Fi to
// come back or a laptop lid to be opened again, short enough that a server that is really gone is
// reported rather than waited on forever.
const RETRY_DELAYS = [1, 2, 4, 8, 15, 30, 30, 60, 60, 60];
timing.retryDelays = RETRY_DELAYS;

// Worth another try: no connection, the upload busy with a request that has not let go yet, or the
// server - or a proxy in front of it - having a moment. 520-524 are what Cloudflare answers when it
// cannot reach the server or gives up waiting on it, which is the same thing seen through a tunnel.
// 503 is not here: that is the virus scanner unable to judge the file, which a retry would not change.
const RETRYABLE = new Set([0, 408, 423, 500, 502, 504, 520, 521, 522, 523, 524]);

// How long to keep asking after every byte has arrived but the server has not said how the upload
// ended - it is still scanning or saving the file. A little longer than the scanner's own timeout.
const FINISHING_SECONDS = 180;
timing.finishingSeconds = FINISHING_SECONDS;

const REMEMBERED = 'fileserve.uploads';

function readRemembered() {
    try {
        return JSON.parse(localStorage.getItem(REMEMBERED) || '{}');
    } catch {
        return {};
    }
}

function writeRemembered(map) {
    try {
        localStorage.setItem(REMEMBERED, JSON.stringify(map));
    } catch {
        // Private window or storage turned off: uploads still resume, just not across a reload.
    }
}

function remember(fingerprint, url) {
    const map = readRemembered();
    map[fingerprint] = url;
    writeRemembered(map);
}

function forget(fingerprint) {
    const map = readRemembered();
    delete map[fingerprint];
    writeRemembered(map);
}

function base64(text) {
    let binary = '';
    for (const byte of new TextEncoder().encode(text))
        binary += String.fromCharCode(byte);
    return btoa(binary);
}

/** The server's message out of a body that may be problem details, plain text, or nothing. */
function message(body) {
    if (!body) return '';
    try {
        const problem = JSON.parse(body);
        return problem.detail || problem.title || '';
    } catch {
        return body;
    }
}

function request(job, method, url, headers, body, onProgress) {
    return new Promise(resolve => {
        const xhr = new XMLHttpRequest();
        job.xhr = xhr;

        if (onProgress)
            xhr.upload.onprogress = e => onProgress(e.loaded);

        // Only a request with a body can stall on the way up; a HEAD that hangs is the browser's to time out.
        const stalled = body ? watchStall(xhr) : () => false;

        const done = status => {
            job.xhr = null;
            resolve({
                status,
                body: status > 0 ? xhr.responseText : '',
                header: name => (status > 0 ? xhr.getResponseHeader(name) : null)
            });
        };

        xhr.onload = () => done(xhr.status);
        xhr.onerror = () => done(0);
        xhr.onabort = () => done(stalled() ? 0 : -1);

        xhr.open(method, url);
        xhr.setRequestHeader('Tus-Resumable', TUS_VERSION);
        for (const [name, value] of Object.entries(headers))
            xhr.setRequestHeader(name, value);
        xhr.send(body);
    });
}

function sleep(job, seconds) {
    return new Promise(resolve => {
        const timer = setTimeout(resolve, seconds * 1000);
        job.wake = () => {
            clearTimeout(timer);
            resolve();
        };
    });
}

/**
 * How many bytes the server holds, or -1 when it has no such upload (or not for this account). For
 * an upload that has finished, the server also says how it ended: `savedAs` is where the file was
 * saved, `error` and `errorStatus` why it was not. Neither means it has not finished yet.
 */
async function offsetOf(job) {
    const r = await request(job, 'HEAD', job.url, {}, null);
    if (r.status === 200 || r.status === 204) {
        const saved = r.header('Upload-Saved-As');
        const error = r.header('Upload-Error');
        return {
            status: r.status,
            offset: parseInt(r.header('Upload-Offset'), 10),
            savedAs: saved ? decodeURIComponent(saved) : null,
            error: error ? decodeURIComponent(error) : null,
            errorStatus: parseInt(r.header('Upload-Error-Status') || '0', 10) || 500
        };
    }

    return { status: r.status, offset: -1, body: r.body };
}

/**
 * Every byte is on the server but the reply saying how it went was lost. Asks until the server
 * says the file was saved, or why it was not, or until it has had longer than a scan can take.
 */
async function awaitOutcome(job, known, size) {
    const until = performance.now() + timing.finishingSeconds * 1000;
    while (true) {
        if (known.savedAs)
            return { status: 200, body: '', savedAs: known.savedAs };

        if (known.error)
            return { status: known.errorStatus, body: known.error };

        if (known.offset >= 0 && known.offset < size)
            return null;

        if (known.offset < 0 && !RETRYABLE.has(known.status))
            return { status: 0, body: 'The file arrived, but the connection dropped before the server could confirm it was saved. Refresh the folder to check.' };

        if (performance.now() > until)
            return { status: 0, body: 'The file arrived, but the server has not confirmed it was saved. Refresh the folder to check.' };

        await sleep(job, timing.pollSeconds);
        if (job.cancelled)
            return { status: -1, body: '' };

        known = await offsetOf(job);
    }
}

/**
 * Sends file `index` of a claim over tus. `metadata` is what the server needs to know where the
 * file goes - folderId, path, filename, overwrite. Resolves like `start`, plus `savedAs`: where the
 * file actually landed, which is somewhere else when its name was taken.
 */
export async function startResumable(id, key, index, endpoint, metadata, chunkSize, dotnet) {
    const file = (claimed.get(key) || [])[index];
    if (!file)
        return { status: 400, body: 'The file is no longer available. Pick it again.' };

    const job = { xhr: null, wake: null, cancelled: false, url: null };
    active.set(id, {
        abort: () => {
            job.cancelled = true;
            if (job.xhr) job.xhr.abort();
            if (job.wake) job.wake();
        }
    });

    const report = throttled(dotnet);
    const status = text => dotnet.invokeMethodAsync('OnStatus', text);
    const fingerprint = [metadata.folderId, metadata.path, file.name, file.size, file.lastModified].join('|');

    const finish = result => {
        active.delete(id);
        return result;
    };

    const cancelled = async () => {
        // The server holds the bytes and the quota reservation until told otherwise; tell it.
        if (job.url)
            await request({ xhr: null }, 'DELETE', job.url, {}, null);
        forget(fingerprint);
        return finish({ status: -1, body: '' });
    };

    // Picked again after a reload, or a retry of one that failed: carry on where the server is.
    job.url = readRemembered()[fingerprint] || null;
    let offset = -1;
    if (job.url) {
        const known = await offsetOf(job);
        offset = known.offset;

        // The same file to the same place already went up in full, and the reply to it was missed.
        if (known.savedAs) {
            forget(fingerprint);
            return finish({ status: 200, body: '', savedAs: known.savedAs });
        }

        // It went up before and was refused or failed to save. Start a fresh upload.
        if (known.error)
            offset = -1;

        if (offset < 0) {
            forget(fingerprint);
            job.url = null;
        } else if (offset > 0) {
            dotnet.invokeMethodAsync('OnResumed', offset);
        }
    }

    if (job.cancelled)
        return cancelled();

    if (offset < 0) {
        const pairs = Object.entries(metadata).map(([k, v]) => `${k} ${base64(String(v))}`);
        const created = await request(job, 'POST', endpoint, {
            'Upload-Length': String(file.size),
            'Upload-Metadata': pairs.join(',')
        }, null);

        if (job.cancelled)
            return cancelled();

        if (created.status !== 201)
            return finish({ status: created.status, body: message(created.body) });

        // The path alone: behind a tunnel or proxy the scheme and host the server sees are not
        // always the ones this page was loaded from.
        job.url = new URL(created.header('Location'), window.location.href).pathname;
        remember(fingerprint, job.url);
        offset = 0;
    }

    let attempt = 0;
    while (offset < file.size) {
        const from = offset;
        const end = Math.min(from + chunkSize, file.size);
        const r = await request(job, 'PATCH', job.url, {
            'Upload-Offset': String(from),
            'Content-Type': 'application/offset+octet-stream'
        }, file.slice(from, end), loaded => report(from + loaded, file.size));

        if (job.cancelled)
            return cancelled();

        if (r.status === 204) {
            offset = parseInt(r.header('Upload-Offset'), 10);
            report(offset, file.size);

            if (attempt > 0) {
                attempt = 0;
                status(null);
            }

            if (offset >= file.size) {
                forget(fingerprint);
                const saved = r.header('Upload-Saved-As');
                return finish({ status: 200, body: '', savedAs: saved ? decodeURIComponent(saved) : null });
            }
        } else if (r.status === 409 || RETRYABLE.has(r.status)) {
            // A 409 is the offset having moved under us - a request that was cut off kept what it
            // had. Either way, ask where things stand before sending another byte.
            attempt++;
            const delays = timing.retryDelays;
            if (attempt > delays.length)
                return finish({ status: 0, body: 'The connection was lost and did not come back. Pick the file again to resume.' });

            if (r.status !== 409) {
                status(`Reconnecting… (attempt ${attempt} of ${delays.length})`);
                await sleep(job, delays[attempt - 1]);
                if (job.cancelled)
                    return cancelled();
            }

            const known = await offsetOf(job);
            if (job.cancelled)
                return cancelled();

            // Every byte is there, so the last chunk did arrive - but the answer saying how it went
            // was lost with the connection. The server keeps that answer for a while; ask for it,
            // waiting if it is still scanning or saving the file.
            if (known.offset >= file.size || known.savedAs || known.error) {
                const outcome = await awaitOutcome(job, known, file.size);
                if (job.cancelled || (outcome && outcome.status === -1))
                    return cancelled();

                if (outcome) {
                    forget(fingerprint);
                    return finish(outcome);
                }
            }

            if (known.offset >= 0) {
                offset = known.offset;
                report(offset, file.size);
            } else if (!RETRYABLE.has(known.status)) {
                forget(fingerprint);
                return finish({ status: known.status, body: message(known.body) || 'The upload expired on the server. Start it again.' });
            }
        } else {
            forget(fingerprint);
            return finish({ status: r.status, body: message(r.body) });
        }
    }

    // Only reached for an empty file, which the caller sends the other way.
    forget(fingerprint);
    return finish({ status: 200, body: '', savedAs: null });
}
