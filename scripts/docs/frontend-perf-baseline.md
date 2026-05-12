# Frontend Performance Baseline

Recorded: 2026-05-02

Test group: RAM Group (`aa942634-84bd-407f-bb87-28c8dec42ce3`)

## Test Timing

- Warm idle: captured after restart before opening the RAM group.
- After 1 open: captured after opening the RAM group once.
- After 5 opens: captured after five RAM group opens.
- After idle: captured 60 seconds after the five-open checkpoint.
- Frontend report: copied from the dev-only frontend perf overlay after five samples.

## Backend Memory Baseline

| Checkpoint | API WorkingSet MB | API Private MB | DB memory | CPU |
|---|---:|---:|---|---|
| Warm idle | 150.7 | 76.0 | 57.03 MiB | API 2.078125, DB 0.02% |
| After 1 open | 157.1 | 81.0 | 57.06 MiB | API 2.34375, DB 0.03% |
| After 5 opens | 197.4 | 118.4 | 57.39 MiB | API 3.265625, DB 0.00% |
| After 60s idle | 195.0 | 115.9 | 57.40 MiB | API 3.265625, DB 0.00% |

## Frontend Baseline

| Metric | Baseline |
|---|---:|
| Group details fetch ms p50 | 36.3 |
| Group open render ms p50 | 685.4 |
| Payload size KB p50 | 279.9 |
| Expenses returned / total | 600 / 600 |
| Settlements returned / total | 0 / 0 |
| Has more expenses | false |
| Has more settlements | false |

## Frontend Samples

| Run | Fetch ms | Render ms | Payload KB | Expenses | Settlements | More expenses | More settlements |
|---:|---:|---:|---:|---:|---:|---|---|
| 1 | 23.4 | 618.4 | 279.9 | 600 / 600 | 0 / 0 | false | false |
| 2 | 36.3 | 685.4 | 279.9 | 600 / 600 | 0 / 0 | false | false |
| 3 | 24.0 | 586.6 | 279.9 | 600 / 600 | 0 / 0 | false | false |
| 4 | 58.4 | 780.5 | 279.9 | 600 / 600 | 0 / 0 | false | false |
| 5 | 148.3 | 787.9 | 279.9 | 600 / 600 | 0 / 0 | false | false |

## Baseline Read

- API Private memory increased 42.4 MB after five RAM group opens.
- API Private memory remained 39.9 MB above idle after 60 seconds.
- DB memory changed by only 0.37 MiB from idle to peak.
- The frontend received all 600 expenses in the initial group details payload.
- Payload size was 279.9 KB p50 before API pagination.
- Group open render time was 685.4 ms p50 before API pagination.

## Comparison Table

| Metric | Baseline | After #31 | After #32 |
|---|---:|---:|---:|
| Group details fetch ms p50 | 36.3 | | |
| Group open render ms p50 | 685.4 | | |
| Payload size KB p50 | 279.9 | | |
| Expenses returned / total | 600 / 600 | | |
| Settlements returned / total | 0 / 0 | | |
| Has more expenses | false | | |
| Has more settlements | false | | |
| API WorkingSet idle MB | 150.7 | | |
| API WorkingSet peak after 5 loads MB | 197.4 | | |
| API WorkingSet after idle MB | 195.0 | | |
| API Private idle MB | 76.0 | | |
| API Private peak after 5 loads MB | 118.4 | | |
| API Private after idle MB | 115.9 | | |
| DB memory idle MiB | 57.03 | | |
| DB memory peak after 5 loads MiB | 57.40 | | |
