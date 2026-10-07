# 02 — Capacity and sizing

| Item | Value |
|---|---|
| Document | ClockInXtra — measured capacity, and how to size a deployment |
| Harness | `tests/Attendance.LoadTest` |
| Measured | 2026-09-20, on a development workstation |
| Status | **Indicative.** Production numbers require a run on the production servers |

---

## 1. The only load that matters

Attendance has one shape of load: everybody arrives at once. A thousand people
spread over a working day is nothing; the same thousand arriving between 07:45
and 08:00 is the system's whole design problem. Everything below is about that
quarter of an hour.

Run the harness and read the numbers rather than guessing:

```bash
dotnet run --project tests/Attendance.LoadTest -- --employees 300 --concurrency 32
```

It provisions virtual employees with real credentials, authenticator secrets and
device keys, clocks them all in through the real signed pipeline, prints
throughput and latency, and then removes everything it created and puts every
setting back. **Never point it at production**: it creates employees, widens the
clock-in window while it runs, and leaves audit entries that cannot be removed.

---

## 2. What sets the ceiling

Every clock-in verifies a password with **PBKDF2-HMAC-SHA512 at 220,000
iterations**. That cost is deliberate — it is what makes a stolen credential
table expensive to attack — and it is the largest fixed piece of CPU in the
request, so it sets the per-core ceiling before anything else does.

Measured on the development workstation: **about 155 ms of CPU per
verification**, so roughly **6.5 clock-ins per second per core** as a hard
upper bound, before the database, TLS or anything else is considered.

The practical consequence: **clock-in capacity is a CPU question, not a database
question.** Adding application nodes adds capacity almost linearly. Adding
database hardware does not, until the node count is high enough for the database
to become the constraint.

---

## 3. Measured, end to end

150 clock-ins through the full pipeline — signature verification, device lookup,
nonce claim, password, TOTP, geodesic distance, transaction — in process against
a development database, on a 12-logical-processor workstation that was also
running SQL Server and the harness itself:

| Requests in flight | Throughput | Latency p50 | Latency p95 |
|---|---|---|---|
| 8 | 21.6 /s | 332 ms | 920 ms |
| 16 | 20.9 /s | 702 ms | 992 ms |
| 32 | 24.9 /s | 1,192 ms | 1,720 ms |

Two things to read from this:

- **Throughput plateaus at about 22–25 a second** on this machine, which is the
  CPU ceiling above shared with SQL Server and the harness. A dedicated node
  should do better.
- **Latency grows with concurrency while throughput does not.** Past the plateau,
  extra concurrent requests only queue. A node behind a load balancer should be
  given a connection limit rather than allowed to accept everything and make
  every employee wait.

---

## 4. Sizing

Required rate is headcount divided by the arrival window. Against a measured
**20 clock-ins per second per node** (deliberately rounded down from the numbers
above):

| Headcount | All arriving within | Needed per second | Nodes |
|---|---|---|---|
| 250 | 10 min | 0.4 | 1 |
| 500 | 10 min | 0.8 | 1 |
| 1,000 | 10 min | 1.7 | 1 |
| 2,500 | 10 min | 4.2 | 1 |
| 5,000 | 10 min | 8.3 | 1 |
| 10,000 | 5 min | 33.3 | 2 |

For any headcount this organisation is likely to have, **one application node
carries the morning peak, and a second exists for availability rather than
capacity** — which is what the reference topology already proposes (two API
nodes behind a load balancer).

What would change that:

- A much shorter arrival window: a single shift starting on the hour, with
  everybody clocking in within two or three minutes.
- Retries. A phone that fails and is tapped repeatedly multiplies the load;
  this is why the per-device rate limit exists.
- Raising the PBKDF2 iteration count, which is the same decision in reverse:
  more resistance to offline attack, less throughput per core.

---

## 5. Before production, re-measure

These numbers came from a workstation, in process, with the database on the same
machine. A production run differs in every direction: TLS and a reverse proxy
add work, the network adds latency, a server-grade CPU adds throughput, and a
dedicated database server removes the contention this measurement included.

Run the harness against a staging deployment that matches production, with
`--employees` set to the real headcount, and record the result here. Until then,
treat §3 and §4 as an order of magnitude and a method, not a promise.

Related: the arrival-window and headcount questions are open requirements 15–17
in `docs/architecture/01-requirements-register.md`. This document is what turns
an answer to those into a topology.
