# NetprobeSharp

This project is meant to be a replacement for [netprobe_lite](https://github.com/plaintextpackets/netprobe_lite) after I had a few annoyances with it (code not being embedded into the Docker image, requirement of having 2~3 instances of the code running, needing docker and other issues).

It is not meant to be a comprehensive networking tool like [SmokePing](https://oss.oetiker.ch/smokeping/) and similar. I only made a rewrite of netprobe_lite in C# since it's the language I knew best, but I still know almost nothing of networking. I originally tried to hand-roll the ICMP sockets in C# to avoid shelling out to `ping`, but it grew too complex to justify, so the prober just runs the system `ping` and parses its summary — which still reports sub-millisecond RTTs, unlike `System.Net.NetworkInformation.Ping`.

## What it does

On a fixed interval, NetprobeSharp:

1. Pings every site in [`Sites`](#configuration-reference) and records the round-trip time of every reply (as a histogram), plus packet loss and jitter per site.
2. Sends a recursive DNS query for [`DnsTestSite`](#configuration-reference) to every resolver in [`DnsResolvers`](#configuration-reference) and records the round-trip latency per resolver (as a histogram).
3. Combines those into a single internet health score.

All of this is published as [Prometheus metrics](#metrics) for you to scrape and graph, and a [`/health`](#metrics) endpoint is exposed for liveness probes.

## System Requirements

NetprobeSharp shells out to the system `ping`, so it needs `ping` on `PATH` — the `iputils` package on Linux; preinstalled on macOS/BSD. `ping` handles IPv4/IPv6 selection and RTT aggregation; the prober only parses its summary block.

On a normal distro install there's nothing else to do: `ping` already works for unprivileged users because it ships setuid or with the `cap_net_raw` file capability.

In a minimal container you need two things: install the ping package, and let it open an ICMP socket without root. Modern `iputils` `ping` uses an unprivileged ICMP datagram socket (`SOCK_DGRAM`/`IPPROTO_ICMP`) when the running group ID is within `net.ipv4.ping_group_range`, which is off by default in many base images. Enable it one of these ways:

### Docker:

```bash
docker run --sysctl "net.ipv4.ping_group_range=0 2147483647" ...
```

### Docker Compose:

```yaml
services:
  netprobe:
    image: ...
    sysctls:
      - "net.ipv4.ping_group_range=0 2147483647"
```

In both Docker cases the sysctl is network-namespaced, so it only touches the container — no host change — as long as the container has its own netns (i.e. not `network_mode: host`).

## Running

The image is published at `gggdotdev/netprobesharp:latest`:

```bash
docker pull gggdotdev/netprobesharp:latest
```

It is built for `linux/amd64`, `linux/arm64` and `linux/arm/v7`, so it runs as-is on x86-64 servers, Apple Silicon and Raspberry Pi (32- and 64-bit).

Two things are needed for a working run:

- **Let `ping` open an ICMP socket without root** — the `net.ipv4.ping_group_range` sysctl described under [System Requirements](#system-requirements).
- **Expose the metrics port** — publish container port `9464`. The server binds to all interfaces (`http://+:9464`), so a published port reaches it; there's nothing else to configure. The port is overridable via [`ASPNETCORE_URLS`](#metrics) if you need a different one.

### `docker run` — configured via environment variables

This is the quickest way to try it; no config file needed. Replace the `My_DNS_Server` IP with your own router/resolver.

```bash
docker run --rm \
  --sysctl "net.ipv4.ping_group_range=0 2147483647" \
  -p 9464:9464 \
  -e NETPROBE_Sites__0=google.com \
  -e NETPROBE_Sites__1=github.com \
  -e NETPROBE_DnsResolvers__Google_DNS=8.8.8.8 \
  -e NETPROBE_DnsResolvers__Cloudflare_DNS=1.1.1.1 \
  -e NETPROBE_DnsResolvers__My_DNS_Server=192.168.1.1 \
  gggdotdev/netprobesharp:latest
```

Then scrape it from the host:

```bash
curl http://localhost:9464/metrics
```

### `docker run` — configured via a mounted `netprobe.jsonc`

Put a [`netprobe.jsonc`](#netprobejsonc) in the current directory, mount it into a config dir and point `NETPROBE_ConfigPath` at that dir:

```bash
docker run --rm \
  --sysctl "net.ipv4.ping_group_range=0 2147483647" \
  -p 9464:9464 \
  -e NETPROBE_ConfigPath=/config \
  -v "$PWD/netprobe.jsonc:/config/netprobe.jsonc:ro" \
  gggdotdev/netprobesharp:latest
```

The container runs as a non-root user, so make sure the mounted file is world-readable (`chmod a+r netprobe.jsonc`).

### Docker Compose

```yaml
services:
  netprobe:
    image: gggdotdev/netprobesharp:latest
    restart: unless-stopped
    sysctls:
      - "net.ipv4.ping_group_range=0 2147483647"
    ports:
      - "9464:9464"
    environment:
      NETPROBE_ConfigPath: "/config"
    volumes:
      - ./netprobe.jsonc:/config/netprobe.jsonc:ro
```

```bash
docker compose up -d
curl http://localhost:9464/metrics
```

### Scraping from Prometheus

Point a Prometheus scrape job at the exposed port. If Prometheus runs in the same Compose network, target the service name; otherwise target the host/IP and published port:

```yaml
scrape_configs:
  - job_name: netprobe
    static_configs:
      # same Docker network as the compose service above:
      - targets: ["netprobe:9464"]
      # or, scraping the host that published the port:
      # - targets: ["192.168.1.10:9464"]
```

## Configuration

Configuration comes from three sources, applied in this order — each one overrides the ones before it:

1. **`netprobe.jsonc`** — a JSON (with comments) file next to the executable. Optional, and reloaded automatically when it changes.
2. **Environment variables** prefixed with `NETPROBE_`.
3. **Command-line arguments**.

So a command-line argument beats an environment variable, which beats the JSON file. All options live at the root of the configuration; there is no wrapping section.

### Where `netprobe.jsonc` is loaded from

By default, the file is read from the directory containing the executable. Set the `NETPROBE_ConfigPath` environment variable to point at a different directory (it must contain a `netprobe.jsonc`). This is the one setting that is *only* an environment variable — it's read before configuration binding to decide where to look.

```bash
NETPROBE_ConfigPath=/etc/netprobe ./NetprobeSharp
```

### Configuration reference

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `Sites` | string array | *(required)* | Domains/IPs to ping each interval. Must have at least one valid host. |
| `DnsResolvers` | map of name → IP | *(required)* | DNS resolvers to time. Each value must be a valid IP. **Must** include a key named `My_DNS_Server` (case-insensitive) — its latency is the one used in the health score. |
| `DnsTestSite` | string | `google.com` | The domain queried against every resolver. Must be a valid domain. |
| `ProbeIntervalSec` | int | `30` | Seconds between probe runs. Must be ≥ 1. |
| `ProbeCountPerSite` | int | `50` | Pings sent to each site per run. Must be ≥ 1. |
| `PingTimeoutMs` | int | `1000` | How long to wait for each ping reply before marking it lost (`ping -W`). Must be ≥ 1. |
| `PingSpacingMs` | int | `100` | Delay between consecutive pings within a single run (`ping -i`). Distinct from `ProbeIntervalSec`, which is the gap between whole runs. Must be ≥ 1. |
| `DnsTimeoutMs` | int | `1000` | How long to wait for a DNS reply before timing out. Must be ≥ 1. |
| `Score.LossThreshold` | double | `5` | Packet loss % treated as the worst case (caps the loss contribution). |
| `Score.LossWeight` | double | `0.60` | Weight of packet loss in the score. |
| `Score.LatencyThreshold` | double | `100` | Ping latency (ms) treated as the worst case. |
| `Score.LatencyWeight` | double | `0.15` | Weight of ping latency in the score. |
| `Score.JitterThreshold` | double | `30` | Jitter (ms) treated as the worst case. |
| `Score.JitterWeight` | double | `0.20` | Weight of jitter in the score. |
| `Score.DnsThreshold` | double | `100` | DNS latency (ms) treated as the worst case. |
| `Score.DnsWeight` | double | `0.05` | Weight of DNS latency in the score. |
| `Speedtest.Enable` | bool | `false` | Enable the periodic speed-test module (Ookla). Disabled by default — no network I/O occurs while `false`. |
| `Speedtest.TestIntervalMin` | int | `10` | Minutes between speed-test runs. Must be ≥ 5. |
| `Speedtest.DownloadSizeMb` | int | `1024` | Download payload cap in MB. Must be ≥ 1. |
| `Speedtest.UploadSizeMb` | int | `256` | Upload payload cap in MB. Must be ≥ 1. |
| `Speedtest.ServerReselectionIntervalMin` | int? | `null` | Re-select the fastest server this often (minutes); `null` selects once at startup. Must be ≥ 1 when set. |

Configuration is validated on startup; if anything is missing or invalid (e.g. no `Sites`, a bad IP, or a missing `My_DNS_Server` resolver) the app refuses to start and logs the errors.

### `netprobe.jsonc`

This is the recommended way to configure NetprobeSharp. A starter file ships with the build:

```jsonc
{
  // Probe cycle
  "ProbeIntervalSec": 30,
  // Ping
  "ProbeCountPerSite": 50,
  "PingTimeoutMs": 1000,
  "PingSpacingMs": 100,
  "Sites": [
    "google.com",
    "facebook.com",
    "twitter.com",
    "youtube.com",
    "amazon.com"
  ],
  // DNS
  "DnsTimeoutMs": 1000,
  "DnsTestSite": "google.com",
  "DnsResolvers": {
    "Google_DNS": "8.8.8.8",
    "Quad9_DNS": "9.9.9.9",
    "CloudFlare_DNS": "1.1.1.1",
    // The name/key for the next one is used for quality calculations, so don't change it.
    // But *DO* replace the IP with the DNS server you use in your home network.
    "My_DNS_Server": "8.8.8.8"
  },
  // Optional — overrides any of the scoring defaults
  "Score": {
    "LossThreshold": 5,
    "LossWeight": 0.60,
    "LatencyThreshold": 100,
    "LatencyWeight": 0.15,
    "JitterThreshold": 30,
    "JitterWeight": 0.20,
    "DnsThreshold": 100,
    "DnsWeight": 0.05
  }
}
```

### Environment variables

Every option can be set via an environment variable prefixed with `NETPROBE_`. Nested options use `__` (double underscore) as the separator, and collection elements are addressed by key/index.

```bash
NETPROBE_ProbeIntervalSec=60
NETPROBE_ProbeCountPerSite=20
NETPROBE_DnsTestSite=cloudflare.com

# Arrays are indexed from 0
NETPROBE_Sites__0=google.com
NETPROBE_Sites__1=github.com

# Dictionaries are keyed by name
NETPROBE_DnsResolvers__Google_DNS=8.8.8.8
NETPROBE_DnsResolvers__My_DNS_Server=192.168.1.1

# Nested Score object
NETPROBE_Score__LatencyThreshold=80
NETPROBE_Score__LatencyWeight=0.25
```

### Command-line arguments

The same options can be passed as arguments. Use `:` (or `__`) for nesting and `key`/index for collection members:

```bash
./NetprobeSharp \
  --ProbeIntervalSec=60 \
  --ProbeCountPerSite=20 \
  --Sites:0=google.com \
  --Sites:1=github.com \
  --DnsResolvers:My_DNS_Server=192.168.1.1 \
  --Score:LatencyThreshold=80
```

Note that arrays set this way are merged on top of (not a replacement for) whatever the JSON/env sources already defined, since they bind per-index.

## Metrics

NetprobeSharp exposes its results as Prometheus metrics over an ASP.NET Core endpoint (provided by `OpenTelemetry.Exporter.Prometheus.AspNetCore`). By default, they are scrapeable at:

```
http://localhost:9464/metrics
```

A liveness endpoint is served on the same port:

```
http://localhost:9464/health
```

The server is bound to `http://+:9464` (all interfaces, port `9464`), so it's reachable from other machines and containers out of the box — no configuration needed. To listen elsewhere, set the standard `ASPNETCORE_URLS` environment variable (e.g. `ASPNETCORE_URLS=http://+:8080`).

Metrics follow Prometheus naming conventions: base units (seconds, ratios `0`–`1`) and unit-suffixed names. The published metrics are:

| Metric | Type | Labels | Description |
| --- | --- | --- | --- |
| `netprobe_ping_rtt_seconds` | histogram | `target` (site) | Round-trip time of each ping reply, in seconds. Use `histogram_quantile()` for percentiles. |
| `netprobe_ping_jitter_seconds` | gauge | `target` (site) | Per-site jitter (RTT population stddev / `mdev`), in seconds. |
| `netprobe_ping_packet_loss_ratio` | gauge | `target` (site) | Per-site packet loss as a ratio `0`–`1`. |
| `netprobe_ping_up` | gauge | `target` (site) | `1` if the last ping probe returned a parseable summary, else `0`. |
| `netprobe_dns_query_duration_seconds` | histogram | `resolver` (resolver name) | Per-resolver DNS query latency, in seconds. |
| `netprobe_dns_up` | gauge | `resolver` (resolver name) | `1` if the last DNS probe received a reply, else `0`. |
| `netprobe_health_score` | gauge | — | Overall internet health score, `0`–`1` (see below). |
| `netprobe_build_info` | gauge | `version` | Constant `1`; exposes the build version as a label. |
| `netprobe_speedtest_latency_seconds` | gauge | — | Latency to the selected Ookla server, in seconds. Only recorded when `Speedtest.Enable = true`. |
| `netprobe_speedtest_download_speed_bytes_per_second` | gauge | — | Download throughput from the selected server, in **bytes** per second (not bits — multiply by 8 for bps). |
| `netprobe_speedtest_upload_speed_bytes_per_second` | gauge | — | Upload throughput to the selected server, in **bytes** per second. |
| `netprobe_speedtest_up` | gauge | — | `1` if the last speed test completed successfully, `0` if it failed or `Speedtest.Enable = false`. |
| `netprobe_speedtest_server_info` | gauge | `sponsor`, `location` | Always `1`. Identifies the currently selected Ookla server via labels. Updated on selection/reselection. |

The four numeric speedtest metrics carry no server label — each is always a single Prometheus time series. Server identity is exposed separately via `netprobe_speedtest_server_info` (the [info-metric pattern](https://www.robustperception.io/how-to-have-labels-for-machine-roles)), which can be joined in PromQL when needed:

```promql
netprobe_speedtest_download_speed_bytes_per_second
  * on() group_left(sponsor, location)
  netprobe_speedtest_server_info
```

### Querying latency percentiles

Because RTT and DNS latency are histograms, you compute percentiles in PromQL rather than reading a pre-baked value. For example, the 95th-percentile ping RTT per target over the last 5 minutes:

```promql
histogram_quantile(0.95, sum by (le, target) (rate(netprobe_ping_rtt_seconds_bucket[5m])))
```

The mean is `rate(netprobe_ping_rtt_seconds_sum[5m]) / rate(netprobe_ping_rtt_seconds_count[5m])`.

## Scoring

The health score starts at `1.0` (perfect) and subtracts a weighted penalty for loss, latency, jitter and DNS latency. Each component is normalized against its threshold and capped at its weight, so the worst possible score is `1 - (LossWeight + LatencyWeight + JitterWeight + DnsWeight)`:

```
score = 1
      - LossWeight    × min(1, avgLoss     / LossThreshold)
      - LatencyWeight × min(1, avgLatency  / LatencyThreshold)
      - JitterWeight  × min(1, avgJitter   / JitterThreshold)
      - DnsWeight     × min(1, myDnsLatency / DnsThreshold)
```

`avgLoss`, `avgLatency` and `avgJitter` are averaged across all `Sites`, while the DNS term uses only the latency of the `My_DNS_Server` resolver. With the default weights, a higher score is better, `1.0` means everything is well under its threshold, and a fully degraded connection bottoms out at `0.0`.

When a site is fully unreachable (100% loss) or `ping`'s output can't be parsed, the missing latency/jitter figures are treated as their configured thresholds **for the score calculation only**, so an outage still drags the score down instead of silently disappearing. Likewise, a resolver that doesn't answer within the timeout contributes `DnsThreshold` to the score.

This substitution is confined to the score — it does **not** leak into the metrics. A failed probe records `netprobe_ping_up` / `netprobe_dns_up` at `0` (with `netprobe_ping_packet_loss_ratio` at `1` for total loss), and the latency/jitter samples are simply absent rather than reported as a fake threshold value. That way a scraper can tell a genuine `100 ms` measurement apart from a timeout.
