import requests
from datetime import datetime

# Same endpoint, but now we ask for a range of daily bars
url = "https://query1.finance.yahoo.com/v8/finance/chart/YM=F"

# Parameters: 1 month of history, in 1-day bars
params = {
    "range": "1mo",
    "interval": "1d"
}

headers = {"User-Agent": "Mozilla/5.0"}

response = requests.get(url, headers=headers, params=params)
data = response.json()

# Pull the arrays out of the response
result     = data["chart"]["result"][0]
timestamps = result["timestamp"]
quotes     = result["indicators"]["quote"][0]

opens  = quotes["open"]
highs  = quotes["high"]
lows   = quotes["low"]
closes = quotes["close"]

# Print each day, most recent last
print("=" * 60)
print(f"  YM Daily Bars — Last {len(timestamps)} Sessions")
print("=" * 60)
print(f"  {'Date':<12} {'Open':>10} {'High':>10} {'Low':>10} {'Close':>10}")
print("-" * 60)

for i in range(len(timestamps)):
    # Convert the unix timestamp to a readable date
    date = datetime.fromtimestamp(timestamps[i]).strftime("%Y-%m-%d")
    o = opens[i]
    h = highs[i]
    l = lows[i]
    c = closes[i]
    
    # Some bars can be missing (holidays, partial data) — skip those
    if o is None or h is None or l is None or c is None:
        continue
    
    print(f"  {date:<12} {o:>10.0f} {h:>10.0f} {l:>10.0f} {c:>10.0f}")

print("=" * 60)