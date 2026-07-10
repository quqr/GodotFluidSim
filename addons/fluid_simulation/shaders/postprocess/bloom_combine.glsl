// Bloom Combine — Merges the shaded output with the blurred bloom texture.
// Reads the shaded image (TexIdDisplayOutput or TexIdColor) and the blurred
// bloom texture, applies linearToGamma to bloom, adds them, and writes the
// final composited result to TexIdFinalOutput.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D shaded_texture;
layout(rgba16f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 2, binding = 0) uniform sampler2D bloom_texture;

layout(push_constant, std430) uniform Params {
    vec2 size;       // output resolution (DyeResolution) — offset 0, size 8
    float intensity; // bloom intensity multiplier — offset 8, size 4
    float _pad0;     // offset 12, size 4
} params;

vec3 linearToGamma(vec3 color)
{
    color = max(color, vec3(0));
    return max(1.055 * pow(color, vec3(0.416666667)) - 0.055, vec3(0));
}

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    if (uv.x >= int(params.size.x) || uv.y >= int(params.size.y))
        return;

    vec2 vUv = (vec2(uv) + 0.5) / params.size;

    vec3 c = texture(shaded_texture, vUv).rgb;
    vec3 bloom = texture(bloom_texture, vUv).rgb;
    bloom = linearToGamma(bloom);
    c += bloom * params.intensity;

    float a = max(c.r, max(c.g, c.b));
    imageStore(output_data, uv, vec4(c, a));
}
