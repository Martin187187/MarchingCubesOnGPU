Shader "Custom/PebbleArray_AlbedoFromSlice"
{
    Properties
    {
        // Dummy texture to enable uv_MainTex in surface Input (we don't actually sample this)
        _MainTex ("UV Source", 2D) = "white" {}

        // Albedo array containing your slices
        _AlbedoArray ("Albedo Array", 2DArray) = "" {}

        // Slice index (set per-renderer via MaterialPropertyBlock as a float)
        _TextureIndex ("Texture Index", Float) = 0

        // PBR controls
        _Metallic   ("Metallic", Range(0,1)) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 300

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.5  // needed for Texture2DArray

        #include "UnityCG.cginc"

        UNITY_DECLARE_TEX2DARRAY(_AlbedoArray);

        struct Input
        {
            float2 uv_MainTex;   // use mesh UV0 through the _MainTex channel
        };

        half  _Metallic;
        half  _Smoothness;
        float _TextureIndex;    // float property; we cast to int for the slice

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Round & clamp slice just in case (clamp at material side if you like)
            int slice = (int)round(_TextureIndex);

            // Sample the array: xy = UVs, z = slice index
            float3 uvw = float3(IN.uv_MainTex, slice);
            fixed4 tex = UNITY_SAMPLE_TEX2DARRAY(_AlbedoArray, uvw);

            // Overwrite albedo with the sampled texture (no tint)
            o.Albedo     = tex.rgb;
            o.Metallic   = _Metallic;
            o.Smoothness = _Smoothness;
            o.Alpha      = 1;
        }
        ENDCG
    }

    FallBack "Diffuse"
}
