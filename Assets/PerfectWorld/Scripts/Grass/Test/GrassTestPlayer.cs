using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace BrewMonster.Scripts.Grass.Test
{
    /// <summary>
    /// Mockup host player for the grass trample test: a capsule that walks with WASD / arrow keys (hold Shift to run)
    /// or, with auto-walk on, ping-pongs across the field by itself so the trample can be watched without touching the
    /// keyboard. Press P at runtime to toggle auto-walk.
    ///
    /// No physics and no CharacterController on purpose: the transform is moved directly, and that is all
    /// <see cref="GrassInteractionController"/> measures (position over time) — the doc's "movement system" is as
    /// replaceable as the rest (§14). A teleport-sized step is absorbed by the controller, so even dragging the capsule
    /// around in the Scene view behaves.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GrassTestPlayer : MonoBehaviour
    {
        [Header("Manual control")]
        [SerializeField, Tooltip("Walk speed (m/s).")]
        private float walkSpeed = 2.5f;

        [SerializeField, Tooltip("Run speed (m/s) while Shift is held — the trample leans harder the faster you move.")]
        private float runSpeed = 7.5f;

        [SerializeField, Tooltip("How fast the capsule turns to face its movement (deg/s).")]
        private float turnSpeedDegrees = 720f;

        [SerializeField, Tooltip("Camera whose forward/right define W/A/S/D. Empty = world axes.")]
        private Camera viewCamera;

        [Header("Auto walk")]
        [SerializeField, Tooltip("Walk back and forth across the field by itself — press Play and just watch.")]
        private bool autoWalk = true;

        [SerializeField, Tooltip("Auto-walk speed (m/s).")]
        private float autoWalkSpeed = 4.5f;

        [SerializeField, Tooltip("Length (m) of the auto-walk path, starting at the spawn position.")]
        private float autoWalkLength = 10f;

        [SerializeField, Tooltip("Direction of the auto-walk path, in world space. Across the view by default, so the " +
                                 "bend and the spring-back behind the capsule are both seen from the side.")]
        private Vector3 autoWalkAxis = Vector3.right;

        [Header("HUD")]
        [SerializeField, Tooltip("Draw the key hint in the Game view.")]
        private bool showHint = true;

        /// <summary>Camera used to interpret the movement keys (the editor menu assigns the test camera).</summary>
        public Camera ViewCamera
        {
            get { return viewCamera; }
            set { viewCamera = value; }
        }

        private float m_fGroundY;
        private Vector3 m_vAutoOrigin;
        private float m_fAutoDistance;

        private void OnEnable()
        {
            m_fGroundY = transform.position.y;
            m_vAutoOrigin = transform.position;
            m_fAutoDistance = 0f;
        }

        private void Update()
        {
            float fDeltaTime = Time.deltaTime;
            if (fDeltaTime <= 0f)
                return;

            Vector2 vInput = ReadMoveInput(out bool bRun, out bool bToggleAutoWalk);
            if (bToggleAutoWalk)
                autoWalk = !autoWalk;

            Vector3 vVelocity;

            if (vInput.sqrMagnitude > 1e-4f)
            {
                //	The hand wins over the autopilot: moving the capsule yourself takes it off auto-walk.
                autoWalk = false;
                vVelocity = InputToWorldDirection(vInput) * (bRun ? runSpeed : walkSpeed);
            }
            else if (autoWalk)
            {
                vVelocity = AutoWalkVelocity();
            }
            else
            {
                return;
            }

            Vector3 vPosition = transform.position + vVelocity * fDeltaTime;
            vPosition.y = m_fGroundY;                               //	the capsule never leaves the ground
            transform.position = vPosition;

            Vector3 vFace = vVelocity;
            vFace.y = 0f;
            if (vFace.sqrMagnitude > 1e-6f)
            {
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation, Quaternion.LookRotation(vFace), turnSpeedDegrees * fDeltaTime);
            }
        }

        /// <summary>Move keys → world direction on the XZ plane, relative to the view camera when there is one.</summary>
        private Vector3 InputToWorldDirection(Vector2 p_vInput)
        {
            Vector3 vForward = Vector3.forward;
            Vector3 vRight = Vector3.right;

            if (viewCamera != null)
            {
                vForward = Vector3.ProjectOnPlane(viewCamera.transform.forward, Vector3.up).normalized;
                vRight = Vector3.ProjectOnPlane(viewCamera.transform.right, Vector3.up).normalized;

                if (vForward.sqrMagnitude < 1e-4f)
                    vForward = Vector3.forward;
                if (vRight.sqrMagnitude < 1e-4f)
                    vRight = Vector3.right;
            }

            return (vForward * p_vInput.y + vRight * p_vInput.x).normalized;
        }

        /// <summary>Ping-pong along <see cref="autoWalkAxis"/>: the trample is watched from a fixed camera, so a
        /// straight back-and-forth line shows both the bend and the spring-back behind the capsule.</summary>
        private Vector3 AutoWalkVelocity()
        {
            Vector3 vAxis = autoWalkAxis.sqrMagnitude > 1e-6f ? autoWalkAxis.normalized : Vector3.forward;
            float fLength = Mathf.Max(1f, autoWalkLength);

            m_fAutoDistance += autoWalkSpeed * Time.deltaTime;
            float fAlong = Mathf.PingPong(m_fAutoDistance, fLength);

            Vector3 vDelta = m_vAutoOrigin + vAxis * fAlong - transform.position;
            vDelta.y = 0f;

            if (vDelta.magnitude < 1e-3f)
                return Vector3.zero;

            return vDelta.normalized * autoWalkSpeed;
        }

        private Vector2 ReadMoveInput(out bool p_bRun, out bool p_bToggleAutoWalk)
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard pKeyboard = Keyboard.current;
            if (pKeyboard == null)
            {
                p_bRun = false;
                p_bToggleAutoWalk = false;
                return Vector2.zero;
            }

            float fX = (pKeyboard.dKey.isPressed || pKeyboard.rightArrowKey.isPressed ? 1f : 0f)
                     - (pKeyboard.aKey.isPressed || pKeyboard.leftArrowKey.isPressed ? 1f : 0f);
            float fY = (pKeyboard.wKey.isPressed || pKeyboard.upArrowKey.isPressed ? 1f : 0f)
                     - (pKeyboard.sKey.isPressed || pKeyboard.downArrowKey.isPressed ? 1f : 0f);

            p_bRun = pKeyboard.leftShiftKey.isPressed || pKeyboard.rightShiftKey.isPressed;
            p_bToggleAutoWalk = pKeyboard.pKey.wasPressedThisFrame;
            return new Vector2(fX, fY);
#elif ENABLE_LEGACY_INPUT_MANAGER
            float fX = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1f : 0f)
                     - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f);
            float fY = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1f : 0f)
                     - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1f : 0f);

            p_bRun = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            p_bToggleAutoWalk = Input.GetKeyDown(KeyCode.P);
            return new Vector2(fX, fY);
#else
            p_bRun = false;
            p_bToggleAutoWalk = false;
            return Vector2.zero;
#endif
        }

        private void OnGUI()
        {
            if (!showHint)
                return;

            GUILayout.BeginArea(new Rect(12f, 12f, 520f, 108f));
            GUILayout.Box("Grass trample test");
            GUILayout.Label("WASD / arrows  : walk");
            GUILayout.Label("Shift          : run (leans the grass harder)");
            GUILayout.Label($"P              : auto-walk {(autoWalk ? "ON" : "OFF")}");
            GUILayout.EndArea();
        }
    }
}
