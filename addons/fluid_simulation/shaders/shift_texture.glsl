// Shift Texture — Offset the entire texture by a 2D displacement.
// Used when the fluid domain follows a moving node (e.g. camera): shifts velocity and
// color textures to maintain spatial consistency as the domain center moves in world space.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D input_texture;
layout(rgba32f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;

layout(push_constant, std430) uniform Params {
	vec2 size; // Texture dimensions
	vec2 offset; // UV offset to shift the texture by
} params;

void main()
{
	ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    vec2 tex_coord = (vec2(uv) + 0.5) / params.size + params.offset;
    vec4 data = texture(input_texture, tex_coord);
    imageStore(output_data, uv, data);
}
