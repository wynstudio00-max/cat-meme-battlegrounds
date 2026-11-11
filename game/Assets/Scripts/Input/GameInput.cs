using UnityEngine;
using UnityEngine.InputSystem;

namespace CatBattleground
{
    /// <summary>
    /// One place for the game's controls (2026-10-28, phone browsers): keyboard + mouse exactly as before, plus the on-screen touch
    /// controls (<see cref="TouchControls"/>, PUBG Mobile style) which write into the Touch* fields every frame before the game reads
    /// them (TouchControls runs early). The game scripts ask this class instead of Keyboard.current / Mouse.current.
    ///   Move        WASD | left joystick              Sprint      Shift | joystick pushed to the top edge
    ///   Look        mouse delta | drag on the right    Jump        Space | JUMP (also leaves the plane / opens the chute)
    ///   Fire        LMB | FIRE (left + right)          Aim         RMB | AIM (toggle)
    ///   Crouch      C | CROUCH                         Reload      R | RELOAD
    ///   Bomb        G hold / release | BOMB hold / release, slide off it to cancel (RMB cancels on PC)
    ///   Map         M | tap the minimap                Pause       Esc | pause button
    ///   Next        Tab / → / click | tap (spectating) Previous    ←
    /// </summary>
    public static class GameInput
    {
        /// <summary>A phone / tablet: touch controls on, no mouse lock, lighter graphics (MobileProfile).</summary>
        public static bool TouchMode { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Detect()
        {
            TouchMode = Application.isMobilePlatform || (Touchscreen.current != null && Mouse.current == null)
                        || Application.absoluteURL.Contains("touch=1");   // ?touch=1 forces it on a PC browser for testing
        }

        // ---------------------------------------------------------------- written by TouchControls each frame
        internal static Vector2 TouchMove, TouchLook;
        internal static bool TouchSprint, TouchFire, TouchAim, TouchBomb;
        internal static int TouchJumpFrame = -1, TouchCrouchFrame = -1, TouchReloadFrame = -1, TouchMapFrame = -1,
            TouchPauseFrame = -1, TouchTapFrame = -1, TouchBombCancelFrame = -1;
        static bool Now(int frame) => frame == Time.frameCount;

        static Keyboard Kb => Keyboard.current;
        static Mouse Ms => Mouse.current;

        /// <summary>Game input is live: the mouse is locked to the game (PC) or we are on a touch screen.</summary>
        public static bool Locked => TouchMode || Cursor.lockState == CursorLockMode.Locked;

        public static Vector2 Move
        {
            get
            {
                var v = TouchMove;
                var kb = Kb;
                if (kb != null)
                {
                    if (kb.wKey.isPressed) v.y += 1f;
                    if (kb.sKey.isPressed) v.y -= 1f;
                    if (kb.dKey.isPressed) v.x += 1f;
                    if (kb.aKey.isPressed) v.x -= 1f;
                }
                return Vector2.ClampMagnitude(v, 1f);
            }
        }

        /// <summary>This frame's look movement in "mouse pixels" (the PlayerController applies its sensitivity on top).</summary>
        public static Vector2 Look => (Ms != null && Cursor.lockState == CursorLockMode.Locked ? Ms.delta.ReadValue() : Vector2.zero) + TouchLook;

        public static bool Sprint => TouchSprint || (Kb != null && Kb.leftShiftKey.isPressed);
        public static bool JumpPressed => Now(TouchJumpFrame) || (Kb != null && Kb.spaceKey.wasPressedThisFrame);
        public static bool CrouchPressed => Now(TouchCrouchFrame) || (Kb != null && Kb.cKey.wasPressedThisFrame);
        public static bool ReloadPressed => Now(TouchReloadFrame) || (Kb != null && Kb.rKey.wasPressedThisFrame);
        public static bool FireHeld => TouchFire || (Ms != null && Cursor.lockState == CursorLockMode.Locked && Ms.leftButton.isPressed);
        public static bool AimHeld => TouchAim || (Ms != null && Cursor.lockState == CursorLockMode.Locked && Ms.rightButton.isPressed);
        public static bool BombHeld => TouchBomb || (Kb != null && Kb.gKey.isPressed);
        public static bool BombPressed => (TouchBomb && TouchBombDownFrame == Time.frameCount) || (Kb != null && Kb.gKey.wasPressedThisFrame);
        internal static int TouchBombDownFrame = -1;
        public static bool BombCancelPressed => Now(TouchBombCancelFrame) || (Ms != null && Ms.rightButton.wasPressedThisFrame);
        public static bool MapPressed => Now(TouchMapFrame) || (Kb != null && Kb.mKey.wasPressedThisFrame);
        public static bool PausePressed => Now(TouchPauseFrame) || (Kb != null && Kb.escapeKey.wasPressedThisFrame);
        public static bool NextPressed => Now(TouchTapFrame) || (Kb != null && (Kb.tabKey.wasPressedThisFrame || Kb.rightArrowKey.wasPressedThisFrame))
                                          || (Ms != null && Ms.leftButton.wasPressedThisFrame && Cursor.lockState == CursorLockMode.Locked);
        public static bool PreviousPressed => Kb != null && Kb.leftArrowKey.wasPressedThisFrame;
        /// <summary>Phones: a short tap on the screen that did not hit a button (spectating: next cat; closes the big map).</summary>
        public static bool TapAnywhere => Now(TouchTapFrame);

        /// <summary>Lock the mouse to the game (PC only; phones never lock a cursor).</summary>
        public static void LockCursor(bool locked)
        {
            if (TouchMode) return;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
