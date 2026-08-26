import requests

url = "https://query1.finance.yahoo.com/v8/finance/chart/YM=F"
headers = {"User-Agent": "Mozilla/5.0"}

response = requests.get(url, headers=headers)
data = response.json()

meta = data["chart"]["result"][0]["meta"]

# Pull the inputs we need for pivot math
high  = meta["regularMarketDayHigh"]
low   = meta["regularMarketDayLow"]
close = meta["chartPreviousClose"]

# Classic floor trader pivot formulas
pivot = (high + low + close) / 3
r1 = (2 * pivot) - low
s1 = (2 * pivot) - high
r2 = pivot + (high - low)
s2 = pivot - (high - low)

# Round to 1 point (YM ticks in 1-point increments)
pivot = round(pivot)
r1    = round(r1)
s1    = round(s1)
r2    = round(r2)
s2    = round(s2)

# Print the ladder — resistance at top, support at bottom
print("=" * 40)
print(f"  YM Pivot Levels for Next Session")
print("=" * 40)
print(f"  R2:      {r2}")
print(f"  R1:      {r1}")
print(f"  Pivot:   {pivot}")
print(f"  S1:      {s1}")
print(f"  S2:      {s2}")
print("=" * 40)
print(f"  Based on:  H {high}  L {low}  C {close}")
print("=" * 40)
