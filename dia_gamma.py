import yfinance as yf
import numpy as np
from scipy.stats import norm
from datetime import datetime, date

# ==========================================================
#   Black-Scholes gamma for a single option
#   S = spot price, K = strike, T = time to expiry (years),
#   r = risk-free rate, sigma = implied volatility
# ==========================================================
def bs_gamma(S, K, T, r, sigma):
    if T <= 0 or sigma <= 0:
        return 0.0
    d1 = (np.log(S / K) + (r + 0.5 * sigma**2) * T) / (sigma * np.sqrt(T))
    return norm.pdf(d1) / (S * sigma * np.sqrt(T))

# ==========================================================
#   Fetch DIA options
# ==========================================================
dia = yf.Ticker("DIA")
spot = dia.history(period="1d")["Close"].iloc[-1]
print(f"DIA spot: {spot:.2f}")

# Use the nearest expiration for now
nearest_exp = dia.options[0]
print(f"Expiration: {nearest_exp}")

# Time to expiration in years (approximate — trading days would be more precise)
exp_date = datetime.strptime(nearest_exp, "%Y-%m-%d").date()
days_to_exp = (exp_date - date.today()).days
T = max(days_to_exp, 1) / 365.0  # avoid divide-by-zero on 0DTE
print(f"Days to expiration: {days_to_exp}  (T = {T:.4f} years)")

# Risk-free rate (rough — 10Y yield ~4.3%)
r = 0.043

chain = dia.option_chain(nearest_exp)
calls = chain.calls
puts  = chain.puts

# ==========================================================
#   Compute GEX per strike
#   GEX = gamma × open_interest × contract_multiplier × spot^2 × 0.01
#   Calls contribute POSITIVE gamma to dealers (long calls)
#   Puts contribute NEGATIVE gamma to dealers (short puts)
# ==========================================================
CONTRACT_SIZE = 100  # standard equity option

gex_by_strike = {}

# Process calls (positive contribution)
for _, row in calls.iterrows():
    K = row["strike"]
    oi = row["openInterest"] or 0
    iv = row["impliedVolatility"] or 0
    if oi == 0 or iv == 0:
        continue
    gamma = bs_gamma(spot, K, T, r, iv)
    gex = gamma * oi * CONTRACT_SIZE * spot * spot * 0.01
    gex_by_strike[K] = gex_by_strike.get(K, 0) + gex

# Process puts (negative contribution)
for _, row in puts.iterrows():
    K = row["strike"]
    oi = row["openInterest"] or 0
    iv = row["impliedVolatility"] or 0
    if oi == 0 or iv == 0:
        continue
    gamma = bs_gamma(spot, K, T, r, iv)
    gex = gamma * oi * CONTRACT_SIZE * spot * spot * 0.01
    gex_by_strike[K] = gex_by_strike.get(K, 0) - gex

# ==========================================================
#   Show top strikes by absolute GEX
# ==========================================================
sorted_strikes = sorted(gex_by_strike.items(), key=lambda x: abs(x[1]), reverse=True)

print(f"\n─── Top 15 strikes by |GEX| ───")
print(f"  {'Strike':>8}  {'GEX ($M)':>10}  {'YM equiv':>10}")
print("  " + "─" * 32)
for K, gex in sorted_strikes[:15]:
    ym_equiv = round(K * 100)
    sign = "🟢" if gex > 0 else "🔴"
    print(f"  {K:>8.1f}  {gex/1_000_000:>10.2f}  {ym_equiv:>10} {sign}")

# ==========================================================
#   Find Call Wall and Put Wall
# ==========================================================
calls_gex = {k: v for k, v in gex_by_strike.items() if v > 0 and k > spot}
puts_gex  = {k: v for k, v in gex_by_strike.items() if v < 0 and k < spot}

if calls_gex:
    call_wall = max(calls_gex, key=calls_gex.get)
    print(f"\n🔴 CALL WALL: DIA {call_wall}  →  YM {round(call_wall * 100)}")
if puts_gex:
    put_wall = min(puts_gex, key=puts_gex.get)
    print(f"🟢 PUT WALL:  DIA {put_wall}  →  YM {round(put_wall * 100)}")# ==========================================================
#   Gamma Flip: the spot price where cumulative GEX crosses zero
# ==========================================================
# Sort strikes ascending
strikes_sorted = sorted(gex_by_strike.keys())

# Sweep spot from low to high, summing GEX across ALL strikes
# The flip is where the cumulative sum changes sign
cumulative = 0
prev_strike = None
prev_cumulative = 0
gamma_flip = None

for K in strikes_sorted:
    cumulative += gex_by_strike[K]
    # Check if we just crossed zero
    if prev_strike is not None:
        if (prev_cumulative <= 0 and cumulative > 0) or (prev_cumulative >= 0 and cumulative < 0):
            # Linear interpolation between prev_strike and K
            frac = -prev_cumulative / (cumulative - prev_cumulative)
            gamma_flip = prev_strike + frac * (K - prev_strike)
            break
    prev_strike = K
    prev_cumulative = cumulative

if gamma_flip is not None:
    regime = "🟢 POSITIVE GAMMA (stable)" if spot > gamma_flip else "🔴 NEGATIVE GAMMA (volatile)"
    print(f"\n🔶 GAMMA FLIP: DIA {gamma_flip:.2f}  →  YM {round(gamma_flip * 100)}")
    print(f"   Spot DIA {spot:.2f} is {'ABOVE' if spot > gamma_flip else 'BELOW'} the flip")
    print(f"   Regime: {regime}")
else:
    print("\n🔶 GAMMA FLIP: could not locate (cumulative GEX did not cross zero)")