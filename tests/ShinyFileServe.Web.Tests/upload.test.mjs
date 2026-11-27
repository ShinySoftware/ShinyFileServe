// upload.js under the failures real uploads meet. Run with: node --test tests/ShinyFileServe.Web.Tests
import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { FakeTusServer, fakeDotnet, installBrowser, randomFile } from './fake-tus.mjs';

let server;
let storage;
installBrowser(new FakeTusServer());
const upload = await import('../../src/ShinyFileServe.Web/wwwroot/js/upload.js');

const CHUNK = 64 * 1024;
const META = { folderId: 'f1', path: '', filename: 'video.mp4', overwrite: 'true' };

beforeEach(() => {
    server = new FakeTusServer();
    storage = installBrowser(server);

    // Milliseconds instead of minutes. The shape of the waits is what matters, not their length.
    upload.__setTimingForTests({
        stallSeconds: 0.05,
        multipartStallSeconds: 0.3,
        retryDelays: [0.001, 0.001, 0.001, 0.001],
        finishingSeconds: 1,
        pollSeconds: 0.005
    });
});

async function send(file, { metadata = META, dotnet = fakeDotnet(), id = 'job' } = {}) {
    const key = await upload.claim({ files: [file], value: '' }, []);
    const result = await upload.startResumable(id, key, 0, '/api/uploads', { ...metadata, filename: file.name }, CHUNK, dotnet);
    return { result, dotnet };
}

const patches = () => server.requests.filter(r => r.method === 'PATCH').length;

test('an upload goes up in chunks and reports where it was saved', async () => {
    const { file, bytes } = randomFile('video.mp4', 5 * CHUNK + 123);

    const { result } = await send(file);

    assert.equal(result.status, 200);
    assert.equal(result.savedAs, 'video.mp4');
    assert.deepEqual(server.saved.get('video.mp4'), bytes);
    assert.equal(patches(), 6);
    assert.equal(storage.size === 0 || storage.get('fileserve.uploads') === '{}', true, 'forgets the finished upload');
});

test('a chunk cut off part way resumes from what the server kept', async () => {
    const { file, bytes } = randomFile('video.mp4', 4 * CHUNK + 7);
    server.faults = [null, { dropAfter: 20000 }];

    const { result, dotnet } = await send(file);

    assert.equal(result.status, 200);
    assert.deepEqual(server.saved.get('video.mp4'), bytes);
    assert.ok(dotnet.named('OnStatus').some(([text]) => text?.startsWith('Reconnecting')), 'says it is reconnecting');
    assert.ok(dotnet.named('OnStatus').some(([text]) => text === null), 'and clears that once moving again');

    // After the drop it asked where things stood, and carried on from the 20,000 bytes that arrived.
    const resumed = server.requests.filter(r => r.method === 'PATCH').map(r => parseInt(r.headers['Upload-Offset'], 10));
    assert.ok(resumed.includes(CHUNK + 20000), `resumed at the kept offset: ${resumed}`);
});

test('when the reply to the last chunk is lost, it asks and learns the file was saved', async () => {
    const { file, bytes } = randomFile('video.mp4', 2 * CHUNK);
    server.faults = [null, { loseReply: true }];

    const { result } = await send(file);

    assert.equal(result.status, 200, result.body);
    assert.equal(result.savedAs, 'video.mp4');
    assert.deepEqual(server.saved.get('video.mp4'), bytes);
});

test('when the reply is lost while the server is still scanning, it waits for the outcome', async () => {
    const { file } = randomFile('video.mp4', 2 * CHUNK);
    server.faults = [null, { stillScanning: true }];
    server.finishingHeads = 5;

    const { result } = await send(file);

    assert.equal(result.status, 200, result.body);
    assert.equal(result.savedAs, 'video.mp4');
    assert.ok(server.requests.filter(r => r.method === 'HEAD').length >= 6, 'kept asking while the server finished');
});

test('a save that fails on the server is reported with its reason, not as an expired upload', async () => {
    const { file } = randomFile('video.mp4', 2 * CHUNK);
    server.saveError = 'The file arrived, but the server could not save it into the folder.';

    const { result } = await send(file);

    assert.equal(result.status, 500);
    assert.match(result.body, /could not save/);
    assert.doesNotMatch(result.body, /expired/);
});

test('a failed save whose reply was lost is still reported with its reason', async () => {
    const { file } = randomFile('video.mp4', 2 * CHUNK);
    server.saveError = 'The file arrived, but the server could not save it into the folder.';
    server.faults = [null, { loseReply: true }];

    const { result } = await send(file);

    assert.equal(result.status, 500);
    assert.match(result.body, /could not save/);
});

test('a request that stops moving is abandoned and retried instead of hanging forever', async () => {
    const { file, bytes } = randomFile('video.mp4', 3 * CHUNK);
    server.faults = [null, { stall: true }];

    const { result } = await send(file);

    assert.equal(result.status, 200);
    assert.deepEqual(server.saved.get('video.mp4'), bytes);
});

test('a Cloudflare timeout (524) is retried', async () => {
    const { file, bytes } = randomFile('video.mp4', 2 * CHUNK);
    server.faults = [{ status: 524 }];

    const { result } = await send(file);

    assert.equal(result.status, 200);
    assert.deepEqual(server.saved.get('video.mp4'), bytes);
});

test('a refusal that a retry cannot fix is reported straight away', async () => {
    const { file } = randomFile('video.mp4', 2 * CHUNK);
    server.faults = [{ status: 403 }];

    const { result } = await send(file);

    assert.equal(result.status, 403);
    assert.equal(patches(), 1);
});

test('it gives up after the last retry and says how to resume', async () => {
    const { file } = randomFile('video.mp4', 2 * CHUNK);
    server.faults = Array.from({ length: 20 }, () => ({ status: 502 }));

    const { result } = await send(file);

    assert.equal(result.status, 0);
    assert.match(result.body, /Pick the file again to resume/);
});

test('picking the same file again after a reload resumes where the server is', async () => {
    const { file, bytes } = randomFile('video.mp4', 4 * CHUNK);

    // The first attempt dies for good part way through (every retry fails).
    server.faults = [null, null, ...Array.from({ length: 10 }, () => ({ status: 502 }))];
    const first = await send(file);
    assert.equal(first.result.status, 0);

    // The page is reloaded and the file picked again.
    server.faults = [];
    const created = server.requests.filter(r => r.method === 'POST').length;
    const { result, dotnet } = await send(file);

    assert.equal(result.status, 200);
    assert.deepEqual(server.saved.get('video.mp4'), bytes);
    assert.equal(server.requests.filter(r => r.method === 'POST').length, created, 'no new upload was created');
    assert.deepEqual(dotnet.named('OnResumed'), [[2 * CHUNK]]);
});

test('picking a file again whose earlier upload finished unseen returns the saved file without sending it again', async () => {
    const { file } = randomFile('video.mp4', 2 * CHUNK);

    // Sent in full by a tab that closed before it heard back: the server finished it, and this
    // browser still remembers the upload's address.
    const created = await server.handle({
        method: 'POST',
        url: '/api/uploads',
        headers: {
            'Upload-Length': String(file.size),
            'Upload-Metadata': `folderId ${btoa('f1')},path ,filename ${btoa('video.mp4')},overwrite ${btoa('true')}`
        }
    }, {});
    const url = created.headers.Location;
    const io = { progress() { }, drop: () => ({ drop: true }) };
    await server.handle({ method: 'PATCH', url, headers: { 'Upload-Offset': '0' }, body: new Blob([await file.arrayBuffer()]) }, io);

    const fingerprint = ['f1', '', 'video.mp4', file.size, file.lastModified].join('|');
    storage.set('fileserve.uploads', JSON.stringify({ [fingerprint]: url }));
    const before = patches();

    const { result } = await send(file);

    assert.equal(result.status, 200);
    assert.equal(result.savedAs, 'video.mp4');
    assert.equal(patches(), before, 'nothing was sent again');
});

test('cancelling tells the server to throw the upload away', async () => {
    const { file } = randomFile('video.mp4', 4 * CHUNK);
    server.faults = [null, { stall: true }];
    upload.__setTimingForTests({ stallSeconds: 60 });

    const pending = send(file);
    await new Promise(resolve => setTimeout(resolve, 30));
    upload.abort('job');
    const { result } = await pending;

    assert.equal(result.status, -1);
    assert.ok(server.requests.some(r => r.method === 'DELETE'), 'sent DELETE');
});

test('a multipart upload that stops moving fails instead of hanging', async () => {
    const { file } = randomFile('photo.jpg', 1000);
    globalThis.XMLHttpRequest = class extends globalThis.XMLHttpRequest {
        send(body) {
            this.state = 'sending';
            this.upload.dispatch('loadstart', { loaded: 0, total: 1000, lengthComputable: true });
        }
    };

    const key = await upload.claim({ files: [file], value: '' }, []);
    const result = await upload.start('multi', key, [0], '/api/files/upload', fakeDotnet());

    assert.equal(result.status, 0);
    assert.match(result.body, /stopped moving/);
});

test('a multipart batch paused while the server scans a file is not mistaken for a stall', async () => {
    const { file } = randomFile('photos.zip', 1000);
    globalThis.XMLHttpRequest = class extends globalThis.XMLHttpRequest {
        send(body) {
            this.state = 'sending';
            this.upload.dispatch('loadstart', { loaded: 0, total: 1000, lengthComputable: true });
            this.upload.dispatch('progress', { loaded: 400, total: 1000, lengthComputable: true });

            // Longer than a resumable chunk may stall, shorter than a multipart batch may: the
            // server is scanning the first file and not reading the rest.
            setTimeout(() => {
                if (this.state !== 'sending') return;
                this.upload.dispatch('progress', { loaded: 1000, total: 1000, lengthComputable: true });
                this.upload.dispatch('load');
                this.state = 'done';
                this.status = 200;
                this.responseText = '[]';
                this.dispatch('load');
                this.dispatch('loadend');
            }, 150);
        }
    };

    const key = await upload.claim({ files: [file], value: '' }, []);
    const result = await upload.start('scan', key, [0], '/api/files/upload', fakeDotnet());

    assert.equal(result.status, 200);
});
