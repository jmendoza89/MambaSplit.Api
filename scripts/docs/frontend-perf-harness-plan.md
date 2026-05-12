# Frontend Performance Harness - Implementation Plan

Relates to: MambaSplit.Api #30, #31, #32
See also: [frontend-pagination-plan.md](frontend-pagination-plan.md) for the UI changes that depend on the new API contract.

This plan covers the dev-only frontend overlay and the backend/container measurements needed to prove that the API fixes reduce both user-visible latency and RAM pressure.

## 1. Goals

- Capture baseline metrics before API issue #31 lands.
- Verify the group details payload shrinks after API pagination.
- Verify the browser renders fewer rows on initial group load.
- Track API and DB container memory separately so backend RAM improvements are not inferred from frontend metrics.
- Keep all instrumentation dev-only and out of production builds.

## 2. Dev-Only Performance Overlay

Render only when `import.meta.env.DEV` is true. The overlay should be a small collapsible fixed panel in the bottom-right corner, above any bottom navigation.

### Metrics

| Metric | Source |
|---|---|
| API fetch time (ms) | `performance.now()` around the group details request |
| Payload size (KB) | `Content-Length`, or cloned response body byte length fallback |
| Initial expenses returned | `details.expenses.length` |
| Total expenses in group | `details.summary.expenseCount` |
| Initial settlements returned | `details.settlements.length` |
| Total settlements in group | `details.summary.settlementCount` |
| Group open render time (ms) | Route/fetch start to first frame after final initial row render |
| Has more expenses | `details.hasMoreExpenses` |
| Has more settlements | `details.hasMoreSettlements` |

Use route/fetch-relative timing, not `navigationStart`, because this is a single-page app and navigation time may include unrelated previous activity.

### Data Shape

File: `src/stores/devPerfStore.ts`

```ts
interface DevPerfMetrics {
  fetchMs: number;
  payloadKb: number;
  renderMs: number | null;
  expensesReturned: number;
  expensesTotal: number;
  settlementsReturned: number;
  settlementsTotal: number;
  hasMoreExpenses: boolean;
  hasMoreSettlements: boolean;
  measuredAt: string;
}
```

File: `src/components/dev/PerfOverlay.tsx`

- Read from the dev perf store.
- Render nothing outside dev mode.
- Show empty/placeholder values before the first group details fetch.
- Update on every group details fetch.

### Fetch Instrumentation

The API client needs access to the raw `Response` before JSON parsing. If the existing client hides the raw response, add a small dev-only metadata hook rather than duplicating the request path.

Payload size order:

1. Use `Content-Length` when present.
2. Fall back to `response.clone().text()` and measure encoded bytes with `new Blob([text]).size`.
3. Do not use `response.text()` directly before JSON parsing because it consumes the body.

If the frontend and API are cross-origin locally, the API must expose `Content-Length` through CORS for the primary path to work.

### Render Timing

- Set `groupOpenStartedAt = performance.now()` immediately before the group details fetch.
- Set `renderMs` after the initial expense and settlement lists have rendered.
- Use `useEffect` plus `requestAnimationFrame` in the group page or list parent, not in an individual row if the list can be empty.

## 3. Backend RAM Measurement

Frontend metrics are not enough to prove issue #30. Record API and DB memory separately.

Test group: RAM Group (`aa942634-84bd-407f-bb87-28c8dec42ce3`) - 600 expenses, 2 members.

Use the actual container names from `docker ps`. Example:

```powershell
docker stats mambasplit_api mambasplit_db --no-stream --format "table {{.Name}}\t{{.MemUsage}}\t{{.CPUPerc}}"
```

Capture memory at these points:

1. After local stack is warm and idle.
2. Immediately after opening the RAM group once.
3. After 5 repeated RAM group opens.
4. After waiting 30 seconds idle.

If the API is run directly instead of Docker, use `dotnet-counters` as an optional deeper check:

```powershell
dotnet-counters monitor --process-id <api-pid> System.Runtime Microsoft.AspNetCore.Hosting
```

Useful counters:

- GC heap size
- Allocation rate
- Gen 2 GC count
- Request rate
- Current requests

## 4. Baseline Checklist

Record p50 over 5 loads unless noted otherwise.

| Metric | Baseline | After #31 | After #32 |
|---|---:|---:|---:|
| Group details fetch ms | | | |
| Group open render ms | | | |
| Payload size KB | | | |
| Expenses returned / total | 600 / 600 | 50 / 600 | 50 / 600 |
| Settlements returned / total | all / all | 50 / all | 50 / all |
| API memory idle MB | | | |
| API memory peak after 5 loads MB | | | |
| API memory after 30s idle MB | | | |
| DB memory idle MB | | | |
| DB memory peak after 5 loads MB | | | |

Post completed values in issue #31 when validating the critical API fix.

## 5. Files to Create or Modify in mambasplit-web

| File | Action |
|---|---|
| `src/components/dev/PerfOverlay.tsx` | Create dev-only overlay |
| `src/stores/devPerfStore.ts` | Create lightweight metrics store |
| `src/pages/GroupPage.tsx` or equivalent | Record fetch, payload, and render metrics |
| API client group details function | Expose payload size metadata for dev instrumentation |

The harness can be built before the API fix lands. Before #31, it should show `600 / 600` expenses for the RAM group. After #31, it should show `50 / 600` with `hasMoreExpenses = true`.
