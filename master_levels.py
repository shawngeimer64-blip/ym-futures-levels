import requests
from datetime import datetime, time, timedelta
from zoneinfo import ZoneInfo

# ============================================================
#   YM MASTER LEVELS
#   Pulls all levels for the next YM trading session
# ============================================================

et = ZoneInfo("America/New_York")
headers = {"User-Agent": "Mozilla/5.0"}
url = "https://query1.finance.yahoo.com/v8/finance/chart/YM=F"

# ---------- Fetch daily bars (3 months) ----------
daily_resp = requests.get(url, headers=headers,
                          params={"range": "3mo", "interval": "1d"})
daily_data = daily_resp.json()
d_result = daily_data["chart"]["result"][0]
d_ts     = d_result["timestamp"]
d_q      = d_result["indicators"]["quote"][0]

daily_bars = []
for i in range(len(d_ts)):
    if None in (d_q["open"][i], d_q["high"][i], d_q["low"][i], d_q["close"][i]):
        continue
    daily_bars.append({
        "dt":    datetime.fromtimestamp(d_ts[i], tz=et),
        "open":  d_q["open"][i],
        "high":  d_q["high"][i],
        "low":   d_q["low"][i],
        "close": d_q["close"][i],
    })

# ---------- Fetch intraday bars (5 days, 5-min) ----------
intra_resp = requests.get(url, headers=headers,
                          params={"range": "5d", "interval": "5m"})
intra_data = intra_resp.json()
i_result = intra_data["chart"]["result"][0]
i_ts     = i_result["timestamp"]
i_q      = i_result["indicators"]["quote"][0]

intra_bars = []
for i in range(len(i_ts)):
    if i_q["high"][i] is None or i_q["low"][i] is None:
        continue
    intra_bars.append({
        "dt":   datetime.fromtimestamp(i_ts[i], tz=et),
        "high": i_q["high"][i],
        "low":  i_q["low"][i],
    })

# Current price for reference
current_price = intra_data["chart"]["result"][0]["meta"]["regularMarketPrice"]

# ============================================================
#   CALCULATE EVERYTHING
# ============================================================

now_et = datetime.now(et)
today = now_et.date()

# ---- Yesterday's pivots ----
yesterday_bar = daily_bars[-2]
y_h = yesterday_bar["high"]
y_l = yesterday_bar["low"]
y_c = yesterday_bar["close"]

pivot = round((y_h + y_l + y_c) / 3)
r1 = round((2 * pivot) - y_l)
s1 = round((2 * pivot) - y_h)
r2 = round(pivot + (y_h - y_l))
s2 = round(pivot - (y_h - y_l))
r3 = round(y_h + 2 * (pivot - y_l))
s3 = round(y_l - 2 * (y_h - pivot))
y_mid = round((y_h + y_l) / 2)

# ---- Overnight ----
on_start = datetime.combine(today - timedelta(days=1), time(18, 0), tzinfo=et)
on_end   = datetime.combine(today, time(9, 30), tzinfo=et)
on_bars = [b for b in intra_bars if on_start <= b["dt"] < on_end]

if on_bars:
    onh = round(max(b["high"] for b in on_bars))
    onl = round(min(b["low"]  for b in on_bars))
    on_mid = round((onh + onl) / 2)
else:
    onh = onl = on_mid = None

# ---- Prior Week ----
this_monday = today - timedelta(days=today.weekday())
last_monday = this_monday - timedelta(days=7)
last_friday = last_monday + timedelta(days=4)
pw_bars = [b for b in daily_bars if last_monday <= b["dt"].date() <= last_friday]

if pw_bars:
    pwh = round(max(b["high"] for b in pw_bars))
    pwl = round(min(b["low"]  for b in pw_bars))
    pwc = round(pw_bars[-1]["close"])
    pw_mid = round((pwh + pwl) / 2)
else:
    pwh = pwl = pwc = pw_mid = None

# ---- Prior Month ----
first_this = today.replace(day=1)
last_prior = first_this - timedelta(days=1)
first_prior = last_prior.replace(day=1)
pm_bars = [b for b in daily_bars if first_prior <= b["dt"].date() <= last_prior]

if pm_bars:
    pmh = round(max(b["high"] for b in pm_bars))
    pml = round(min(b["low"]  for b in pm_bars))
    pmc = round(pm_bars[-1]["close"])
    pm_mid = round((pmh + pml) / 2)
else:
    pmh = pml = pmc = pm_mid = None

# ============================================================
#   PRINT THE UNIFIED LEVEL SHEET
# ============================================================

print()
print("╔" + "═" * 55 + "╗")
print("║" + f"  YM MASTER LEVEL SHEET".ljust(55) + "║")
print("║" + f"  Generated: {now_et.strftime('%Y-%m-%d %I:%M %p')} ET".ljust(55) + "║")
print("║" + f"  Current YM: {current_price:.0f}".ljust(55) + "║")
print("╚" + "═" * 55 + "╝")

print(f"\n─── Yesterday's Pivots ({yesterday_bar['dt'].date()}) ───")
print(f"  R3          {r3}")
print(f"  R2          {r2}")
print(f"  R1          {r1}")
print(f"  Pivot       {pivot}")
print(f"  Y-Mid       {y_mid}")
print(f"  S1          {s1}")
print(f"  S2          {s2}")
print(f"  S3          {s3}")

print(f"\n─── Overnight Session ───")
if onh:
    print(f"  ONH         {onh}")
    print(f"  ON-Mid      {on_mid}")
    print(f"  ONL         {onl}")
    print(f"  ON Range    {onh - onl} pts")
else:
    print("  (no overnight data available)")

print(f"\n─── Prior Week ({last_monday} → {last_friday}) ───")
if pwh:
    print(f"  PWH         {pwh}")
    print(f"  PW-Mid      {pw_mid}")
    print(f"  PWC         {pwc}")
    print(f"  PWL         {pwl}")
    print(f"  PW Range    {pwh - pwl} pts")

print(f"\n─── Prior Month ({first_prior} → {last_prior}) ───")
if pmh:
    print(f"  PMH         {pmh}")
    print(f"  PM-Mid      {pm_mid}")
    print(f"  PMC         {pmc}")
    print(f"  PML         {pml}")
    print(f"  PM Range    {pmh - pml} pts")

print()