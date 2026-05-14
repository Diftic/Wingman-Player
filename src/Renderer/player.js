/* ============================================================
   Wingman Media Player — player controller
   ============================================================
   Playback is driven by the Wingman YouTube skill (TBD) via
   window.__wingmanLoad(videoId, playlistId), which the host can
   invoke through CoreWebView2.ExecuteScriptAsync.
*/

(function () {
  'use strict';

  // ---- State ----
  var player           = null;
  var playerReady      = false;
  var pendingVideoId   = null;
  var pendingPlaylist  = null;

  // ---- Idle / passive background ----
  var idleLogo = document.getElementById('idle-logo');

  function showIdleLogo() {
    if (idleLogo) idleLogo.classList.remove('hidden');
  }

  function hideIdleLogo() {
    if (idleLogo) idleLogo.classList.add('hidden');
  }

  function postCurrentStation(label) {
    try {
      window.chrome.webview.postMessage(JSON.stringify({ type: 'currentStation', label: label }));
    } catch (_) {}
  }

  // ---- Wingman skill entry point ----
  // Call from C# via CoreWebView2.ExecuteScriptAsync, e.g.:
  //   window.__wingmanLoad('dQw4w9WgXcQ');                                  // single video
  //   window.__wingmanLoad('dQw4w9WgXcQ', null, {startSeconds: 30});        // start at 30s
  //   window.__wingmanLoad(null, 'PLxxxxxxxxxx', {index: 2});               // playlist at index 2
  //   window.__wingmanLoad(null, null);                                     // back to idle
  var pendingOpts = null;
  window.__wingmanLoad = function (videoId, playlistId, opts) {
    opts = opts || {};
    if (!videoId && !playlistId) {
      showIdleLogo();
      lastReportedTitle = '';
      try {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'nowPlaying', title: '' }));
      } catch (_) {}
      if (player && playerReady) {
        try { player.stopVideo(); } catch (_) {}
      }
      return;
    }

    hideIdleLogo();
    lastReportedTitle = '';

    if (!playerReady || !player) {
      pendingVideoId  = videoId  || null;
      pendingPlaylist = playlistId || null;
      pendingOpts     = opts;
      return;
    }

    if (videoId) {
      var args = { videoId: videoId };
      if (opts.startSeconds != null) args.startSeconds = opts.startSeconds;
      if (opts.endSeconds   != null) args.endSeconds   = opts.endSeconds;
      player.loadVideoById(args);
    } else {
      var pargs = { listType: 'playlist', list: playlistId };
      if (opts.index        != null) pargs.index        = opts.index;
      if (opts.startSeconds != null) pargs.startSeconds = opts.startSeconds;
      player.loadPlaylist(pargs);
    }
    // Belt-and-suspenders against autoplay-policy edge cases: even though
    // loadVideoById/loadPlaylist *should* auto-start, some Edge/WebView2
    // versions hold the initial play. Chain a delayed playVideo() that's a
    // no-op if already playing but un-stalls a cued state. 250ms gives the
    // load call time to register before we nudge play.
    setTimeout(function () {
      try { player.playVideo(); } catch (_) {}
    }, 250);
  };

  // ---- Wingman transport hooks ----
  // Called from C# via CoreWebView2.ExecuteScriptAsync. Each is a no-op when
  // the YT.Player isn't ready (early launch / idle state); HTTP server
  // surfaces "not ready" via the bridge's null-result path → 503.
  window.__wingmanPlay     = function () { try { if (playerReady && player) player.playVideo();     } catch (_) {} };
  window.__wingmanPause    = function () { try { if (playerReady && player) player.pauseVideo();    } catch (_) {} };
  window.__wingmanStop     = function () { try { if (playerReady && player) player.stopVideo();     } catch (_) {} };
  window.__wingmanNext     = function () { try { if (playerReady && player) player.nextVideo();     } catch (_) {} };
  window.__wingmanPrevious = function () { try { if (playerReady && player) player.previousVideo(); } catch (_) {} };
  window.__wingmanSeek     = function (seconds) {
    try { if (playerReady && player) player.seekTo(Math.max(0, Number(seconds) || 0), true); } catch (_) {}
  };

  // Returns a snapshot the HTTP server hands back as JSON. State strings match
  // YT.PlayerState integers verbatim so the skill side doesn't need to look up
  // numeric codes.
  window.__wingmanGetState = function () {
    var fallback = { state: 'idle', videoId: null, title: null, currentTime: 0, duration: 0 };
    if (!playerReady || !player) return fallback;
    try {
      var stateNum   = player.getPlayerState();
      var stateNames = { '-1': 'unstarted', '0': 'ended', '1': 'playing', '2': 'paused', '3': 'buffering', '5': 'cued' };
      var data       = player.getVideoData() || {};
      return {
        state:       stateNames[String(stateNum)] || 'unknown',
        videoId:     data.video_id || null,
        title:       data.title    || null,
        currentTime: player.getCurrentTime() || 0,
        duration:    player.getDuration()    || 0,
      };
    } catch (_) {
      return fallback;
    }
  };

  // ---- Live stream / API player swap ----
  // Wingman drives playback by videoId or playlistId, both of which the
  // YouTube IFrame API handles. The legacy raw live_stream iframe path is
  // gone — the Wingman skill resolves the current broadcast videoId itself.

  function createApiPlayer() {
    var vars = {
      autoplay:       0,
      controls:       1,
      rel:            0,
      modestbranding: 1,
      iv_load_policy: 3,
      origin:         window.location.origin,
    };

    player = new YT.Player('player', {
      width:       '100%',
      height:      '100%',
      playerVars:  vars,
      events: {
        onReady: function () {
          playerReady = true;
          postKeyForwardCapable();
          if (pendingVideoId || pendingPlaylist) {
            window.__wingmanLoad(pendingVideoId, pendingPlaylist, pendingOpts || {});
            pendingVideoId  = null;
            pendingPlaylist = null;
            pendingOpts     = null;
          }
        },
        onStateChange: onPlayerStateChange,
        onError:       onPlayerError,
      },
    });
  }

  // ---- Key-forward capability push ----
  // C# only swallows allow-listed keys when forwarding will succeed.
  function postKeyForwardCapable() {
    var canForward = playerReady && !!player;
    try {
      window.chrome.webview.postMessage(JSON.stringify({ type: 'keyForwardCapable', value: canForward }));
    } catch (_) {}
  }

  // ---- Hover state push for keyboard hook gating ----
  // C#'s keyboard hook only forwards keys to the embed when the cursor is over
  // #video-wrap. Tracked here in the DOM so it works at any DPI / zoom.
  (function () {
    var wrap = document.getElementById('video-wrap');
    if (!wrap) return;
    function post(over) {
      try {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'hoverVideo', over: over }));
      } catch (_) {}
    }
    wrap.addEventListener('mouseenter', function () { post(true); });
    wrap.addEventListener('mouseleave', function () { post(false); });
  })();

  // ---- Hover hotkey forwarder ----
  // C# global keyboard hook intercepts a small allow-list of YouTube keys when
  // the cursor is over the video rect (without stealing focus) and calls this
  // function to drive the IFrame API directly.
  window.__wingmanForwardKey = function (action) {
    if (!playerReady || !player) return;
    try {
      switch (action) {
        case 'space':
          var st = player.getPlayerState();
          if (st === YT.PlayerState.PLAYING) player.pauseVideo();
          else player.playVideo();
          break;
        case 'mute':
          if (player.isMuted()) player.unMute();
          else player.mute();
          break;
        case 'left':
          player.seekTo(Math.max(0, player.getCurrentTime() - 5), true);
          break;
        case 'right':
          player.seekTo(player.getCurrentTime() + 5, true);
          break;
        case 'up':
          if (player.isMuted()) player.unMute();
          player.setVolume(Math.min(100, player.getVolume() + 5));
          break;
        case 'down':
          player.setVolume(Math.max(0, player.getVolume() - 5));
          break;
      }
    } catch (_) {}
  };

  // ---- Load YouTube IFrame API ----
  var tag = document.createElement('script');
  tag.src = 'https://www.youtube.com/iframe_api';
  document.head.appendChild(tag);

  window.onYouTubeIframeAPIReady = function () {
    createApiPlayer();
  };

  function onPlayerStateChange(event) {
    // Forward the YT.PlayerState to the C# host so OverlayWindow can drive
    // the idle-hide timer (state→ENDED starts it; state→PLAYING cancels it
    // and re-shows the overlay if it had been parked off-screen).
    try {
      window.chrome.webview.postMessage(JSON.stringify({
        type: 'playerState',
        state: event.data
      }));
    } catch (_) {}

    if (event.data === YT.PlayerState.PLAYING) {
      scheduleTrackUpdate();
    }
    if (event.data === YT.PlayerState.ENDED) {
      showIdleLogo();
    }
  }

  function onPlayerError(event) {
    console.warn('Wingman Player error:', event.data);
  }

  // Track title polling (API fires no title-change event).
  var lastReportedTitle = '';
  function updateTrackTitle() {
    if (!playerReady || !player) return;
    try {
      var data = player.getVideoData();
      if (data && data.title && data.title !== lastReportedTitle) {
        lastReportedTitle = data.title;
        window.__wingmanNowPlaying = data.title;
        try {
          window.chrome.webview.postMessage(JSON.stringify({ type: 'nowPlaying', title: data.title }));
        } catch (_) {}
      }
    } catch (_) {}
  }

  function scheduleTrackUpdate() {
    setTimeout(updateTrackTitle, 800);
    setTimeout(updateTrackTitle, 2500);
  }

  setInterval(function () {
    if (playerReady && player && player.getPlayerState() === YT.PlayerState.PLAYING) {
      updateTrackTitle();
    }
  }, 2000);

  // ---- Settings panel ----
  // Opacity slider maps 0–100% display → 30–100% actual (CSS opacity on html).
  var dragLocked     = false;
  var currentOpacity = 1.0;
  var currentZoom    = 100;

  var settingsBtn    = document.getElementById('settings-btn');
  var settingsPanel  = document.getElementById('settings-panel');
  var lockBtn        = document.getElementById('lock-btn');
  var opacitySlider  = document.getElementById('opacity-slider');
  var opacityVal     = document.getElementById('opacity-val');
  var zoomDownBtn    = document.getElementById('zoom-down-btn');
  var zoomUpBtn      = document.getElementById('zoom-up-btn');
  var zoomInput      = document.getElementById('zoom-input');
  var hotkeyInput    = document.getElementById('hotkey-input');
  var minimizeSelect = document.getElementById('minimize-mode-select');

  if (settingsBtn) {
    settingsBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      var miniplayerPanelEl = document.getElementById('miniplayer-settings-panel');
      if (miniplayerPanelEl && !miniplayerPanelEl.classList.contains('hidden')) {
        miniplayerPanelEl.classList.add('hidden');
        try {
          window.chrome.webview.postMessage(JSON.stringify({ type: 'bannerEditMode', value: false }));
        } catch (_) {}
      }
      var streamerPanelEl = document.getElementById('streamer-settings-panel');
      if (streamerPanelEl && !streamerPanelEl.classList.contains('hidden')) {
        streamerPanelEl.classList.add('hidden');
      }
      settingsPanel.classList.toggle('hidden');
    });
  }

  var discordBtn = document.getElementById('discord-btn');
  if (discordBtn) {
    discordBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      try {
        window.chrome.webview.postMessage(JSON.stringify({
          type: 'openUrl',
          url: 'https://discord.com/invite/shipbit-1173573578604687360',
        }));
      } catch (_) {}
    });
  }

  // Version is pushed in by C# (BuildSyncScript sets window.__wingmanVersion).
  var versionBtn = document.getElementById('version-btn');
  function versionLabel() {
    var v = window.__wingmanVersion || '0.0.0';
    return 'v' + v + '  ·  Check for updates';
  }
  window.__wingmanRefreshVersionLabel = function () {
    if (versionBtn && !versionBtn.disabled) versionBtn.textContent = versionLabel();
  };
  if (versionBtn) {
    versionBtn.textContent = versionLabel();
    versionBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      if (versionBtn.disabled) return;
      versionBtn.disabled = true;
      versionBtn.textContent = 'Checking for updates…';
      try {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'checkForUpdates' }));
      } catch (_) {
        versionBtn.disabled = false;
        versionBtn.textContent = versionLabel();
      }
    });
  }
  window.__wingmanUpdateCheckDone = function () {
    if (!versionBtn) return;
    versionBtn.disabled = false;
    versionBtn.textContent = versionLabel();
  };

  if (lockBtn) {
    lockBtn.addEventListener('click', function () {
      dragLocked = !dragLocked;
      lockBtn.textContent = dragLocked ? '🔒 Locked' : '🔓 Unlocked';
      lockBtn.classList.toggle('locked', dragLocked);
      if (settingsPanel) settingsPanel.classList.add('hidden');
      try {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'lock', locked: dragLocked }));
      } catch (_) {}
    });
  }

  if (opacitySlider) {
    opacitySlider.addEventListener('input', function () {
      var pct = parseInt(this.value, 10);
      opacityVal.textContent = pct + '%';
      currentOpacity = 0.30 + (pct / 100) * 0.70;
      try {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'opacity', value: currentOpacity }));
      } catch (_) {}
    });
  }

  function sendZoom(pct) {
    currentZoom = Math.min(100, Math.max(30, pct));
    if (zoomInput) zoomInput.value = currentZoom;
    try {
      window.chrome.webview.postMessage(JSON.stringify({ type: 'zoom', pct: currentZoom }));
    } catch (_) {}
  }

  function currentZoomValue() {
    if (zoomInput) {
      var v = parseInt(zoomInput.value, 10);
      if (!isNaN(v)) return v;
    }
    return currentZoom;
  }

  if (zoomDownBtn) {
    zoomDownBtn.addEventListener('click', function () { sendZoom(currentZoomValue() - 10); });
  }

  if (zoomUpBtn) {
    zoomUpBtn.addEventListener('click', function () { sendZoom(currentZoomValue() + 10); });
  }

  if (zoomInput) {
    zoomInput.addEventListener('change', function () {
      var val = parseInt(this.value, 10);
      if (isNaN(val)) val = currentZoom;
      sendZoom(val);
    });
  }

  // ---- Hotkey recorder ----
  var heldKeys = {};

  if (hotkeyInput) {
    hotkeyInput.addEventListener('focus', function () {
      heldKeys = {};
      hotkeyInput.value = '';
      try { window.chrome.webview.postMessage(JSON.stringify({ type: 'hotkey-focus', active: true })); } catch (_) {}
    });

    hotkeyInput.addEventListener('blur', function () {
      heldKeys = {};
      try { window.chrome.webview.postMessage(JSON.stringify({ type: 'hotkey-focus', active: false })); } catch (_) {}
    });

    hotkeyInput.addEventListener('keydown', function (e) {
      e.preventDefault();
      heldKeys[e.code] = true;
      hotkeyInput.value = Object.keys(heldKeys).join(' + ');
    });

    hotkeyInput.addEventListener('keyup', function (e) {
      e.preventDefault();
      delete heldKeys[e.code];
      if (Object.keys(heldKeys).length === 0) {
        var recorded = hotkeyInput.value;
        var keys = recorded ? recorded.split(' + ') : [];
        if (keys.length > 0) {
          try { window.chrome.webview.postMessage(JSON.stringify({ type: 'hotkey', keys: keys })); } catch (_) {}
        }
        hotkeyInput.blur();
      }
    });
  }

  if (minimizeSelect) {
    minimizeSelect.addEventListener('change', function () {
      try {
        window.chrome.webview.postMessage(JSON.stringify({
          type: 'minimizeMode',
          value: minimizeSelect.value,
        }));
      } catch (_) {}
    });
  }

  // ---- Miniplayer Settings sub-panel ----
  var miniplayerBtn        = document.getElementById('miniplayer-settings-btn');
  var miniplayerPanel      = document.getElementById('miniplayer-settings-panel');
  var miniplayerBackBtn    = document.getElementById('miniplayer-back-btn');
  var bannerLockBtn        = document.getElementById('banner-lock-btn');
  var bannerOpacitySlider  = document.getElementById('banner-opacity-slider');
  var bannerOpacityVal     = document.getElementById('banner-opacity-val');
  var bannerScaleInput     = document.getElementById('banner-scale-input');
  var bannerScaleDownBtn   = document.getElementById('banner-scale-down-btn');
  var bannerScaleUpBtn     = document.getElementById('banner-scale-up-btn');
  var bannerResetBtn       = document.getElementById('banner-reset-btn');

  var bannerLocked = true;

  function postBannerEdit(active) {
    try {
      window.chrome.webview.postMessage(JSON.stringify({ type: 'bannerEditMode', value: active }));
    } catch (_) {}
  }

  function updateBannerLockBtn() {
    if (!bannerLockBtn) return;
    bannerLockBtn.textContent = bannerLocked ? '🔒 Banner locked' : '🔓 Banner unlocked';
    bannerLockBtn.classList.toggle('locked', bannerLocked);
  }

  function showMiniplayerPanel() {
    if (settingsPanel) settingsPanel.classList.add('hidden');
    if (miniplayerPanel) miniplayerPanel.classList.remove('hidden');
    postBannerEdit(true);
  }

  function hideMiniplayerPanel() {
    if (miniplayerPanel) miniplayerPanel.classList.add('hidden');
    if (settingsPanel) settingsPanel.classList.remove('hidden');
    postBannerEdit(false);
  }

  if (miniplayerBtn) {
    miniplayerBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      showMiniplayerPanel();
    });
  }

  if (miniplayerBackBtn) {
    miniplayerBackBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      hideMiniplayerPanel();
    });
  }

  if (bannerLockBtn) {
    bannerLockBtn.addEventListener('click', function () {
      bannerLocked = !bannerLocked;
      updateBannerLockBtn();
      try {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'bannerLock', locked: bannerLocked }));
      } catch (_) {}
    });
  }

  if (bannerOpacitySlider) {
    bannerOpacitySlider.addEventListener('input', function () {
      var pct = parseInt(this.value, 10);
      if (bannerOpacityVal) bannerOpacityVal.textContent = pct + '%';
      var v = pct / 100;
      try {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'bannerOpacity', value: v }));
      } catch (_) {}
    });
  }

  function sendBannerScale(pct) {
    var clamped = Math.min(120, Math.max(20, pct));
    if (bannerScaleInput) bannerScaleInput.value = clamped;
    try {
      window.chrome.webview.postMessage(JSON.stringify({ type: 'bannerScale', value: clamped }));
    } catch (_) {}
  }

  function currentBannerScale() {
    if (bannerScaleInput) {
      var v = parseInt(bannerScaleInput.value, 10);
      if (!isNaN(v)) return v;
    }
    return 100;
  }

  if (bannerScaleDownBtn) {
    bannerScaleDownBtn.addEventListener('click', function () { sendBannerScale(currentBannerScale() - 10); });
  }
  if (bannerScaleUpBtn) {
    bannerScaleUpBtn.addEventListener('click', function () { sendBannerScale(currentBannerScale() + 10); });
  }
  if (bannerScaleInput) {
    bannerScaleInput.addEventListener('change', function () {
      var v = parseInt(this.value, 10);
      if (isNaN(v)) v = 100;
      sendBannerScale(v);
    });
  }

  if (bannerResetBtn) {
    bannerResetBtn.addEventListener('click', function () {
      try {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'bannerReset' }));
      } catch (_) {}
    });
  }

  // ---- Streamer Info sub-panel ----
  var streamerBtn       = document.getElementById('streamer-settings-btn');
  var streamerPanel     = document.getElementById('streamer-settings-panel');
  var streamerBackBtn   = document.getElementById('streamer-back-btn');
  var copyStreamUrlBtn  = document.getElementById('copy-stream-url-btn');

  function showStreamerPanel() {
    if (settingsPanel) settingsPanel.classList.add('hidden');
    if (streamerPanel) streamerPanel.classList.remove('hidden');
  }

  function hideStreamerPanel() {
    if (streamerPanel) streamerPanel.classList.add('hidden');
    if (settingsPanel) settingsPanel.classList.remove('hidden');
  }

  if (streamerBtn) {
    streamerBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      showStreamerPanel();
    });
  }

  if (streamerBackBtn) {
    streamerBackBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      hideStreamerPanel();
    });
  }

  if (copyStreamUrlBtn) {
    copyStreamUrlBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      var url = copyStreamUrlBtn.getAttribute('data-url') || '';
      var original = copyStreamUrlBtn.textContent;
      function flash(text) {
        copyStreamUrlBtn.textContent = text;
        setTimeout(function () { copyStreamUrlBtn.textContent = original; }, 1200);
      }
      try {
        navigator.clipboard.writeText(url).then(
          function () { flash('Copied!'); },
          function () { flash('Copy failed'); }
        );
      } catch (_) { flash('Copy failed'); }
    });
  }

  // Close panel on click outside
  document.addEventListener('click', function (e) {
    if (settingsPanel && !settingsPanel.classList.contains('hidden')) {
      if (!settingsPanel.contains(e.target) && e.target !== settingsBtn && !settingsBtn.contains(e.target)) {
        settingsPanel.classList.add('hidden');
      }
    }
    if (streamerPanel && !streamerPanel.classList.contains('hidden')) {
      if (!streamerPanel.contains(e.target) && e.target !== settingsBtn && !settingsBtn.contains(e.target)) {
        streamerPanel.classList.add('hidden');
      }
    }
  });

  // Frame drag — mousedown on non-interactive frame areas tells C# to start drag.
  document.addEventListener('mousedown', function (e) {
    var el = e.target;
    while (el && el !== document.documentElement) {
      if (
        el.tagName === 'BUTTON'        ||
        el.tagName === 'INPUT'         ||
        el.tagName === 'A'             ||
        el.id     === 'video-wrap'     ||
        el.id     === 'settings-panel'
      ) return;
      el = el.parentElement;
    }
    try {
      window.chrome.webview.postMessage(JSON.stringify({ type: 'startDrag' }));
    } catch (_) {}
  });

})();
