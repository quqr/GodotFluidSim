// Divergence — WFS divergenceShader compute version with inline boundary + obstacle handling.
// Computes div = 0.5 * (R - L + T - B) where L/R/T/B are velocity components.
// Domain edges use WFS inline boundary (reflect: L = -C.x when vL.x < 0.0).
// Obstacle cells output zero divergence; obstacle neighbors use reflected velocity.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D input_velocity;
layout(rgba16f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 2, binding = 0) uniform sampler2D obstacle;

layout(push_constant, std430) uniform Params {
    vec2 size;
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    if (uv.x >= int(params.size.x) || uv.y >= int(params.size.y))
        return;

    vec2 texel = 1.0 / params.size;
    vec2 vUv = (vec2(uv) + 0.5) / params.size;

    float obs = texture(obstacle, vUv).a;
    if (obs > 0.75) {
        imageStore(output_data, uv, vec4(0.0, 0.0, 0.0, 1.0));
        return;
    }

    vec2 vL = vUv - vec2(texel.x, 0.0);
    vec2 vR = vUv + vec2(texel.x, 0.0);
    vec2 vT = vUv + vec2(0.0, texel.y);
    vec2 vB = vUv - vec2(0.0, texel.y);

    float L = texture(input_velocity, vL).x;
    float R = texture(input_velocity, vR).x;
    float T = texture(input_velocity, vT).y;
    float B = texture(input_velocity, vB).y;
    vec2 C = texture(input_velocity, vUv).xy;

    // WFS inline boundary (domain edges)
    if (vL.x < 0.0) L = -C.x;
    if (vR.x > 1.0) R = -C.x;
    if (vT.y > 1.0) T = -C.y;
    if (vB.y < 0.0) B = -C.y;

    // Obstacle boundary (reflect)
    float obsL = texture(obstacle, vL).a;
    float obsR = texture(obstacle, vR).a;
    float obsT = texture(obstacle, vT).a;
    float obsB = texture(obstacle, vB).a;
    if (obsL > 0.75) L = -C.x;
    if (obsR > 0.75) R = -C.x;
    if (obsT > 0.75) T = -C.y;
    if (obsB > 0.75) B = -C.y;

    float div = 0.5 * (R - L + T - B);
    imageStore(output_data, uv, vec4(div, 0.0, 0.0, 1.0));
}
