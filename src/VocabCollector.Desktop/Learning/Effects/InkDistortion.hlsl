// Stationary horizontal ink diffusion. No vertical refraction or time noise.
// Amount 1 conceals and 0 returns exactly to the original sampling position.
sampler2D Input : register(s0);
float Amount : register(c0);
float2 SurfaceSize : register(c1);
float SmearWidth : register(c2);
float Grain : register(c3);

float4 Main(float2 uv : TEXCOORD) : COLOR
{
    float2 size = max(SurfaceSize, float2(1, 1));
    float row = floor(uv.y * size.y * 1.2);
    float seed = frac(sin(row * 12.9898 + 78.233) * 43758.5453);
    float span = SmearWidth * (0.5 + seed * 0.5) * Amount / size.x;
    float2 p = uv;
    // Equal weights avoid nine separate sample multiplies, keeping PS 2.0's
    // 64-instruction budget while reducing the former seven-tap comb pattern.
    float4 ink = tex2D(Input, p + float2(-span, 0));
    ink += tex2D(Input, p + float2(-span * 0.75, 0));
    ink += tex2D(Input, p + float2(-span * 0.50, 0));
    ink += tex2D(Input, p + float2(-span * 0.25, 0));
    ink += tex2D(Input, p);
    ink += tex2D(Input, p + float2(span * 0.25, 0));
    ink += tex2D(Input, p + float2(span * 0.50, 0));
    ink += tex2D(Input, p + float2(span * 0.75, 0));
    ink += tex2D(Input, p + float2(span, 0));
    return ink * ((1.0 - (1.0 - seed) * Grain * Amount) / 9.0);
}
