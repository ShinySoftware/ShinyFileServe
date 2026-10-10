// Copying a link. The async clipboard API exists only in a secure context - https or loopback -
// and this server is usually reached over plain http on a LAN address, where it is simply missing.
// The old execCommand path still works there from a click, so it is the fallback rather than an error.
export async function copy(text) {
    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Refused (permissions, focus) - fall through to the old way.
        }
    }

    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.top = '-1000px';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();

    let copied = false;
    try {
        copied = document.execCommand('copy');
    } catch {
        copied = false;
    }

    area.remove();
    return copied;
}
