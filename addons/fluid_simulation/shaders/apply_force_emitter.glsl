// Apply Force Emitters — Apply forces from FluidForceEmitter nodes directly on GPU.
// Reads emitter parameters from a storage buffer and computes force contributions
// per pixel in parallel, completely bypassing the CPU-side per-pixel path.
//
// Supported force patterns: Directional, Point, Vortex, Attractor, Repulsor
// Supported emission shapes: Point, Circle, Rect, Line
// TextureMask shape falls back to CPU path.
#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D velocity_in;
layout(rgba16f, set = 1, binding = 0) uniform restrict writeonly image2D velocity_out;
layout(set = 2, binding = 0) uniform sampler2D obstacle;

struct ForceEmitter {
    vec2 center;           // pixel coords of emitter center
    vec2 force;            // force direction/magnitude
    vec2 shapeSize;        // emission shape dimensions in pixels
    float forceRadius;     // influence radius in pixels
    float falloffExponent; // distance falloff exponent
    float swirlStrength;   // tangential force multiplier for Vortex mode
    int forcePattern;      // ForcePattern enum (0=Directional,1=Point,2=Vortex,3=Attractor,4=Repulsor)
    int emissionShape;     // EmissionShape enum (0=Point,1=Circle,2=Rect,3=Line)
    float _pad;            // padding to 48 bytes
};

layout(std430, set = 3, binding = 0) readonly buffer EmitterBuffer {
    ForceEmitter data[];
} emitterBuffer;

layout(push_constant, std430) uniform Params {
    vec2 size;            // Texture dimensions (width, height)
    int emitterCount;     // Number of active emitters this frame
    int _pad;             // Padding to 16 bytes
} params;

// ---------- Shape tests ----------
// ForceRadius controls the force influence range (circular radius)
// ShapeSize is only used for Rect/Line shape proportions

bool isInPointShape(vec2 offset) {
    return dot(offset, offset) < 4.0;
}

bool isInCircleShape(vec2 offset, float forceRadius) {
    return dot(offset, offset) < forceRadius * forceRadius;
}

bool isInRectShape(vec2 offset, float forceRadius, vec2 shapeSize) {
    // shapeSize acts as proportion multiplier for Rect dimensions
    vec2 halfSize = forceRadius * shapeSize;
    return abs(offset.x) < halfSize.x && abs(offset.y) < halfSize.y;
}

bool isInLineShape(vec2 offset, float forceRadius, float shapeSizeX) {
    // Line: width = forceRadius * shapeSizeX, height = 2 pixels
    return abs(offset.x) < forceRadius * shapeSizeX && abs(offset.y) < 2.0;
}

bool isPixelInShape(ivec2 uv, ForceEmitter emitter) {
    vec2 offset = vec2(uv) + 0.5 - emitter.center;
    
    switch (emitter.emissionShape) {
        case 0: return isInPointShape(offset);
        case 1: return isInCircleShape(offset, emitter.forceRadius);
        case 2: return isInRectShape(offset, emitter.forceRadius, emitter.shapeSize);
        case 3: return isInLineShape(offset, emitter.forceRadius, emitter.shapeSize.x);
        default: return false;
    }
}

// ---------- Force pattern calculations ----------

vec2 calcDirectionalForce(ForceEmitter emitter) {
    return emitter.force;
}

vec2 calcPointForce(vec2 offset, float dist, ForceEmitter emitter) {
    vec2 dir = offset / dist;
    return dir * length(emitter.force);
}

vec2 calcVortexForce(vec2 offset, float dist, ForceEmitter emitter) {
    vec2 dir = offset / dist;
    vec2 tangent = vec2(-dir.y, dir.x);
    return tangent * length(emitter.force) * emitter.swirlStrength + dir * length(emitter.force) * 0.1;
}

vec2 calcAttractorForce(vec2 offset, float dist, ForceEmitter emitter) {
    vec2 dir = offset / dist;
    return -dir * length(emitter.force);
}

vec2 calcRepulsorForce(vec2 offset, float dist, ForceEmitter emitter) {
    vec2 dir = offset / dist;
    return dir * length(emitter.force);
}

// ---------- Falloff ----------

float calcFalloff(vec2 offset, float pixelRadius, float falloffExp) {
    float dist = length(offset);
    float normalizedDist = dist / pixelRadius;
    if (normalizedDist >= 1.0) return 0.0;
    float falloff = 1.0 - normalizedDist;
    return pow(falloff, falloffExp);
}

// ---------- Main ----------

void main() {
    ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
    
    // Clamp to texture bounds
    if (uv.x >= int(params.size.x) || uv.y >= int(params.size.y))
        return;
    
    // Check obstacle — skip obstacle cells
    float obs = texture(obstacle, (vec2(uv) + 0.5) / params.size).a;
    if (obs > 0.75) {
        imageStore(velocity_out, uv, vec4(0, 0, 0, 1));
        return;
    }
    
    vec4 vel = imageLoad(velocity_in, uv);
    vec2 totalForce = vec2(0, 0);
    
    for (int i = 0; i < params.emitterCount; i++) {
        ForceEmitter emitter = emitterBuffer.data[i];
        
        // Skip if pixel is not in the emitter's shape region
        if (!isPixelInShape(uv, emitter))
            continue;
        
        vec2 offset = vec2(uv) + 0.5 - emitter.center;
        float dist = length(offset);
        if (dist < 0.001) dist = 0.001;
        
        // Calculate force based on pattern
        vec2 forceVector;
        switch (emitter.forcePattern) {
            case 0: forceVector = calcDirectionalForce(emitter); break;
            case 1: forceVector = calcPointForce(offset, dist, emitter); break;
            case 2: forceVector = calcVortexForce(offset, dist, emitter); break;
            case 3: forceVector = calcAttractorForce(offset, dist, emitter); break;
            case 4: forceVector = calcRepulsorForce(offset, dist, emitter); break;
            default: forceVector = vec2(0, 0); break;
        }
        
        // Apply falloff
        float falloff = calcFalloff(offset, emitter.forceRadius, emitter.falloffExponent);
        forceVector *= falloff;
        
        // Skip negligible forces
        if (abs(forceVector.x) < 0.0001 && abs(forceVector.y) < 0.0001)
            continue;
        
        totalForce += forceVector;
    }
    
    // Write velocity + force contribution
    imageStore(velocity_out, uv, vel + vec4(totalForce, 0, 0));
}