// Splat Color — Inject a single Gaussian blob of color into the color field.
// Supports two mixing modes:
//   - Additive (subtractive_mixing < 0.5): alpha-blended mix of new color over existing
//   - Subtractive (subtractive_mixing > 0.5): Beer-Lambert absorption model, where density
//     attenuates existing color through exponential transmission. This simulates CMY-like
//     color mixing (e.g. cyan + yellow = green).
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D input_data;
layout(rgba32f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;

layout(push_constant, std430) uniform Params {
	vec2 size; // Texture dimensions
	vec2 point; // Splat center in pixel coordinates
	float radius; // Gaussian radius (controls spread)
	float aspect_ratio; // Width/height ratio for non-square textures
	float color_r; // Red component of injected color
	float color_g; // Green component of injected color
	float color_b; // Blue component of injected color
	float color_a; // Alpha component of injected color
	float subtractive_mixing; // 1.0 = subtractive (CMY), 0.0 = additive (RGB)
	float density_scale; // Multiplier for density in subtractive mode
} params;

void main()
{
	ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    vec2 p = vec2(uv) - params.point;
    p.x *= params.aspect_ratio;
    float splat = exp(-dot(p, p) / params.radius) * params.color_a;
    vec4 data = imageLoad(input_data, uv);
    
    if (params.subtractive_mixing > 0.5) {
        // Subtractive mixing: Beer-Lambert absorption
        float density = splat * params.density_scale;
        vec3 absorption = 1.0 - vec3(params.color_r, params.color_g, params.color_b);
        vec3 transmission = exp(-density * absorption);
        vec3 result = data.rgb * transmission;
        imageStore(output_data, uv, vec4(result, data.a));
    } else {
        // Additive mixing: alpha blend
        vec3 blended = mix(data.rgb, vec3(params.color_r, params.color_g, params.color_b), splat);
        float alpha = max(data.a, splat);
        imageStore(output_data, uv, vec4(blended, alpha));
    }
}
