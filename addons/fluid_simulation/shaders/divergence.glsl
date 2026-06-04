// Divergence — Compute the divergence of the velocity field.
// Uses central differences: div(w) = 0.5 * ((w[i+1,j].x - w[i-1,j].x) + (w[i,j+1].y - w[i,j-1].y)).
// The result is stored in the R channel and used as the right-hand side of the pressure
// Poisson equation in the Jacobi solver. Obstacle neighbors are treated as having zero
// velocity (no-slip condition).
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D input_w;
layout(rgba32f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 2, binding = 0) uniform sampler2D obstacle;

layout(push_constant, std430) uniform Params {
    vec2 size; // Texture dimensions
    float halfRdx; // 0.5 / dx, scaling factor for central differences
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    float obs = texture(obstacle, (vec2(uv) + 0.5) / params.size).a;
    if (obs > 0.75) {
        imageStore(output_data, uv, vec4(0, 0, 0, 1));
        return;
    }
    // Check obstacle status of 4-connected neighbors
    float obsLeft = texture(obstacle, (vec2(uv - ivec2(1, 0)) + 0.5) / params.size).a;
    float obsRight = texture(obstacle, (vec2(uv + ivec2(1, 0)) + 0.5) / params.size).a;
    float obsDown = texture(obstacle, (vec2(uv - ivec2(0, 1)) + 0.5) / params.size).a;
    float obsUp = texture(obstacle, (vec2(uv + ivec2(0, 1)) + 0.5) / params.size).a;
    // Use zero velocity for obstacle neighbors (no-slip)
    vec2 wLeft = (obsLeft > 0.75) ? vec2(0, 0) : imageLoad(input_w, uv - ivec2(1, 0)).xy;
    vec2 wRight = (obsRight > 0.75) ? vec2(0, 0) : imageLoad(input_w, uv + ivec2(1, 0)).xy;
    vec2 wDown = (obsDown > 0.75) ? vec2(0, 0) : imageLoad(input_w, uv - ivec2(0, 1)).xy;
    vec2 wUp = (obsUp > 0.75) ? vec2(0, 0) : imageLoad(input_w, uv + ivec2(0, 1)).xy;
    // Central difference divergence: div = halfRdx * (dw/dx + dw/dy)
    float result = params.halfRdx * ((wRight.x - wLeft.x) + (wUp.y - wDown.y));
    imageStore(output_data, uv, vec4(result, 0, 0, 1));
}
