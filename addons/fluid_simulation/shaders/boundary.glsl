// Boundary — Enforce boundary conditions at domain edges and obstacle surfaces.
// At domain edges, reflects the velocity component perpendicular to the boundary by reading
// from an offset neighbor (offset points inward). At interior cells away from boundaries,
// passes velocity through unchanged (scale=1). Obstacle cells are set to zero velocity with alpha=1.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D input_data;
layout(rgba32f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 2, binding = 0) uniform sampler2D obstacle;

layout(push_constant, std430) uniform Params {
    vec2 size; // Texture dimensions
    float scale; // Reflection scale factor (-1 for reflection, 1 for pass-through)
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    float obs = texture(obstacle, (vec2(uv) + 0.5) / params.size).a;
    if (obs > 0.75) {
        imageStore(output_data, uv, vec4(0, 0, 0, 1));
        return;
    }
    ivec2 offset = ivec2(0, 0);
    float scale = params.scale;
    // Determine which domain edge this pixel is on
    if (uv.x < 1)
        // Left edge: read from right neighbor
        offset.x = 1;
    else if (params.size.x - uv.x < 2)
        // Right edge: read from left neighbor
        offset.x = -1;
    else if (uv.y < 1)
        // Bottom edge: read from top neighbor
        offset.y = 1;
    else if (params.size.y - uv.y < 2)
        // Top edge: read from bottom neighbor
        offset.y = -1;
    else
        // Interior cell: pass through unchanged
        scale = 1;
    vec2 result = scale * imageLoad(input_data, uv + offset).xy;
    imageStore(output_data, uv, vec4(result, 0, 1));
}
