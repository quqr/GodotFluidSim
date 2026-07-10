// Advect — WFS advectionShader compute version (single shader for velocity and dye).
// Semi-Lagrangian: coord = vUv - dt * velocity * velocityTexel
// result = texture(source, coord) / (1 + dissipation * dt)
// Cross-resolution: velocity sampled at SimResolution, source at sourceSize (velocity or dye).
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D input_velocity;
layout(set = 1, binding = 0) uniform sampler2D input_source;
layout(rgba16f, set = 2, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 3, binding = 0) uniform sampler2D obstacle;

layout(push_constant, std430) uniform Params {
    vec2 velocitySize;
    vec2 sourceSize;
    float dt;
    float dissipation;
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    if (uv.x >= int(params.sourceSize.x) || uv.y >= int(params.sourceSize.y))
        return;

    vec2 vUv = (vec2(uv) + 0.5) / params.sourceSize;
    vec2 velocityTexel = 1.0 / params.velocitySize;

    vec2 velocity = texture(input_velocity, vUv).xy;
    vec2 coord = vUv - params.dt * velocity * velocityTexel;
    vec4 result = texture(input_source, coord);

    float decay = 1.0 + params.dissipation * params.dt;
    result /= decay;

    imageStore(output_data, uv, result);
}
