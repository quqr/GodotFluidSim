// Splat — Inject a single Gaussian blob of velocity into the field.
// The splat uses a 2D Gaussian: exp(-dot(p,p) / radius) * color, where p is the
// aspect-corrected distance from the splat center. Used for single-point velocity
// injection (e.g. mouse drag).
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
	float color_r; // Red component of injected value
	float color_g; // Green component of injected value
	float color_b; // Blue component of injected value
	float color_a; // Alpha component of injected value
	float _pad1; // Padding
	float _pad2; // Padding
} params;

void main()
{
	ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    vec2 p = vec2(uv) - params.point;
    // Correct for non-square aspect ratio
    p.x *= params.aspect_ratio;
    // Gaussian splat: exp(-|p|^2 / radius) * color
    vec4 splat = exp(-dot(p, p) / params.radius) * vec4(params.color_r, params.color_g, params.color_b, params.color_a);
    vec4 data = imageLoad(input_data, uv);
    // Additive blend
    imageStore(output_data, uv, data + splat);
}
