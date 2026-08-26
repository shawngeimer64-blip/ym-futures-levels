import yfinance as yf
import numpy as np
from scipy.stats import norm
from datetime import datetime, date

def bs_gamma(S, K, T, r, sigma):
    if T <= 0 or sigma <= 0:
        return 0.0
    d1 = (np.log(S / K) + (r + 0.5 * sigma**2) * T) / (sigma * np.sqrt(T))
    return norm.pdf(d1) / (S * sigma * np.sqrt(T))

dia = yf.Ticker("DIA")
spot = dia.history(period="1d")["Close"].iloc[-1]
print(f"DIA spot: {spot:.2f}")
print(f"YM equiv: {round(spot * 100)}")

r = 0.043
CONTRACT_SIZE = 100

# ==========================================================
#   Loop over EVERY expiration, sum GEX per strike
# ==========================================================
all_expirations = dia.options
print(f"\nAggregating across {len(all_expirations)} expirations...")

gex_by_strike = {}
gex_0dte = {}  # separate bucket for 0DTE / nearest expiration

today = date.today()

for i, exp in enumerate(all_expirations):
    exp_date = datetime.strptime(exp, "%Y-%m-%d").date()
    days_to_exp = (exp_date - today).days
    
    # Skip anything more than 60 days out — too far to matter for dealer hedging
    if days_to_exp > 60:
        continue
    
    T = max(days_to_exp, 1) / 365.0
    
    try:
        chain = dia.option_chain(exp)
    except Exception as e:
        print(f"  skipping {exp}: {e}")
        continue

    is_0dte = (i == 0)  # nearest expiration = "0DTE bucket"

    for _, row in chain.calls.iterrows():
        K = row["strike"]; oi = row["openInterest"] or 0; iv = row["impliedVolatility"] or 0
        if oi == 0 or iv == 0: continue
        gamma = bs_gamma(spot, K, T, r, iv)
        gex = gamma * oi * CONTRACT_SIZE * spot * spot * 0.01
        gex_by_strike[K] = gex_by_strike.get(K, 0) + gex
        if is_0dte:
            gex_0dte[K] = gex_0dte.get(K, 0) + gex

    for _, row in chain.puts.iterrows():
        K = row["strike"]; oi = row["openInterest"] or 0; iv = row["impliedVolatility"] or 0
        if oi == 0 or iv == 0: continue
        gamma = bs_gamma(spot, K, T, r, iv)
        gex = gamma * oi * CONTRACT_SIZE * spot * spot * 0.01
        gex_by_strike[K] = gex_by_strike.get(K, 0) - gex
        if is_0dte:
            gex_0dte[K] = gex_0dte.get(K, 0) - gex

print(f"Aggregated {len(gex_by_strike)} unique strikes")

# ==========================================================
#   Top strikes overall
# ==========================================================
sorted_strikes = sorted(gex_by_strike.items(), key=lambda x: abs(x[1]), reverse=True)
print(f"\n─── Top 15 strikes by |GEX| (ALL expirations) ───")
print(f"  {'Strike':>8}  {'GEX ($M)':>10}  {'YM equiv':>10}")
print("  " + "─" * 32)
for K, gex in sorted_strikes[:15]:
    sign = "🟢" if gex > 0 else "🔴"
    print(f"  {K:>8.1f}  {gex/1_000_000:>10.2f}  {round(K*100):>10} {sign}")

# ==========================================================
#   Call Wall & Put Wall (full chain)
# ==========================================================
calls_gex = {k: v for k, v in gex_by_strike.items() if v > 0 and k > spot}
puts_gex  = {k: v for k, v in gex_by_strike.items() if v < 0 and k < spot}

call_wall = max(calls_gex, key=calls_gex.get) if calls_gex else None
put_wall  = min(puts_gex,  key=puts_gex.get)  if puts_gex  else None

# 0DTE walls
c0 = {k: v for k, v in gex_0dte.items() if v > 0 and k > spot}
p0 = {k: v for k, v in gex_0dte.items() if v < 0 and k < spot}
call_wall_0dte = max(c0, key=c0.get) if c0 else None
put_wall_0dte  = min(p0, key=p0.get) if p0 else None

# ==========================================================
#   Gamma Flip (full chain)
# ==========================================================
def find_flip(gex_dict):
    strikes = sorted(gex_dict.keys())
    cumulative = 0
    prev_K = None
    prev_cum = 0
    for K in strikes:
        cumulative += gex_dict[K]
        if prev_K is not None:
            if (prev_cum <= 0 and cumulative > 0) or (prev_cum >= 0 and cumulative < 0):
                frac = -prev_cum / (cumulative - prev_cum) if cumulative != prev_cum else 0
                return prev_K + frac * (K - prev_K)
        prev_K = K
        prev_cum = cumulative
    return None

flip_full = find_flip(gex_by_strike)
flip_0dte = find_flip(gex_0dte)

# ==========================================================
#   Print final summary
# ==========================================================
print(f"\n" + "=" * 55)
print(f"  YM GAMMA LEVELS")
print("=" * 55)
if flip_full:
    print(f"  🔶 Gamma Flip (full):  DIA {flip_full:.2f}  →  YM {round(flip_full*100)}")
if flip_0dte:
    print(f"  🔶 Gamma Flip (0DTE):  DIA {flip_0dte:.2f}  →  YM {round(flip_0dte*100)}")
if call_wall:
    print(f"  🔴 Call Wall (full):   DIA {call_wall}  →  YM {round(call_wall*100)}")
if call_wall_0dte:
    print(f"  🔴 Call Wall (0DTE):   DIA {call_wall_0dte}  →  YM {round(call_wall_0dte*100)}")
if put_wall:
    print(f"  🟢 Put Wall  (full):   DIA {put_wall}  →  YM {round(put_wall*100)}")
if put_wall_0dte:
    print(f"  🟢 Put Wall  (0DTE):   DIA {put_wall_0dte}  →  YM {round(put_wall_0dte*100)}")
print("=" * 55)

if flip_full:
    regime = "🟢 POSITIVE (stable)" if spot > flip_full else "🔴 NEGATIVE (volatile)"
    print(f"  Current DIA {spot:.2f} → Regime: {regime}")
print("=" * 55)