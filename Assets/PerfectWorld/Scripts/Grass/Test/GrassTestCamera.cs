using UnityEngine;

namespace BrewMonster.Scripts.Grass.Test
{
    /// <summary>
    /// Minimal follow camera for the grass trample test: keeps a fixed world-space offset from the capsule and looks at
    /// it, so the trample is always on screen while the capsule walks. LateUpdate (after the player moved) and
    /// exponential smoothing — nothing here is meant to become a real camera rig.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class GrassTestCamera : MonoBehaviour
    {
        [SerializeField, Tooltip("What to follow (the mockup player capsule).")]
        private Transform target;

        [SerializeField, Tooltip("World-space offset from the target.")]
        private Vector3 offset = new Vector3(0f, 3.4f, -6f);

        [SerializeField, Tooltip("Smoothing time (s) of the follow; 0 = rigid.")]
        private float smoothTime = 0.15f;

        [SerializeField, Tooltip("Height (m) above the target that the camera aims at — roughly the capsule centre.")]
        private float lookHeight = 0.9f;

        /// <summary>What the camera follows (the editor menu assigns the capsule it creates).</summary>
        public Transform Target
        {
            get { return target; }
            set { target = value; }
        }

        private Vector3 m_vVelocity;

        private void LateUpdate()
        {
            if (target == null)
                return;

            Vector3 vDesired = target.position + offset;

            transform.position = smoothTime > 1e-4f
                ? Vector3.SmoothDamp(transform.position, vDesired, ref m_vVelocity, smoothTime)
                : vDesired;

            Vector3 vLookAt = target.position + Vector3.up * lookHeight;
            Vector3 vForward = vLookAt - transform.position;
            if (vForward.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(vForward, Vector3.up);
        }
    }
}
