// Pressure — WFS pressureShader compute version (Jacobi iteration).
// Computes: pressure = (L + R + B + T - divergence) * 0.25
// Obstacle cells output zero pressure.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D input_pressure;
layout(set = 1, binding = 0) uniform sampler2D input_divergence;
layout(rgba16f, set = 2, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 3, binding = 0) uniform sampler2D obstacle;

layout(push_constant, std430) uniform Params {
    vec2 size;
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    if (uv.x >= int(params.size.x) || uv.y >= int(params.size.y))
        return;

    float obs = texture(obstacle, (vec2(uv) + 0.5) / params.size).a;
    if (obs > 0.75) {
        imageStore(output_data, uv, vec4(0.0, 0.0, 0.0, 1.0));
        return;
    }

    vec2 texel = 1.0 / params.size;
    vec2 vUv = (vec2(uv) + 0.5) / params.size;

    float L = texture(input_pressure, vUv - vec2(texel.x, 0.0)).x;
    float R = texture(input_pressure, vUv + vec2(texel.x, 0.0)).x;
    float T = texture(input_pressure, vUv + vec2(0.0, texel.y)).x;
    float B = texture(input_pressure, vUv - vec2(0.0, texel.y)).x;
    float divergence = texture(input_divergence, vUv).x;

    float pressure = (L + R + B + T - divergence) * 0.25;
    imageStore(output_data, uv, vec4(pressure, 0.0, 0.0, 1.0));
}
