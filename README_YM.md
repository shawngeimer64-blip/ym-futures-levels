# YM Levels — NinjaTrader 8 Level-Break Trading System

A NinjaTrader 8 system for **YM (E-mini Dow)** that computes daily support/resistance
and options-gamma levels in Python, draws them on the chart, and trades a "star" (★)
level-break signal.

There is also an ES (E-mini S&P 500) sibling of this system. This repo is the YM version.

## What's in here

**Python (levels engine — run these outside NinjaTrader):**

| File | What it does |
|------|--------------|
| `ym_full_levels.py` | Fetches YM futures + DIA options, computes session / overnight / prior-week / prior-month pivots and gamma walls, writes `ym_levels.csv`. |
| `compute_stats.py` | Turns logged level interactions (`ym_interactions.csv`) into break/reject/hold probabilities (`ym_stats.csv`). |
| `run_levels_loop.py` | Optional: re-runs the levels script on a timer through the session. |
| `run_levels.bat` | Optional double-click launcher (runs levels + stats). Portable — no hard-coded paths. |

**NinjaScript (`.cs` — go in NinjaTrader):**

| File | Type | What it does |
|------|------|--------------|
| `YMLevels.cs` | Indicator | Draws the levels + an on-chart dashboard, prints the ★ signal, exposes a `StarSignal` plot. |
| `Polaris.cs` | Strategy | Trades the ★ signal (self-contained, with its own dashboard, gates, and risk controls). |
| `YMLevelsLogger.cs` | Indicator | Logs every price/level interaction to `ym_interactions.csv` so the stats can build. |

## Requirements

- **NinjaTrader 8**
- **Python 3.10+** with the packages in `requirements.txt`:
  ```
  pip install -r requirements.txt
  ```

## Setup

### 1. Python side

Put the Python files anywhere you like (e.g. a `ym_levels` folder). Then run:

```
python ym_full_levels.py
```

This writes `ym_levels.csv` into your NinjaTrader user folder automatically:
`Documents\NinjaTrader 8\ym_levels\ym_levels.csv`
(the folder is created on first run — you don't make it yourself).

To also build the probability stats, run:

```
python compute_stats.py
```

Or just double-click `run_levels.bat`, which runs both.

### 2. NinjaTrader side

1. Copy the indicators into `Documents\NinjaTrader 8\bin\Custom\Indicators\`:
   - `YMLevels.cs`
   - `YMLevelsLogger.cs`
2. Copy the strategy into `Documents\NinjaTrader 8\bin\Custom\Strategies\`:
   - `Polaris.cs`
3. Open the NinjaScript Editor and press **F5** to compile.
4. On a YM chart, add:
   - **YMLevels** indicator (levels + dashboard)
   - **YMLevelsLogger** indicator (builds the stats over time)
   - **Polaris** strategy (test in **Sim** first)

## How the pieces connect

```
ym_full_levels.py  ──writes──►  ym_levels.csv  ──read by──►  YMLevels / Polaris  (draw + trade)
YMLevelsLogger     ──writes──►  ym_interactions.csv  ──read by──►  compute_stats.py  ──writes──►  ym_stats.csv  ──read by──►  YMLevels (break-odds display)
```

The stats start empty and **build up over live sessions** — the logger only records
interactions while it's running on a live chart. Break-odds show "building" until enough
data accumulates.

## Notes on the levels

- Levels come from **YM futures** (price) and **DIA options** (gamma walls) via `yfinance`.
- The star (★) fires three ways: a level break with the trend, a reversal (price reclaims
  the trend EMA), and a pullback-and-resume continuation. All are tunable in the indicator
  settings.
- This system was developed on a **Renko** chart (ninZaRenko 50/4) but the levels and star
  logic work on any chart type; tick-based thresholds adapt to the instrument.

## Disclaimer

This is trading software provided as-is, for educational purposes. Test in simulation before
using real money. Trading futures involves substantial risk of loss. Nothing here is financial
advice, and no outcome is guaranteed.
