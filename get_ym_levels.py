import requests

url = "https://query1.finance.yahoo.com/v8/finance/chart/YM=F"
headers = {"User-Agent": "Mozilla/5.0"}

response = requests.get(url, headers=headers)
data = response.json()

# The "meta" section holds all the key session values
meta = data["chart"]["result"][0]["meta"]

# Pull the useful ones
current_price   = meta["regularMarketPrice"]
prior_close     = meta["chartPreviousClose"]
day_high        = meta["regularMarketDayHigh"]
day_low         = meta["regularMarketDayLow"]
fifty_two_high  = meta["fiftyTwoWeekHigh"]
fifty_two_low   = meta["fiftyTwoWeekLow"]

# Print them out in a clean format
print("=" * 40)
print(f"  YM Futures — Key Levels")
print("=" * 40)
print(f"  Current Price:     {current_price}")
print(f"  Prior Close:       {prior_close}")
print(f"  Today's High:      {day_high}")
print(f"  Today's Low:       {day_low}")
print(f"  52-Week High:      {fifty_two_high}")
print(f"  52-Week Low:       {fifty_two_low}")
print("=" * 40)
