#!/usr/bin/env python3
"""
CavernizeAndroid W60.2 Scarlett Gen4 Unbroken-Duplex Loopback Delta & `--compare-cables` Grader
(`scarlett_loopback_delta.py`).

Zero +1L growth on `run_audio_ground_truth.py` (`2567L` ratchet preserved). Pure Python stdlib DSP.
Implements BP_QA_V3_CHAOS_HEADROOM_AND_SCARLETT_DELTA:
- Unbroken-duplex 2.0s Farina ESS timeline (`[0.5s sil -> 2.0s DRY L/R -> 0.5s gap -> 2.0s FX L/R]`)
  eliminating HAL reopen jitter (`±240..1440` samples).
- Focusrite Scarlett 2i2 4th Gen safety pre-flight (`ClipSafe`, `DirectMonitor`, `48V`, `Air`, `INST`, `AutoGain` OFF)
  + `-40 dBFS` pilot tone check (catches clipping `>=-1 dBFS`, bleed `>-18 dBFS`, L/R swap, TS-on-TRS H2/H3 `>=-45 dB`).
- GCC-PHAT fractional subsample delay estimator (`<= 0.10 smp` accuracy).
- Detected-only 50/60 Hz + harmonics (`2f..6f`) mains hum notch mask (unperturbed clean spectrum).
- Strictly LEFT-multiplied 2x2 cable matrix deconvolution: `H_DSP(f) = M_cable(f)^-1 @ M_FX(f)`.
- `--compare-cables` empirical grader (`line_out_2xp10` vs `hp_out_ycable`) catching TS-on-TRS H2/H3 distortion.
- `--self-test` synthetic suite (QAS Q3).
"""
from __future__ import annotations

import argparse
import cmath
import json
import math
import struct
import sys
import wave
from dataclasses import dataclass
from pathlib import Path
from typing import Any

# Try optional NumPy acceleration for large 48 kHz 2.0s sweeps while remaining 100% pure-stdlib safe
try:
    import numpy as _np  # type: ignore
except Exception:
    _np = None

DEFAULT_SR = 48000
DEFAULT_SWEEP_SEC = 2.0
DEFAULT_F_START_HZ = 20.0
DEFAULT_F_END_HZ = 20000.0
DEFAULT_SILENCE_SEC = 0.5
DEFAULT_GAP_SEC = 0.5
HARD_H2_H3_REJECT_DB = -45.0
HARD_CAL_PEAK_REJECT_DBFS = -1.0
HARD_PILOT_BLEED_REJECT_DBFS = -18.0
HARD_DELTA_H_ERR_DB = 0.35
HARD_EXCESS_THD_PCT = 0.10
HARD_DELAY_ERR_SMP = 0.10


def _next_pow2(n: int) -> int:
    p = 1
    while p < n:
        p <<= 1
    return p


def _fft_pure(buf: list[complex], inverse: bool = False) -> list[complex]:
    """Iterative radix-2 Cooley-Tukey FFT in pure Python stdlib."""
    n = len(buf)
    if n <= 1:
        return list(buf)
    a = list(buf)
    j = 0
    for i in range(1, n):
        bit = n >> 1
        while j & bit:
            j ^= bit
            bit >>= 1
        j ^= bit
        if i < j:
            a[i], a[j] = a[j], a[i]
    sign = 1.0 if inverse else -1.0
    length = 2
    while length <= n:
        ang = sign * 2.0 * math.pi / length
        wlen = complex(math.cos(ang), math.sin(ang))
        half = length >> 1
        for i in range(0, n, length):
            w = 1.0 + 0.0j
            for k in range(half):
                u = a[i + k]
                v = a[i + k + half] * w
                a[i + k] = u + v
                a[i + k + half] = u - v
                w *= wlen
        length <<= 1
    if inverse:
        inv_n = 1.0 / n
        a = [x * inv_n for x in a]
    return a


def rfft_complex(signal: list[float], nfft: int) -> list[complex]:
    if _np is not None:
        arr = _np.zeros(nfft, dtype=_np.float64)
        lim = min(len(signal), nfft)
        arr[:lim] = signal[:lim]
        spec = _np.fft.fft(arr)
        return spec.tolist()
    padded = [complex(signal[i], 0.0) if i < len(signal) else 0.0j for i in range(nfft)]
    return _fft_pure(padded, inverse=False)


def irfft_real(spec: list[complex]) -> list[float]:
    if _np is not None:
        arr = _np.asarray(spec, dtype=_np.complex128)
        return _np.fft.ifft(arr).real.tolist()
    return [x.real for x in _fft_pure(spec, inverse=True)]


def convolve_linear(a: list[float], b: list[float]) -> list[float]:
    out_len = len(a) + len(b) - 1
    nfft = _next_pow2(out_len)
    if _np is not None:
        fa = _np.fft.rfft(_np.asarray(a, dtype=_np.float64), n=nfft)
        fb = _np.fft.rfft(_np.asarray(b, dtype=_np.float64), n=nfft)
        c = _np.fft.irfft(fa * fb, n=nfft)[:out_len]
        return c.tolist()
    fa = rfft_complex(a, nfft)
    fb = rfft_complex(b, nfft)
    prod = [x * y for x, y in zip(fa, fb)]
    return irfft_real(prod)[:out_len]


def generate_farina_ess(
    f_start_hz: float = DEFAULT_F_START_HZ,
    f_end_hz: float = DEFAULT_F_END_HZ,
    duration_sec: float = DEFAULT_SWEEP_SEC,
    sample_rate: int = DEFAULT_SR,
    amplitude: float = 0.25,
) -> tuple[list[float], list[float], float]:
    """Generates Cavern-compatible Farina Exponential Sine Sweep + matched -6 dB/oct inverse filter."""
    n = max(256, int(round(duration_sec * sample_rate)))
    l_const = duration_sec / math.log(f_end_hz / f_start_hz)
    phase_scale = 2.0 * math.pi * f_start_hz * l_const
    fade = min(int(round(sample_rate * 0.015)), n // 8)
    sweep = [0.0] * n
    for i in range(n):
        t = i / sample_rate
        s = math.sin(phase_scale * (math.exp(t / l_const) - 1.0))
        w = 1.0
        if fade > 0:
            if i < fade:
                w = 0.5 * (1.0 - math.cos(math.pi * i / fade))
            elif i >= n - fade:
                w = 0.5 * (1.0 - math.cos(math.pi * (n - 1 - i) / fade))
        sweep[i] = amplitude * w * s

    raw_inv = [sweep[n - 1 - i] * math.exp(-(i / sample_rate) / l_const) for i in range(n)]
    conv = convolve_linear(sweep, raw_inv)
    peak = max(abs(x) for x in conv) or 1.0
    inv = [x / peak for x in raw_inv]
    return sweep, inv, l_const


def build_unbroken_duplex_sequence(
    sample_rate: int = DEFAULT_SR,
    sweep_sec: float = DEFAULT_SWEEP_SEC,
    silence_sec: float = DEFAULT_SILENCE_SEC,
    gap_sec: float = DEFAULT_GAP_SEC,
    amplitude: float = 0.25,
) -> dict[str, Any]:
    """Builds the canonical unbroken-duplex sequence `[0.5s sil -> 2.0s DRY L/R -> 0.5s gap -> 2.0s FX L/R]`."""
    sweep, inv, l_const = generate_farina_ess(
        DEFAULT_F_START_HZ, DEFAULT_F_END_HZ, sweep_sec, sample_rate, amplitude
    )
    n_sil = int(round(silence_sec * sample_rate))
    n_gap = int(round(gap_sec * sample_rate))
    n_sw = len(sweep)
    # 2x2 unbroken sequence: SIL -> DRY_L -> small_gap -> DRY_R -> GAP -> FX_L -> small_gap -> FX_R -> tail
    n_inter = int(round(0.25 * sample_rate))
    n_tail = int(round(0.25 * sample_rate))
    segments = {
        "silence": (0, n_sil),
        "dry_L": (n_sil, n_sil + n_sw),
        "dry_R": (n_sil + n_sw + n_inter, n_sil + 2 * n_sw + n_inter),
        "fx_L": (n_sil + 2 * n_sw + n_inter + n_gap, n_sil + 3 * n_sw + n_inter + n_gap),
        "fx_R": (n_sil + 3 * n_sw + 2 * n_inter + n_gap, n_sil + 4 * n_sw + 2 * n_inter + n_gap),
    }
    total = segments["fx_R"][1] + n_tail
    left = [0.0] * total
    right = [0.0] * total
    for seg_name in ("dry_L", "fx_L"):
        a, b = segments[seg_name]
        left[a:b] = sweep
    for seg_name in ("dry_R", "fx_R"):
        a, b = segments[seg_name]
        right[a:b] = sweep
    return {
        "sampleRate": sample_rate,
        "sweep": sweep,
        "inverseFilter": inv,
        "lConst": l_const,
        "segments": segments,
        "left": left,
        "right": right,
        "totalFrames": total,
    }


def estimate_gcc_phat_delay_samples(ref: list[float], rec: list[float], max_lag: int | None = None) -> float:
    """Estimates fractional subsample delay (`rec` relative to `ref`) via GCC-PHAT + parabolic interpolation."""
    nfft = _next_pow2(len(ref) + len(rec))
    if _np is not None:
        fr = _np.fft.rfft(_np.asarray(ref, dtype=_np.float64), n=nfft)
        fy = _np.fft.rfft(_np.asarray(rec, dtype=_np.float64), n=nfft)
        cross = fy * _np.conj(fr)
        mag = _np.abs(cross)
        # Regularized PHAT weighting avoids boosting out-of-band noise bins
        floor = max(float(_np.max(mag)) * 1e-4, 1e-12)
        phat = cross / (mag + floor)
        cc = _np.fft.irfft(phat, n=nfft)
        if max_lag is not None and max_lag < nfft // 2:
            pos = cc[: max_lag + 1]
            neg = cc[nfft - max_lag :]
            p_idx = int(_np.argmax(pos))
            n_idx = int(_np.argmax(neg))
            peak_idx = p_idx if pos[p_idx] >= neg[n_idx] else (nfft - max_lag + n_idx)
        else:
            peak_idx = int(_np.argmax(cc))
        ym1 = float(cc[(peak_idx - 1) % nfft])
        y0 = float(cc[peak_idx])
        yp1 = float(cc[(peak_idx + 1) % nfft])
    else:
        fr = rfft_complex(ref, nfft)
        fy = rfft_complex(rec, nfft)
        cross = [y * r.conjugate() for r, y in zip(fr, fy)]
        max_mag = max(abs(c) for c in cross) or 1e-12
        floor = max(max_mag * 1e-4, 1e-12)
        phat = [c / (abs(c) + floor) for c in cross]
        cc_list = irfft_real(phat)
        search_indices = (
            list(range(0, min(max_lag + 1, nfft // 2))) + list(range(max(nfft // 2, nfft - max_lag), nfft))
            if max_lag is not None
            else list(range(nfft))
        )
        peak_idx = max(search_indices, key=lambda idx: cc_list[idx])
        ym1 = cc_list[(peak_idx - 1) % nfft]
        y0 = cc_list[peak_idx]
        yp1 = cc_list[(peak_idx + 1) % nfft]

    denom = ym1 - 2.0 * y0 + yp1
    delta = 0.5 * (ym1 - yp1) / denom if abs(denom) > 1e-15 else 0.0
    delta = max(-0.5, min(0.5, delta))
    lag = peak_idx - nfft if peak_idx > nfft // 2 else peak_idx
    return float(lag) + delta


def detect_mains_hum_harmonics(
    silence_ch: list[float],
    sample_rate: int,
    *,
    threshold_db: float = 10.0,
) -> list[float]:
    """Detects ONLY active 50 Hz or 60 Hz mains hum harmonics (`1f..6f`) above local noise floor."""
    if len(silence_ch) < 512:
        return []
    nfft = _next_pow2(min(len(silence_ch), max(4096, sample_rate // 2)))
    seg = silence_ch[:nfft]
    # Hann window
    n = len(seg)
    win = [seg[i] * 0.5 * (1.0 - math.cos(2.0 * math.pi * i / max(1, n - 1))) for i in range(n)]
    spec = rfft_complex(win, nfft)
    mags = [abs(spec[k]) for k in range(nfft // 2 + 1)]
    bin_hz = sample_rate / nfft
    # Hann window coherent gain for a full-scale sine is N/4 (0.25 * n)
    fs_ref = max(n * 0.25, 1.0)
    min_abs_dbfs = -90.0

    detected: list[float] = []
    for base_hz in (50.0, 60.0):
        cand_hits: list[float] = []
        base_detected = False
        for mult in range(1, 7):
            f_hz = base_hz * mult
            center = int(round(f_hz / bin_hz))
            if center < 4 or center + 8 >= len(mags):
                continue
            peak = max(mags[center - 1 : center + 2])
            peak_dbfs = 20.0 * math.log10(max(peak, 1e-12) / fs_ref)
            if peak_dbfs < min_abs_dbfs:
                continue
            side = mags[max(1, center - 8) : max(2, center - 2)] + mags[center + 3 : min(len(mags), center + 9)]
            side_sorted = sorted(side)
            median_floor = side_sorted[len(side_sorted) // 2] if side_sorted else 1e-12
            snr_db = 20.0 * math.log10(max(peak, 1e-12) / max(median_floor, 1e-12))
            if mult == 1:
                if snr_db >= threshold_db:
                    base_detected = True
                    cand_hits.append(f_hz)
            elif base_detected and snr_db >= threshold_db:
                cand_hits.append(f_hz)
            elif snr_db >= (threshold_db + 8.0) and peak_dbfs >= -80.0:
                cand_hits.append(f_hz)
        detected.extend(cand_hits)
    return sorted(set(detected))


def _notch_frequency_bins_inplace(
    spec: list[complex],
    sample_rate: int,
    notched_freqs_hz: list[float],
    half_width_hz: float = 3.0,
) -> None:
    """Linearly interpolates complex spectrum across ONLY detected mains hum harmonics."""
    if not notched_freqs_hz:
        return
    nfft = len(spec)
    bin_hz = sample_rate / nfft
    half_bins = max(1, int(math.ceil(half_width_hz / bin_hz)))
    for f_hz in notched_freqs_hz:
        c = int(round(f_hz / bin_hz))
        lo = max(1, c - half_bins)
        hi = min(nfft // 2 - 1, c + half_bins)
        if lo >= hi:
            continue
        left_val = spec[lo - 1]
        right_val = spec[hi + 1]
        span = float((hi + 1) - (lo - 1))
        for k in range(lo, hi + 1):
            t = (k - (lo - 1)) / span
            interp = left_val * (1.0 - t) + right_val * t
            spec[k] = interp
            if k > 0 and nfft - k < nfft:
                spec[nfft - k] = interp.conjugate()


def deconvolve_ess_ir_and_harmonics(
    recorded: list[float],
    inverse_filter: list[float],
    l_const: float,
    sample_rate: int,
    *,
    ir_len: int = 2048,
    pre_samples: int = 64,
) -> dict[str, Any]:
    """Deconvolves a recorded Farina ESS into linear IR + separated H2/H3 precursor distortion levels (dB)."""
    conv = convolve_linear(recorded, inverse_filter)
    # Find direct H1 peak
    peak_idx = 0
    peak_abs = 0.0
    for i, v in enumerate(conv):
        av = abs(v)
        if av > peak_abs:
            peak_abs = av
            peak_idx = i
    if peak_abs <= 1e-12:
        return {
            "ir": [0.0] * ir_len,
            "peakIdx": 0,
            "h2Db": -120.0,
            "h3Db": -120.0,
            "thdPct": 0.0,
            "peakAbs": 0.0,
        }

    half_win = max(16, min(256, sample_rate // 250))

    def _window_energy(center: int, lo_off: int, hi_off: int) -> float:
        a = max(0, center - lo_off)
        b = min(len(conv), center + hi_off + 1)
        if b <= a:
            return 0.0
        return sum(conv[k] * conv[k] for k in range(a, b))

    h1_e = max(_window_energy(peak_idx, half_win, half_win * 2), 1e-24)
    lag_h2 = int(round(l_const * math.log(2.0) * sample_rate))
    lag_h3 = int(round(l_const * math.log(3.0) * sample_rate))

    h2_e = _window_energy(peak_idx - lag_h2, half_win, half_win) if peak_idx - lag_h2 > half_win else 0.0
    h3_e = _window_energy(peak_idx - lag_h3, half_win, half_win) if peak_idx - lag_h3 > half_win else 0.0
    h2_db = 10.0 * math.log10(max(h2_e, 1e-24) / h1_e)
    h3_db = 10.0 * math.log10(max(h3_e, 1e-24) / h1_e)
    thd_pct = math.sqrt(max(0.0, (h2_e + h3_e) / h1_e)) * 100.0

    start = max(0, peak_idx - pre_samples)
    ir = [0.0] * ir_len
    avail = min(ir_len, len(conv) - start)
    for i in range(avail):
        ir[i] = conv[start + i]

    return {
        "ir": ir,
        "peakIdx": peak_idx,
        "h2Db": round(h2_db, 2),
        "h3Db": round(h3_db, 2),
        "thdPct": round(thd_pct, 4),
        "peakAbs": peak_abs,
    }


def verify_gen4_preflight(
    hw_flags: dict[str, bool],
    *,
    cal_peak_dbfs: float,
    pilot_bleed_dbfs: float,
    lr_swapped: bool,
    dry_h2_db: float,
    dry_h3_db: float,
) -> dict[str, Any]:
    """Verifies Focusrite Scarlett 2i2 Gen4 safety flags & `-40 dBFS` pilot tone invariants (`W60-SCARLETT-CAL-08`)."""
    violations: list[str] = []
    must_be_off = ("clip_safe", "direct_monitor", "phantom_48v", "air_mode", "inst_mode", "auto_gain")
    for k in must_be_off:
        if bool(hw_flags.get(k, False)):
            violations.append(f"gen4_flag_must_be_off:{k}")
    if cal_peak_dbfs >= HARD_CAL_PEAK_REJECT_DBFS:
        violations.append(f"cal_peak_clipping:{cal_peak_dbfs:.2f}dBFS>={HARD_CAL_PEAK_REJECT_DBFS:.1f}dBFS")
    if pilot_bleed_dbfs > HARD_PILOT_BLEED_REJECT_DBFS:
        violations.append(f"pilot_bleed_excessive:{pilot_bleed_dbfs:.2f}dBFS>{HARD_PILOT_BLEED_REJECT_DBFS:.1f}dBFS")
    if lr_swapped:
        violations.append("lr_channels_swapped")
    if dry_h2_db >= HARD_H2_H3_REJECT_DB:
        violations.append(f"dry_h2_distortion:{dry_h2_db:.2f}dB>={HARD_H2_H3_REJECT_DB:.1f}dB")
    if dry_h3_db >= HARD_H2_H3_REJECT_DB:
        violations.append(f"dry_h3_distortion:{dry_h3_db:.2f}dB>={HARD_H2_H3_REJECT_DB:.1f}dB")

    return {
        "passed": len(violations) == 0,
        "killId": "W60-SCARLETT-CAL-08" if violations else None,
        "violations": violations,
    }


def deconvolve_2x2_left_multiplied(
    dry_irs: dict[str, list[float]],
    fx_irs: dict[str, list[float]],
    sample_rate: int,
    *,
    notched_freqs_hz: list[float] | None = None,
    right_multiply_bug: bool = False,
) -> dict[str, Any]:
    """
    Computes `H_DSP(f) = M_cable(f)^-1 @ M_FX(f)` via strictly LEFT-multiplied 2x2 regularized matrix inversion.
    `dry_irs` and `fx_irs` each map `"LL"`, `"LR"`, `"RL"`, `"RR"` to aligned impulse response arrays.
    """
    ir_len = max(len(dry_irs["LL"]), len(fx_irs["LL"]))
    nfft = _next_pow2(max(2048, ir_len * 2))
    notches = list(notched_freqs_hz or [])

    def _spec(ir: list[float]) -> list[complex]:
        s = rfft_complex(ir, nfft)
        if notches:
            _notch_frequency_bins_inplace(s, sample_rate, notches)
        return s

    d_ll, d_lr, d_rl, d_rr = _spec(dry_irs["LL"]), _spec(dry_irs["LR"]), _spec(dry_irs["RL"]), _spec(dry_irs["RR"])
    f_ll, f_lr, f_rl, f_rr = _spec(fx_irs["LL"]), _spec(fx_irs["LR"]), _spec(fx_irs["RL"]), _spec(fx_irs["RR"])

    bins = nfft // 2 + 1
    h_ll: list[complex] = [0j] * bins
    h_lr: list[complex] = [0j] * bins
    h_rl: list[complex] = [0j] * bins
    h_rr: list[complex] = [0j] * bins
    freqs_hz = [k * sample_rate / nfft for k in range(bins)]

    for k in range(bins):
        a, b, c, d = d_ll[k], d_lr[k], d_rl[k], d_rr[k]
        p, q, r, s = f_ll[k], f_lr[k], f_rl[k], f_rr[k]
        det = a * d - b * c
        reg = 1e-10
        inv_det = det.conjugate() / (abs(det) ** 2 + reg)
        # M_cable^-1 = inv_det * [[d, -b], [-c, a]]
        i00, i01, i10, i11 = d * inv_det, -b * inv_det, -c * inv_det, a * inv_det
        if not right_multiply_bug:
            # Correct physical order: M_FX = M_cable @ H_DSP  ==>  H_DSP = M_cable^-1 @ M_FX
            h_ll[k] = i00 * p + i01 * r
            h_lr[k] = i00 * q + i01 * s
            h_rl[k] = i10 * p + i11 * r
            h_rr[k] = i10 * q + i11 * s
        else:
            # Wrong right-multiplication (M_FX @ M_cable^-1) — kept strictly for negative unit proof
            h_ll[k] = p * i00 + q * i10
            h_lr[k] = p * i01 + q * i11
            h_rl[k] = r * i00 + s * i10
            h_rr[k] = r * i01 + s * i11

    return {
        "nfft": nfft,
        "freqsHz": freqs_hz,
        "H_LL": h_ll,
        "H_LR": h_lr,
        "H_RL": h_rl,
        "H_RR": h_rr,
        "notchedFreqsHz": notches,
    }


@dataclass
class CableProfileMetrics:
    name: str
    cal_peak_dbfs: float
    pilot_bleed_dbfs: float
    lr_swapped: bool
    dry_h2_db: float
    dry_h3_db: float
    crosstalk_db: float
    snr_db: float
    hw_flags: dict[str, bool]


def compare_cables(candidates: list[CableProfileMetrics]) -> dict[str, Any]:
    """Grades physical loopback cable paths (e.g. `line_out_2xp10` vs `hp_out_ycable`) and rejects TS-on-TRS distortion."""
    graded: list[dict[str, Any]] = []
    passing: list[dict[str, Any]] = []
    for c in candidates:
        pf = verify_gen4_preflight(
            c.hw_flags,
            cal_peak_dbfs=c.cal_peak_dbfs,
            pilot_bleed_dbfs=c.pilot_bleed_dbfs,
            lr_swapped=c.lr_swapped,
            dry_h2_db=c.dry_h2_db,
            dry_h3_db=c.dry_h3_db,
        )
        # Higher score is better: clean H2/H3 distortion + SNR - crosstalk penalty
        distortion_margin = min(-c.dry_h2_db, -c.dry_h3_db)
        score = distortion_margin * 1.5 + c.snr_db * 0.5 - max(0.0, 50.0 + c.crosstalk_db) * 0.4
        entry = {
            "cable": c.name,
            "passed": pf["passed"],
            "score": round(score, 2),
            "dryH2Db": round(c.dry_h2_db, 2),
            "dryH3Db": round(c.dry_h3_db, 2),
            "crosstalkDb": round(c.crosstalk_db, 2),
            "snrDb": round(c.snr_db, 2),
            "violations": pf["violations"],
        }
        graded.append(entry)
        if pf["passed"]:
            passing.append(entry)

    passing.sort(key=lambda x: x["score"], reverse=True)
    recommended = passing[0]["cable"] if passing else None
    return {
        "passed": recommended is not None,
        "recommendedCable": recommended,
        "candidates": graded,
    }


def _apply_biquad(samples: list[float], b0: float, b1: float, b2: float, a1: float, a2: float) -> list[float]:
    out = [0.0] * len(samples)
    z1 = 0.0
    z2 = 0.0
    for i, x in enumerate(samples):
        y = b0 * x + z1
        z1 = b1 * x - a1 * y + z2
        z2 = b2 * x - a2 * y
        out[i] = y
    return out


def _design_peaking_eq(f0: float, gain_db: float, q: float, sr: int) -> tuple[float, float, float, float, float]:
    a_amp = 10.0 ** (gain_db / 40.0)
    w0 = 2.0 * math.pi * f0 / sr
    alpha = math.sin(w0) / (2.0 * q)
    cos_w0 = math.cos(w0)
    b0 = 1.0 + alpha * a_amp
    b1 = -2.0 * cos_w0
    b2 = 1.0 - alpha * a_amp
    a0 = 1.0 + alpha / a_amp
    a1 = -2.0 * cos_w0
    a2 = 1.0 - alpha / a_amp
    return b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0


def _biquad_mag_db(f_hz: float, coeffs: tuple[float, float, float, float, float], sr: int) -> float:
    b0, b1, b2, a1, a2 = coeffs
    w = 2.0 * math.pi * f_hz / sr
    z1 = cmath.exp(-1j * w)
    z2 = cmath.exp(-2j * w)
    num = b0 + b1 * z1 + b2 * z2
    den = 1.0 + a1 * z1 + a2 * z2
    return 20.0 * math.log10(max(abs(num / den), 1e-12))


def _fractional_delay_signal(samples: list[float], delay_smp: float) -> list[float]:
    """Applies exact sub-sample fractional delay via windowed-sinc (Lagrange/Lanczos kernel)."""
    n = len(samples)
    out = [0.0] * n
    int_d = int(math.floor(delay_smp))
    frac = delay_smp - int_d
    radius = 6
    kernel = []
    for k in range(-radius, radius + 1):
        x = frac - k
        if abs(x) < 1e-12:
            val = 1.0
        else:
            px = math.pi * x
            val = (math.sin(px) / px) * (math.sin(px / radius) / (px / radius))
        kernel.append(val)
    ksum = sum(kernel) or 1.0
    kernel = [v / ksum for v in kernel]
    for i in range(n):
        base = i - int_d
        acc = 0.0
        for idx, w in enumerate(kernel):
            src = base - (idx - radius)
            if 0 <= src < n:
                acc += samples[src] * w
        out[i] = acc
    return out


def _read_stereo_wav(path: Path) -> tuple[int, list[float], list[float]]:
    with wave.open(str(path), "rb") as wf:
        ch = wf.getnchannels()
        sw = wf.getsampwidth()
        sr = wf.getframerate()
        n = wf.getnframes()
        raw = wf.readframes(n)
    if ch != 2 or sw not in (2, 3, 4):
        raise ValueError(f"Expected stereo 16/24/32-bit PCM WAV, got ch={ch} sw={sw}")
    left = [0.0] * n
    right = [0.0] * n
    if sw == 2:
        vals = struct.unpack(f"<{n * 2}h", raw)
        inv = 1.0 / 32768.0
        for i in range(n):
            left[i] = vals[i * 2] * inv
            right[i] = vals[i * 2 + 1] * inv
    elif sw == 4:
        vals = struct.unpack(f"<{n * 2}i", raw)
        inv = 1.0 / 2147483648.0
        for i in range(n):
            left[i] = vals[i * 2] * inv
            right[i] = vals[i * 2 + 1] * inv
    else:
        inv = 1.0 / 8388608.0
        for i in range(n):
            off = i * 6
            lv = int.from_bytes(raw[off : off + 3], "little", signed=True)
            rv = int.from_bytes(raw[off + 3 : off + 6], "little", signed=True)
            left[i] = lv * inv
            right[i] = rv * inv
    return sr, left, right


def run_self_test() -> int:
    """
    Executes QAS Q3 synthetic validation suite:
    - Synthesizes `line_out_2xp10` with TS-on-TRS H2/H3 `-38 dB` (MUST BE REJECTED).
    - Synthesizes `hp_out_ycable` with `-35 dB` crosstalk, asymmetric L/R gain (`+0.8/-0.6 dB`),
      60+120+180 Hz mains hum, fractional delay (`14.37` smp), and known `+0.50 dB` 1 kHz DSP EQ delta.
    - Verifies `--compare-cables` rejects distorted TS & selects cleaner path, detected-only notch mask
      preserves `delta_h_err <= 0.35 dB`, `delay_err <= 0.10 smp`, `excess_thd <= 0.10%`, and proves
      LEFT-multiplied `M_cable^-1 @ M_FX` beats wrong right-multiplication.
    """
    sr = 48000
    sweep_sec = 2.0
    sweep, inv, l_const = generate_farina_ess(DEFAULT_F_START_HZ, DEFAULT_F_END_HZ, sweep_sec, sr, amplitude=0.25)

    # 1. Synthesize TS-on-TRS distorted `line_out_2xp10` (-38 dB H2/H3) vs clean `hp_out_ycable` (-35 dB crosstalk)
    ts_distorted = [x + 0.050 * (x * x) + 0.040 * (x * x * x) for x in sweep]
    ts_dec = deconvolve_ess_ir_and_harmonics(ts_distorted, inv, l_const, sr)
    assert ts_dec["h2Db"] >= HARD_H2_H3_REJECT_DB, f"Expected TS synthetic H2 >= -45 dB, got {ts_dec['h2Db']}"

    clean_flags = {
        "clip_safe": False,
        "direct_monitor": False,
        "phantom_48v": False,
        "air_mode": False,
        "inst_mode": False,
        "auto_gain": False,
    }
    cmp_res = compare_cables([
        CableProfileMetrics(
            name="line_out_2xp10",
            cal_peak_dbfs=-10.0,
            pilot_bleed_dbfs=-40.0,
            lr_swapped=False,
            dry_h2_db=ts_dec["h2Db"],
            dry_h3_db=ts_dec["h3Db"],
            crosstalk_db=-68.0,
            snr_db=78.0,
            hw_flags=clean_flags,
        ),
        CableProfileMetrics(
            name="hp_out_ycable",
            cal_peak_dbfs=-10.5,
            pilot_bleed_dbfs=-40.2,
            lr_swapped=False,
            dry_h2_db=-76.0,
            dry_h3_db=-81.0,
            crosstalk_db=-35.0,
            snr_db=82.0,
            hw_flags=clean_flags,
        ),
    ])
    assert cmp_res["passed"], f"compare_cables failed: {cmp_res}"
    assert cmp_res["recommendedCable"] == "hp_out_ycable", f"Expected hp_out_ycable, got {cmp_res}"
    ts_entry = [c for c in cmp_res["candidates"] if c["cable"] == "line_out_2xp10"][0]
    assert not ts_entry["passed"], "Distorted TS-on-TRS cable must be rejected!"

    # 2. Synthesize 0.5s silence with 60 Hz + 120 Hz + 180 Hz mains hum and verify detected-only notch
    n_sil = int(0.5 * sr)
    sil = [
        0.008 * math.sin(2.0 * math.pi * 60.0 * i / sr)
        + 0.004 * math.sin(2.0 * math.pi * 120.0 * i / sr)
        + 0.002 * math.sin(2.0 * math.pi * 180.0 * i / sr)
        + 1e-5 * math.sin(2.0 * math.pi * 1379.0 * i / sr)
        for i in range(n_sil)
    ]
    hum_hits = detect_mains_hum_harmonics(sil, sr)
    assert 60.0 in hum_hits and 120.0 in hum_hits and 180.0 in hum_hits, f"Missed hum harmonics: {hum_hits}"
    assert 50.0 not in hum_hits and 100.0 not in hum_hits, f"False-positive 50Hz notch on clean bins: {hum_hits}"

    # 3. Synthesize unbroken-duplex `hp_out_ycable` (asymmetric gain +0.8/-0.6 dB, -35 dB crosstalk, +14.37 smp delay,
    #    60/120/180 Hz hum) + known +0.50 dB 1 kHz peaking EQ DSP (with slight cross-feed for matrix order proof)
    true_delay_smp = 14.37
    g_l = 10.0 ** (0.8 / 20.0)
    g_r = 10.0 ** (-0.6 / 20.0)
    xt = 10.0 ** (-35.0 / 20.0)  # -35 dB crosstalk
    eq_coeffs = _design_peaking_eq(1000.0, 0.50, 1.0, sr)

    delayed_sw = _fractional_delay_signal(sweep, true_delay_smp)
    hum_sw = [
        0.002 * math.sin(2.0 * math.pi * 60.0 * i / sr)
        + 0.001 * math.sin(2.0 * math.pi * 120.0 * i / sr)
        + 0.0005 * math.sin(2.0 * math.pi * 180.0 * i / sr)
        for i in range(len(sweep))
    ]
    # GCC-PHAT fractional delay check under hum
    rec_for_delay = [g_l * delayed_sw[i] + hum_sw[i] for i in range(len(sweep))]
    est_delay = estimate_gcc_phat_delay_samples(sweep, rec_for_delay, max_lag=512)
    delay_err = abs(est_delay - true_delay_smp)
    assert delay_err <= HARD_DELAY_ERR_SMP, f"GCC-PHAT delay err {delay_err:.4f} smp > {HARD_DELAY_ERR_SMP}"

    # Build 2x2 DRY & FX recorded segments: M_FX = M_cable @ H_DSP
    # DSP applies +0.50 dB peaking EQ on L & R plus 0.15 linear cross-mix (so M_cable & H_DSP do NOT commute!)
    eq_sw = _apply_biquad(delayed_sw, *eq_coeffs)
    dsp_x = 0.15

    dry_ll_rec = [g_l * delayed_sw[i] + hum_sw[i] for i in range(len(sweep))]
    dry_rl_rec = [xt * g_r * delayed_sw[i] + hum_sw[i] for i in range(len(sweep))]
    dry_lr_rec = [xt * g_l * delayed_sw[i] + hum_sw[i] for i in range(len(sweep))]
    dry_rr_rec = [g_r * delayed_sw[i] + hum_sw[i] for i in range(len(sweep))]

    # Input L -> H_DSP -> [eq_sw, dsp_x*eq_sw] -> M_cable -> [g_l*(eq + xt*dsp_x*eq), g_r*(xt*eq + dsp_x*eq)]
    fx_ll_rec = [g_l * (eq_sw[i] + xt * dsp_x * eq_sw[i]) + hum_sw[i] for i in range(len(sweep))]
    fx_rl_rec = [g_r * (xt * eq_sw[i] + dsp_x * eq_sw[i]) + hum_sw[i] for i in range(len(sweep))]
    fx_lr_rec = [g_l * (dsp_x * eq_sw[i] + xt * eq_sw[i]) + hum_sw[i] for i in range(len(sweep))]
    fx_rr_rec = [g_r * (xt * dsp_x * eq_sw[i] + eq_sw[i]) + hum_sw[i] for i in range(len(sweep))]

    dec_dry_ll = deconvolve_ess_ir_and_harmonics(dry_ll_rec, inv, l_const, sr)
    dec_dry_lr = deconvolve_ess_ir_and_harmonics(dry_lr_rec, inv, l_const, sr)
    dec_dry_rl = deconvolve_ess_ir_and_harmonics(dry_rl_rec, inv, l_const, sr)
    dec_dry_rr = deconvolve_ess_ir_and_harmonics(dry_rr_rec, inv, l_const, sr)

    dec_fx_ll = deconvolve_ess_ir_and_harmonics(fx_ll_rec, inv, l_const, sr)
    dec_fx_lr = deconvolve_ess_ir_and_harmonics(fx_lr_rec, inv, l_const, sr)
    dec_fx_rl = deconvolve_ess_ir_and_harmonics(fx_rl_rec, inv, l_const, sr)
    dec_fx_rr = deconvolve_ess_ir_and_harmonics(fx_rr_rec, inv, l_const, sr)

    excess_thd = max(0.0, dec_fx_ll["thdPct"] - dec_dry_ll["thdPct"])
    assert excess_thd <= HARD_EXCESS_THD_PCT, f"Excess THD {excess_thd:.4f}% > {HARD_EXCESS_THD_PCT}%"

    dry_irs = {"LL": dec_dry_ll["ir"], "LR": dec_dry_lr["ir"], "RL": dec_dry_rl["ir"], "RR": dec_dry_rr["ir"]}
    fx_irs = {"LL": dec_fx_ll["ir"], "LR": dec_fx_lr["ir"], "RL": dec_fx_rl["ir"], "RR": dec_fx_rr["ir"]}

    left_res = deconvolve_2x2_left_multiplied(dry_irs, fx_irs, sr, notched_freqs_hz=hum_hits, right_multiply_bug=False)
    right_res = deconvolve_2x2_left_multiplied(dry_irs, fx_irs, sr, notched_freqs_hz=hum_hits, right_multiply_bug=True)

    # Evaluate magnitude error on LL & RL cross-term across 150 Hz..12 kHz
    test_freqs = [200.0, 400.0, 700.0, 1000.0, 1500.0, 3000.0, 6000.0, 10000.0]
    nfft = left_res["nfft"]
    max_err_left_db = 0.0
    max_cross_err_left_db = 0.0
    max_cross_err_right_db = 0.0
    for f_hz in test_freqs:
        k = int(round(f_hz * nfft / sr))
        expected_ll_db = _biquad_mag_db(f_hz, eq_coeffs, sr)
        expected_rl_db = expected_ll_db + 20.0 * math.log10(dsp_x)
        meas_ll_db = 20.0 * math.log10(max(abs(left_res["H_LL"][k]), 1e-12))
        meas_rl_left_db = 20.0 * math.log10(max(abs(left_res["H_RL"][k]), 1e-12))
        meas_rl_right_db = 20.0 * math.log10(max(abs(right_res["H_RL"][k]), 1e-12))
        max_err_left_db = max(max_err_left_db, abs(meas_ll_db - expected_ll_db))
        max_cross_err_left_db = max(max_cross_err_left_db, abs(meas_rl_left_db - expected_rl_db))
        max_cross_err_right_db = max(max_cross_err_right_db, abs(meas_rl_right_db - expected_rl_db))

    assert max_err_left_db <= HARD_DELTA_H_ERR_DB, f"Delta H_LL err {max_err_left_db:.3f} dB > {HARD_DELTA_H_ERR_DB} dB"
    assert max_cross_err_left_db <= HARD_DELTA_H_ERR_DB, f"Delta H_RL (left-mult) err {max_cross_err_left_db:.3f} dB"
    # Confirm wrong right-multiplication suffers asymmetric L/R gain swap (~1.4 dB error!)
    assert max_cross_err_right_db > 1.0, f"Expected right-mult bug to fail (>1.0 dB), got {max_cross_err_right_db:.3f} dB"

    print(
        f"[PASS] scarlett_loopback_delta self-test: --compare-cables rejected TS-on-TRS (H2={ts_dec['h2Db']}dB) -> "
        f"picked hp_out_ycable | hum_notched={hum_hits} | delta_h_err={max_err_left_db:.3f}dB<=0.35dB | "
        f"delay_err={delay_err:.4f}smp<=0.10smp | excess_thd={excess_thd:.4f}%<=0.10% | "
        f"left_vs_right_cross_err={max_cross_err_left_db:.3f}dB vs {max_cross_err_right_db:.2f}dB"
    )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="CavernizeAndroid W60.2 Scarlett Gen4 Loopback Delta & Cable Grader")
    parser.add_argument("--self-test", action="store_true", help="Run QAS Q3 synthetic loopback + compare-cables suite")
    parser.add_argument("--compare-cables", dest="compare_json", help="JSON file with cable candidates to grade")
    parser.add_argument("--analyze-wav", dest="wav_path", help="Analyze a recorded unbroken-duplex stereo WAV")
    args = parser.parse_args(argv)

    if args.self_test:
        return run_self_test()

    if args.compare_json:
        raw = json.loads(Path(args.compare_json).read_text(encoding="utf-8"))
        cands = [
            CableProfileMetrics(
                name=str(c["name"]),
                cal_peak_dbfs=float(c["calPeakDbfs"]),
                pilot_bleed_dbfs=float(c["pilotBleedDbfs"]),
                lr_swapped=bool(c.get("lrSwapped", False)),
                dry_h2_db=float(c["dryH2Db"]),
                dry_h3_db=float(c["dryH3Db"]),
                crosstalk_db=float(c.get("crosstalkDb", -60.0)),
                snr_db=float(c.get("snrDb", 75.0)),
                hw_flags=dict(c.get("hwFlags") or {}),
            )
            for c in raw.get("candidates", [])
        ]
        res = compare_cables(cands)
        print(json.dumps(res, indent=2))
        return 0 if res["passed"] else 1

    if args.wav_path:
        sr, left, right = _read_stereo_wav(Path(args.wav_path))
        seq = build_unbroken_duplex_sequence(sample_rate=sr)
        s0, s1 = seq["segments"]["silence"]
        hum = detect_mains_hum_harmonics(left[s0:min(s1, len(left))], sr)
        d0, d1 = seq["segments"]["dry_L"]
        dry_dec = deconvolve_ess_ir_and_harmonics(left[d0:min(d1 + sr // 4, len(left))], seq["inverseFilter"], seq["lConst"], sr)
        print(json.dumps({"sampleRate": sr, "frames": len(left), "detectedHumHz": hum, "dryL": {k: v for k, v in dry_dec.items() if k != "ir"}}, indent=2))
        return 0

    parser.error("Provide --self-test, --compare-cables <json>, or --analyze-wav <wav>")
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
