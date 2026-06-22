# NetprobeSharp

This project is meant to be a replacement for [netprobe_lite](https://github.com/plaintextpackets/netprobe_lite) after I had a few annoyances with it (code not being embedded into the Docker image, requirement of having 2~3 instances of the code running, needing docker and other issues).

It is not meant to be a comprehensive networkign tool like [SmokePing](https://oss.oetiker.ch/smokeping/) and similar. I only made a rewrite of netprobe_lite in C# since it's the language I knew best, but I still know almost nothing of networking. I originally tried to hand-roll the ICMP sockets in C# to avoid shelling out to `ping`, but it grew too complex to justify, so the prober just runs the system `ping` and parses its summary — which still reports sub-millisecond RTTs, unlike `System.Net.NetworkInformation.Ping`.

## What it does

On a fixed interval, NetprobeSharp:

1. Pings every site in [`Sites`](#configuration-reference) and records average latency, packet loss and jitter per site.
2. Sends a recursive DNS query for [`DnsTestSite`](#configuration-reference) to every resolver in [`DnsResolvers`](#configuration-reference) and records the round-trip latency per resolver.
3. Combines those into a single internet health score.

All of this is published as [Prometheus metrics](#metrics) for you to scrape and graph.

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

## Configuration

Configuration comes from three sources, applied in this order — each one overrides the ones before it:

1. **`netprobe.jsonc`** — a JSON (with comments) file next to the executable. Optional, and reloaded automatically when it changes.
2. **Environment variables** prefixed with `NETPROBE_`.
3. **Command-line arguments**.

So a command-line argument beats an environment variable, which beats the JSON file. All options live at the root of the configuration; there is no wrapping section.

### Where `netprobe.jsonc` is loaded from

By default the file is read from the directory containing the executable. Set the `NETPROBE_ConfigPath` environment variable to point at a different directory (it must contain a `netprobe.jsonc`). This is the one setting that is *only* an environment variable — it's read before configuration binding to decide where to look.

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
| `Score.LossThreshold` | double | `5` | Packet loss % treated as the worst case (caps the loss contribution). |
| `Score.LossWeight` | double | `0.60` | Weight of packet loss in the score. |
| `Score.LatencyThreshold` | double | `100` | Ping latency (ms) treated as the worst case. |
| `Score.LatencyWeight` | double | `0.15` | Weight of ping latency in the score. |
| `Score.JitterThreshold` | double | `30` | Jitter (ms) treated as the worst case. |
| `Score.JitterWeight` | double | `0.20` | Weight of jitter in the score. |
| `Score.DnsThreshold` | double | `100` | DNS latency (ms) treated as the worst case. |
| `Score.DnsWeight` | double | `0.05` | Weight of DNS latency in the score. |

Configuration is validated on startup; if anything is missing or invalid (e.g. no `Sites`, a bad IP, or a missing `My_DNS_Server` resolver) the app refuses to start and logs the errors.

### `netprobe.jsonc`

This is the recommended way to configure NetprobeSharp. A starter file ships with the build:

```jsonc
{
  // General
  "ProbeIntervalSec": 30,
  // Ping
  "ProbeCountPerSite": 50,
  "Sites": [
    "google.com",
    "facebook.com",
    "twitter.com",
    "youtube.com",
    "amazon.com"
  ],
  // DNS
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

NetprobeSharp exposes its results as Prometheus metrics over an HTTP listener (provided by `OpenTelemetry.Exporter.Prometheus.HttpListener`). By default they are scrapeable at:

```
http://localhost:9464/metrics
```

The host and port are read by the exporter from the standard OpenTelemetry environment variables (note: these are *not* `NETPROBE_`-prefixed):

```bash
OTEL_EXPORTER_PROMETHEUS_HOST=0.0.0.0   # default: localhost
OTEL_EXPORTER_PROMETHEUS_PORT=9464      # default: 9464
```

Set `OTEL_EXPORTER_PROMETHEUS_HOST=0.0.0.0` if you need to scrape the metrics from another machine or container.

Three gauges are published:

| Metric | Labels | Description |
| --- | --- | --- |
| `Network_Stats` | `type` (`loss` / `latency` / `jitter`), `target` (site) | Per-site packet loss (%), average latency (ms) and jitter (ms). |
| `DNS_Stats` | `server` (resolver name) | Per-resolver DNS query latency (ms). |
| `Health_Stats` | — | Overall internet health score, `0`–`1` (see below). |

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

When a site is fully unreachable (100% loss) or `ping`'s output can't be parsed, that probe is recorded at the configured thresholds rather than dropped, so an outage still drags the score down instead of silently disappearing. Likewise, a resolver that doesn't answer within the timeout is recorded at `DnsThreshold`.
