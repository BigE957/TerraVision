sampler TextureSampler : register(s0);

float Intensity = 1.0;

// Add the vertex color parameter
float4 PixelShaderFunction(float2 coords : TEXCOORD0, float4 vertexColor : COLOR0) : COLOR0
{
    // Sample the texture
    float4 textureColor = tex2D(TextureSampler, coords);
    
    // Multiply texture color by vertex color (this is what SpriteBatch normally does)
    float4 color = textureColor * vertexColor;
    
    // Now apply greyscale to the combined color
    float grey = dot(color.rgb, float3(0.299, 0.587, 0.114));
    float3 greyColor = float3(grey, grey, grey);
    float3 finalColor = lerp(color.rgb, greyColor, Intensity);
    
    return float4(finalColor, color.a);
}

technique Greyscale
{
    pass AutoloadPass
    {
        PixelShader = compile ps_2_0 PixelShaderFunction();
    }
}