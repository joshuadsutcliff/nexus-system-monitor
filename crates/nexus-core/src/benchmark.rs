use std::fs::File;
use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::thread;
use std::time::{Duration, Instant};

/// Platform-safe abstraction for uncached disk benchmark I/O (`F_NOCACHE` + `F_FULLFSYNC` + `statvfs`).
pub trait UncachedDiskIo: Send + Sync {
    fn available_space_bytes(&self, dir: &Path) -> std::io::Result<u64>;
    fn open_uncached_rw(&self, path: &Path) -> std::io::Result<File>;
    fn full_sync(&self, file: &File) -> std::io::Result<()>;
}

/// Default benchmark duration per pass (5.0 seconds).
pub const DEFAULT_BENCHMARK_DURATION_SECS: f64 = 5.0;

/// Number of SplitMix64/Wyhash iterations per `cpu_benchmark_mix` batch.
pub const CPU_MIX_BATCH_ITERATIONS: usize = 250_000;

/// Arithmetic/bitwise operations credited per `250,000`-iteration batch (`2.5e6` ops).
pub const CPU_OPS_PER_BATCH: f64 = 2_500_000.0;

/// Reference baseline (`[cpu_single, cpu_multi, gpu_compute, gpu_graphics, disk_read, disk_write]`)
/// anchored at `NexusScore = 100`.
pub const DEFAULT_NEXUS_BASELINE: [f64; 6] = [1200.0, 8000.0, 2500.0, 1800.0, 3000.0, 2500.0];

/// Results from running the CPU Single-Core and Multi-Core benchmark passes.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CpuBenchmarkResult {
    pub single_core_mops: f64,
    pub multi_core_mops: f64,
    pub elapsed_single_secs: f64,
    pub elapsed_multi_secs: f64,
    pub checksum: u64,
}

/// Core 64-bit SplitMix64/Wyhash integer mixing kernel (`CPUBenchmarkRunner.mix` parity).
#[inline(never)]
pub fn cpu_benchmark_mix(seed: u64, iterations: usize) -> u64 {
    let mut state = seed;
    for i in (1..=iterations as u64).rev() {
        let x = state.wrapping_add(0x9e37_79b9_7f4a_7c15);
        let y = (x ^ (x >> 29)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
        let z = y.rotate_left(17).wrapping_mul(0x94d0_49bb_1331_11eb);
        state = z ^ (z >> 31) ^ i;
    }
    std::hint::black_box(state)
}

/// Runs the Single-Core (`left`) CPU benchmark pass for `duration`, returning `(Mops/s, elapsed_s, checksum)`.
pub fn run_cpu_single(
    duration: Duration,
    cancel: &AtomicBool,
    mut progress_cb: impl FnMut(f32),
) -> (f64, f64, u64) {
    let start = Instant::now();
    let mut last_progress = Instant::now();
    let mut state = 0x1234_5678_9abc_def0_u64;
    let mut total_ops = 0.0_f64;
    let total_secs = duration.as_secs_f64().max(1e-6);

    loop {
        if cancel.load(Ordering::Relaxed) {
            break;
        }
        state = cpu_benchmark_mix(state, CPU_MIX_BATCH_ITERATIONS);
        total_ops += CPU_OPS_PER_BATCH;

        let elapsed = start.elapsed();
        if last_progress.elapsed().as_secs_f64() >= 0.05 {
            progress_cb((elapsed.as_secs_f64() / total_secs).clamp(0.0, 1.0) as f32);
            last_progress = Instant::now();
        }
        if elapsed >= duration {
            break;
        }
    }

    let elapsed_secs = start.elapsed().as_secs_f64().max(1e-6);
    progress_cb(1.0);
    let mops = total_ops / (elapsed_secs * 1_000_000.0);
    (mops, elapsed_secs, state)
}

/// Runs the Multi-Core (`right`) CPU benchmark pass across all logical cores for `duration`,
/// returning `(Mops/s, elapsed_s, checksum)`.
pub fn run_cpu_multi(
    duration: Duration,
    cancel: &AtomicBool,
    mut progress_cb: impl FnMut(f32),
) -> (f64, f64, u64) {
    let workers = thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(1)
        .max(1);
    let total_batches = Arc::new(AtomicU64::new(0));
    let start = Instant::now();
    let total_secs = duration.as_secs_f64().max(1e-6);

    let mut handles = Vec::with_capacity(workers);
    for w in 0..workers {
        let batches_ref = Arc::clone(&total_batches);
        let cancel_flag = cancel.load(Ordering::Relaxed);
        if cancel_flag {
            break;
        }
        let seed = 0x9e37_79b9_7f4a_7c15_u64 ^ ((w as u64 + 1).wrapping_mul(0xbf58_476d_1ce4_e5b9));
        handles.push(thread::spawn(move || {
            let mut state = seed;
            let mut local_batches = 0u64;
            while start.elapsed() < duration {
                state = cpu_benchmark_mix(state, CPU_MIX_BATCH_ITERATIONS);
                local_batches += 1;
            }
            batches_ref.fetch_add(local_batches, Ordering::Relaxed);
            state
        }));
    }

    while start.elapsed() < duration && !cancel.load(Ordering::Relaxed) {
        thread::sleep(Duration::from_millis(50).min(duration));
        progress_cb((start.elapsed().as_secs_f64() / total_secs).clamp(0.0, 1.0) as f32);
    }

    let mut checksum = 0u64;
    for h in handles {
        if let Ok(s) = h.join() {
            checksum ^= s;
        }
    }
    let elapsed_secs = start.elapsed().as_secs_f64().max(1e-6);
    progress_cb(1.0);
    let batches = total_batches.load(Ordering::Relaxed) as f64;
    let mops = (batches * CPU_OPS_PER_BATCH) / (elapsed_secs * 1_000_000.0);
    (mops, elapsed_secs, checksum)
}

/// Runs both Single-Core and Multi-Core CPU benchmark passes sequentially.
pub fn run_cpu_suite(
    duration: Duration,
    cancel: &AtomicBool,
    mut progress_cb: impl FnMut(f32),
) -> CpuBenchmarkResult {
    let (single_core_mops, elapsed_single_secs, c1) =
        run_cpu_single(duration, cancel, |p| progress_cb(p * 0.5));
    let (multi_core_mops, elapsed_multi_secs, c2) =
        run_cpu_multi(duration, cancel, |p| progress_cb(0.5 + p * 0.5));
    CpuBenchmarkResult {
        single_core_mops,
        multi_core_mops,
        elapsed_single_secs,
        elapsed_multi_secs,
        checksum: c1 ^ c2,
    }
}

/// Computes normalized hardware ratios (`results[i] / baseline[i]`) for the 6 core metrics:
/// `[cpu_single, cpu_multi, gpu_compute, gpu_graphics, disk_read, disk_write]`.
pub fn benchmark_ratios(results: &[f64; 6], baseline: &[f64; 6]) -> [f64; 6] {
    let mut out = [0.0; 6];
    for i in 0..6 {
        let r = results[i];
        let b = baseline[i];
        if r.is_normal() && b.is_normal() && r > 0.0 && b > 0.0 {
            let q = r / b;
            if q.is_finite() && q > 0.0 {
                out[i] = q;
            }
        }
    }
    out
}

/// Computes the `100`-anchored composite `NexusScore` (`0..=300`):
/// `trunc(mean(clamp(r_i, 0.0, 3.0)) * 100.0)`.
pub fn nexus_score(ratios: &[f64; 6]) -> i64 {
    let sum: f64 = ratios
        .iter()
        .map(|&r| {
            if r.is_finite() && r > 0.0 {
                r.min(3.0)
            } else {
                0.0
            }
        })
        .sum();
    ((sum / 6.0) * 100.0).trunc() as i64
}

/// Formats any throughput or ratio value into an exact 6-character VFD display string.
pub fn six_character_ratio(val: f64) -> String {
    if !val.is_finite() || val <= 0.0 {
        "0.0000".to_string()
    } else if val >= 999_999.5 {
        ">99999".to_string()
    } else if val < 10.0 {
        format!("{val:.4}")
    } else if val < 100.0 {
        format!("{val:.3}")
    } else if val < 1000.0 {
        format!("{val:.2}")
    } else if val < 10_000.0 {
        format!("{val:.1}")
    } else {
        format!("{val:6.0}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cpu_mix_is_deterministic_and_timed_suite_produces_positive_mops() {
        let a = cpu_benchmark_mix(0x1234, 1_000);
        let b = cpu_benchmark_mix(0x1234, 1_000);
        assert_eq!(a, b);
        assert_ne!(a, 0x1234);

        let cancel = AtomicBool::new(false);
        let res = run_cpu_suite(Duration::from_millis(60), &cancel, |_| {});
        assert!(res.single_core_mops > 0.0);
        assert!(res.multi_core_mops > 0.0);
    }

    #[test]
    fn nexus_score_and_ratios_follow_100_anchor_and_300_cap() {
        assert_eq!(nexus_score(&[1.0; 6]), 100);
        assert_eq!(nexus_score(&[4.5; 6]), 300);
        assert_eq!(nexus_score(&[f64::NAN; 6]), 0);
        assert_eq!(nexus_score(&[-2.0; 6]), 0);

        let r = benchmark_ratios(
            &[2400.0, 8000.0, 0.0, f64::NAN, 3000.0, 5000.0],
            &DEFAULT_NEXUS_BASELINE,
        );
        assert!((r[0] - 2.0).abs() < 1e-9);
        assert!((r[1] - 1.0).abs() < 1e-9);
        assert_eq!(r[2], 0.0);
        assert_eq!(r[3], 0.0);
        assert!((r[4] - 1.0).abs() < 1e-9);
        assert!((r[5] - 2.0).abs() < 1e-9);
    }

    #[test]
    fn six_character_ratio_formats_all_seven_bands() {
        assert_eq!(six_character_ratio(-1.0), "0.0000");
        assert_eq!(six_character_ratio(f64::NAN), "0.0000");
        assert_eq!(six_character_ratio(1.23456), "1.2346");
        assert_eq!(six_character_ratio(12.3456), "12.346");
        assert_eq!(six_character_ratio(123.456), "123.46");
        assert_eq!(six_character_ratio(1234.56), "1234.6");
        assert_eq!(six_character_ratio(12345.0), " 12345");
        assert_eq!(six_character_ratio(1_000_000.0), ">99999");
        for v in [0.0, 1.5, 15.0, 150.0, 1500.0, 15000.0, 1_500_000.0] {
            assert_eq!(six_character_ratio(v).len(), 6);
        }
    }
}
