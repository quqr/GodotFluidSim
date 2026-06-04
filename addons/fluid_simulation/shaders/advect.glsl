// Advect — Semi-Lagrangian advection for velocity and color fields.
// Traces each pixel backward through the velocity field to find the source position,
// then samples the input field at that position. This is the core transport step of
// the Navier-Stokes solver.
//
// Optional diffusion: when diffusion_strength > 0, applies a Laplacian diffusion term
// at the source position to simulate viscous spreading.
//
// Decay: velocity channels use absolute-value decay; color alpha uses linear decay.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D input_advected;
layout(rgba32f, set = 1, binding = 0) uniform image2D input_velocity;
layout(rgba32f, set = 2, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 3, binding = 0) uniform sampler2D obstacle;

layout(push_constant, std430) uniform Params {
    vec2 size; // Texture dimensions
    float dt; // Time step
    float rdx; // Reciprocal of grid cell size (1/dx)
    float velocity_decay; // Per-step velocity magnitude decay
    float color_decay; // Per-step color alpha decay
    float is_velocity; // 1.0 if advecting velocity, 0.0 if advecting color
    float diffusion_strength; // Diffusion coefficient (0 = no diffusion)
    float _pad1;
    float _pad2;
    float _pad3;
    float _pad4;
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    float obs = texture(obstacle, (vec2(uv) + 0.5) / params.size).a;
    // Obstacle cells: zero velocity or halve color
    if (obs > 0.75) {
        if (params.is_velocity > 0.5) {
            imageStore(output_data, uv, vec4(0, 0, 0, 0));
        } else {
            vec4 current = texture(input_advected, (vec2(uv) + 0.5) / params.size);
            current *= 0.5;
            imageStore(output_data, uv, current);
        }
        return;
    }
    // Semi-Lagrangian: trace backward through velocity field
    vec2 velocity = imageLoad(input_velocity, uv).xy;
    vec2 source_pos = uv - (params.dt * params.rdx * velocity) + 0.5;
    float source_obs = texture(obstacle, source_pos / params.size).a;
    // If source position lands on an obstacle, fall back to current position
    if (source_obs > 0.75) {
        source_pos = vec2(uv) + 0.5;
    }
    vec4 source_data = texture(input_advected, source_pos / params.size);

    // Optional Laplacian diffusion at source position
    if (params.diffusion_strength > 0.0) {
        vec2 texel_size = 1.0 / params.size;
        vec4 left   = texture(input_advected, source_pos / params.size + vec2(-texel_size.x, 0.0));
        vec4 right  = texture(input_advected, source_pos / params.size + vec2( texel_size.x, 0.0));
        vec4 down   = texture(input_advected, source_pos / params.size + vec2(0.0, -texel_size.y));
        vec4 up     = texture(input_advected, source_pos / params.size + vec2(0.0,  texel_size.y));
        // Laplacian = sum of neighbors - 4 * center
        vec4 laplacian = left + right + down + up - 4.0 * source_data;
        source_data += params.diffusion_strength * params.dt * laplacian;
    }

    // Apply decay: absolute-value shrink for velocity, linear for color alpha
    if (params.is_velocity > 0.5) {
        source_data.xy = max(vec2(0.0), abs(source_data.xy) - vec2(params.velocity_decay)) * sign(source_data.xy);
    } else {
        source_data.a = max(0.0, source_data.a - params.color_decay);
    }
    imageStore(output_data, uv, source_data);
}
