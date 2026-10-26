using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrewMonster.Scripts.Grass.Test
{
    /// <summary>
    /// Test harness for the grass interaction ("bends when the player walks through it"): spawns a square patch of
    /// billboard blades with <see cref="Graphics.RenderMeshInstanced"/> (the GPU-instancing draw path — one shared blade
    /// mesh, per-blade wind phase in <c>_Phase</c>).
    ///
    /// It only DRAWS. The interaction data is published by <see cref="GrassInteractionController"/> into shader
    /// globals, exactly like the technical doc describes
    /// (<c>Assets/PerfectWorld/Scripts/Grass/interactive_grass_bending_unity_urp.md</c> §5/§14): movement system →
    /// controller → shader. Nothing here ticks or owns gameplay state, which is why the field can be swapped for the
    /// production grass renderers without touching the effect.
    ///
    /// Why it exists: the whole effect lives in <c>BrewMonster/GrassBillboard</c> + that controller, so the only way to
    /// see it (and tune it) is to have grass on screen and something walking through it. Blade mesh, blade texture and
    /// material are all generated in code, so the test needs no art assets — the entire rig is one menu item
    /// (Tools &gt; BrewMonster &gt; Add Grass Trample Test Rig).
    ///
    /// Both can be replaced: assign <see cref="bladeMeshOverride"/> / <see cref="materialOverride"/> to instance a real
    /// blade card with a real material. The assigned assets are never modified — when something has to be changed on
    /// the material (GPU Instancing off, or the blade-height fallback below) a runtime copy is made and used instead.
    ///
    /// Not production code: the patch is drawn as one block every frame (no streaming, no chunked culling) and the
    /// instance data is rebuilt only when the Inspector changes something it depends on.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GrassTestField : MonoBehaviour
    {
        /// <summary>Shader the blades are drawn with — the one the trample lives in.</summary>
        private const string GrassShaderName = "BrewMonster/GrassBillboard";

        /// <summary>
        /// Blades per <see cref="Graphics.RenderMeshInstanced"/> call. Every instance of an instanced draw costs
        /// constant-buffer room: one objectToWorld matrix (64 B, or less with assumeuniformscaling) plus 4 B for each
        /// instanced property such as <c>_Phase</c>. That buffer is 64 KB on desktop and 16 KB on GL-class platforms,
        /// so a batch of 250 always fits — a full 1023 would silently be split, and on mobile down to ~125. This is a
        /// comfort margin, not a hard limit.
        /// </summary>
        private const int BladesPerBatch = 250;

        private static readonly int s_iBaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int s_iBaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int s_iPhase = Shader.PropertyToID("_Phase");
        private static readonly int s_iSightRange = Shader.PropertyToID("_SightRange");
        private static readonly int s_iWindSpeed = Shader.PropertyToID("_WindSpeed");
        private static readonly int s_iWindStrength = Shader.PropertyToID("_WindStrength");
        private static readonly int s_iTrampleDirectionBlend = Shader.PropertyToID("_TrampleDirectionBlend");
        private static readonly int s_iTramplePush = Shader.PropertyToID("_TramplePush");
        private static readonly int s_iTrampleLean = Shader.PropertyToID("_TrampleLean");
        private static readonly int s_iTrampleShake = Shader.PropertyToID("_TrampleShake");
        private static readonly int s_iTramplePress = Shader.PropertyToID("_TramplePress");
        private static readonly int s_iBladeBaseY = Shader.PropertyToID("_BladeBaseY");
        private static readonly int s_iBladeHeightScale = Shader.PropertyToID("_BladeHeightScale");

        private static readonly int s_iAmbientDay = Shader.PropertyToID("_AmbientDayColor");
        private static readonly int s_iAmbientNight = Shader.PropertyToID("_AmbientNightColor");
        private static readonly int s_iDirLightDay = Shader.PropertyToID("_DirLtColorDayColor");
        private static readonly int s_iDirLightNight = Shader.PropertyToID("_DirLtColorNightColor");
        private static readonly int s_iNightControl = Shader.PropertyToID("_NightColorControl");

        [Header("Assets (empty = generated in code)")]
        [SerializeField, Tooltip("Blade card to instance. The shader expects TEXCOORD1 (uv2) to carry x = wind phase, " +
                                 "y = normalized height 0..1; a mesh without a usable TEXCOORD1 still works, because " +
                                 "the height is then derived from the mesh bounds (_BladeBaseY/_BladeHeightScale).")]
        private Mesh bladeMeshOverride;

        [SerializeField, Tooltip("Grass material. Leave empty to build one from BrewMonster/GrassBillboard. An " +
                                 "assigned material keeps its own look values and is used as-is; only when something " +
                                 "must change (GPU Instancing off, blade-height fallback) is a runtime copy used.")]
        private Material materialOverride;

        [SerializeField, Tooltip("Also push this component's look/trample sliders into the ASSIGNED material " +
                                 "(through a runtime copy, so the asset stays untouched). Off = the material wins.")]
        private bool applyLookToMaterial;

        [SerializeField, Tooltip("Ignore TEXCOORD1 and take the blade height from the mesh bounds even when that " +
                                 "channel looks usable — needed for an imported card whose uv2 is a lightmap UV set " +
                                 "rather than a 0..1 base-to-tip height.")]
        private bool forceBladeHeightFromBounds;

        [Header("Field (centred on this transform)")]
        [SerializeField, Tooltip("Side length (m) of the grass patch.")]
        private float fieldSize = 26f;

        [SerializeField, Tooltip("Grid spacing (m) between blades before jitter. 0.3 over 26 m ≈ 7.5k blades.")]
        private float bladeSpacing = 0.3f;

        [SerializeField, Tooltip("Min/max uniform scale of a blade: the height in metres for the generated card " +
                                 "(a 1 m card), a multiplier for an assigned mesh. Uniform on purpose — the shader " +
                                 "declares instancing_options assumeuniformscaling, so a non-uniform scale would " +
                                 "break the normals.")]
        private Vector2 bladeScale = new Vector2(0.35f, 0.65f);

        [SerializeField, Tooltip("Deterministic jitter/phase, so the patch looks the same on every Play.")]
        private int randomSeed = 12345;

        [Header("Interaction look (the radius lives on the controller)")]
        [SerializeField, Tooltip("Controller whose state the HUD reports. Filled in automatically when the rig is " +
                                 "built from the menu; used for display only — the shader reads globals.")]
        private GrassInteractionController interaction;

        [SerializeField, Tooltip("How far (m) a blade at full strength is pushed away — grass bends away hard.")]
        private float tramplePush = 0.9f;

        [SerializeField, Tooltip("Extra lean (m at 8 m/s) in the direction of the run.")]
        private float trampleLean = 0.35f;

        [SerializeField, Range(0f, 1f), Tooltip("0 = blades only ever push away from the player, 1 = they only lean " +
                                                "against his travel direction (doc §7.3). In between blends both.")]
        private float trampleDirectionBlend = 0.25f;

        [SerializeField, Tooltip("Shake amplitude (m) while the player is walking through this blade.")]
        private float trampleShake = 0.18f;

        [SerializeField, Tooltip("How far (m) the blade tip is pressed down under the feet.")]
        private float tramplePress = 0.35f;

        [Header("Look")]
        [SerializeField, Tooltip("Wind amplitude of the ambient sway.")]
        private float windStrength = 0.18f;

        [SerializeField, Tooltip("Wind speed of the ambient sway.")]
        private float windSpeed = 1.6f;

        [SerializeField, Tooltip("Blades are clipped beyond this distance from the camera.")]
        private float sightRange = 250f;

        [Header("Lighting")]
        [SerializeField, Tooltip("Write the shared weather globals the grass shader reads. In the real game " +
                                 "CECSceneWeatherEnv owns them — here nothing does, so the grass would render black.")]
        private bool applyLightingGlobals = true;

        [SerializeField] private Color ambientDayColor = new Color(0.34f, 0.37f, 0.33f, 1f);
        [SerializeField] private Color ambientNightColor = new Color(0.05f, 0.06f, 0.09f, 1f);
        [SerializeField] private Color dirLightDayColor = new Color(0.85f, 0.85f, 0.78f, 1f);
        [SerializeField] private Color dirLightNightColor = new Color(0.10f, 0.12f, 0.18f, 1f);

        [Header("HUD")]
        [SerializeField, Tooltip("Show what the trample driver is publishing (activity, speed, wake points) — the " +
                                 "quickest way to tell whether the effect is driver-side or shader-side.")]
        private bool showHud = true;

        /// <summary>Controller the HUD reads its numbers from (the editor menu wires it up).</summary>
        public GrassInteractionController Interaction
        {
            get { return interaction; }
            set { interaction = value; }
        }

        /// <summary>Number of blades in the patch — handy for a quick sanity check in the Inspector.</summary>
        public int BladeCount { get; private set; }

        /// <summary>One instanced draw: matrices + the matching per-blade <c>_Phase</c> array.</summary>
        private struct Batch
        {
            public Matrix4x4[] matrices;
            public MaterialPropertyBlock props;
            public int count;
        }

        private Batch[] m_aBatches;
        private float m_fPatchSize;
        private Mesh m_bladeMesh;
        private Texture2D m_bladeTexture;
        private Material m_material;
        private RenderParams m_renderParams;

        //	What this component created itself and therefore has to destroy again — an assigned mesh/material belongs to
        //	the project and must survive a Play session.
        private bool m_bOwnsMesh;
        private bool m_bOwnsMaterial;
        private bool m_bApplyLook;

        //	Signature of the settings the current build was made from, so OnValidate can tell whether the Inspector
        //	actually changed something that needs a rebuild instead of rebuilding on every keystroke.
        private int m_iBuildSignature;

        private void OnEnable()
        {
            if (!SystemInfo.supportsInstancing)
            {
                Debug.LogError($"[{nameof(GrassTestField)}] This platform does not support GPU instancing.", this);
                enabled = false;
                return;
            }

            //	The HUD wants the controller's numbers; nothing else depends on it, so a missing reference is fine.
            if (interaction == null)
                interaction = FindAnyObjectByType<GrassInteractionController>();

            Initialize();
        }

        /// <summary>
        /// Build the mesh, the material, the instance batches and the render parameters. Also re-run while playing when
        /// the Inspector changes the mesh, the material or the patch geometry, so everything here is live.
        /// </summary>
        private void Initialize()
        {
            ReleaseRuntimeAssets();

            ResolveBladeMesh();
            m_material = ResolveMaterial();
            if (m_bladeMesh == null || m_material == null)
            {
                ReleaseRuntimeAssets();
                enabled = false;
                return;
            }

            m_iBuildSignature = BuildSignature();

            if (applyLightingGlobals)
                ApplyLightingGlobals();

            BuildInstances();

            //	Bounds of the whole patch: the patch square plus one blade, taken from the MESH bounds scaled the way the
            //	instances are — an assigned card is very often not a 1 m mesh, and too small a bound would cull grass
            //	that is still on screen. The interaction displaces vertices outside the mesh bounds, so the margin is
            //	grown by the largest bend the material can produce (doc §10.5: bounds must cover max deformation).
            float fMaxScale = Mathf.Max(bladeScale.x, bladeScale.y);
            Bounds pMeshBounds = m_bladeMesh.bounds;
            Vector3 vBladeSize = pMeshBounds.size * fMaxScale;
            float fBendMargin = Mathf.Max(0f, tramplePush) + Mathf.Max(0f, trampleLean) + Mathf.Max(0f, trampleShake);

            m_renderParams = new RenderParams(m_material)
            {
                worldBounds = new Bounds(
                    transform.position + Vector3.up * (pMeshBounds.center.y * fMaxScale),
                    new Vector3(m_fPatchSize + vBladeSize.x + 2f * fBendMargin,
                                vBladeSize.y,
                                m_fPatchSize + vBladeSize.z + 2f * fBendMargin)),
                camera = null,                                      //	every camera, Scene view included
                layer = gameObject.layer,
                receiveShadows = false,                             //	the shader has no shadow pass
                shadowCastingMode = ShadowCastingMode.Off,
                motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.Off,
            };
        }

        private void OnDisable()
        {
            ReleaseRuntimeAssets();

            m_aBatches = null;
            BladeCount = 0;
        }

        private void Update()
        {
            if (m_material == null || m_aBatches == null)
                return;

            //	Drawing only: the interaction globals were published by GrassInteractionController in LateUpdate, so the
            //	shader already has this frame's player state.
            for (int i = 0; i < m_aBatches.Length; i++)
            {
                Batch pBatch = m_aBatches[i];
                m_renderParams.matProps = pBatch.props;
                Graphics.RenderMeshInstanced(m_renderParams, m_bladeMesh, 0, pBatch.matrices, pBatch.count);
            }
        }

        private void OnValidate()
        {
            //	Live tuning: while playing, the sliders above drive the material that is in use straight away — but only
            //	when it is ours (generated, or a copy made for the assigned material); an assigned asset keeps its own
            //	look unless applyLookToMaterial says otherwise.
            if (m_material != null && m_bApplyLook)
                ApplyLook(m_material);

            if (applyLightingGlobals)
                ApplyLightingGlobals();

            //	Picking another mesh/material, or changing the patch geometry, rebuilds on the spot — no need to
            //	disable/enable the component to try a different blade card.
            if (Application.isPlaying && isActiveAndEnabled && m_material != null && BuildSignature() != m_iBuildSignature)
                Initialize();
        }

        /// <summary>
        /// Fingerprint of every setting a rebuild depends on — picked assets, patch geometry, and the two toggles that
        /// decide whether the assigned material is copied or overridden.
        /// </summary>
        private int BuildSignature()
        {
            unchecked
            {
                //	Object.GetHashCode() of a UnityEngine.Object is its instance id (GetInstanceID itself is obsolete in
                //	Unity 6), which is exactly the "is this still the same asset?" the signature needs.
                int iHash = 17;
                iHash = iHash * 31 + (bladeMeshOverride != null ? bladeMeshOverride.GetHashCode() : 0);
                iHash = iHash * 31 + (materialOverride != null ? materialOverride.GetHashCode() : 0);
                iHash = iHash * 31 + (materialOverride == null || applyLookToMaterial ? 1 : 0);
                iHash = iHash * 31 + (forceBladeHeightFromBounds ? 1 : 0);
                iHash = iHash * 31 + fieldSize.GetHashCode();
                iHash = iHash * 31 + bladeSpacing.GetHashCode();
                iHash = iHash * 31 + bladeScale.x.GetHashCode();
                iHash = iHash * 31 + bladeScale.y.GetHashCode();
                iHash = iHash * 31 + randomSeed;
                return iHash;
            }
        }

        private void OnDrawGizmosSelected()
        {
            float fSize = m_fPatchSize > 0.01f ? m_fPatchSize : fieldSize;

            Gizmos.color = new Color(0.35f, 0.9f, 0.35f, 0.35f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(new Vector3(0f, bladeScale.y * 0.5f, 0f),
                                new Vector3(fSize, bladeScale.y, fSize));
        }

        private void OnGUI()
        {
            if (!showHud || m_material == null)
                return;

            GUILayout.BeginArea(new Rect(12f, Screen.height - 104f, 380f, 96f));
            GUILayout.Box("Grass interaction");
            GUILayout.Label($"blades {BladeCount}   batches {(m_aBatches != null ? m_aBatches.Length : 0)}");

            if (interaction == null)
            {
                GUILayout.Label("no GrassInteractionController — grass will not bend");
            }
            else
            {
                GUILayout.Label($"activity {interaction.Activity:0.00}   speed {interaction.Speed:0.00} m/s");
                GUILayout.Label($"radius {interaction.InteractionRadius:0.00} m   " +
                                $"enabled {interaction.InteractionEnabled}");
            }
            GUILayout.EndArea();
        }

        // ---------------------------------------------------------------------------------------------------------
        //	Instances
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Jittered grid of blades, split into batches that each fit one instanced draw.</summary>
        private void BuildInstances()
        {
            float fSpacing = Mathf.Max(0.05f, bladeSpacing);
            int iPerSide = Mathf.Max(1, Mathf.RoundToInt(fieldSize / fSpacing));
            int iTotal = iPerSide * iPerSide;
            BladeCount = iTotal;

            //	Snap the patch size to the grid so the bounds, the gizmo and the blades agree even when fieldSize is
            //	not a multiple of the spacing.
            m_fPatchSize = iPerSide * fSpacing;

            m_aBatches = new Batch[Mathf.CeilToInt(iTotal / (float)BladesPerBatch)];

            //	System.Random, not UnityEngine.Random: the patch must not depend on (or disturb) the global seed.
            System.Random pRandom = new System.Random(randomSeed);
            int iIndex = 0;

            for (int iBatch = 0; iBatch < m_aBatches.Length; iBatch++)
            {
                int iCount = Mathf.Min(BladesPerBatch, iTotal - iBatch * BladesPerBatch);
                Matrix4x4[] aMatrices = new Matrix4x4[iCount];
                float[] aPhases = new float[iCount];

                for (int i = 0; i < iCount; i++, iIndex++)
                {
                    int iX = iIndex % iPerSide;
                    int iZ = iIndex / iPerSide;

                    float fX = (iX + 0.5f) * fSpacing - m_fPatchSize * 0.5f;
                    float fZ = (iZ + 0.5f) * fSpacing - m_fPatchSize * 0.5f;
                    float fJitter = fSpacing * 0.45f;
                    fX += ((float)pRandom.NextDouble() * 2f - 1f) * fJitter;
                    fZ += ((float)pRandom.NextDouble() * 2f - 1f) * fJitter;

                    float fHeight = Mathf.Lerp(bladeScale.x, bladeScale.y, (float)pRandom.NextDouble());
                    Quaternion qYaw = Quaternion.Euler(0f, (float)pRandom.NextDouble() * 360f, 0f);

                    //	Position follows the field transform, but the blade scale stays in metres and uniform — keep this
                    //	transform unscaled, or the mesh would no longer match the shader's assumeuniformscaling.
                    Vector3 vWorld = transform.TransformPoint(new Vector3(fX, 0f, fZ));
                    aMatrices[i] = Matrix4x4.TRS(vWorld, transform.rotation * qYaw, Vector3.one * fHeight);

                    //	Per-blade wind phase: the instanced draw path cannot bake it into a shared mesh, so it goes
                    //	through _Phase (UNITY_INSTANCING_BUFFER) exactly like GrassGpuInstancedRenderer does it.
                    aPhases[i] = (float)pRandom.NextDouble() * Mathf.PI * 2f;
                }

                MaterialPropertyBlock pProps = new MaterialPropertyBlock();
                pProps.SetFloatArray(s_iPhase, aPhases);

                m_aBatches[iBatch] = new Batch { matrices = aMatrices, props = pProps, count = iCount };
            }
        }

        // ---------------------------------------------------------------------------------------------------------
        //	Blade mesh / texture / material (generated, no assets)
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// One crossed-quad blade card: height 1 with the base at y = 0 (instances scale it), width baked in.
        /// TEXCOORD1 carries what the shader needs per blade: x = wind phase (0 here — the instanced path uses
        /// <c>_Phase</c>) and y = normalized height (0 = base, 1 = tip), which is what makes the tip bend and the root
        /// stay put.
        /// </summary>
        private static Mesh BuildBladeMesh()
        {
            const float fHalfWidth = 0.125f;

            Vector3[] aVertices =
            {
                new Vector3(-fHalfWidth, 0f, 0f), new Vector3(fHalfWidth, 0f, 0f),
                new Vector3(fHalfWidth, 1f, 0f), new Vector3(-fHalfWidth, 1f, 0f),
                new Vector3(0f, 0f, -fHalfWidth), new Vector3(0f, 0f, fHalfWidth),
                new Vector3(0f, 1f, fHalfWidth), new Vector3(0f, 1f, -fHalfWidth),
            };

            Vector2[] aUv =
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            };

            Vector2[] aBladeData =
            {
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            };

            Vector3[] aNormals =
            {
                new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, -1f),
                new Vector3(-1f, 0f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(-1f, 0f, 0f),
            };

            int[] aTriangles = { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };

            Mesh pMesh = new Mesh { name = "GrassBladeTest" };
            pMesh.SetVertices(aVertices);
            pMesh.SetUVs(0, aUv);
            pMesh.SetUVs(1, aBladeData);                            //	TEXCOORD1 = uv2Blade in the shader
            pMesh.SetNormals(aNormals);
            pMesh.SetTriangles(aTriangles, 0);
            pMesh.bounds = new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(fHalfWidth * 2f, 1f, fHalfWidth * 2f));
            return pMesh;
        }

        /// <summary>
        /// Procedural blade card: three tapered blades on alpha, so one quad reads as a small tuft. Generated so the
        /// test needs no imported texture; alpha is hard-edged (cutout + alpha-to-coverage in the shader).
        /// </summary>
        private static Texture2D BuildBladeTexture()
        {
            const int iWidth = 64;
            const int iHeight = 128;

            float[] aBaseX = { 0.30f, 0.51f, 0.70f };
            float[] aLean = { 0.11f, -0.03f, 0.13f };
            float[] aHalfWidth = { 0.055f, 0.070f, 0.050f };
            Color[] aRootColor =
            {
                new Color(0.17f, 0.29f, 0.09f),
                new Color(0.21f, 0.33f, 0.11f),
                new Color(0.15f, 0.27f, 0.08f),
            };
            Color cTipColor = new Color(0.55f, 0.72f, 0.30f);

            Texture2D pTexture = new Texture2D(iWidth, iHeight, TextureFormat.RGBA32, true, false)
            {
                name = "GrassBladeTest",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 4,
            };

            Color[] aPixels = new Color[iWidth * iHeight];
            for (int i = 0; i < aPixels.Length; i++)
                aPixels[i] = Color.clear;

            float fTexel = 1f / iWidth;

            for (int iBlade = 0; iBlade < aBaseX.Length; iBlade++)
            {
                for (int iY = 0; iY < iHeight; iY++)
                {
                    float fT = iY / (iHeight - 1f);                 //	0 = root, 1 = tip
                    float fTaper = Mathf.Pow(1f - fT, 0.65f);
                    float fCenterX = aBaseX[iBlade] + aLean[iBlade] * fT * fT;
                    float fHalf = aHalfWidth[iBlade] * fTaper + fTexel * 0.35f;
                    Color cColor = Color.Lerp(aRootColor[iBlade], cTipColor, fT * fT);

                    int iMinX = Mathf.Max(0, Mathf.FloorToInt((fCenterX - fHalf) * iWidth));
                    int iMaxX = Mathf.Min(iWidth - 1, Mathf.CeilToInt((fCenterX + fHalf) * iWidth));

                    for (int iX = iMinX; iX <= iMaxX; iX++)
                    {
                        float fPixelX = (iX + 0.5f) / iWidth;
                        float fCoverage = Mathf.Clamp01(
                            (fHalf - Mathf.Abs(fPixelX - fCenterX)) / (fTexel * 0.75f) + 0.5f);
                        if (fCoverage <= 0f)
                            continue;

                        int iPixel = iY * iWidth + iX;
                        if (aPixels[iPixel].a >= fCoverage)
                            continue;                               //	an overlapping blade already covered it

                        cColor.a = fCoverage;
                        aPixels[iPixel] = cColor;
                    }
                }
            }

            pTexture.SetPixels(aPixels);
            pTexture.Apply(true, false);
            return pTexture;
        }

        /// <summary>The assigned blade card, or the generated one. An assigned mesh is never modified or destroyed.</summary>
        private void ResolveBladeMesh()
        {
            if (bladeMeshOverride != null)
            {
                m_bladeMesh = bladeMeshOverride;
                m_bOwnsMesh = false;
                return;
            }

            m_bladeMesh = BuildBladeMesh();
            m_bOwnsMesh = true;
        }

        /// <summary>
        /// Pick the material to draw with: the generated one, the assigned asset as-is, or — when something has to be
        /// changed on it — a runtime copy. Writing to the copy instead of the asset is what keeps the project's material
        /// clean while still letting the test run, and it is what makes the look sliders safe to touch.
        /// </summary>
        private Material ResolveMaterial()
        {
            bool bHeightFallback = TryGetBladeHeightFallback(out float p_fBaseY, out float p_fHeightScale);

            if (materialOverride == null)
            {
                Material pGenerated = BuildGeneratedMaterial(p_fBaseY, p_fHeightScale, bHeightFallback);
                if (pGenerated == null)
                    return null;

                m_bOwnsMaterial = true;
                m_bApplyLook = true;
                return pGenerated;
            }

            bool bNeedsCopy = !materialOverride.enableInstancing || bHeightFallback || applyLookToMaterial;

            Material pMaterial = bNeedsCopy ? new Material(materialOverride) : materialOverride;
            m_bOwnsMaterial = bNeedsCopy;
            m_bApplyLook = applyLookToMaterial;

            if (pMaterial.shader == null || pMaterial.shader.name != GrassShaderName)
            {
                Debug.LogWarning($"[{nameof(GrassTestField)}] Material '{materialOverride.name}' does not use " +
                                 $"'{GrassShaderName}', so it will draw the blades but will not bend them — the " +
                                 "trample lives in that shader.", this);
            }

            if (bNeedsCopy)
            {
                pMaterial.name = materialOverride.name + " (runtime copy)";

                if (!materialOverride.enableInstancing)
                {
                    pMaterial.enableInstancing = true;
                    Debug.Log($"[{nameof(GrassTestField)}] '{materialOverride.name}' has GPU Instancing off, which " +
                              "RenderMeshInstanced requires — the runtime copy has it on. Tick it on the asset itself " +
                              "to be explicit.", this);
                }

                if (bHeightFallback)
                {
                    pMaterial.SetFloat(s_iBladeBaseY, p_fBaseY);
                    pMaterial.SetFloat(s_iBladeHeightScale, p_fHeightScale);
                }

                if (applyLookToMaterial)
                    ApplyLook(pMaterial);
            }

            return pMaterial;
        }

        private Material BuildGeneratedMaterial(float p_fBaseY, float p_fHeightScale, bool p_bHeightFallback)
        {
            Shader pShader = Shader.Find(GrassShaderName);
            if (pShader == null)
            {
                Debug.LogError($"[{nameof(GrassTestField)}] Shader '{GrassShaderName}' not found — is " +
                               "Assets/ModelRenderer/Art/Shaders/GrassBillboard.shader imported?", this);
                return null;
            }

            m_bladeTexture = BuildBladeTexture();

            Material pMaterial = new Material(pShader)
            {
                name = "GrassTestField (runtime)",
                enableInstancing = true,
            };
            pMaterial.SetTexture(s_iBaseMap, m_bladeTexture);
            pMaterial.SetColor(s_iBaseColor, Color.white);
            ApplyLook(pMaterial);

            //	The generated card bakes both TEXCOORD1 values, so it only needs the bounds fallback when a custom mesh
            //	was assigned without a blade-height channel.
            pMaterial.SetFloat(s_iBladeBaseY, p_bHeightFallback ? p_fBaseY : 0f);
            pMaterial.SetFloat(s_iBladeHeightScale, p_bHeightFallback ? p_fHeightScale : 0f);
            return pMaterial;
        }

        /// <summary>
        /// A blade card only bends where the shader can tell base from tip. That is TEXCOORD1.y (the height baked by the
        /// renderers) for the generated card, and the _BladeBaseY/_BladeHeightScale fallback on the bounds for an
        /// imported mesh that carries no such channel — a plain uv2 from an FBX looks present but holds texture
        /// coordinates, so the values are checked, not just the attribute.
        /// </summary>
        private bool TryGetBladeHeightFallback(out float p_fBaseY, out float p_fHeightScale)
        {
            p_fBaseY = 0f;
            p_fHeightScale = 0f;

            if (m_bladeMesh == null)
                return false;

            if (!forceBladeHeightFromBounds && HasBladeHeightChannel(m_bladeMesh))
                return false;

            Bounds pBounds = m_bladeMesh.bounds;
            p_fBaseY = pBounds.min.y;
            p_fHeightScale = pBounds.size.y > 1e-4f ? 1f / pBounds.size.y : 0f;

            Debug.Log($"[{nameof(GrassTestField)}] Mesh '{m_bladeMesh.name}' has no usable TEXCOORD1 blade height — " +
                      $"deriving it from the bounds instead (_BladeBaseY = {p_fBaseY:0.###}, " +
                      $"_BladeHeightScale = {p_fHeightScale:0.###}). Bake TEXCOORD1.y = 0..1 from base to tip to skip " +
                      "this.", this);
            return true;
        }

        /// <summary>True when TEXCOORD1.y actually reaches the tip (0.9+), i.e. holds a normalized blade height.</summary>
        private static bool HasBladeHeightChannel(Mesh pMesh)
        {
            if (!pMesh.HasVertexAttribute(VertexAttribute.TexCoord1))
                return false;

            List<Vector2> aBladeData = new List<Vector2>();
            pMesh.GetUVs(1, aBladeData);
            if (aBladeData.Count == 0)
                return false;

            for (int i = 0; i < aBladeData.Count; i++)
            {
                if (aBladeData[i].y > 0.9f)
                    return true;
            }

            return false;
        }

        /// <summary>Destroy whatever this component created itself; assigned assets are left alone.</summary>
        private void ReleaseRuntimeAssets()
        {
            if (m_bOwnsMaterial)
                DestroySafely(m_material);

            DestroySafely(m_bladeTexture);

            if (m_bOwnsMesh)
                DestroySafely(m_bladeMesh);

            m_bladeTexture = null;
            m_material = null;
            m_bladeMesh = null;

            m_bOwnsMesh = false;
            m_bOwnsMaterial = false;
            m_bApplyLook = false;
        }

        /// <summary>Push the look/trample sliders into the material (also used for live tuning through OnValidate).</summary>
        private void ApplyLook(Material pMaterial)
        {
            pMaterial.SetFloat(s_iSightRange, sightRange);
            pMaterial.SetFloat(s_iWindSpeed, windSpeed);
            pMaterial.SetFloat(s_iWindStrength, windStrength);
            pMaterial.SetFloat(s_iTramplePush, tramplePush);
            pMaterial.SetFloat(s_iTrampleLean, trampleLean);
            pMaterial.SetFloat(s_iTrampleDirectionBlend, trampleDirectionBlend);
            pMaterial.SetFloat(s_iTrampleShake, trampleShake);
            pMaterial.SetFloat(s_iTramplePress, tramplePress);
        }

        /// <summary>
        /// The shared weather globals the shader multiplies the texture with. Without them the grass is lit with
        /// (0, 0, 0) and reads as black — in the real game CECSceneWeatherEnv writes them every frame.
        /// </summary>
        private void ApplyLightingGlobals()
        {
            Shader.SetGlobalColor(s_iAmbientDay, ambientDayColor);
            Shader.SetGlobalColor(s_iAmbientNight, ambientNightColor);
            Shader.SetGlobalColor(s_iDirLightDay, dirLightDayColor);
            Shader.SetGlobalColor(s_iDirLightNight, dirLightNightColor);
            Shader.SetGlobalFloat(s_iNightControl, 0f);
        }

        private static void DestroySafely(Object pObject)
        {
            if (pObject == null)
                return;

            if (Application.isPlaying)
                Destroy(pObject);
            else
                DestroyImmediate(pObject);
        }
    }
}
