// Apply Colors — Blend the input color sources into the current color field.
// Uses pure addition (WFS-style): current + source * source.a, alpha = 1.0.
// Obstacle cells preserve their current color unchanged.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D input_colors;
layout(rgba16f, set = 1, binding = 0) uniform image2D input_sources;
layout(rgba16f, set = 2, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 3, binding = 0) uniform sampler2D obstacle;

layout(push_constant, std430) uniform Params {
    vec2 size; // Texture dimensions
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    float obs = texture(obstacle, (vec2(uv) + 0.5) / params.size).a;
    // Obstacle cells: preserve current color
    if (obs > 0.75) {
        imageStore(output_data, uv, imageLoad(input_colors, uv));
        return;
    }
    vec4 current_color = imageLoad(input_colors, uv);
    vec4 source_color = imageLoad(input_sources, uv);

    // Pure addition (WFS-style)
    imageStore(output_data, uv, vec4(current_color.rgb + source_color.rgb * source_color.a, 1.0));
}
