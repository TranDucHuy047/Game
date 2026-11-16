Shader "CoralArchipelago/URP/CoralBioluminescentLit"
{
    Properties
    {
        [Header(Base Surface)]
        _BaseMap ("Albedo Texture", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (0.85, 0.55, 0.50, 1)
        _Smoothness ("Smoothness", Range(0.0, 1.0)) = 0.35

        [Header(Bioluminescence Night Glow)]
        _EmissionMask ("Emission Mask (R)", 2D) = "white" {}
        [HDR] _GlowColor ("Bioluminescent Color", Color) = (0.10, 1.60, 2.20, 1)
        _DayGlowFloor ("Daytime Minimum Glow", Range(0.0, 0.5)) = 0.04
        _NightGlowMultiplier ("Night Glow Multiplier", Range(0.0, 5.0)) = 2.0
        _PulseSpeed ("Bio Pulse Speed", Range(0.0, 8.0)) = 1.8
        _WaveFrequency ("Bio Wave Frequency", Range(0.0, 6.0)) = 1.4
        _FresnelPower ("Rim Fresnel Power", Range(0.5, 6.0)) = 2.4
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
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_EmissionMask);
            SAMPLER(sampler_EmissionMask);

            // Global shader parameters driven by WorldTimeManager & iOSGraphicsAndThermalManager.
            // Declared outside UnityPerMaterial to preserve SRP Batcher compatibility.
            half _GlobalBioluminescence;
            half _ThermalVFXMultiplier;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _GlowColor;
                half _Smoothness;
                half _DayGlowFloor;
                half _NightGlowMultiplier;
                half _PulseSpeed;
                half _WaveFrequency;
                half _FresnelPower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

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
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = (half)ComputeFogFactor(posInputs.positionCS.z);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half4 albedoTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half3 albedo = albedoTex.rgb * _BaseColor.rgb;
                half mask = SAMPLE_TEXTURE2D(_EmissionMask, sampler_EmissionMask, input.uv).r;

                half3 normalWS = normalize(input.normalWS);
                half3 viewDirWS = (half3)SafeNormalize(GetWorldSpaceViewDir(input.positionWS));

                // Direct lighting + Shadows using Apple Silicon FP16 (half) precision
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half ndotl = saturate(dot(normalWS, (half3)mainLight.direction));
                half3 directDiffuse = (half3)mainLight.color * (ndotl * (half)(mainLight.distanceAttenuation * mainLight.shadowAttenuation));

                // Cheap Blinn-Phong specular highlight
                half3 halfDir = normalize((half3)mainLight.direction + viewDirWS);
                half specPower = lerp(8.0h, 64.0h, _Smoothness);
                half spec = pow(saturate(dot(normalWS, halfDir)), specPower) * _Smoothness * 0.45h;
                half3 specular = (half3)mainLight.color * (spec * (half)mainLight.shadowAttenuation);

                // Spherical Harmonics ambient sky/ground bounce
                half3 ambient = SampleSH(normalWS) * albedo;
                half3 litColor = albedo * directDiffuse + ambient + specular;

                // Nighttime Bioluminescence (Pulse + Fresnel Rim) without dynamic point lights
                half nightCurve = saturate(_DayGlowFloor + _GlobalBioluminescence * _NightGlowMultiplier);
                half wavePhase = (half)_Time.y * _PulseSpeed + (half)(input.positionWS.y + input.positionWS.x * 0.5) * _WaveFrequency;
                half pulse = 0.72h + 0.28h * sin(wavePhase);

                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDirWS)), _FresnelPower);
                half thermalScale = lerp(0.55h, 1.0h, saturate(_ThermalVFXMultiplier));
                half3 emission = _GlowColor.rgb * (mask * nightCurve * pulse * (0.65h + fresnel * 0.85h) * thermalScale);

                half3 finalColor = litColor + emission;
                finalColor = MixFog(finalColor, input.fogFactor);

                return half4(finalColor, 1.0h);
            }
            ENDHLSL
        }

        // ShadowCaster Pass so corals & lamps cast clean shadows without Fallback errors
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _GlowColor;
                half _Smoothness;
                half _DayGlowFloor;
                half _NightGlowMultiplier;
                half _PulseSpeed;
                half _WaveFrequency;
                half _FresnelPower;
            CBUFFER_END

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

            float3 _LightDirection;

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

        // DepthOnly Pass so water shader _CameraDepthTexture detects coral shores accurately
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
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _GlowColor;
                half _Smoothness;
                half _DayGlowFloor;
                half _NightGlowMultiplier;
                half _PulseSpeed;
                half _WaveFrequency;
                half _FresnelPower;
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
