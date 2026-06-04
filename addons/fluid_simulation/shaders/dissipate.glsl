// Dissipate — Scale texture data by a dissipation factor each frame.
// Used to gradually reduce velocity or color intensity over time.
// A dissipation of 1.0 means no decay; values < 1.0 cause exponential decay.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D input_data;
layout(rgba32f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;

layout(push_constant, std430) uniform Params {
	vec2 size; // Texture dimensions
	float dissipation; // Per-frame scaling factor (e.g. 0.98 for slow decay)
} params;

void main()
{
	ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    vec4 data = imageLoad(input_data, uv);
    imageStore(output_data, uv, data * params.dissipation);
}
