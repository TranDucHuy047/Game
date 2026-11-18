Shader "CoralArchipelago/URP/TropicalLagoonWaterURP"
{
    Properties
    {
        [Header(Lagoon And Ocean Depth Colors)]
        _ShallowColor ("Shallow Turquoise Lagoon", Color) = (0.071, 0.839, 0.875, 0.56)
        _DeepColor ("Deep Ocean Blue", Color) = (0.024, 0.259, 0.451, 0.94)
        _NightDeepColor ("Night Ocean Deep Tint", Color) = (0.012, 0.06, 0.16, 0.96)
        _DepthFadeDistance ("Depth Fade Distance", Range(0.5, 25.0)) = 6.5

        [Header(Gerstner 3D Ocean Waves)]
        _WaveAmplitude ("Wave Height", Range(0.0, 0.5)) = 0.13
        _WaveFrequency ("Wave Frequency", Range(0.1, 3.0)) = 0.52
        _WaveSpeed ("Wave Speed", Range(0.1, 4.0)) = 1.35
        _WaveSteepness ("Gerstner Steepness", Range(0.05, 0.65)) = 0.32

        [Header(Shoreline Foam And BioGlow)]
        _FoamColor ("Shoreline Foam White", Color) = (0.96, 0.99, 1.0, 1.0)
        [HDR] _BiolumFoamColor ("Night BioGlow Foam", Color) = (0.14, 2.0, 2.6, 1.0)
        _ShoreFoamWidth ("Shoreline Foam Width", Range(0.1, 4.5)) = 1.95
        _FoamPatternScale ("Foam Ripple Scale", Range(0.5, 10.0)) = 3.2

        [Header(Sky Fresnel And Sun Specular Glint)]
        _SpecularPower ("Sun Glint Sharpness", Range(32.0, 320.0)) = 180.0
        _SpecularStrength ("Sun Glint Strength", Range(0.0, 4.0)) = 1.65
        _FresnelStrength ("Sky Fresnel Reflection", Range(0.0, 2.0)) = 0.85
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
                float _WaveSteepness;
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

            void AddGerstnerWave(
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
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                float3 disp = float3(0.0, 0.0, 0.0);
                float3 tangent = float3(1.0, 0.0, 0.0);
                float3 binormal = float3(0.0, 0.0, 1.0);

                AddGerstnerWave(normalize(float2(0.84, 0.54)), _WaveSteepness, _WaveFrequency, _WaveSpeed, _WaveAmplitude, worldPos, disp, tangent, binormal);
                AddGerstnerWave(normalize(float2(-0.52, 0.85)), _WaveSteepness * 0.72, _WaveFrequency * 1.48, _WaveSpeed * 1.22, _WaveAmplitude * 0.52, worldPos, disp, tangent, binormal);
                AddGerstnerWave(normalize(float2(0.28, -0.96)), _WaveSteepness * 0.45, _WaveFrequency * 2.35, _WaveSpeed * 1.48, _WaveAmplitude * 0.24, worldPos, disp, tangent, binormal);

                float3 displacedWS = worldPos + disp;
                output.positionWS = displacedWS;
                output.positionCS = TransformWorldToHClip(displacedWS);
                output.screenPos = ComputeScreenPos(output.positionCS);
                output.normalWS = (half3)normalize(cross(binormal, tangent));
                output.waveHeight = (half)(disp.y / max(_WaveAmplitude * 1.5, 0.001));
                output.fogFactor = (half)ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // Depth buffer comparison for crystal-clear shallow turquoise (#12D6DF) -> deep ocean (#064273)
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

                // Procedural capillary ripples normal for tropical sun sparkle
                float t = _Time.y;
                float rx = sin(input.positionWS.x * 2.4 + t * 2.3) + cos(input.positionWS.z * 2.1 - t * 1.9);
                float rz = cos(input.positionWS.z * 2.7 + t * 2.1) - sin(input.positionWS.x * 1.9 - t * 1.7);
                half3 normalWS = normalize(input.normalWS + half3((half)rx * 0.055h, 0.0h, (half)rz * 0.055h));

                // Shoreline foam + rhythmic lapping wave bands where water meets sand & rocks
                half shoreProximity = 1.0h - saturate((half)(waterDepth / max(_ShoreFoamWidth, 0.01)));
                float ringPattern = sin(waterDepth * 7.2 - t * 3.1 + rx * 0.9);
                float foamNoise = 0.65 + 0.35 * sin(input.positionWS.x * _FoamPatternScale + cos(input.positionWS.z * _FoamPatternScale - t * 1.8));
                half shoreFoamMask = saturate(smoothstep(0.18h, 0.78h, shoreProximity * ((half)foamNoise + (half)ringPattern * 0.24h * shoreProximity)));
                half crestFoamMask = saturate((input.waveHeight - 0.56h) * 2.1h);
                half totalFoam = saturate(shoreFoamMask + crestFoamMask * 0.40h);

                half3 foamTint = lerp(_FoamColor.rgb, _BiolumFoamColor.rgb, nightBlend * 0.85h);
                waterCol = lerp(waterCol, foamTint, totalFoam * 0.88h);

                half bioScale = nightBlend * lerp(0.55h, 1.0h, saturate(_ThermalVFXMultiplier));
                waterCol += _BiolumFoamColor.rgb * ((shoreFoamMask * 1.45h + crestFoamMask * 0.75h) * bioScale);

                // Sky Fresnel reflection & Sun Specular Glint
                Light mainLight = GetMainLight();
                half3 viewDir = (half3)SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half3 reflDir = reflect(-viewDir, normalWS);

                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDir)), 3.6h) * _FresnelStrength;
                half skyFactor = saturate(reflDir.y * 0.8h + 0.2h);
                half3 daySky = lerp(half3(0.58h, 0.86h, 0.98h), half3(0.24h, 0.62h, 0.96h), skyFactor);
                half3 nightSky = lerp(_BiolumFoamColor.rgb * 0.32h, half3(0.05h, 0.14h, 0.28h), skyFactor);
                half3 skyRefl = lerp(daySky, nightSky, nightBlend);
                waterCol = lerp(waterCol, skyRefl, saturate(fresnel * 0.65h));

                half3 halfDir = normalize((half3)mainLight.direction + viewDir);
                half ndoth = saturate(dot(normalWS, halfDir));
                half spec = pow(ndoth, _SpecularPower) * _SpecularStrength;
                waterCol += (half3)mainLight.color * spec;

                waterAlpha = saturate(waterAlpha + fresnel * 0.24h + totalFoam * 0.34h);
                waterCol = MixFog(waterCol, input.fogFactor);
                return half4(waterCol, waterAlpha);
            }
            ENDHLSL
        }
    }
}
