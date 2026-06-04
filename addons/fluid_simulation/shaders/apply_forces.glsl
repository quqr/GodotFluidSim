// Apply Forces — Add external force sources to the velocity field.
// Reads the current velocity and the input forces texture, then stores their sum.
// Obstacle cells are zeroed out to enforce no-slip boundary conditions.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D input_velocity;
layout(rgba32f, set = 1, binding = 0) uniform image2D input_sources;
layout(rgba32f, set = 2, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 3, binding = 0) uniform sampler2D obstacle;

layout(push_constant, std430) uniform Params {
    vec2 size; // Texture dimensions
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    float obs = texture(obstacle, (vec2(uv) + 0.5) / params.size).a;
    if (obs > 0.75) {
        imageStore(output_data, uv, vec4(0, 0, 0, 1));
        return;
    }
    vec4 input_data = imageLoad(input_velocity, uv);
    vec4 source_data = imageLoad(input_sources, uv);
    imageStore(output_data, uv, input_data + source_data);
}
