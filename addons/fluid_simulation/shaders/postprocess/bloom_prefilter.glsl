// Bloom Prefilter — WFS bloomPrefilterShader (soft-knee bright extraction).
// Reads the raw dye texture (DyeResolution) and extracts bright areas using
// a soft-knee curve, writing to the bloom texture (bloomResolution). The
// linear sampler handles the downsample automatically.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D input_texture;
layout(rgba16f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;

layout(push_constant, std430) uniform Params {
    vec2 size;       // bloom output resolution — offset 0, size 8
    float _pad0;     // offset 8, size 4
    float _pad1;     // offset 12, size 4
    vec3 curve;      // offset 16, size 12 (curve0=threshold-knee, curve1=2*knee, curve2=0.25/knee)
    float threshold; // offset 28, size 4
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    if (uv.x >= int(params.size.x) || uv.y >= int(params.size.y))
        return;

    vec2 vUv = (vec2(uv) + 0.5) / params.size;

    vec3 c = texture(input_texture, vUv).rgb;
    float br = max(c.r, max(c.g, c.b));
    float rq = clamp(br - params.curve.x, 0.0, params.curve.y);
    rq = params.curve.z * rq * rq;
    c *= max(rq, br - params.threshold) / max(br, 0.0001);

    imageStore(output_data, uv, vec4(c, 0.0));
}
