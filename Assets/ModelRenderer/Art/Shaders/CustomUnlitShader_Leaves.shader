Shader "BrewMonster/UnlitWithShadowsLeaves"
{
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        [HideInInspector] _PWColorMode ("PW Color Mode", Float) = 0
        _ShadowStrength ("Shadow Strength", Range(0, 1)) = 0.5

        // Billboard: Y axis stays up and the mesh is aimed so its BACK side meets the camera, because
        // these materials render the back face. Off by default: unless a material turns this on, every
        // vertex keeps the transform it is authored with. Drives the _BILLBOARD_ON keyword, so vert
        // compiles it away instead of branching at runtime.
        [Toggle(_BILLBOARD_ON)] _Billboard ("Billboard (face camera)", Float) = 0

        // Where the billboard rotates from. Off: the whole mesh turns around its object origin. On: every
        // vertex turns around the centre of its own quad/triangle and takes that quad's plane normal from
        // the two baked data textures below - one texel per vertex, addressed by SV_VertexID. Bake them with
        // Tools > BrewMonster > Bake Billboard Quad Data.
        [Toggle(_BILLBOARD_PER_VERTEX_PIVOT)] _BillboardPivot ("Pivot Per Quad/Triangle", Float) = 0

        // Object-space pivot and plane normal of the quad each vertex belongs to, one texel per vertex of the
        // mesh. RGBAFloat, point sampled, no compression, no mipmaps - the baker writes these as .asset
        // textures with those settings, so do not re-import them differently. With nothing assigned the pivot
        // reads as zero and the billboard falls back to the mesh origin.
        [NoScaleOffset] _BillboardPivotTex ("Billboard Pivot (baked)", 2D) = "black" {}
        [NoScaleOffset] _BillboardNormalTex ("Billboard Normal (baked)", 2D) = "white" {}
        [HideInInspector] _BillboardDataSize ("Billboard Data Size", Vector) = (1, 1, 1, 1)

        // Wind: the leaves sway in a gust that travels across the tree. Off by default - at 0 strength the
        // vertex ends up exactly where it used to - so turn Wind Strength up on the leaves material to bring
        // a tree to life. No keyword for this one: the whole effect is a few sines in the vertex shader, so
        // it is not worth the extra variants, and a strength of 0 multiplies out to a zero offset.
        //
        // Strength is a fraction of how high a card sits, not a distance: at 0.1 a card 10 units above the
        // tree's origin swings 1 unit, whether that tree is 2 or 200 units tall.
        _WindStrength ("Wind Strength (fraction of height)", Range(0, 1)) = 0.02
        _WindSpeed ("Wind Speed", Range(0, 5)) = 1
        // Roughly the distance, in world units, between two gust peaks: make it small to have the movement
        // ripple from card to card, large to have a whole tree - or a whole row of them - move together.
        _WindGustSize ("Wind Gust Size (world units)", Range(0.1, 500)) = 30
        // Angle on the ground plane: 0 blows toward +X, 90 toward +Z.
        _WindDirection ("Wind Direction (degrees, 0 = +X, 90 = +Z)", Range(0, 360)) = 45

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
    // CustomEditor "BrewMonsterUnlitShaderGUI"

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
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

            // Same shape for the per-quad pivot, plus the shader model its data read needs: SV_VertexID and a
            // vertex-stage texture fetch put this at 4.5 here (D3D11, Vulkan, Metal, GLES3.1). Lower it to 3.5
            // and test on the devices if you need GLES3.0 - there is no buffer in this path any more.
            #pragma shader_feature_local_vertex _BILLBOARD_PER_VERTEX_PIVOT
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            // Baked per-vertex billboard data: one texel per vertex, sampled in the vertex shader with a UV
            // worked out from SV_VertexID, so nothing has to be bound per renderer and instancing keeps
            // working. Only read where _BILLBOARD_PER_VERTEX_PIVOT is on; a material with no textures assigned
            // keeps the mesh origin as its pivot.
            TEXTURE2D(_BillboardPivotTex);
            SAMPLER(sampler_BillboardPivotTex);
            TEXTURE2D(_BillboardNormalTex);
            SAMPLER(sampler_BillboardNormalTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _Color;
                float _PWColorMode;
                float _RenderType;
                half _ShadowStrength;
                float4 _BillboardDataSize;
                float _WindStrength;
                float _WindSpeed;
                float _WindGustSize;
                float _WindDirection;
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

            // The baked data textures hold one texel per vertex, so a vertex index reads as a grid position.
            // _BillboardDataSize is (width, height, 1/width, 1/height), written by the baker next to the
            // textures; adding half a texel lands on texel centres, which is where a point sampler reads.
            float2 BillboardDataUV(uint vertexID)
            {
                float width = max(_BillboardDataSize.x, 1.0);
                float2 texel = float2(fmod((float)vertexID, width), floor((float)vertexID / width));
                return (texel + 0.5) * _BillboardDataSize.zw;
            }

            // ---------------------------------------------------------------------------------------
            // Wind
            //
            // The direction the wind blows along, on the ground plane. _WindDirection is an angle so it can
            // be dialled in the material inspector; there is no vertical component, because a gust that
            // lifted the leaves off their branches would read as floating rather than as wind.
            float2 WindDirectionXZ()
            {
                float windAngle = radians(_WindDirection);
                return float2(cos(windAngle), sin(windAngle));
            }

            // One gust, read at a world position: -1 at one end of its swing, +1 at the other. Its phase
            // travels along the wind direction, so cards further downwind lag behind the ones upwind and the
            // movement ripples through the foliage instead of the whole tree pivoting as one block, and a
            // second sine of a different period rides on top so the strength builds and dies down in gusts
            // rather than ticking at a constant amplitude - a single sine looks like a metronome.
            float WindWave(float3 positionWS)
            {
                float2 windDir = WindDirectionXZ();

                float time = _Time.y * _WindSpeed;
                float travel = 2.0 * PI * dot(positionWS.xz, windDir) / max(_WindGustSize, 0.01);

                float gust = sin(travel - time);
                float envelope = 0.65 + 0.35 * sin(travel * 0.35 - time * 1.7);

                return gust * envelope;
            }

            // World Y of the object origin: height is measured from there, so that is the one point which
            // never moves and a tree stays planted at its foot. Read from the object's own matrix, so it
            // follows wherever the renderer is positioned and whatever it is scaled to.
            float WindBaseY()
            {
                return TransformObjectToWorld(float3(0.0, 0.0, 0.0)).y;
            }

            // How far the wind pushes a point, in world units, along the wind. The push grows with the
            // point's height above the object origin, which is what keeps the bottom of a tree still while
            // its crown travels furthest. It also makes _WindStrength scale free: being a fraction of that
            // height, 0.1 moves a point 10 units up by 1 unit, in a mesh of any size.
            //
            // What the caller passes decides whether the mesh moves rigidly or bends: a card's own centre
            // for a billboarded card - every vertex of it then reads the same value, so the leaf keeps its
            // shape and size - the vertex itself for everything else.
            float3 WindOffsetWS(float3 positionWS)
            {
                float height = positionWS.y - WindBaseY();
                float amount = _WindStrength * height * WindWave(positionWS);

                float2 windDir = WindDirectionXZ();
                return float3(windDir.x, 0.0, windDir.y) * amount;
            }

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
                pivotOS = SAMPLE_TEXTURE2D_LOD(_BillboardPivotTex, sampler_BillboardPivotTex, BillboardDataUV(input.vertexID), 0).xyz;
                #endif

                float3 pivotWS = TransformObjectToWorld(pivotOS);

                float3 toCamera = facingSign * normalize(_WorldSpaceCameraPos - pivotWS);

                // Giữ trục Y hướng lên.
                // Nếu muốn mesh nghiêng theo camera thì bỏ đoạn này.
                float3 up = float3(0, 1, 0);

                float3 right = normalize(cross(up, toCamera));
                float3 billboardUp = normalize(cross(toCamera, right));

                // Wind, read before the billboard turns the result toward the camera, so a card that is
                // swaying still ends up facing the camera rather than being aimed away from it.
                //
                // A card the bake covered sways as one piece: all of its vertices read the same anchor, the
                // card's own centre from the pivot texture, so they all get one identical offset and the leaf
                // keeps its shape and its size. Anything without a card - a bark vertex, a material with no
                // pivot texture, or the whole-mesh pivot below - reads its own position instead and so bends,
                // exactly as it does when the billboard is off altogether.
                float3 windAnchorWS = vertexInput.positionWS;
                #ifdef _BILLBOARD_PER_VERTEX_PIVOT
                windAnchorWS = dot(pivotOS, pivotOS) > 1e-6 ? pivotWS : vertexInput.positionWS;
                #endif

                float3 windOffsetWS = WindOffsetWS(windAnchorWS);

                float3 vertexWS;

                #ifdef _BILLBOARD_PER_VERTEX_PIVOT
                // Per card: express the vertex offset inside the card's own frame and rebuild it in the
                // billboard frame, so a tilted card keeps its shape and only its facing changes. The plane
                // normal comes from the baked texture rather than from the NORMAL stream: a smoothed vertex
                // normal would tilt the frame out of the card's plane.
                float3 normalOS = SAMPLE_TEXTURE2D_LOD(_BillboardNormalTex, sampler_BillboardNormalTex, BillboardDataUV(input.vertexID), 0).xyz;

                // A vertex the baker did not cover - a bark vertex, or a material with no textures assigned -
                // reads as zero here, so fall back to up and keep the basis below finite. Such vertices are not
                // billboarded anyway, because the material drawing them has the keyword off.
                normalOS = dot(normalOS, normalOS) > 1e-6 ? normalOS : float3(0, 1, 0);

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

                // Same wind for both shapes of billboard, added to whatever it just built: on a card it is
                // one rigid offset shared by the whole quad, on a mesh it is a per-vertex bend.
                vertexWS += windOffsetWS;

                output.positionCS = TransformWorldToHClip(vertexWS);
                #endif

                // No billboard: sway the vertex itself and rebuild the clip position from the world position
                // it moved to. Nothing here applies a rotation, so the mesh bends - high up it travels
                // further than down at the foot - instead of sliding sideways in one piece.
                //
                // With _WindStrength at 0 the offset is a single multiply by zero, so the vertex lands
                // exactly where GetVertexPositionInputs put it above and nothing shifts.
                #ifndef _BILLBOARD_ON
                output.positionWS += WindOffsetWS(output.positionWS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                #endif

                // Fog, applied after the wind and the billboard: with both off this consumes the same
                // clip position as before, and with either on the fog follows the moved vertex.
                output.fogCoord = ComputeFogFactor(output.positionCS.z);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 mainTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 color;
                // Prefer uniform over keyword: first EnableKeyword frame can still draw the
                // default (multiply) variant and tint the whole mesh until compile completes.
                // No blend-with-High-Map mode here: the leaves never draw eyes, brows or a mouth, so the
                // shared shader's face blend was dropped together with its _HighMap / _SkinColor properties.
                if (_PWColorMode >= 1.5)
                {
                    // bodyrender: lerp(t0, t0 * color, t0.a)
                    color = lerp(mainTex, mainTex * _Color, mainTex.a);
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

            // Keep this layout identical to the ForwardLit pass, wind values included. This pass follows
            // neither the billboard nor the wind - it draws the mesh where it is authored, as it always has
            // - and the baked leaves materials disable the pass outright, so nothing is out of step in
            // practice. Declaring the fields anyway keeps the two CBUFFERs from drifting apart.
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _Color;
                float _PWColorMode;
                float _RenderType;
                half _ShadowStrength;
                float4 _BillboardDataSize;
                float _WindStrength;
                float _WindSpeed;
                float _WindGustSize;
                float _WindDirection;
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
