import requests
from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

url = "https://query1.finance.yahoo.com/v8/finance/chart/YM=F"
# 3 months of daily bars — plenty for prior week and prior month
params = {"range": "3mo", "interval": "1d"}
headers = {"User-Agent": "Mozilla/5.0"}

response = requests.get(url, headers=headers, params=params)
data = response.json()

result     = data["chart"]["result"][0]
timestamps = result["timestamp"]
quotes     = result["indicators"]["quote"][0]

highs  = quotes["high"]
lows   = quotes["low"]
closes = quotes["close"]

et = ZoneInfo("America/New_York")

# Build a clean list of bars with real datetimes
bars = []
for i in range(len(timestamps)):
    if None in (highs[i], lows[i], closes[i]):
        continue
    bars.append({
        "dt":    datetime.fromtimestamp(timestamps[i], tz=et),
        "high":  highs[i],
        "low":   lows[i],
        "close": closes[i],
    })

now_et = datetime.now(et)
today = now_et.date()

# ---- Prior Week ----
# Monday of this week
this_monday = today - timedelta(days=today.weekday())
# Monday of last week
last_monday = this_monday - timedelta(days=7)
# Friday of last week
last_friday = last_monday + timedelta(days=4)

pw_bars = [b for b in bars if last_monday <= b["dt"].date() <= last_friday]

# ---- Prior Month ----
# First day of this month
first_of_this_month = today.replace(day=1)
# Last day of last month = one day before first of this month
last_of_prior_month = first_of_this_month - timedelta(days=1)
# First day of last month
first_of_prior_month = last_of_prior_month.replace(day=1)

pm_bars = [b for b in bars if first_of_prior_month <= b["dt"].date() <= last_of_prior_month]

# ---- Print Prior Week ----
print("=" * 55)
print(f"  YM Prior Week Levels")
print(f"  {last_monday} → {last_friday}")
print("=" * 55)
if pw_bars:
    pwh = max(b["high"] for b in pw_bars)
    pwl = min(b["low"]  for b in pw_bars)
    pwc = pw_bars[-1]["close"]  # Friday's close
    pw_mid = round((pwh + pwl) / 2)
    print(f"  Prior Week High (PWH):  {pwh:.0f}")
    print(f"  Prior Week Mid:         {pw_mid}")
    print(f"  Prior Week Low  (PWL):  {pwl:.0f}")
    print(f"  Prior Week Close (PWC): {pwc:.0f}")
    print(f"  Prior Week Range:       {pwh - pwl:.0f} pts")
else:
    print("  No bars found for prior week")
print("=" * 55)

# ---- Print Prior Month ----
print(f"\n  YM Prior Month Levels")
print(f"  {first_of_prior_month} → {last_of_prior_month}")
print("=" * 55)
if pm_bars:
    pmh = max(b["high"] for b in pm_bars)
    pml = min(b["low"]  for b in pm_bars)
    pmc = pm_bars[-1]["close"]
    pm_mid = round((pmh + pml) / 2)
    print(f"  Prior Month High (PMH):  {pmh:.0f}")
    print(f"  Prior Month Mid:         {pm_mid}")
    print(f"  Prior Month Low  (PML):  {pml:.0f}")
    print(f"  Prior Month Close (PMC): {pmc:.0f}")
    print(f"  Prior Month Range:       {pmh - pml:.0f} pts")
else:
    print("  No bars found for prior month")
print("=" * 55)