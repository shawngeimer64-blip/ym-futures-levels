import yfinance as yf

# DIA is the SPDR Dow Jones Industrial Average ETF
# 1 DIA point ≈ 100 YM points (roughly)
dia = yf.Ticker("DIA")

# What expirations are available?
expirations = dia.options
print("Available expirations:")
for exp in expirations[:10]:  # first 10 only, list can be long
    print(f"  {exp}")

# Grab the nearest expiration (this is 0DTE or closest to it)
nearest_exp = expirations[0]
print(f"\nUsing nearest expiration: {nearest_exp}")

# Fetch the option chain for that expiration
chain = dia.option_chain(nearest_exp)
calls = chain.calls
puts  = chain.puts

# What columns exist? What does one row look like?
print(f"\nCalls: {len(calls)} strikes")
print(f"Puts:  {len(puts)} strikes")

print(f"\nColumns available for each option:")
print(f"  {list(calls.columns)}")

# Show a few calls near the money
current_price = dia.info.get("regularMarketPrice") or dia.history(period="1d")["Close"].iloc[-1]
print(f"\nCurrent DIA price: {current_price:.2f}")

# Filter to strikes within ±$5 of current price
near_calls = calls[(calls["strike"] > current_price - 5) & (calls["strike"] < current_price + 5)]
near_puts  = puts[(puts["strike"] > current_price - 5) & (puts["strike"] < current_price + 5)]

print(f"\n--- Nearby CALL strikes ---")
print(near_calls[["strike", "lastPrice", "openInterest", "impliedVolatility"]].to_string(index=False))

print(f"\n--- Nearby PUT strikes ---")
print(near_puts[["strike", "lastPrice", "openInterest", "impliedVolatility"]].to_string(index=False))