# Architecture

Export this diagram to `docs/architecture.png` (mermaid CLI, draw.io, or the
mermaid live editor) so the README image link resolves.

```mermaid
flowchart LR
    C[Client] -->|POST /pos-reports<br/>raw ACARS text| IG[IngestApi Lambda]
    IG -->|raw/yyyy/MM/dd/guid.txt| S3[(S3 bucket)]
    S3 -->|ObjectCreated| PR[Parser Lambda]
    PR -->|ParsedPosReport JSON| Q[[SQS queue]]
    PR -.->|unparseable| QU[(S3 quarantine/)]
    Q --> CA[Calculator Lambda]
    Q -.->|after 3 receives| DLQ[[SQS DLQ]]
    CA -->|CalculationResult| DB[(DynamoDB)]
    DB --> ST[StatusApi Lambda]
    ST -->|GET /status/flightId| C
```

## Why four functions

Ingest never blocks on parsing, and parsing never blocks on calculation.
A parser bug cannot lose data, because the raw text is already durable in S3
and can be replayed by re-firing the S3 event.
