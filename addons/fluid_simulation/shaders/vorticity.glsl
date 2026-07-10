// Vorticity Confinement — WFS vorticityShader compute version.
// Reads the curl texture and applies vorticity confinement force to restore rotational detail.
// 4-point stencil on curl field: force = 0.5 * vec2(abs(T)-abs(B), abs(R)-abs(L))
// velocity += normalize(force) * curl * C * dt, clamped to [-1000, 1000].
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D input_velocity;
layout(set = 1, binding = 0) uniform sampler2D input_curl;
layout(rgba16f, set = 2, binding = 0) uniform restrict writeonly image2D output_data;

layout(push_constant, std430) uniform Params {
    vec2 size;
    float curl;
    float dt;
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    if (uv.x >= int(params.size.x) || uv.y >= int(params.size.y))
        return;

    vec2 texel = 1.0 / params.size;
    vec2 vUv = (vec2(uv) + 0.5) / params.size;

    float L = texture(input_curl, vUv - vec2(texel.x, 0.0)).x;
    float R = texture(input_curl, vUv + vec2(texel.x, 0.0)).x;
    float T = texture(input_curl, vUv + vec2(0.0, texel.y)).x;
    float B = texture(input_curl, vUv - vec2(0.0, texel.y)).x;
    float C = texture(input_curl, vUv).x;

    vec2 force = 0.5 * vec2(abs(T) - abs(B), abs(R) - abs(L));
    force /= length(force) + 0.0001;
    force *= params.curl * C;
    force.y *= -1.0;

    vec2 velocity = texture(input_velocity, vUv).xy;
    velocity += force * params.dt;
    velocity = clamp(velocity, -1000.0, 1000.0);

    imageStore(output_data, uv, vec4(velocity, 0.0, 1.0));
}
