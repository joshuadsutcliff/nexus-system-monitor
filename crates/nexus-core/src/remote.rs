use crate::{MetricReading, SystemSnapshot};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
#[cfg(not(windows))]
use std::fs::File;
#[cfg(not(windows))]
use std::io::Read;
use std::net::{IpAddr, Ipv4Addr};
use std::path::Path;
use std::sync::{Arc, Mutex};
use thiserror::Error;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::broadcast;

pub const DEFAULT_REMOTE_PORT: u16 = 51843; // 0xCA83
pub const LEGACY_REMOTE_PORT: u16 = 51842; // 0xCA82
pub const BONJOUR_SERVICE_TYPE: &str = "_nexus-remote._tcp";
pub const MAX_FRAME_BYTES: usize = 1_048_576; // 1 MiB
pub const MAX_HANDSHAKE_FRAME_BYTES: usize = 8_192; // 8 KiB
pub const SEALED_WIRE_MARKER: u8 = 0x02;
pub const SEALED_HEADER_BYTES: usize = 9;

pub const INFO_PAIR_SESSION_KEY: &[u8] = b"nexus pair session key v2";
pub const INFO_PAIR_SAS: &[u8] = b"nexus pair sas v2";
pub const INFO_SESSION_KEY: &[u8] = b"nexus session key v2";
pub const INFO_CLIENT_FRAMES: &[u8] = b"nexus client frames v2";
pub const INFO_DEVICE_PROOF: &[u8] = b"nexus device proof v2";

#[derive(Debug, Error)]
pub enum RemoteError {
    #[error("frame length {len} exceeds limit {limit}")]
    FrameTooLarge { len: usize, limit: usize },
    #[error("empty frame")]
    EmptyFrame,
    #[error("invalid sealed wire header or marker")]
    InvalidSealedHeader,
    #[error("authentication failed (ChaCha20-Poly1305 tag mismatch)")]
    AuthenticationFailed,
    #[error("replayed or out-of-order sequence {got} < {expected}")]
    ReplaySequence { got: u64, expected: u64 },
    #[error("peer IP {0} blocked by RemoteAddressPolicy")]
    AddressBlocked(IpAddr),
    #[error("pairing rate limited for {0}s")]
    RateLimited(u64),
    #[error("invalid pairing handshake: {0}")]
    HandshakeFailed(String),
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
}

/// Fills `dest` with cryptographically secure random bytes (safe Rust, zero unsafe).
pub fn fill_random_bytes(dest: &mut [u8]) -> std::io::Result<()> {
    #[cfg(windows)]
    {
        getrandom::fill(dest).map_err(|e| std::io::Error::other(e.to_string()))
    }
    #[cfg(not(windows))]
    {
        let mut f = File::open("/dev/urandom")?;
        f.read_exact(dest)
    }
}

/// Generates a 6-character unambiguous alphanumeric pairing code via rejection sampling (`b < 0xE0`).
pub struct PairingCode;

impl PairingCode {
    pub const ALPHABET: &'static [u8; 32] = b"23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    pub fn generate() -> std::io::Result<String> {
        let mut out = String::with_capacity(6);
        let mut buf = [0u8; 32];
        while out.len() < 6 {
            fill_random_bytes(&mut buf)?;
            for &b in &buf {
                if b < 0xE0 {
                    out.push(Self::ALPHABET[(b & 0x1F) as usize] as char);
                    if out.len() == 6 {
                        break;
                    }
                }
            }
        }
        Ok(out)
    }
}

/// Private-network & CGNAT address policy.
pub struct RemoteAddressPolicy;

impl RemoteAddressPolicy {
    pub fn is_carrier_grade_nat(ip: Ipv4Addr) -> bool {
        let o = ip.octets();
        o[0] == 100 && (o[1] & 0xC0) == 64
    }

    pub fn is_allowed(ip: IpAddr, allow_cgnat: bool) -> bool {
        match ip {
            IpAddr::V4(v4) => Self::is_allowed_v4(v4, allow_cgnat),
            IpAddr::V6(v6) => {
                if let Some(mapped) = v6.to_ipv4_mapped() {
                    return Self::is_allowed_v4(mapped, allow_cgnat);
                }
                if v6.is_loopback() {
                    return true;
                }
                let seg0 = v6.segments()[0];
                let is_link_local = (seg0 & 0xffc0) == 0xfe80; // fe80::/10
                let is_ula = (seg0 & 0xfe00) == 0xfc00; // fc00::/7
                is_link_local || is_ula
            }
        }
    }

    fn is_allowed_v4(v4: Ipv4Addr, allow_cgnat: bool) -> bool {
        if Self::is_carrier_grade_nat(v4) {
            return allow_cgnat;
        }
        v4.is_loopback() || v4.is_private() || v4.is_link_local()
    }
}

/// Pairing attempt rate limiter (`forgiveness_interval_secs = 60.0`, `max_penalty_secs = 10`).
#[derive(Debug, Clone)]
pub struct RemoteAttemptLimiter {
    pub forgiveness_interval_secs: f64,
    pub max_penalty_secs: u32,
    failures: u32,
    last_failure_at: Option<f64>,
}

impl Default for RemoteAttemptLimiter {
    fn default() -> Self {
        Self {
            forgiveness_interval_secs: 60.0,
            max_penalty_secs: 10,
            failures: 0,
            last_failure_at: None,
        }
    }
}

impl RemoteAttemptLimiter {
    pub fn decayed_failures(&self, now_secs: f64) -> u32 {
        let Some(last) = self.last_failure_at else {
            return self.failures;
        };
        let elapsed = (now_secs - last).max(0.0);
        let interval = self.forgiveness_interval_secs.max(1.0);
        let decay = (elapsed / interval).floor() as u32;
        self.failures.saturating_sub(decay)
    }

    pub fn penalty_secs(&self, now_secs: f64) -> u64 {
        u64::from(self.decayed_failures(now_secs).min(self.max_penalty_secs))
    }

    pub fn record_failure(&mut self, now_secs: f64) {
        let cur = self.decayed_failures(now_secs);
        self.failures = cur.saturating_add(1);
        self.last_failure_at = Some(now_secs);
    }

    pub fn record_success(&mut self) {
        self.failures = 0;
        self.last_failure_at = None;
    }
}

/// Deterministic length-prefixed transcript builder.
pub struct RemoteTranscript;

impl RemoteTranscript {
    pub fn encode(label: &str, fields: &[&[u8]]) -> Vec<u8> {
        let mut out =
            Vec::with_capacity(label.len() + fields.iter().map(|f| 4 + f.len()).sum::<usize>());
        out.extend_from_slice(label.as_bytes());
        for field in fields {
            let len = u32::try_from(field.len()).unwrap_or(u32::MAX);
            out.extend_from_slice(&len.to_be_bytes());
            out.extend_from_slice(field);
        }
        out
    }

    pub fn pair_transcript(
        client_pub: &[u8; 32],
        server_pub: &[u8; 32],
        client_nonce: &[u8],
        server_nonce: &[u8],
    ) -> Vec<u8> {
        Self::encode(
            "nexus-pair-v2",
            &[client_pub, server_pub, client_nonce, server_nonce],
        )
    }

    pub fn session_transcript(
        client_pub: &[u8; 32],
        server_identity_pub: &[u8; 32],
        server_nonce: &[u8],
        device_id: &[u8],
    ) -> Vec<u8> {
        Self::encode(
            "nexus-session-v2",
            &[client_pub, server_identity_pub, server_nonce, device_id],
        )
    }
}

/// 4-byte big-endian length-prefixed framing.
pub fn encode_frame(payload: &[u8], max_bytes: usize) -> Result<Vec<u8>, RemoteError> {
    if payload.is_empty() {
        return Err(RemoteError::EmptyFrame);
    }
    if payload.len() > max_bytes {
        return Err(RemoteError::FrameTooLarge {
            len: payload.len(),
            limit: max_bytes,
        });
    }
    let mut out = Vec::with_capacity(4 + payload.len());
    out.extend_from_slice(&(payload.len() as u32).to_be_bytes());
    out.extend_from_slice(payload);
    Ok(out)
}

/// Drains all complete frames from `buf`, leaving any incomplete suffix in `buf`.
pub fn decode_frames(buf: &mut Vec<u8>, max_bytes: usize) -> Result<Vec<Vec<u8>>, RemoteError> {
    let mut frames = Vec::new();
    let mut cursor = 0usize;
    while buf.len().saturating_sub(cursor) >= 4 {
        let len = u32::from_be_bytes([
            buf[cursor],
            buf[cursor + 1],
            buf[cursor + 2],
            buf[cursor + 3],
        ]) as usize;
        if len == 0 {
            return Err(RemoteError::EmptyFrame);
        }
        if len > max_bytes {
            return Err(RemoteError::FrameTooLarge {
                len,
                limit: max_bytes,
            });
        }
        if buf.len().saturating_sub(cursor + 4) < len {
            break;
        }
        let start = cursor + 4;
        let end = start + len;
        frames.push(buf[start..end].to_vec());
        cursor = end;
    }
    if cursor > 0 {
        buf.drain(..cursor);
    }
    Ok(frames)
}

// ---------------------------------------------------------------------------
// Pure-Rust Cryptography (FIPS 180-4 SHA-256, RFC 2104 HMAC, RFC 5869 HKDF,
// RFC 7748 X25519, RFC 8439 ChaCha20-Poly1305)
// ---------------------------------------------------------------------------

const SHA256_K: [u32; 64] = [
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
];

/// FIPS 180-4 SHA-256.
pub fn sha256(data: &[u8]) -> [u8; 32] {
    let mut h: [u32; 8] = [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab,
        0x5be0cd19,
    ];
    let bit_len = (data.len() as u64).wrapping_mul(8);
    let mut padded = Vec::with_capacity(data.len() + 72);
    padded.extend_from_slice(data);
    padded.push(0x80);
    while (padded.len() % 64) != 56 {
        padded.push(0);
    }
    padded.extend_from_slice(&bit_len.to_be_bytes());

    for block in padded.as_chunks::<64>().0 {
        let mut w = [0u32; 64];
        for (i, chunk) in block.as_chunks::<4>().0.iter().enumerate() {
            w[i] = u32::from_be_bytes(*chunk);
        }
        for i in 16..64 {
            let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
            let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16]
                .wrapping_add(s0)
                .wrapping_add(w[i - 7])
                .wrapping_add(s1);
        }
        let [mut a, mut b, mut c, mut d, mut e, mut f, mut g, mut hh] = h;
        for i in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let ch = (e & f) ^ ((!e) & g);
            let temp1 = hh
                .wrapping_add(s1)
                .wrapping_add(ch)
                .wrapping_add(SHA256_K[i])
                .wrapping_add(w[i]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b) ^ (a & c) ^ (b & c);
            let temp2 = s0.wrapping_add(maj);

            hh = g;
            g = f;
            f = e;
            e = d.wrapping_add(temp1);
            d = c;
            c = b;
            b = a;
            a = temp1.wrapping_add(temp2);
        }
        h[0] = h[0].wrapping_add(a);
        h[1] = h[1].wrapping_add(b);
        h[2] = h[2].wrapping_add(c);
        h[3] = h[3].wrapping_add(d);
        h[4] = h[4].wrapping_add(e);
        h[5] = h[5].wrapping_add(f);
        h[6] = h[6].wrapping_add(g);
        h[7] = h[7].wrapping_add(hh);
    }

    let mut out = [0u8; 32];
    for (i, word) in h.iter().enumerate() {
        out[i * 4..(i + 1) * 4].copy_from_slice(&word.to_be_bytes());
    }
    out
}

/// RFC 2104 HMAC-SHA256.
pub fn hmac_sha256(key: &[u8], data: &[u8]) -> [u8; 32] {
    let mut k0 = [0u8; 64];
    if key.len() > 64 {
        let hk = sha256(key);
        k0[..32].copy_from_slice(&hk);
    } else {
        k0[..key.len()].copy_from_slice(key);
    }
    let mut ipad = [0u8; 64];
    let mut opad = [0u8; 64];
    for i in 0..64 {
        ipad[i] = k0[i] ^ 0x36;
        opad[i] = k0[i] ^ 0x5c;
    }
    let mut inner = Vec::with_capacity(64 + data.len());
    inner.extend_from_slice(&ipad);
    inner.extend_from_slice(data);
    let inner_hash = sha256(&inner);

    let mut outer = Vec::with_capacity(64 + 32);
    outer.extend_from_slice(&opad);
    outer.extend_from_slice(&inner_hash);
    sha256(&outer)
}

/// RFC 5869 HKDF-SHA256 (Extract + Expand).
pub fn hkdf_sha256(ikm: &[u8], salt: &[u8], info: &[u8], out_len: usize) -> Vec<u8> {
    let zero_salt = [0u8; 32];
    let actual_salt = if salt.is_empty() {
        &zero_salt[..]
    } else {
        salt
    };
    let prk = hmac_sha256(actual_salt, ikm);

    let mut okm = Vec::with_capacity(out_len);
    let mut t = Vec::new();
    let mut counter: u8 = 1;
    while okm.len() < out_len {
        let mut msg = Vec::with_capacity(t.len() + info.len() + 1);
        msg.extend_from_slice(&t);
        msg.extend_from_slice(info);
        msg.push(counter);
        t = hmac_sha256(&prk, &msg).to_vec();
        let take = (out_len - okm.len()).min(32);
        okm.extend_from_slice(&t[..take]);
        counter = counter.wrapping_add(1);
    }
    okm
}

pub fn hkdf_sha256_32(ikm: &[u8], salt: &[u8], info: &[u8]) -> [u8; 32] {
    let v = hkdf_sha256(ikm, salt, info, 32);
    let mut out = [0u8; 32];
    out.copy_from_slice(&v[..32]);
    out
}

// --- RFC 7748 X25519 (TweetNaCl 16-limb GF(2^255 - 19) Montgomery ladder) ---

type FieldElem = [i64; 16];

fn fe_zero() -> FieldElem {
    [0; 16]
}

fn fe_one() -> FieldElem {
    let mut o = [0; 16];
    o[0] = 1;
    o
}

fn fe_from_bytes(s: &[u8; 32]) -> FieldElem {
    let mut o = [0i64; 16];
    for i in 0..16 {
        o[i] = i64::from(s[2 * i]) + (i64::from(s[2 * i + 1]) << 8);
    }
    o[15] &= 0x7fff;
    o
}

fn fe_car25519(o: &mut FieldElem) {
    for i in 0..16 {
        o[i] += 1 << 16;
        let c = o[i] >> 16;
        if i < 15 {
            o[i + 1] += c - 1;
        } else {
            o[0] += 38 * (c - 1);
        }
        o[i] -= c << 16;
    }
}

fn fe_sel25519(p: &mut FieldElem, q: &mut FieldElem, b: i64) {
    let c = !(b - 1);
    for i in 0..16 {
        let t = c & (p[i] ^ q[i]);
        p[i] ^= t;
        q[i] ^= t;
    }
}

fn fe_to_bytes(n: &FieldElem) -> [u8; 32] {
    let mut t = *n;
    fe_car25519(&mut t);
    fe_car25519(&mut t);
    fe_car25519(&mut t);
    let mut m = [0i64; 16];
    for _ in 0..2 {
        m[0] = t[0] - 0xffed;
        for i in 1..15 {
            m[i] = t[i] - 0xffff - ((m[i - 1] >> 16) & 1);
            m[i - 1] &= 0xffff;
        }
        m[15] = t[15] - 0x7fff - ((m[14] >> 16) & 1);
        let b = (m[15] >> 16) & 1;
        m[14] &= 0xffff;
        fe_sel25519(&mut t, &mut m, 1 - b);
    }
    let mut o = [0u8; 32];
    for i in 0..16 {
        o[2 * i] = (t[i] & 0xff) as u8;
        o[2 * i + 1] = ((t[i] >> 8) & 0xff) as u8;
    }
    o
}

fn fe_add(a: &FieldElem, b: &FieldElem) -> FieldElem {
    let mut o = [0i64; 16];
    for i in 0..16 {
        o[i] = a[i] + b[i];
    }
    o
}

fn fe_sub(a: &FieldElem, b: &FieldElem) -> FieldElem {
    let mut o = [0i64; 16];
    for i in 0..16 {
        o[i] = a[i] - b[i];
    }
    o
}

fn fe_mul(a: &FieldElem, b: &FieldElem) -> FieldElem {
    let mut t = [0i128; 31];
    for i in 0..16 {
        for j in 0..16 {
            t[i + j] += i128::from(a[i]) * i128::from(b[j]);
        }
    }
    for i in 0..15 {
        t[i] += 38 * t[i + 16];
    }
    let mut o = [0i64; 16];
    for i in 0..16 {
        o[i] = t[i] as i64;
    }
    fe_car25519(&mut o);
    fe_car25519(&mut o);
    o
}

fn fe_sq(a: &FieldElem) -> FieldElem {
    fe_mul(a, a)
}

fn fe_inv(i: &FieldElem) -> FieldElem {
    let mut c = *i;
    for a in (0..=253).rev() {
        c = fe_sq(&c);
        if a != 2 && a != 4 {
            c = fe_mul(&c, i);
        }
    }
    c
}

/// RFC 7748 X25519 scalar multiplication `scalar * u_coord`.
pub fn x25519(scalar: &[u8; 32], u_coord: &[u8; 32]) -> [u8; 32] {
    let mut z = *scalar;
    z[0] &= 248;
    z[31] &= 127;
    z[31] |= 64;

    let x = fe_from_bytes(u_coord);
    let mut a = fe_one();
    let mut b = x;
    let mut c = fe_zero();
    let mut d = fe_one();
    let mut a24 = fe_zero();
    a24[0] = 0xdb41;
    a24[1] = 1; // 121665 = 0x1db41

    for i in (0..=254).rev() {
        let r = i64::from((z[i >> 3] >> (i & 7)) & 1);
        fe_sel25519(&mut a, &mut b, r);
        fe_sel25519(&mut c, &mut d, r);

        let mut e = fe_add(&a, &c);
        a = fe_sub(&a, &c);
        c = fe_add(&b, &d);
        b = fe_sub(&b, &d);
        d = fe_sq(&e);
        let f = fe_sq(&a);
        a = fe_mul(&c, &a);
        c = fe_mul(&b, &e);
        e = fe_add(&a, &c);
        a = fe_sub(&a, &c);
        b = fe_sq(&a);
        c = fe_sub(&d, &f);
        a = fe_mul(&c, &a24);
        a = fe_add(&a, &d);
        c = fe_mul(&c, &a);
        a = fe_mul(&d, &f);
        d = fe_mul(&b, &x);
        b = fe_sq(&e);

        fe_sel25519(&mut a, &mut b, r);
        fe_sel25519(&mut c, &mut d, r);
    }

    let c_inv = fe_inv(&c);
    let out = fe_mul(&a, &c_inv);
    fe_to_bytes(&out)
}

pub fn x25519_base(scalar: &[u8; 32]) -> [u8; 32] {
    let mut base = [0u8; 32];
    base[0] = 9;
    x25519(scalar, &base)
}

pub fn generate_x25519_keypair() -> std::io::Result<([u8; 32], [u8; 32])> {
    let mut priv_key = [0u8; 32];
    fill_random_bytes(&mut priv_key)?;
    let pub_key = x25519_base(&priv_key);
    Ok((priv_key, pub_key))
}

// --- RFC 8439 ChaCha20-Poly1305 AEAD ---

#[inline]
fn quarter_round(s: &mut [u32; 16], a: usize, b: usize, c: usize, d: usize) {
    s[a] = s[a].wrapping_add(s[b]);
    s[d] ^= s[a];
    s[d] = s[d].rotate_left(16);
    s[c] = s[c].wrapping_add(s[d]);
    s[b] ^= s[c];
    s[b] = s[b].rotate_left(12);
    s[a] = s[a].wrapping_add(s[b]);
    s[d] ^= s[a];
    s[d] = s[d].rotate_left(8);
    s[c] = s[c].wrapping_add(s[d]);
    s[b] ^= s[c];
    s[b] = s[b].rotate_left(7);
}

fn chacha20_block(key: &[u8; 32], counter: u32, nonce: &[u8; 12]) -> [u8; 64] {
    let mut state = [0u32; 16];
    state[0] = 0x6170_7865;
    state[1] = 0x3320_646e;
    state[2] = 0x7962_2d32;
    state[3] = 0x6b20_6574;
    for i in 0..8 {
        state[4 + i] =
            u32::from_le_bytes([key[i * 4], key[i * 4 + 1], key[i * 4 + 2], key[i * 4 + 3]]);
    }
    state[12] = counter;
    for i in 0..3 {
        state[13 + i] = u32::from_le_bytes([
            nonce[i * 4],
            nonce[i * 4 + 1],
            nonce[i * 4 + 2],
            nonce[i * 4 + 3],
        ]);
    }

    let mut working = state;
    for _ in 0..10 {
        quarter_round(&mut working, 0, 4, 8, 12);
        quarter_round(&mut working, 1, 5, 9, 13);
        quarter_round(&mut working, 2, 6, 10, 14);
        quarter_round(&mut working, 3, 7, 11, 15);
        quarter_round(&mut working, 0, 5, 10, 15);
        quarter_round(&mut working, 1, 6, 11, 12);
        quarter_round(&mut working, 2, 7, 8, 13);
        quarter_round(&mut working, 3, 4, 9, 14);
    }

    let mut out = [0u8; 64];
    for i in 0..16 {
        let w = working[i].wrapping_add(state[i]);
        out[i * 4..(i + 1) * 4].copy_from_slice(&w.to_le_bytes());
    }
    out
}

fn chacha20_xor(key: &[u8; 32], counter: u32, nonce: &[u8; 12], data: &[u8]) -> Vec<u8> {
    let mut out = vec![0u8; data.len()];
    let mut ctr = counter;
    for (idx, chunk) in data.chunks(64).enumerate() {
        let block = chacha20_block(key, ctr, nonce);
        let base = idx * 64;
        for (j, &b) in chunk.iter().enumerate() {
            out[base + j] = b ^ block[j];
        }
        ctr = ctr.wrapping_add(1);
    }
    out
}

fn poly1305_mac(one_time_key: &[u8; 32], msg: &[u8]) -> [u8; 16] {
    // 26-bit limbs for 130-bit arithmetic mod 2^130 - 5 (Donna-style).
    let mut r_bytes = [0u8; 16];
    r_bytes.copy_from_slice(&one_time_key[..16]);
    r_bytes[3] &= 15;
    r_bytes[7] &= 15;
    r_bytes[11] &= 15;
    r_bytes[15] &= 15;
    r_bytes[4] &= 252;
    r_bytes[8] &= 252;
    r_bytes[12] &= 252;

    let t0 = u32::from_le_bytes([r_bytes[0], r_bytes[1], r_bytes[2], r_bytes[3]]);
    let t1 = u32::from_le_bytes([r_bytes[4], r_bytes[5], r_bytes[6], r_bytes[7]]);
    let t2 = u32::from_le_bytes([r_bytes[8], r_bytes[9], r_bytes[10], r_bytes[11]]);
    let t3 = u32::from_le_bytes([r_bytes[12], r_bytes[13], r_bytes[14], r_bytes[15]]);

    let r0 = u64::from(t0 & 0x03ff_ffff);
    let r1 = u64::from(((t0 >> 26) | (t1 << 6)) & 0x03ff_ffff);
    let r2 = u64::from(((t1 >> 20) | (t2 << 12)) & 0x03ff_ffff);
    let r3 = u64::from(((t2 >> 14) | (t3 << 18)) & 0x03ff_ffff);
    let r4 = u64::from(t3 >> 8);

    let s1 = r1 * 5;
    let s2 = r2 * 5;
    let s3 = r3 * 5;
    let s4 = r4 * 5;

    let mut h0 = 0u64;
    let mut h1 = 0u64;
    let mut h2 = 0u64;
    let mut h3 = 0u64;
    let mut h4 = 0u64;

    for chunk in msg.chunks(16) {
        let mut block = [0u8; 17];
        block[..chunk.len()].copy_from_slice(chunk);
        block[chunk.len()] = 1;

        let u0 = u32::from_le_bytes([block[0], block[1], block[2], block[3]]);
        let u1 = u32::from_le_bytes([block[4], block[5], block[6], block[7]]);
        let u2 = u32::from_le_bytes([block[8], block[9], block[10], block[11]]);
        let u3 = u32::from_le_bytes([block[12], block[13], block[14], block[15]]);
        let u4 = u32::from(block[16]);

        h0 += u64::from(u0 & 0x03ff_ffff);
        h1 += u64::from(((u0 >> 26) | (u1 << 6)) & 0x03ff_ffff);
        h2 += u64::from(((u1 >> 20) | (u2 << 12)) & 0x03ff_ffff);
        h3 += u64::from(((u2 >> 14) | (u3 << 18)) & 0x03ff_ffff);
        h4 += u64::from((u3 >> 8) | (u4 << 24));

        let d0 = u128::from(h0) * u128::from(r0)
            + u128::from(h1) * u128::from(s4)
            + u128::from(h2) * u128::from(s3)
            + u128::from(h3) * u128::from(s2)
            + u128::from(h4) * u128::from(s1);
        let d1 = u128::from(h0) * u128::from(r1)
            + u128::from(h1) * u128::from(r0)
            + u128::from(h2) * u128::from(s4)
            + u128::from(h3) * u128::from(s3)
            + u128::from(h4) * u128::from(s2);
        let d2 = u128::from(h0) * u128::from(r2)
            + u128::from(h1) * u128::from(r1)
            + u128::from(h2) * u128::from(r0)
            + u128::from(h3) * u128::from(s4)
            + u128::from(h4) * u128::from(s3);
        let d3 = u128::from(h0) * u128::from(r3)
            + u128::from(h1) * u128::from(r2)
            + u128::from(h2) * u128::from(r1)
            + u128::from(h3) * u128::from(r0)
            + u128::from(h4) * u128::from(s4);
        let d4 = u128::from(h0) * u128::from(r4)
            + u128::from(h1) * u128::from(r3)
            + u128::from(h2) * u128::from(r2)
            + u128::from(h3) * u128::from(r1)
            + u128::from(h4) * u128::from(r0);

        let c0 = d0 >> 26;
        h0 = (d0 & 0x03ff_ffff) as u64;
        let d1 = d1 + c0;
        let c1 = d1 >> 26;
        h1 = (d1 & 0x03ff_ffff) as u64;
        let d2 = d2 + c1;
        let c2 = d2 >> 26;
        h2 = (d2 & 0x03ff_ffff) as u64;
        let d3 = d3 + c2;
        let c3 = d3 >> 26;
        h3 = (d3 & 0x03ff_ffff) as u64;
        let d4 = d4 + c3;
        let c4 = d4 >> 26;
        h4 = (d4 & 0x03ff_ffff) as u64;
        h0 += (c4 as u64) * 5;
        let c0b = h0 >> 26;
        h0 &= 0x03ff_ffff;
        h1 += c0b;
    }

    let c1 = h1 >> 26;
    h1 &= 0x03ff_ffff;
    h2 += c1;
    let c2 = h2 >> 26;
    h2 &= 0x03ff_ffff;
    h3 += c2;
    let c3 = h3 >> 26;
    h3 &= 0x03ff_ffff;
    h4 += c3;
    let c4 = h4 >> 26;
    h4 &= 0x03ff_ffff;
    h0 += c4 * 5;
    let c0 = h0 >> 26;
    h0 &= 0x03ff_ffff;
    h1 += c0;

    // Compare h >= 2^130 - 5 via g = h + 5 - 2^130
    let mut g0 = h0 + 5;
    let c0 = g0 >> 26;
    g0 &= 0x03ff_ffff;
    let mut g1 = h1 + c0;
    let c1 = g1 >> 26;
    g1 &= 0x03ff_ffff;
    let mut g2 = h2 + c1;
    let c2 = g2 >> 26;
    g2 &= 0x03ff_ffff;
    let mut g3 = h3 + c2;
    let c3 = g3 >> 26;
    g3 &= 0x03ff_ffff;
    let mut g4 = (h4 + c3).wrapping_sub(1 << 26);

    let mask = ((g4 >> 63) as i64).wrapping_sub(1) as u64;
    g0 &= mask;
    g1 &= mask;
    g2 &= mask;
    g3 &= mask;
    g4 &= mask;
    let nmask = !mask;
    h0 = (h0 & nmask) | g0;
    h1 = (h1 & nmask) | g1;
    h2 = (h2 & nmask) | g2;
    h3 = (h3 & nmask) | g3;
    h4 = (h4 & nmask) | g4;

    let f0 = (h0 | (h1 << 26)) as u32;
    let f1 = ((h1 >> 6) | (h2 << 20)) as u32;
    let f2 = ((h2 >> 12) | (h3 << 14)) as u32;
    let f3 = ((h3 >> 18) | (h4 << 8)) as u32;

    let s0 = u32::from_le_bytes([
        one_time_key[16],
        one_time_key[17],
        one_time_key[18],
        one_time_key[19],
    ]);
    let s1 = u32::from_le_bytes([
        one_time_key[20],
        one_time_key[21],
        one_time_key[22],
        one_time_key[23],
    ]);
    let s2 = u32::from_le_bytes([
        one_time_key[24],
        one_time_key[25],
        one_time_key[26],
        one_time_key[27],
    ]);
    let s3 = u32::from_le_bytes([
        one_time_key[28],
        one_time_key[29],
        one_time_key[30],
        one_time_key[31],
    ]);

    let w0 = u64::from(f0) + u64::from(s0);
    let w1 = u64::from(f1) + u64::from(s1) + (w0 >> 32);
    let w2 = u64::from(f2) + u64::from(s2) + (w1 >> 32);
    let w3 = u64::from(f3) + u64::from(s3) + (w2 >> 32);

    let mut tag = [0u8; 16];
    tag[0..4].copy_from_slice(&(w0 as u32).to_le_bytes());
    tag[4..8].copy_from_slice(&(w1 as u32).to_le_bytes());
    tag[8..12].copy_from_slice(&(w2 as u32).to_le_bytes());
    tag[12..16].copy_from_slice(&(w3 as u32).to_le_bytes());
    tag
}

fn poly1305_aead_tag(otk: &[u8; 32], aad: &[u8], ciphertext: &[u8]) -> [u8; 16] {
    let aad_pad = (16 - (aad.len() % 16)) % 16;
    let ct_pad = (16 - (ciphertext.len() % 16)) % 16;
    let mut mac_data = Vec::with_capacity(aad.len() + aad_pad + ciphertext.len() + ct_pad + 16);
    mac_data.extend_from_slice(aad);
    mac_data.resize(mac_data.len() + aad_pad, 0);
    mac_data.extend_from_slice(ciphertext);
    mac_data.resize(mac_data.len() + ct_pad, 0);
    mac_data.extend_from_slice(&(aad.len() as u64).to_le_bytes());
    mac_data.extend_from_slice(&(ciphertext.len() as u64).to_le_bytes());
    poly1305_mac(otk, &mac_data)
}

fn ct_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0u8;
    for (x, y) in a.iter().zip(b.iter()) {
        diff |= x ^ y;
    }
    diff == 0
}

/// RFC 8439 ChaCha20-Poly1305 AEAD encrypt (returns `ciphertext || 16-byte tag`).
pub fn chacha20_poly1305_seal(
    key: &[u8; 32],
    nonce: &[u8; 12],
    aad: &[u8],
    plaintext: &[u8],
) -> Vec<u8> {
    let block0 = chacha20_block(key, 0, nonce);
    let mut otk = [0u8; 32];
    otk.copy_from_slice(&block0[..32]);
    let ciphertext = chacha20_xor(key, 1, nonce, plaintext);
    let tag = poly1305_aead_tag(&otk, aad, &ciphertext);
    let mut out = Vec::with_capacity(ciphertext.len() + 16);
    out.extend_from_slice(&ciphertext);
    out.extend_from_slice(&tag);
    out
}

/// RFC 8439 ChaCha20-Poly1305 AEAD decrypt.
pub fn chacha20_poly1305_open(
    key: &[u8; 32],
    nonce: &[u8; 12],
    aad: &[u8],
    ciphertext_and_tag: &[u8],
) -> Result<Vec<u8>, RemoteError> {
    if ciphertext_and_tag.len() < 16 {
        return Err(RemoteError::AuthenticationFailed);
    }
    let split = ciphertext_and_tag.len() - 16;
    let ciphertext = &ciphertext_and_tag[..split];
    let tag = &ciphertext_and_tag[split..];

    let block0 = chacha20_block(key, 0, nonce);
    let mut otk = [0u8; 32];
    otk.copy_from_slice(&block0[..32]);
    let expected_tag = poly1305_aead_tag(&otk, aad, ciphertext);
    if !ct_eq(tag, &expected_tag) {
        return Err(RemoteError::AuthenticationFailed);
    }
    Ok(chacha20_xor(key, 1, nonce, ciphertext))
}

/// Key schedule and SAS derivation for companion remote protocol.
pub struct RemoteKeySchedule;

impl RemoteKeySchedule {
    pub fn derive_pairing_keys(
        shared_secret: &[u8; 32],
        pair_transcript: &[u8],
    ) -> ([u8; 32], String) {
        let pair_key = hkdf_sha256_32(shared_secret, pair_transcript, INFO_PAIR_SESSION_KEY);
        let sas_seed = hkdf_sha256_32(shared_secret, pair_transcript, INFO_PAIR_SAS);
        let code_num =
            u32::from_be_bytes([sas_seed[0], sas_seed[1], sas_seed[2], sas_seed[3]]) % 1_000_000;
        (pair_key, format!("{code_num:06}"))
    }

    pub fn derive_session_keys(
        shared_secret: &[u8; 32],
        session_transcript: &[u8],
        device_token: &[u8],
    ) -> ([u8; 32], [u8; 32]) {
        let mut salt = Vec::with_capacity(session_transcript.len() + device_token.len());
        salt.extend_from_slice(session_transcript);
        salt.extend_from_slice(device_token);
        let session_key = hkdf_sha256_32(shared_secret, &salt, INFO_SESSION_KEY);
        let client_key = hkdf_sha256_32(&session_key, &[], INFO_CLIENT_FRAMES);
        (session_key, client_key)
    }

    pub fn device_token_proof(device_token: &[u8], session_transcript: &[u8]) -> [u8; 32] {
        let mut msg = Vec::with_capacity(INFO_DEVICE_PROOF.len() + session_transcript.len());
        msg.extend_from_slice(INFO_DEVICE_PROOF);
        msg.extend_from_slice(session_transcript);
        hmac_sha256(device_token, &msg)
    }

    pub fn verify_device_token_proof(
        device_token: &[u8],
        session_transcript: &[u8],
        proof: &[u8; 32],
    ) -> bool {
        let expected = Self::device_token_proof(device_token, session_transcript);
        ct_eq(&expected, proof)
    }
}

/// Sealed Wire Frame: `1B marker (0x02) || 8B BE sequence || ChaCha20-Poly1305(nonce = 0x00000000 || seq_be, aad = header, plaintext)`.
pub struct SealedWireFrame;

impl SealedWireFrame {
    pub fn nonce_bytes(sequence: u64) -> [u8; 12] {
        let mut nonce = [0u8; 12];
        nonce[4..12].copy_from_slice(&sequence.to_be_bytes());
        nonce
    }

    pub fn seal(key: &[u8; 32], sequence: u64, plaintext: &[u8]) -> Vec<u8> {
        let mut header = [0u8; SEALED_HEADER_BYTES];
        header[0] = SEALED_WIRE_MARKER;
        header[1..9].copy_from_slice(&sequence.to_be_bytes());
        let nonce = Self::nonce_bytes(sequence);
        let ct_and_tag = chacha20_poly1305_seal(key, &nonce, &header, plaintext);
        let mut out = Vec::with_capacity(SEALED_HEADER_BYTES + ct_and_tag.len());
        out.extend_from_slice(&header);
        out.extend_from_slice(&ct_and_tag);
        out
    }

    pub fn open(
        key: &[u8; 32],
        expected_min_seq: u64,
        frame: &[u8],
    ) -> Result<(u64, Vec<u8>), RemoteError> {
        if frame.len() < SEALED_HEADER_BYTES + 16 || frame[0] != SEALED_WIRE_MARKER {
            return Err(RemoteError::InvalidSealedHeader);
        }
        let seq = u64::from_be_bytes([
            frame[1], frame[2], frame[3], frame[4], frame[5], frame[6], frame[7], frame[8],
        ]);
        if seq < expected_min_seq {
            return Err(RemoteError::ReplaySequence {
                got: seq,
                expected: expected_min_seq,
            });
        }
        let header = &frame[..SEALED_HEADER_BYTES];
        let nonce = Self::nonce_bytes(seq);
        let pt = chacha20_poly1305_open(key, &nonce, header, &frame[SEALED_HEADER_BYTES..])?;
        Ok((seq, pt))
    }
}

// ---------------------------------------------------------------------------
// Remote Telemetry Envelope, Server State, Bonjour Advertiser & Tokio Listener
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RemoteTelemetrySample {
    pub generation: u64,
    pub sampled_at_ns: u64,
    pub cpu_percent: Option<f64>,
    pub mem_used_percent: Option<f64>,
    pub gpu_percent: Option<f64>,
    pub package_watts: Option<f64>,
    pub max_die_celsius: Option<f64>,
    pub process_count: usize,
}

impl RemoteTelemetrySample {
    pub fn from_snapshot(snap: &SystemSnapshot) -> Self {
        let opt = |m: &crate::Metric<f64>| match m.reading {
            MetricReading::Measured { value, .. } if value.is_finite() => Some(value),
            _ => None,
        };
        Self {
            generation: snap.generation,
            sampled_at_ns: snap.sampled_at_ns,
            cpu_percent: opt(&snap.system.cpu_percent),
            mem_used_percent: opt(&snap.system.mem_used_percent),
            gpu_percent: opt(&snap.system.gpu_utilization_percent),
            package_watts: opt(&snap.hardware_telemetry.soc_power.package_watts),
            max_die_celsius: opt(&snap.hardware_telemetry.thermals.max_die_celsius),
            process_count: snap.processes.len(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "type", rename_all = "camelCase")]
pub enum RemoteHandshakeMessage {
    PairHello {
        #[serde(rename = "clientPublicKeyHex")]
        client_public_key_hex: String,
        #[serde(rename = "clientNonceHex")]
        client_nonce_hex: String,
        #[serde(rename = "pairingCode")]
        pairing_code: String,
        #[serde(rename = "deviceId")]
        device_id: String,
        #[serde(rename = "deviceName")]
        device_name: String,
    },
    PairAccept {
        #[serde(rename = "serverPublicKeyHex")]
        server_public_key_hex: String,
        #[serde(rename = "serverNonceHex")]
        server_nonce_hex: String,
        #[serde(rename = "sasCode")]
        sas_code: String,
        #[serde(rename = "deviceTokenHex")]
        device_token_hex: String,
    },
    Error {
        message: String,
    },
}

#[derive(Debug, Clone)]
pub struct ConnectedRemotePeer {
    pub device_id: String,
    pub device_name: String,
    pub peer_addr: String,
    pub sas_code: String,
}

#[derive(Debug, Clone)]
pub struct PairedDeviceRecord {
    pub device_id: String,
    pub device_name: String,
    pub device_token: [u8; 32],
}

#[derive(Debug, Clone)]
pub struct RemoteServerConfig {
    pub port: u16,
    pub allow_cgnat: bool,
    pub advertise_bonjour: bool,
}

impl Default for RemoteServerConfig {
    fn default() -> Self {
        Self {
            port: DEFAULT_REMOTE_PORT,
            allow_cgnat: false,
            advertise_bonjour: true,
        }
    }
}

#[derive(Debug)]
pub struct RemoteServerStatus {
    pub running: bool,
    pub bound_port: Option<u16>,
    pub allow_cgnat: bool,
    pub pairing_code: String,
    pub last_sas_code: Option<String>,
    pub status_message: String,
    pub connected_peers: Vec<ConnectedRemotePeer>,
    pub paired_devices: HashMap<String, PairedDeviceRecord>,
    pub limiter: RemoteAttemptLimiter,
}

impl RemoteServerStatus {
    pub fn new(allow_cgnat: bool) -> Self {
        Self {
            running: false,
            bound_port: None,
            allow_cgnat,
            pairing_code: PairingCode::generate().unwrap_or_else(|_| "NEXUS2".to_string()),
            last_sas_code: None,
            status_message: "Stopped".to_string(),
            connected_peers: Vec::new(),
            paired_devices: HashMap::new(),
            limiter: RemoteAttemptLimiter::default(),
        }
    }
}

pub struct RemoteServerHandle {
    pub status: Arc<Mutex<RemoteServerStatus>>,
    tx: broadcast::Sender<Arc<RemoteTelemetrySample>>,
    shutdown_tx: Option<tokio::sync::oneshot::Sender<()>>,
    bonjour_child: Option<std::process::Child>,
}

impl Drop for RemoteServerHandle {
    fn drop(&mut self) {
        self.stop();
    }
}

impl RemoteServerHandle {
    pub fn start(
        rt: &tokio::runtime::Handle,
        config: RemoteServerConfig,
    ) -> Result<Self, RemoteError> {
        let mut status_obj = RemoteServerStatus::new(config.allow_cgnat);
        let pairing_code = status_obj.pairing_code.clone();
        let (tx, _) = broadcast::channel::<Arc<RemoteTelemetrySample>>(64);
        let (shutdown_tx, mut shutdown_rx) = tokio::sync::oneshot::channel::<()>();

        let std_listener = std::net::TcpListener::bind(("0.0.0.0", config.port))
            .or_else(|_| std::net::TcpListener::bind(("0.0.0.0", LEGACY_REMOTE_PORT)))?;
        std_listener.set_nonblocking(true)?;
        let bound_port = std_listener.local_addr()?.port();

        let bonjour_child = if config.advertise_bonjour && Path::new("/usr/bin/dns-sd").exists() {
            std::process::Command::new("/usr/bin/dns-sd")
                .args([
                    "-R",
                    "Nexus System Monitor",
                    BONJOUR_SERVICE_TYPE,
                    "local",
                    &bound_port.to_string(),
                ])
                .stdout(std::process::Stdio::null())
                .stderr(std::process::Stdio::null())
                .spawn()
                .ok()
        } else {
            None
        };

        status_obj.running = true;
        status_obj.bound_port = Some(bound_port);
        status_obj.status_message = format!(
            "Listening on 0.0.0.0:{bound_port} ({BONJOUR_SERVICE_TYPE}, code {pairing_code})"
        );

        let status = Arc::new(Mutex::new(status_obj));
        let status_loop = Arc::clone(&status);
        let tx_loop = tx.clone();

        let _ = rt;
        std::thread::spawn(move || {
            let Ok(server_rt) = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
            else {
                return;
            };
            server_rt.block_on(async move {
                let Ok(listener) = TcpListener::from_std(std_listener) else {
                    return;
                };
                loop {
                    tokio::select! {
                        _ = &mut shutdown_rx => {
                            break;
                        }
                        accepted = listener.accept() => {
                            let Ok((stream, addr)) = accepted else {
                                continue;
                            };
                            let allow_cgnat = status_loop
                                .lock()
                                .unwrap_or_else(|e| e.into_inner())
                                .allow_cgnat;
                            if !RemoteAddressPolicy::is_allowed(addr.ip(), allow_cgnat) {
                                drop(stream);
                                continue;
                            }
                            let st = Arc::clone(&status_loop);
                            let rx = tx_loop.subscribe();
                            tokio::spawn(async move {
                                let _ = handle_remote_client(stream, addr.to_string(), st, rx).await;
                            });
                        }
                    }
                }
                if let Ok(mut guard) = status_loop.lock() {
                    guard.running = false;
                    guard.bound_port = None;
                    guard.connected_peers.clear();
                    guard.status_message = "Stopped".to_string();
                }
            });
        });

        Ok(Self {
            status,
            tx,
            shutdown_tx: Some(shutdown_tx),
            bonjour_child,
        })
    }

    pub fn broadcast_snapshot(&self, snap: &SystemSnapshot) {
        if self.tx.receiver_count() > 0 {
            let _ = self
                .tx
                .send(Arc::new(RemoteTelemetrySample::from_snapshot(snap)));
        }
    }

    pub fn set_bonjour_advertising(&mut self, enable: bool) -> Result<bool, RemoteError> {
        if !enable {
            if let Some(mut child) = self.bonjour_child.take() {
                let _ = child.kill();
                let _ = child.wait();
            }
            return Ok(false);
        }
        if self.bonjour_child.is_some() {
            return Ok(true);
        }
        let bound_port = self.status.lock().ok().and_then(|s| s.bound_port);
        let Some(port) = bound_port else {
            return Ok(false);
        };
        if !Path::new("/usr/bin/dns-sd").exists() {
            return Ok(false);
        }
        let child = std::process::Command::new("/usr/bin/dns-sd")
            .args([
                "-R",
                "Nexus System Monitor",
                BONJOUR_SERVICE_TYPE,
                "local",
                &port.to_string(),
            ])
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .spawn()?;
        self.bonjour_child = Some(child);
        Ok(true)
    }

    pub fn stop(&mut self) {
        if let Some(tx) = self.shutdown_tx.take() {
            let _ = tx.send(());
        }
        if let Some(mut child) = self.bonjour_child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
        if let Ok(mut guard) = self.status.lock() {
            guard.running = false;
            guard.bound_port = None;
            guard.connected_peers.clear();
            guard.status_message = "Stopped".to_string();
        }
    }
}

fn unix_secs_f64() -> f64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs_f64())
        .unwrap_or(0.0)
}

pub fn hex_encode(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut s = String::with_capacity(bytes.len() * 2);
    for &b in bytes {
        s.push(HEX[(b >> 4) as usize] as char);
        s.push(HEX[(b & 0x0f) as usize] as char);
    }
    s
}

pub fn hex_decode(s: &str) -> Option<Vec<u8>> {
    let bytes = s.as_bytes();
    if !bytes.len().is_multiple_of(2) {
        return None;
    }
    let mut out = Vec::with_capacity(bytes.len() / 2);
    for pair in bytes.as_chunks::<2>().0 {
        let hi = (pair[0] as char).to_digit(16)?;
        let lo = (pair[1] as char).to_digit(16)?;
        out.push(((hi << 4) | lo) as u8);
    }
    Some(out)
}

async fn handle_remote_client(
    mut stream: TcpStream,
    peer_addr: String,
    status: Arc<Mutex<RemoteServerStatus>>,
    mut rx: broadcast::Receiver<Arc<RemoteTelemetrySample>>,
) -> Result<(), RemoteError> {
    let mut read_buf = Vec::with_capacity(4096);
    let mut tmp = [0u8; 4096];

    let hello_bytes = loop {
        let n = stream.read(&mut tmp).await?;
        if n == 0 {
            return Err(RemoteError::HandshakeFailed("EOF before PairHello".into()));
        }
        read_buf.extend_from_slice(&tmp[..n]);
        let frames = decode_frames(&mut read_buf, MAX_HANDSHAKE_FRAME_BYTES)?;
        if let Some(first) = frames.into_iter().next() {
            break first;
        }
    };

    let msg: RemoteHandshakeMessage = serde_json::from_slice(&hello_bytes)
        .map_err(|e| RemoteError::HandshakeFailed(e.to_string()))?;

    let RemoteHandshakeMessage::PairHello {
        client_public_key_hex,
        client_nonce_hex,
        pairing_code,
        device_id,
        device_name,
    } = msg
    else {
        return Err(RemoteError::HandshakeFailed("expected PairHello".into()));
    };

    let penalty = {
        let guard = status.lock().unwrap_or_else(|e| e.into_inner());
        guard.limiter.penalty_secs(unix_secs_f64())
    };
    if penalty > 0 {
        tokio::time::sleep(std::time::Duration::from_secs(penalty)).await;
    }

    let expected_code = status
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .pairing_code
        .clone();
    if !ct_eq(pairing_code.trim().as_bytes(), expected_code.as_bytes()) {
        status
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .limiter
            .record_failure(unix_secs_f64());
        let err_payload = serde_json::to_vec(&RemoteHandshakeMessage::Error {
            message: "Invalid pairing code".to_string(),
        })
        .unwrap_or_default();
        let _ = stream
            .write_all(&encode_frame(&err_payload, MAX_HANDSHAKE_FRAME_BYTES)?)
            .await;
        return Err(RemoteError::HandshakeFailed("invalid pairing code".into()));
    }

    let client_pub_vec = hex_decode(&client_public_key_hex)
        .filter(|v| v.len() == 32)
        .ok_or_else(|| RemoteError::HandshakeFailed("bad clientPublicKeyHex".into()))?;
    let mut client_pub = [0u8; 32];
    client_pub.copy_from_slice(&client_pub_vec);

    let client_nonce = hex_decode(&client_nonce_hex)
        .ok_or_else(|| RemoteError::HandshakeFailed("bad clientNonceHex".into()))?;

    let (server_priv, server_pub) = generate_x25519_keypair()?;
    let mut server_nonce = [0u8; 16];
    fill_random_bytes(&mut server_nonce)?;
    let mut device_token = [0u8; 32];
    fill_random_bytes(&mut device_token)?;

    let shared = x25519(&server_priv, &client_pub);
    let pair_tx =
        RemoteTranscript::pair_transcript(&client_pub, &server_pub, &client_nonce, &server_nonce);
    let (_pair_key, sas_code) = RemoteKeySchedule::derive_pairing_keys(&shared, &pair_tx);

    let session_tx = RemoteTranscript::session_transcript(
        &client_pub,
        &server_pub,
        &server_nonce,
        device_id.as_bytes(),
    );
    let (session_key, _client_key) =
        RemoteKeySchedule::derive_session_keys(&shared, &session_tx, &device_token);

    let accept_payload = serde_json::to_vec(&RemoteHandshakeMessage::PairAccept {
        server_public_key_hex: hex_encode(&server_pub),
        server_nonce_hex: hex_encode(&server_nonce),
        sas_code: sas_code.clone(),
        device_token_hex: hex_encode(&device_token),
    })
    .map_err(|e| RemoteError::HandshakeFailed(e.to_string()))?;

    stream
        .write_all(&encode_frame(&accept_payload, MAX_HANDSHAKE_FRAME_BYTES)?)
        .await?;

    {
        let mut guard = status.lock().unwrap_or_else(|e| e.into_inner());
        guard.limiter.record_success();
        guard.last_sas_code = Some(sas_code.clone());
        guard.paired_devices.insert(
            device_id.clone(),
            PairedDeviceRecord {
                device_id: device_id.clone(),
                device_name: device_name.clone(),
                device_token,
            },
        );
        guard.connected_peers.push(ConnectedRemotePeer {
            device_id: device_id.clone(),
            device_name,
            peer_addr: peer_addr.clone(),
            sas_code,
        });
    }

    let mut seq = 1u64;
    while let Ok(sample) = rx.recv().await {
        let json = serde_json::to_vec(&*sample).unwrap_or_default();
        let sealed = SealedWireFrame::seal(&session_key, seq, &json);
        let framed = encode_frame(&sealed, MAX_FRAME_BYTES)?;
        if stream.write_all(&framed).await.is_err() {
            break;
        }
        seq = seq.wrapping_add(1);
    }

    if let Ok(mut guard) = status.lock() {
        guard
            .connected_peers
            .retain(|p| p.peer_addr != peer_addr || p.device_id != device_id);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rfc_vectors_sha256_hmac_hkdf_x25519_and_chacha20_poly1305_match_exactly() {
        // 1. FIPS 180-4 SHA-256("abc")
        assert_eq!(
            hex_encode(&sha256(b"abc")),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );

        // 2. RFC 4231 HMAC-SHA256 Test Case 1
        let key1 = [0x0bu8; 20];
        assert_eq!(
            hex_encode(&hmac_sha256(&key1, b"Hi There")),
            "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7"
        );

        // 3. RFC 5869 HKDF-SHA256 Test Case 1
        let ikm = [0x0bu8; 22];
        let salt = hex_decode("000102030405060708090a0b0c").unwrap();
        let info = hex_decode("f0f1f2f3f4f5f6f7f8f9").unwrap();
        let okm = hkdf_sha256(&ikm, &salt, &info, 42);
        assert_eq!(
            hex_encode(&okm),
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"
        );

        // 4. RFC 7748 Section 6.1 X25519 Test Vector
        let alice_priv: [u8; 32] =
            hex_decode("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a")
                .unwrap()
                .try_into()
                .unwrap();
        let alice_pub_expected = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a";
        assert_eq!(hex_encode(&x25519_base(&alice_priv)), alice_pub_expected);

        let bob_priv: [u8; 32] =
            hex_decode("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb")
                .unwrap()
                .try_into()
                .unwrap();
        let bob_pub = x25519_base(&bob_priv);
        assert_eq!(
            hex_encode(&bob_pub),
            "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"
        );
        let shared = x25519(&alice_priv, &bob_pub);
        assert_eq!(
            hex_encode(&shared),
            "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"
        );

        // 5. RFC 8439 Section 2.8.2 AEAD ChaCha20-Poly1305 Test Vector
        let key: [u8; 32] =
            hex_decode("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f")
                .unwrap()
                .try_into()
                .unwrap();
        let nonce: [u8; 12] = hex_decode("070000004041424344454647")
            .unwrap()
            .try_into()
            .unwrap();
        let aad = hex_decode("50515253c0c1c2c3c4c5c6c7").unwrap();
        let pt = b"Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.";
        let sealed = chacha20_poly1305_seal(&key, &nonce, &aad, pt);
        let expected_ct_and_tag = concat!(
            "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d6",
            "3dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b36",
            "92ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc",
            "3ff4def08e4b7a9de576d26586cec64b6116",
            "1ae10b594f09e26a7e902ecbd0600691"
        );
        assert_eq!(hex_encode(&sealed), expected_ct_and_tag);
        let opened = chacha20_poly1305_open(&key, &nonce, &aad, &sealed).unwrap();
        assert_eq!(opened, pt);
    }

    #[test]
    fn address_policy_attempt_limiter_framing_and_sealed_wire_work() {
        assert!(RemoteAddressPolicy::is_allowed(
            "127.0.0.1".parse().unwrap(),
            false
        ));
        assert!(RemoteAddressPolicy::is_allowed(
            "192.168.1.42".parse().unwrap(),
            false
        ));
        assert!(RemoteAddressPolicy::is_allowed(
            "10.0.0.5".parse().unwrap(),
            false
        ));
        assert!(RemoteAddressPolicy::is_allowed(
            "172.16.4.1".parse().unwrap(),
            false
        ));
        assert!(RemoteAddressPolicy::is_allowed(
            "fc00::1".parse().unwrap(),
            false
        ));
        assert!(RemoteAddressPolicy::is_allowed(
            "fe80::1".parse().unwrap(),
            false
        ));
        assert!(!RemoteAddressPolicy::is_allowed(
            "8.8.8.8".parse().unwrap(),
            true
        ));
        assert!(!RemoteAddressPolicy::is_allowed(
            "100.64.0.1".parse().unwrap(),
            false
        ));
        assert!(RemoteAddressPolicy::is_allowed(
            "100.64.0.1".parse().unwrap(),
            true
        ));

        let mut limiter = RemoteAttemptLimiter::default();
        for _ in 0..15 {
            limiter.record_failure(100.0);
        }
        assert_eq!(limiter.penalty_secs(100.0), 10);
        assert_eq!(limiter.decayed_failures(100.0 + 300.0), 10); // 5 min = -5 failures
        assert_eq!(limiter.penalty_secs(100.0 + 900.0), 0); // 15 min = all forgiven
        limiter.record_failure(1000.0);
        limiter.record_success();
        assert_eq!(limiter.penalty_secs(1000.0), 0);

        let code = PairingCode::generate().expect("pairing code");
        assert_eq!(code.len(), 6);
        assert!(
            code.chars()
                .all(|c| PairingCode::ALPHABET.contains(&(c as u8)))
        );

        let (priv_a, pub_a) = generate_x25519_keypair().unwrap();
        let (priv_b, pub_b) = generate_x25519_keypair().unwrap();
        let sec_a = x25519(&priv_a, &pub_b);
        let sec_b = x25519(&priv_b, &pub_a);
        assert_eq!(sec_a, sec_b);

        let tx = RemoteTranscript::pair_transcript(&pub_a, &pub_b, b"n1", b"n2");
        let (k_a, sas_a) = RemoteKeySchedule::derive_pairing_keys(&sec_a, &tx);
        let (k_b, sas_b) = RemoteKeySchedule::derive_pairing_keys(&sec_b, &tx);
        assert_eq!(k_a, k_b);
        assert_eq!(sas_a, sas_b);
        assert_eq!(sas_a.len(), 6);

        let token = [0x42u8; 32];
        let stx = RemoteTranscript::session_transcript(&pub_a, &pub_b, b"sn", b"dev1");
        let proof = RemoteKeySchedule::device_token_proof(&token, &stx);
        assert!(RemoteKeySchedule::verify_device_token_proof(
            &token, &stx, &proof
        ));

        let (sk, _ck) = RemoteKeySchedule::derive_session_keys(&sec_a, &stx, &token);
        let sealed = SealedWireFrame::seal(&sk, 7, b"{\"cpu\":42.5}");
        assert_eq!(sealed[0], SEALED_WIRE_MARKER);
        let framed = encode_frame(&sealed, MAX_FRAME_BYTES).unwrap();
        let mut buf = framed.clone();
        let decoded = decode_frames(&mut buf, MAX_FRAME_BYTES).unwrap();
        assert!(buf.is_empty());
        assert_eq!(decoded.len(), 1);
        let (seq, pt) = SealedWireFrame::open(&sk, 7, &decoded[0]).unwrap();
        assert_eq!(seq, 7);
        assert_eq!(pt, b"{\"cpu\":42.5}");
        assert!(matches!(
            SealedWireFrame::open(&sk, 8, &decoded[0]),
            Err(RemoteError::ReplaySequence { .. })
        ));
    }

    #[test]
    fn remote_server_end_to_end_handshake_and_sealed_telemetry_broadcast() {
        let rt = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        let mut srv = RemoteServerHandle::start(
            rt.handle(),
            RemoteServerConfig {
                port: 0,
                allow_cgnat: false,
                advertise_bonjour: false,
            },
        )
        .expect("start remote server");

        let (port, pairing_code) = {
            let st = srv.status.lock().unwrap();
            (st.bound_port.unwrap(), st.pairing_code.clone())
        };

        let (client_priv, client_pub) = generate_x25519_keypair().unwrap();
        let client_nonce = [0x11u8; 16];
        let hello = RemoteHandshakeMessage::PairHello {
            client_public_key_hex: hex_encode(&client_pub),
            client_nonce_hex: hex_encode(&client_nonce),
            pairing_code,
            device_id: "test-ipad".to_string(),
            device_name: "Test iPad".to_string(),
        };
        let hello_bytes = serde_json::to_vec(&hello).unwrap();
        let framed_hello = encode_frame(&hello_bytes, MAX_HANDSHAKE_FRAME_BYTES).unwrap();

        let snap = SystemSnapshot::new_empty(42, 9_000);
        let session_key = rt.block_on(async {
            use tokio::io::{AsyncReadExt, AsyncWriteExt};
            let mut stream = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
            stream.write_all(&framed_hello).await.unwrap();

            let mut rbuf = Vec::new();
            let mut tmp = [0u8; 4096];
            let accept_bytes = loop {
                let n = stream.read(&mut tmp).await.unwrap();
                assert!(n > 0);
                rbuf.extend_from_slice(&tmp[..n]);
                let frames = decode_frames(&mut rbuf, MAX_HANDSHAKE_FRAME_BYTES).unwrap();
                if let Some(f) = frames.into_iter().next() {
                    break f;
                }
            };

            let accept: RemoteHandshakeMessage = serde_json::from_slice(&accept_bytes).unwrap();
            let RemoteHandshakeMessage::PairAccept {
                server_public_key_hex,
                server_nonce_hex,
                sas_code,
                device_token_hex,
            } = accept
            else {
                panic!("expected PairAccept");
            };
            assert_eq!(sas_code.len(), 6);

            let srv_pub: [u8; 32] = hex_decode(&server_public_key_hex)
                .unwrap()
                .try_into()
                .unwrap();
            let srv_nonce = hex_decode(&server_nonce_hex).unwrap();
            let dev_token = hex_decode(&device_token_hex).unwrap();

            let shared = x25519(&client_priv, &srv_pub);
            let session_tx = RemoteTranscript::session_transcript(
                &client_pub,
                &srv_pub,
                &srv_nonce,
                b"test-ipad",
            );
            let (sk, _) = RemoteKeySchedule::derive_session_keys(&shared, &session_tx, &dev_token);

            // Broadcast a snapshot and read the sealed frame
            srv.broadcast_snapshot(&snap);
            let sealed_bytes = loop {
                if let Ok(frames) = decode_frames(&mut rbuf, MAX_FRAME_BYTES)
                    && let Some(f) = frames.into_iter().next()
                {
                    break f;
                }
                let n = stream.read(&mut tmp).await.unwrap();
                assert!(n > 0);
                rbuf.extend_from_slice(&tmp[..n]);
            };
            let (seq, pt) = SealedWireFrame::open(&sk, 1, &sealed_bytes).unwrap();
            assert_eq!(seq, 1);
            let decoded: RemoteTelemetrySample = serde_json::from_slice(&pt).unwrap();
            assert_eq!(decoded.generation, 42);
            sk
        });

        assert_ne!(session_key, [0u8; 32]);
        srv.stop();
    }
}
