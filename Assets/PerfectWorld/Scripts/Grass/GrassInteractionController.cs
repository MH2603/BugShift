using UnityEngine;

namespace BrewMonster.Scripts.Grass
{
    /// <summary>
    /// Publishes the player's interaction data for the grass shader — the C# half of the effect described in
    /// <c>Assets/PerfectWorld/Scripts/Grass/interactive_grass_bending_unity_urp.md</c> (§5 "C# controller for global
    /// shader parameters", §6 "use one global radius").
    ///
    /// The deformation itself is per-vertex on the GPU (<c>BrewMonster/GrassBillboard</c>): this component writes four
    /// shader globals once per frame and does nothing else. No per-blade CPU work, no material copies, no
    /// MaterialPropertyBlock, no per-frame material mutation — the globals reach every grass material of every grass
    /// type through both draw paths, which is what makes the whole meadow react identically:
    ///
    ///   _GrassPlayerPosition      xyz = player world position, w = 1
    ///   _GrassPlayerDirection     xyz = planar velocity (m/s) — normalized it is the travel direction, its length is
    ///                             the speed the lean is scaled by;  w = movement activity 0..1
    ///   _GrassInteractionRadius   radius in metres — GLOBAL on purpose (§6), so the controller stays the single source
    ///                             of truth instead of every grass material carrying its own copy
    ///   _GrassInteractionEnabled  0/1 — 0 makes the shader skip the effect entirely
    ///
    /// Activity (the <c>w</c> of the direction) is what tells the shader "he is walking" apart from "he is standing in
    /// the grass": the radial bend always follows his position, while the shake and the extra lean fade out with the
    /// activity, so standing still lets the grass relax instead of holding it down.
    ///
    /// Scope (doc §8): only the CURRENT position is published, so grass springs back as soon as he leaves the radius.
    /// Leaving a visible trail would need an interaction-field texture; that is deliberately not implemented here.
    ///
    /// Runs in LateUpdate (§14), i.e. after the movement system has moved the player for this frame, and reads an
    /// optional <see cref="movementSource"/> so the gameplay root and the visible mesh can be different transforms.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GrassInteractionController : MonoBehaviour
    {
        [Header("Source")]
        [SerializeField, Tooltip("Transform whose world position drives the grass interaction. Empty = this transform.")]
        private Transform player;

        [SerializeField, Tooltip("Optional transform the movement is measured from, when the visual mesh and the " +
                                 "gameplay root are different objects. Empty = the player transform.")]
        private Transform movementSource;

        [Header("Interaction")]
        [SerializeField, Min(0.1f), Tooltip("Horizontal reach of the player's influence (m). Published globally, so it " +
                                            "applies to every grass type at once.")]
        private float interactionRadius = 1.5f;

        [SerializeField, Tooltip("Whether this controller currently affects grass. Off publishes the disabled flag, " +
                                 "so the shader does no interaction work at all.")]
        private bool interactionEnabled = true;

        [Header("Response")]
        [SerializeField, Tooltip("Speed (m/s) at which activity starts counting; it reaches full activity at twice this.")]
        private float activateSpeed = 0.6f;

        [SerializeField, Tooltip("Smoothing time (s) of the measured velocity — removes the jitter of a stepped / " +
                                 "frame-driven move.")]
        private float velocitySmoothTime = 0.08f;

        [SerializeField, Tooltip("One frame of movement longer than this (m) is treated as a teleport (relogin, " +
                                 "jump-to-instance, /goto) instead of a run, so the grass does not get flung.")]
        private float teleportDistance = 5f;

        [SerializeField, Tooltip("Rate (1/s) at which the activity follows its target — snappy, but not steppy.")]
        private float activityResponse = 8f;

        private static readonly int s_iPlayerPosition = Shader.PropertyToID("_GrassPlayerPosition");
        private static readonly int s_iPlayerDirection = Shader.PropertyToID("_GrassPlayerDirection");
        private static readonly int s_iInteractionRadius = Shader.PropertyToID("_GrassInteractionRadius");
        private static readonly int s_iInteractionEnabled = Shader.PropertyToID("_GrassInteractionEnabled");

        /// <summary>Transform the interaction follows (the editor menu wires it to the mockup capsule).</summary>
        public Transform Player
        {
            get { return player; }
            set { player = value; }
        }

        /// <summary>Transform the movement is measured from; null means the player transform is used.</summary>
        public Transform MovementSource
        {
            get { return movementSource; }
            set { movementSource = value; }
        }

        /// <summary>Interaction radius in metres (also published to the shader as a global).</summary>
        public float InteractionRadius
        {
            get { return interactionRadius; }
            set { interactionRadius = Mathf.Max(0.1f, value); }
        }

        /// <summary>Whether the grass reacts at all.</summary>
        public bool InteractionEnabled
        {
            get { return interactionEnabled; }
            set
            {
                interactionEnabled = value;

                //	Apply immediately instead of on the next LateUpdate, so toggling it off stops the effect now.
                if (isActiveAndEnabled)
                    PublishShaderParameters(PlayerPosition, m_vVelocity, Activity);
                else
                    Shader.SetGlobalFloat(s_iInteractionEnabled, 0f);
            }
        }

        /// <summary>Last activity written to the shader (0..1) — handy for debugging in the HUD.</summary>
        public float Activity { get; private set; }

        /// <summary>Smoothed horizontal speed of the player (m/s).</summary>
        public float Speed { get; private set; }

        private Vector3 m_vPreviousPosition;
        private Vector3 m_vVelocity;

        private void OnEnable()
        {
            //	Initialise from the current position so the first frame does not read as a huge movement spike, and
            //	publish right away so the grass reacts on frame one instead of on the first LateUpdate.
            m_vPreviousPosition = ResolveMovementSource().position;
            m_vVelocity = Vector3.zero;
            Activity = 0f;
            Speed = 0f;

            PublishShaderParameters(PlayerPosition, Vector3.zero, 0f);
        }

        private void LateUpdate()
        {
            if (player == null)
            {
                //	Nothing to follow: disable the global instead of leaving stale values bending the grass.
                Shader.SetGlobalFloat(s_iInteractionEnabled, 0f);
                return;
            }

            float fDeltaTime = Time.deltaTime;
            if (fDeltaTime <= 1e-5f)
                return;                                         //	paused / first frame — nothing moved

            Vector3 vPosition = ResolveMovementSource().position;

            //	Horizontal displacement of one frame → velocity. Y is dropped: the grass reacts to walking through it,
            //	not to jumping over it (§7.3).
            Vector3 vStep = vPosition - m_vPreviousPosition;
            vStep.y = 0f;

            if (vStep.sqrMagnitude > teleportDistance * teleportDistance)
            {
                vStep = Vector3.zero;
                m_vVelocity = Vector3.zero;
                Activity = 0f;
            }

            Vector3 vInstant = vStep / fDeltaTime;
            float fLerp = velocitySmoothTime > 1e-4f
                ? 1f - Mathf.Exp(-fDeltaTime / velocitySmoothTime)
                : 1f;

            m_vVelocity = Vector3.Lerp(m_vVelocity, vInstant, fLerp);
            m_vPreviousPosition = vPosition;

            //	Speed → activity: 0 at activateSpeed, 1 at twice that, smoothed so a stuttering frame rate does not
            //	pulse the whole meadow.
            Speed = m_vVelocity.magnitude;
            float fTarget = Mathf.Clamp01((Speed - activateSpeed) / Mathf.Max(0.01f, activateSpeed));
            Activity = Mathf.MoveTowards(Activity, fTarget, fDeltaTime * activityResponse);

            PublishShaderParameters(player.position, m_vVelocity, Activity);
        }

        private void OnDisable()
        {
            //	Clear the globals so a disabled controller cannot leave the effect enabled (§5, "Controller/shader
            //	integration checklist").
            Shader.SetGlobalFloat(s_iInteractionEnabled, 0f);
            Shader.SetGlobalFloat(s_iInteractionRadius, 0f);
            Shader.SetGlobalVector(s_iPlayerPosition, Vector4.zero);
            Shader.SetGlobalVector(s_iPlayerDirection, Vector4.zero);
        }

        /// <summary>Radius override for gameplay code / tests; the shader picks it up on the next publish.</summary>
        public void SetInteractionRadius(float p_fRadius)
        {
            InteractionRadius = p_fRadius;
        }

        /// <summary>Enable or disable the effect without disabling this component.</summary>
        public void SetInteractionEnabled(bool p_bEnabled)
        {
            InteractionEnabled = p_bEnabled;
        }

        /// <summary>Where the interaction is measured from: the movement source when set, else the player, else us.</summary>
        private Transform ResolveMovementSource()
        {
            return movementSource != null ? movementSource : player != null ? player : transform;
        }

        /// <summary>Where the interaction happens: the player transform, or this transform while none is assigned.</summary>
        private Vector3 PlayerPosition
        {
            get { return player != null ? player.position : transform.position; }
        }

        /// <summary>
        /// Writes the four globals the grass shader reads. Global state is shared by every grass material of the scene,
        /// which is exactly the intent here — one local player, every grass type — so these names must not be reused
        /// by anything that draws something else.
        /// </summary>
        private void PublishShaderParameters(Vector3 p_vPosition, Vector3 p_vVelocity, float p_fActivity)
        {
            Shader.SetGlobalVector(s_iPlayerPosition, new Vector4(p_vPosition.x, p_vPosition.y, p_vPosition.z, 1f));
            Shader.SetGlobalVector(s_iPlayerDirection, new Vector4(p_vVelocity.x, 0f, p_vVelocity.z, p_fActivity));
            Shader.SetGlobalFloat(s_iInteractionRadius, Mathf.Max(0.01f, interactionRadius));
            Shader.SetGlobalFloat(s_iInteractionEnabled, interactionEnabled && player != null ? 1f : 0f);
        }
    }
}
