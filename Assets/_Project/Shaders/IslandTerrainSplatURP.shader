Shader "CoralArchipelago/URP/IslandTerrainSplatURP"
{
    Properties
    {
        [Header(Layer 1 Wet Shoreline Sand)]
        _WetSandColor ("Wet Sand Color", Color) = (0.42, 0.35, 0.24, 1)
        _WetSandHeight ("Wet Sand Max Height", Float) = -1.0

        [Header(Layer 2 Tropical White Gold Sand)]
        _DrySandColor ("Dry Tropical Sand", Color) = (0.94, 0.87, 0.72, 1)
        _DrySandMaxHeight ("Dry Sand Max Height", Float) = 3.6

        [Header(Layer 3 Lush Grass And Moss)]
        _GrassColor ("Deep Tropical Grass", Color) = (0.14, 0.45, 0.19, 1)
        _MossColor ("Organic Moss Patch", Color) = (0.28, 0.58, 0.22, 1)

        [Header(Layer 4 Cracked Cliff Rock And AO)]
        _CliffColor ("Slate Basalt Cliff", Color) = (0.24, 0.26, 0.29, 1)
        _CliffCreviceColor ("Cliff Crevice AO", Color) = (0.09, 0.10, 0.12, 1)
        _HighRockHeight ("High Altitude Rock Height", Float) = 28.0
        _SlopeThreshold ("Flat Terrain Normal Y Threshold", Range(0.5, 0.95)) = 0.78

        [Header(Procedural Detail And Night BioGlow)]
        _NoiseScale ("Terrain Noise Scale", Float) = 0.085
        _UseVertexBiomeTint ("Biome Vertex Tint Blend", Range(0.0, 1.0)) = 0.32
        [HDR] _BioVeinColor ("Night BioGlow Veins", Color) = (0.06, 1.25, 1.65, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        LOD 250

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
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            half _GlobalBioluminescence;
            half _ThermalVFXMultiplier;

            CBUFFER_START(UnityPerMaterial)
                half4 _WetSandColor;
                float _WetSandHeight;
                half4 _DrySandColor;
                float _DrySandMaxHeight;
                half4 _GrassColor;
                half4 _MossColor;
                half4 _CliffColor;
                half4 _CliffCreviceColor;
                float _HighRockHeight;
                half _SlopeThreshold;
                float _NoiseScale;
                half _UseVertexBiomeTint;
                half4 _BioVeinColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half fogFactor : TEXCOORD2;
                half4 vertexColor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);

                float a = Hash21(i);
                float b = Hash21(i + float2(1.0, 0.0));
                float c = Hash21(i + float2(0.0, 1.0));
                float d = Hash21(i + float2(1.0, 1.0));

                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            float Fbm3(float2 p)
            {
                float v = 0.0;
                float amp = 0.5;
                v += ValueNoise(p) * amp;
                p = p * 2.07 + float2(17.3, 9.2);
                amp *= 0.5;
                v += ValueNoise(p) * amp;
                p = p * 2.13 + float2(31.7, 13.4);
                amp *= 0.5;
                v += ValueNoise(p) * amp;
                return v;
            }

            float RidgedRockPattern(float2 p)
            {
                float n1 = abs(ValueNoise(p) - 0.5) * 2.0;
                float n2 = abs(ValueNoise(p * 2.35 + float2(7.1, 4.3)) - 0.5) * 2.0;
                float n3 = abs(ValueNoise(p * 5.2 + float2(19.4, 2.7)) - 0.5) * 2.0;
                return saturate(n1 * 0.55 + n2 * 0.30 + n3 * 0.15);
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = (half3)normalize(normInputs.normalWS);
                output.fogFactor = (half)ComputeFogFactor(posInputs.positionCS.z);
                output.vertexColor = (half4)input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half3 geomNormal = normalize(input.normalWS);
                float3 posWS = input.positionWS;
                float h = posWS.y;

                // Multi-frequency noise for organic transitions (never flat linear bands)
                float fbmMacro = Fbm3(posWS.xz * _NoiseScale);
                float fbmMicro = Fbm3(posWS.xz * (_NoiseScale * 6.5) + float2(4.2, 8.9));

                // -----------------------------------------------------------------
                // Layer 1 (y < 1.2m): Dark Wet Sand with high smoothness (0.75)
                // -----------------------------------------------------------------
                half wetMask = 1.0h - saturate((half)((h - (_WetSandHeight - 0.45)) / 0.70));
                half3 wetSandCol = _WetSandColor.rgb * lerp(0.86h, 1.08h, (half)fbmMicro);

                // -----------------------------------------------------------------
                // Layer 2 (1.2m - 3.0m): Tropical Sand + Procedural Sand Ripples Normal
                // -----------------------------------------------------------------
                float ripplePhase = posWS.x * 2.8 + posWS.z * 1.4 + fbmMacro * 4.5;
                float rippleDx = cos(ripplePhase) * 0.22;
                float rippleDz = cos(ripplePhase) * 0.11;
                half3 sandNormal = normalize(geomNormal + half3((half)rippleDx, 0.0h, (half)rippleDz));
                half3 drySandCol = _DrySandColor.rgb * lerp(0.92h, 1.06h, (half)(fbmMicro * 0.7 + fbmMacro * 0.3));

                half3 sandCol = lerp(drySandCol, wetSandCol, wetMask);

                // -----------------------------------------------------------------
                // Layer 3 (y > 3.0m & flat normal.y > 0.78): Lush Grass & Organic Moss
                // -----------------------------------------------------------------
                float noisyGrassStart = _DrySandMaxHeight + (fbmMacro - 0.5) * 1.8;
                half grassHeightMask = smoothstep((half)noisyGrassStart - 0.75h, (half)noisyGrassStart + 0.85h, (half)h);
                half flatMask = smoothstep(_SlopeThreshold - 0.06h, _SlopeThreshold + 0.06h, geomNormal.y);

                // Organic grass/moss patches interleave with sand patches cleanly
                half mossPatchNoise = saturate((half)((fbmMacro * 0.65 + fbmMicro * 0.35 - 0.28) * 2.1));
                half3 grassCol = lerp(_GrassColor.rgb, _MossColor.rgb, mossPatchNoise);
                grassCol *= lerp(0.85h, 1.15h, (half)fbmMicro);

                // Blend subtle biome vertex tint from Chunk generator into grass & highlands
                grassCol = lerp(grassCol, grassCol * lerp(half3(0.75h, 0.75h, 0.75h), input.vertexColor.rgb * 1.55h, 0.85h), _UseVertexBiomeTint);

                // -----------------------------------------------------------------
                // Layer 4 (slope normal.y <= 0.78 OR y > 28m): Cracked Cliff Rock + AO
                // -----------------------------------------------------------------
                // Triplanar-friendly rock coordinate uses Y + X/Z projection so vertical cliffs never stretch
                float2 cliffUV = lerp(posWS.xz * 0.22, float2(posWS.x + posWS.z, posWS.y * 1.45) * 0.22, saturate(1.0h - geomNormal.y));
                float crackC = RidgedRockPattern(cliffUV);
                float crackR = RidgedRockPattern(cliffUV + float2(0.06, 0.0));
                float crackU = RidgedRockPattern(cliffUV + float2(0.0, 0.06));

                half3 rockBump = normalize(geomNormal + half3((half)(crackC - crackR) * 2.4h, 0.15h, (half)(crackC - crackU) * 2.4h));
                half creviceAO = lerp(0.42h, 1.08h, smoothstep(0.08h, 0.72h, (half)crackC));

                float highRockStart = _HighRockHeight + (fbmMacro - 0.5) * 6.5;
                half highAltRockMask = smoothstep((half)highRockStart - 3.5h, (half)highRockStart + 4.5h, (half)h);
                half slopeRockMask = 1.0h - flatMask;
                half rockMask = saturate(max(slopeRockMask, highAltRockMask));

                half3 rockCol = lerp(_CliffCreviceColor.rgb, _CliffColor.rgb, (half)crackC) * creviceAO;
                // Blend dark volcanic vertex color when climbing Volcanic Summit (y > 40m)
                half volcanoTint = saturate((half)((h - 42.0) / 55.0));
                rockCol = lerp(rockCol, input.vertexColor.rgb * creviceAO * 1.05h, volcanoTint * 0.65h);

                // Combine all 4 layers cleanly
                half grassCoverage = grassHeightMask * flatMask * (1.0h - highAltRockMask * 0.85h);
                half3 albedo = lerp(sandCol, grassCol, grassCoverage);
                albedo = lerp(albedo, rockCol, rockMask);

                // Combine perturbed surface normals (sand ripples vs cracked cliffs)
                half3 normalWS = normalize(lerp(sandNormal, geomNormal, grassCoverage));
                normalWS = normalize(lerp(normalWS, rockBump, rockMask));

                // Smoothness: Wet sand (0.75), Dry sand/grass matte (0.14), damp rock (0.34)
                half surfaceSmoothness = lerp(0.14h, 0.75h, wetMask * (1.0h - grassCoverage) * (1.0h - rockMask));
                surfaceSmoothness = lerp(surfaceSmoothness, 0.34h, rockMask * 0.5h);

                // URP Lighting (Main Directional Light + 4-Cascade Soft Shadows + Specular + Additional Point Lights)
                float4 shadowCoord = TransformWorldToShadowCoord(posWS);
                Light mainLight = GetMainLight(shadowCoord);
                half3 viewDir = (half3)SafeNormalize(GetWorldSpaceViewDir(posWS));

                half ndotl = saturate(dot(normalWS, (half3)mainLight.direction));
                half atten = (half)(mainLight.distanceAttenuation * mainLight.shadowAttenuation);
                half3 diffuse = (half3)mainLight.color * (ndotl * atten);

                // Blinn-Phong specular highlight for wet sand & damp rocks
                half3 halfDir = normalize((half3)mainLight.direction + viewDir);
                half ndoth = saturate(dot(normalWS, halfDir));
                half specPower = lerp(24.0h, 96.0h, surfaceSmoothness);
                half3 specular = (half3)mainLight.color * (pow(ndoth, specPower) * surfaceSmoothness * 0.55h * atten);

                #ifdef _ADDITIONAL_LIGHTS
                uint lightCount = GetAdditionalLightsCount();
                for (uint li = 0u; li < lightCount; ++li)
                {
                    Light addLight = GetAdditionalLight(li, posWS);
                    half addNdotL = saturate(dot(normalWS, (half3)addLight.direction));
                    diffuse += (half3)addLight.color * (addNdotL * (half)(addLight.distanceAttenuation * addLight.shadowAttenuation));
                }
                #endif

                half3 ambient = SampleSH(normalWS);
                half3 litColor = albedo * (diffuse + ambient) + specular;

                // Night BioGlow crystal/lava veins along rock cracks
                half veinMask = smoothstep(0.24h, 0.04h, (half)crackC);
                half bioScale = _GlobalBioluminescence * lerp(0.5h, 1.0h, saturate(_ThermalVFXMultiplier));
                half3 bioGlow = _BioVeinColor.rgb * (veinMask * rockMask * bioScale * 0.42h);

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
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _WetSandColor;
                float _WetSandHeight;
                half4 _DrySandColor;
                float _DrySandMaxHeight;
                half4 _GrassColor;
                half4 _MossColor;
                half4 _CliffColor;
                half4 _CliffCreviceColor;
                float _HighRockHeight;
                half _SlopeThreshold;
                float _NoiseScale;
                half _UseVertexBiomeTint;
                half4 _BioVeinColor;
            CBUFFER_END

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
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
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _WetSandColor;
                float _WetSandHeight;
                half4 _DrySandColor;
                float _DrySandMaxHeight;
                half4 _GrassColor;
                half4 _MossColor;
                half4 _CliffColor;
                half4 _CliffCreviceColor;
                float _HighRockHeight;
                half _SlopeThreshold;
                float _NoiseScale;
                half _UseVertexBiomeTint;
                half4 _BioVeinColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
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
