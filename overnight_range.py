import requests
from datetime import datetime, time, timedelta
from zoneinfo import ZoneInfo

url = "https://query1.finance.yahoo.com/v8/finance/chart/YM=F"
# 5-day range, 5-minute bars — plenty of data for last night's overnight session
params = {"range": "5d", "interval": "5m"}
headers = {"User-Agent": "Mozilla/5.0"}

response = requests.get(url, headers=headers, params=params)
data = response.json()

result     = data["chart"]["result"][0]
timestamps = result["timestamp"]
quotes     = result["indicators"]["quote"][0]

highs = quotes["high"]
lows  = quotes["low"]

# ET timezone — futures sessions are defined in Eastern Time
et = ZoneInfo("America/New_York")

# Build a clean list of bars, each with a proper ET datetime
bars = []
for i in range(len(timestamps)):
    if highs[i] is None or lows[i] is None:
        continue
    dt_et = datetime.fromtimestamp(timestamps[i], tz=et)
    bars.append({
        "dt":   dt_et,
        "high": highs[i],
        "low":  lows[i],
    })

# Figure out "today" in ET
now_et = datetime.now(et)
today_date = now_et.date()

# Overnight window: yesterday 6:00 PM ET  →  today 9:30 AM ET
overnight_start = datetime.combine(today_date - timedelta(days=1), time(18, 0), tzinfo=et)
overnight_end   = datetime.combine(today_date, time(9, 30), tzinfo=et)

# Filter bars into the overnight window
overnight_bars = [b for b in bars if overnight_start <= b["dt"] < overnight_end]

if not overnight_bars:
    print("No overnight bars found (might be a weekend or holiday).")
else:
    onh = max(b["high"] for b in overnight_bars)
    onl = min(b["low"]  for b in overnight_bars)
    on_mid = round((onh + onl) / 2)

    print("=" * 55)
    print(f"  YM Overnight Session")
    print(f"  {overnight_start.strftime('%a %b %d %I:%M %p')} → {overnight_end.strftime('%a %b %d %I:%M %p')} ET")
    print("=" * 55)
    print(f"  Overnight High (ONH):  {onh:.0f}")
    print(f"  Overnight Mid:         {on_mid}")
    print(f"  Overnight Low (ONL):   {onl:.0f}")
    print(f"  Overnight Range:       {onh - onl:.0f} pts")
    print(f"  Bars counted:          {len(overnight_bars)}")
    print("=" * 55)