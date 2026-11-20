use crate::snapshot::SystemSnapshot;
use bitflags::bitflags;
use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;
use tokio::sync::{mpsc, watch};

/// 5-state Nexus update speed.
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum UpdateSpeed {
    Fast = 0,
    High = 1,
    Normal = 2,
    Slow = 3,
    Paused = 4,
}

impl UpdateSpeed {
    /// Converts ordinal into `UpdateSpeed`, clamping `> 4` to `Normal` (`0x100548d74`).
    #[inline]
    pub const fn from_ordinal(ordinal: u8) -> Self {
        match ordinal {
            0 => Self::Fast,
            1 => Self::High,
            2 => Self::Normal,
            3 => Self::Slow,
            4 => Self::Paused,
            _ => Self::Normal,
        }
    }

    /// Returns exact nanosecond interval from `__TEXT,__const` `0x100670280`.
    #[inline]
    pub const fn interval_ns(self) -> u64 {
        match self {
            Self::Fast => 250_000_000,
            Self::High => 500_000_000,
            Self::Normal => 1_000_000_000,
            Self::Slow => 2_000_000_000,
            Self::Paused => 0,
        }
    }

    #[inline]
    pub const fn duration(self) -> Option<Duration> {
        let ns = self.interval_ns();
        if ns == 0 {
            None
        } else {
            Some(Duration::from_nanos(ns))
        }
    }
}

bitflags! {
    #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
    pub struct DomainMask: u32 {
        const CPU_MEM_GPU   = 1 << 0;
        const SOC_POWER     = 1 << 1;
        const THERMALS      = 1 << 2;
        const DISK_NET_IO   = 1 << 3;
        const PROCESSES     = 1 << 4;
        const SLOW_SERVICES = 1 << 5;
        const CONNECTIONS   = 1 << 6;
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct LeaseRecord {
    interval: Duration,
    domains: DomainMask,
}

#[derive(Debug, Default)]
struct CoordinatorState {
    leases: HashMap<u64, LeaseRecord>,
}

/// Multi-subscriber cadence coordinator that computes the active minimum interval and domain union
/// across all live `CadenceLease` handles without evicting or orphaning subscribers.
#[derive(Debug, Clone)]
pub struct CadenceCoordinator {
    next_id: Arc<AtomicU64>,
    state: Arc<Mutex<CoordinatorState>>,
    schedule_tx: watch::Sender<(Option<Duration>, DomainMask)>,
    schedule_rx: watch::Receiver<(Option<Duration>, DomainMask)>,
}

impl Default for CadenceCoordinator {
    fn default() -> Self {
        Self::new()
    }
}

impl CadenceCoordinator {
    pub fn new() -> Self {
        let (schedule_tx, schedule_rx) = watch::channel((None, DomainMask::empty()));
        Self {
            next_id: Arc::new(AtomicU64::new(1)),
            state: Arc::new(Mutex::new(CoordinatorState::default())),
            schedule_tx,
            schedule_rx,
        }
    }

    /// Acquires an RAII lease for `interval` and `domains`. Dropping the returned `CadenceLease`
    /// automatically recalculates the active cadence and domain union.
    pub fn acquire_lease(&self, interval: Duration, domains: DomainMask) -> CadenceLease {
        let lease_id = self.next_id.fetch_add(1, Ordering::Relaxed);
        {
            let mut guard = self.state.lock();
            guard
                .leases
                .insert(lease_id, LeaseRecord { interval, domains });
            let summary = Self::compute_summary(&guard);
            let _ = self.schedule_tx.send(summary);
        }
        CadenceLease {
            lease_id,
            interval,
            domains,
            coordinator: Some(self.clone()),
        }
    }

    /// Updates an existing lease's interval and domains in place.
    pub fn update_lease(&self, lease_id: u64, interval: Duration, domains: DomainMask) {
        let mut guard = self.state.lock();
        if let Some(rec) = guard.leases.get_mut(&lease_id) {
            rec.interval = interval;
            rec.domains = domains;
            let summary = Self::compute_summary(&guard);
            let _ = self.schedule_tx.send(summary);
        }
    }

    /// Returns the current `(min_interval, domain_union)` across all active leases.
    pub fn active_schedule(&self) -> (Option<Duration>, DomainMask) {
        *self.schedule_rx.borrow()
    }

    /// Returns a watch receiver notified whenever leases are added, updated, or dropped.
    pub fn subscribe_schedule(&self) -> watch::Receiver<(Option<Duration>, DomainMask)> {
        self.schedule_rx.clone()
    }

    fn release_lease(&self, lease_id: u64) {
        let mut guard = self.state.lock();
        if guard.leases.remove(&lease_id).is_some() {
            let summary = Self::compute_summary(&guard);
            let _ = self.schedule_tx.send(summary);
        }
    }

    fn compute_summary(state: &CoordinatorState) -> (Option<Duration>, DomainMask) {
        let mut min_interval: Option<Duration> = None;
        let mut union_domains = DomainMask::empty();
        for rec in state.leases.values() {
            union_domains |= rec.domains;
            if !rec.interval.is_zero() {
                min_interval = Some(match min_interval {
                    None => rec.interval,
                    Some(cur) => cur.min(rec.interval),
                });
            }
        }
        (min_interval, union_domains)
    }
}

/// RAII lease held by UI views, pop-outs, `RulesEngine`, `SleepPrevention`, and `MetricsStore`.
/// Dropping a lease recalculates the active minimum interval without evicting other subscribers.
#[derive(Debug)]
pub struct CadenceLease {
    pub lease_id: u64,
    pub interval: Duration,
    pub domains: DomainMask,
    coordinator: Option<CadenceCoordinator>,
}

impl CadenceLease {
    pub fn update(&mut self, interval: Duration, domains: DomainMask) {
        self.interval = interval;
        self.domains = domains;
        if let Some(coord) = &self.coordinator {
            coord.update_lease(self.lease_id, interval, domains);
        }
    }
}

impl Drop for CadenceLease {
    fn drop(&mut self) {
        if let Some(coord) = self.coordinator.take() {
            coord.release_lease(self.lease_id);
        }
    }
}

/// Publisher half paired with `SnapshotHub`.
#[derive(Debug, Clone)]
pub struct SnapshotPublisher {
    ui_tx: watch::Sender<Arc<SystemSnapshot>>,
    automation_tx: mpsc::Sender<Arc<SystemSnapshot>>,
    storage_tx: mpsc::Sender<Arc<SystemSnapshot>>,
}

impl SnapshotPublisher {
    /// Publishes a verified coherent snapshot to the depth-1 UI watch channel and bounded
    /// automation (capacity 32) and storage (capacity 64) rings without blocking the sampler.
    pub fn publish(&self, snapshot: Arc<SystemSnapshot>) {
        let _ = self.ui_tx.send(Arc::clone(&snapshot));
        let _ = self.automation_tx.try_send(Arc::clone(&snapshot));
        let _ = self.storage_tx.try_send(snapshot);
    }

    /// Subscribes an additional UI / Popout consumer to the depth-1 coalescing watch channel.
    pub fn subscribe_ui(&self) -> watch::Receiver<Arc<SystemSnapshot>> {
        self.ui_tx.subscribe()
    }
}

/// Multi-consumer fan-out hub replacing single-writer polling locks and shared stream eviction.
pub struct SnapshotHub {
    /// Depth-1 coalescing channel for the main UI and up to 6 pop-outs.
    pub ui_inbox: watch::Receiver<Arc<SystemSnapshot>>,
    /// Dedicated bounded ring (capacity 32) for `RulesEngine` and `AlertsService`.
    pub automation_rx: mpsc::Receiver<Arc<SystemSnapshot>>,
    /// Dedicated bounded ring (capacity 64) for the single-threaded SQLite writer actor.
    pub storage_rx: mpsc::Receiver<Arc<SystemSnapshot>>,
}

impl SnapshotHub {
    pub const AUTOMATION_CAPACITY: usize = 32;
    pub const STORAGE_CAPACITY: usize = 64;

    pub fn new(initial: Arc<SystemSnapshot>) -> (SnapshotPublisher, Self) {
        let (ui_tx, ui_inbox) = watch::channel(initial);
        let (automation_tx, automation_rx) = mpsc::channel(Self::AUTOMATION_CAPACITY);
        let (storage_tx, storage_rx) = mpsc::channel(Self::STORAGE_CAPACITY);
        (
            SnapshotPublisher {
                ui_tx,
                automation_tx,
                storage_tx,
            },
            Self {
                ui_inbox,
                automation_rx,
                storage_rx,
            },
        )
    }
}
