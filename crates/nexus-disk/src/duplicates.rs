use crate::DiskNode;
use compact_str::CompactString;
use rayon::prelude::*;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::fs::File;
use std::io::Read;
use std::path::Path;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DuplicateGroup {
    pub hash_hex: CompactString,
    pub file_size: u64,
    pub wasted_bytes: u64,
    pub paths: Vec<CompactString>,
}

/// 2-pass duplicate file finder:
/// - Pass 1: collects all regular files with `size > 0` and groups by exact byte size.
/// - Pass 2: computes SHA-256 over matching-size files in parallel (fast pre-check + full stream hash).
pub fn find_duplicates(root: &DiskNode) -> Vec<DuplicateGroup> {
    let mut all_files = Vec::new();
    collect_files(root, &mut all_files);

    let mut by_size: HashMap<u64, Vec<&DiskNode>> = HashMap::new();
    for file in all_files {
        if file.size > 0 {
            by_size.entry(file.size).or_default().push(file);
        }
    }

    let candidate_paths: Vec<(u64, CompactString)> = by_size
        .into_iter()
        .filter(|(_, list)| list.len() > 1)
        .flat_map(|(sz, list)| list.into_iter().map(move |n| (sz, n.full_path.clone())))
        .collect();

    if candidate_paths.is_empty() {
        return Vec::new();
    }

    let hashed: Vec<(u64, String, CompactString)> = candidate_paths
        .par_iter()
        .filter_map(|(sz, path)| {
            let digest = sha256_file_hex(Path::new(path.as_str())).ok()?;
            Some((*sz, digest, path.clone()))
        })
        .collect();

    let mut by_hash: HashMap<(u64, String), Vec<CompactString>> = HashMap::new();
    for (sz, hash, path) in hashed {
        by_hash.entry((sz, hash)).or_default().push(path);
    }

    let mut groups: Vec<DuplicateGroup> = by_hash
        .into_iter()
        .filter(|(_, paths)| paths.len() > 1)
        .map(|((file_size, hash), mut paths)| {
            paths.sort();
            let wasted_bytes = file_size.saturating_mul((paths.len() - 1) as u64);
            DuplicateGroup {
                hash_hex: CompactString::from(hash),
                file_size,
                wasted_bytes,
                paths,
            }
        })
        .collect();

    groups.sort_by_key(|a| std::cmp::Reverse(a.wasted_bytes));
    groups
}

fn collect_files<'a>(node: &'a DiskNode, out: &mut Vec<&'a DiskNode>) {
    if !node.is_dir {
        out.push(node);
        return;
    }
    for child in &node.children {
        collect_files(child, out);
    }
}

/// Pure-Rust FIPS 180-4 SHA-256 streaming file hash (`#![forbid(unsafe_code)]`).
pub fn sha256_file_hex(path: &Path) -> std::io::Result<String> {
    let mut file = File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buf = [0u8; 65_536];
    loop {
        let n = file.read(&mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
    }
    Ok(hasher.finalize_hex())
}

pub fn sha256_bytes_hex(data: &[u8]) -> String {
    let mut hasher = Sha256::new();
    hasher.update(data);
    hasher.finalize_hex()
}

struct Sha256 {
    state: [u32; 8],
    bit_len: u64,
    buffer: [u8; 64],
    buf_len: usize,
}

impl Sha256 {
    const H_INIT: [u32; 8] = [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab,
        0x5be0cd19,
    ];

    const K: [u32; 64] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4,
        0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe,
        0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f,
        0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
        0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc,
        0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
        0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116,
        0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7,
        0xc67178f2,
    ];

    fn new() -> Self {
        Self {
            state: Self::H_INIT,
            bit_len: 0,
            buffer: [0u8; 64],
            buf_len: 0,
        }
    }

    fn update(&mut self, data: &[u8]) {
        self.bit_len = self
            .bit_len
            .wrapping_add((data.len() as u64).wrapping_mul(8));
        let mut idx = 0usize;
        if self.buf_len > 0 {
            let take = (64 - self.buf_len).min(data.len());
            self.buffer[self.buf_len..self.buf_len + take].copy_from_slice(&data[..take]);
            self.buf_len += take;
            idx += take;
            if self.buf_len == 64 {
                let block = self.buffer;
                self.compress(&block);
                self.buf_len = 0;
            }
        }
        while idx + 64 <= data.len() {
            let mut block = [0u8; 64];
            block.copy_from_slice(&data[idx..idx + 64]);
            self.compress(&block);
            idx += 64;
        }
        if idx < data.len() {
            let rem = data.len() - idx;
            self.buffer[..rem].copy_from_slice(&data[idx..]);
            self.buf_len = rem;
        }
    }

    fn compress(&mut self, block: &[u8; 64]) {
        let mut w = [0u32; 64];
        for i in 0..16 {
            w[i] = u32::from_be_bytes([
                block[i * 4],
                block[i * 4 + 1],
                block[i * 4 + 2],
                block[i * 4 + 3],
            ]);
        }
        for i in 16..64 {
            let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
            let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16]
                .wrapping_add(s0)
                .wrapping_add(w[i - 7])
                .wrapping_add(s1);
        }
        let [mut a, mut b, mut c, mut d, mut e, mut f, mut g, mut h] = self.state;
        for (&k_i, &w_i) in Self::K.iter().zip(w.iter()) {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let ch = (e & f) ^ ((!e) & g);
            let temp1 = h
                .wrapping_add(s1)
                .wrapping_add(ch)
                .wrapping_add(k_i)
                .wrapping_add(w_i);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b) ^ (a & c) ^ (b & c);
            let temp2 = s0.wrapping_add(maj);
            h = g;
            g = f;
            f = e;
            e = d.wrapping_add(temp1);
            d = c;
            c = b;
            b = a;
            a = temp1.wrapping_add(temp2);
        }
        self.state[0] = self.state[0].wrapping_add(a);
        self.state[1] = self.state[1].wrapping_add(b);
        self.state[2] = self.state[2].wrapping_add(c);
        self.state[3] = self.state[3].wrapping_add(d);
        self.state[4] = self.state[4].wrapping_add(e);
        self.state[5] = self.state[5].wrapping_add(f);
        self.state[6] = self.state[6].wrapping_add(g);
        self.state[7] = self.state[7].wrapping_add(h);
    }

    fn finalize_hex(mut self) -> String {
        let bit_len = self.bit_len;
        let zero_len = if self.buf_len < 56 {
            56 - self.buf_len
        } else {
            120 - self.buf_len
        };
        let mut padding = vec![0u8; zero_len];
        padding[0] = 0x80;
        padding.extend_from_slice(&bit_len.to_be_bytes());
        self.update(&padding);

        let mut hex = String::with_capacity(64);
        for word in self.state {
            use std::fmt::Write;
            let _ = write!(hex, "{word:08x}");
        }
        hex
    }
}
