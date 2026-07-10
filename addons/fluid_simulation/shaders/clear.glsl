// Clear — Multiply a texture by a scalar value (WFS clearShader).
// Used for pressure decay: pressure *= PRESSURE each frame.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D input_texture;
layout(rgba16f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;

layout(push_constant, std430) uniform Params {
    vec2 size;
    float value;
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    if (uv.x >= int(params.size.x) || uv.y >= int(params.size.y))
        return;

    vec4 c = texture(input_texture, (vec2(uv) + 0.5) / params.size);
    imageStore(output_data, uv, params.value * c);
}
