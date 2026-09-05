# POS Report Pipeline — Design

**Date:** 2026-09-05
**Status:** Approved

## Purpose

Ingest aircraft position (POS) reports in ACARS-style text, parse them into
structured records, compute the great-circle distance and bearing from the
aircraft's current position to its destination airport, and expose the latest
position and recent track per flight over HTTP.

The pipeline is deliberately split into four Lambdas so that ingest never
blocks on parsing and parsing never blocks on calculation. Each stage fails
independently and retries independently.

## Architecture

```
POST /pos-reports ──► IngestApi ──► S3 (raw/)
                                      │ ObjectCreated
                                      ▼
                                    Parser ──► SQS ──► Calculator ──► DynamoDB
                                      │          │ (DLQ)                 ▲
                                      └► S3 (quarantine/)               │
                                                     GET /status/{id} ─ StatusApi
```

### AWS resources

| Resource | Purpose |
|---|---|
| S3 bucket | `raw/` holds verbatim submissions; `quarantine/` holds unparseable ones |
| SQS queue + DLQ | Decouples parsing from calculation; DLQ after 3 receives |
| DynamoDB table | One item per position report, queried by flight |
| API Gateway REST API | `POST /pos-reports`, `GET /status/{flightId}` |
| 4 Lambda functions | Ingest, Parser, Calculator, Status |

## Report format

```
POS
FLT/ABC123
DT/2026-09-05T14:20:00Z
PSN/N3722.5 W12205.8
ALT/FL350
DEST/KLAX
```

Rules:

- Line 1 must be exactly `POS` (case-insensitive, trimmed). Anything else is a
  parse failure.
- Every subsequent line is `KEY/VALUE`, split on the **first** `/` only.
  Unknown keys are ignored, so the format can grow without breaking parsers.
- `FLT` — callsign. Required. Uppercased on parse.
- `DT` — ISO-8601 instant, must be UTC. Required.
- `PSN` — `<lat> <lon>` in DDMM.M. Latitude is **two** degree digits with a
  leading `N`/`S`; longitude is **three** degree digits with a leading `E`/`W`.
  `N3722.5` → `37 + 22.5/60` = `37.375`.
  `W12205.8` → `-(122 + 5.8/60)` = `-122.09667`.
  Minutes must be `< 60`; latitude must be within ±90, longitude within ±180.
- `ALT` — either `FL350` (→ 35000 ft) or `2500FT` (→ 2500 ft). Required.
- `DEST` — 4-letter ICAO code. Required.

### Flight ID

`flightId = "{CALLSIGN}-{yyyyMMdd}"`, where the date is the **UTC date of the
`DT` field of that report**. So `ABC123` reporting at `2026-09-05T14:20:00Z`
belongs to flight `ABC123-20260905`.

A flight crossing UTC midnight therefore splits into two flight IDs. This is a
known and accepted consequence: deriving the ID from each report keeps the
parser stateless, and the alternative (anchoring to the first report seen)
requires a read-before-write on the hot path for a case that only affects
long-haul overnight sectors.

## Data flow

### 1. IngestApi — `POST /pos-reports`

Accepts the raw report as the request body (`text/plain`). Validates only that
the body is non-empty, is under 16 KB, and its first line is `POS`. Writes the
bytes verbatim to `raw/{yyyy}/{MM}/{dd}/{guid}.txt` and returns `202 Accepted`
with the S3 key.

It does **not** parse. Ingest stays fast, and a parser bug can never cause data
loss — the raw text is already durable in S3 and can be replayed.

### 2. Parser — S3 `ObjectCreated` on `raw/`

Reads the object, calls `PosReportParser.Parse`, and publishes the resulting
`ParsedPosReport` to SQS as JSON.

On parse failure it copies the object to `quarantine/` with the failure reason
in object metadata, logs at error level, and **returns success**. Throwing would
make S3/Lambda retry a message that can never succeed.

### 3. Calculator — SQS trigger

Batch size 10, with `ReportBatchItemFailures` partial batch responses so one bad
message does not re-drive the whole batch.

For each report: look up `DestinationIcao` in `AirportCatalog`, compute
great-circle distance (`Haversine`) and initial bearing from the current
position to the destination, and write a `CalculationResult` item.

### 4. StatusApi — `GET /status/{flightId}`

Queries DynamoDB for the flight. Returns the latest report plus the recent
track (default 50 points, `?limit=` to override, capped at 500).

## Data model

DynamoDB, single table:

- **PK** `flightId` (S) — e.g. `ABC123-20260905`
- **SK** `reportedAt` (S) — ISO-8601 instant

Every report is its own item. "Latest" is a query with
`ScanIndexForward=false, Limit=1`; the track is the same query unbounded and
already in chronological order. No GSI is required.

Item attributes: `flightId`, `reportedAt`, `callsign`, `latitude`, `longitude`,
`altitudeFeet`, `destinationIcao`, `destinationName`, `distanceToDestinationKm`,
`distanceToDestinationNm`, `initialBearingDegrees`, `calculationStatus`,
`calculatedAt`, `sourceKey`.

## Error handling

The governing rule: **retry only what can succeed on retry.**

| Failure | Classification | Behaviour |
|---|---|---|
| Empty body / bad first line at ingest | Permanent | `400`, nothing written |
| Unparseable report | Permanent | Copy to `quarantine/`, log, return success |
| Unknown destination ICAO | Permanent | Write the item with null distances and `calculationStatus = UNKNOWN_DESTINATION`; do **not** fail the message |
| DynamoDB throttle / S3 5xx | Transient | Report as batch item failure; SQS retries; DLQ after 3 receives |
| Unknown flightId at status | Not an error | `404` |

An unknown destination still produces a stored position record. The aircraft's
location is real data and useful on its own; discarding it because a lookup
table is incomplete would lose information the system already has.

## Scope decisions

**Excluded: progress percentage and ETA.** The report format carries `DEST` but
no origin and no ground speed. Both figures would require either inventing an
origin or deriving speed from consecutive reports — state the calculator does
not currently read. Adding them is a follow-on change once an `ORIG` field or a
read-previous step is justified.

**AirportCatalog is hardcoded** — an in-memory dictionary of roughly 35 major
international airports. It is a pure static lookup with no I/O, which keeps
`Haversine` and the calculator trivially unit-testable. Replacing it with a
DynamoDB-backed catalog is a contained change behind the same interface.

## Testing

`PosReportPipeline.Tests` (xUnit):

- **PosReportParserTests** — valid report; northern/southern and eastern/western
  hemispheres; `FL` and `FT` altitude forms; missing required field; bad first
  line; minutes ≥ 60; out-of-range coordinates; unknown keys ignored;
  non-UTC `DT` rejected.
- **HaversineTests** — known city pairs against published great-circle
  distances (±0.5 %); identical points → 0; bearing north/east/south/west;
  bearing normalised to `[0, 360)`.
- **FlightIdTests** — callsign uppercased; date taken from `DT` not from wall
  clock; UTC midnight boundary splits flights; whitespace trimmed.

`cdk/test` — CDK assertions covering resource counts, the S3 notification
wiring, the SQS redrive policy, and both API routes.

## Build and deploy

CDK reads each function's code from `src/publish/<ProjectName>`, produced by
`./build.sh`, which runs `dotnet publish -c Release` per Lambda project.
Running `build.sh` is a prerequisite to `cdk deploy`; the stack fails fast with
a clear message if a publish directory is missing.

Target runtime: .NET 8 (LTS), `Runtime.DOTNET_8`.
