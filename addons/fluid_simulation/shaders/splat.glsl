// Splat — WFS splatShader compute version (merged velocity + color).
// Gaussian splat: exp(-dot(p,p) / radius) * color.rgb
// Pure addition: base.rgb + splat, alpha = 1.0
// For velocity splat: color.rg = velocity, color.ba = 0
// For color splat: color.rgb = dye color
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D input_data;
layout(rgba16f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;

layout(push_constant, std430) uniform Params {
    vec4 color;          // offset 0,  16 bytes (rgba)
    vec2 size;           // offset 16, 8 bytes
    vec2 point;          // offset 24, 8 bytes
    float radius;        // offset 32, 4 bytes
    float aspectRatio;   // offset 36, 4 bytes
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    if (uv.x >= int(params.size.x) || uv.y >= int(params.size.y))
        return;

    vec2 p = (vec2(uv) + 0.5) / params.size - params.point;
    p.x *= params.aspectRatio;

    vec3 splat = exp(-dot(p, p) / params.radius) * params.color.rgb;
    vec4 base = imageLoad(input_data, uv);

    imageStore(output_data, uv, vec4(base.rgb + splat, 1.0));
}
