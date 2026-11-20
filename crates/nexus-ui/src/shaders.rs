use std::mem::size_of;
use thiserror::Error;

/// WGSL compute & fragment benchmark shader (`benchmarkCompute`, `benchmarkVertex`, `benchmarkFragment`).
pub const SHADER_BENCHMARK_WGSL: &str = include_str!("../shaders/benchmark.wgsl");

/// WGSL disk visualizer shader (`diskVertex`, `diskFragment`, Ocean metaballs & 48x26 Defrag Blocks).
pub const SHADER_DISK_SPACE_WGSL: &str = include_str!("../shaders/disk_space.wgsl");

/// WGSL VFD vector tunnel shader (`tunnelVertex`, `tunnelFragment`, Vector Tunnel & EDR scaling).
pub const SHADER_VECTOR_TUNNEL_WGSL: &str = include_str!("../shaders/vector_tunnel.wgsl");

/// 48-byte `#[repr(C)]` GPU storage struct matching `struct Blob` in `shaders/disk_space.wgsl`.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct GpuDiskBlob {
    /// `xy = position`, `z = radius`, `w = seed`
    pub pos_rad_seed: [f32; 4],
    /// `xyz = colour`, `w = share`
    pub colour_share: [f32; 4],
    /// `x = fraction`, `y = pattern (1..6)`, `zw = pad`
    pub frac_pat_pad: [f32; 4],
}

const _: () = assert!(size_of::<GpuDiskBlob>() == 48);

impl GpuDiskBlob {
    /// Safely serializes this 48-byte struct into little-endian bytes (`#![forbid(unsafe_code)]`).
    pub fn to_le_bytes(&self) -> [u8; 48] {
        let mut out = [0u8; 48];
        let floats = [
            self.pos_rad_seed[0],
            self.pos_rad_seed[1],
            self.pos_rad_seed[2],
            self.pos_rad_seed[3],
            self.colour_share[0],
            self.colour_share[1],
            self.colour_share[2],
            self.colour_share[3],
            self.frac_pat_pad[0],
            self.frac_pat_pad[1],
            self.frac_pat_pad[2],
            self.frac_pat_pad[3],
        ];
        for (i, f) in floats.into_iter().enumerate() {
            out[i * 4..(i + 1) * 4].copy_from_slice(&f.to_le_bytes());
        }
        out
    }
}

/// 80-byte `#[repr(C)]` GPU uniform struct matching `struct Uniforms` in `shaders/disk_space.wgsl`.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct GpuDiskUniforms {
    pub viewport: [f32; 2],
    pub time: f32,
    pub progress: f32,
    pub completion: f32,
    pub blob_count: i32,
    pub mode: i32,
    pub water_level: f32,
    pub patterned: i32,
    pub flat: i32,
    pub _pad0: [f32; 2],
    pub ground: [f32; 4],
    pub empty: [f32; 4],
}

const _: () = assert!(size_of::<GpuDiskUniforms>() == 80);

impl GpuDiskUniforms {
    /// Safely serializes this 80-byte uniform struct into little-endian bytes (`#![forbid(unsafe_code)]`).
    pub fn to_le_bytes(&self) -> [u8; 80] {
        let mut out = [0u8; 80];
        let words: [[u8; 4]; 20] = [
            self.viewport[0].to_le_bytes(),
            self.viewport[1].to_le_bytes(),
            self.time.to_le_bytes(),
            self.progress.to_le_bytes(),
            self.completion.to_le_bytes(),
            self.blob_count.to_le_bytes(),
            self.mode.to_le_bytes(),
            self.water_level.to_le_bytes(),
            self.patterned.to_le_bytes(),
            self.flat.to_le_bytes(),
            self._pad0[0].to_le_bytes(),
            self._pad0[1].to_le_bytes(),
            self.ground[0].to_le_bytes(),
            self.ground[1].to_le_bytes(),
            self.ground[2].to_le_bytes(),
            self.ground[3].to_le_bytes(),
            self.empty[0].to_le_bytes(),
            self.empty[1].to_le_bytes(),
            self.empty[2].to_le_bytes(),
            self.empty[3].to_le_bytes(),
        ];
        for (i, w) in words.into_iter().enumerate() {
            out[i * 4..(i + 1) * 4].copy_from_slice(&w);
        }
        out
    }
}

/// 48-byte `#[repr(C)]` GPU storage struct matching `struct Segment` in `shaders/vector_tunnel.wgsl`.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct GpuTunnelSegment {
    /// `[x0, y0, x1, y1]`
    pub endpoints: [f32; 4],
    /// `[red, green, blue, alpha]`
    pub color: [f32; 4],
    /// `[width, 0.0, 0.0, 0.0]`
    pub width_pad: [f32; 4],
}

const _: () = assert!(size_of::<GpuTunnelSegment>() == 48);

impl GpuTunnelSegment {
    /// Safely serializes this 48-byte struct into little-endian bytes (`#![forbid(unsafe_code)]`).
    pub fn to_le_bytes(&self) -> [u8; 48] {
        let mut out = [0u8; 48];
        let floats = [
            self.endpoints[0],
            self.endpoints[1],
            self.endpoints[2],
            self.endpoints[3],
            self.color[0],
            self.color[1],
            self.color[2],
            self.color[3],
            self.width_pad[0],
            self.width_pad[1],
            self.width_pad[2],
            self.width_pad[3],
        ];
        for (i, f) in floats.into_iter().enumerate() {
            out[i * 4..(i + 1) * 4].copy_from_slice(&f.to_le_bytes());
        }
        out
    }
}

/// 16-byte `#[repr(C)]` GPU uniform struct matching `struct TunnelUniforms` in `shaders/vector_tunnel.wgsl`.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct GpuTunnelUniforms {
    pub viewport: [f32; 2],
    pub dynamic_range: [f32; 2],
}

const _: () = assert!(size_of::<GpuTunnelUniforms>() == 16);

impl GpuTunnelUniforms {
    /// Safely serializes this 16-byte uniform struct into little-endian bytes (`#![forbid(unsafe_code)]`).
    pub fn to_le_bytes(&self) -> [u8; 16] {
        let mut out = [0u8; 16];
        let floats = [
            self.viewport[0],
            self.viewport[1],
            self.dynamic_range[0],
            self.dynamic_range[1],
        ];
        for (i, f) in floats.into_iter().enumerate() {
            out[i * 4..(i + 1) * 4].copy_from_slice(&f.to_le_bytes());
        }
        out
    }
}

#[derive(Debug, Error)]
pub enum ShaderError {
    #[error("WGSL parse error in {shader}: {details}")]
    Parse {
        shader: &'static str,
        details: String,
    },
    #[error("WGSL validation error in {shader}: {details}")]
    Validation {
        shader: &'static str,
        details: String,
    },
}

/// Parses and validates a WGSL shader string using `wgpu::naga`.
pub fn validate_wgsl_shader(
    name: &'static str,
    source: &str,
) -> Result<wgpu::naga::Module, ShaderError> {
    let module = wgpu::naga::front::wgsl::parse_str(source).map_err(|e| ShaderError::Parse {
        shader: name,
        details: e.emit_to_string(source),
    })?;
    let mut validator = wgpu::naga::valid::Validator::new(
        wgpu::naga::valid::ValidationFlags::all(),
        wgpu::naga::valid::Capabilities::default(),
    );
    validator
        .validate(&module)
        .map_err(|e| ShaderError::Validation {
            shader: name,
            details: format!("{e:?}"),
        })?;
    Ok(module)
}

/// Validates all 3 embedded WGSL shaders (`benchmark.wgsl`, `disk_space.wgsl`, `vector_tunnel.wgsl`).
pub fn validate_all_embedded_shaders() -> Result<(), ShaderError> {
    let _ = validate_wgsl_shader("benchmark.wgsl", SHADER_BENCHMARK_WGSL)?;
    let _ = validate_wgsl_shader("disk_space.wgsl", SHADER_DISK_SPACE_WGSL)?;
    let _ = validate_wgsl_shader("vector_tunnel.wgsl", SHADER_VECTOR_TUNNEL_WGSL)?;
    Ok(())
}
