// Splat Batch — Inject multiple Gaussian blobs of color in a single dispatch.
// Reads emitter points from a storage buffer and applies the same color mixing logic as
// splat_color.glsl for each point. This is a performance optimization: instead of N separate
// dispatches, processes all points in one pass.
//
// Supports both additive and subtractive (Beer-Lambert) color mixing.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D input_data;
layout(rgba32f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;

struct EmitterPoint {
    vec2 position; // Pixel coordinates of the emitter point
    vec2 velocity; // Velocity to inject (used by velocity splat, not here)
    vec4 color; // RGBA color to inject
    float color_radius; // Gaussian radius for color splat
    float velocity_radius; // Gaussian radius for velocity splat (not used here)
};

layout(set = 2, binding = 0) buffer PointBuffer {
    EmitterPoint points[];
};

layout(push_constant, std430) uniform Params {
    vec2 size; // Texture dimensions
    float aspect_ratio; // Width/height ratio
    int point_count; // Number of points in the buffer
    float subtractive_mixing; // 1.0 = subtractive (CMY), 0.0 = additive (RGB)
    float density_scale; // Multiplier for density in subtractive mode
} params;

void main() {
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    vec4 data = imageLoad(input_data, uv);
    
    for (int i = 0; i < params.point_count; i++) {
        EmitterPoint p = points[i];
        vec2 diff = vec2(uv) - p.position;
        diff.x *= params.aspect_ratio;
        
        // Gaussian weight for color splat
        float color_splat = exp(-dot(diff, diff) / p.color_radius);
        if (color_splat > 0.001) {
            if (params.subtractive_mixing > 0.5) {
                // Subtractive mixing: Beer-Lambert absorption
                float density = color_splat * p.color.a * params.density_scale;
                vec3 absorption = 1.0 - p.color.rgb;
                vec3 transmission = exp(-density * absorption);
                data.rgb *= transmission;
            } else {
                // Additive mixing: alpha blend
                data.rgb = mix(data.rgb, p.color.rgb, color_splat * p.color.a);
                data.a = max(data.a, color_splat * p.color.a);
            }
        }
    }
    
    imageStore(output_data, uv, data);
}
