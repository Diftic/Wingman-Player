namespace wingman_player.UI;

using System.Windows.Input;
using Models.Keyboard;

/// <summary>
///     Maps WPF <see cref="Key"/> values to platform-independent <see cref="KeyboardKey"/> values.
/// </summary>
internal static class WpfKeyMapper
{
    internal static KeyboardKey ToKeyboardKey(Key key) => key switch
    {
        // Function
        Key.F1 => KeyboardKey.F1,   Key.F2 => KeyboardKey.F2,   Key.F3 => KeyboardKey.F3,
        Key.F4 => KeyboardKey.F4,   Key.F5 => KeyboardKey.F5,   Key.F6 => KeyboardKey.F6,
        Key.F7 => KeyboardKey.F7,   Key.F8 => KeyboardKey.F8,   Key.F9 => KeyboardKey.F9,
        Key.F10 => KeyboardKey.F10, Key.F11 => KeyboardKey.F11, Key.F12 => KeyboardKey.F12,
        Key.F13 => KeyboardKey.F13, Key.F14 => KeyboardKey.F14, Key.F15 => KeyboardKey.F15,
        Key.F16 => KeyboardKey.F16, Key.F17 => KeyboardKey.F17, Key.F18 => KeyboardKey.F18,
        Key.F19 => KeyboardKey.F19, Key.F20 => KeyboardKey.F20, Key.F21 => KeyboardKey.F21,
        Key.F22 => KeyboardKey.F22, Key.F23 => KeyboardKey.F23, Key.F24 => KeyboardKey.F24,

        // Modifiers
        Key.LeftCtrl  => KeyboardKey.ControlLeft,  Key.RightCtrl  => KeyboardKey.ControlRight,
        Key.LeftShift => KeyboardKey.ShiftLeft,    Key.RightShift => KeyboardKey.ShiftRight,
        Key.LeftAlt   => KeyboardKey.AltLeft,      Key.RightAlt   => KeyboardKey.AltRight,
        Key.LWin      => KeyboardKey.MetaLeft,     Key.RWin       => KeyboardKey.MetaRight,

        // Navigation
        Key.Left  => KeyboardKey.ArrowLeft,  Key.Right => KeyboardKey.ArrowRight,
        Key.Up    => KeyboardKey.ArrowUp,    Key.Down  => KeyboardKey.ArrowDown,
        Key.Home  => KeyboardKey.Home,       Key.End   => KeyboardKey.End,
        Key.PageUp => KeyboardKey.PageUp,   Key.PageDown => KeyboardKey.PageDown,

        // Editing
        Key.Back   => KeyboardKey.Backspace, Key.Delete => KeyboardKey.Delete,
        Key.Insert => KeyboardKey.Insert,    Key.Enter  => KeyboardKey.Enter,
        Key.Tab    => KeyboardKey.Tab,       Key.Escape => KeyboardKey.Escape,

        // Locks / system
        Key.CapsLock    => KeyboardKey.CapsLock,
        Key.NumLock     => KeyboardKey.NumLock,
        Key.Scroll      => KeyboardKey.ScrollLock,
        Key.PrintScreen => KeyboardKey.PrintScreen,
        Key.Pause       => KeyboardKey.Pause,
        Key.Apps        => KeyboardKey.ContextMenu,
        Key.Space       => KeyboardKey.Space,

        // Alpha
        Key.A => KeyboardKey.KeyA, Key.B => KeyboardKey.KeyB, Key.C => KeyboardKey.KeyC,
        Key.D => KeyboardKey.KeyD, Key.E => KeyboardKey.KeyE, Key.F => KeyboardKey.KeyF,
        Key.G => KeyboardKey.KeyG, Key.H => KeyboardKey.KeyH, Key.I => KeyboardKey.KeyI,
        Key.J => KeyboardKey.KeyJ, Key.K => KeyboardKey.KeyK, Key.L => KeyboardKey.KeyL,
        Key.M => KeyboardKey.KeyM, Key.N => KeyboardKey.KeyN, Key.O => KeyboardKey.KeyO,
        Key.P => KeyboardKey.KeyP, Key.Q => KeyboardKey.KeyQ, Key.R => KeyboardKey.KeyR,
        Key.S => KeyboardKey.KeyS, Key.T => KeyboardKey.KeyT, Key.U => KeyboardKey.KeyU,
        Key.V => KeyboardKey.KeyV, Key.W => KeyboardKey.KeyW, Key.X => KeyboardKey.KeyX,
        Key.Y => KeyboardKey.KeyY, Key.Z => KeyboardKey.KeyZ,

        // Digits
        Key.D0 => KeyboardKey.Digit0, Key.D1 => KeyboardKey.Digit1, Key.D2 => KeyboardKey.Digit2,
        Key.D3 => KeyboardKey.Digit3, Key.D4 => KeyboardKey.Digit4, Key.D5 => KeyboardKey.Digit5,
        Key.D6 => KeyboardKey.Digit6, Key.D7 => KeyboardKey.Digit7, Key.D8 => KeyboardKey.Digit8,
        Key.D9 => KeyboardKey.Digit9,

        // Numpad
        Key.NumPad0 => KeyboardKey.Numpad0, Key.NumPad1 => KeyboardKey.Numpad1,
        Key.NumPad2 => KeyboardKey.Numpad2, Key.NumPad3 => KeyboardKey.Numpad3,
        Key.NumPad4 => KeyboardKey.Numpad4, Key.NumPad5 => KeyboardKey.Numpad5,
        Key.NumPad6 => KeyboardKey.Numpad6, Key.NumPad7 => KeyboardKey.Numpad7,
        Key.NumPad8 => KeyboardKey.Numpad8, Key.NumPad9 => KeyboardKey.Numpad9,
        Key.Multiply => KeyboardKey.NumpadMultiply, Key.Add      => KeyboardKey.NumpadAdd,
        Key.Subtract => KeyboardKey.NumpadSubtract, Key.Decimal  => KeyboardKey.NumpadDecimal,
        Key.Divide   => KeyboardKey.NumpadDivide,

        // Symbols
        Key.OemSemicolon    => KeyboardKey.Semicolon,
        Key.OemPlus         => KeyboardKey.Equal,
        Key.OemComma        => KeyboardKey.Comma,
        Key.OemMinus        => KeyboardKey.Minus,
        Key.OemPeriod       => KeyboardKey.Period,
        Key.OemQuestion     => KeyboardKey.Slash,
        Key.OemTilde        => KeyboardKey.Backquote,
        Key.OemOpenBrackets => KeyboardKey.BracketLeft,
        Key.OemPipe         => KeyboardKey.Backslash,
        Key.OemCloseBrackets => KeyboardKey.BracketRight,
        Key.OemQuotes       => KeyboardKey.Quote,

        _ => KeyboardKey.Unknown,
    };
}
