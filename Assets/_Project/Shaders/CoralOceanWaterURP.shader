Shader "CoralArchipelago/URP/CoralOceanWaterURP"
{
    Properties
    {
        [Header(Water Depth Colors)]
        _ShallowColor ("Shallow Turquoise Lagoon", Color) = (0.10, 0.84, 0.80, 0.56)
        _DeepColor ("Deep Ocean Color", Color) = (0.02, 0.18, 0.38, 0.93)
        _NightWaterTint ("Night Deep Tint", Color) = (0.01, 0.05, 0.16, 0.96)
        _DepthMaxDistance ("Depth Fade Distance", Range(0.5, 25.0)) = 7.5

        [Header(Gerstner 3D Waves)]
        _WaveAmplitude ("Primary Wave Height", Range(0.0, 0.6)) = 0.16
        _WaveFrequency ("Wave Frequency", Range(0.1, 3.0)) = 0.48
        _WaveSpeed ("Wave Speed", Range(0.1, 4.0)) = 1.25
        _WaveSteepness ("Gerstner Steepness", Range(0.05, 0.65)) = 0.35

        [Header(Shoreline Foam And Night BioGlow)]
        [HDR] _BiolumFoamColor ("Bioluminescent Glow Color", Color) = (0.12, 1.90, 2.55, 1.0)
        _DayFoamColor ("Day Shoreline Foam", Color) = (0.95, 0.99, 1.0, 1.0)
        _ShoreFoamDistance ("Shoreline Foam Width", Range(0.1, 4.5)) = 1.65
        _FoamScale ("Foam Pattern Scale", Range(0.5, 10.0)) = 3.4

        [Header(Sun and Moon Reflections)]
        _SpecularPower ("Specular Sharpness", Range(16.0, 256.0)) = 112.0
        _SpecularStrength ("Specular Strength", Range(0.0, 2.5)) = 1.15
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        LOD 200

        Pass
        {
            Name "WaterForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            half _GlobalBioluminescence;
            half _ThermalVFXMultiplier;

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _NightWaterTint;
                float _DepthMaxDistance;
                float _WaveAmplitude;
                float _WaveFrequency;
                float _WaveSpeed;
                float _WaveSteepness;
                half4 _BiolumFoamColor;
                half4 _DayFoamColor;
                float _ShoreFoamDistance;
                float _FoamScale;
                half _SpecularPower;
                half _SpecularStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half waveHeight : TEXCOORD3;
                half fogFactor : TEXCOORD4;
            };

            void AccumulateGerstnerWave(
                float2 dir,
                float steepness,
                float freq,
                float speed,
                float amp,
                float3 basePos,
                inout float3 disp,
                inout float3 tangent,
                inout float3 binormal)
            {
                float phase = dot(dir, basePos.xz) * freq + _Time.y * speed;
                float c = cos(phase);
                float s = sin(phase);

                disp.x += dir.x * (steepness * amp) * c;
                disp.z += dir.y * (steepness * amp) * c;
                disp.y += amp * s;

                float wa = freq * amp;
                tangent += float3(
                    -dir.x * dir.x * (steepness * wa) * s,
                    dir.x * wa * c,
                    -dir.x * dir.y * (steepness * wa) * s);

                binormal += float3(
                    -dir.x * dir.y * (steepness * wa) * s,
                    dir.y * wa * c,
                    -dir.y * dir.y * (steepness * wa) * s);
            }

            Varyings vert(Attributes input)
            {
                Varyings output;

                float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                float3 disp = float3(0.0, 0.0, 0.0);
                float3 tangent = float3(1.0, 0.0, 0.0);
                float3 binormal = float3(0.0, 0.0, 1.0);

                // 3 intersecting Gerstner waves for natural open-sea swells
                AccumulateGerstnerWave(normalize(float2(0.85, 0.52)), _WaveSteepness, _WaveFrequency, _WaveSpeed, _WaveAmplitude, worldPos, disp, tangent, binormal);
                AccumulateGerstnerWave(normalize(float2(-0.55, 0.83)), _WaveSteepness * 0.75, _WaveFrequency * 1.45, _WaveSpeed * 1.18, _WaveAmplitude * 0.55, worldPos, disp, tangent, binormal);
                AccumulateGerstnerWave(normalize(float2(0.25, -0.96)), _WaveSteepness * 0.5, _WaveFrequency * 2.25, _WaveSpeed * 1.42, _WaveAmplitude * 0.28, worldPos, disp, tangent, binormal);

                float3 displacedWS = worldPos + disp;
                half3 waveNormal = (half3)normalize(cross(binormal, tangent));

                output.positionWS = displacedWS;
                output.positionCS = TransformWorldToHClip(displacedWS);
                output.screenPos = ComputeScreenPos(output.positionCS);
                output.normalWS = waveNormal;
                output.waveHeight = (half)(disp.y / max(_WaveAmplitude * 1.6, 0.001));
                output.fogFactor = (half)ComputeFogFactor(output.positionCS.z);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 screenUV = input.screenPos.xy / max(input.screenPos.w, 0.0001);
                float rawSceneDepth = SampleSceneDepth(screenUV);
                float sceneEyeDepth = LinearEyeDepth(rawSceneDepth, _ZBufferParams);
                float waterEyeDepth = input.screenPos.w;
                float waterDepth = max(0.0, sceneEyeDepth - waterEyeDepth);

                half depthFactor = saturate((half)(waterDepth / max(_DepthMaxDistance, 0.01)));
                half nightBlend = saturate(_GlobalBioluminescence);

                half3 deepCol = lerp(_DeepColor.rgb, _NightWaterTint.rgb, nightBlend * 0.72h);
                half3 waterColor = lerp(_ShallowColor.rgb, deepCol, depthFactor);
                half waterAlpha = lerp(_ShallowColor.a, _DeepColor.a, depthFactor);

                // Animated shoreline foam & wave crest foam
                half shoreProximity = 1.0h - saturate((half)(waterDepth / max(_ShoreFoamDistance, 0.01)));
                float rippleCoord = (input.positionWS.x + input.positionWS.z) * _FoamScale;
                float rippleTime = _Time.y * 2.2 - shoreProximity * 4.5;
                half rippleA = (half)sin(rippleCoord + rippleTime);
                half rippleB = (half)cos((input.positionWS.x * 1.1 - input.positionWS.z * 0.9) * _FoamScale - _Time.y * 1.7);
                half foamNoise = 0.68h + (rippleA * rippleB) * 0.45h;

                half shoreFoam = saturate(smoothstep(0.08h, 0.75h, shoreProximity * foamNoise));
                half crestFoam = saturate((input.waveHeight - 0.52h) * 2.2h);
                half totalFoam = saturate(shoreFoam + crestFoam * 0.45h);

                half3 foamColor = lerp(_DayFoamColor.rgb, _BiolumFoamColor.rgb, nightBlend * 0.85h);
                half thermalScale = lerp(0.55h, 1.0h, saturate(_ThermalVFXMultiplier));
                half bioGlow = nightBlend * thermalScale;

                waterColor = lerp(waterColor, foamColor, totalFoam * 0.82h);
                waterColor += _BiolumFoamColor.rgb * ((shoreFoam * 1.55h + crestFoam * 0.85h) * bioGlow);

                // Sun / Moon specular reflection + Fresnel horizon sheen
                Light mainLight = GetMainLight();
                half3 viewDir = (half3)SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half3 normalWS = normalize(input.normalWS);
                half3 halfDir = normalize((half3)mainLight.direction + viewDir);

                half ndoth = saturate(dot(normalWS, halfDir));
                half spec = pow(ndoth, _SpecularPower) * _SpecularStrength;
                waterColor += (half3)mainLight.color * spec;

                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDir)), 3.2h);
                half3 skySheen = lerp(half3(0.45h, 0.78h, 0.95h), _BiolumFoamColor.rgb * 0.35h, nightBlend);
                waterColor += skySheen * (fresnel * 0.25h);
                waterAlpha = saturate(waterAlpha + fresnel * 0.22h + totalFoam * 0.30h);

                waterColor = MixFog(waterColor, input.fogFactor);
                return half4(waterColor, waterAlpha);
            }
            ENDHLSL
        }
    }
}
