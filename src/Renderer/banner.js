/* ============================================================
   PulseNet Banner — minimised "now playing" tile
   ============================================================ */

(function () {
  'use strict';

  var stationEl = document.getElementById('banner-station-name');
  var titleEl   = document.getElementById('banner-title');
  var hotkeyEl  = document.getElementById('banner-hotkey');

  // C# pushes updates via PostWebMessageAsJson from the host:
  //   { type: 'station', value: '...' }
  //   { type: 'title',   value: '...' }
  //   { type: 'hotkey',  value: 'F9'  }
  window.chrome.webview.addEventListener('message', function (e) {
    var data = e.data;
    if (typeof data !== 'object' || !data) return;
    switch (data.type) {
      case 'station':
        if (stationEl) stationEl.textContent = data.value || 'PulseNet Player';
        break;
      case 'title':
        if (titleEl) {
          var hasTitle = data.value && data.value.length;
          titleEl.textContent = hasTitle ? data.value : '';
          titleEl.style.display = hasTitle ? '' : 'none';
        }
        break;
      case 'hotkey':
        if (hotkeyEl) hotkeyEl.textContent = data.value || 'F8';
        break;
    }
  });

  // Forward left-button mousedown to the host so it can start a native window
  // drag (only effective when the host's edit-mode + unlocked state is on).
  document.body.addEventListener('mousedown', function (e) {
    if (e.button !== 0) return;
    try {
      window.chrome.webview.postMessage(JSON.stringify({ type: 'bannerDragStart' }));
    } catch (_) {}
    e.preventDefault();
  });

  // Tell the host we're ready so it can push the current state immediately
  // instead of waiting for the next track-poll tick.
  try {
    window.chrome.webview.postMessage(JSON.stringify({ type: 'banner-ready' }));
  } catch (_) {}
})();
