import requests

# YM futures symbol on Yahoo Finance is "YM=F"
url = "https://query1.finance.yahoo.com/v8/finance/chart/YM=F"

# Yahoo blocks requests that don't look like a browser, so we add a header
headers = {"User-Agent": "Mozilla/5.0"}

# Fetch the data
response = requests.get(url, headers=headers)
data = response.json()

# Dig into the response to find the current price
price = data["chart"]["result"][0]["meta"]["regularMarketPrice"]

print(f"YM current price: {price}")
