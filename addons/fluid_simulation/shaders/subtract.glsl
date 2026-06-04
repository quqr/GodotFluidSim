// Subtract — Subtract the pressure gradient from the velocity field.
// This is the projection step that enforces incompressibility:
// u_new = u - grad(p) where grad(p) = 0.5 * ((p[i+1,j] - p[i-1,j]) / dx, (p[i,j+1] - p[i,j-1]) / dx).
// Obstacle neighbors use Neumann boundary (pressure = center pressure), which ensures
// zero pressure gradient normal to obstacle surfaces.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D input_p;
layout(rgba32f, set = 1, binding = 0) uniform image2D input_w;
layout(rgba32f, set = 2, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 3, binding = 0) uniform sampler2D obstacle;

layout(push_constant, std430) uniform Params {
    vec2 size; // Texture dimensions
    float halfRdx; // 0.5 / dx, scaling factor for the gradient
} params;

void main()
{
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    float obs = texture(obstacle, (vec2(uv) + 0.5) / params.size).a;
    if (obs > 0.75) {
        imageStore(output_data, uv, vec4(0, 0, 0, 1));
        return;
    }
    float pCenter = imageLoad(input_p, uv).r;
    float obsLeft = texture(obstacle, (vec2(uv - ivec2(1, 0)) + 0.5) / params.size).a;
    float obsRight = texture(obstacle, (vec2(uv + ivec2(1, 0)) + 0.5) / params.size).a;
    float obsDown = texture(obstacle, (vec2(uv - ivec2(0, 1)) + 0.5) / params.size).a;
    float obsUp = texture(obstacle, (vec2(uv + ivec2(0, 1)) + 0.5) / params.size).a;
    // Neumann boundary: obstacle neighbors use center pressure (zero gradient)
    float pLeft = (obsLeft > 0.75) ? pCenter : imageLoad(input_p, uv - ivec2(1, 0)).r;
    float pRight = (obsRight > 0.75) ? pCenter : imageLoad(input_p, uv + ivec2(1, 0)).r;
    float pDown = (obsDown > 0.75) ? pCenter : imageLoad(input_p, uv - ivec2(0, 1)).r;
    float pUp = (obsUp > 0.75) ? pCenter : imageLoad(input_p, uv + ivec2(0, 1)).r;
    vec4 uNew = imageLoad(input_w, uv);
    // Subtract pressure gradient: u_new = u - halfRdx * grad(p)
    uNew.xy -= params.halfRdx * vec2(pRight - pLeft, pUp - pDown);
    imageStore(output_data, uv, uNew);
}
