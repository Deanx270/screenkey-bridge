/* Requests arrive only from the local ScreenKey Bridge native host. */
let port;
let retry;
function connect() {
  if (port) return;
  clearTimeout(retry);
  try {
    port = chrome.runtime.connectNative('com.screenkeybridge.page');
    port.onMessage.addListener(async message => {
      if (message.type !== 'capture') return;
      try {
        const win = await chrome.windows.getLastFocused({populate: true});
        if (!win.focused) throw new Error('Bring Microsoft Edge to the foreground on the PC.');
        const tab = win.tabs.find(t => t.active);
        if (!tab || !/^https?:\/\//i.test(tab.url || '')) throw new Error('Open a normal web page in the active Edge tab.');
        const [{result}] = await chrome.scripting.executeScript({target: {tabId: tab.id}, func: snapshot});
        if (!result || result.html.length > 8 * 1024 * 1024 || new TextEncoder().encode(JSON.stringify(result)).length > 12 * 1024 * 1024) throw new Error('The page is too large to save.');
        port.postMessage({id: message.id, title: result.title, html: result.html});
      } catch (e) { port.postMessage({id: message.id, error: String(e.message || e)}); }
    });
    port.onDisconnect.addListener(() => { port = null; retry = setTimeout(connect, 5000); });
    port.postMessage({type: 'ready'});
  } catch (_) { retry = setTimeout(connect, 5000); }
}

// Runs in the active page's isolated extension world, only on a phone request.
async function snapshot() {
  const root = document.documentElement.cloneNode(true);
  // Inline already-loaded images when the browser permits it; cross-origin images
  // can remain linked and may require a connection when opened on the phone.
  const originalsImages = document.querySelectorAll('img');
  const copiedImages = root.querySelectorAll('img');
  let embeddedBytes = 0;
  for (let i = 0; i < Math.min(originalsImages.length, 60); i++) {
    const image = originalsImages[i];
    if (!image.complete || !image.naturalWidth || image.naturalWidth * image.naturalHeight > 4000000) continue;
    try {
      const canvas = document.createElement('canvas'); canvas.width = image.naturalWidth; canvas.height = image.naturalHeight;
      canvas.getContext('2d').drawImage(image, 0, 0);
      const data = canvas.toDataURL('image/png');
      if (data.length > 1500000 || embeddedBytes + data.length > 4000000) continue;
      embeddedBytes += data.length; copiedImages[i].src = data; copiedImages[i].removeAttribute('srcset');
    } catch (_) { /* Browser refused cross-origin pixel access. */ }
  }
  const originals = document.querySelectorAll('input,textarea,select');
  const copies = root.querySelectorAll('input,textarea,select');
  for (let i = 0; i < copies.length; i++) {
    const src = originals[i], dst = copies[i];
    if (dst.localName === 'input') {
      if (['password', 'file', 'hidden'].includes((src.type || '').toLowerCase())) { dst.removeAttribute('value'); continue; }
      dst.setAttribute('value', src.value || '');
      if (src.checked) dst.setAttribute('checked', ''); else dst.removeAttribute('checked');
    } else if (dst.localName === 'textarea') dst.textContent = src.value;
    else for (let j = 0; j < dst.options.length; j++) {
      if (src.options[j]?.selected) dst.options[j].setAttribute('selected', ''); else dst.options[j].removeAttribute('selected');
    }
  }
  root.querySelectorAll('script,iframe,frame,object,embed,base,meta[http-equiv],link[rel="preload"],link[rel="prefetch"]').forEach(n => n.remove());
  for (const node of root.querySelectorAll('*')) {
    for (const attr of [...node.attributes]) {
      if (/^on/i.test(attr.name) || /^(action|formaction|srcdoc)$/i.test(attr.name) || /^\s*javascript:/i.test(attr.value)) node.removeAttribute(attr.name);
    }
  }
  const head = root.querySelector('head') || root.insertBefore(document.createElement('head'), root.firstChild);
  const base = document.createElement('base'); base.href = document.baseURI; head.prepend(base);
  const csp = document.createElement('meta'); csp.httpEquiv = 'Content-Security-Policy';
  csp.content = "default-src 'none'; img-src data: https: http:; style-src 'unsafe-inline' https: http:; font-src data: https: http:; media-src https: http:; base-uri https: http:; form-action 'none'";
  head.prepend(csp);
  const charset = document.createElement('meta'); charset.charset = 'utf-8'; head.prepend(charset);
  return {title: document.title.slice(0, 200), html: '<!doctype html>\n' + root.outerHTML};
}

chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
connect();
