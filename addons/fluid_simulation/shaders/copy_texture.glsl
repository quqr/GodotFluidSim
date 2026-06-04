// Copy Texture — Simple pixel-by-pixel copy from input to output.
// Used primarily to snapshot the current obstacle texture into the previous-frame buffer.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D input_data;
layout(rgba32f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;

layout(push_constant, std430) uniform Params {
	vec2 size; // Texture dimensions (width, height)
} params;

void main()
{
	ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    vec4 data = imageLoad(input_data, uv);
    imageStore(output_data, uv, data);
}
