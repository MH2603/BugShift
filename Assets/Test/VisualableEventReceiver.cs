using UnityEngine;

public class VisualaleEventReceiver : MonoBehaviour
{
    /// <summary>
    /// Cached Renderer component.
    /// </summary>
    private Renderer _renderer;

    private void Awake()
    {
        // Cache the Renderer component to avoid repeated lookups.
        _renderer = GetComponent<Renderer>();
    }

    /// <summary>
    /// Called by Unity when the Renderer becomes visible
    /// to at least one camera.
    /// </summary>
    private void OnBecameVisible()
    {
        Debug.Log(
            $"[{name}] Became visible. " +
            $"Bounds: {_renderer.bounds}"
        );

        // Handle visibility start here.
        // Example: resume visual effects or animation-related logic.
    }

    /// <summary>
    /// Called by Unity when the Renderer is no longer visible
    /// to any camera.
    /// </summary>
    private void OnBecameInvisible()
    {
        Debug.Log($"[{name}] Became invisible.");

        // Handle visibility end here.
    }
}
