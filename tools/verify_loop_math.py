"""Numerical research probes, not a Wallpaper Engine/GPU renderer.

These verify selected mathematical constructions used in the feasibility report.
They do not certify the appearance or full state of a real wallpaper.
"""

import json
import math
from pathlib import Path
import random


def main():
    rng = random.Random(431960)
    period = 20.0
    times = [rng.uniform(-40, 40) for _ in range(100)]

    def sine(t):
        return math.sin(2 * math.pi * 3 * t / period + 0.7)

    def multisine(t):
        return sum(weight * math.sin(2 * math.pi * k * t / period + phase)
                   for weight, k, phase in ((1, 3, 0.1), (0.3, -2, 0.7), (0.1, 1, 0.4)))

    def fixed_texture(u, v):
        return math.sin(2 * math.pi * u) * math.cos(2 * math.pi * v)

    def shine_texture(t):
        a = 2 / period
        return fixed_texture(0.12 + a*t, 0.37 + a*t) * fixed_texture(0.4 + a*t/2, 0.7 - a*t/2)

    def flow(phase):
        x, y = phase % 1 - 0.5, (phase + 0.5) % 1 - 0.5
        blend = 2 * abs(phase % 1 - 0.5)
        # An arbitrary fixed spatial sampling function, not a periodic source.
        return (1-blend)*math.exp(0.3+x*0.2) + blend*math.exp(0.3+y*0.2)

    # Motion Blur's scalar recurrence y[n] = (1-r)*y[n-1] + r*x[n].
    # Solve the cycle's initial history instead of resetting to an empty buffer.
    inputs = [rng.random() for _ in range(60)]
    rate = 0.17
    q = 1-rate
    initial = rate * sum(q**j * inputs[-1-j] for j in range(len(inputs))) / (1-q**len(inputs))
    state = initial
    for value in inputs:
        state = q*state + rate*value

    events = [(period-0.3, 1.2, 0.7, -0.2), (1.1, 2.4, 0.1, 0.4), (8.7, 0.9, -0.3, 0.2)]

    def particles(t):
        result = []
        for index, (birth, lifetime, origin, velocity) in enumerate(events):
            age = (t-birth) % period
            if age < lifetime:
                alpha = math.sin(math.pi*age/lifetime)**2
                result.append((index, origin+velocity*age, alpha))
        return result

    def particle_error(t):
        a, b = particles(t), particles(t+period)
        if [v[0] for v in a] != [v[0] for v in b]:
            return float("inf")
        return max((abs(x-y) for p, q in zip(a, b) for x, y in zip(p[1:], q[1:])), default=0)

    results = {
        "scope": "Scalar/source-formula probes only; no GPU, sampler, engine state or visual verification",
        "target_period_seconds": period,
        "synchronized_sine_max_error": max(abs(sine(t)-sine(t+period)) for t in times),
        "synchronized_multisine_max_error": max(abs(multisine(t)-multisine(t+period)) for t in times),
        "repeat_uv_shine_analogue_max_error": max(abs(shine_texture(t)-shine_texture(t+period)) for t in times),
        "waterflow_half_phase_analogue_max_error": max(abs(flow(t)-flow(t+0.5)) for t in times),
        "motionblur_solved_initial_state_error": abs(state-initial),
        "periodic_particle_event_max_error": max(particle_error(t) for t in times+[0, 1e-7, period-1e-7]),
        "previous_cycle_particle_alive_at_zero": any(p[0] == 0 for p in particles(0)),
        "unmatched_sine_time_modulo_boundary_jump": abs(math.sin(1.52*period)-math.sin(0)),
        "caveats": [
            "The multi-frequency probe changes frequencies to integer harmonics; it does not preserve the original exact motion.",
            "Particle example assumes lifetime shorter than T, deterministic independent trajectories, and matching birth/death fades.",
            "Motion blur derivation assumes fixed rate/mask, same-pixel sampling and mathematical real arithmetic.",
        ],
    }
    for name, value in results.items():
        if name.endswith("max_error") or name.endswith("state_error"):
            assert value < 1e-10, (name, value)
    assert results["previous_cycle_particle_alive_at_zero"]
    assert results["unmatched_sine_time_modulo_boundary_jump"] > 0.1
    output = Path(__file__).resolve().parents[1] / "output/census/loop-math-probes.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(results, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
