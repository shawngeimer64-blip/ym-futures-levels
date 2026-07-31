# YM Levels

A pre-market levels and confluence system for the YM (Dow) futures, built for
NinjaTrader 8. A small Python tool computes session, gamma, and confluence
levels each morning and writes them to CSV; a NinjaTrader indicator draws those
levels on the chart and shows a live dashboard with regime, bias, a break/reject
probability read as price approaches each level, a confirmation lean, and an
optional entry-signal system.

---

## ⚠️ Disclaimer — read this first

**This software is for educational and informational purposes only. It is not
financial advice, and nothing it produces is a recommendation to buy or sell
anything.**

- Trading futures involves substantial risk of loss and is not suitable for
  every investor.
- The probabilities and signals shown are derived from the tool's own logged
  historical data on your machine. Past performance does not predict future
  results, and small sample sizes can be misleading.
- The options-derived gamma levels depend on third-party data (DIA options via
  Yahoo Finance) that can be sparse, delayed, or wrong on any given day.
- You are solely responsible for any trades you place. The authors accept no
  liability for any losses. Use at your own risk, and validate everything
  yourself before risking real money.

---

## What it does

- **Session levels** — pivots, overnight high/low/mid, prior week and prior
  month levels from YM futures data.
- **Gamma levels** — gamma flip, call wall, put wall (and 0DTE variants) derived
  from DIA options, with sanity guards for bad-data mornings and a pivot-based
  regime fallback when the options are unusable.
- **Confluence detection** — where multiple levels stack up near price.
- **Live dashboard** — regime (positive/negative gamma), bull/bear bias, day
  range position, nearby confluence zones with historical break/reject odds, a
  confirmation lean (EMA slope + ADX + RSI, optionally combined with zone
  structure), and a freshness stamp confirming the day's levels updated.
- **Break/reject probabilities** — a logger records how price interacts with each
  level over time; a stats script turns that log into per-bucket break/reject
  percentages the dashboard reads back.
- **Entry signals** — optional chart arrows, sound alerts, and a dashboard box
  when the empirical odds and the confirmation lean agree at an approaching level.

## Requirements

- **Python 3.9+** (for `zoneinfo`) — for the levels/stats scripts.
- **NinjaTrader 8** — for the chart indicators.
- Python packages listed in `requirements.txt`.

## Files

| File | What it is |
|------|------------|
| `ym_full_levels.py` | Pre-market script: computes all levels, writes `ym_levels.csv`. |
| `compute_stats.py` | Turns the interaction log into `ym_stats.csv` (break/reject odds). |
| `YMLevels.cs` | NinjaTrader indicator: draws levels + the dashboard. |
| `YMLevelsLogger.cs` | NinjaTrader indicator: logs price/level interactions for the stats. |

The `.csv` files the system generates are intentionally **not** included — they
are recreated on your machine when you run the scripts.

## Setup

### 1. Python side

Install the dependencies (one command):

```
pip install -r requirements.txt
```

Put `ym_full_levels.py` and `compute_stats.py` in a folder of your choice. The
scripts read and write CSVs **next to themselves**, so wherever you place them is
where the data files will appear. A common choice is `Documents\ym_levels`.

Run the levels script before the session (it fetches futures + options data):

```
python ym_full_levels.py
```

This creates `ym_levels.csv` in the same folder.

### 2. NinjaTrader side

1. Copy `YMLevels.cs` and `YMLevelsLogger.cs` into your NinjaTrader indicators
   folder:
   `Documents\NinjaTrader 8\bin\Custom\Indicators\`
2. In NinjaTrader, open the NinjaScript Editor and press **F5** to compile.
3. Add **YMLevels** (and optionally **YMLevelsLogger**) to a YM chart.
4. In the indicator settings, set **CSV Path** and **Stats CSV Path** to point at
   the folder where your Python scripts write their CSVs. (The default assumes
   `Documents\ym_levels`.)

### 3. Building the probability stats over time

The break/reject percentages come from your own logged data, so they start empty
and fill in as the logger runs during live sessions. Periodically run:

```
python compute_stats.py
```

to rebuild `ym_stats.csv` from the accumulated interaction log. The dashboard
picks up the refreshed stats automatically.

## Daily workflow

1. **Pre-market:** run `python ym_full_levels.py` to generate the day's levels.
2. **During the session:** the YMLevels indicator draws the levels and dashboard;
   the logger records interactions.
3. **Occasionally:** run `python compute_stats.py` to refresh the probability
   buckets from the growing log.

## Notes and limitations

- Gamma/options data quality varies day to day. On thin or bad mornings the tool
  drops nonsensical gamma levels and falls back to a pivot-based regime so the
  dashboard still resolves — but that day's "regime" is structural, not gamma.
- The probability buckets need enough samples (default 30) before a percentage is
  treated as trustworthy; until then the dashboard says it's still collecting.
- This was built and tested for YM specifically. Other instruments would need the
  level logic and tick sizes reviewed.

## License

MIT — see [LICENSE](LICENSE). You're free to use, modify, and share this. It
comes with no warranty; see the disclaimer above.
