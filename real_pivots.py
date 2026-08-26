import requests
from datetime import datetime

url = "https://query1.finance.yahoo.com/v8/finance/chart/YM=F"
params = {"range": "1mo", "interval": "1d"}
headers = {"User-Agent": "Mozilla/5.0"}

response = requests.get(url, headers=headers, params=params)
data = response.json()

result     = data["chart"]["result"][0]
timestamps = result["timestamp"]
quotes     = result["indicators"]["quote"][0]

opens  = quotes["open"]
highs  = quotes["high"]
lows   = quotes["low"]
closes = quotes["close"]

# Build a clean list of only valid bars (skip any missing days)
bars = []
for i in range(len(timestamps)):
    if None in (opens[i], highs[i], lows[i], closes[i]):
        continue
    bars.append({
        "date":  datetime.fromtimestamp(timestamps[i]).strftime("%Y-%m-%d"),
        "open":  opens[i],
        "high":  highs[i],
        "low":   lows[i],
        "close": closes[i],
    })

# Grab yesterday (2nd-to-last) and today (last) for context
yesterday = bars[-2]
today     = bars[-1]

# Pivot math — from yesterday's completed session
h = yesterday["high"]
l = yesterday["low"]
c = yesterday["close"]

pivot = (h + l + c) / 3
r1 = (2 * pivot) - l
s1 = (2 * pivot) - h
r2 = pivot + (h - l)
s2 = pivot - (h - l)
r3 = h + 2 * (pivot - l)
s3 = l - 2 * (h - pivot)

# Also useful: yesterday's midpoint (aka the "50% level")
mid = (h + l) / 2

# Round everything to whole points (YM's tick size)
pivot = round(pivot); mid = round(mid)
r1 = round(r1); r2 = round(r2); r3 = round(r3)
s1 = round(s1); s2 = round(s2); s3 = round(s3)

# Print
print("=" * 55)
print(f"  YM Levels for Next Session")
print(f"  Based on {yesterday['date']}: H {h:.0f}  L {l:.0f}  C {c:.0f}")
print("=" * 55)
print(f"  R3:           {r3}")
print(f"  R2:           {r2}")
print(f"  R1:           {r1}")
print(f"  Pivot:        {pivot}")
print(f"  Mid (yest):   {mid}")
print(f"  S1:           {s1}")
print(f"  S2:           {s2}")
print(f"  S3:           {s3}")
print("=" * 55)
print(f"  Today so far: O {today['open']:.0f}  H {today['high']:.0f}  L {today['low']:.0f}  C {today['close']:.0f}")
print("=" * 55)