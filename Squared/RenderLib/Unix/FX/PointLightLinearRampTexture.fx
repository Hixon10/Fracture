Texture2D RampTexture;

/* If this is on a register, RampTexture disappears in MGFX.
 * We'll have to assume a default state here.
 * -flibit
 */
sampler RampTextureSampler = sampler_state {
    Texture = (RampTexture);
    MagFilter = Linear;
    MinFilter = Linear;
    MipFilter = Linear;
    AddressU = Clamp;
    AddressV = Clamp;
    AddressW = Clamp;
};

float RampLookup (float value) {
    return tex2D(RampTextureSampler, float2(value, 0)).r;
}

float2 ViewportScale;
float2 ViewportPosition;

float4x4 ProjectionMatrix;
float4x4 ModelViewMatrix;

float4 LightNeutralColor;

float4 ApplyTransform (float2 position2d) {
    float2 localPosition = ((position2d - ViewportPosition) * ViewportScale);
    return mul(mul(float4(localPosition.xy, 0, 1), ModelViewMatrix), ProjectionMatrix);
}

void PointLightVertexShader(
    in float2 position : POSITION0,
    inout float4 color : COLOR0,
    inout float2 lightCenter : TEXCOORD0,
    inout float2 ramp : TEXCOORD1,
    out float2 worldPosition : TEXCOORD2,
    out float4 result : POSITION0
) {
    worldPosition = position;
    result = ApplyTransform(position);
}

void PointLightPixelShaderLinearRampTexture(
    in float2 worldPosition: TEXCOORD2,
    in float2 lightCenter : TEXCOORD0,
    in float2 ramp : TEXCOORD1, // start, end
    in float4 color : COLOR0,
    out float4 result : COLOR0
) {
    float distance = length(worldPosition - lightCenter) - ramp.x;
    float distanceOpacity = 1 - clamp(distance / (ramp.y - ramp.x), 0, 1);

    distanceOpacity = RampLookup(distanceOpacity);

    float opacity = color.a;
    float4 lightColorActual = float4(color.r * opacity, color.g * opacity, color.b * opacity, opacity);
    result = lerp(LightNeutralColor, lightColorActual, distanceOpacity);
}

technique PointLightLinearRampTexture {
    pass P0
    {
        vertexShader = compile vs_2_0 PointLightVertexShader();
        pixelShader = compile ps_2_0 PointLightPixelShaderLinearRampTexture();
    }
}
