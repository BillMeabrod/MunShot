using System;
using System.Collections.Generic;
using KSP.Game;
using KSP.Input;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MunShot.Cameras
{
    /// <summary>
    /// What the player asks of the photo camera this frame, from every input device together. The camera turns it
    /// into movement the same way whichever device it came from, so keyboard and gamepad behave alike (speed ramp,
    /// roll, orbit).
    /// </summary>
    public struct PhotoCameraIntent
    {
        /// <summary>Flying: x right, y forward, each -1 to 1; a stick pushed halfway flies at half speed.</summary>
        public Vector2 Move;

        /// <summary>Held up (+) or down (-), -1 to 1: flies up or down in Free, comes closer (+) or goes further in Orbit.</summary>
        public float Vertical;

        /// <summary>Mouse-wheel steps this frame: a step up or down in Free, a step closer (+) or further in Orbit.</summary>
        public float Steps;

        /// <summary>How far to turn this frame, in degrees: x to the right, y upward.</summary>
        public Vector2 Look;

        /// <summary>Tilting: + to the left, - to the right, -1 to 1.</summary>
        public float Roll;

        /// <summary>Fast: five times the speed, and while flying, speeding up the longer it's held.</summary>
        public bool Fast;

        /// <summary>Slow: a fifth of the speed.</summary>
        public bool Slow;

        /// <summary>Take the tilt off (pressed this frame).</summary>
        public bool Level;

        public void Add(PhotoCameraIntent other)
        {
            Move = Vector2.ClampMagnitude(Move + other.Move, 1f);
            Vertical = Mathf.Clamp(Vertical + other.Vertical, -1f, 1f);
            Steps += other.Steps;
            Look += other.Look;
            Roll = Mathf.Clamp(Roll + other.Roll, -1f, 1f);
            Fast |= other.Fast;
            Slow |= other.Slow;
            Level |= other.Level;
        }
    }

    /// <summary>
    /// Keyboard and mouse: the keys in MunShot's settings, hold right mouse to look (the cursor is hidden and held in
    /// place meanwhile, like the game's own camera drag), mouse wheel for steps.
    /// </summary>
    public sealed class KeyboardMouseCameraInput
    {
        // Degrees of turn per pixel of mouse movement
        private const float DegreesPerPixel = 0.15f;

        private bool _looking;
        private CursorLockMode _cursorLockBeforeLook;
        private bool _cursorVisibleBeforeLook;

        /// <param name="pointerOverUi">The mouse is over photo mode's own UI: a right-click or wheel there is the UI's.</param>
        /// <param name="keyboardTaken">A text field has the keyboard: typing isn't flying.</param>
        public PhotoCameraIntent Read(bool pointerOverUi, bool keyboardTaken)
        {
            var intent = new PhotoCameraIntent();
            UpdateLooking(pointerOverUi);
            if (_looking) intent.Look = global::Mouse.SystemDelta * DegreesPerPixel;
            if (!pointerOverUi) intent.Steps = UnityEngine.Input.mouseScrollDelta.y;
            if (keyboardTaken) return intent;

            intent.Move = Vector2.ClampMagnitude(new Vector2(Axis(MunShotSettings.Right.Held, MunShotSettings.Left.Held),
                                                             Axis(MunShotSettings.Forward.Held, MunShotSettings.Back.Held)), 1f);
            intent.Roll = Axis(MunShotSettings.RollLeft.Held, MunShotSettings.RollRight.Held);
            intent.Fast = MunShotSettings.Fast.Held;
            intent.Slow = MunShotSettings.Slow.Held;
            intent.Level = MunShotSettings.LevelHorizon.Down;
            return intent;
        }

        /// <summary>Lets go of the cursor if it's held for looking.</summary>
        public void Reset() => StopLooking();

        private void UpdateLooking(bool pointerOverUi)
        {
            global::Mouse.MouseButton right = global::Mouse.Right;
            bool held = right != null && right.IsPressed();
            if (!_looking && held && right.WasPressedThisFrame() && !pointerOverUi)
            {
                _looking = true;
                _cursorLockBeforeLook = Cursor.lockState;
                _cursorVisibleBeforeLook = Cursor.visible;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else if (_looking && !held) StopLooking();
        }

        private void StopLooking()
        {
            if (!_looking) return;
            _looking = false;
            Cursor.lockState = _cursorLockBeforeLook;
            Cursor.visible = _cursorVisibleBeforeLook;
        }

        private static float Axis(bool positive, bool negative) => (positive ? 1f : 0f) - (negative ? 1f : 0f);
    }

    /// <summary>
    /// Gamepad, laid out like controlling a kerbal in first person:
    /// <list type="bullet">
    /// <item>left stick flies, and right stick looks: copies of the game's own EVA movement and flight camera look
    /// actions, so rebinding those in the game's settings (to any device) carries over. Copies, because photo mode
    /// locks the originals (it keeps the kerbal and vessel still);</item>
    /// <item>LB / RB roll (the buttons EVA uses where the keyboard has Q / E), d-pad up / down flies up and down (or
    /// closer and further in Orbit), either trigger is Fast, R3 levels the horizon. The game has no actions for
    /// these, so they're MunShot's own.</item>
    /// </list>
    /// Only listens while enabled (photo camera engaged).
    /// </summary>
    public sealed class GamepadCameraInput : IDisposable
    {
        // Degrees per second of turn with the stick pushed all the way
        private const float LookDegreesPerSecond = 120f;

        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;
        private InputActionMap _map;
        private InputAction _forward, _right, _lookRight, _lookUp, _roll, _vertical, _fast, _level;

        public GamepadCameraInput(Action<string> logInfo, Action<string> logWarning)
        {
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        }

        /// <summary>
        /// Starts listening. The game's actions are copied now, so rebinds made in the game's settings since last
        /// time are picked up.
        /// </summary>
        public void Enable()
        {
            Disable();
            GameInput input = GameManager.Instance?.Game?.Input;
            if (input == null)
            {
                _logWarning("Photo camera: no gamepad controls, the game's input isn't available.");
                return;
            }

            _map = new InputActionMap("MunShot Photo Camera");
            var copied = new List<string>();
            // EVA's front/back is negative forward (stick up, W): see the game's bindings
            _forward = CopyGameAction(input.EVA.MoveFrontBack, "Fly Forward", copied);
            _right = CopyGameAction(input.EVA.MoveLeftRight, "Fly Right", copied);
            _lookRight = CopyGameAction(input.Flight.CameraYawGamepad, "Look Right", copied);
            _lookUp = CopyGameAction(input.Flight.CameraPitchGamepad, "Look Up", copied);

            _roll = AxisAction("Roll");
            _roll.AddCompositeBinding("1DAxis").With("Negative", "<Gamepad>/rightShoulder").With("Positive", "<Gamepad>/leftShoulder");
            _vertical = AxisAction("Up Down");
            _vertical.AddCompositeBinding("1DAxis").With("Negative", "<Gamepad>/dpad/down").With("Positive", "<Gamepad>/dpad/up");
            _fast = _map.AddAction("Fast", InputActionType.Button);
            _fast.AddBinding("<Gamepad>/leftTrigger");
            _fast.AddBinding("<Gamepad>/rightTrigger");
            _level = _map.AddAction("Level Horizon", InputActionType.Button, "<Gamepad>/rightStickPress");

            _map.Enable();
            _logInfo($"Photo camera: gamepad controls on ({string.Join("; ", copied)}; LB/RB roll, d-pad up/down, triggers fast, R3 level).");
        }

        public void Disable()
        {
            if (_map == null) return;
            _map.Disable();
            _map.Dispose();
            _map = null;
            _forward = _right = _lookRight = _lookUp = _roll = _vertical = _fast = _level = null;
        }

        public void Dispose() => Disable();

        public PhotoCameraIntent Read(float deltaTime)
        {
            var intent = new PhotoCameraIntent();
            if (_map == null) return intent;
            intent.Move = Vector2.ClampMagnitude(new Vector2(Value(_right), -Value(_forward)), 1f);
            intent.Look = new Vector2(Value(_lookRight), Value(_lookUp)) * (LookDegreesPerSecond * deltaTime);
            intent.Roll = Value(_roll);
            intent.Vertical = Value(_vertical);
            intent.Fast = _fast.IsPressed();
            intent.Level = _level.WasPressedThisFrame();
            return intent;
        }

        private InputAction AxisAction(string name)
        {
            InputAction action = _map.AddAction(name, InputActionType.Value);
            action.expectedControlType = "Axis";
            return action;
        }

        private static float Value(InputAction action) => action != null ? action.ReadValue<float>() : 0f;

        // A copy of one of the game's actions with its bindings as they are now (including the player's rebinds),
        // leaving out keyboard and mouse ones: those are MunShot's own keys
        private InputAction CopyGameAction(InputAction source, string name, List<string> described)
        {
            if (source == null)
            {
                _logWarning($"Photo camera: the game has no action for '{name}' on the gamepad.");
                return null;
            }
            InputAction copy = _map.AddAction(name, source.type);
            copy.expectedControlType = source.expectedControlType;
            var paths = new List<string>();
            var bindings = source.bindings;
            for (int i = 0; i < bindings.Count; i++)
            {
                InputBinding binding = bindings[i];
                if (binding.isComposite)
                {
                    InputActionSetupExtensions.CompositeSyntax composite = default;
                    bool any = false;
                    for (int part = i + 1; part < bindings.Count && bindings[part].isPartOfComposite; part++)
                    {
                        InputBinding p = bindings[part];
                        if (IsKeyboardOrMouse(p.effectivePath)) continue;
                        if (!any) composite = copy.AddCompositeBinding(binding.effectivePath, binding.effectiveInteractions, binding.effectiveProcessors);
                        composite = composite.With(p.name, p.effectivePath, processors: p.effectiveProcessors);
                        paths.Add($"{p.name} {p.effectivePath}");
                        any = true;
                    }
                }
                else if (!binding.isPartOfComposite && !IsKeyboardOrMouse(binding.effectivePath))
                {
                    copy.AddBinding(binding.effectivePath, binding.effectiveInteractions, binding.effectiveProcessors);
                    paths.Add(binding.effectivePath);
                }
            }
            if (paths.Count == 0) _logWarning($"Photo camera: the game's '{source.name}' has no gamepad binding; '{name}' won't work on a gamepad.");
            described.Add($"{name} from the game's {source.name} [{string.Join(", ", paths)}]");
            return copy;
        }

        private static bool IsKeyboardOrMouse(string path) =>
            string.IsNullOrEmpty(path) || path.StartsWith("<Keyboard>", StringComparison.OrdinalIgnoreCase)
                                       || path.StartsWith("<Mouse>", StringComparison.OrdinalIgnoreCase)
                                       || path.StartsWith("<Pointer>", StringComparison.OrdinalIgnoreCase);
    }
}
