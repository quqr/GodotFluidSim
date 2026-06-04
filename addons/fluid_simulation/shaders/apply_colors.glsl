// Apply Colors — Blend the input color sources into the current color field.
// Reads the current color and the input color source texture, then combines them.
// Supports two mixing modes:
//   - Additive (subtractive_mixing < 0.5): alpha-blended mix
//   - Subtractive (subtractive_mixing > 0.5): Beer-Lambert absorption model
// Obstacle cells preserve their current color unchanged.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D input_colors;
layout(rgba32f, set = 1, binding = 0) uniform image2D input_sources;
layout(rgba32f, set = 2, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 3, binding = 0) uniform sampler2D obstacle;

layout(push_constant, std430) uniform Params {
    vec2 size; // Texture dimensions
    float subtractive_mixing; // 1.0 = subtractive (CMY), 0.0 = additive (RGB)
    float density_scale; // Multiplier for density in subtractive mode
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
    
    if (params.subtractive_mixing > 0.5) {
        // Subtractive mixing: Beer-Lambert absorption
        float density = source_color.a * params.density_scale;
        vec3 absorption = 1.0 - source_color.rgb;
        vec3 transmission = exp(-density * absorption);
        vec3 result = current_color.rgb * transmission;
        imageStore(output_data, uv, vec4(result, 1.0));
    } else {
        // Additive mixing: alpha blend
        imageStore(output_data, uv, vec4(mix(current_color.rgb, source_color.rgb, source_color.a), max(current_color.a, source_color.a)));
    }
}
