// Billboard grass shader — used by BOTH grass area renderers (CELGrassLand is the A/B switch):
//   • GrassInstancedRenderer     : ONE merged mesh + MeshRenderer per loaded area  → phase arrives in mesh uv2;
//   • GrassGpuInstancedRenderer  : Graphics.RenderMeshInstanced per batch of blades → phase arrives in _Phase.
// The two phase sources are ADDED, so each renderer only fills its own one:
//   merged mesh → uv2.x = phase, _Phase unset (0);    instanced → uv2.x = 0, _Phase = phase.
//
// TEXCOORD1 (uv2) carries two per-vertex values, both filled by whichever renderer is active:
//   x = per-blade wind phase    y = normalized blade height (0 = base, 1 = tip)
// The height is what bends only the TIP of a blade — for the wind and for the player interaction — instead of sliding
// the whole sprite. Baked per vertex (merged mesh) or into a per-type copy of the blade mesh (instanced);
// _BladeBaseY/_BladeHeightScale is the fallback for a blade mesh that could not be baked (doc §3.2, option A:
// local-space height normalized by the mesh bounds).
//
// Port of the legacy grass look (CElement CELGrassType params — T0463):
//   • ECWDFILEGRASSTYPE::fSoftness  → wind stiffness   (_Softness)
//   • ECWDFILEGRASSTYPE::fSightRange → per-type cull distance (_SightRange)
//   • ECWDFILEGRASSTYPE::bAlphaBlend/iAlphaRef → alpha test mode (_Cutoff; a blend pass is a later step)
//   • per-blade wind phase          → mesh uv2.x (TEXCOORD1) and/or _Phase (instancing buffer), see above
//
// Ambient/dir-light colours come from the shared weather globals written by CECSceneWeatherEnv
// (GlobalShaderVariableSetting): _AmbientDayColor / _AmbientNightColor / _NightColorControl,
// so grass is lit consistently with terrain through the day/night cycle.
//
// Player interaction ("grass bends when he walks through it"). This follows
// Assets/PerfectWorld/Scripts/Grass/interactive_grass_bending_unity_urp.md:
//   • the data is GLOBAL, published once per frame by GrassInteractionController (§5) — nothing per-material, so both
//     draw paths and every grass type react identically;
//   • the radius is GLOBAL too (§6: "use one global radius") — one source of truth, no material updates per frame;
//   • falloff is 1 - smoothstep(0, radius, distance), so there is no hard circular edge (§4);
//   • direction is radial push-away blended with the travel direction (§7.3), every normalisation is guarded (§7.3);
//   • wind stays independent of the interaction and uses a world-space phase (§9);
//   • the interaction vector is built ONCE PER BLADE ROOT (player → root, see GrassBladeRootWS) instead of per vertex,
//     so a card bends as one rigid unit instead of shearing, and the wind displacement cannot feed back into it.
//   _GrassPlayerPosition      xyz = player world position, w = 1
//   _GrassPlayerDirection     xyz = planar velocity (normalized = travel direction, length = speed), w = movement
//                             activity 0..1 (0 = standing still, so the grass relaxes under his feet)
//   _GrassInteractionRadius   radius (m)
//   _GrassInteractionEnabled  0/1 — 0 short-circuits the effect for every vertex
// A blade inside the radius is bent away from the player, leans the way he runs, shakes while he walks through it and
// has its tip pressed down. The base never moves (the displacement is weighted by the normalized blade height), and
// the falloff going to 0 at the radius is what returns the grass to its rest pose — the doc's baseline recovers
// immediately because only the CURRENT position is known (no trail memory, see §8).
//
// Renderer bounds: the displacement moves geometry outside the mesh's own bounds, so whatever draws this shader must
// report bounds that include _TramplePush + _TrampleLean + _TrampleShake (doc §10.5) or blades will be culled early.
Shader "BrewMonster/GrassBillboard"
{
    Properties
    {
        [MainTexture] _BaseMap ("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        _SightRange ("Sight Range (m)", Float) = 100
        _Softness ("Wind Softness (0..2)", Range(0.0, 2.0)) = 1.0
        _WindSpeed ("Wind Speed", Float) = 1.6
        _WindStrength ("Wind Strength", Range(0.0, 1.0)) = 0.18
        _TramplePush ("Bend Push (m)", Float) = 0.9
        _TrampleLean ("Bend Lean (m at 8 m/s)", Float) = 0.35
        _TrampleDirectionBlend ("Radial vs Travel Blend (0..1)", Range(0.0, 1.0)) = 0.25
        _TrampleShake ("Bend Shake (m)", Float) = 0.18
        _TrampleShakeSpeed ("Bend Shake Speed", Float) = 7.0
        _TramplePress ("Bend Press (m, tip pushed down)", Float) = 0.35
        _BladeBaseY ("Blade Base Y (local, fallback only)", Float) = 0.0
        _BladeHeightScale ("Blade Height Scale (1/m, 0 = take UV1.y)", Float) = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "GrassForward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off          // crossed quads are visible from both sides
            ZWrite On
            AlphaToMask On

            HLSLPROGRAM
            #pragma vertex GrassVert
            #pragma fragment GrassFrag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling   // blade instances are uniformly scaled; dropping
                                                              // worldToObject doubles the per-call instance cap
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _Cutoff;
                float _SightRange;
                float _Softness;
                float _WindSpeed;
                float _WindStrength;
                float _TramplePush;
                float _TrampleLean;
                float _TrampleDirectionBlend;
                float _TrampleShake;
                float _TrampleShakeSpeed;
                float _TramplePress;
                float _BladeBaseY;
                float _BladeHeightScale;
            CBUFFER_END

            //	Per-instance wind phase of the INSTANCED draw path (GrassGpuInstancedRenderer writes it with
            //	MaterialPropertyBlock.SetFloatArray). A shared blade mesh cannot carry a per-blade phase in uv2 like
            //	the merged mesh does. Non-instanced draws (MeshRenderer of the merged-mesh path) read the default 0,
            //	so both renderers produce the same wind.
            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float, _Phase)
            UNITY_INSTANCING_BUFFER_END(Props)

            //	Global weather colours (set with Shader.SetGlobalColor by CECSceneWeatherEnv).
            half4 _AmbientDayColor;
            half4 _AmbientNightColor;
            half4 _DirLtColorDayColor;
            half4 _DirLtColorNightColor;
            float _NightColorControl;

            //	Global player interaction, published once per frame by GrassInteractionController — deliberately NOT in
            //	UnityPerMaterial, so every grass type and both draw paths pick the same state up (§5) and so the radius
            //	can be one global value instead of a per-material one (§6).
            float4 _GrassPlayerPosition;                    //	xyz world position, w = 1
            float4 _GrassPlayerDirection;                   //	xyz planar velocity, w = activity 0..1
            float _GrassInteractionRadius;                  //	metres
            float _GrassInteractionEnabled;                 //	0/1

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                //	x = per-blade wind phase, y = normalized blade height 0..1 (0 = base, 1 = tip).
                //	Merged mesh: baked per vertex by GrassInstancedRenderer.
                //	Instanced: baked into the per-type copy of the blade mesh by GrassGpuInstancedRenderer.
                float2 uv2Blade : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            /// <summary>
            /// Normalisation that cannot blow up on a zero-length vector (§7.3) — the player exactly on a blade's XZ
            /// position, or standing still, are both normal cases, not edge cases.
            /// </summary>
            float2 GrassSafeNormalize(float2 p_vValue, float2 p_vFallback)
            {
                float fLengthSq = dot(p_vValue, p_vValue);
                return fLengthSq > 1e-6 ? p_vValue * rsqrt(fLengthSq) : p_vFallback;
            }

            /// <summary>
            /// World-space ROOT of the blade a vertex belongs to — the anchor of the interaction vector. All vertices of
            /// one blade get the same value, so direction, distance and falloff stop varying across the card (which
            /// sheared the quads when every vertex computed its own).
            ///
            /// The two draw paths need different sources:
            ///   • instanced (GrassGpuInstancedRenderer / RenderMeshInstanced): the mesh holds ONE blade, so the object
            ///     origin is the root — exact for any card shape, wide or lopsided;
            ///   • merged mesh (GrassInstancedRenderer): one mesh holds a whole area, so the object origin is the area,
            ///     not the blade. The vertex is flattened onto the base plane instead; its XZ is the blade's XZ for a
            ///     vertical card. A card that is very wide in XZ (or leaned) would need a per-blade root baked into a
            ///     spare vertex channel, e.g. TEXCOORD2 — doc §3.3.
            /// </summary>
            float3 GrassBladeRootWS(float3 positionOS)
            {
#ifdef UNITY_INSTANCING_ENABLED
                return TransformObjectToWorld(float3(0.0, _BladeBaseY, 0.0));
#else
                return TransformObjectToWorld(float3(positionOS.x, _BladeBaseY, positionOS.z));
#endif
            }

            /// <summary>
            /// Displacement of one vertex caused by the player walking through the grass — the baseline model of the
            /// technical doc: radial push-away, blended with the travel direction, weighted by a smooth distance
            /// falloff and by the normalized blade height so the root stays planted.
            ///
            /// The vector is built from the BLADE ROOT (<paramref name="rootWS"/>) instead of the vertex position, so
            /// direction, distance and falloff are the same for every vertex of a blade: the card leans as one rigid
            /// piece instead of each vertex taking its own direction (which sheared the quad), and the wind
            /// displacement can no longer feed back into the interaction. Only the height mask varies along the blade,
            /// which is exactly what keeps the root planted and folds the tip over.
            /// </summary>
            float3 GrassInteractionOffset(float3 verWS, float3 rootWS, float fPhase, float fHeight01)
            {
                //	Player → blade root (doc §7.1: pushing along this vector bends the grass AWAY from the player).
                float2 vToRoot = rootWS.xz - _GrassPlayerPosition.xz;
                float fRadius = max(_GrassInteractionRadius, 0.001);
                float2 vToVertex = verWS.xz - _GrassPlayerPosition.xz;
                float fDistance = length(vToRoot);
                // float fDistance = length(vToVertex);

                if (fDistance >= fRadius)
                    return float3(0.0, 0.0, 0.0);                //	outside the interaction radius

                //	Smooth falloff: 1 at the player, 0 at the radius — no hard circular boundary.
                float fWeight = 1.0 - smoothstep(0.0, fRadius, fDistance);

                //	Push away from the player's body, blended towards the trailing side of his travel direction.
                float2 vAway = GrassSafeNormalize(vToRoot, float2(0.0, 1.0));
                float2 vVelocity = _GrassPlayerDirection.xz;
                float fSpeed = length(vVelocity);
                float2 vTravel = GrassSafeNormalize(vVelocity, vAway);
                float2 vBendDir = GrassSafeNormalize(
                    lerp(vAway, -vTravel, saturate(_TrampleDirectionBlend)), vAway);

                //	Bend profile: not just the tip — the whole upper half folds, which is what reads as "grass being
                //	pushed over" instead of "sprite sliding".
                float fBend = fHeight01 * (0.35 + 0.65 * fHeight01);

                //	(1) Bend the blades away from the player.
                float2 vOffset = vBendDir * (fWeight * _TramplePush * fBend);

                //	(2) Lean them the way he runs (full amplitude at ~8 m/s), only while he actually moves.
                if (fSpeed > 0.001)
                {
                    vOffset -= vTravel *
                               (fWeight * _TrampleLean * _GrassPlayerDirection.w * min(fSpeed, 8.0) * 0.125 * fBend);
                    // vOffset += vBendDir *
                    //            (fWeight * _TrampleLean * _GrassPlayerDirection.w * min(fSpeed, 8.0) * 0.125 * fBend);
                    // vOffset += vTravel *
                    //            (fWeight * _TrampleLean * _GrassPlayerDirection.w * min(fSpeed, 8.0) * 0.125 * fBend);
                               
                }

                //	(3) Shake while he is walking through this blade (stops as soon as he stops, the bend does not).
                float fWobble = sin(_Time.y * _TrampleShakeSpeed + fPhase * 3.0 + fDistance * 4.0);
                vOffset += vBendDir * (fWobble * _TrampleShake * fWeight * _GrassPlayerDirection.w * fHeight01);

                //	(4) Press the tip down so the clump looks flattened under the feet.
                float fPress = -_TramplePress * fWeight * fHeight01 * fHeight01;

                return float3(vOffset.x, fPress, vOffset.y);
            }

            Varyings GrassVert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);

                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);

                //	The phase comes from the mesh uv2 (merged-mesh renderer) and/or _Phase (instanced renderer);
                //	each renderer fills exactly one of them, so the sum is the per-blade phase in both cases.
                float fPhase = v.uv2Blade.x + UNITY_ACCESS_INSTANCED_PROP(Props, _Phase);

                //	Normalized blade height (0 = base, 1 = tip). Baked into uv2.y by both renderers; the
                //	_BladeBaseY/_BladeHeightScale pair is the fallback for a blade mesh whose uv2 was not baked.
                float fHeight = max(v.uv2Blade.y,
                                    saturate((v.positionOS.y - _BladeBaseY) * _BladeHeightScale));

                //	Wind (§9): world-space phase so neighbouring patches stay in step, plus the per-blade phase so the
                //	meadow does not move in lockstep. Bends the tip, never the root, and stays independent of the
                //	player interaction — the two displacements simply add.
                float fWindPhase = _Time.y * _WindSpeed + positionWS.x * 0.7 + positionWS.z * 0.45 + fPhase;
                float fSoft = saturate(1.0 - _Softness * 0.5);
                float fWave = sin(fWindPhase) + 0.35 * sin(fWindPhase * 2.3);
                float fWindBend = fWave * _WindStrength * fSoft * fHeight * fHeight;
                positionWS.x += fWindBend;
                positionWS.z += fWindBend * 0.4;

                //	Player interaction. The vector is built from the BLADE ROOT, not from this vertex, so the whole card
                //	leans as one piece: one root position, one distance, one falloff, one direction for every vertex of
                //	the blade — the height mask alone still varies along it.
                if (_GrassInteractionEnabled > 0.0 && _GrassInteractionRadius > 0.0)
                {
                    float3 vRootWS = GrassBladeRootWS(v.positionOS.xyz);
                    positionWS += GrassInteractionOffset(positionWS, vRootWS, fPhase, fHeight);
                }

                o.positionWS = positionWS;
                o.positionCS = TransformWorldToHClip(positionWS);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 GrassFrag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                half4 cTexture = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * _BaseColor;
                clip(cTexture.a - _Cutoff);

                //	C++ CELGrassType culls a bitmap beyond the type's sight range: fade then clip so the
                //	transition is not a hard pop at the boundary.
                float fDistance = distance(i.positionWS, _WorldSpaceCameraPos);
                float fFade = saturate((_SightRange - fDistance) / max(1.0, _SightRange * 0.15));
                clip(fFade - 0.01);

                Light pMainLight = GetMainLight();
                half3 cAmbient = lerp(_AmbientDayColor.rgb, _AmbientNightColor.rgb, saturate(_NightColorControl));
                half3 cDirLt = lerp(_DirLtColorDayColor.rgb, _DirLtColorNightColor.rgb, saturate(_NightColorControl));

                half fNdl = saturate(dot(normalize(i.normalWS), pMainLight.direction)) * 0.5 + 0.5;
                half3 cColor = cTexture.rgb * (cAmbient + cDirLt * fNdl);
                cColor = MixFog(cColor, i.fogFactor);

                return half4(cColor, cTexture.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
