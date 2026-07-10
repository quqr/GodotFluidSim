// Gaussian Blur — Separable two-pass Gaussian blur.
// Performs horizontal or vertical blur in a single dispatch.
// Direction is controlled via push constant: 0 = horizontal, 1 = vertical.
// Weights are computed dynamically from sigma and radius parameters.
// Adapted from F:\Codes\高斯模糊\tests\addons\fluid_simulation\shaders\gaussian_blur.glsl
// (output format changed from rgba32f to rgba16f to match bloom textures).
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D input_texture;
layout(rgba16f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;

layout(push_constant, std430) uniform Params {
    vec2 size;       // Texture dimensions (width, height) — offset 0, size 8
    float sigma;     // Gaussian standard deviation — offset 8, size 4
    int radius;      // Kernel radius — offset 12, size 4
    int direction;   // 0 = horizontal, 1 = vertical — offset 16, size 4
    float _pad0;     // offset 20, size 4
    float _pad1;     // offset 24, size 4
    float _pad2;     // offset 28, size 4
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    if (uv.x >= int(params.size.x) || uv.y >= int(params.size.y)) return;

    vec2 texel_size = 1.0 / params.size;
    vec2 center_uv = (vec2(uv) + 0.5) / params.size;

    // Determine blur direction
    vec2 dir = params.direction == 0
        ? vec2(texel_size.x, 0.0)
        : vec2(0.0, texel_size.y);

    // Accumulate weighted samples
    vec4 color = vec4(0.0);
    float total_weight = 0.0;

    // Center sample
    float center_weight = 1.0;
    color += texture(input_texture, center_uv) * center_weight;
    total_weight += center_weight;

    // Symmetric loop: sample both sides at offset d
    for (int d = 1; d <= params.radius; d++)
    {
        float w = exp(-0.5 * float(d * d) / (params.sigma * params.sigma));
        vec2 offset = dir * float(d);

        color += texture(input_texture, center_uv + offset) * w;
        color += texture(input_texture, center_uv - offset) * w;
        total_weight += 2.0 * w;
    }

    color /= total_weight;
    imageStore(output_data, uv, color);
}
