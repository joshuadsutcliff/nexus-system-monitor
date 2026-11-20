// Nexus System Monitor WGSL Shader (`diskVertex`, `diskFragment`)
// Disk Space Visualizer: Mode 1 ("Ocean" Metaballs) & Mode 2 ("Blocks" 48x26 Defragmenter Grid)
// Uses explicit vec4<f32> packing (48 bytes per Blob, 80 bytes for Uniforms) to match Rust #[repr(C)] layout identically.

struct Blob {
    // xy = position, z = radius, w = seed
    pos_rad_seed: vec4<f32>,
    // xyz = colour, w = share
    colour_share: vec4<f32>,
    // x = fraction, y = pattern, zw = reserved
    frac_pat_pad: vec4<f32>,
};

struct Uniforms {
    viewport: vec2<f32>,
    time: f32,
    progress: f32,
    completion: f32,
    blob_count: i32,
    mode: i32,
    water_level: f32,
    patterned: i32,
    flat: i32,
    _pad0: vec2<f32>,
    ground: vec4<f32>,
    empty: vec4<f32>,
};

@group(0) @binding(0)
var<storage, read> blobs: array<Blob>;

@group(0) @binding(1)
var<uniform> uniforms: Uniforms;

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn diskVertex(@builtin(vertex_index) vertex_id: u32) -> VertexOut {
    let cx = select(-1.0, 3.0, vertex_id == 2u);
    let cy = select(-1.0, 3.0, vertex_id == 1u);
    let corner = vec2<f32>(cx, cy);
    var out: VertexOut;
    out.position = vec4<f32>(corner, 0.0, 1.0);
    out.uv = corner * 0.5 + vec2<f32>(0.5, 0.5);
    return out;
}

fn patternMask(pattern: i32, within: vec2<f32>) -> f32 {
    let w = within * 4.0;
    if (pattern == 1) {
        return step(0.5, fract(w.x + w.y));
    }
    if (pattern == 2) {
        return step(0.5, fract(w.x - w.y));
    }
    if (pattern == 3) {
        return step(0.5, fract(w.y));
    }
    if (pattern == 4) {
        return step(0.5, fract(w.x));
    }
    if (pattern == 5) {
        return max(step(0.62, fract(w.x)), step(0.62, fract(w.y)));
    }
    if (pattern == 6) {
        let c = fract(w) - vec2<f32>(0.5, 0.5);
        return 1.0 - step(0.055, dot(c, c));
    }
    return 0.0;
}

fn blockColour(uv: vec2<f32>) -> vec3<f32> {
    let columns: i32 = 48;
    let rows: i32 = 26;
    let column = clamp(i32(uv.x * f32(columns)), 0, columns - 1);
    let row = clamp(i32((1.0 - uv.y) * f32(rows)), 0, rows - 1);

    let within = fract(vec2<f32>(uv.x * f32(columns), (1.0 - uv.y) * f32(rows)));
    let gap: f32 = 0.14;
    if (within.x < gap || within.x > 1.0 - gap || within.y < gap || within.y > 1.0 - gap) {
        if (uniforms.flat != 0) {
            return uniforms.ground.rgb;
        }
        return vec3<f32>(0.055, 0.065, 0.10);
    }

    let index = row * columns + column;
    let total = columns * rows;
    let position = f32(index) / f32(total);

    let front = max(uniforms.progress, 0.001);
    if (position > front) {
        return uniforms.empty.rgb;
    }

    let within_filled = position / front;
    var running: f32 = 0.0;
    var owner: i32 = uniforms.blob_count - 1;
    for (var i: i32 = 0; i < uniforms.blob_count; i = i + 1) {
        running = running + blobs[u32(i)].frac_pat_pad.x;
        if (within_filled < running) {
            owner = i;
            break;
        }
    }
    if (owner < 0) {
        return uniforms.empty.rgb;
    }

    var colour = blobs[u32(owner)].colour_share.xyz;
    if (uniforms.patterned != 0) {
        let mask = patternMask(i32(blobs[u32(owner)].frac_pat_pad.y), within);
        colour = colour * (1.0 - 0.55 * mask);
    }
    if (uniforms.flat != 0) {
        return colour;
    }
    let recency = smoothstep(front - 0.06, front, position);
    let lit = 0.6 + 0.55 * recency;
    let flicker = 0.94 + 0.06 * sin(uniforms.time * 6.0 + f32(index));
    return colour * lit * flicker;
}

@fragment
fn diskFragment(in: VertexOut) -> @location(0) vec4<f32> {
    let aspect = uniforms.viewport.x / max(uniforms.viewport.y, 1.0);
    let p = vec2<f32>((in.uv.x - 0.5) * aspect + 0.5, in.uv.y);

    if (uniforms.mode == 2) {
        return vec4<f32>(blockColour(in.uv), 1.0);
    }

    var field: f32 = 0.0;
    var colour = vec3<f32>(0.0, 0.0, 0.0);
    var weight: f32 = 0.0;

    for (var i: i32 = 0; i < uniforms.blob_count; i = i + 1) {
        let blob = blobs[u32(i)];
        let b_pos = blob.pos_rad_seed.xy;
        let b_rad = blob.pos_rad_seed.z;
        let b_seed = blob.pos_rad_seed.w;
        let b_col = blob.colour_share.xyz;

        var centre = vec2<f32>((b_pos.x - 0.5) * aspect + 0.5, b_pos.y);
        centre.x = centre.x + sin(uniforms.time * 0.7 + b_seed) * 0.006;
        centre.y = centre.y + cos(uniforms.time * 0.5 + b_seed * 1.3) * 0.006;

        let d = p - centre;
        let distance_squared = max(dot(d, d), 1e-6);
        let contribution = (b_rad * b_rad) / distance_squared;
        field = field + contribution;

        let sharp = contribution * contribution * contribution;
        colour = colour + b_col * sharp;
        weight = weight + sharp;
    }

    if (weight <= 0.0) {
        return vec4<f32>(uniforms.ground.rgb, 1.0);
    }
    colour = colour / weight;

    let surface = smoothstep(0.75, 1.25, field);
    let rim = (1.0 - smoothstep(1.0, 1.6, field)) * surface;
    let core = smoothstep(1.2, 3.2, field);

    var body = colour * (0.62 + core * 0.55);
    body = body + colour * rim * 0.9;

    let halo = smoothstep(0.06, 0.9, field) * (1.0 - surface);
    let background = uniforms.ground.rgb;
    var rgb = mix(background, body, surface);
    rgb = rgb + colour * halo * 0.28;

    return vec4<f32>(rgb, 1.0);
}
