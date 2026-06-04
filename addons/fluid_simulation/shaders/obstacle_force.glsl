// Obstacle Force — Apply repulsive forces from moving obstacles to the fluid.
// Two mechanisms:
//   1. Velocity-based force: For each fluid cell, searches a 5x5 neighborhood for obstacle
//      cells with encoded velocity (RG channels). If found, applies a force proportional to
//      (obstacle_velocity - fluid_velocity), weighted by inverse distance. This pushes fluid
//      away from moving obstacles.
//   2. Displacement-based force: Checks 4 cells at offset ±3 along each axis. If a cell
//      transitioned from obstacle→fluid (obstacle moved away), pushes fluid outward.
//      If fluid→obstacle (obstacle moved in), pushes fluid inward. This handles the pushing
//      effect when obstacles move into fluid.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba32f, set = 0, binding = 0) uniform image2D input_velocity;
layout(rgba32f, set = 1, binding = 0) uniform restrict writeonly image2D output_data;
layout(set = 2, binding = 0) uniform sampler2D obstacle;
layout(set = 3, binding = 0) uniform sampler2D obstacle_pre;

layout(push_constant, std430) uniform Params {
	vec2 size; // Texture dimensions
	float obstacle_force_strength; // Force magnitude multiplier
	float dt; // Time step
	vec2 fluid_domain_offset; // UV offset between current and previous obstacle textures
} params;

void main()
{
	ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
	vec2 tex_coord = (vec2(uv) + 0.5) / params.size;

	vec4 obsCurData = texture(obstacle, tex_coord);
	float obsCur = obsCurData.a;

	if (obsCur > 0.75) {
		imageStore(output_data, uv, vec4(0.0, 0.0, 0.0, 1.0));
		return;
	}

	vec2 force = vec2(0.0);
	vec2 fluidVelocity = imageLoad(input_velocity, uv).xy;

	// --- Mechanism 1: Velocity-based force from 5x5 neighborhood ---
	for (int dy = -2; dy <= 2; dy++) {
		for (int dx = -2; dx <= 2; dx++) {
			if (dx == 0 && dy == 0) continue;
			ivec2 neighbor = uv + ivec2(dx, dy);
			if (neighbor.x < 0 || neighbor.x >= int(params.size.x) ||
				neighbor.y < 0 || neighbor.y >= int(params.size.y)) {
				continue;
			}
			vec2 neighbor_coord = (vec2(neighbor) + 0.5) / params.size;
			vec4 obsNeighbor = texture(obstacle, neighbor_coord);

			// Obstacle cell with encoded velocity
			if (obsNeighbor.a > 0.1) {
				vec2 objVelocity = obsNeighbor.rg;
				float speedSq = dot(objVelocity, objVelocity);
				if (speedSq > 0.01) {
					// Inverse distance weighting
					float dist = length(vec2(dx, dy));
					float weight = 1.0 / (1.0 + dist);
					force += weight * params.obstacle_force_strength * (objVelocity - fluidVelocity);
				}
			}
		}
	}

	// --- Mechanism 2: Displacement-based force at ±3 offsets ---
	ivec2 offsets3[4] = ivec2[4](
		ivec2(3, 0),
		ivec2(-3, 0),
		ivec2(0, 3),
		ivec2(0, -3)
	);

	for (int i = 0; i < 4; i++) {
		ivec2 neighbor = uv + offsets3[i];
		if (neighbor.x < 0 || neighbor.x >= int(params.size.x) ||
			neighbor.y < 0 || neighbor.y >= int(params.size.y)) {
			continue;
		}
		vec2 neighbor_coord = (vec2(neighbor) + 0.5) / params.size;
		float nCur = texture(obstacle, neighbor_coord).a;
		float nPre = texture(obstacle_pre, neighbor_coord + params.fluid_domain_offset).a;

		// Obstacle moved away: push fluid outward
		if (nPre > 0.1 && nCur < 0.1) {
			force += normalize(vec2(offsets3[i])) * nPre * params.obstacle_force_strength;
		// Obstacle moved in: push fluid inward (away from obstacle)
		} else if (nCur > 0.1 && nPre < 0.1) {
			force -= normalize(vec2(offsets3[i])) * nCur * params.obstacle_force_strength;
		}
	}

	vec4 velocity = imageLoad(input_velocity, uv);
	velocity.xy += force * params.dt;
	imageStore(output_data, uv, velocity);
}
