using UnityEngine;

namespace MunShot.Cameras
{
    /// <summary>
    /// What the player asks of the photo camera this frame, read from the keyboard and mouse by
    /// <see cref="KeyboardMouseCameraInput"/>; the camera turns it into movement. Kept apart from the camera so another
    /// input device can be added later by producing the same intent.
    /// </summary>
    public struct PhotoCameraIntent
    {
        /// <summary>Flying: x right, y forward, each -1 to 1.</summary>
        public Vector2 Move;

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
}
