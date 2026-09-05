# POS Report Pipeline

A serverless pipeline that receives aircraft position (POS) reports over HTTP,
stores the raw message, parses it into a structured record, calculates how much
longer the flight has to run and how much fuel it should land with, and serves
the latest status for any flight.

Four C# Lambdas, wired together with AWS CDK in TypeScript. Every resource is
created by CDK; nothing is configured by hand in the console.

![Architecture](docs/architecture.png)

## How the pieces fit together

The pipeline is four stages joined by S3 and SQS rather than by direct calls.
Each stage does one thing and hands off durably, so a failure in one never
loses work done by an earlier one.

| # | Stage | Trigger | What it does |
|---|---|---|---|
| 1 | **IngestApi** | `POST /pos-reports` | Validates the message, derives the flight ID, writes the raw text to `pos/`, returns `RECEIVED` |
| 2 | **Parser** | S3 `ObjectCreated` on `pos/` | Parses the message, writes JSON to `attachment/`, indexes it in the **pos-reports** table, queues the flight |
| 3 | **Calculator** | SQS message | Loads the report, computes remaining time and arrival fuel, writes JSON to `results/`, indexes it in the **results** table |
| 4 | **StatusApi** | `GET /status/{flightId}` | Returns the newest state known for the flight |

The API answers immediately and the rest happens asynchronously. A status
lookup straight after submitting may return `404` or the earlier `PARSED`
state; that is the pipeline being asynchronous, not a fault.

**Why S3 holds the payloads and DynamoDB holds only pointers.** Each table row
carries `flightId`, `timestamp` and an `attachment` key. Reports and results
are unbounded in a way that key-value rows are not, and S3 is far cheaper per
byte, so DynamoDB is used for what it is good at — an ordered index per flight
— and S3 for the documents themselves.

## The message format

```
POS/{flightNumber}.FR {departure}/TO {destination}/{ddHHmm}/{position}/{groundSpeedKnots}/{fuelOnBoardKg}/{fuelFlowKgPerHour}
```

Example:

```
POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800
```

Flight UL204, Yangon to Bangkok, day 04 at 12:05 UTC, position N16°42.3'
E096°12.5', 450 knots, 12500 kg on board, burning 2800 kg/h.

### Position

The latitude and longitude run together with no separator. They are told apart
by digit count: **latitude has two degree digits, longitude has three.**

| Field | Raw | Degrees | Minutes | Calculation | Decimal |
|---|---|---|---|---|---|
| Latitude | `N1642.3` | 16 | 42.3 | 16 + 42.3/60 | 16.705 |
| Longitude | `E09612.5` | 096 | 12.5 | 96 + 12.5/60 | 96.2083 |

`S` and `W` produce the same magnitude, negated.

### Flight ID

```
flightNumber + flightDate + departure + destination      (no separators)
UL204        + 20260904   + RGN       + BKK              = UL20420260904RGNBKK
```

The message carries a day but no year or month, so both come from **when the
API received the request**.

### Timestamp

`041205` is day 04, hour 12, minute 05. Combined with the receipt year and
month it becomes `2026-09-04T12:05:00Z`.

## API

### `POST /pos-reports`

Body is the raw report as `text/plain`.

```bash
curl -X POST "$API_URL/pos-reports" \
  -H 'Content-Type: text/plain' \
  --data 'POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800'
```

```json
{ "flightId": "UL20420260904RGNBKK", "status": "RECEIVED" }
```

Returns `202 Accepted`, because the work has been accepted but not finished.
`400` with a reason if the body is missing, oversized, or malformed.

### `GET /status/{flightId}`

```bash
curl "$API_URL/status/UL20420260904RGNBKK"
```

Once the calculator has run:

```json
{
  "flightId": "UL20420260904RGNBKK",
  "status": "CALCULATED",
  "timestamp": "2026-09-04T12:06:15.842Z",
  "remainingFlightTimeMinutes": 43,
  "estimatedFuelAtArrivalKg": 10513,
  "lowFuelWarning": false
}
```

In the moment before that, the report is stored but not yet calculated:

```json
{
  "flightId": "UL20420260904RGNBKK",
  "status": "PARSED",
  "timestamp": "2026-09-04T12:05:00Z",
  "message": "Report stored; calculation has not completed yet."
}
```

`404` if nothing is known about the flight at all.

## Stored objects

**`attachment/{flightId}-{reportTimestamp}.json`** — the parsed report:

```json
{
  "flightId": "UL20420260904RGNBKK",
  "flightNumber": "UL204",
  "departure": "RGN",
  "destination": "BKK",
  "timestamp": "2026-09-04T12:05:00Z",
  "latitude": 16.705,
  "longitude": 96.2083,
  "groundSpeedKnots": 450,
  "fuelOnBoardKg": 12500,
  "fuelFlowKgPerHour": 2800
}
```

**`results/{flightId}-{calculatedAt}.json`** — the calculation, carrying its own
inputs so it is self-contained:

```json
{
  "flightId": "UL20420260904RGNBKK",
  "timestamp": "2026-09-04T12:06:15.842Z",
  "input": {
    "currentLatitude": 16.705,
    "currentLongitude": 96.2083,
    "destination": "BKK",
    "groundSpeedKnots": 450,
    "fuelOnBoardKg": 12500,
    "fuelFlowKgPerHour": 2800
  },
  "remainingFlightTimeMinutes": 43,
  "estimatedFuelAtArrivalKg": 10513,
  "lowFuelWarning": false
}
```

Both DynamoDB tables use `flightId` as the partition key and `timestamp` as the
sort key, and store the S3 key in `attachment`.

## The calculation

Great-circle distance to the destination airport by the haversine formula, then
distance over ground speed:

```
distanceNm                = haversine(current position, destination)
remainingFlightTimeHours  = distanceNm / groundSpeedKnots
estimatedFuelAtArrivalKg  = fuelOnBoardKg - (fuelFlowKgPerHour x remainingFlightTimeHours)
```

Worked through with the example message:

| Step | Value |
|---|---|
| Distance RGN position → BKK | 319.369 nm |
| Remaining flight time | 0.70971 h → **43 min** |
| Estimated fuel at arrival | 12500 − (2800 × 0.70971) → **10513 kg** |

Negative arrival fuel sets `lowFuelWarning`, is stored on the results row, and
is logged at warning level.

This is a deliberately simplified model — a spherical earth, no wind, no
altitude, constant ground speed and burn. Real flight planning uses none of
those assumptions.

Destination coordinates come from a hardcoded IATA lookup in
`Geo/AirportCatalog.cs` (RGN, BKK and SIN as specified, plus around twenty more
so test messages have somewhere to fly).

## Project structure

```
pos-report-pipeline/
├── README.md
├── build.sh                          # publishes the Lambda bundles CDK uploads
├── smoke-test.sh                     # end-to-end check against a deployed stack
├── docs/
│   ├── architecture.png              # diagram
│   └── architecture.py               # script that generates the diagram
│
├── cdk/                              # TypeScript CDK app
│   ├── bin/
│   │   └── app.ts                    # CDK entry point
│   ├── lib/
│   │   └── pos-pipeline-stack.ts     # the whole stack
│   ├── test/
│   │   └── pos-pipeline-stack.test.ts  # CDK assertions
│   ├── cdk.json
│   ├── package.json
│   └── tsconfig.json
│
└── src/                              # C# Lambda code
    ├── PosReportPipeline.sln
    │
    ├── PosReportPipeline.Shared/     # library shared by the Lambdas
    │   ├── Models/
    │   │   ├── ParsedPosReport.cs
    │   │   └── CalculationResult.cs
    │   ├── Parsing/
    │   │   └── PosReportParser.cs    # format parse + lat/lon conversion + S3 key formats
    │   ├── Geo/
    │   │   ├── Haversine.cs
    │   │   └── AirportCatalog.cs     # hardcoded airport lookup
    │   └── PosReportPipeline.Shared.csproj
    │
    ├── PosReportPipeline.IngestApi/  # Lambda 1: POST /pos-reports
    │   ├── Function.cs
    │   └── PosReportPipeline.IngestApi.csproj
    │
    ├── PosReportPipeline.Parser/     # Lambda 2: S3 event trigger
    │   ├── Function.cs
    │   └── PosReportPipeline.Parser.csproj
    │
    ├── PosReportPipeline.Calculator/ # Lambda 3: SQS trigger
    │   ├── Function.cs
    │   └── PosReportPipeline.Calculator.csproj
    │
    ├── PosReportPipeline.StatusApi/  # Lambda 4: GET /status/{flightId}
    │   ├── Function.cs
    │   └── PosReportPipeline.StatusApi.csproj
    │
    └── PosReportPipeline.Tests/      # xUnit — local unit tests
        ├── PosReportParserTests.cs
        ├── HaversineTests.cs
        ├── FlightIdTests.cs
        ├── CalculatorTests.cs
        ├── AirportCatalogTests.cs
        ├── PosObjectKeysTests.cs
        └── PosReportPipeline.Tests.csproj
```

`PosReportPipeline.Shared` has **no AWS dependencies at all** — no `AWSSDK.*`,
no `Amazon.Lambda.*`. All the logic worth testing lives there, which is why the
unit tests need no mocks.

## Running the tests locally

No AWS account and no credentials needed. This is where the parsing, the
haversine calculation and the fuel maths are checked.

```bash
cd src
dotnet test PosReportPipeline.sln
```

The CDK assertions run separately and also need no AWS account:

```bash
cd cdk
npm install
npm test
```

There is no local emulator for the AWS wiring. `smoke-test.sh` covers that
against a deployed stack — see below.

## Deploying

Prerequisites: .NET 8 SDK, Node.js 18+, AWS credentials, and a bootstrapped
account.

```bash
# 1. publish the four Lambda bundles CDK uploads
./build.sh

# 2. deploy
cd cdk
npm install
npx cdk bootstrap        # first time in this account/region only
npx cdk deploy
```

`cdk deploy` prints the API URL and the resource names:

```
Outputs:
PosReportPipelineStack.ApiUrl = https://xxxx.execute-api.eu-west-1.amazonaws.com/prod/
```

`build.sh` must run before any `cdk` command. The stack fails fast with a clear
message if the bundles are missing, rather than uploading empty assets that
only break on the first invocation.

### End-to-end check after deploying

```bash
./smoke-test.sh
```

It finds the API URL from the CloudFormation stack, or takes one as an
argument. Sixteen checks covering the rejections, the worked example, the
low-fuel warning, and the permanent-failure path — including that an unknown
destination leaves the DLQ empty. Exits non-zero on any failure, so it works in
CI.

```
1. Rejections (400 before anything is stored)
  PASS  empty body
  ...
5. Unknown destination is a permanent failure, not a retry
  PASS  report still readable as PARSED
  PASS  DLQ stayed empty
-------------------------------------------
passed 16, failed 0
```

By hand, if you prefer:

```bash
export API_URL="https://xxxx.execute-api.ap-southeast-1.amazonaws.com/prod"

curl -X POST "$API_URL/pos-reports" -H 'Content-Type: text/plain' \
  --data 'POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800'
# -> { "flightId": "UL20420260904RGNBKK", "status": "RECEIVED" }

curl "$API_URL/status/<flightId from above>"
```

Use the `flightId` the POST returns rather than the one above: the flight date
takes its year and month from when the request arrives, so it follows today's
date, not September 2026.

### Tearing down

```bash
npx cdk destroy
```

The bucket and both tables are destroyed with the stack so a reviewer is not
left with orphaned resources. Deploy with `-c retainData=true` to keep them,
which is what a real environment would use.

## Key decisions and trade-offs

**The API parses the whole message, not just its shape.** The brief asks for a
rough well-formedness check, and there is one (`IsRoughlyWellFormed`). But
deriving the flight ID needs the flight number, day, departure and destination
— most of the message — so the full parse costs almost nothing extra. It makes
`202` a real promise: anything accepted will parse downstream, and a caller with
a bad message finds out immediately instead of by polling a status that never
advances.

**The parser recovers the receipt time from the S3 key rather than reading the
clock.** The flight date is built from the year and month at receipt. If the
parser called `UtcNow` again, a report received at 23:59:59 on 30 September and
parsed a second later would be filed under October — a different flight ID from
the one the caller was already given, with no error anywhere. So the ingest
timestamp is embedded in the key (`pos/{flightId}-{receivedAt}`) and read back
out. Parsing is a pure function of the message and that timestamp, which also
makes replaying an old object deterministic. `FlightIdTests` pins this.

**Failures are classified before they are handled.** The rule throughout is
*retry only what can succeed on retry*:

| Failure | Kind | Behaviour |
|---|---|---|
| Malformed body at ingest | permanent | `400`, nothing stored |
| Unparseable message in the parser | permanent | logged at error, invocation succeeds — the raw object stays under `pos/` and can be replayed after a parser fix |
| Unreadable SQS body | permanent | logged and dropped |
| Destination not in the catalog | permanent | logged and dropped — no amount of retrying adds an airport to a hardcoded table |
| Missing report row | transient | retried, then the DLQ |
| DynamoDB throttle, S3 5xx | transient | reported as a batch item failure, retried, then the DLQ |

Treating permanent failures as retryable is what turns a DLQ into noise. Here
only genuinely transient problems reach it, so anything in the DLQ is worth
looking at.

**Ground speed of zero is rejected at parse time.** The calculator divides by
it. Catching it at the edge keeps an undefined result from becoming a poison
message.

**Partial batch failures are enabled on the SQS trigger.** Without
`reportBatchItemFailures`, one bad message in a batch of ten re-drives the nine
good ones alongside it, and they get processed repeatedly.

**The S3 trigger is scoped to `pos/`.** The parser writes to `attachment/` and
the calculator to `results/` in the same bucket. An unfiltered notification
would make the pipeline trigger itself in a loop.

**IAM is written as explicit statements, not CDK `grant*` helpers.** The helpers
are convenient but generous — `grantWriteData` on a table also allows
`UpdateItem`, `DeleteItem` and `BatchWriteItem`, and `grantRead` on a bucket
adds `ListBucket`. Each function gets exactly the actions it calls on exactly
the prefixes it touches. Each also has its own role and its own log group,
instead of the `AWSLambdaBasicExecutionRole` managed policy that allows writing
to every log group in the account. The test suite asserts no statement contains
a wildcard action or resource.

**The object is written before the row that points at it,** in both the parser
and the calculator. The reverse order would leave rows pointing at objects that
do not exist. A crash between the two leaves an orphaned object, which is
harmless and invisible to readers.

**The calculator reads the report with `ConsistentRead`.** The parser writes the
row before sending the message, but an eventually-consistent read can still miss
it. A consistent read removes the race instead of relying on retries to hide it.

**Logging is events and errors, not steps.** One line per report received,
parsed and calculated, carrying the flight ID; errors with the reason; a warning
for low fuel. No entry/exit tracing.

## Assumptions

- **The position format follows the worked example, `N1642.3E09612.5`, with the
  hemisphere letters first.** The format line in the brief reads
  `{lat}{N|S}{lon}{E|W}`, which puts them last. Rather than guess which one real
  traffic uses, the parser accepts both; there is a test for each.
- **`202 Accepted` rather than `200 OK`** for a submission, since the work is
  accepted but not complete.
- **`GET /status` includes a `status` field** (`CALCULATED` or `PARSED`) beyond
  the response shown in the brief, so a caller can tell a finished calculation
  from a report that is still in flight without comparing timestamps.
- **A report whose day does not exist in the receipt month is rejected** — day 31
  received in February, for instance.
- **Latitude and longitude are stored to 4 decimal places,** matching the
  attachment example in the brief. That is roughly 11 m, against the report's
  own 0.1-minute (~185 m) resolution, so no real precision is lost.
- **Reports are treated as immutable.** Re-sending the same report for the same
  flight and minute overwrites the same row, which makes retries idempotent.
- **The API is unauthenticated.** Real traffic would need authentication and
  rate limiting; both were left out as outside the scope of the exercise.
- **No pagination on status.** The brief asks only for the most recent state.
  The tables are keyed to serve a full flight track by `flightId`, so adding it
  later is a query change and no data migration.

## What I would add next

Rough order of value, none of it required by the brief:

- Authentication on both routes, and a WAF or usage plan in front of the API.
- Alarms on DLQ depth, on Lambda errors, and on `lowFuelWarning` — nothing
  currently notices when a flight is projected to run dry.
- A `GET /flights/{flightId}/track` returning the full ordered set of reports;
  the table already supports it.
- The airport catalog moved into DynamoDB behind the same interface, so an
  unknown airport becomes a data fix rather than a deployment.
- An integration test running against a deployed stack, to cover the wiring the
  unit tests deliberately do not.
