// Vorticity Confinement — Re-inject rotational detail lost to numerical diffusion.
// Computes the curl (vorticity) of the velocity field at each cell and its neighbors,
// then applies a force proportional to the gradient of |curl|, directed to restore the
// local rotation. This makes small vortices persist longer and appear more detailed.
//
// Algorithm:
//   1. Compute curl at center and 4 offset positions using 13-point stencil
//   2. Compute gradient of |curl|: eta = normalize(grad(|curl|))
//   3. Apply force: f = vorticity_amount * dt * eta * curl_center
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D input_velocity;
layout(rgba32f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;

layout(push_constant, std430) uniform Params {
	vec2 size; // Texture dimensions
	float dt; // Time step
	float vorticity_amount; // Strength of vorticity confinement force
} params;

void main()
{
	ivec2 uv = ivec2(gl_GlobalInvocationID.xy);

    // Sample 13-point velocity stencil for curl computation
    vec4 velocity = imageLoad(input_velocity, uv);
    vec2 v00 = velocity.xy;
    vec2 vL0 = imageLoad(input_velocity, uv - ivec2(1, 0)).xy;
    vec2 vR0 = imageLoad(input_velocity, uv + ivec2(1, 0)).xy;
    vec2 v0D = imageLoad(input_velocity, uv - ivec2(0, 1)).xy;
    vec2 v0U = imageLoad(input_velocity, uv + ivec2(0, 1)).xy;
    vec2 vLL = imageLoad(input_velocity, uv - ivec2(2, 0)).xy;
    vec2 vRR = imageLoad(input_velocity, uv + ivec2(2, 0)).xy;
    vec2 v0DD = imageLoad(input_velocity, uv - ivec2(0, 2)).xy;
    vec2 v0UU = imageLoad(input_velocity, uv + ivec2(0, 2)).xy;
    vec2 vLD = imageLoad(input_velocity, uv + ivec2(-1, -1)).xy;
    vec2 vLU = imageLoad(input_velocity, uv + ivec2(-1, 1)).xy;
    vec2 vRD = imageLoad(input_velocity, uv + ivec2(1, -1)).xy;
    vec2 vRU = imageLoad(input_velocity, uv + ivec2(1, 1)).xy;

    // Curl (2D scalar): curl = dvx/dy - dvy/dx at center and 4 offset positions
    float curlCenter = (vR0.y - vL0.y) - (v0U.x - v0D.x);
    float curlLeft = (v00.y - vLL.y) - (vLU.x - vLD.x);
    float curlRight = (vRR.y - v00.y) - (vRU.x - vRD.x);
    float curlUp = (vRU.y - vLU.y) - (v0UU.x - v00.x);
    float curlDown = (vRD.y - vLD.y) - (v00.x - v0DD.x);

    // Gradient of |curl|: points from low to high vorticity
    vec2 eta = vec2(abs(curlUp) - abs(curlDown), abs(curlLeft) - abs(curlRight));
    // Small epsilon to avoid division by zero
    eta = normalize(eta + 1e-9);

    // Apply vorticity confinement force
    velocity.xy += params.vorticity_amount * params.dt * eta * curlCenter;

    imageStore(output_data, uv, velocity);
}
