// Jacobi — Iterative Jacobi solver for Poisson-type equations.
// Used for both velocity diffusion and pressure solve.
// The update formula: x_new = (xLeft + xRight + xDown + xUp + alpha * b) * rBeta
// where alpha and rBeta depend on the equation being solved:
//   - Diffusion: alpha = dx*dx / (nu * dt), rBeta = 1 / (4 + alpha)
//   - Pressure:  alpha = -dx*dx, rBeta = 1/4
//
// Obstacle neighbors use reflected boundary conditions:
//   - Horizontal neighbors reflect the X component (negate xCenter.x)
//   - Vertical neighbors reflect the Y component (negate xCenter.y)
// This enforces no-penetration at obstacle surfaces.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D input_x;
layout(rgba32f, set = 1, binding = 0) uniform image2D input_b;
layout(rgba32f, set = 2, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 3, binding = 0) uniform sampler2D obstacle;

layout(push_constant, std430) uniform Params {
    vec2 size; // Texture dimensions
    float alpha; // Equation-dependent coefficient
    float rbeta; // Reciprocal of beta (1/beta), scaling factor for the update
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    float obs = texture(obstacle, (vec2(uv) + 0.5) / params.size).a;
    if (obs > 0.75) {
        imageStore(output_data, uv, vec4(0, 0, 0, 1));
        return;
    }
    vec2 xCenter = imageLoad(input_x, uv).xy;
    // Check obstacle status of neighbors
    float obsLeft = texture(obstacle, (vec2(uv - ivec2(1, 0)) + 0.5) / params.size).a;
    float obsRight = texture(obstacle, (vec2(uv + ivec2(1, 0)) + 0.5) / params.size).a;
    float obsDown = texture(obstacle, (vec2(uv - ivec2(0, 1)) + 0.5) / params.size).a;
    float obsUp = texture(obstacle, (vec2(uv + ivec2(0, 1)) + 0.5) / params.size).a;
    // Reflected boundary: horizontal neighbors negate X, vertical negate Y
    vec2 xLeft = (obsLeft > 0.75) ? vec2(-xCenter.x, xCenter.y) : imageLoad(input_x, uv - ivec2(1, 0)).xy;
    vec2 xRight = (obsRight > 0.75) ? vec2(-xCenter.x, xCenter.y) : imageLoad(input_x, uv + ivec2(1, 0)).xy;
    vec2 xDown = (obsDown > 0.75) ? vec2(xCenter.x, -xCenter.y) : imageLoad(input_x, uv - ivec2(0, 1)).xy;
    vec2 xUp = (obsUp > 0.75) ? vec2(xCenter.x, -xCenter.y) : imageLoad(input_x, uv + ivec2(0, 1)).xy;
    vec2 bCenter = imageLoad(input_b, uv).xy;
    // Jacobi update: x_new = (sum of neighbors + alpha * b) * rBeta
    vec2 result = (xLeft + xRight + xDown + xUp + params.alpha * bCenter) * params.rbeta;
    imageStore(output_data, uv, vec4(result, 0.0, 1.0));
}
