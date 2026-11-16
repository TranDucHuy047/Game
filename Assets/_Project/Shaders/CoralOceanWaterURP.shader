Shader "CoralArchipelago/URP/CoralOceanWaterURP"
{
    Properties
    {
        [Header(Water Depth Colors)]
        _ShallowColor ("Shallow Lagoon Color", Color) = (0.12, 0.78, 0.76, 0.72)
        _DeepColor ("Deep Ocean Color", Color) = (0.02, 0.14, 0.30, 0.94)
        _NightWaterTint ("Night Deep Tint", Color) = (0.01, 0.04, 0.14, 0.96)
        _DepthMaxDistance ("Depth Fade Distance", Range(0.5, 25.0)) = 6.5

        [Header(Waves)]
        _WaveAmplitude ("Wave Height", Range(0.0, 0.6)) = 0.14
        _WaveFrequency ("Wave Frequency", Range(0.1, 3.0)) = 0.65
        _WaveSpeed ("Wave Speed", Range(0.1, 4.0)) = 1.25

        [Header(Bioluminescent Shore Foam)]
        [HDR] _BiolumFoamColor ("Bioluminescent Foam Color", Color) = (0.15, 1.80, 2.40, 1.0)
        _DayFoamColor ("Day Foam Color", Color) = (0.90, 0.98, 1.0, 1.0)
        _ShoreFoamDistance ("Shore Foam Width", Range(0.1, 4.0)) = 1.4
        _FoamScale ("Foam Pattern Scale", Range(0.5, 10.0)) = 3.2

        [Header(Specular Highlights)]
        _SpecularPower ("Sun/Moon Specular Sharpness", Range(16.0, 256.0)) = 96.0
        _SpecularStrength ("Specular Strength", Range(0.0, 2.0)) = 0.85
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

            Varyings vert(Attributes input)
            {
                Varyings output;

                float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                float t = _Time.y * _WaveSpeed;

                // Dual intersecting directional waves (cheap on mobile ALU)
                float phase1 = worldPos.x * _WaveFrequency + worldPos.z * (_WaveFrequency * 0.65) + t;
                float phase2 = worldPos.z * (_WaveFrequency * 1.3) - worldPos.x * (_WaveFrequency * 0.45) + t * 1.15;

                float h1 = sin(phase1) * _WaveAmplitude;
                float h2 = cos(phase2) * (_WaveAmplitude * 0.55);
                float totalH = h1 + h2;

                worldPos.y += totalH;

                // Analytical wave derivatives for crisp specular reflections without normal map texture fetch
                float dHx = cos(phase1) * _WaveAmplitude * _WaveFrequency
                          + sin(phase2) * (_WaveAmplitude * 0.55) * (_WaveFrequency * 0.45);
                float dHz = cos(phase1) * _WaveAmplitude * (_WaveFrequency * 0.65)
                          - sin(phase2) * (_WaveAmplitude * 0.55) * (_WaveFrequency * 1.3);

                half3 waveNormal = normalize(half3((half)(-dHx), 1.0h, (half)(-dHz)));

                output.positionWS = worldPos;
                output.positionCS = TransformWorldToHClip(worldPos);
                output.screenPos = ComputeScreenPos(output.positionCS);
                output.normalWS = waveNormal;
                output.waveHeight = (half)(totalH / max(_WaveAmplitude, 0.001));
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

                half3 deepCol = lerp(_DeepColor.rgb, _NightWaterTint.rgb, nightBlend * 0.75h);
                half3 waterColor = lerp(_ShallowColor.rgb, deepCol, depthFactor);
                half waterAlpha = lerp(_ShallowColor.a, _DeepColor.a, depthFactor);

                // Procedural shore & wave crest foam (zero texture bandwidth cost on mobile)
                half shoreProximity = 1.0h - saturate((half)(waterDepth / max(_ShoreFoamDistance, 0.01)));
                float rippleCoord = (input.positionWS.x + input.positionWS.z) * _FoamScale;
                half ripplePattern = (half)(sin(rippleCoord + _Time.y * 2.4) * cos((input.positionWS.x - input.positionWS.z) * _FoamScale * 0.8 - _Time.y * 1.9));
                half shoreFoam = saturate(shoreProximity * (0.75h + ripplePattern * 0.45h));
                half crestFoam = saturate((input.waveHeight - 0.55h) * 2.0h);

                half totalFoam = saturate(shoreFoam + crestFoam * 0.45h);
                half3 foamColor = lerp(_DayFoamColor.rgb, _BiolumFoamColor.rgb, nightBlend);

                // Nighttime bioluminescent boost scales down gently if iOS hits thermal limits
                half thermalScale = lerp(0.5h, 1.0h, saturate(_ThermalVFXMultiplier));
                half bioGlow = nightBlend * thermalScale;

                waterColor = lerp(waterColor, foamColor, totalFoam * 0.75h);
                waterColor += _BiolumFoamColor.rgb * ((shoreFoam * 1.35h + crestFoam * 0.75h) * bioGlow);

                // Sun / Moon reflection highlight
                Light mainLight = GetMainLight();
                half3 viewDir = (half3)SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half3 normalWS = normalize(input.normalWS);
                half3 halfDir = normalize((half3)mainLight.direction + viewDir);

                half ndoth = saturate(dot(normalWS, halfDir));
                half spec = pow(ndoth, _SpecularPower) * _SpecularStrength;
                waterColor += (half3)mainLight.color * spec;

                // Subtle Fresnel reflection boost near horizon
                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDir)), 3.0h);
                waterAlpha = saturate(waterAlpha + fresnel * 0.2h + totalFoam * 0.25h);

                waterColor = MixFog(waterColor, input.fogFactor);
                return half4(waterColor, waterAlpha);
            }
            ENDHLSL
        }
    }
}
