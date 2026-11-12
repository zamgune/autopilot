// The player's hands: keys, mouse buttons, mouse movement, the mouse pointer and gamepad controls go in as devices would
// send them, so the game's own input code sees them, whichever input API it reads.
//
// With the Input System package: the bridge adds its own virtual keyboard, mouse and gamepad and queues state events
// on them (the game's actions, bindings, processors, and Keyboard.current / Mouse.current reads see them). Keys come as
// Unreal FKey names (W, SpaceBar, LeftShift, One, LeftMouseButton, MouseX, Gamepad_LeftX...) or Input System key names
// (w, space). The `input` op drives an action ("Player/Jump", or "Jump") by pressing what it is bound to on these
// devices.
//
// The legacy Input Manager (UnityEngine.Input) reads the operating system's own events, and nothing can be queued on
// it. Instead the package's IL post-processor (CodeGen/LegacyInputRewriter.cs) points the game's calls to
// UnityEngine.Input at LegacyInput, which adds what the bridge holds here (KeyCodes, joystick 1, the pointer). Both get
// every key: a game reading either API, or both, sees the same hands. hello and context say what reaches the game
// ("input").

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace Autopilot.Playtest
{
    public static class Hands
    {
        public static readonly bool InputSystemCompiled =
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            true;
#else
            false;
#endif

        public static readonly bool LegacyInputEnabled =
#if ENABLE_LEGACY_INPUT_MANAGER
            true;
#else
            false;
#endif

        /// <summary>What the bridge can drive, for hello and context.</summary>
        public static Dictionary<string, object> Capabilities()
        {
            var handling = InputSystemCompiled && LegacyInputEnabled ? "both" : InputSystemCompiled ? "input_system" : LegacyInputEnabled ? "input_manager" : "none";
            var rewritten = LegacyInput.Rewritten();
            var legacyCalls = rewritten.Sum(r => r.calls);
            var notes = new List<object>();
            if (legacyCalls > 0)
            {
                notes.Add($"The game's code reads UnityEngine.Input (the legacy Input Manager) in {legacyCalls} places ("
                          + string.Join(", ", rewritten.Select(r => r.name)) + "): those reads see the bridge's keys, mouse buttons, "
                          + "wheel, gamepad (as joystick 1) and pointer too. Point the mouse first (game.request(\"pointer\", "
                          + "{\"screen_x\", \"screen_y\"} | {\"world\": point} | {\"object\": path})), then press LeftMouseButton or "
                          + "RightMouseButton there; hold it and point again to drag.");
                if (!LegacyInputEnabled)
                    notes.Add("Active Input Handling is Input System only, so UnityEngine.Input itself is off: during a run the game's "
                              + "UnityEngine.Input reads see only the bridge's input (outside a run they throw, as before).");
            }
            else if (!InputSystemCompiled)
            {
                notes.Add("The game reads the legacy Input Manager only (Active Input Handling: Input Manager), and none of its "
                          + "code was routed through the bridge: keys, mouse and actions can't reach it. Drive the game with rc "
                          + "calls, move (it moves the player's CharacterController) and UI clicks.");
            }
            else if (LegacyInputEnabled)
            {
                notes.Add("Active Input Handling is Both: keys reach code that reads the Input System (actions, Keyboard.current); "
                          + "no code of the game's that reads UnityEngine.Input was found routed through the bridge.");
            }
            return new Dictionary<string, object>
            {
                ["handling"] = handling,
                ["keys"] = InputSystemCompiled || legacyCalls > 0,
                ["actions"] = InputSystemCompiled,
                ["legacy_input_manager_injectable"] = legacyCalls > 0,
                ["legacy_input_reads"] = legacyCalls,
                ["pointer"] = true,
                ["move"] = InputSystemCompiled ? "input" : "controller",
                ["notes"] = notes,
            };
        }

        // -- controls -----------------------------------------------------------------------------------------

        enum Kind
        {
            Key,
            MouseButton,
            MouseX,
            MouseY,
            Scroll,
            PadButton,
            LeftX,
            LeftY,
            RightX,
            RightY,
            LeftTrigger,
            RightTrigger,
        }

        enum Pad
        {
            South,
            East,
            West,
            North,
            LeftShoulder,
            RightShoulder,
            Select,
            Start,
            LeftStick,
            RightStick,
            DpadUp,
            DpadDown,
            DpadLeft,
            DpadRight,
        }

        struct Control
        {
            public Kind Kind;
            public KeyCode Code;  // a key's or a mouse button's (Mouse0 left, 1 right, 2 middle, 3 back, 4 forward)
            public Pad Pad;
            public float Sign;  // for stick parts bound as buttons (leftStick/up): +1 or -1
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            public Key Key;  // a key's on the Input System's keyboard (some have no KeyCode: Code is None)

            string KeyName => Key.ToString();
#else
            string KeyName => Code.ToString();
#endif

            public bool IsAxis => Kind == Kind.MouseX || Kind == Kind.MouseY || Kind == Kind.Scroll || Kind == Kind.LeftX
                                  || Kind == Kind.LeftY || Kind == Kind.RightX || Kind == Kind.RightY || Kind == Kind.LeftTrigger
                                  || Kind == Kind.RightTrigger;

            public bool IsMouseMove => Kind == Kind.MouseX || Kind == Kind.MouseY || Kind == Kind.Scroll;

            public string Id => Kind == Kind.Key ? "key:" + KeyName : Kind == Kind.MouseButton ? "mouse:" + Code : Kind == Kind.PadButton ? "pad:" + Pad : Kind.ToString();
        }

        static Control KeyControl(KeyCode code)
        {
            var c = new Control { Kind = Kind.Key, Code = code };
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            c.Key = KeyOf(code);
#endif
            return c;
        }

        static Control Mouse(int button) => new Control { Kind = Kind.MouseButton, Code = KeyCode.Mouse0 + button };
        static Control PadButton(Pad pad) => new Control { Kind = Kind.PadButton, Pad = pad };
        static Control Axis(Kind kind, float sign = 0) => new Control { Kind = kind, Sign = sign };

        static readonly Dictionary<string, Control> fkeys = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> fkeyOf = new Dictionary<string, string>();

        static void K(string name, KeyCode code) => C(name, KeyControl(code));

        static void C(string name, Control c)
        {
            fkeys[name] = c;
            if (!fkeyOf.ContainsKey(c.Id)) fkeyOf[c.Id] = name;
        }

        static Hands()
        {
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            KeyMaps();
#endif
            for (var c = 'A'; c <= 'Z'; ++c) K(c.ToString(), KeyCode.A + (c - 'A'));
            string[] digits = { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" };
            for (var i = 0; i < 10; ++i)
            {
                K(digits[i], KeyCode.Alpha0 + i);
                K("NumPad" + digits[i], KeyCode.Keypad0 + i);
            }
            for (var i = 1; i <= 12; ++i) K("F" + i, KeyCode.F1 + (i - 1));
            K("SpaceBar", KeyCode.Space);
            K("Enter", KeyCode.Return);
            K("Tab", KeyCode.Tab);
            K("Escape", KeyCode.Escape);
            K("BackSpace", KeyCode.Backspace);
            K("Delete", KeyCode.Delete);
            K("Insert", KeyCode.Insert);
            K("Home", KeyCode.Home);
            K("End", KeyCode.End);
            K("PageUp", KeyCode.PageUp);
            K("PageDown", KeyCode.PageDown);
            K("CapsLock", KeyCode.CapsLock);
            K("LeftShift", KeyCode.LeftShift);
            K("RightShift", KeyCode.RightShift);
            K("LeftControl", KeyCode.LeftControl);
            K("RightControl", KeyCode.RightControl);
            K("LeftAlt", KeyCode.LeftAlt);
            K("RightAlt", KeyCode.RightAlt);
            K("LeftCommand", KeyCode.LeftCommand);
            K("RightCommand", KeyCode.RightCommand);
            K("Up", KeyCode.UpArrow);
            K("Down", KeyCode.DownArrow);
            K("Left", KeyCode.LeftArrow);
            K("Right", KeyCode.RightArrow);
            K("Tilde", KeyCode.BackQuote);
            K("Hyphen", KeyCode.Minus);
            K("Equals", KeyCode.Equals);
            K("LeftBracket", KeyCode.LeftBracket);
            K("RightBracket", KeyCode.RightBracket);
            K("Backslash", KeyCode.Backslash);
            K("Semicolon", KeyCode.Semicolon);
            K("Apostrophe", KeyCode.Quote);
            K("Comma", KeyCode.Comma);
            K("Period", KeyCode.Period);
            K("Slash", KeyCode.Slash);
            K("Add", KeyCode.KeypadPlus);
            K("Subtract", KeyCode.KeypadMinus);
            K("Multiply", KeyCode.KeypadMultiply);
            K("Divide", KeyCode.KeypadDivide);
            K("Decimal", KeyCode.KeypadPeriod);
            K("Pause", KeyCode.Pause);
            K("ScrollLock", KeyCode.ScrollLock);
            K("NumLock", KeyCode.Numlock);
            C("LeftMouseButton", Mouse(0));
            C("RightMouseButton", Mouse(1));
            C("MiddleMouseButton", Mouse(2));
            C("ThumbMouseButton", Mouse(3));
            C("ThumbMouseButton2", Mouse(4));
            C("MouseX", Axis(Kind.MouseX));
            C("MouseY", Axis(Kind.MouseY));
            C("MouseWheelAxis", Axis(Kind.Scroll));
            C("Gamepad_FaceButton_Bottom", PadButton(Pad.South));
            C("Gamepad_FaceButton_Right", PadButton(Pad.East));
            C("Gamepad_FaceButton_Left", PadButton(Pad.West));
            C("Gamepad_FaceButton_Top", PadButton(Pad.North));
            C("Gamepad_LeftShoulder", PadButton(Pad.LeftShoulder));
            C("Gamepad_RightShoulder", PadButton(Pad.RightShoulder));
            C("Gamepad_Special_Left", PadButton(Pad.Select));
            C("Gamepad_Special_Right", PadButton(Pad.Start));
            C("Gamepad_LeftThumbstick", PadButton(Pad.LeftStick));
            C("Gamepad_RightThumbstick", PadButton(Pad.RightStick));
            C("Gamepad_DPad_Up", PadButton(Pad.DpadUp));
            C("Gamepad_DPad_Down", PadButton(Pad.DpadDown));
            C("Gamepad_DPad_Left", PadButton(Pad.DpadLeft));
            C("Gamepad_DPad_Right", PadButton(Pad.DpadRight));
            C("Gamepad_LeftX", Axis(Kind.LeftX));
            C("Gamepad_LeftY", Axis(Kind.LeftY));
            C("Gamepad_RightX", Axis(Kind.RightX));
            C("Gamepad_RightY", Axis(Kind.RightY));
            C("Gamepad_LeftTriggerAxis", Axis(Kind.LeftTrigger));
            C("Gamepad_RightTriggerAxis", Axis(Kind.RightTrigger));
            C("Gamepad_LeftTrigger", Axis(Kind.LeftTrigger));
            C("Gamepad_RightTrigger", Axis(Kind.RightTrigger));
        }

        // -- the legacy Input Manager's side (LegacyInput): KeyCodes, joystick 1 as an Xbox pad on Windows ---------

        static readonly bool[] dpad = new bool[4];  // up, down, left, right
        static float leftTrigger, rightTrigger;

        static void ToLegacy(Control c, float value, float stick)
        {
            switch (c.Kind)
            {
                case Kind.Key:
                case Kind.MouseButton:
                    LegacyInput.Key(c.Code, value > 0.5f);
                    break;
                case Kind.MouseX:
                    LegacyInput.MouseDelta(new Vector2(value, 0));
                    break;
                case Kind.MouseY:
                    LegacyInput.MouseDelta(new Vector2(0, value));
                    break;
                case Kind.Scroll:
                    LegacyInput.Scroll(value);
                    break;
                case Kind.PadButton:
                    if (c.Pad >= Pad.DpadUp)
                    {
                        dpad[c.Pad - Pad.DpadUp] = value > 0.5f;
                        LegacyInput.PadAxis(5, (dpad[3] ? 1 : 0) - (dpad[2] ? 1 : 0));  // (the 6th axis: right +)
                        LegacyInput.PadAxis(6, (dpad[0] ? 1 : 0) - (dpad[1] ? 1 : 0));  // (the 7th: up +)
                    }
                    else
                    {
                        LegacyInput.PadButton((int)c.Pad, value > 0.5f);  // (A 0, B 1, X 2, Y 3, LB 4, RB 5, Back 6, Start 7, LS 8, RS 9)
                    }
                    break;
                case Kind.LeftX:
                    LegacyInput.PadAxis(0, stick);
                    break;
                case Kind.LeftY:
                    LegacyInput.PadAxis(1, -stick);  // (the Y axis: down +)
                    break;
                case Kind.RightX:
                    LegacyInput.PadAxis(3, stick);
                    break;
                case Kind.RightY:
                    LegacyInput.PadAxis(4, -stick);
                    break;
                case Kind.LeftTrigger:
                case Kind.RightTrigger:
                    if (c.Kind == Kind.LeftTrigger) leftTrigger = Mathf.Clamp01(value);
                    else rightTrigger = Mathf.Clamp01(value);
                    LegacyInput.PadAxis(2, leftTrigger - rightTrigger);  // (the 3rd axis: both triggers)
                    LegacyInput.PadAxis(8, leftTrigger);
                    LegacyInput.PadAxis(9, rightTrigger);
                    break;
            }
        }

        static void Set(Control c, float value)
        {
            var stick = Mathf.Clamp(value * (c.Sign == 0 ? 1 : c.Sign), -1, 1);
            ToLegacy(c, value, stick);
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            switch (c.Kind)
            {
                case Kind.Key:
                    if (c.Key == UnityEngine.InputSystem.Key.None) break;
                    keyState.Set(c.Key, value > 0.5f);
                    keyDirty = true;
                    break;
                case Kind.MouseButton:
                    mouseState = mouseState.WithButton(MouseOf(c.Code), value > 0.5f);
                    mouseDirty = true;
                    break;
                case Kind.MouseX:
                    mouseDelta.x += value;
                    mouseDirty = true;
                    break;
                case Kind.MouseY:
                    mouseDelta.y += value;
                    mouseDirty = true;
                    break;
                case Kind.Scroll:
                    scrollDelta.y += value * 120f;
                    mouseDirty = true;
                    break;
                case Kind.PadButton:
                    padState = padState.WithButton(GamepadOf(c.Pad), value > 0.5f);
                    padDirty = true;
                    break;
                case Kind.LeftX:
                    padState.leftStick.x = stick;
                    padDirty = true;
                    break;
                case Kind.LeftY:
                    padState.leftStick.y = stick;
                    padDirty = true;
                    break;
                case Kind.RightX:
                    padState.rightStick.x = stick;
                    padDirty = true;
                    break;
                case Kind.RightY:
                    padState.rightStick.y = stick;
                    padDirty = true;
                    break;
                case Kind.LeftTrigger:
                    padState.leftTrigger = Mathf.Clamp01(value);
                    padDirty = true;
                    break;
                case Kind.RightTrigger:
                    padState.rightTrigger = Mathf.Clamp01(value);
                    padDirty = true;
                    break;
            }
#endif
        }

        static void SetPointer(Vector2 pixels)
        {
            LegacyInput.Pointer(pixels);
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            EnsureDevices();
            mouseState.position = pixels;
            mouseDirty = true;
#endif
        }

        // -- the devices and their state (the Input System's side) -------------------------------------------

#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        static Keyboard keyboard;
        static UnityEngine.InputSystem.Mouse mouse;
        static Gamepad gamepad;
        static KeyboardState keyState;
        static MouseState mouseState;
        static GamepadState padState;
        static bool keyDirty, mouseDirty, padDirty;
        static Vector2 mouseDelta, scrollDelta;
        static bool passthrough;

        /// <summary>Add the bridge's devices (once; again after the Input System lost them).</summary>
        public static void EnsureDevices()
        {
            // (the editor keeps devices across a script reload: the bridge's own are found again by name)
            if (keyboard == null || !keyboard.added)
            {
                keyboard = Existing<Keyboard>("AutopilotPlaytestKeyboard") ?? InputSystem.AddDevice<Keyboard>("AutopilotPlaytestKeyboard");
                keyState = new KeyboardState();
                keyDirty = true;
            }
            if (mouse == null || !mouse.added)
            {
                mouse = Existing<UnityEngine.InputSystem.Mouse>("AutopilotPlaytestMouse") ?? InputSystem.AddDevice<UnityEngine.InputSystem.Mouse>("AutopilotPlaytestMouse");
                mouseState = new MouseState { position = LegacyInput.PointerNow };
                mouseDirty = true;
            }
            if (gamepad == null || !gamepad.added)
            {
                gamepad = Existing<Gamepad>("AutopilotPlaytestGamepad") ?? InputSystem.AddDevice<Gamepad>("AutopilotPlaytestGamepad");
                padState = new GamepadState();
                padDirty = true;
            }
#if UNITY_EDITOR
            // Keyboard and mouse events reach the game in the editor only while the Game view has focus (the Input
            // System's play-mode setting): the brain's do whatever has focus
            if (!passthrough)
            {
                var manager = typeof(InputSystem).Assembly.GetType("UnityEngine.InputSystem.InputManager");
                var start = manager?.GetMethod("StartEditorEventPassthrough", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (start != null)
                {
                    start.Invoke(null, null);
                    passthrough = true;
                }
            }
#endif
            if (!Application.runInBackground && Application.isPlaying && !Application.isEditor)
                Application.runInBackground = true;  // (a player without focus would drop the events)
        }

        static T Existing<T>(string name) where T : InputDevice
        {
            foreach (var d in InputSystem.devices)
                if (d is T t && d.name.StartsWith(name, StringComparison.Ordinal))
                    return t;
            return null;
        }

        public static void RemoveDevices()
        {
            IsolateDevices(false);
            ReleaseAll();
            Flush();
            if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
            if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
            if (gamepad != null && gamepad.added) InputSystem.RemoveDevice(gamepad);
            keyboard = null;
            mouse = null;
            gamepad = null;
#if UNITY_EDITOR
            if (passthrough)
            {
                var manager = typeof(InputSystem).Assembly.GetType("UnityEngine.InputSystem.InputManager");
                manager?.GetMethod("StopEditorEventPassthrough", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(null, null);
                passthrough = false;
            }
#endif
        }

        /// <summary>Queue what changed this frame as state events (the Input System takes them at its next update).</summary>
        public static void Flush()
        {
            if (keyboard == null) return;
            if (keyDirty && keyboard.added)
                InputSystem.QueueStateEvent(keyboard, keyState);
            if (mouseDirty && mouse != null && mouse.added)
            {
                var s = mouseState;
                s.delta = mouseDelta;
                s.scroll = scrollDelta;
                InputSystem.QueueStateEvent(mouse, s);
            }
            if (padDirty && gamepad != null && gamepad.added)
                InputSystem.QueueStateEvent(gamepad, padState);
            keyDirty = mouseDirty = padDirty = false;
            mouseDelta = scrollDelta = Vector2.zero;
        }

        // KeyCode <-> Key: the same name, but for these
        static readonly Dictionary<KeyCode, Key> keyOf = new Dictionary<KeyCode, Key>();
        static readonly Dictionary<Key, KeyCode> codeOf = new Dictionary<Key, KeyCode>();

        static void KeyMaps()
        {
            void Pair(KeyCode code, Key key)
            {
                if (!keyOf.ContainsKey(code)) keyOf[code] = key;
                if (!codeOf.ContainsKey(key)) codeOf[key] = code;
            }
            for (var i = 0; i < 10; ++i)
            {
                Pair(KeyCode.Alpha0 + i, Key.Digit0 + i);
                Pair(KeyCode.Keypad0 + i, Key.Numpad0 + i);
            }
            Pair(KeyCode.Return, Key.Enter);
            Pair(KeyCode.BackQuote, Key.Backquote);
            Pair(KeyCode.LeftControl, Key.LeftCtrl);
            Pair(KeyCode.RightControl, Key.RightCtrl);
            Pair(KeyCode.LeftCommand, Key.LeftMeta);
            Pair(KeyCode.RightCommand, Key.RightMeta);
            Pair(KeyCode.Numlock, Key.NumLock);
            Pair(KeyCode.Print, Key.PrintScreen);
            Pair(KeyCode.Menu, Key.ContextMenu);
            Pair(KeyCode.KeypadEnter, Key.NumpadEnter);
            Pair(KeyCode.KeypadDivide, Key.NumpadDivide);
            Pair(KeyCode.KeypadMultiply, Key.NumpadMultiply);
            Pair(KeyCode.KeypadMinus, Key.NumpadMinus);
            Pair(KeyCode.KeypadPlus, Key.NumpadPlus);
            Pair(KeyCode.KeypadPeriod, Key.NumpadPeriod);
            Pair(KeyCode.KeypadEquals, Key.NumpadEquals);
            foreach (var name in Enum.GetNames(typeof(Key)))
            {
                var key = (Key)Enum.Parse(typeof(Key), name);
                if (key == Key.None || codeOf.ContainsKey(key)) continue;
                if (Enum.IsDefined(typeof(KeyCode), name)) Pair((KeyCode)Enum.Parse(typeof(KeyCode), name), key);
            }
        }

        static Key KeyOf(KeyCode code) => keyOf.TryGetValue(code, out var key) ? key : Key.None;
        static KeyCode CodeOf(Key key) => codeOf.TryGetValue(key, out var code) ? code : KeyCode.None;

        static Control KeyControl(Key key) => new Control { Kind = Kind.Key, Key = key, Code = CodeOf(key) };

        static MouseButton MouseOf(KeyCode code)
        {
            switch (code)
            {
                case KeyCode.Mouse1: return MouseButton.Right;
                case KeyCode.Mouse2: return MouseButton.Middle;
                case KeyCode.Mouse3: return MouseButton.Back;
                case KeyCode.Mouse4: return MouseButton.Forward;
                default: return MouseButton.Left;
            }
        }

        static readonly GamepadButton[] gamepadOf =
        {
            GamepadButton.South, GamepadButton.East, GamepadButton.West, GamepadButton.North, GamepadButton.LeftShoulder,
            GamepadButton.RightShoulder, GamepadButton.Select, GamepadButton.Start, GamepadButton.LeftStick,
            GamepadButton.RightStick, GamepadButton.DpadUp, GamepadButton.DpadDown, GamepadButton.DpadLeft, GamepadButton.DpadRight,
        };

        static GamepadButton GamepadOf(Pad pad) => gamepadOf[(int)pad];

        // -- a run's input is the bridge's alone --------------------------------------------------------------
        // While a run goes (Tick), the person's own keyboards, mice, pens, touchscreens, gamepads and joysticks (the
        // native ones: a game's on-screen controls and the bridge's devices aren't) are disabled, so what they press in
        // the editor, or a controller in their hands, doesn't reach the game and mix with the bridge's input. The
        // editor's own windows read the operating system's events, not the Input System, so the editor stays usable,
        // Stop included. A PlayerInput paired with a disabled device is paired with the bridge's devices instead, in
        // the same control scheme. In the editor the run also gets a copy of the Input System's settings in which all
        // input reaches the game whatever has focus: by default keyboard and pointer input reach it only while the Game
        // view has focus (such devices are even disabled while it hasn't), and during a run the focus is elsewhere, in
        // Aura say. The copy isn't an asset, so the project's settings are never written. All of it is put back when
        // the run ends or play mode does. What was disabled is kept in the editor's session too: a script reload in a
        // run loses this class's state, but not the devices' (nor the settings copy, which the Input System keeps).

        const string IsolationKey = "Autopilot.Playtest.Isolation";
        const string RunSettingsName = "Autopilot run input settings";
        static readonly List<int> disabledForRun = new List<int>();  // device ids
        static readonly Dictionary<PlayerInput, (string scheme, InputDevice[] devices)> pairedBeforeRun =
            new Dictionary<PlayerInput, (string scheme, InputDevice[] devices)>();
        static InputSettings settingsBeforeRun;
        static bool isolated, routed, isolationLoaded;

        static bool PersonsDevice(InputDevice d) =>
            d.native && (d is Keyboard || d is UnityEngine.InputSystem.Pointer || d is Gamepad || d is Joystick);

        /// <summary>On (every frame of a run): the game gets the bridge's input alone. Off: put back what that changed.</summary>
        internal static void IsolateDevices(bool on)
        {
            if (!isolationLoaded) LoadIsolation();
            if (on)
            {
                RouteAllInputToGame();
                var devices = InputSystem.devices;
                for (var i = 0; i < devices.Count; ++i)
                {
                    var d = devices[i];
                    if (!d.enabled || !PersonsDevice(d)) continue;
                    InputSystem.DisableDevice(d);
                    if (!disabledForRun.Contains(d.deviceId)) disabledForRun.Add(d.deviceId);
                    isolated = true;
                    SaveIsolation();
                }
                foreach (var mine in new InputDevice[] { keyboard, mouse, gamepad })
                    if (mine != null && mine.added && !mine.enabled)
                        InputSystem.EnableDevice(mine);  // (the Input System disables a keyboard while the Game view lacks focus)
                PairWithBridge();
                return;
            }
            if (!isolated) return;
            isolated = false;
            foreach (var id in disabledForRun)
            {
                var d = InputSystem.GetDeviceById(id);
                if (d != null && !d.enabled) InputSystem.EnableDevice(d);
            }
            disabledForRun.Clear();
            RestorePairing();
            RestoreSettings();
            SaveIsolation();
        }

        /// <summary>Whether the game gets the bridge's input alone now (IsolateDevices), for tests.</summary>
        internal static bool Isolating => isolated;

        // (the PlayerInputs that were paired with a device disabled for the run take the bridge's of the same kind)
        static void PairWithBridge()
        {
            foreach (var p in PlayerInput.all)
            {
                var user = p.user;
                if (!user.valid || pairedBeforeRun.ContainsKey(p)) continue;
                var paired = user.pairedDevices.ToArray();
                if (!paired.Any(d => disabledForRun.Contains(d.deviceId))) continue;
                var mine = new List<InputDevice>();
                foreach (var d in paired)
                {
                    var swap = d is Keyboard ? keyboard
                        : d is UnityEngine.InputSystem.Mouse ? mouse
                        : d is Gamepad ? gamepad
                        : disabledForRun.Contains(d.deviceId) ? null : d;
                    if (swap != null && !mine.Contains(swap)) mine.Add(swap);
                }
                if (mine.Count == 0) continue;
                pairedBeforeRun[p] = (p.currentControlScheme, paired);
                isolated = true;
                Pair(p, p.currentControlScheme, mine.ToArray());
            }
        }

        static void RestorePairing()
        {
            foreach (var pair in pairedBeforeRun)
            {
                var p = pair.Key;
                if (p == null || !p.user.valid) continue;  // (destroyed with its scene)
                var devices = pair.Value.devices.Where(d => d.added).ToArray();
                if (devices.Length > 0) Pair(p, pair.Value.scheme, devices);
            }
            pairedBeforeRun.Clear();
        }

        static void Pair(PlayerInput p, string scheme, InputDevice[] devices)
        {
            if (!string.IsNullOrEmpty(scheme))
            {
                p.SwitchCurrentControlScheme(scheme, devices);
                return;
            }
            // (actions without control schemes)
            p.user.UnpairDevices();
            foreach (var d in devices) UnityEngine.InputSystem.Users.InputUser.PerformPairingWithDevice(d, p.user);
        }

        static void RouteAllInputToGame()
        {
#if UNITY_EDITOR
            var current = InputSystem.settings;
            if (current.editorInputBehaviorInPlayMode == InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView
                && current.backgroundBehavior == InputSettings.BackgroundBehavior.IgnoreFocus)
                return;
            var copy = UnityEngine.Object.Instantiate(current);
            copy.name = RunSettingsName;
            copy.hideFlags = HideFlags.HideAndDontSave;
            copy.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            copy.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            settingsBeforeRun = current;
            routed = isolated = true;
            SaveIsolation();
            InputSystem.settings = copy;
#endif
        }

        static void RestoreSettings()
        {
#if UNITY_EDITOR
            if (!routed) return;
            routed = false;
            var copy = InputSystem.settings;
            var original = settingsBeforeRun;
            settingsBeforeRun = null;
            if (original == null)
            {
                // (a script reload in the run: the project's settings asset, else the defaults, as the Input System loads them)
                if (!UnityEditor.EditorBuildSettings.TryGetConfigObject("com.unity.input.settings", out original) || original == null)
                {
                    original = ScriptableObject.CreateInstance<InputSettings>();
                    original.hideFlags = HideFlags.HideAndDontSave;
                }
            }
            InputSystem.settings = original;
            if (copy != null && copy != original && copy.name == RunSettingsName) UnityEngine.Object.DestroyImmediate(copy);
#endif
        }

        static void SaveIsolation()
        {
#if UNITY_EDITOR
            if (!isolated) UnityEditor.SessionState.EraseString(IsolationKey);
            else UnityEditor.SessionState.SetString(IsolationKey, (routed ? "routed;" : ";") + string.Join(",", disabledForRun));
#endif
        }

        static void LoadIsolation()
        {
            isolationLoaded = true;
#if UNITY_EDITOR
            var saved = UnityEditor.SessionState.GetString(IsolationKey, "");
            if (saved.Length == 0) return;
            var parts = saved.Split(';');
            routed = parts[0] == "routed";
            foreach (var id in parts.Length > 1 ? parts[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) : new string[0])
                if (int.TryParse(id, out var n) && !disabledForRun.Contains(n))
                    disabledForRun.Add(n);
            isolated = true;
#endif
        }

#if UNITY_EDITOR
        // (leaving play mode in a run puts everything back before the Input System restores its own settings)
        [UnityEditor.InitializeOnLoadMethod]
        static void PutBackOnExitingPlayMode() =>
            UnityEditor.EditorApplication.playModeStateChanged += state =>
            {
                if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) IsolateDevices(false);
            };
#endif
#else
        public static void EnsureDevices() { }
        public static void RemoveDevices() => ReleaseAll();
        public static void Flush() { }
        internal static void IsolateDevices(bool on) { }
        internal static bool Isolating => false;
#endif

        // -- held keys (leased) ------------------------------------------------------------------------------

        sealed class Held
        {
            public Control Control;
            public float Value = 1;
            public bool Rate;
            public double Renewed;
            public string Owner;  // "key" (the brain's), "input:<action>", "move"
            public bool Leased;  // let go when not renewed (the key op's holds, actions held through it too)
            public double Until;  // a timed hold: let go at this game time (Time.timeAsDouble), needing no renewal; 0: none
        }

        static readonly Dictionary<string, Held> held = new Dictionary<string, Held>();
        static double leaseSeconds = 1.0;
        static bool wasConnected;

        static double Now => PlaytestClient.Now;

        static void Hold(Control c, float value, bool rate, string owner)
        {
            var id = c.Id + "#" + owner;
            if (!held.TryGetValue(id, out var h))
            {
                h = new Held { Control = c, Owner = owner };
                held[id] = h;
                if (!c.IsAxis) Set(c, 1);
            }
            h.Value = value;
            h.Rate = rate;
            h.Renewed = Now;
            if (c.IsAxis && !rate && !c.IsMouseMove)
                Set(c, value);  // a stick or trigger stays where it is put
        }

        static void Release(string id)
        {
            if (!held.TryGetValue(id, out var h)) return;
            held.Remove(id);
            if (h.Control.IsMouseMove) return;
            // still down for another owner?
            if (!held.Values.Any(o => o.Control.Id == h.Control.Id)) Set(h.Control, 0);
        }

        static void ReleaseOwner(string owner)
        {
            foreach (var id in held.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToList())
                Release(id);
        }

        public static void ReleaseAll()
        {
            foreach (var id in held.Keys.ToList()) Release(id);
        }

        /// <summary>Every frame: held axes keep their value, leases run out (a brain gone quiet leaves nothing held).
        /// <paramref name="running"/>: a run is going, so in play the game gets the bridge's input alone (IsolateDevices,
        /// LegacyInput.Isolated); between runs the person's input reaches it too.</summary>
        public static void Tick(bool connected, bool running = false)
        {
            LegacyInput.Active = connected;
            var isolate = connected && running && Application.isPlaying;
            LegacyInput.Isolated = isolate;
            IsolateDevices(isolate);
            if (!connected)
            {
                if (held.Count > 0) ReleaseAll();
                if (wasConnected) LegacyInput.Clear();
                wasConnected = false;
                Flush();
                return;
            }
            wasConnected = true;
            var now = Now;
            foreach (var pair in held.ToList())
            {
                var h = pair.Value;
                if (h.Until > 0 ? Time.timeAsDouble >= h.Until : (h.Owner == "key" || h.Leased) && now - h.Renewed > leaseSeconds)
                {
                    Release(pair.Key);
                    continue;
                }
                if (h.Control.IsMouseMove)
                    Set(h.Control, h.Rate ? h.Value * Time.unscaledDeltaTime : h.Value);
            }
            Flush();
        }

        // -- the key op ------------------------------------------------------------------------------------

        /// <summary>The key op's name of a legacy KeyCode (a key or a mouse button): its FKey name where it has one.</summary>
        static string FKeyName(KeyCode code)
        {
            if (code == KeyCode.None || code >= KeyCode.JoystickButton0) return null;  // (a joystick's: the gamepad's own names)
            string first = null;
            foreach (var pair in fkeys)
            {
                var c = pair.Value;
                if ((c.Kind != Kind.Key && c.Kind != Kind.MouseButton) || c.Code != code) continue;
                if (fkeyOf.TryGetValue(c.Id, out var canonical) && canonical == pair.Key) return pair.Key;
                first ??= pair.Key;
            }
            return first;
        }

        /// <summary>The legacy Input Manager's axes as controls, as Unreal lists its legacy mappings: a button as
        /// {"name", "action": "legacy:Name", "keys", "digital": true}, any other axis as "legacy-axis:Name" (its keys,
        /// MouseX / MouseY / MouseWheelAxis for the mouse's).</summary>
        public static List<object> LegacyControls()
        {
            var list = new List<object>();
            foreach (var (name, codes, mouse, button) in LegacyInput.AxisList())
            {
                var keys = new List<object>();
                foreach (var code in codes)
                {
                    var key = FKeyName(code);
                    if (key != null && !keys.Contains(key)) keys.Add(key);
                }
                if (mouse >= 0) keys.Add(mouse == 0 ? "MouseX" : mouse == 1 ? "MouseY" : "MouseWheelAxis");
                if (keys.Count == 0) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = name, ["action"] = (button ? "legacy:" : "legacy-axis:") + name, ["keys"] = keys, ["digital"] = button,
                });
            }
            return list;
        }

        /// <summary>input with a legacy button (`legacy:<Name>`, as Unreal's): its first key pressed, held or let go
        /// through the key op. False for any other action.</summary>
        static bool Legacy(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            var action = Json.Str(args, "action");
            if (!action.StartsWith("legacy:", StringComparison.Ordinal)) return false;
            var name = action.Substring(7);
            var entry = LegacyInput.AxisList().FirstOrDefault(a => a.name == name);
            var key = entry.keys?.Select(FKeyName).FirstOrDefault(k => k != null);
            if (key == null)
            {
                client.RespondError(id, $"Object {action} does not exist");
                return true;
            }
            var keyArgs = new Dictionary<string, object>(args) { ["key"] = key };
            keyArgs.Remove("action");
            KeyOp(client, id, keyArgs);
            return true;
        }

        static bool TryControl(string name, out Control c)
        {
            if (fkeys.TryGetValue(name, out c)) return true;
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            // An Input System key name (w, space, leftShift) or a path on the bridge's devices (<Keyboard>/w)
            if (Enum.TryParse(name, true, out Key key) && key != Key.None && !int.TryParse(name, out _))
            {
                c = KeyControl(key);
                return true;
            }
            EnsureDevices();
            var found = FromPath(name.StartsWith("<", StringComparison.Ordinal) ? name : "<Keyboard>/" + name);
            if (found.Count == 1)
            {
                c = found[0];
                return true;
            }
#endif
            // A KeyCode's name (Alpha1, Mouse0, JoystickButton0) or an Input Manager key name (left shift, mouse 0)
            if (!string.IsNullOrEmpty(name) && (Enum.TryParse(name, true, out KeyCode code) && !int.TryParse(name, out _) || LegacyInput.Code(name, out code))
                && code != KeyCode.None)
            {
                if (code >= KeyCode.Mouse0 && code <= KeyCode.Mouse6)
                {
                    c = Mouse(code - KeyCode.Mouse0);
                    return true;
                }
                if (code >= KeyCode.JoystickButton0 && code <= KeyCode.JoystickButton9)
                {
                    c = PadButton((Pad)(code - KeyCode.JoystickButton0));
                    return true;
                }
                c = KeyControl(code);
                return true;
            }
            return false;
        }

        /// <summary>key: {"key", "mode": "press"|"hold"|"release"|"release_all", "value"?, "rate"?, "lease"?, "seconds"?}.
        /// A hold with "seconds" (a timed hold) is let go after that much game time, without renewal.</summary>
        public static void KeyOp(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            var mode = Json.Str(args, "mode", "press");
            var seconds = Json.Num(args, "seconds", 0);
            var lease = Json.Num(args, "lease", 0);
            if (lease > 0) leaseSeconds = Math.Max(0.2, Math.Min(10, lease));
            if (mode == "release_all")
            {
                foreach (var hid in held.Where(p => p.Value.Owner == "key").Select(p => p.Key).ToList())
                    Release(hid);
                client.Respond(id, new Dictionary<string, object>());
                return;
            }
            var name = Json.Str(args, "key");
            if (!TryControl(name, out var c))
            {
#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                // Not a key: an Input System action by name ("Player/Jump", "Jump"), as game.press / game.hold give them
                var action = string.IsNullOrEmpty(name) ? null : FindAction(name);
                if (action != null)
                {
                    ActionDo(client, id, action, mode, ValueOf(args), false, true, seconds);
                    return;
                }
#endif
                client.RespondError(id, $"Unknown key {name}");
                return;
            }
            EnsureDevices();
            var value = (float)Json.Num(args, "value", 1.0);
            var rate = Json.Bool(args, "rate", false);
            if (mode == "release")
            {
                Release(c.Id + "#key");
                client.Respond(id, new Dictionary<string, object>());
                return;
            }
            if (mode == "hold")
            {
                Hold(c, value, rate, "key");
                held[c.Id + "#key"].Until = seconds > 0 ? Time.timeAsDouble + seconds : 0;
                client.Respond(id, new Dictionary<string, object>());
                return;
            }
            if (c.IsMouseMove)
            {
                // A tap of a mouse axis: the mouse moves once (a stick or trigger goes there for a moment, below)
                Set(c, value);
                client.Respond(id, new Dictionary<string, object>());
                return;
            }
            Tap(client, id, c, value);
        }

        static void Tap(PlaytestClient client, long id, Control c, float value)
        {
            var owner = "tap:" + id;
            Hold(c, value, false, owner);
            var frame = Time.frameCount;
            client.AddPending(new Pending
            {
                Id = id,
                Op = "key",
                Seconds = 0.05,
                Tick = (p, now) =>
                {
                    if (now - p.Start < p.Seconds || Time.frameCount < frame + 2) return false;
                    ReleaseOwner(owner);
                    client.Respond(p.Id, new Dictionary<string, object>());
                    return true;
                },
            });
        }

        // -- the pointer -------------------------------------------------------------------------------------

        /// <summary>pointer: the mouse goes to a place on screen, without moving across to it (no Mouse X/Y on the way).
        /// {"screen_x", "screen_y"}: -1 to 1 from the middle, y up (as view gives things); {"world": a kit point} or
        /// {"object": a path}: where the camera shows it (an object: its drawn box's middle). Answers where that is
        /// ({"screen_x", "screen_y", "x", "y": pixels from the bottom left, "on_screen"}).</summary>
        public static void PointerOp(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            float w = Math.Max(1, Screen.width), h = Math.Max(1, Screen.height);
            Vector2 px;
            var world = Json.Obj(args, "world");
            var objectPath = Json.Str(args, "object");
            if (world != null || objectPath.Length > 0)
            {
                Vector3 at;
                if (world != null)
                {
                    at = Frame.ToUnity(world);
                }
                else
                {
                    var o = Paths.Resolve(objectPath);
                    var go = o as GameObject ?? (o as Component)?.gameObject;
                    if (go == null)
                    {
                        client.RespondError(id, $"Object {objectPath} does not exist");
                        return;
                    }
                    at = Ops.BoundsOf(go)?.center ?? go.transform.position;
                }
                var cam = Player.Eyes(null);
                if (cam == null)
                {
                    client.RespondError(id, "No camera");
                    return;
                }
                var sp = cam.WorldToScreenPoint(at);
                if (sp.z <= 0)
                {
                    client.RespondError(id, "That is behind the camera");
                    return;
                }
                px = new Vector2(sp.x, sp.y);
            }
            else if (Json.Has(args, "screen_x") || Json.Has(args, "screen_y"))
            {
                px = new Vector2((float)(Json.Num(args, "screen_x", 0) + 1) / 2 * w, (float)(Json.Num(args, "screen_y", 0) + 1) / 2 * h);
            }
            else
            {
                client.RespondError(id, "pointer needs screen_x and screen_y, world or object");
                return;
            }
            SetPointer(px);
            client.Respond(id, new Dictionary<string, object>
            {
                ["screen_x"] = Json.Round(px.x / w * 2 - 1, 3),
                ["screen_y"] = Json.Round(px.y / h * 2 - 1, 3),
                ["x"] = Math.Round(px.x),
                ["y"] = Math.Round(px.y),
                ["on_screen"] = px.x >= 0 && px.x <= w && px.y >= 0 && px.y <= h,
            });
        }

#if AUTOPILOT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        // -- actions ---------------------------------------------------------------------------------------

        /// <summary>The game's actions now: those enabled, then the project-wide ones and those of PlayerInputs.</summary>
        public static List<InputAction> Actions()
        {
            var list = new List<InputAction>();
            var seen = new HashSet<InputAction>();
            foreach (var a in InputSystem.ListEnabledActions())
                if (seen.Add(a)) list.Add(a);
#if AUTOPILOT_PROJECT_WIDE_ACTIONS
            try
            {
                var project = InputSystem.actions;
                if (project != null)
                    foreach (var a in project)
                        if (seen.Add(a)) list.Add(a);
            }
            catch
            {
                // (no project-wide actions)
            }
#endif
            foreach (var p in PlayerInput.all)
                if (p != null && p.actions != null)
                    foreach (var a in p.actions)
                        if (seen.Add(a)) list.Add(a);
            return list;
        }

        public static string ActionPath(InputAction a)
        {
            return a.actionMap != null ? a.actionMap.name + "/" + a.name : a.name;
        }

        static InputAction FindAction(string name)
        {
            var actions = Actions();
            // "Map/Action", then "Action" (enabled first: the order of Actions())
            return actions.FirstOrDefault(a => string.Equals(ActionPath(a), name, StringComparison.OrdinalIgnoreCase))
                   ?? actions.FirstOrDefault(a => string.Equals(a.name, name, StringComparison.OrdinalIgnoreCase))
                   ?? actions.FirstOrDefault(a => a.id.ToString() == name);
        }

        /// <summary>The bridge's device controls a binding path stands for.</summary>
        static List<Control> FromPath(string path)
        {
            var list = new List<Control>();
            if (string.IsNullOrEmpty(path) || keyboard == null) return list;
            var kc = InputControlPath.TryFindControl(keyboard, path);
            if (kc is KeyControl key && key.keyCode != Key.None)
            {
                list.Add(KeyControl(key.keyCode));
                return list;
            }
            var mc = InputControlPath.TryFindControl(mouse, path);
            if (mc != null)
            {
                if (mc == mouse.leftButton || mc == mouse.press) list.Add(Mouse(0));
                else if (mc == mouse.rightButton) list.Add(Mouse(1));
                else if (mc == mouse.middleButton) list.Add(Mouse(2));
                else if (mc == mouse.backButton) list.Add(Mouse(3));
                else if (mc == mouse.forwardButton) list.Add(Mouse(4));
                else if (mc == mouse.delta)
                {
                    list.Add(Axis(Kind.MouseX));
                    list.Add(Axis(Kind.MouseY));
                }
                else if (mc == mouse.delta.x) list.Add(Axis(Kind.MouseX));
                else if (mc == mouse.delta.y) list.Add(Axis(Kind.MouseY));
                else if (mc == mouse.scroll || mc == mouse.scroll.y) list.Add(Axis(Kind.Scroll));
                if (list.Count > 0) return list;
            }
            var pc = InputControlPath.TryFindControl(gamepad, path);
            if (pc != null)
            {
                for (var p = 0; p < gamepadOf.Length; ++p)
                {
                    if (gamepad[gamepadOf[p]] == pc)
                    {
                        list.Add(PadButton((Pad)p));
                        return list;
                    }
                }
                if (pc == gamepad.leftStick)
                {
                    list.Add(Axis(Kind.LeftX));
                    list.Add(Axis(Kind.LeftY));
                }
                else if (pc == gamepad.rightStick)
                {
                    list.Add(Axis(Kind.RightX));
                    list.Add(Axis(Kind.RightY));
                }
                else if (pc == gamepad.leftStick.x) list.Add(Axis(Kind.LeftX));
                else if (pc == gamepad.leftStick.y) list.Add(Axis(Kind.LeftY));
                else if (pc == gamepad.rightStick.x) list.Add(Axis(Kind.RightX));
                else if (pc == gamepad.rightStick.y) list.Add(Axis(Kind.RightY));
                else if (pc == gamepad.leftStick.up) list.Add(Axis(Kind.LeftY, 1));
                else if (pc == gamepad.leftStick.down) list.Add(Axis(Kind.LeftY, -1));
                else if (pc == gamepad.leftStick.left) list.Add(Axis(Kind.LeftX, -1));
                else if (pc == gamepad.leftStick.right) list.Add(Axis(Kind.LeftX, 1));
                else if (pc == gamepad.rightStick.up) list.Add(Axis(Kind.RightY, 1));
                else if (pc == gamepad.rightStick.down) list.Add(Axis(Kind.RightY, -1));
                else if (pc == gamepad.rightStick.left) list.Add(Axis(Kind.RightX, -1));
                else if (pc == gamepad.rightStick.right) list.Add(Axis(Kind.RightX, 1));
                else if (pc == gamepad.leftTrigger) list.Add(Axis(Kind.LeftTrigger));
                else if (pc == gamepad.rightTrigger) list.Add(Axis(Kind.RightTrigger));
            }
            return list;
        }

        /// <summary>A plan to give an action a value: which controls to set to what.</summary>
        static List<(Control control, float value)> Plan(InputAction action, Vector3 value, bool preferStick)
        {
            var bindings = action.bindings;
            var simple = new List<(Control, float)>();
            List<(Control, float)> composite = null, stick = null;
            for (var i = 0; i < bindings.Count; ++i)
            {
                var b = bindings[i];
                if (b.isComposite)
                {
                    // Its parts: up/down/left/right (2D), negative/positive (1D), or button/modifier
                    var parts = new List<(Control, float)>();
                    var ok = true;
                    for (var j = i + 1; j < bindings.Count && bindings[j].isPartOfComposite; ++j)
                    {
                        var part = bindings[j];
                        var controls = FromPath(part.effectivePath);
                        if (controls.Count == 0)
                        {
                            continue;
                        }
                        var c = controls[0];
                        var n = (part.name ?? "").ToLowerInvariant();
                        float v = 0;
                        if (n == "up") v = value.y > 0.3f ? 1 : 0;
                        else if (n == "down") v = value.y < -0.3f ? 1 : 0;
                        else if (n == "left") v = value.x < -0.3f ? 1 : 0;
                        else if (n == "right") v = value.x > 0.3f ? 1 : 0;
                        else if (n == "positive") v = value.x > 0.3f ? 1 : 0;
                        else if (n == "negative") v = value.x < -0.3f ? 1 : 0;
                        else if (n == "forward") v = value.z > 0.3f ? 1 : 0;
                        else if (n == "backward") v = value.z < -0.3f ? 1 : 0;
                        else v = 1;  // button / modifier / binding of a one-modifier composite
                        if (v > 0) parts.Add((c, v));
                    }
                    if (ok && composite == null && parts.Count > 0) composite = parts;
                    continue;
                }
                if (b.isPartOfComposite) continue;
                var found = FromPath(b.effectivePath);
                if (found.Count == 0) continue;
                if (found.Count == 2 && found[0].Kind == Kind.LeftX || found.Count == 2 && found[0].Kind == Kind.RightX)
                {
                    stick ??= new List<(Control, float)> { (found[0], value.x), (found[1], value.y) };
                    continue;
                }
                if (found.Count == 2 && found[0].Kind == Kind.MouseX)
                {
                    if (simple.Count == 0) simple = new List<(Control, float)> { (found[0], value.x), (found[1], value.y) };
                    continue;
                }
                if (simple.Count == 0)
                {
                    var c = found[0];
                    var v = c.IsAxis ? value.x * (c.Sign == 0 ? 1 : c.Sign) : 1f;
                    simple.Add((c, v));
                }
            }
            var isValue = action.type == InputActionType.Value && action.expectedControlType != "Button";
            if (isValue && preferStick && stick != null) return stick;
            if (isValue && composite != null) return composite;
            if (isValue && stick != null) return stick;
            if (simple.Count > 0) return simple;
            return composite ?? stick ?? simple;
        }

        /// <summary>input: {"action": "Map/Action" | "Action", "value": {"X","Y","Z"}, "mode": "press"|"hold"|"release"}</summary>
        public static void InputOp(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            if (Legacy(client, id, args)) return;
            var name = Json.Str(args, "action");
            var mode = Json.Str(args, "mode", "press");
            EnsureDevices();
            var action = FindAction(name);
            if (action == null)
            {
                client.RespondError(id, $"Object {name} does not exist");
                return;
            }
            ActionDo(client, id, action, mode, ValueOf(args), Json.Bool(args, "stick", false), false);
        }

        /// <summary>An action's value as asked: {"X","Y","Z"}, [x, y, z] or a number (default: 1 on X).</summary>
        static Vector3 ValueOf(Dictionary<string, object> args)
        {
            if (args == null || !args.TryGetValue("value", out var v) || v == null) return new Vector3(1, 0, 0);
            switch (v)
            {
                case Dictionary<string, object> d:
                    return new Vector3((float)Json.Num(d, "X", Json.Num(d, "x", 0)), (float)Json.Num(d, "Y", Json.Num(d, "y", 0)), (float)Json.Num(d, "Z", Json.Num(d, "z", 0)));
                case List<object> l:
                    float At(int i) => i < l.Count && l[i] != null ? Convert.ToSingle(l[i], System.Globalization.CultureInfo.InvariantCulture) : 0f;
                    return new Vector3(At(0), At(1), At(2));
                case double n:
                    return new Vector3((float)n, 0, 0);
                case bool b:
                    return new Vector3(b ? 1 : 0, 0, 0);
            }
            return new Vector3(1, 0, 0);
        }

        /// <summary>press / hold / release an action. leased: held through the key op (renewed, let go when not);
        /// seconds: a timed hold through it (let go after that much game time).</summary>
        static void ActionDo(PlaytestClient client, long id, InputAction action, string mode, Vector3 value, bool preferStick, bool leased,
            double seconds = 0)
        {
            var owner = "input:" + ActionPath(action);
            if (mode == "release")
            {
                ReleaseOwner(owner);
                client.Respond(id, new Dictionary<string, object>());
                return;
            }
            var plan = Plan(action, value, preferStick);
            if (plan.Count == 0)
            {
                client.RespondError(id, $"{ActionPath(action)} has no binding the bridge can press (bound to: "
                                        + string.Join(", ", action.bindings.Select(b => b.effectivePath)) + ")");
                return;
            }
            if (mode == "hold")
            {
                // (a renewal holds on to what is down already: only what the new value doesn't press is let go)
                var want = new HashSet<string>(plan.Where(x => x.value != 0).Select(x => x.control.Id + "#" + owner));
                foreach (var hid in held.Where(p => p.Value.Owner == owner && !want.Contains(p.Key)).Select(p => p.Key).ToList())
                    Release(hid);
                foreach (var (c, val) in plan)
                {
                    if (val == 0) continue;
                    Hold(c, val, false, owner);
                    held[c.Id + "#" + owner].Leased = leased;
                    held[c.Id + "#" + owner].Until = seconds > 0 ? Time.timeAsDouble + seconds : 0;
                }
                // (held until released, as Enhanced Input's continuous injection; through the key op, leased)
                client.Respond(id, new Dictionary<string, object>());
                return;
            }
            // press: down now, up a moment later
            var tap = "tap:" + id;
            foreach (var (c, val) in plan)
            {
                if (c.IsMouseMove) Set(c, val);
                else if (val != 0) Hold(c, val, false, tap);
            }
            var frame = Time.frameCount;
            client.AddPending(new Pending
            {
                Id = id,
                Op = "input",
                Seconds = 0.05,
                Tick = (p, now) =>
                {
                    if (now - p.Start < p.Seconds || Time.frameCount < frame + 2) return false;
                    ReleaseOwner(tap);
                    client.Respond(p.Id, new Dictionary<string, object>());
                    return true;
                },
            });
        }

        /// <summary>The game's controls for context: {"name", "action": "Map/Action", "keys": [FKey names], "digital"}.</summary>
        public static List<object> Controls()
        {
            var list = new List<object>();
            EnsureDevices();
            foreach (var a in Actions())
            {
                var keys = new List<object>();
                foreach (var b in a.bindings)
                {
                    if (b.isComposite) continue;
                    foreach (var c in FromPath(b.effectivePath))
                    {
                        var key = fkeyOf.TryGetValue(c.Id, out var fk) ? fk : c.Id;
                        if (c.Sign != 0) key += c.Sign > 0 ? "+" : "-";
                        if (!keys.Contains(key)) keys.Add(key);
                    }
                }
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = a.name,
                    ["action"] = ActionPath(a),
                    ["keys"] = keys,
                    ["digital"] = a.type == InputActionType.Button || a.expectedControlType == "Button",
                    ["value_type"] = a.expectedControlType ?? "",
                    ["enabled"] = a.enabled,
                    ["bindings"] = a.bindings.Where(b => !b.isComposite).Select(b => (object)((b.isPartOfComposite ? b.name + ":" : "") + b.effectivePath)).ToList(),
                });
            }
            list.AddRange(LegacyControls());
            return list;
        }

        // -- move: the player walks a way (kit frame) ----------------------------------------------------------

        /// <summary>The game's move action: an enabled Vector2 action named like "Move".</summary>
        static InputAction MoveAction()
        {
            var actions = Actions();
            return actions.FirstOrDefault(a => a.enabled && a.expectedControlType == "Vector2" && a.name.Equals("Move", StringComparison.OrdinalIgnoreCase))
                   ?? actions.FirstOrDefault(a => a.enabled && a.expectedControlType == "Vector2" && a.name.IndexOf("move", StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? actions.FirstOrDefault(a => a.expectedControlType == "Vector2" && a.name.Equals("Move", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>How move gives the input: "stick" (the move action's gamepad stick), "keys" (its key composite), or
        /// null when it can't (no move action the bridge can drive).</summary>
        public static string MoveVia(string asked, out InputAction action)
        {
            action = MoveAction();
            if (action == null) return null;
            EnsureDevices();
            var stick = Plan(action, new Vector3(0, 1, 0), true);
            var hasStick = stick.Count > 0 && (stick[0].control.Kind == Kind.LeftX || stick[0].control.Kind == Kind.RightX);
            var keys = Plan(action, new Vector3(0, 1, 0), false);
            var hasKeys = keys.Count > 0 && keys[0].control.Kind == Kind.Key;
            if (asked == "stick") return hasStick ? "stick" : null;
            if (asked == "keys") return hasKeys ? "keys" : null;
            return hasStick ? "stick" : hasKeys ? "keys" : null;
        }

        /// <summary>Give the move action a value (x right, y forward) this frame, through the way chosen.</summary>
        public static void MoveInput(InputAction action, string via, Vector2 local)
        {
            const string owner = "move";
            var plan = Plan(action, new Vector3(local.x, local.y, 0), via == "stick");
            var want = new Dictionary<string, (Control, float)>();
            foreach (var (c, v) in plan)
                if (v != 0) want[c.Id] = (c, v);
            foreach (var pair in held.Where(p => p.Value.Owner == owner).ToList())
                if (!want.ContainsKey(pair.Value.Control.Id))
                    Release(pair.Key);
            foreach (var (c, v) in want.Values)
                Hold(c, v, false, owner);
            if (via == "stick")
            {
                // Sticks: both axes, zeros too
                foreach (var (c, v) in plan) Set(c, v);
            }
        }

        public static void MoveStop()
        {
            ReleaseOwner("move");
        }
#else
        public static void InputOp(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            if (Legacy(client, id, args)) return;
            if (Json.Str(args, "mode") == "release")
            {
                client.Respond(id, new Dictionary<string, object>());
                return;
            }
            client.RespondError(id, "Actions can't be injected: the Input System is off in this project (see context's input)");
        }

        public static List<object> Controls()
        {
            return LegacyControls();
        }
#endif
    }
}
