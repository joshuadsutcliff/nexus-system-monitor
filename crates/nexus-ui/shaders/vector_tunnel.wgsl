// Nexus System Monitor WGSL Shader (`tunnelVertex`, `tunnelFragment`)
// Vector Tunnel & HDR/EDR VFD Screensaver (`tunnelVertex` / `tunnelFragment`)
// Uses vec4<f32> packing (48 bytes per Segment) to avoid WGSL vec3 alignment drift.

struct Segment {
    // x = x0, y = y0, z = x1, w = y1
    endpoints: vec4<f32>,
    // r, g, b, a
    color: vec4<f32>,
    // x = width, yzw = reserved
    width_pad: vec4<f32>,
};

struct TunnelUniforms {
    viewport: vec2<f32>,
    dynamic_range: vec2<f32>,
};

@group(0) @binding(0)
var<storage, read> segments: array<Segment>;

@group(0) @binding(1)
var<uniform> uniforms: TunnelUniforms;

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) local: vec2<f32>,
    @location(1) @interpolate(flat) seg_length: f32,
    @location(2) @interpolate(flat) radius: f32,
    @location(3) @interpolate(flat) color: vec4<f32>,
};

@vertex
fn tunnelVertex(
    @builtin(vertex_index) v: u32,
    @builtin(instance_index) i: u32,
) -> VertexOut {
    let s = segments[i];
    let start = s.endpoints.xy;
    let end = s.endpoints.zw;
    let len = length(end - start);
    let direction = select(vec2<f32>(1.0, 0.0), (end - start) / len, len > 0.0001);
    let normal = vec2<f32>(-direction.y, direction.x);
    let radius = max(0.225, s.width_pad.x * 0.5);
    let extent = radius * 6.0 + 1.0;

    var corners = array<vec2<f32>, 6>(
        vec2<f32>(0.0, -1.0),
        vec2<f32>(1.0, -1.0),
        vec2<f32>(0.0, 1.0),
        vec2<f32>(0.0, 1.0),
        vec2<f32>(1.0, -1.0),
        vec2<f32>(1.0, 1.0)
    );
    let c = corners[min(v, 5u)];
    let local = vec2<f32>(mix(-extent, len + extent, c.x), c.y * extent);
    let point = start + direction * local.x + normal * local.y;

    var color = max(s.color.rgb, vec3<f32>(0.0));
    if (uniforms.dynamic_range.y > 0.0) {
        let hi = pow((color + vec3<f32>(0.055)) / 1.055, vec3<f32>(2.4));
        let lo = color / 12.92;
        color = select(hi, lo, color <= vec3<f32>(0.04045));
        color = color * uniforms.dynamic_range.x;
    }

    var out: VertexOut;
    out.position = vec4<f32>(
        point.x / max(uniforms.viewport.x, 1.0) * 2.0 - 1.0,
        1.0 - point.y / max(uniforms.viewport.y, 1.0) * 2.0,
        0.0,
        1.0
    );
    out.local = local;
    out.seg_length = len;
    out.radius = radius;
    out.color = vec4<f32>(color, s.color.a);
    return out;
}

@fragment
fn tunnelFragment(in: VertexOut) -> @location(0) vec4<f32> {
    let clamped_x = clamp(in.local.x, 0.0, in.seg_length);
    let d = length(vec2<f32>(in.local.x - clamped_x, in.local.y));
    let aa = max(0.25, fwidth(d));
    let core = 1.0 - smoothstep(max(0.0, in.radius - aa), in.radius + aa, d);
    let halo = exp(-d * d / (in.radius * in.radius * 9.0)) * 0.28;
    return vec4<f32>(in.color.rgb, in.color.a * min(1.0, core + halo));
}
