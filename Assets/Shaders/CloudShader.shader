Shader "Minecraft/CloudShader"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        tags {"Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent"}

        ZWrite Off
        Lighting Off
        Fog
        {
            Mode OFF
        }
        
        Blend SrcAlpha OneMinusSrcAlpha
        
        Pass
        {
            Stencil
            {
                Ref 1
                Comp Greater
                Pass IncrSat
            }
            
            Color [_Color]
        }
    }
}