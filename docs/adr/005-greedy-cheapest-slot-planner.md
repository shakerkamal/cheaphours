# ADR-005: Plan charging by taking the cheapest 15-minute slots before departure

- **Status:** Accepted
- **Date:** 2026-11-04
- **Decision ids:** D2, D4, D5

## Context

A goal asks for a target charge by a departure time. The planner must pick when to charge.
- Day-ahead prices come in 15-minute slots (D4): 96 a day, 92 or 100 on the daylight-saving days. They
  are published around 13:00 for the next day, so a plan made in the morning sees only part of the night.
- There is one car and one charger per goal (D2). The only power limit is the charger's maximum power.
- The vehicle reports its charge and battery capacity. The energy needed is the charge gap over the
  battery, divided by a charging efficiency (0.90 by default).
- Charging must end a safety margin (30 minutes by default) before departure.
- The plan must say what it costs and what charging straight away would have cost, so the user sees the
  savings.
- The planner runs in the API (a new goal), in the worker (new prices, charge drift) and thousands of
  times in property tests, so it must be fast, pure and deterministic.

## Decision

- The planner is a pure function in `CheapHours.Planning` (`GreedyPlanner.Plan`). It takes the time, the
  goal, the device limits and the known prices, reads no clock and does no I/O. It uses decimals only.
- **Every chosen slot charges at the charger's maximum power.** The number of slots is the smallest whose
  energy covers the need: `n = ceil(energy × 4 / power)`.
- **It takes the `n` cheapest priced slots in the window,** with ties broken by the earlier slot. Only
  when the priced slots run out does it add unpriced ones, earliest first, and mark the plan
  `pricesComplete: false`. When the window is shorter than `n`, it takes every slot and marks the target
  missed.
- **The arrival cost** is the cost of the window's first `n` slots, the same number of slots at the same
  power, so the savings compare like with like.
- **A plan is a list of slots, each with a power (D5).** Command dispatch later turns each slot into
  start, stop or set-power commands with a deterministic key per plan version and slot.

**Why greedy is exact here.** With one car at a flat power, every slot delivers the same energy. The
problem is then: choose `n` of the window's `m` slots to minimise the summed price. Any choice that
leaves out a cheaper slot for a dearer one can swap them and get cheaper, so the `n` cheapest slots are
optimal. Sorting `m` slots (at most 48 hours, 192 slots) costs microseconds.

## Alternatives considered

- **A linear or mixed-integer solver (OR-Tools, HiGHS).** Rejected for now: for this problem it returns
  the same answer as sorting, at the cost of a native dependency, slower tests and a model that is harder
  to explain. The "Deliberately leave out" list names it. A solver starts to pay off when constraints
  couple slots or cars:
  - a grid connection limit shared by several cars;
  - a minimum run length or a limit on start/stop cycles per night;
  - power that varies with the charge level (the taper near full);
  - mixing a heat pump's flexible load with the car's.
- **Variable power per slot** (for example half power in a cheap slot to stretch it). Rejected: with a
  flat price inside a slot, half power over two slots costs the average of their prices, never less than
  the cheaper one at full power. It also needs chargers and cars that follow fine power limits.
- **Charging in one continuous block** (the cheapest contiguous window). Rejected: it costs more whenever
  the cheap slots are not adjacent, which is common at night, and the charger can start and stop per
  slot.
- **Treating unpriced slots as expensive** (planning only with known prices and leaving the car short).
  Rejected: a car that misses its target is worse than one that charged at an unknown price. The plan
  charges early when it must, says so with `pricesComplete: false`, and re-plans when the prices arrive.

## Consequences

- The planner's rules are four properties that FsCheck checks over 1,000 generated cases each, including
  windows across both daylight-saving changes, negative prices and missing prices:
  - no slot ends after departure minus the margin;
  - no slot charges above the charger's power;
  - the energy is covered whenever the window allows it;
  - a fully priced plan never costs more than charging on arrival.
- A plan may deliver up to one slot's energy more than needed. The arrival cost charges the same number of
  slots, so the savings figure stays fair.
- Re-planning is cheap enough to run on every trigger. A re-plan that gives an equal result stores no new
  version, so duplicate triggers are harmless.
- Moving to a solver later changes `GreedyPlanner` only: the planner's input and result, the stored plan
  versions and the API stay as they are. The property tests stay as the solver's acceptance tests.
