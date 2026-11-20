use crate::{Metric, MetricReading, SystemSnapshot};
use std::fmt::Write as _;

fn emit_metric(out: &mut String, snap: &SystemSnapshot, name: &str, help: &str, m: &Metric<f64>) {
    let src = snap.source_name(m.provenance.source);
    let qual = m.provenance.quality.as_str();
    let state = m.state().as_str();
    match m.reading {
        MetricReading::Measured { value, stale } if value.is_finite() => {
            let _ = writeln!(out, "# HELP {name} {help}");
            let _ = writeln!(out, "# TYPE {name} gauge");
            let _ = writeln!(
                out,
                "{name}{{source=\"{src}\",quality=\"{qual}\",stale=\"{stale}\"}} {value}"
            );
        }
        _ => {
            let _ = writeln!(
                out,
                "nexus_metric_unavailable{{metric=\"{name}\",state=\"{state}\",source=\"{src}\"}} 1"
            );
        }
    }
}

/// Renders Prometheus `/metrics` text format (`nexus_*` prefix), omitting `Unavailable`
/// metrics instead of fabricating `0.0`, while reporting `nexus_metric_unavailable` markers.
pub fn render_prometheus_metrics(
    snap: &SystemSnapshot,
    alert_events_total: u64,
    anomaly_events_total: u64,
) -> String {
    let mut out = String::with_capacity(6144);

    // CPU
    emit_metric(
        &mut out,
        snap,
        "nexus_cpu_usage_percent",
        "Total CPU utilization percentage",
        &snap.system.cpu_percent,
    );
    emit_metric(
        &mut out,
        snap,
        "nexus_cpu_user_percent",
        "User CPU utilization percentage",
        &snap.system.cpu_user_percent,
    );
    emit_metric(
        &mut out,
        snap,
        "nexus_cpu_kernel_percent",
        "Kernel CPU utilization percentage",
        &snap.system.cpu_kernel_percent,
    );

    if !snap.system.cores.is_empty() {
        let mut wrote_header = false;
        for core in &snap.system.cores {
            if let MetricReading::Measured { value, .. } = core.total_percent.reading
                && value.is_finite()
            {
                if !wrote_header {
                    let _ = writeln!(
                        out,
                        "# HELP nexus_cpu_core_usage_percent Per-core CPU utilization (%)"
                    );
                    let _ = writeln!(out, "# TYPE nexus_cpu_core_usage_percent gauge");
                    wrote_header = true;
                }
                let _ = writeln!(
                    out,
                    "nexus_cpu_core_usage_percent{{core=\"{}\"}} {:.2}",
                    core.core_index, value
                );
            }
        }
    }

    let _ = writeln!(
        out,
        "# HELP nexus_cpu_process_count Total running processes"
    );
    let _ = writeln!(out, "# TYPE nexus_cpu_process_count gauge");
    let _ = writeln!(out, "nexus_cpu_process_count {}", snap.processes.len());

    // Memory
    emit_metric(
        &mut out,
        snap,
        "nexus_memory_total_bytes",
        "Total physical memory in bytes",
        &snap.system.mem_total_bytes,
    );
    emit_metric(
        &mut out,
        snap,
        "nexus_memory_used_bytes",
        "Used physical memory in bytes",
        &snap.system.mem_used_bytes,
    );
    emit_metric(
        &mut out,
        snap,
        "nexus_memory_available_bytes",
        "Available physical memory in bytes",
        &snap.system.mem_free_bytes,
    );
    emit_metric(
        &mut out,
        snap,
        "nexus_memory_used_percent",
        "Used physical memory percentage",
        &snap.system.mem_used_percent,
    );
    emit_metric(
        &mut out,
        snap,
        "nexus_memory_wired_bytes",
        "Wired memory in bytes",
        &snap.system.mem_wired_bytes,
    );
    emit_metric(
        &mut out,
        snap,
        "nexus_memory_compressed_bytes",
        "Compressed memory in bytes",
        &snap.system.mem_compressed_bytes,
    );
    emit_metric(
        &mut out,
        snap,
        "nexus_swap_used_bytes",
        "Used swap memory in bytes",
        &snap.system.swap_used_bytes,
    );

    // GPU
    emit_metric(
        &mut out,
        snap,
        "nexus_gpu_usage_percent",
        "GPU device utilization percentage",
        &snap.system.gpu_utilization_percent,
    );
    emit_metric(
        &mut out,
        snap,
        "nexus_gpu_memory_used_bytes",
        "GPU memory in use in bytes",
        &snap.system.gpu_in_use_memory_bytes,
    );
    emit_metric(
        &mut out,
        snap,
        "nexus_gpu_memory_total_bytes",
        "GPU allocated memory in bytes",
        &snap.system.gpu_alloc_memory_bytes,
    );

    // SoC Power & Thermals
    emit_metric(
        &mut out,
        snap,
        "nexus_soc_package_watts",
        "Apple Silicon package power in watts",
        &snap.hardware_telemetry.soc_power.package_watts,
    );
    emit_metric(
        &mut out,
        snap,
        "nexus_soc_system_power_in_watts",
        "System power input in watts",
        &snap.hardware_telemetry.soc_power.system_power_in_watts,
    );
    emit_metric(
        &mut out,
        snap,
        "nexus_cpu_temperature_celsius",
        "Maximum CPU/die temperature in Celsius",
        &snap.hardware_telemetry.thermals.max_die_celsius,
    );

    // Disks
    for disk in &snap.disks {
        let label = escape_prom_label(&disk.bsd_name);
        if let MetricReading::Measured { value, .. } = disk.read_bytes_per_sec.reading
            && value.is_finite()
        {
            let _ = writeln!(
                out,
                "nexus_disk_read_bytes_per_second{{disk=\"{label}\"}} {value}"
            );
        }
        if let MetricReading::Measured { value, .. } = disk.write_bytes_per_sec.reading
            && value.is_finite()
        {
            let _ = writeln!(
                out,
                "nexus_disk_write_bytes_per_second{{disk=\"{label}\"}} {value}"
            );
        }
        if let MetricReading::Measured { value, .. } = disk.root_used_percent.reading
            && value.is_finite()
        {
            let _ = writeln!(
                out,
                "nexus_disk_used_percent{{disk=\"{label}\"}} {value:.2}"
            );
        }
    }

    // Networks
    for net in &snap.networks {
        let label = escape_prom_label(&net.interface);
        if let MetricReading::Measured { value, .. } = net.tx_bytes_per_sec.reading
            && value.is_finite()
        {
            let _ = writeln!(
                out,
                "nexus_network_send_bytes_per_second{{adapter=\"{label}\"}} {value}"
            );
        }
        if let MetricReading::Measured { value, .. } = net.rx_bytes_per_sec.reading
            && value.is_finite()
        {
            let _ = writeln!(
                out,
                "nexus_network_recv_bytes_per_second{{adapter=\"{label}\"}} {value}"
            );
        }
    }

    // Counters
    let _ = writeln!(
        out,
        "# HELP nexus_alert_events_total Total alert threshold breaches since start"
    );
    let _ = writeln!(out, "# TYPE nexus_alert_events_total counter");
    let _ = writeln!(out, "nexus_alert_events_total {alert_events_total}");

    let _ = writeln!(
        out,
        "# HELP nexus_anomaly_events_total Total anomaly events detected since start"
    );
    let _ = writeln!(out, "# TYPE nexus_anomaly_events_total counter");
    let _ = writeln!(out, "nexus_anomaly_events_total {anomaly_events_total}");

    out
}

fn escape_prom_label(s: &str) -> String {
    s.replace('\\', "\\\\")
        .replace('"', "\\\"")
        .replace('\n', "\\n")
}

/// Pre-built Grafana dashboard JSON visualizing Nexus System Monitor Prometheus metrics.
pub struct GrafanaDashboard;

impl GrafanaDashboard {
    pub const JSON: &'static str = r#"{
  "__inputs": [
    {
      "name": "DS_PROMETHEUS",
      "label": "Prometheus",
      "description": "Point this at http://localhost:9182 (or your configured Nexus port)",
      "type": "datasource",
      "pluginId": "prometheus",
      "pluginName": "Prometheus"
    }
  ],
  "__requires": [
    {"type":"grafana","id":"grafana","name":"Grafana","version":"10.0.0"},
    {"type":"datasource","id":"prometheus","name":"Prometheus","version":"1.0.0"},
    {"type":"panel","id":"timeseries","name":"Time series","version":""},
    {"type":"panel","id":"gauge","name":"Gauge","version":""},
    {"type":"panel","id":"bargauge","name":"Bar gauge","version":""},
    {"type":"panel","id":"stat","name":"Stat","version":""}
  ],
  "title": "Nexus System Monitor",
  "uid": "nexus-monitor-v2",
  "description": "System metrics from Nexus System Monitor. Requires Prometheus datasource pointed at http://localhost:9182/metrics.",
  "schemaVersion": 38,
  "version": 1,
  "tags": ["nexus-monitor"],
  "time": {"from": "now-1h", "to": "now"},
  "refresh": "10s",
  "panels": [
    {"id":1,"type":"row","title":"CPU","collapsed":false,"gridPos":{"h":1,"w":24,"x":0,"y":0},"panels":[]},
    {
      "id":2,"type":"timeseries","title":"CPU Total Usage",
      "gridPos":{"h":8,"w":12,"x":0,"y":1},
      "datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},
      "targets":[{"refId":"A","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_cpu_usage_percent","legendFormat":"CPU %"}],
      "fieldConfig":{"defaults":{"unit":"percent","min":0,"max":100,"thresholds":{"mode":"absolute","steps":[{"value":null,"color":"green"},{"value":70,"color":"yellow"},{"value":90,"color":"red"}]}}}
    },
    {
      "id":3,"type":"timeseries","title":"CPU Per Core",
      "gridPos":{"h":8,"w":12,"x":12,"y":1},
      "datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},
      "targets":[{"refId":"A","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_cpu_core_usage_percent","legendFormat":"Core {{core}}"}],
      "fieldConfig":{"defaults":{"unit":"percent","min":0,"max":100}}
    },
    {"id":4,"type":"row","title":"Memory","collapsed":false,"gridPos":{"h":1,"w":24,"x":0,"y":9},"panels":[]},
    {
      "id":5,"type":"gauge","title":"Memory Used",
      "gridPos":{"h":8,"w":6,"x":0,"y":10},
      "datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},
      "targets":[{"refId":"A","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_memory_used_percent","legendFormat":"Used %"}],
      "fieldConfig":{"defaults":{"unit":"percent","min":0,"max":100,"thresholds":{"mode":"absolute","steps":[{"value":null,"color":"green"},{"value":70,"color":"yellow"},{"value":90,"color":"red"}]}}}
    },
    {
      "id":6,"type":"timeseries","title":"Memory Usage (bytes)",
      "gridPos":{"h":8,"w":18,"x":6,"y":10},
      "datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},
      "targets":[
        {"refId":"A","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_memory_used_bytes","legendFormat":"Used"},
        {"refId":"B","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_memory_available_bytes","legendFormat":"Available"}
      ],
      "fieldConfig":{"defaults":{"unit":"bytes","min":0}}
    },
    {"id":7,"type":"row","title":"Disk & Network","collapsed":false,"gridPos":{"h":1,"w":24,"x":0,"y":18},"panels":[]},
    {
      "id":8,"type":"timeseries","title":"Disk I/O Rate",
      "gridPos":{"h":8,"w":12,"x":0,"y":19},
      "datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},
      "targets":[
        {"refId":"A","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_disk_read_bytes_per_second","legendFormat":"Read {{disk}}"},
        {"refId":"B","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_disk_write_bytes_per_second","legendFormat":"Write {{disk}}"}
      ],
      "fieldConfig":{"defaults":{"unit":"Bps","min":0}}
    },
    {
      "id":9,"type":"timeseries","title":"Network Throughput",
      "gridPos":{"h":8,"w":12,"x":12,"y":19},
      "datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},
      "targets":[
        {"refId":"A","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_network_send_bytes_per_second","legendFormat":"Send {{adapter}}"},
        {"refId":"B","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_network_recv_bytes_per_second","legendFormat":"Recv {{adapter}}"}
      ],
      "fieldConfig":{"defaults":{"unit":"Bps","min":0}}
    },
    {"id":10,"type":"row","title":"GPU, Power & Alerts","collapsed":false,"gridPos":{"h":1,"w":24,"x":0,"y":27},"panels":[]},
    {
      "id":11,"type":"timeseries","title":"GPU Usage (%)",
      "gridPos":{"h":8,"w":12,"x":0,"y":28},
      "datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},
      "targets":[{"refId":"A","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_gpu_usage_percent","legendFormat":"GPU %"}],
      "fieldConfig":{"defaults":{"unit":"percent","min":0,"max":100}}
    },
    {
      "id":12,"type":"stat","title":"Package Power (W)",
      "gridPos":{"h":4,"w":6,"x":12,"y":28},
      "datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},
      "targets":[{"refId":"A","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_soc_package_watts","legendFormat":"Watts"}],
      "fieldConfig":{"defaults":{"unit":"watt"}}
    },
    {
      "id":13,"type":"stat","title":"CPU Die Temperature",
      "gridPos":{"h":4,"w":6,"x":18,"y":28},
      "datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},
      "targets":[{"refId":"A","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_cpu_temperature_celsius","legendFormat":"Temp"}],
      "fieldConfig":{"defaults":{"unit":"celsius"}}
    },
    {
      "id":14,"type":"stat","title":"Alert Events (total)",
      "gridPos":{"h":4,"w":6,"x":12,"y":32},
      "datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},
      "targets":[{"refId":"A","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_alert_events_total","legendFormat":"Alerts"}]
    },
    {
      "id":15,"type":"stat","title":"Running Processes",
      "gridPos":{"h":4,"w":6,"x":18,"y":32},
      "datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},
      "targets":[{"refId":"A","datasource":{"type":"prometheus","uid":"${DS_PROMETHEUS}"},"expr":"nexus_cpu_process_count","legendFormat":"Processes"}]
    }
  ]
}"#;
}

/// Generates a Telegraf TOML configuration snippet scraping the Nexus Monitor Prometheus endpoint.
pub struct TelegrafConfigGenerator;

impl TelegrafConfigGenerator {
    pub fn generate(port: u16) -> String {
        format!(
            r#"# Nexus System Monitor - Telegraf Input Configuration
# Save as nexus-monitor.conf inside /etc/telegraf/telegraf.d/ or paste into telegraf.conf.

[[inputs.prometheus]]
  ## Nexus System Monitor Prometheus endpoint
  urls = ["http://localhost:{port}/metrics"]

  ## Scrape interval - override global agent interval if desired
  # interval = "15s"

  ## HTTP timeout
  response_timeout = "5s"

  ## Use metric_version = 2 so metric names become field keys
  metric_version = 2

  [inputs.prometheus.tags]
    source = "nexus-monitor"
"#
        )
    }
}
