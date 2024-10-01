﻿
Shader "UI/Sunburst"
{
	Properties
	{
		_RotSpeed("Rotation Speed", Range(-2,2)) = 1
		_SliceWidth("Slice Width", Range(.1,60)) = 20
		_VignetteInner("Vignette Inner", Float) = 1
		_VignetteOuter("Vignette Outer", Float) = 1
		[PerRendererData] _MainTex("Sprite Texture", 2D) = "white" {}
		_ColorInner("Inner Color", Color) = (1,1,1,1)
		_ColorOuter("Outer Color", Color) = (1,1,1,1)
		_Color("Tint", Color) = (1,1,1,1)


		_StencilComp("Stencil Comparison", Float) = 8
		_Stencil("Stencil ID", Float) = 0
		_StencilOp("Stencil Operation", Float) = 0
		_StencilWriteMask("Stencil Write Mask", Float) = 255
		_StencilReadMask("Stencil Read Mask", Float) = 255

		_ColorMask("Color Mask", Float) = 15

		[Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip("Use Alpha Clip", Float) = 0
	}

	SubShader
	{
		Tags
		{
			"Queue" = "Transparent"
			"IgnoreProjector" = "True"
			"RenderType" = "Transparent"
			"PreviewType" = "Plane"
			"CanUseSpriteAtlas" = "True"
		}

		Stencil
		{
			Ref[_Stencil]
			Comp[_StencilComp]
			Pass[_StencilOp]
			ReadMask[_StencilReadMask]
			WriteMask[_StencilWriteMask]
		}

		Cull Off
		Lighting Off
		ZWrite Off
		ZTest[unity_GUIZTestMode]
		Blend SrcAlpha OneMinusSrcAlpha
		ColorMask[_ColorMask]

		Pass
		{
			Name "Default"
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 2.0

			#include "UnityCG.cginc"
			#include "UnityUI.cginc"

			#pragma multi_compile_local _ UNITY_UI_CLIP_RECT
			#pragma multi_compile_local _ UNITY_UI_ALPHACLIP

			struct appdata_t
			{
				float4 vertex   : POSITION;
				float4 color    : COLOR;
				float2 texcoord : TEXCOORD0;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct v2f
			{
				float4 vertex   : SV_POSITION;
				fixed4 color : COLOR;
				float2 texcoord  : TEXCOORD0;
				float4 worldPosition : TEXCOORD1;
				UNITY_VERTEX_OUTPUT_STEREO
			};

			sampler2D _MainTex;
			fixed4 _Color;
			fixed4 _TextureSampleAdd;
			float4 _ClipRect;
			float4 _MainTex_ST;

			v2f vert(appdata_t v)
			{
				v2f OUT;
				UNITY_SETUP_INSTANCE_ID(v);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
				OUT.worldPosition = v.vertex;
				OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
				OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
				OUT.color = v.color * _Color;
				return OUT;
			}

			float unlerp(float from, float to, float value) {
				return (value - from) / (to - from);
			}

			// Adapted from https://www.shadertoy.com/view/lljXzy
			float _RotSpeed;
			float _VignetteInner;
			float _VignetteOuter;
			float _SliceWidth;
			fixed4 _ColorInner;
			fixed4 _ColorOuter;
			static const float PI	= 3.14159265f;
			static const float PI2	= 6.28318530f;
			fixed4 frag(v2f IN) : SV_Target
			{
				fixed2 uv = IN.texcoord;
				float2 toCenter = float2(.5, .5) - uv;
				float 	l = length(toCenter);
				float 	r = cos((ceil(_SliceWidth) * atan2(toCenter.y, toCenter.x)) + _RotSpeed * 360 * _Time);

				//half4 color = (tex2D(_MainTex, uv) + _TextureSampleAdd) * IN.color;
				half4 color = lerp(_ColorInner, _ColorOuter, unlerp(_VignetteInner, _VignetteOuter * .5,l)) * IN.color;
				// Ball
				color *= smoothstep(r, r + 1e-2, l);	
				// Vignette
				color.a *= smoothstep(_VignetteOuter, _VignetteInner, l);
				//color.r = l;
				//color = l;
				//color.a = 1;

				#ifdef UNITY_UI_CLIP_RECT
				//color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
				#endif

				#ifdef UNITY_UI_ALPHACLIP
				//clip(color.a - 0.001);
				#endif

				return color;
			}

			
		ENDCG
		}
	}
}