# Optional: Grafana Loki

The daemon can push its own log, a periodic metrics line and the ESP32's status messages
to **your own** Grafana Loki. This is off by default, and the repository contains no
URL, tenant or token: you enter them yourself.

## Setup

1. Get a Loki push endpoint:
   - **Grafana Cloud**: in your stack's Loki details, use the URL (for example
     `https://logs-prod-xxx.grafana.net`), the numeric user/instance ID as Username, and
     an access-policy token with the `logs:write` scope. Auth mode `Basic`.
   - **Self-hosted Loki**: for example `http://loki:3100`, usually auth mode `None`. Set
     Tenant only if multi-tenancy is enabled (sent as `X-Scope-OrgID`).
2. Open **Observability…** from the tray menu, tick **Send to Grafana Loki**,
   and fill in URL, Auth mode, Username and Token.
3. Click **Test**. It pushes one test line and shows the HTTP status (Loki
   answers 204 on success) or the error.
4. Click **Save**. The change applies without a restart. Turning the sink off stops collection
   and discards anything still queued.

`/loki/api/v1/push` is appended to the URL only when it has no path, so a full push URL
or a reverse-proxy path also works. If you use credentials, use `https`. For a plain
`http` URL with credentials, the dialog asks before every **Test**, and before **Save**
when the sink is enabled.

### The token

The token is saved only as Windows DPAPI ciphertext (`TokenProtected` in
`%LocalAppData%\ResourceMonitorDaemon\settings.json`). Only the same Windows user account can
decrypt it (with a roaming or domain profile that can include other PCs), which
includes any program running as that user. Leaving the
token box empty keeps the saved token; **Clear** removes it. For automation, set
`RESMON_Observability__Loki__Token` instead (read at startup, like all
[environment variables](../README.md#configuration)): it takes precedence and is never
written to disk. A plain `Token` key in a JSON file is ignored with a warning. The token, the
`Authorization` header and the URL path are never logged.

## Settings

Section `Observability:Loki`; environment form `RESMON_Observability__Loki__<Name>`. The
Observability dialog edits the token and the first seven settings; the rest are JSON
or environment only.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Master switch |
| `Url` | empty | Loki base URL or full push URL (`http` or `https`, no user name or password in it) |
| `AuthMode` | `None` | `None`, `Basic` (Username + token) or `Bearer` (token) |
| `Username` | empty | Basic-auth user, e.g. the Grafana Cloud instance ID |
| `TenantId` | empty | Optional `X-Scope-OrgID` |
| `IncludeHostLabel` | `false` | Adds `host="<PC name>"` to every stream |
| `MetricsIntervalSeconds` | `10` | One metrics line per interval, 0–3600; `0` turns it off |
| `MinLevel` | `Information` | Lowest daemon log level forwarded (the normal `Logging` filter applies first) |
| `BatchMaxEntries` | `500` | Entries per push, 1–5000 |
| `QueueCapacity` | `5000` | Queued entries, 100–100000; when full, the oldest are dropped |
| `FlushIntervalMs` | `2000` | Maximum wait before a partial batch is sent, 100–60000 |
| `Gzip` | `true` | Compress the request body |

## Streams

All streams use fixed, low-cardinality labels, plus `host="<PC name>"` when
`IncludeHostLabel` is on.

| Labels | Content |
| --- | --- |
| `{app="resmon-daemon", kind="log", level="trace\|debug\|info\|warn\|error\|critical"}` | Daemon log lines at or above `MinLevel`, as `Category: message` |
| `{app="resmon-daemon", kind="metrics"}` | One JSON line per interval: `{"cpu":12.3,"mem":45,"mem_used_gb":7.21,"mem_total_gb":15.86,"mqtt":"connected"}`; `mqtt` is `connected`, `disconnected` or `paused` |
| `{app="resmon-device", kind="status"}` | The validated [`resmon/status`](protocol.md#device-status) JSON, schema fields only; burst of 3, then at most one per 5 s |

## What is sent, and privacy

Nothing is kept or sent until you enable the sink and enter a URL; lines logged during
startup are briefly buffered in memory and discarded if the sink is off. The one exception
is **Test**: it pushes a single fixed line (`Resource Monitor daemon: observability test
line`) to the URL in the dialog, with the credentials and host label entered there, even
while the sink is not enabled. Data only ever goes to the URL you entered.

Log lines are redacted before they are sent:

- per-user folders become `%LOCALAPPDATA%`, `%APPDATA%` or `%USERPROFILE%`, including
  their short 8.3 forms such as `C:\Users\ABCDEF~1`;
- the profile folder in any other absolute path (`C:\Users\<name>\…` or
  `\\server\Users\<name>\…`) becomes `<user>`. The name ends at the next path separator,
  quote, bracket, comma, semicolon or invalid file-name character, so a folder name that
  itself contains `' ( ) , ;` is only partly replaced. `Public`, `Default`,
  `Default User` and `All Users` are kept; relative paths and URLs are left alone;
- your Windows user name becomes `<user>` as a whole word, case-insensitively. If the
  name is a common word such as `admin`, that word is replaced in ordinary text too.
  Names shorter than 3 characters are not replaced in free text (that would mangle
  ordinary words), but paths containing them are still covered by the folder rules;
- the PC name becomes `<host>`, unless `IncludeHostLabel` is on; as with the user name,
  names shorter than 3 characters are not replaced in free text.

Apart from that the lines match `daemon.log`. They can contain the broker host name or
IP address you configured, including a `FallbackAddress` saved by the pairing wizard.
Pairing lines at Information level name the device as `PROV_RESMON_XXXXXX`, whose
suffix is the last three bytes of the ESP32's Wi-Fi MAC address. Pairing lines with the
device's Bluetooth and IP addresses are [Debug-level](daemon.md#logging-and-diagnostics),
so they are forwarded only if you lower both `MinLevel` and the `Logging` filter to
Debug. The device status never contains an SSID, password, IP or MAC address. Note that
any client on the LAN can publish to `resmon/status`, so the forwarded text fields
(`build` up to 64 characters, `wifi` and `prov` up to 32) can carry arbitrary text into
Loki.

The sink's own messages, such as failed pushes, stay in the local log.

## Reliability

Sampling and MQTT publishing never wait for Loki; the sampling loop only adds a metrics
entry to the queue.

- Network errors, timeouts and 5xx responses are retried with backoff (1 s doubling to
  60 s) for as long as the outage lasts. Entries are lost only if the bounded queue
  overflows (`QueueCapacity`, default 5000): it then drops the oldest entries and
  reports the count in the local log at most once a minute.
- 429 honours `Retry-After` (up to 60 s). After 3 throttled attempts a batch is split in
  half; a single entry throttled 3 times without `Retry-After` is dropped.
- A batch is at most 1 MiB of JSON as sent (raw UTF-8, so Chinese text is not inflated
  by escaping). A 413 response splits the batch; a single entry that is still too large
  is dropped.
- Other 4xx responses and redirects drop the batch, with one warning per status code.
  When Loki says `total ignored: N out of M`, only those N entries count as dropped.
  Loki rejects entries older than its `reject_old_samples_max_age` (about a week by
  default).
- 401, 403 and 404 also back off, so wrong credentials or a wrong URL are not retried
  every flush interval.
- On exit the daemon spends at most 2 s flushing
  (`ObservabilityService.ShutdownFlushBudget`). There is no disk buffer, so entries
  still queued then, or when the process is killed, are lost.

## Example LogQL queries

```logql
# Average CPU % over 5 minutes
avg_over_time({app="resmon-daemon",kind="metrics"} | json | unwrap cpu [5m])

# Peak memory %
max_over_time({app="resmon-daemon",kind="metrics"} | json | unwrap mem [5m])

# Share of metrics samples in the last hour with the ESP32 broker unreachable
sum(count_over_time({app="resmon-daemon",kind="metrics"} | json | mqtt="disconnected" [1h]))
  / sum(count_over_time({app="resmon-daemon",kind="metrics"} [1h]))

# Daemon warnings and errors
{app="resmon-daemon",kind="log",level=~"warn|error|critical"}

# Device free heap (lowest in 15 minutes) and Wi-Fi signal
min_over_time({app="resmon-device",kind="status"} | json | unwrap heap_free [15m])
avg_over_time({app="resmon-device",kind="status"} | json | unwrap rssi [5m])

# Device restarts (uptime reset) with firmware build
{app="resmon-device",kind="status"} | json | uptime_s < 60 | line_format "{{.build}} heap={{.heap_free}}"
```

Implementation details are in [daemon.md](daemon.md#optional-loki-sink).
