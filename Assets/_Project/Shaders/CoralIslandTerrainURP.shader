Shader "CoralArchipelago/URP/CoralIslandTerrainURP"
{
    Properties
    {
        [Header(Coral Sand Shore)]
        _SandColor ("Dry Sand Color", Color) = (0.90, 0.80, 0.58, 1)
        _WetSandColor ("Wet Shore Sand Color", Color) = (0.58, 0.48, 0.34, 1)
        _SandMaxHeight ("Sand Transition Height", Float) = 1.95

        [Header(Tropical Grass)]
        _GrassColor ("Tropical Grass Color", Color) = (0.18, 0.56, 0.28, 1)
        _LushGrassColor ("Lush Grass Accent", Color) = (0.26, 0.68, 0.32, 1)
        _GrassMaxHeight ("Rock Transition Height", Float) = 4.6

        [Header(Volcanic Cliff & Night BioGlow)]
        _RockColor ("Volcanic Cliff Color", Color) = (0.24, 0.26, 0.32, 1)
        [HDR] _BioVeinColor ("Night Rock Vein Glow", Color) = (0.06, 1.15, 1.50, 1)
        _SlopeThreshold ("Cliff Slope Threshold", Range(0.3, 0.95)) = 0.76
        _BlendSharpness ("Biome Blend Width", Range(0.05, 1.5)) = 0.45
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        LOD 200

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            half _GlobalBioluminescence;
            half _ThermalVFXMultiplier;

            CBUFFER_START(UnityPerMaterial)
                half4 _SandColor;
                half4 _WetSandColor;
                float _SandMaxHeight;
                half4 _GrassColor;
                half4 _LushGrassColor;
                float _GrassMaxHeight;
                half4 _RockColor;
                half4 _BioVeinColor;
                half _SlopeThreshold;
                half _BlendSharpness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half fogFactor : TEXCOORD2;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = (half3)normalize(normInputs.normalWS);
                output.fogFactor = (half)ComputeFogFactor(posInputs.positionCS.z);
                return output;
            }

            half HashPattern(float2 p)
            {
                return (half)(sin(p.x * 1.37 + cos(p.y * 1.71)) * 0.5 + 0.5);
            }

            half4 frag(Varyings input) : SV_Target
            {
                half3 normalWS = normalize(input.normalWS);
                float h = input.positionWS.y;

                // Procedural micro-detail variation without texture samplers (fast on mobile TBDR)
                half detailA = HashPattern(input.positionWS.xz * 0.55);
                half detailB = HashPattern(input.positionWS.xz * 2.15);
                half noise = detailA * 0.65h + detailB * 0.35h;

                // 1. Wet sand near water level -> warm coral sand
                half wetBlend = saturate((half)((h - 0.1) / 1.15));
                half3 sandCol = lerp(_WetSandColor.rgb, _SandColor.rgb, wetBlend);
                sandCol *= lerp(0.92h, 1.06h, noise);

                // 2. Tropical lush grass
                half3 grassCol = lerp(_GrassColor.rgb, _LushGrassColor.rgb, noise);

                // 3. Volcanic rock cliff
                half3 rockCol = _RockColor.rgb * lerp(0.82h, 1.18h, detailB);

                // Biome Splatting by height & slope
                half grassMask = smoothstep((half)_SandMaxHeight - _BlendSharpness, (half)_SandMaxHeight + _BlendSharpness, (half)h);
                half heightRockMask = smoothstep((half)_GrassMaxHeight - _BlendSharpness, (half)_GrassMaxHeight + _BlendSharpness, (half)h);
                half slopeRockMask = smoothstep(_SlopeThreshold + 0.08h, _SlopeThreshold - 0.10h, normalWS.y);
                half rockMask = saturate(max(heightRockMask, slopeRockMask));

                half3 albedo = lerp(sandCol, grassCol, grassMask);
                albedo = lerp(albedo, rockCol, rockMask);

                // Main directional light + soft shadows
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half ndotl = saturate(dot(normalWS, (half3)mainLight.direction));
                half3 diffuse = (half3)mainLight.color * (ndotl * (half)(mainLight.distanceAttenuation * mainLight.shadowAttenuation));

                // Additional point lights (Altar / Lamps / Crystals)
                #ifdef _ADDITIONAL_LIGHTS
                uint lightCount = GetAdditionalLightsCount();
                for (uint li = 0u; li < lightCount; ++li)
                {
                    Light addLight = GetAdditionalLight(li, input.positionWS);
                    half addNdotL = saturate(dot(normalWS, (half3)addLight.direction));
                    diffuse += (half3)addLight.color * (addNdotL * (half)(addLight.distanceAttenuation * addLight.shadowAttenuation));
                }
                #endif

                half3 ambient = SampleSH(normalWS);
                half3 litColor = albedo * (diffuse + ambient);

                // Subtle bioluminescent veins in volcanic rock & wet shoreline at night
                half veinMask = smoothstep(0.76h, 0.94h, detailA * detailB * 1.45h);
                half bioScale = _GlobalBioluminescence * lerp(0.5h, 1.0h, saturate(_ThermalVFXMultiplier));
                half3 bioGlow = _BioVeinColor.rgb * (veinMask * saturate(rockMask + (1.0h - wetBlend) * 0.35h) * bioScale * 0.45h);

                half3 finalColor = MixFog(litColor + bioGlow, input.fogFactor);
                return half4(finalColor, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                float3 posWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 posCS = TransformWorldToHClip(ApplyShadowBias(posWS, normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    posCS.z = min(posCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    posCS.z = max(posCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                output.positionCS = posCS;
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
