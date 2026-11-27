// A browser for upload.js to run in, without a browser: a fake XMLHttpRequest wired to an in-memory
// tus server that answers the way FileServe's does - including what it says about an upload that has
// already finished - and that can be told to fail in the ways real connections fail.

export class FakeTusServer {
    constructor() {
        this.uploads = new Map();   // id -> { length, bytes: Uint8Array, offset, name }
        this.finished = new Map();  // id -> { length, savedAs, status, error }
        this.saved = new Map();     // name -> Uint8Array
        this.requests = [];         // every request, for assertions
        this.faults = [];           // consumed one per PATCH: what goes wrong with it, or null
        this.finishingHeads = 0;    // HEADs that still see "every byte, no outcome yet"
        this.nextId = 1;
        this.saveError = null;      // set to make completion fail like an unwritable folder
    }

    patchFault() {
        return this.faults.length > 0 ? this.faults.shift() : null;
    }

    /** Answers one request. `io` reports upload progress and may fail the connection. */
    async handle(req, io) {
        this.requests.push({ method: req.method, url: req.url, headers: { ...req.headers } });

        if (req.method === 'POST' && req.url === '/api/uploads') {
            const id = 'u' + this.nextId++;
            const length = parseInt(req.headers['Upload-Length'], 10);
            const meta = Object.fromEntries(req.headers['Upload-Metadata'].split(',').map(pair => {
                const [k, v] = pair.split(' ');
                return [k, Buffer.from(v, 'base64').toString('utf8')];
            }));
            this.uploads.set(id, { length, bytes: new Uint8Array(length), offset: 0, name: meta.filename });
            return { status: 201, headers: { Location: `/api/uploads/${id}` } };
        }

        const id = req.url.split('/').pop();
        const upload = this.uploads.get(id);
        const done = this.finished.get(id);

        if (req.method === 'HEAD') {
            if (upload && this.finishingHeads > 0 && upload.offset === upload.length) {
                this.finishingHeads--;
                return { status: 200, headers: { 'Upload-Offset': String(upload.offset), 'Upload-Length': String(upload.length) } };
            }
            if (upload && upload.completing) {
                // The scan is over: publish now.
                this.complete(id, upload);
                return this.handle(req, io);
            }
            if (upload)
                return { status: 200, headers: { 'Upload-Offset': String(upload.offset), 'Upload-Length': String(upload.length) } };
            if (done)
                return { status: 200, headers: { 'Upload-Offset': String(done.length), 'Upload-Length': String(done.length), ...outcomeHeaders(done) } };
            return { status: 404, body: 'That upload does not exist, or has expired.' };
        }

        if (req.method === 'DELETE') {
            this.uploads.delete(id);
            return { status: 204 };
        }

        if (req.method === 'PATCH') {
            if (!upload && done) {
                return done.error
                    ? { status: done.status, headers: outcomeHeaders(done), body: done.error }
                    : { status: 204, headers: { 'Upload-Offset': String(done.length), ...outcomeHeaders(done) } };
            }
            if (!upload)
                return { status: 404, body: 'That upload does not exist, or has expired.' };

            const fault = this.patchFault();
            const offset = parseInt(req.headers['Upload-Offset'], 10);
            if (fault?.status)
                return { status: fault.status, body: 'proxy says no' };
            if (fault?.stall)
                return io.hang();
            if (offset !== upload.offset)
                return { status: 409, body: 'Offset mismatch' };

            const body = new Uint8Array(await req.body.arrayBuffer());
            const keep = fault?.dropAfter !== undefined ? Math.min(fault.dropAfter, body.length) : body.length;
            upload.bytes.set(body.subarray(0, keep), offset);
            upload.offset += keep;
            io.progress(keep, body.length);

            if (fault?.dropAfter !== undefined)
                return io.drop();

            if (upload.offset < upload.length)
                return { status: 204, headers: { 'Upload-Offset': String(upload.offset) } };

            // The last byte: scan and publish, as the server's completion hook does.
            if (fault?.stillScanning) {
                upload.completing = true;
                return io.drop();
            }

            const outcome = this.complete(id, upload);
            if (fault?.loseReply)
                return io.drop();

            return outcome.error
                ? { status: outcome.status, headers: outcomeHeaders(outcome), body: outcome.error }
                : { status: 204, headers: { 'Upload-Offset': String(upload.length), ...outcomeHeaders(outcome) } };
        }

        return { status: 405 };
    }

    complete(id, upload) {
        this.uploads.delete(id);
        const outcome = this.saveError
            ? { length: upload.length, savedAs: null, status: 500, error: this.saveError }
            : { length: upload.length, savedAs: upload.name, status: 204, error: null };

        if (!outcome.error)
            this.saved.set(upload.name, upload.bytes);

        this.finished.set(id, outcome);
        return outcome;
    }
}

function outcomeHeaders(outcome) {
    const headers = {};
    if (outcome.savedAs)
        headers['Upload-Saved-As'] = encodeURIComponent(outcome.savedAs);
    if (outcome.error) {
        headers['Upload-Error'] = encodeURIComponent(outcome.error);
        headers['Upload-Error-Status'] = String(outcome.status);
    }
    return headers;
}

class FakeEventTarget {
    constructor() {
        this.listeners = {};
    }
    addEventListener(type, fn) {
        (this.listeners[type] ||= []).push(fn);
    }
    dispatch(type, event = {}) {
        const handler = this['on' + type];
        if (handler) handler(event);
        for (const fn of this.listeners[type] || []) fn(event);
    }
}

/** An XMLHttpRequest whose network is `server`. */
export function fakeXhr(server) {
    return class FakeXMLHttpRequest extends FakeEventTarget {
        constructor() {
            super();
            this.upload = new FakeEventTarget();
            this.headers = {};
            this.status = 0;
            this.responseText = '';
            this.responseHeaders = {};
            this.state = 'new';
        }

        open(method, url) {
            this.method = method;
            this.url = url;
        }

        setRequestHeader(name, value) {
            this.headers[name] = value;
        }

        getResponseHeader(name) {
            return this.responseHeaders[name] ?? null;
        }

        send(body) {
            this.state = 'sending';
            const size = body && typeof body.size === 'number' ? body.size : 0;
            if (body) this.upload.dispatch('loadstart', { loaded: 0, total: size, lengthComputable: true });

            const io = {
                progress: (loaded, total) => {
                    if (this.state !== 'sending') return;
                    this.upload.dispatch('progress', { loaded, total, lengthComputable: true });
                },
                drop: () => ({ drop: true }),
                hang: () => new Promise(() => { })
            };

            setTimeout(async () => {
                const result = await server.handle({ method: this.method, url: this.url, headers: this.headers, body }, io);
                if (this.state !== 'sending') return;

                if (result.drop) {
                    this.state = 'done';
                    this.dispatch('error');
                    this.dispatch('loadend');
                    return;
                }

                if (body) {
                    this.upload.dispatch('progress', { loaded: size, total: size, lengthComputable: true });
                    this.upload.dispatch('load');
                }

                this.state = 'done';
                this.status = result.status;
                this.responseText = result.body || '';
                this.responseHeaders = result.headers || {};
                this.dispatch('load');
                this.dispatch('loadend');
            });
        }

        abort() {
            if (this.state !== 'sending') return;
            this.state = 'aborted';
            this.dispatch('abort');
            this.dispatch('loadend');
        }
    };
}

/** What .NET's DotNetObjectReference looks like from the script's side. */
export function fakeDotnet() {
    const calls = [];
    return {
        calls,
        invokeMethodAsync(name, ...args) {
            calls.push({ name, args });
            return Promise.resolve();
        },
        named(name) {
            return calls.filter(c => c.name === name).map(c => c.args);
        }
    };
}

export function installBrowser(server) {
    const store = new Map();
    globalThis.window = { location: { href: 'https://files.example/' }, addEventListener() { } };
    globalThis.localStorage = {
        getItem: k => (store.has(k) ? store.get(k) : null),
        setItem: (k, v) => store.set(k, String(v)),
        removeItem: k => store.delete(k),
        clear: () => store.clear()
    };
    globalThis.XMLHttpRequest = fakeXhr(server);
    return store;
}

export function randomFile(name, size, seed = 1) {
    const bytes = new Uint8Array(size);
    let x = seed;
    for (let i = 0; i < size; i++) {
        x = (x * 1103515245 + 12345) & 0x7fffffff;
        bytes[i] = x & 0xff;
    }
    return { file: new File([bytes], name, { lastModified: 1700000000000 }), bytes };
}
