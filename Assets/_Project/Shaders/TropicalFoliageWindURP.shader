Shader "CoralArchipelago/URP/TropicalFoliageWindURP"
{
    Properties
    {
        [Header(Foliage Surface Colors)]
        _BaseColor ("Primary Foliage Color", Color) = (0.18, 0.56, 0.20, 1)
        _TipColor ("Sunlit Frond Tip Color", Color) = (0.36, 0.72, 0.24, 1)
        _Smoothness ("Smoothness", Range(0.0, 1.0)) = 0.36

        [Header(Vertex Wind Sway Animation)]
        _WindSpeed ("Wind Speed", Range(0.0, 6.0)) = 2.15
        _WindAmplitude ("Wind Sway Strength", Range(0.0, 1.0)) = 0.22
        _WindFrequency ("Wind Spatial Frequency", Range(0.1, 4.0)) = 0.65
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

            Cull Off
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

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _TipColor;
                half _Smoothness;
                float _WindSpeed;
                float _WindAmplitude;
                float _WindFrequency;
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
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half tipFactor : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float3 ApplyTropicalWind(float3 positionOS)
            {
                float3 posWS = TransformObjectToWorld(positionOS);
                float bendMask = saturate(length(positionOS.xz) * 0.45 + max(0.0, positionOS.y) * 0.35);
                float phase = _Time.y * _WindSpeed + (posWS.x + posWS.z) * _WindFrequency;
                float gust = sin(phase) + 0.45 * cos(phase * 1.7 + posWS.x * 0.3);

                float3 offset = float3(
                    gust * _WindAmplitude * bendMask,
                    cos(phase * 1.3) * (_WindAmplitude * 0.45) * bendMask,
                    sin(phase * 0.9) * (_WindAmplitude * 0.75) * bendMask);

                return positionOS + offset;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float3 swayedOS = ApplyTropicalWind(input.positionOS.xyz);
                VertexPositionInputs posInputs = GetVertexPositionInputs(swayedOS);
                VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = (half3)normalize(normInputs.normalWS);
                output.tipFactor = saturate((half)(length(input.positionOS.xz) * 0.32 + input.uv.y * 0.55));
                output.fogFactor = (half)ComputeFogFactor(posInputs.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half3 normalWS = normalize(input.normalWS);

                half3 albedo = lerp(_BaseColor.rgb, _TipColor.rgb, input.tipFactor);
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half3 lightDir = (half3)mainLight.direction;
                // Wrap diffuse + subtle foliage subsurface translucency when backlit
                half ndotlWrap = saturate(dot(normalWS, lightDir) * 0.65h + 0.35h);
                half3 viewDir = (half3)SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half backLit = pow(saturate(dot(viewDir, -lightDir)), 3.0h);

                half atten = (half)(mainLight.distanceAttenuation * lerp(0.55h, 1.0h, (half)mainLight.shadowAttenuation));
                half3 diffuse = (half3)mainLight.color * (ndotlWrap * atten);
                half3 sss = _TipColor.rgb * (half3)mainLight.color * (backLit * 0.38h * atten);
                half3 ambient = SampleSH(normalWS);

                half3 finalColor = albedo * (diffuse + ambient) + sss;
                finalColor = MixFog(finalColor, input.fogFactor);
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
            Cull Off

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _TipColor;
                half _Smoothness;
                float _WindSpeed;
                float _WindAmplitude;
                float _WindFrequency;
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
                float3 posWSBase = TransformObjectToWorld(input.positionOS.xyz);
                float bendMask = saturate(length(input.positionOS.xz) * 0.45 + max(0.0, input.positionOS.y) * 0.35);
                float phase = _Time.y * _WindSpeed + (posWSBase.x + posWSBase.z) * _WindFrequency;
                float gust = sin(phase) + 0.45 * cos(phase * 1.7 + posWSBase.x * 0.3);
                float3 swayedOS = input.positionOS.xyz + float3(
                    gust * _WindAmplitude * bendMask,
                    cos(phase * 1.3) * (_WindAmplitude * 0.45) * bendMask,
                    sin(phase * 0.9) * (_WindAmplitude * 0.75) * bendMask);

                float3 posWS = TransformObjectToWorld(swayedOS);
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
            Cull Off

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _TipColor;
                half _Smoothness;
                float _WindSpeed;
                float _WindAmplitude;
                float _WindFrequency;
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
