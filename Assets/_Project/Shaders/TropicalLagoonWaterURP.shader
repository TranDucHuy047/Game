Shader "CoralArchipelago/URP/TropicalLagoonWaterURP"
{
    Properties
    {
        [Header(Lagoon And Ocean Depth Colors)]
        _ShallowColor ("Shallow Turquoise Lagoon", Color) = (0.071, 0.839, 0.875, 0.54)
        _DeepColor ("Deep Ocean Blue", Color) = (0.024, 0.259, 0.451, 0.92)
        _NightDeepColor ("Night Ocean Deep Tint", Color) = (0.012, 0.06, 0.16, 0.96)
        _DepthFadeDistance ("Depth Fade Distance", Range(0.5, 25.0)) = 6.5

        [Header(Multi Directional Organic Water Ripples)]
        _WaveAmplitude ("Wave Height", Range(0.0, 0.2)) = 0.03
        _WaveFrequency ("Noise Ripple Scale", Range(0.05, 2.0)) = 0.24
        _WaveSpeed ("Flow Speed", Range(0.05, 3.0)) = 0.55

        [Header(Shoreline Foam And BioGlow)]
        _FoamColor ("Shoreline Foam White", Color) = (0.96, 0.99, 1.0, 1.0)
        [HDR] _BiolumFoamColor ("Night BioGlow Foam", Color) = (0.14, 2.0, 2.6, 1.0)
        _ShoreFoamWidth ("Shoreline Foam Width", Range(0.1, 4.5)) = 1.85
        _FoamPatternScale ("Foam Noise Scale", Range(0.2, 6.0)) = 1.15

        [Header(Sky Fresnel And Gentle Sun Glint)]
        _SpecularPower ("Sun Glint Sharpness", Range(48.0, 320.0)) = 210.0
        _SpecularStrength ("Sun Glint Strength", Range(0.0, 1.0)) = 0.18
        _FresnelStrength ("Sky Fresnel Reflection", Range(0.0, 2.0)) = 0.52
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        LOD 250

        Pass
        {
            Name "LagoonWaterForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            half _GlobalBioluminescence;
            half _ThermalVFXMultiplier;

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _NightDeepColor;
                float _DepthFadeDistance;
                float _WaveAmplitude;
                float _WaveFrequency;
                float _WaveSpeed;
                half4 _FoamColor;
                half4 _BiolumFoamColor;
                float _ShoreFoamWidth;
                float _FoamPatternScale;
                half _SpecularPower;
                half _SpecularStrength;
                half _FresnelStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half waveHeight : TEXCOORD3;
                half fogFactor : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float2 Hash22(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.xx + p3.yz) * p3.zy) * 2.0 - 1.0;
            }

            float PerlinNoise2D(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);

                float a = dot(Hash22(i + float2(0.0, 0.0)), f - float2(0.0, 0.0));
                float b = dot(Hash22(i + float2(1.0, 0.0)), f - float2(1.0, 0.0));
                float c = dot(Hash22(i + float2(0.0, 1.0)), f - float2(0.0, 1.0));
                float d = dot(Hash22(i + float2(1.0, 1.0)), f - float2(1.0, 1.0));

                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            // Multi-directional rotated Perlin octaves (zero horizontal stripes or grid alignment)
            float MultiDirWaterNoise(float2 posXZ, float t)
            {
                float2 p1 = float2(posXZ.x * 0.80 - posXZ.y * 0.60, posXZ.x * 0.60 + posXZ.y * 0.80);
                float2 p2 = float2(posXZ.x * -0.54 - posXZ.y * 0.84, posXZ.x * 0.84 - posXZ.y * 0.54);
                float2 p3 = float2(posXZ.x * 0.36 + posXZ.y * 0.93, -posXZ.x * 0.93 + posXZ.y * 0.36);

                float n1 = PerlinNoise2D(p1 * _WaveFrequency + float2(t * 0.38, -t * 0.24));
                float n2 = PerlinNoise2D(p2 * (_WaveFrequency * 1.95) + float2(-t * 0.45, t * 0.33));
                float n3 = PerlinNoise2D(p3 * (_WaveFrequency * 3.70) + float2(t * 0.58, t * 0.41));

                return n1 * 0.55 + n2 * 0.30 + n3 * 0.15;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                float t = _Time.y * _WaveSpeed;

                // Smooth micro-elevation only (_WaveAmplitude = 0.03), flat smooth normal avoids coarse mesh striping
                float n = MultiDirWaterNoise(worldPos.xz * 0.45, t);
                float dispY = n * _WaveAmplitude;

                float3 displacedWS = worldPos + float3(0.0, dispY, 0.0);
                output.positionWS = displacedWS;
                output.positionCS = TransformWorldToHClip(displacedWS);
                output.screenPos = ComputeScreenPos(output.positionCS);
                output.normalWS = half3(0.0h, 1.0h, 0.0h);
                output.waveHeight = (half)saturate(n * 0.5 + 0.5);
                output.fogFactor = (half)ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // Depth buffer comparison for translucent turquoise shallow lagoon (#12D6DF) -> deep ocean (#064273)
                float2 screenUV = input.screenPos.xy / max(input.screenPos.w, 0.0001);
                float rawSceneDepth = SampleSceneDepth(screenUV);
                float sceneEyeDepth = LinearEyeDepth(rawSceneDepth, _ZBufferParams);
                float waterEyeDepth = input.screenPos.w;
                float waterDepth = max(0.0, sceneEyeDepth - waterEyeDepth);

                half depthFactor = saturate((half)(waterDepth / max(_DepthFadeDistance, 0.01)));
                half nightBlend = saturate(_GlobalBioluminescence);

                half3 deepCol = lerp(_DeepColor.rgb, _NightDeepColor.rgb, nightBlend * 0.75h);
                half3 waterCol = lerp(_ShallowColor.rgb, deepCol, depthFactor);
                half waterAlpha = lerp(_ShallowColor.a, _DeepColor.a, depthFactor);

                // Multi-directional Perlin ripple gradient normal (subtle natural water surface)
                float t = _Time.y * _WaveSpeed;
                float2 samplePos = input.positionWS.xz * 1.15;
                const float eps = 0.18;
                float hC = MultiDirWaterNoise(samplePos, t);
                float hX = MultiDirWaterNoise(samplePos + float2(eps, 0.0), t);
                float hZ = MultiDirWaterNoise(samplePos + float2(0.0, eps), t);

                float nx = (hC - hX) * 0.22;
                float nz = (hC - hZ) * 0.22;
                half3 normalWS = normalize(half3((half)nx, 1.0h, (half)nz));

                // Organic noisy shoreline foam where shallow water meets sand or rocks (zero periodic bands)
                half shoreProximity = 1.0h - saturate((half)(waterDepth / max(_ShoreFoamWidth, 0.01)));
                float foamNoiseA = PerlinNoise2D(input.positionWS.xz * _FoamPatternScale + float2(t * 0.25, -t * 0.19)) * 0.5 + 0.5;
                float foamNoiseB = PerlinNoise2D(input.positionWS.xz * (_FoamPatternScale * 2.4) + float2(-t * 0.31, t * 0.22)) * 0.5 + 0.5;
                float organicFoam = foamNoiseA * 0.65 + foamNoiseB * 0.35;

                half shoreFoamMask = saturate(smoothstep(0.24h, 0.72h, shoreProximity * (half)(0.55 + organicFoam * 0.85)));
                half crestFoamMask = saturate(((half)organicFoam - 0.82h) * 2.5h) * (1.0h - depthFactor * 0.65h);
                half totalFoam = saturate(shoreFoamMask + crestFoamMask * 0.22h);

                half3 foamTint = lerp(_FoamColor.rgb, _BiolumFoamColor.rgb, nightBlend * 0.85h);
                waterCol = lerp(waterCol, foamTint, totalFoam * 0.84h);

                half bioScale = nightBlend * lerp(0.55h, 1.0h, saturate(_ThermalVFXMultiplier));
                waterCol += _BiolumFoamColor.rgb * ((shoreFoamMask * 1.25h + crestFoamMask * 0.55h) * bioScale);

                // Soft sky Fresnel reflection & restrained Sun Specular Glint (sunSpec * 0.18)
                Light mainLight = GetMainLight();
                half3 viewDir = (half3)SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half3 reflDir = reflect(-viewDir, normalWS);

                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDir)), 4.2h) * _FresnelStrength;
                half skyFactor = saturate(reflDir.y * 0.8h + 0.2h);
                half3 daySky = lerp(half3(0.56h, 0.84h, 0.96h), half3(0.24h, 0.60h, 0.92h), skyFactor);
                half3 nightSky = lerp(_BiolumFoamColor.rgb * 0.28h, half3(0.05h, 0.14h, 0.28h), skyFactor);
                half3 skyRefl = lerp(daySky, nightSky, nightBlend);
                waterCol = lerp(waterCol, skyRefl, saturate(fresnel * 0.45h));

                half3 halfDir = normalize((half3)mainLight.direction + viewDir);
                half ndoth = saturate(dot(normalWS, halfDir));
                half sunSpec = pow(ndoth, _SpecularPower);
                waterCol += (half3)mainLight.color * (sunSpec * min(_SpecularStrength, 0.18h));

                waterAlpha = saturate(waterAlpha + fresnel * 0.16h + totalFoam * 0.30h);
                waterCol = MixFog(waterCol, input.fogFactor);
                return half4(waterCol, waterAlpha);
            }
            ENDHLSL
        }
    }
}
