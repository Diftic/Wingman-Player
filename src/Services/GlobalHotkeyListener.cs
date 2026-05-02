namespace wingman_player.Services;

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PInvoke;
using Settings;
using WinPInvoke = Windows.Win32.PInvoke;

/// <summary>
///     Registers a low-level keyboard hook (WH_KEYBOARD_LL) and fires <see cref="HotkeyPressed"/>
///     whenever the configured hotkey combination is released.
/// </summary>
/// <remarks>
///     Reference: https://learn.microsoft.com/en-us/archive/blogs/toub/low-level-keyboard-hook-in-c
/// </remarks>
public sealed class GlobalHotkeyListener : IHostedService, IDisposable
{
    // CsWin32 returns a SafeHandle from SetWindowsHookEx; dispose it to unhook.
    private UnhookWindowsHookExSafeHandle? _hookHandle;

    // Keep a raw HHOOK for CallNextHookEx. Extracted from the SafeHandle after install.
    private HHOOK _hookId = HHOOK.Null;

    // Instance field — must not be static so multiple instances don't collide.
    private HOOKPROC? _proc;
    private Thread? _thread;
    private uint _threadId;

    // Keys currently held down, tracked via KEYDOWN/KEYUP events in the hook.
    private readonly HashSet<VIRTUAL_KEY> _heldKeys = [];

    private readonly SettingsManager _settings;
    private readonly ILogger<GlobalHotkeyListener> _logger;

    /// <summary>
    /// When true, all hotkey and escape events are silently suppressed.
    /// Set this while the settings window is open so key captures in the
    /// hotkey field don't accidentally toggle the overlay.
    /// </summary>
    public bool Paused { get; set; }

    public event EventHandler? HotkeyPressed;

    public GlobalHotkeyListener(SettingsManager settings, ILogger<GlobalHotkeyListener> logger)
    {
        _settings = settings;
        _logger = logger;
        _proc = HookProc;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(Run) { IsBackground = true };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // Post WM_QUIT to unblock GetMessage on the hook thread, then unhook.
        if (_threadId != 0)
            WinPInvoke.PostThreadMessage(_threadId, WinPInvoke.WM_QUIT, 0, 0);

        _hookHandle?.Dispose();
        _hookHandle = null;
        _hookId = HHOOK.Null;
    }

    private void Run()
    {
        _threadId = WinPInvoke.GetCurrentThreadId();
        SetHook();

        // The hook requires a message loop on this thread.
        while (WinPInvoke.GetMessage(out var msg, HWND.Null, 0, 0))
        {
            WinPInvoke.TranslateMessage(in msg);
            WinPInvoke.DispatchMessage(in msg);
        }
    }

    private void SetHook()
    {
        var handle = WinPInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _proc, default, 0);
        if (handle.IsInvalid)
        {
            _logger.LogWarning("Failed to install keyboard hook; error {Code}", Marshal.GetLastWin32Error());
            return;
        }

        _hookHandle = handle;
        _hookId = new HHOOK(handle.DangerousGetHandle());
        _logger.LogInformation("Keyboard hook installed");
    }

    private LRESULT HookProc(int nCode, WPARAM wparam, LPARAM lparam)
    {
        if (nCode >= 0)
            HandleKeyEvent(wparam, lparam);

        // Always pass to the next hook — never swallow keys so other apps (e.g. SC-HUD)
        // remain unaffected regardless of hook installation order.
        return WinPInvoke.CallNextHookEx(_hookId, nCode, wparam, lparam);
    }

    private void HandleKeyEvent(WPARAM wparam, LPARAM lparam)
    {
        if (Paused)
        {
            // Clear held-key tracking so stale state doesn't accumulate while paused.
            if (wparam == WinPInvoke.WM_KEYUP || wparam == WinPInvoke.WM_SYSKEYUP)
                _heldKeys.Clear();
            return;
        }

        var kbdStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lparam);
        var vk = (VIRTUAL_KEY)kbdStruct.vkCode;

        var isDown = wparam == WinPInvoke.WM_KEYDOWN || wparam == WinPInvoke.WM_SYSKEYDOWN;
        var isUp   = wparam == WinPInvoke.WM_KEYUP   || wparam == WinPInvoke.WM_SYSKEYUP;

        if (isDown)
        {
            // Prune phantom held keys before tracking the new key-down.
            // WM_KEYUP events are silently dropped during screen lock, UAC prompts,
            // and session switches — leaving stale entries in _heldKeys that cause
            // SetEquals to never match the hotkey again until the app restarts.
            _heldKeys.RemoveWhere(k => (WinPInvoke.GetAsyncKeyState((int)k) & 0x8000) == 0);
            _heldKeys.Add(vk);
            return;
        }

        if (!isUp) return;

        // Check for hotkey match BEFORE removing the released key — all keys were
        // still down at the moment the final key was released.
        var shortcutKeys = _settings.Current.ToggleHotkey.PressedKeys
            .Select(WindowsKeyMap.ToCode)
            .ToHashSet();

        if (shortcutKeys.Count > 0 && _heldKeys.SetEquals(shortcutKeys))
        {
            _heldKeys.Remove(vk);
            _logger.LogDebug("Hotkey pressed: {Hotkey}", _settings.Current.ToggleHotkey);
            // Grant any foreground-window right to our process before dispatching to the
            // UI thread.  This call must happen on the hook thread (while input is being
            // processed) — that is the only context where it is allowed by Windows.
            WinPInvoke.AllowSetForegroundWindow(0xFFFFFFFF); // ASFW_ANY
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _heldKeys.Remove(vk);
    }
}
