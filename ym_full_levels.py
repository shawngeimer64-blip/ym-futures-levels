"""
YM FULL LEVELS
==============
One-shot pre-market levels tool combining:
- Session levels (pivots, overnight, prior week/month)
- Gamma levels (flip, call/put walls) from DIA options
- Confluence detector (where levels stack up)
- CSV export for NinjaTrader

FIXES IN THIS VERSION
---------------------
1. Spot is anchored to the reliable YM FUTURES price, not the DIA history
   close. On bad mornings dia.history() returned a garbage close (~471 vs a
   real ~527), which made the flip (47080) look "sane" because it was being
   compared against an equally-garbage spot. Now the futures price is the
   reference for BOTH the sanity guard and regime, so junk flips get dropped.
2. The DIA option chain is filtered to strikes within STRIKE_BAND of spot
   before computing GEX. Deep/stale strikes far from price were dragging the
   flip down toward 470; this removes them.
3. Regime PIVOT FALLBACK: when no valid gamma flip survives, regime is
   resolved from price vs the daily Pivot (above = POSITIVE, below = NEGATIVE)
   instead of writing UNKNOWN. This keeps the NinjaTrader stat buckets
   resolving (POS/NEG) on junk-options mornings so break/reject percentages
   still show. The regime source is noted in the console and is not gamma on
   those days.
4. find_flip() never returns None on non-empty data (closest-to-zero fallback),
   0DTE is detected by real days-to-expiry, and Regime/spot/timestamp meta rows
   are written to the CSV for the dashboard + logger.
"""

import requests
import csv
import os
import yfinance as yf
import numpy as np
from scipy.stats import norm
from datetime import datetime, date, time, timedelta
from zoneinfo import ZoneInfo

# ==========================================================
#   CONFIG
# ==========================================================
ET = ZoneInfo("America/New_York")
HEADERS = {"User-Agent": "Mozilla/5.0"}
YM_URL = "https://query1.finance.yahoo.com/v8/finance/chart/YM=F"
DIA_TO_YM = 100
RISK_FREE = 0.043
CONTRACT_SIZE = 100
CONFLUENCE_TOL = 50
MAX_DTE = 60                 # ignore expiries further out than this
GAMMA_SANITY_PTS = 1500      # gamma level farther than this (YM pts) from the
                             # FUTURES spot is treated as bad data and dropped
STRIKE_BAND = 0.05           # only use option strikes within +/-5% of spot
SPOT_DISAGREE_FRAC = 0.10    # if DIA history close differs from the futures
                             # reference by more than this, trust the futures

# ==========================================================
#   PART 1 — SESSION LEVELS FROM YM FUTURES
# ==========================================================
def fetch_ym_daily():
    r = requests.get(YM_URL, headers=HEADERS,
                     params={"range": "3mo", "interval": "1d"})
    d = r.json()["chart"]["result"][0]
    ts, q = d["timestamp"], d["indicators"]["quote"][0]
    bars = []
    for i in range(len(ts)):
        if None in (q["open"][i], q["high"][i], q["low"][i], q["close"][i]):
            continue
        bars.append({
            "dt": datetime.fromtimestamp(ts[i], tz=ET),
            "open": q["open"][i], "high": q["high"][i],
            "low": q["low"][i], "close": q["close"][i],
        })
    return bars

def fetch_ym_intraday():
    r = requests.get(YM_URL, headers=HEADERS,
                     params={"range": "5d", "interval": "5m"})
    d = r.json()["chart"]["result"][0]
    ts, q = d["timestamp"], d["indicators"]["quote"][0]
    price = d["meta"]["regularMarketPrice"]
    bars = []
    for i in range(len(ts)):
        if q["high"][i] is None or q["low"][i] is None:
            continue
        bars.append({
            "dt": datetime.fromtimestamp(ts[i], tz=ET),
            "high": q["high"][i], "low": q["low"][i],
        })
    return bars, price

def compute_pivots(bar):
    h, l, c = bar["high"], bar["low"], bar["close"]
    p = (h + l + c) / 3
    return {
        "R3": round(h + 2 * (p - l)),
        "R2": round(p + (h - l)),
        "R1": round(2 * p - l),
        "Pivot": round(p),
        "Y-Mid": round((h + l) / 2),
        "S1": round(2 * p - h),
        "S2": round(p - (h - l)),
        "S3": round(l - 2 * (h - p)),
    }

def compute_overnight(intra_bars, today):
    start = datetime.combine(today - timedelta(days=1), time(18, 0), tzinfo=ET)
    end = datetime.combine(today, time(9, 30), tzinfo=ET)
    on = [b for b in intra_bars if start <= b["dt"] < end]
    if not on:
        return None
    h = max(b["high"] for b in on)
    l = min(b["low"] for b in on)
    return {"ONH": round(h), "ON-Mid": round((h + l) / 2), "ONL": round(l)}

def compute_prior_week(daily_bars, today):
    this_mon = today - timedelta(days=today.weekday())
    last_mon = this_mon - timedelta(days=7)
    last_fri = last_mon + timedelta(days=4)
    pw = [b for b in daily_bars if last_mon <= b["dt"].date() <= last_fri]
    if not pw:
        return None, last_mon, last_fri
    h = max(b["high"] for b in pw)
    l = min(b["low"] for b in pw)
    return {
        "PWH": round(h), "PW-Mid": round((h + l) / 2),
        "PWC": round(pw[-1]["close"]), "PWL": round(l),
    }, last_mon, last_fri

def compute_prior_month(daily_bars, today):
    first_this = today.replace(day=1)
    last_prior = first_this - timedelta(days=1)
    first_prior = last_prior.replace(day=1)
    pm = [b for b in daily_bars if first_prior <= b["dt"].date() <= last_prior]
    if not pm:
        return None, first_prior, last_prior
    h = max(b["high"] for b in pm)
    l = min(b["low"] for b in pm)
    return {
        "PMH": round(h), "PM-Mid": round((h + l) / 2),
        "PMC": round(pm[-1]["close"]), "PML": round(l),
    }, first_prior, last_prior

# ==========================================================
#   PART 2 — GAMMA LEVELS FROM DIA OPTIONS
# ==========================================================
def bs_gamma(S, K, T, r, sigma):
    if T <= 0 or sigma <= 0:
        return 0.0
    d1 = (np.log(S / K) + (r + 0.5 * sigma ** 2) * T) / (sigma * np.sqrt(T))
    return norm.pdf(d1) / (S * sigma * np.sqrt(T))

def compute_gex(ref_spot_dia=None):
    """
    ref_spot_dia : reliable DIA-equivalent spot derived from the YM futures
                   price (ym_price / DIA_TO_YM). Used to (a) override a stale
                   dia.history() close and (b) filter option strikes near spot.

    Returns spot, gex_all, gex_0dte, has_true_0dte, spot_was_corrected.
    """
    dia = yf.Ticker("DIA")
    spot = float(dia.history(period="1d")["Close"].iloc[-1])

    # (1) Correct a stale/garbage DIA close against the futures reference.
    spot_was_corrected = False
    if ref_spot_dia is not None and ref_spot_dia > 0:
        if abs(spot - ref_spot_dia) > SPOT_DISAGREE_FRAC * ref_spot_dia:
            spot = ref_spot_dia
            spot_was_corrected = True

    # (2) Strike band around the (now trustworthy) spot.
    lo = spot * (1 - STRIKE_BAND)
    hi = spot * (1 + STRIKE_BAND)

    all_exp = dia.options
    gex_all = {}
    gex_0dte = {}
    today_d = date.today()

    parsed = []
    for exp in all_exp:
        try:
            exp_d = datetime.strptime(exp, "%Y-%m-%d").date()
        except ValueError:
            continue
        dte = (exp_d - today_d).days
        if 0 <= dte <= MAX_DTE:
            parsed.append((exp, dte))
    if not parsed:
        return spot, gex_all, gex_0dte, False, spot_was_corrected

    parsed.sort(key=lambda x: x[1])
    front_exp = parsed[0][0]
    has_true_0dte = (parsed[0][1] == 0)

    for exp, dte in parsed:
        T = max(dte, 1) / 365.0
        try:
            chain = dia.option_chain(exp)
        except Exception:
            continue

        is_front = (exp == front_exp)
        for _, row in chain.calls.iterrows():
            K = row["strike"]
            if K < lo or K > hi:              # strike-band filter
                continue
            oi = row["openInterest"] or 0
            iv = row["impliedVolatility"] or 0
            if oi == 0 or iv == 0:
                continue
            g = bs_gamma(spot, K, T, RISK_FREE, iv)
            gex = g * oi * CONTRACT_SIZE * spot * spot * 0.01
            gex_all[K] = gex_all.get(K, 0) + gex
            if is_front:
                gex_0dte[K] = gex_0dte.get(K, 0) + gex

        for _, row in chain.puts.iterrows():
            K = row["strike"]
            if K < lo or K > hi:              # strike-band filter
                continue
            oi = row["openInterest"] or 0
            iv = row["impliedVolatility"] or 0
            if oi == 0 or iv == 0:
                continue
            g = bs_gamma(spot, K, T, RISK_FREE, iv)
            gex = g * oi * CONTRACT_SIZE * spot * spot * 0.01
            gex_all[K] = gex_all.get(K, 0) - gex
            if is_front:
                gex_0dte[K] = gex_0dte.get(K, 0) - gex

    return spot, gex_all, gex_0dte, has_true_0dte, spot_was_corrected

def find_flip(gex_dict):
    strikes = sorted(gex_dict.keys())
    if not strikes:
        return None
    cum, prev_K, prev_c = 0, None, 0
    running = []
    for K in strikes:
        cum += gex_dict[K]
        running.append((K, cum))
        if prev_K is not None:
            if (prev_c <= 0 and cum > 0) or (prev_c >= 0 and cum < 0):
                if cum != prev_c:
                    frac = -prev_c / (cum - prev_c)
                    return prev_K + frac * (K - prev_K)
        prev_K, prev_c = K, cum
    return min(running, key=lambda kc: abs(kc[1]))[0]

def find_walls(gex_dict, spot):
    calls = {k: v for k, v in gex_dict.items() if v > 0 and k > spot}
    puts = {k: v for k, v in gex_dict.items() if v < 0 and k < spot}
    cw = max(calls, key=calls.get) if calls else None
    pw = min(puts, key=puts.get) if puts else None
    return cw, pw

def sane_level(val_ym, spot_ym):
    if val_ym is None:
        return False
    return abs(val_ym - spot_ym) <= GAMMA_SANITY_PTS

# ==========================================================
#   PART 3 — CONFLUENCE DETECTOR
# ==========================================================
def find_confluences(levels, tol=CONFLUENCE_TOL):
    sorted_lv = sorted(levels, key=lambda x: x[1])
    clusters = []
    used = set()
    for i, (n1, v1) in enumerate(sorted_lv):
        if i in used:
            continue
        cluster = [(n1, v1)]
        used.add(i)
        for j in range(i + 1, len(sorted_lv)):
            if j in used:
                continue
            n2, v2 = sorted_lv[j]
            if abs(v2 - v1) <= tol:
                cluster.append((n2, v2))
                used.add(j)
        if len(cluster) >= 2:
            clusters.append(cluster)
    return clusters

# ==========================================================
#   PART 4 — CSV EXPORT FOR NINJATRADER
# ==========================================================
def export_csv(pivots, on, pw, pm, gamma_levels, regime, dia_spot, ym_price):
    csv_path = os.path.join(
        os.path.dirname(os.path.abspath(__file__)),
        "ym_levels.csv"
    )
    with open(csv_path, "w", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(["Name", "Price", "Type", "Color"])

        for name, val in pivots.items():
            if name.startswith("R"):
                color = "Red"
            elif name.startswith("S"):
                color = "Green"
            else:
                color = "Gray"
            writer.writerow([name, val, "Session", color])

        if on:
            writer.writerow(["ONH", on["ONH"], "Overnight", "Orange"])
            writer.writerow(["ONM", on["ON-Mid"], "Overnight", "Orange"])
            writer.writerow(["ONL", on["ONL"], "Overnight", "Orange"])

        if pw:
            writer.writerow(["PWH", pw["PWH"], "PriorWeek", "Blue"])
            writer.writerow(["PWM", pw["PW-Mid"], "PriorWeek", "Blue"])
            writer.writerow(["PWC", pw["PWC"], "PriorWeek", "Blue"])
            writer.writerow(["PWL", pw["PWL"], "PriorWeek", "Blue"])

        if pm:
            writer.writerow(["PMH", pm["PMH"], "PriorMonth", "Purple"])
            writer.writerow(["PMM", pm["PM-Mid"], "PriorMonth", "Purple"])
            writer.writerow(["PMC", pm["PMC"], "PriorMonth", "Purple"])
            writer.writerow(["PML", pm["PML"], "PriorMonth", "Purple"])

        for name, val in gamma_levels.items():
            if "Flip" in name:
                color = "Yellow"
            elif "Call" in name:
                color = "Magenta"
            else:
                color = "Cyan"
            writer.writerow([name, val, "Gamma", color])

        # ---- Meta rows the dashboard + logger read directly ----
        writer.writerow(["Regime", regime, "Meta", "White"])
        writer.writerow(["DIA_Spot", round(dia_spot, 2), "Meta", "White"])
        writer.writerow(["YM_Price", round(ym_price), "Meta", "White"])
        writer.writerow(["Updated",
                         datetime.now(ET).strftime("%Y-%m-%d %H:%M:%S"),
                         "Meta", "White"])

    return csv_path

# ==========================================================
#   MAIN
# ==========================================================
def main():
    now_et = datetime.now(ET)
    today = now_et.date()

    print("\n" + "=" * 60)
    print(f"  YM FULL LEVEL SHEET")
    print(f"  Generated: {now_et.strftime('%Y-%m-%d %I:%M %p')} ET")
    print("=" * 60)

    # ---- Session ----
    print("\n[1/4] Fetching YM futures data...")
    daily = fetch_ym_daily()
    intra, ym_price = fetch_ym_intraday()
    print(f"  Current YM: {ym_price:.0f}")

    yesterday = daily[-2]
    pivots = compute_pivots(yesterday)
    on = compute_overnight(intra, today)
    pw, pw_start, pw_end = compute_prior_week(daily, today)
    pm, pm_start, pm_end = compute_prior_month(daily, today)

    # ---- Gamma ----
    print("\n[2/4] Fetching DIA options chain (may take 20-40s)...")
    ref_dia = ym_price / DIA_TO_YM          # reliable DIA-equivalent from futures
    dia_spot, gex_all, gex_0dte, has_true_0dte, corrected = compute_gex(ref_dia)

    # Anchor sanity + regime to the FUTURES price, which we trust.
    spot_ym = ym_price

    flip_full = find_flip(gex_all)
    flip_0dte = find_flip(gex_0dte)
    cw_full, pw_full_wall = find_walls(gex_all, dia_spot)
    cw_0dte, pw_0dte = find_walls(gex_0dte, dia_spot)
    print(f"  DIA spot: {dia_spot:.2f}  ({round(dia_spot * DIA_TO_YM)} YM equiv)"
          + ("  [corrected from stale DIA close]" if corrected else ""))
    if not has_true_0dte:
        print("  NOTE: no true 0DTE expiry today; 'Flip 0DTE' uses nearest expiry.")

    # ---- Print sections ----
    print(f"\n--- Yesterday's Pivots ({yesterday['dt'].date()}) ---")
    for k, v in pivots.items():
        print(f"  {k:<8} {v}")

    if on:
        print(f"\n--- Overnight ({on['ONL']}-{on['ONH']}, {on['ONH'] - on['ONL']} pts) ---")
        for k, v in on.items():
            print(f"  {k:<8} {v}")

    if pw:
        print(f"\n--- Prior Week ({pw_start} -> {pw_end}) ---")
        for k, v in pw.items():
            print(f"  {k:<8} {v}")

    if pm:
        print(f"\n--- Prior Month ({pm_start} -> {pm_end}) ---")
        for k, v in pm.items():
            print(f"  {k:<8} {v}")

    print(f"\n--- Gamma Levels (from DIA options) ---")
    gamma_levels = {}

    def add_gamma(name, dia_val):
        if dia_val is None:
            return
        v = round(dia_val * DIA_TO_YM)
        if not sane_level(v, spot_ym):
            print(f"  {name:<15} {v}  [DROPPED: >{GAMMA_SANITY_PTS} pts from spot {spot_ym}]")
            return
        gamma_levels[name] = v
        print(f"  {name:<15} {v}")

    add_gamma("Gamma Flip", flip_full)
    add_gamma("Flip 0DTE", flip_0dte)
    add_gamma("Call Wall", cw_full)
    add_gamma("Call Wall 0DTE", cw_0dte)
    add_gamma("Put Wall", pw_full_wall)
    add_gamma("Put Wall 0DTE", pw_0dte)

    # ---- Regime: gamma flip first, else PIVOT FALLBACK ----
    regime_flip = None
    if "Gamma Flip" in gamma_levels:
        regime_flip = gamma_levels["Gamma Flip"]
    elif "Flip 0DTE" in gamma_levels:
        regime_flip = gamma_levels["Flip 0DTE"]

    if regime_flip is not None:
        regime = "POSITIVE" if spot_ym > regime_flip else "NEGATIVE"
        tag = "stable/mean-revert" if regime == "POSITIVE" else "volatile/trending"
        print(f"\n  Regime: {regime} ({tag})   [gamma flip {regime_flip}]")
    else:
        # No usable gamma flip today (thin/bad options). Fall back to structure:
        # price vs the daily Pivot. Keeps NinjaTrader stat buckets resolving.
        pvt = pivots["Pivot"]
        regime = "POSITIVE" if ym_price > pvt else "NEGATIVE"
        print(f"\n  Regime: {regime}   [PIVOT FALLBACK - no valid gamma flip; "
              f"price {round(ym_price)} vs Pivot {pvt}]")

    # ---- Confluence ----
    print(f"\n[3/4] Scanning for confluences (within {CONFLUENCE_TOL} pts)...")
    all_levels = []
    for k, v in pivots.items():
        all_levels.append((k, v))
    if on:
        for k, v in on.items():
            all_levels.append((k, v))
    if pw:
        for k, v in pw.items():
            all_levels.append((k, v))
    if pm:
        for k, v in pm.items():
            all_levels.append((k, v))
    for k, v in gamma_levels.items():
        all_levels.append((k, v))

    clusters = find_confluences(all_levels)

    print(f"\n--- CONFLUENCE ZONES ---")
    if clusters:
        for c in clusters:
            avg = round(sum(v for _, v in c) / len(c))
            names = ", ".join(n for n, _ in c)
            distance = avg - round(ym_price)
            direction = "above" if distance > 0 else "below"
            print(f"  YM {avg}  ({abs(distance)} pts {direction})  <- {names}")
    else:
        print("  No confluences found within tolerance.")

    # ---- CSV Export ----
    print(f"\n[4/4] Exporting to CSV for NinjaTrader...")
    csv_path = export_csv(pivots, on, pw, pm, gamma_levels,
                          regime, dia_spot, ym_price)
    print(f"  Levels exported to: {csv_path}")

    print("\n" + "=" * 60)
    print(f"  Current YM: {ym_price:.0f}   Regime: {regime}")
    print("=" * 60 + "\n")


if __name__ == "__main__":
    main()