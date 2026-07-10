// Display — WFS displayShaderSource (SHADING only, no bloom/sunrays).
// Computes normals from the dye field's length gradient and applies diffuse lighting.
// alpha = max(r, max(g, b)) for transparency compositing.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D input_texture;
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

    vec3 c = texture(input_texture, vUv).rgb;

    vec3 lc = texture(input_texture, vUv - vec2(texel.x, 0.0)).rgb;
    vec3 rc = texture(input_texture, vUv + vec2(texel.x, 0.0)).rgb;
    vec3 tc = texture(input_texture, vUv + vec2(0.0, texel.y)).rgb;
    vec3 bc = texture(input_texture, vUv - vec2(0.0, texel.y)).rgb;

    float dx = length(rc) - length(lc);
    float dy = length(tc) - length(bc);

    vec3 n = normalize(vec3(dx, dy, length(texel)));
    vec3 l = vec3(0.0, 0.0, 1.0);

    float diffuse = clamp(dot(n, l) + 0.7, 0.7, 1.0);
    c *= diffuse;

    float a = max(c.r, max(c.g, c.b));
    imageStore(output_data, uv, vec4(c, a));
}
