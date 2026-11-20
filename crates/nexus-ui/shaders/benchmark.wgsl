// Nexus System Monitor WGSL Shader (`benchmarkCompute`, `benchmarkVertex`, `benchmarkFragment`)
// GPU Compute & Fragment Benchmark (`Destination.Benchmarks`)

@group(0) @binding(0)
var<storage, read_write> output: array<f32>;

@compute @workgroup_size(64)
fn benchmarkCompute(@builtin(global_invocation_id) global_id: vec3<u32>) {
    let id = global_id.x;
    var x: f32 = f32(id & 1023u) * 0.0009765625 + 0.25;
    for (var i: u32 = 0u; i < 128u; i = i + 1u) {
        x = fma(x, 1.000001, 0.000001);
        x = fma(x, 0.999999, 0.000002);
    }
    if (id < arrayLength(&output)) {
        output[id] = x;
    }
}

struct VertexOut {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn benchmarkVertex(@builtin(vertex_index) id: u32) -> VertexOut {
    var positions = array<vec2<f32>, 3>(
        vec2<f32>(-1.0, -1.0),
        vec2<f32>(3.0, -1.0),
        vec2<f32>(-1.0, 3.0)
    );
    let pos = positions[min(id, 2u)];
    var out: VertexOut;
    out.position = vec4<f32>(pos, 0.0, 1.0);
    out.uv = pos * 0.5 + vec2<f32>(0.5, 0.5);
    return out;
}

@fragment
fn benchmarkFragment(in: VertexOut) -> @location(0) vec4<f32> {
    var x = vec4<f32>(in.uv.x, in.uv.y, in.uv.y, in.uv.x) + vec4<f32>(0.25);
    for (var i: u32 = 0u; i < 64u; i = i + 1u) {
        x = fma(x, vec4<f32>(1.00001), vec4<f32>(0.00001));
        x = fma(x, vec4<f32>(0.99999), vec4<f32>(0.00002));
    }
    return fract(x);
}
