Shader "BrewMonster/UnlitWithShadows"
{
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _HighMap ("High Map", 2D) = "black" {}
        _SkinColor ("Skin Color", Color) = (1,1,1,1)
        [HideInInspector] _PWColorMode ("PW Color Mode", Float) = 0
        _ShadowStrength ("Shadow Strength", Range(0, 1)) = 0.5

        // Billboard: Y axis stays up and the mesh is aimed so its BACK side meets the camera, because
        // these materials render the back face. Off by default: unless a material turns this on, every
        // vertex keeps the transform it is authored with. Drives the _BILLBOARD_ON keyword, so vert
        // compiles it away instead of branching at runtime.
        [Toggle(_BILLBOARD_ON)] _Billboard ("Billboard (face camera)", Float) = 0

        // Where the billboard rotates from. Off: the whole mesh turns around its object origin. On:
        // every vertex turns around the centre of its own quad/triangle, read from
        // _BillboardPivotBuffer - which only a renderer carrying BillboardPivotBinder binds. Leave it
        // off unless that component sits on the object: an unbound buffer reads as garbage.
        [Toggle(_BILLBOARD_PER_VERTEX_PIVOT)] _BillboardPivot ("Pivot Per Quad/Triangle", Float) = 0

        // BlendMode options
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend("Src Blend Mode", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend("Dst Blend Mode", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite("Z Write", Float) = 1

        // Surface options
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull Mode", Float) = 2

        // Material type
        [HideInInspector] _Surface("__surface", Float) = 0.0

        // Custom properties for day/night control, only use to make visual on Editor, not used in shader code directly
        [HideInInspector]_AmbientDayColor("Color", Color) = (1,1,1,1)

        [HideInInspector] _RenderType ("Render Type", Float) = 0 // 0 for Opaque, 1 for Transparent, 2 for Cutout
    }

    // Helper to set render queue and render type based on alpha mode
    CustomEditor "BrewMonsterUnlitShaderGUI"

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }
        LOD 100

        // Main pass
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // Shadows
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS

            // Fog
            #pragma multi_compile_fog

            // Keep variants for batching, but fragment also branches on _PWColorMode so the
            // first role select works before async shader-variant compile finishes.
            #pragma multi_compile_local_fragment _ _PW_COLOR_BLEND _PW_COLOR_BODY

            // Billboard is a per-material switch, not a per-frame one, so it is compiled away
            // instead of costing a branch in vert. Vertex-only because vert is the only stage that
            // reads it, and _local so unused variants are stripped from builds.
            #pragma shader_feature_local_vertex _BILLBOARD_ON

            // Same shape for the per-quad pivot, plus the shader model its buffer read needs:
            // SV_VertexID and StructuredBuffer are shader model 4.5 only (D3D11, Vulkan, Metal,
            // GLES3.1). GLES3.0 devices would lose this pass, so keep the keyword off for them or
            // move the pivot into a vertex attribute instead of a buffer.
            #pragma shader_feature_local_vertex _BILLBOARD_PER_VERTEX_PIVOT
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_HighMap);
            SAMPLER(sampler_HighMap);

            // Object-space billboard pivot per vertex, indexed by SV_VertexID and bound per renderer by
            // BillboardPivotBinder. Only read where _BILLBOARD_PER_VERTEX_PIVOT is on, because an
            // unbound StructuredBuffer returns undefined values rather than zero.
            StructuredBuffer<float3> _BillboardPivotBuffer;

            // Object-space plane normal of the same quad/triangle, also per vertex. It comes from the mesh
            // winding rather than from the NORMAL stream, because a smoothed vertex normal would tilt the
            // card basis out of the card's plane - and the projection onto that basis shrinks the card.
            StructuredBuffer<float3> _BillboardCardNormalBuffer;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _HighMap_ST;
                half4 _Color;
                half4 _SkinColor;
                float _PWColorMode;
                float _RenderType;
                half _ShadowStrength;
            CBUFFER_END

            CBUFFER_START(DayNightCustom)
                half4 _AmbientDayColor;
                half4 _AmbientNightColor;
                half _NightColorControl;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float fogCoord : TEXCOORD3;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;

                // Transform position
                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;

                // Transform normal
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);
                output.normalWS = normalInput.normalWS;

                // UV
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                // Billboard, opt-in per material via `_Billboard` -> `_BILLBOARD_ON`: rotate the mesh
                // around its pivot so it faces the camera, Y axis kept upright. With the keyword off
                // the whole block is gone and positionCS keeps the object-to-clip result above.
                //
                // These materials render the back face of the mesh, so the side that has to end up
                // toward the camera is the object's back, not its front. Measured against a plain
                // look-at billboard that is a 180 degree turn about up: the up axis is unchanged and
                // the horizontal axis mirrors. facingSign below picks the side - flip it if the wrong
                // one shows, for the whole mesh or for the cards.
                #ifdef _BILLBOARD_ON
                const float facingSign = 1.0;

                // Where this vertex rotates from, in object space: the object origin, or - when the
                // renderer bound a pivot buffer - the centre of its own quad/triangle. With the mesh
                // pivot the offset used below is the plain object-space position, exactly as before.
                float3 pivotOS = float3(0, 0, 0);
                #ifdef _BILLBOARD_PER_VERTEX_PIVOT
                pivotOS = _BillboardPivotBuffer[input.vertexID];
                #endif

                float3 pivotWS = TransformObjectToWorld(pivotOS);

                float3 toCamera = facingSign * normalize(_WorldSpaceCameraPos - pivotWS);

                // Giữ trục Y hướng lên.
                // Nếu muốn mesh nghiêng theo camera thì bỏ đoạn này.
                float3 up = float3(0, 1, 0);

                float3 right = normalize(cross(up, toCamera));
                float3 billboardUp = normalize(cross(toCamera, right));

                float3 vertexWS;

                #ifdef _BILLBOARD_PER_VERTEX_PIVOT
                // Per card: express the vertex offset inside the card's own frame and rebuild it in the
                // billboard frame, so a tilted card keeps its shape and only its facing changes. The card's
                // plane normal has to be the real one from the buffer: taken from a smoothed NORMAL stream
                // it would tilt the frame out of the card's plane.
                float3 normalOS = _BillboardCardNormalBuffer[input.vertexID];
                float3 normalWS = normalize(TransformObjectToWorldNormal(normalOS));

                // A card facing straight up or down would make the cross product below degenerate.
                float3 refUp = abs(dot(normalWS, up)) > 0.999 ? float3(0, 0, 1) : up;

                float3 cardRight = normalize(cross(refUp, normalWS));
                float3 cardUp = cross(normalWS, cardRight);

                // doNormalize:false is what keeps this an offset rather than a direction. With the default
                // (true) the helper normalizes the result, so every vertex would land exactly one world
                // unit from its card centre and the card would collapse into a tiny spike - correct place,
                // wrong size. The flag also keeps the object's scale.
                float3 offsetWS = TransformObjectToWorldDir(input.positionOS.xyz - pivotOS, false);

                // Offset in the card's frame, rebuilt in the billboard frame (right, billboardUp, toCamera).
                // That is a rigid rotation, so a card that is not perfectly flat keeps its size too, instead
                // of having the out-of-plane part of its offset silently dropped.
                float3 offsetInCard = float3(
                    dot(offsetWS, cardRight),
                    dot(offsetWS, cardUp),
                    dot(offsetWS, normalWS));

                vertexWS =
                    pivotWS
                    + right * offsetInCard.x
                    + billboardUp * offsetInCard.y
                    + toCamera * offsetInCard.z;
                #else
                // Vertex position trong object space.
                // Giả sử mesh nằm quanh pivot và có kích thước local.
                float3 vertexOS = input.positionOS.xyz;

                vertexWS =
                    pivotWS
                    + right * vertexOS.x
                    + billboardUp * vertexOS.y;
                #endif

                output.positionCS = TransformWorldToHClip(vertexWS);
                #endif

                // Fog, applied after the billboard transform: with the keyword off this consumes the
                // same clip position as before, and with it on the fog follows the moved vertex.
                output.fogCoord = ComputeFogFactor(output.positionCS.z);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 mainTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 color;
                // Prefer uniform over keyword: first EnableKeyword frame can still draw the
                // default (multiply) variant and tint the whole face mesh until compile completes.
                if (_PWColorMode >= 1.5)
                {
                    // bodyrender: lerp(t0, t0 * color, t0.a)
                    color = lerp(mainTex, mainTex * _Color, mainTex.a);
                }
                else if (_PWColorMode >= 0.5)
                {
                    // eyerender/browrender/mouthrender: lerp(t0 * skin, t1 * part, t1.a)
                    half4 highTex = SAMPLE_TEXTURE2D(_HighMap, sampler_HighMap, input.uv);
                    color = lerp(mainTex * _SkinColor, highTex * _Color, highTex.a);
                }
                else
                {
                    color = mainTex * _Color;
                }

                // Shadow calculation
                // float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                // Light mainLight = GetMainLight(shadowCoord);
                // half shadowAttenuation = mainLight.shadowAttenuation;

                // Apply shadow
                // half shadow = lerp(1.0, shadowAttenuation, _ShadowStrength);
                // color.rgb *= shadow;

                // lerp between _AmbientDayColor and _AmbientNightColor using _NightColorControl
                half4 ambientColor = lerp(_AmbientDayColor, _AmbientNightColor, _NightColorControl);
                color *= ambientColor;

                // Apply fog
                color.rgb = MixFog(color.rgb, input.fogCoord);

                return color;
            }
            ENDHLSL
        }

        // Shadow caster pass.
        // Self-contained on purpose: this pass used to include the URP ShadowCasterPass helper,
        // which alpha-tests against `_BaseColor` - a property this shader does not declare (`_Color`
        // is the one it uses) - so had _ALPHATEST_ON ever been enabled every pixel would have been
        // clipped, while with the keyword off a transparent mesh wrote its whole silhouette into the
        // shadow map and cast a fully opaque shadow.
        // Density now follows the alpha the ForwardLit pass blends with, so `_RenderType` decides:
        //   Opaque / Cutout -> solid shadow
        //   Transparent     -> dithered clip, coverage ~= alpha (a 50% see-through mesh casts a ~50% dark shadow)
        Pass
        {
            Name "ShadowCaster"
            Tags{"LightMode" = "ShadowCaster"}

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma exclude_renderers gles gles3 glcore
            #pragma target 4.5

            // Universal Pipeline keywords
            // This is used during shadow map generation to differentiate between directional and punctual light shadows, as they use different formulas to apply Normal Bias
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_HighMap);
            SAMPLER(sampler_HighMap);

            // Keep this layout identical to the ForwardLit pass.
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _HighMap_ST;
                half4 _Color;
                half4 _SkinColor;
                float _PWColorMode;
                float _RenderType;
                half _ShadowStrength;
            CBUFFER_END

            // Set by ShadowUtils.SetupShadowCasterConstantBuffer while the shadow map is rendered.
            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            float4 GetShadowPositionHClip(Attributes input)
            {
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                return ApplyShadowClamping(positionCS);
            }

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output;
                output.positionCS = GetShadowPositionHClip(input);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            // Same alpha the ForwardLit pass ends up blending with, per `_PWColorMode` branch.
            half PWShadowAlpha(float2 uv)
            {
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).a;

                if (_PWColorMode >= 1.5)
                {
                    // bodyrender: lerp(t0, t0 * color, t0.a)
                    alpha = lerp(alpha, alpha * _Color.a, alpha);
                }
                else if (_PWColorMode >= 0.5)
                {
                    // eyerender/browrender/mouthrender: lerp(t0 * skin, t1 * part, t1.a)
                    half highTexAlpha = SAMPLE_TEXTURE2D(_HighMap, sampler_HighMap, uv).a;
                    alpha = lerp(alpha * _SkinColor.a, highTexAlpha * _Color.a, highTexAlpha);
                }
                else
                {
                    alpha = alpha * _Color.a;
                }

                return alpha;
            }

            half4 ShadowPassFragment(Varyings input) : SV_TARGET
            {
                // Transparent : keep a hashed fraction of the pixels so the shadow density tracks alpha
                // instead of writing the whole silhouette. Shadow maps are not MSAA targets, so
                // alpha-to-coverage cannot do this here; the pattern is stable per shadow-map texel.
                if (_RenderType >= 0.5 && _RenderType < 1.5)
                {
                    half alpha = PWShadowAlpha(input.uv);
                    LODDitheringTransition(uint2(input.positionCS.xy), alpha);
                }

                return 0;
            }
            ENDHLSL
        }

        // Depth pass for receiving shadows
        // Pass
        // {
        //     Name "DepthOnly"
        //     Tags { "LightMode"="DepthOnly" }

        //     ZWrite On
        //     ColorMask 0
        //     Cull [_Cull]

        //     HLSLPROGRAM
        //     #pragma vertex DepthOnlyVertex
        //     #pragma fragment DepthOnlyFragment

        //     // Alpha clipping for depth
        //     #pragma shader_feature_local_fragment _ALPHATEST_ON

        //     #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        //     TEXTURE2D(_BaseMap);
        //     SAMPLER(sampler_BaseMap);

        //     CBUFFER_START(UnityPerMaterial)
        //         float4 _BaseMap_ST;
        //         half4 _Color;
        //         half _Cutoff;
        //     CBUFFER_END

        //     struct Attributes
        //     {
        //         float4 position : POSITION;
        //         float2 texcoord : TEXCOORD0;
        //     };

        //     struct Varyings
        //     {
        //         float4 positionCS : SV_POSITION;
        //         float2 uv : TEXCOORD0;
        //     };

        //     Varyings DepthOnlyVertex(Attributes input)
        //     {
        //         Varyings output;
        //         output.positionCS = TransformObjectToHClip(input.position.xyz);
        //         output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
        //         return output;
        //     }

        //     half4 DepthOnlyFragment(Varyings input) : SV_TARGET
        //     {
        //         #ifdef _ALPHATEST_ON
        //             half4 mainTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
        //             half alpha = mainTex.a * _Color.a;
        //             clip(alpha - _Cutoff);
        //         #endif
        //         return 0;
        //     }
        //     ENDHLSL
        // }
    }

    FallBack "Universal Render Pipeline/Lit"
}
