// Curl — Compute the curl (vorticity) of the velocity field.
// WFS curlShader compute version. 4-point stencil: vorticity = R.y - L.y - T.x + B.x.
// Output: 0.5 * vorticity in R channel.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D input_velocity;
layout(rgba16f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;

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

    float vL = texture(input_velocity, vUv - vec2(texel.x, 0.0)).y;
    float vR = texture(input_velocity, vUv + vec2(texel.x, 0.0)).y;
    float vT = texture(input_velocity, vUv + vec2(0.0, texel.y)).x;
    float vB = texture(input_velocity, vUv - vec2(0.0, texel.y)).x;

    float vorticity = vR - vL - vT + vB;
    imageStore(output_data, uv, vec4(0.5 * vorticity, 0.0, 0.0, 1.0));
}
