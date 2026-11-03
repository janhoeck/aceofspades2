// Einfarbig ohne Licht, z. B. für den Rahmen um den anvisierten Block.
Shader "AoS2/Unlit Color"
{
    Properties
    {
        _Color ("Farbe", Color) = (0, 0, 0, 0.6)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "Unlit"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            half4 _Color;
            CBUFFER_END

            float4 Vert(float3 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS);
            }

            half4 Frag() : SV_Target { return _Color; }
            ENDHLSL
        }
    }
}
