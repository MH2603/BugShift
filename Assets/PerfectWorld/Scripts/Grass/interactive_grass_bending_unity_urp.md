# Technical Guide: Interactive Grass Bending in Unity URP

## 1. Overview

This document describes a practical, scalable implementation of interactive grass bending in Unity's Universal Render Pipeline (URP). The target effect is a character moving through grass, causing nearby blades to lean away or deform, followed by a smooth return to their resting pose.

The recommended baseline is **GPU-side vertex displacement in a grass shader**, driven by the player’s world-space position and movement direction. For persistent trails, the guide also describes an optional interaction-field approach using a Render Texture.

> **Important:** This is a proposed implementation strategy based on the visible behavior of the supplied showcase video. It does not identify the original game's internal implementation.

### Goals

- Keep grass roots approximately fixed to the ground.
- Bend blade tips more strongly than blade roots.
- Limit the effect to a radius around the player.
- Produce a smooth falloff without a hard circular boundary.
- Support natural wind animation.
- Avoid per-blade CPU transform updates.
- Offer an upgrade path for persistent trails and large grass fields.

### Recommended baseline

| Concern | Baseline |
|---|---|
| Rendering | Unity URP |
| Deformation | Vertex displacement in the grass shader |
| Interaction input | Player world position and movement direction |
| Falloff | Smooth distance-based attenuation |
| Root stability | Per-vertex height mask |
| Recovery | Immediate shader response when the player moves away |
| Large grass fields | GPU-instanced meshes or the project's existing grass renderer |
| Persistent trails | Optional Render Texture / interaction field |

---

## 2. How the Effect Works

The grass shader evaluates each vertex and calculates how much the player affects it.

Conceptually:

1. Read the vertex's world-space position.
2. Compare its horizontal position with the player's position.
3. Calculate an influence weight from distance.
4. Determine a horizontal bending direction.
5. Scale the displacement by the vertex's height above the root.
6. Add optional wind movement.
7. Transform the modified vertex into clip space and render it.

A simplified model is:

\[
P' = P + D \cdot W_d \cdot W_h \cdot S
\]

Where:

- \(P\): original world-space vertex position.
- \(P'\): displaced world-space position.
- \(D\): horizontal bending direction.
- \(W_d\): distance-based influence in the range 0–1.
- \(W_h\): height mask, normally 0 at the root and 1 at the tip.
- \(S\): maximum displacement strength.

The actual implementation below adds a small wind displacement and avoids dividing by zero when calculating directions.

---

## 3. Prepare the Grass Mesh

### 3.1 Mesh requirements

The grass mesh needs enough vertices along each blade for bending to look smooth. A blade made from a single quad can still lean, but it cannot form a smooth curved shape. Add one or more horizontal edge loops if a visibly curved bend is required.

A practical blade mesh usually has:

- A root located at the base of the blade.
- A known local-up direction, usually local `Y`.
- Vertices distributed from root to tip.
- UVs for the grass texture.
- Normals suitable for the material's lighting model.

### 3.2 Root-to-tip mask

The shader needs to know which vertices should move. Two common approaches are:

**Option A — Local-space height**

If each blade mesh has its root at local `Y = 0` and its tip near a known height, normalize its local Y coordinate to create a height mask.

This is simple, but it is only reliable when the mesh's local coordinate convention is consistent.

**Option B — Vertex color**

Paint or generate a vertex-color channel where:

- Black (`0`) means the vertex is fixed at the root.
- White (`1`) means the vertex receives full bending.
- Intermediate values produce a smooth transition.

Vertex colors are more flexible for irregular meshes. The shader code below uses vertex color red (`COLOR.r`) as the height mask by default. If your mesh does not contain vertex colors, the value may default to white, causing the root to move. Use the local-height alternative or author vertex colors if needed.

### 3.3 Mesh pivot and orientation

For a single blade mesh, the pivot should normally be at the root. For a combined patch mesh, use vertex colors or a per-vertex height value that resets for each blade. A single global object-space Y mask will not correctly identify the root of every blade in a patch whose blades begin at different heights.

---

## 4. Baseline Implementation: HLSL Shader

The following is a compact URP-compatible shader example for a grass mesh. It demonstrates the deformation logic and a simple alpha-clipped texture. It is intended as a starting point, not a replacement for a production grass shader with all required shadow, depth, rendering-layer, and lighting passes.

Create a shader file named `InteractiveGrass.shader`.

```hlsl
Shader "Custom/InteractiveGrass"
{
    Properties
    {
        _BaseMap ("Grass Texture", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.4

        _InteractionRadius ("Interaction Radius", Range(0.1, 10)) = 2.0
        _BendStrength ("Bend Strength", Range(0, 2)) = 0.65
        _BendHeight ("Bend Height", Range(0, 2)) = 0.5
        _WindStrength ("Wind Strength", Range(0, 1)) = 0.08
        _WindFrequency ("Wind Frequency", Range(0, 10)) = 1.5
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
        }

        Cull Off
        ZWrite On
        AlphaToMask On

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
                float _InteractionRadius;
                float _BendStrength;
                float _BendHeight;
                float _WindStrength;
                float _WindFrequency;
            CBUFFER_END

            // Values supplied globally by a controller. The grass material itself
            // does not need a unique player-position property per blade.
            float4 _GrassPlayerPosition;
            float4 _GrassPlayerDirection;
            float _GrassInteractionEnabled;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);

                // Convert the original vertex to world space before deformation.
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);

                // Vertex color red is the root-to-tip mask: 0 at the root, 1 at the tip.
                float heightMask = saturate(input.color.r);

                // Compute distance in the horizontal XZ plane so terrain height
                // differences do not change the interaction radius.
                float2 toPlayer = _GrassPlayerPosition.xz - positionWS.xz;
                float distanceToPlayer = length(toPlayer);
                float safeRadius = max(_InteractionRadius, 0.001);

                // Smoothly fade the effect from full strength near the player
                // to zero at the interaction boundary.
                float distanceWeight = 1.0 - smoothstep(0.0, safeRadius, distanceToPlayer);

                // Prefer pushing grass away from the player's current position.
                // A small direction term from movement can be blended in by changing
                // the direction calculation below.
                float directionLengthSq = dot(toPlayer, toPlayer);
                float2 awayDirection = directionLengthSq > 0.0001
                    ? -toPlayer * rsqrt(directionLengthSq)
                    : float2(0.0, 0.0);

                // The global direction is the player's planar movement direction.
                float2 playerDirection = normalize(_GrassPlayerDirection.xz + float2(0.00001, 0.0));

                // Blend radial push-away with movement direction. A value closer
                // to 0 emphasizes radial push-away; closer to 1 emphasizes trailing.
                const float movementInfluence = 0.25;
                float2 bendDirection = normalize(lerp(awayDirection, -playerDirection, movementInfluence)
                                                  + float2(0.00001, 0.0));

                float interactionWeight = distanceWeight * saturate(_GrassInteractionEnabled);

                // Displace blade tips more than roots. _BendHeight adds a subtle
                // upward/downward tuning factor to the horizontal bend amount.
                float bendAmount = _BendStrength * interactionWeight * heightMask;
                positionWS.xz += bendDirection * bendAmount;

                // Simple world-space wind. This is deliberately subtle; production
                // wind can use a noise texture, gust field, or a dedicated wind system.
                float windPhase = _Time.y * _WindFrequency
                                + positionWS.x * 0.7
                                + positionWS.z * 0.45;
                float wind = sin(windPhase) * _WindStrength * heightMask;
                positionWS.x += wind;

                output.positionHCS = TransformWorldToHClip(positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 color = baseSample * _BaseColor;
                clip(color.a - _Cutoff);
                return color;
            }
            ENDHLSL
        }
    }
}
```

### Important shader notes

1. **The code is illustrative and should be validated in the target Unity/URP version.** Production grass may need `ShadowCaster`, `DepthOnly`, `DepthNormals`, and motion-vector passes, with matching deformation in each pass. Otherwise, shadows or depth may not line up with the visibly bent grass.
2. The shader uses an unlit forward pass for clarity. Integrate the deformation into the project's existing lit grass shader if lighting, fog, receive-shadows, and additional URP features are required.
3. The `_BendHeight` property is exposed for material tuning but is not used by the current displacement expression. Remove it or wire it into a clearly defined deformation model before shipping.
4. `normalize` around near-zero vectors needs care. The code adds a small epsilon, but a production implementation should use an explicit safe-normalize helper for every potentially zero direction.
5. For a mesh containing many blades in one combined patch, a single player position and simple height mask can work, but vertex colors must be authored per blade.
6. The example uses global interaction values. This is efficient for one local player, but it does not directly support many independent players with different interaction fields.

---

## 5. C# Controller for Global Shader Parameters

Create `GrassInteractionController.cs` and attach it to a persistent scene object or the player. Assign the player Transform and optionally specify the interaction radius.

```csharp
using UnityEngine;

/// <summary>
/// Publishes the player's world position and planar movement direction to all
/// grass materials that read the matching shader global properties.
///
/// This controller does not update each blade on the CPU. The grass shader
/// evaluates the interaction per vertex on the GPU.
/// </summary>
[DisallowMultipleComponent]
public sealed class GrassInteractionController : MonoBehaviour
{
    [Header("Player")]
    [SerializeField]
    [Tooltip("Transform whose position drives grass interaction.")]
    private Transform player;

    [SerializeField]
    [Tooltip("Optional movement source. If omitted, player displacement is used.")]
    private Transform movementSource;

    [Header("Interaction")]
    [SerializeField, Min(0.1f)]
    [Tooltip("Maximum horizontal distance affected by the player.")]
    private float interactionRadius = 2.0f;

    [SerializeField]
    [Tooltip("Whether this controller currently affects grass.")]
    private bool interactionEnabled = true;

    private static readonly int PlayerPositionId =
        Shader.PropertyToID("_GrassPlayerPosition");

    private static readonly int PlayerDirectionId =
        Shader.PropertyToID("_GrassPlayerDirection");

    private static readonly int InteractionEnabledId =
        Shader.PropertyToID("_GrassInteractionEnabled");

    private Vector3 previousPosition;
    private Vector3 movementDirection = Vector3.forward;

    private void OnEnable()
    {
        // Initialize the previous position to avoid a false movement direction
        // on the first frame after enabling this component.
        Transform source = movementSource != null ? movementSource : player;
        if (source != null)
        {
            previousPosition = source.position;
        }

        PublishShaderParameters();
    }

    private void LateUpdate()
    {
        if (player == null)
        {
            // Disable the interaction instead of leaving stale values active.
            Shader.SetGlobalFloat(InteractionEnabledId, 0.0f);
            return;
        }

        // Use the optional movement source when provided. This is useful when
        // the visual mesh and gameplay root have different transforms.
        Transform source = movementSource != null ? movementSource : player;
        Vector3 currentPosition = source.position;
        Vector3 displacement = currentPosition - previousPosition;

        // Grass bending is horizontal, so ignore vertical movement when deriving
        // the planar direction.
        displacement.y = 0.0f;

        if (displacement.sqrMagnitude > 0.0001f)
        {
            movementDirection = displacement.normalized;
        }

        previousPosition = currentPosition;
        PublishShaderParameters();
    }

    /// <summary>
    /// Publishes global values consumed by the interactive grass shader.
    /// Shader globals affect every compatible material rendered by the camera,
    /// so avoid using these names for unrelated systems.
    /// </summary>
    private void PublishShaderParameters()
    {
        Vector3 position = player != null ? player.position : transform.position;

        Shader.SetGlobalVector(
            PlayerPositionId,
            new Vector4(position.x, position.y, position.z, 1.0f));

        Shader.SetGlobalVector(
            PlayerDirectionId,
            new Vector4(
                movementDirection.x,
                movementDirection.y,
                movementDirection.z,
                0.0f));

        Shader.SetGlobalFloat(
            InteractionEnabledId,
            interactionEnabled && player != null ? 1.0f : 0.0f);
    }

    /// <summary>
    /// Changes the radius at runtime. The radius itself is a material property
    /// in the example shader, so update the grass material(s) as well.
    /// </summary>
    public void SetInteractionRadius(float radius)
    {
        interactionRadius = Mathf.Max(0.1f, radius);
    }

    /// <summary>
    /// Enables or disables grass interaction without disabling this component.
    /// </summary>
    public void SetInteractionEnabled(bool enabled)
    {
        interactionEnabled = enabled;
        PublishShaderParameters();
    }

    private void OnDisable()
    {
        // Clear the global enabled flag so the effect cannot remain enabled
        // after the controller is disabled.
        Shader.SetGlobalFloat(InteractionEnabledId, 0.0f);
    }
}
```

### Controller/shader integration checklist

- The shader declares `_GrassPlayerPosition`, `_GrassPlayerDirection`, and `_GrassInteractionEnabled` as globals.
- The controller writes those same global names.
- Set `_InteractionRadius` on every grass material using the shader.
- In the example above, `interactionRadius` is stored by the controller but is not published globally, because `_InteractionRadius` is a material property. To drive radius from C#, either:
  - change the shader to declare a global `_GrassInteractionRadius` and use it instead of `_InteractionRadius`; or
  - use a `MaterialPropertyBlock` or shared material update strategy appropriate to the renderer.
- Prefer not to mutate a shared material every frame. For a single global radius, a global shader property is usually simpler.
- If multiple grass systems need different player positions or radii, use separate interaction data or renderers rather than global values.

---

## 6. Recommended Improvement: Use One Global Radius

To make the controller's radius setting functional for all grass, add a global radius value.

### Shader changes

Declare:

```hlsl
float _GrassInteractionRadius;
```

Then replace:

```hlsl
float safeRadius = max(_InteractionRadius, 0.001);
```

with:

```hlsl
float safeRadius = max(_GrassInteractionRadius, 0.001);
```

### Controller changes

Add:

```csharp
private static readonly int InteractionRadiusId =
    Shader.PropertyToID("_GrassInteractionRadius");
```

Then publish the radius in `PublishShaderParameters()`:

```csharp
Shader.SetGlobalFloat(InteractionRadiusId, interactionRadius);
```

This avoids updating individual grass materials and keeps one source of truth for the radius.

---

## 7. Improve the Bending Direction

There are two common direction models.

### 7.1 Radial push-away

Use the vector from the player to the vertex:

\[
D_r = \frac{P_{xz}-C_{xz}}{\|P_{xz}-C_{xz}\|}
\]

This makes grass lean away from the character in every direction. It is a good starting point for grass around the character's feet.

### 7.2 Directional trailing

Use the negative player movement direction:

\[
D_m = -\frac{V_{xz}}{\|V_{xz}\|}
\]

This makes the grass lean opposite to the character's travel direction. It can create a trailing effect, but it is less spatially accurate for grass in front of the character.

### 7.3 Blend both

Blend radial and directional components, then normalize the result. Tune the blend to match the reference video. For a physically intuitive effect, radial push-away should usually dominate near the player's body, while the movement direction can add a subtle trailing bias.

A production implementation should handle zero-length vectors explicitly:

```hlsl
float2 SafeNormalize(float2 value, float2 fallback)
{
    float lengthSq = dot(value, value);
    return lengthSq > 1e-6 ? value * rsqrt(lengthSq) : fallback;
}
```

---

## 8. Optional Advanced Method: Persistent Interaction Field

The baseline shader only knows the player's current position. As soon as the player leaves a blade's interaction radius, that blade returns to its unbent state. This is inexpensive and often sufficient.

If the desired effect leaves a visible trail, the shader needs some memory of earlier player positions. A common approach is a **world-space interaction field** stored in a Render Texture.

### Concept

1. Define a world-space area covered by an interaction texture.
2. Each frame, fade the existing texture toward zero.
3. Draw a soft mark at the player's current position, optionally oriented by movement direction.
4. Sample the texture from the grass shader using each vertex's world-space XZ position.
5. Use the sampled value to control bend strength and/or direction.
6. Allow the texture value to decay, making the grass recover over time.

### Interaction-field data

A simple single-channel texture can store influence strength. More advanced layouts can encode:

- Red: X-axis bend direction or force.
- Green: Z-axis bend direction or force.
- Blue: influence strength.
- Alpha: optional age or secondary data.

The encoding must be consistent between the texture-writing pass and the grass shader. If using signed directions, remap signed values to the 0–1 texture range and decode them in the shader.

### World-to-texture mapping

The shader needs a mapping from world XZ to UV. For an axis-aligned field centered at `fieldCenter` with world size `fieldSize`:

\[
UV = \frac{P_{xz} - (fieldCenter - fieldSize/2)}{fieldSize}
\]

Sample only when the UV is within the texture bounds. For a moving player, the field can follow the player, but moving the field requires carefully preserving or scrolling previous data; otherwise, the trail will visibly jump.

### Decay and stability

A simple decay is:

\[
I_{t+\Delta t} = I_t \cdot e^{-k\Delta t}
\]

where \(k\) controls recovery speed. A ping-pong Render Texture setup or custom render pass can preserve the previous field while writing the next frame. Avoid reading from and writing to the same texture in a way that is unsupported by the graphics API.

### When to use this approach

Use an interaction field when:

- Grass remains bent after the player passes.
- Multiple past positions must contribute to the effect.
- The player creates a visible path through dense grass.
- A single current-position influence does not match the reference.

Do not add this complexity until the basic shader version is working and profiled.

---

## 9. Wind and Secondary Motion

Wind should be independent from player interaction.

A useful decomposition is:

\[
D_{total} = D_{interaction} + D_{wind}
\]

Keep wind subtle relative to the interaction displacement. A simple sine wave can demonstrate the effect, but large grass fields usually benefit from low-frequency noise or a wind field so every blade does not move in lockstep.

Recommendations:

- Use world-space coordinates so adjacent meshes have continuous wind phases.
- Scale wind displacement by the root-to-tip mask.
- Keep root movement close to zero.
- Avoid high-frequency oscillation that looks like vibration.
- Use a stable wind direction and slowly varying gust strength.
- Consider disabling or reducing wind at distant LOD levels.

---

## 10. Rendering and Performance

### 10.1 Avoid per-blade Transform updates

Updating thousands of `Transform` objects from C# each frame can create significant CPU overhead. Shader deformation allows each vertex to be processed in parallel by the GPU.

### 10.2 Instancing

For many repeated grass meshes:

- Use GPU instancing where supported by the rendering path.
- Ensure the material and shader support instancing.
- Keep instance-specific data compatible with the chosen rendering approach.
- If using `RenderMeshInstanced`, ensure the shader's instance setup matches Unity's requirements.
- Profile actual GPU and CPU cost; instancing reduces draw-call overhead but does not eliminate vertex processing.

### 10.3 SRP Batcher

The SRP Batcher and GPU instancing solve different problems. A shader's material constant-buffer layout should follow URP conventions, but the SRP Batcher does not automatically combine arbitrary grass objects into one draw call. Confirm compatibility in the Frame Debugger and Profiler.

### 10.4 Vertex cost

The shader evaluates interaction per vertex. Dense grass meshes with many subdivisions can be expensive, especially with multiple render passes. Use enough geometry for the desired curvature, but avoid unnecessary subdivisions.

### 10.5 Bounds and culling

Vertex displacement moves geometry outside its undeformed bounds. If the grass disappears too early or is culled incorrectly, enlarge renderer bounds or use an appropriate bounds strategy. For large GPU-driven grass systems, make sure the culling bounds account for the maximum bending and wind displacement.

### 10.6 Shadows and extra passes

If the forward pass bends grass but the shadow caster pass does not, the visible grass and its shadows will disagree. Apply the same deformation function in every pass that renders the geometry. Also evaluate whether grass needs to cast shadows at every distance.

### 10.7 Material updates

- Avoid `renderer.material` in per-frame loops because it can instantiate material copies.
- Avoid changing individual materials each frame if a global shader parameter is sufficient.
- Use `MaterialPropertyBlock` for per-renderer variation when compatible with the rendering architecture, while checking how it interacts with the SRP Batcher in the target Unity version.
- For one local player affecting all grass, global shader values are a simple baseline.

### 10.8 GPU-driven renderers

If the project uses a dedicated grass system, GPU Resident Drawer, indirect rendering, or a custom renderer, integrate the deformation into its shader and data flow rather than replacing its draw architecture. Check the system's supported shader hooks and culling behavior.

---

## 11. Suggested Default Parameters

Treat these as starting points, not universal values.

| Parameter | Initial value | Purpose |
|---|---:|---|
| Interaction radius | 1.5–2.5 world units | Horizontal reach of player influence |
| Maximum bend displacement | 0.2–0.7 world units | How far blade tips move |
| Root mask | 0 at root, 1 at tip | Keeps roots planted |
| Direction blend | 0.0–0.3 | Adds trailing direction to radial push-away |
| Wind strength | 0.02–0.1 world units | Subtle ambient motion |
| Recovery rate (field method) | Tune by playtest | How quickly trail data fades |

These values depend on the player scale, grass height, camera distance, and art direction.

---

## 12. Step-by-Step Unity Setup

1. **Create the shader.** Add `InteractiveGrass.shader` and validate it in the exact URP version used by the project.
2. **Create a material.** Assign the grass texture, base color, alpha cutoff, interaction radius, bend strength, and wind values.
3. **Prepare the mesh.** Ensure the root-to-tip mask is correct and that the mesh has enough vertices for the desired deformation.
4. **Assign the material.** Apply it to the grass mesh or integrate its deformation function into the existing grass shader.
5. **Add the controller.** Attach `GrassInteractionController` to a scene object and assign the player's Transform.
6. **Publish the radius globally.** Apply the global-radius changes in Section 6 so the controller's radius is actually used by the shader.
7. **Test the interaction.** Move the player slowly through the grass, then test running, stopping, and moving backward.
8. **Tune direction.** Adjust the radial/directional blend until the grass bends naturally.
9. **Check shadow and depth passes.** Ensure all relevant passes use the same vertex deformation.
10. **Profile.** Use Unity Profiler, Frame Debugger, and GPU profiling tools on target hardware.
11. **Add persistence only if required.** Implement the Render Texture interaction field after the baseline is stable.

---

## 13. Debugging Checklist

### Grass does not bend

- Verify the material uses the intended shader.
- Confirm the controller is enabled and has a valid player Transform.
- Confirm the global property names match exactly.
- Confirm the player and grass use the same world coordinate system.
- Check whether vertex color red is actually populated.
- Increase the interaction radius and bend strength temporarily.
- Inspect the mesh vertex count and root-to-tip mask.

### Entire blade moves, including the root

- The root-to-tip mask may be missing or incorrectly authored.
- Ensure root vertices have mask value 0.
- If using a combined patch, create a mask per blade rather than per patch height.

### Grass bends in the wrong direction

- Confirm whether the direction should be radial, movement-based, or blended.
- Check that vectors are calculated in world space.
- Remember that `playerPosition - vertexPosition` points toward the player; negate it to push away.

### Grass flickers or jitters

- Avoid deriving direction from tiny position changes without a threshold.
- Use a stable movement direction when the player stops.
- Check for inconsistent coordinate spaces.
- If using an interaction Render Texture, avoid unsupported read/write feedback and verify texture update order.

### Grass or shadows disappear unexpectedly

- Verify renderer bounds include maximum deformation.
- Ensure shadow/depth passes use the same deformation.
- Check alpha clipping, culling mode, and LOD transitions.

### Performance is poor

- Profile CPU and GPU separately.
- Reduce unnecessary vertices and shadow passes.
- Use appropriate instancing or the existing GPU grass renderer.
- Limit the number of interaction fields and texture updates.
- Test at the intended grass density and target resolution.

---

## 14. Production Architecture Recommendation

Keep responsibilities separate:

- **Player movement system:** owns gameplay movement and provides a stable world position/direction.
- **Grass interaction controller:** publishes or records interaction data; it should not manage grass mesh objects individually.
- **Grass shader:** evaluates distance, direction, height mask, and wind.
- **Optional interaction-field renderer:** maintains historical interaction data only if persistence is required.
- **Grass rendering system:** owns batching, LOD, culling, and shadow policy.

This separation follows single-responsibility principles and allows the movement system to change without rewriting the grass shader. The shader should receive simple data rather than depending on gameplay classes.

---

## 15. Final Recommendation

Start with the simplest implementation that matches the desired visual behavior:

1. Use vertex displacement in a URP grass shader.
2. Drive it with a global player position, movement direction, and interaction radius.
3. Use per-vertex root-to-tip weighting.
4. Add subtle wind independently.
5. Keep grass roots stable and include deformation in shadow/depth passes.
6. Profile with realistic grass density.
7. Introduce a Render Texture interaction field only if the grass must remember previous player positions.

The baseline is suitable for a prototype and can be integrated into a more advanced grass rendering pipeline. Before shipping, validate the shader against the project's Unity/URP version, rendering features, target hardware, and grass mesh layout.
