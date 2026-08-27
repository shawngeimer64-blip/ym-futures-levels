// =====================================================================================
//  YMLevelsLogger — Phase 1 data collection (v3: reads regime from CSV meta row)
// -------------------------------------------------------------------------------------
//  Watches price against the levels in ym_levels.csv and logs every interaction
//  (touch → outcome) to ym_interactions.csv. That log later feeds compute_stats.py.
//
//  v2 change: only logs LIVE (realtime) interactions. During historical back-processing
//  the levels don't correspond to the old bars, so those rows were garbage AND caused
//  massive duplicate inflation on every reload. Now every logged row is a true, live,
//  correctly-scored interaction with a valid regime tag.
//
//  v3 change: regime is now read from the "Regime" meta row written by
//  ym_full_levels.py, instead of being re-derived from a "Gamma Flip" level that
//  may not exist on thin-data days. Meta rows (Type == "Meta") are captured but NOT
//  treated as tradeable levels, so they never enter the touch loop. Falls back to
//  local derivation from the Gamma Flip level if the meta row is absent.
//
//  Definitions (all in YM points):
//    Touch   : price within TouchTolerance of a level (default 3)
//    Break   : price CLOSES BreakPoints beyond the level within OutcomeBars (default 15 / 6)
//    Reject  : price reverses RejectPoints away from the level within OutcomeBars (default 20)
//    Hold    : neither happens within OutcomeBars → logged as HOLD (still useful)
// =====================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class YMLevelsLogger : Indicator
    {
        #region Types
        private class LevelInfo
        {
            public string Name;
            public double Price;
            public string Type;
        }

        private class PendingInteraction
        {
            public LevelInfo Level;
            public int    TouchBar;
            public double TouchPrice;
            public string Regime;
            public int    TouchNumber;
            public double ApproachMomentum;
            public double DistFromPriorClose;
            public bool   ApproachedFromBelow;
            public double MaxUp;
            public double MaxDown;
        }
        #endregion

        #region State
        private List<LevelInfo> levels = new List<LevelInfo>();
        private DateTime lastFileWrite = DateTime.MinValue;
        private DateTime lastFileCheck = DateTime.MinValue;

        private List<PendingInteraction> pending = new List<PendingInteraction>();

        private Dictionary<string, int> touchCountToday = new Dictionary<string, int>();
        private DateTime currentSessionDate = DateTime.MinValue;

        private Dictionary<string, int> lastTouchBar = new Dictionary<string, int>();

        private double gammaFlipPrice = double.NaN;
        private double priorClose = double.NaN;
        private string csvRegime = "UNK";   // regime read straight from the CSV meta row

        private bool headerWritten;
        #endregion

        #region Lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Logs LIVE price interactions with YM levels to ym_interactions.csv for probability analysis";
                Name = "YMLevelsLogger";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;

                // Globals.UserDataDir is NT8's own "Documents\NinjaTrader 8", resolved
                // per-machine, so this needs no remapping on another box. These were
                // hard-coded to another user's profile, so the CSV never existed here
                // and the logger silently logged nothing. Must stay in step with
                // YMLevels.CsvPath and with the folder ym_full_levels.py writes to.
                string ymDir = System.IO.Path.Combine(
                    NinjaTrader.Core.Globals.UserDataDir, "ym_levels");
                CsvPath = System.IO.Path.Combine(ymDir, "ym_levels.csv");
                LogPath = System.IO.Path.Combine(ymDir, "ym_interactions.csv");

                TouchTolerance = 3;
                BreakPoints    = 15;
                RejectPoints   = 20;
                OutcomeBars    = 6;
                CooldownBars   = 12;
                RefreshSeconds = 30;
                VerbosePrint   = false;
            }
            else if (State == State.Configure)
            {
            }
            else if (State == State.DataLoaded)
            {
                LoadLevels();
                EnsureLogHeader();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 5) return;

            // v2 FIX: only log live interactions — no historical back-processing.
            // During Historical state the levels don't match the old bars, which
            // produced garbage rows, UNK regimes, and 86% duplicate inflation.
            if (State != State.Realtime) return;

            // Reload levels occasionally
            if ((DateTime.Now - lastFileCheck).TotalSeconds >= RefreshSeconds)
            {
                lastFileCheck = DateTime.Now;
                try
                {
                    if (File.Exists(CsvPath))
                    {
                        DateTime wt = File.GetLastWriteTime(CsvPath);
                        if (wt != lastFileWrite) LoadLevels();
                    }
                }
                catch { }
            }

            if (levels.Count == 0) return;

            // New session day → reset per-day counters
            DateTime sessDate = Time[0].Date;
            if (sessDate != currentSessionDate)
            {
                currentSessionDate = sessDate;
                touchCountToday.Clear();
                lastTouchBar.Clear();
            }

            UpdateRefs();

            double high = High[0];
            double low  = Low[0];
            double close = Close[0];

            ResolvePending(high, low, close);

            foreach (LevelInfo lv in levels)
            {
                bool touched = (low - TouchTolerance) <= lv.Price && (high + TouchTolerance) >= lv.Price;
                if (!touched) continue;

                int lastBar;
                if (lastTouchBar.TryGetValue(lv.Name, out lastBar) && (CurrentBar - lastBar) < CooldownBars)
                    continue;

                bool already = false;
                foreach (var p in pending) if (p.Level.Name == lv.Name) { already = true; break; }
                if (already) continue;

                RegisterTouch(lv, close);
                lastTouchBar[lv.Name] = CurrentBar;
            }
        }
        #endregion

        #region Refs / regime
        // NOTE: csvRegime is populated in LoadLevels() from the "Regime" meta row and
        // is intentionally NOT reset here — LoadLevels owns it. UpdateRefs only refreshes
        // the level-derived fallbacks (gammaFlipPrice, priorClose).
        private void UpdateRefs()
        {
            gammaFlipPrice = double.NaN;
            priorClose = double.NaN;

            foreach (LevelInfo lv in levels)
            {
                if (lv.Name == "Gamma Flip") gammaFlipPrice = lv.Price;
                if (lv.Name == "PWC")        priorClose = lv.Price;
            }
        }

        private string CurrentRegime(double spot)
        {
            // Prefer the regime the Python resolved and wrote to the CSV meta row.
            if (csvRegime == "POS" || csvRegime == "NEG") return csvRegime;
            // Fallback: derive locally from the Gamma Flip level if present.
            if (double.IsNaN(gammaFlipPrice)) return "UNK";
            return spot > gammaFlipPrice ? "POS" : "NEG";
        }
        #endregion

        #region Touch registration
        private void RegisterTouch(LevelInfo lv, double spot)
        {
            int tc;
            touchCountToday.TryGetValue(lv.Name, out tc);
            tc += 1;
            touchCountToday[lv.Name] = tc;

            double m = 0;
            if (CurrentBar >= 4)
            {
                double delta = Close[1] - Close[4];
                m = delta / 3.0;
            }

            var pi = new PendingInteraction
            {
                Level = lv,
                TouchBar = CurrentBar,
                TouchPrice = spot,
                Regime = CurrentRegime(spot),
                TouchNumber = tc,
                ApproachMomentum = Math.Round(m, 2),
                DistFromPriorClose = double.IsNaN(priorClose) ? 0 : Math.Round(spot - priorClose, 1),
                ApproachedFromBelow = spot <= lv.Price,
                MaxUp = 0,
                MaxDown = 0
            };
            pending.Add(pi);

            if (VerbosePrint)
                Print($"[touch] {lv.Name} @ {lv.Price:0} regime={pi.Regime} touch#{tc} mom={pi.ApproachMomentum}");
        }
        #endregion

        #region Outcome resolution
        private void ResolvePending(double high, double low, double close)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                PendingInteraction p = pending[i];
                double lvl = p.Level.Price;

                p.MaxUp   = Math.Max(p.MaxUp,   high - lvl);
                p.MaxDown = Math.Max(p.MaxDown, lvl - low);

                int barsSince = CurrentBar - p.TouchBar;

                bool brokeUp   = close >= lvl + BreakPoints;
                bool brokeDown = close <= lvl - BreakPoints;
                if (brokeUp || brokeDown)
                {
                    string dir = brokeUp ? "UP" : "DOWN";
                    LogInteraction(p, "BREAK", dir, close, barsSince);
                    pending.RemoveAt(i);
                    continue;
                }

                bool rejectedDown = p.MaxDown >= RejectPoints && close < lvl;
                bool rejectedUp   = p.MaxUp   >= RejectPoints && close > lvl;
                if (rejectedDown || rejectedUp)
                {
                    string dir = rejectedUp ? "UP" : "DOWN";
                    LogInteraction(p, "REJECT", dir, close, barsSince);
                    pending.RemoveAt(i);
                    continue;
                }

                if (barsSince >= OutcomeBars)
                {
                    LogInteraction(p, "HOLD", "NONE", close, barsSince);
                    pending.RemoveAt(i);
                    continue;
                }
            }
        }
        #endregion

        #region CSV logging
        private void EnsureLogHeader()
        {
            try
            {
                // The log lives beside ym_levels.csv; on a fresh machine that folder may
                // not exist yet, and StreamWriter throws rather than creating it.
                string dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (!File.Exists(LogPath))
                {
                    using (var sw = new StreamWriter(LogPath, false))
                    {
                        sw.WriteLine("timestamp,level_name,level_type,level_price,regime,touch_number,approach_momentum,dist_from_prior_close,approached_from_below,outcome,direction,resolve_price,bars_to_resolve,max_up,max_down");
                    }
                }
                headerWritten = true;
            }
            catch (Exception ex) { Print("YMLevelsLogger: header error " + ex.Message); }
        }

        private void LogInteraction(PendingInteraction p, string outcome, string direction, double resolvePrice, int barsToResolve)
        {
            try
            {
                if (!headerWritten) EnsureLogHeader();

                string row = string.Join(",", new string[]
                {
                    Time[0].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    Escape(p.Level.Name),
                    Escape(p.Level.Type),
                    p.Level.Price.ToString("0", CultureInfo.InvariantCulture),
                    p.Regime,
                    p.TouchNumber.ToString(CultureInfo.InvariantCulture),
                    p.ApproachMomentum.ToString("0.##", CultureInfo.InvariantCulture),
                    p.DistFromPriorClose.ToString("0.#", CultureInfo.InvariantCulture),
                    p.ApproachedFromBelow ? "1" : "0",
                    outcome,
                    direction,
                    resolvePrice.ToString("0", CultureInfo.InvariantCulture),
                    barsToResolve.ToString(CultureInfo.InvariantCulture),
                    p.MaxUp.ToString("0.#", CultureInfo.InvariantCulture),
                    p.MaxDown.ToString("0.#", CultureInfo.InvariantCulture)
                });

                using (var sw = new StreamWriter(LogPath, true))
                {
                    sw.WriteLine(row);
                }

                if (VerbosePrint)
                    Print($"[logged] {p.Level.Name} {outcome} {direction} bars={barsToResolve}");
            }
            catch (Exception ex) { Print("YMLevelsLogger: log error " + ex.Message); }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace(",", " ");
        }
        #endregion

        #region Load levels
        private void LoadLevels()
        {
            var newLevels = new List<LevelInfo>();
            string parsedRegime = "UNK";
            try
            {
                if (!File.Exists(CsvPath)) { Print("YMLevelsLogger: CSV not found " + CsvPath); return; }
                lastFileWrite = File.GetLastWriteTime(CsvPath);

                string[] lines = File.ReadAllLines(CsvPath);
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split(',');
                    if (parts.Length < 4) continue;

                    string nm = parts[0].Trim();
                    string ty = parts[2].Trim();

                    // Meta rows (Regime, DIA_Spot, YM_Price, Updated) aren't tradeable
                    // levels — capture regime and skip so they never enter the touch loop.
                    if (ty == "Meta")
                    {
                        if (nm == "Regime")
                        {
                            string r = parts[1].Trim().ToUpperInvariant();
                            if (r.StartsWith("POS")) parsedRegime = "POS";
                            else if (r.StartsWith("NEG")) parsedRegime = "NEG";
                            else parsedRegime = "UNK";
                        }
                        continue;
                    }

                    double price;
                    if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out price)) continue;

                    newLevels.Add(new LevelInfo
                    {
                        Name = nm,
                        Price = price,
                        Type = ty
                    });
                }

                levels = newLevels;
                csvRegime = parsedRegime;
                if (VerbosePrint) Print("YMLevelsLogger: loaded " + levels.Count + " levels, regime=" + csvRegime);
            }
            catch (Exception ex) { Print("YMLevelsLogger: load error " + ex.Message); }
        }
        #endregion

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Levels CSV Path", Order = 1, GroupName = "1. Files")]
        public string CsvPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Interaction Log Path", Order = 2, GroupName = "1. Files")]
        public string LogPath { get; set; }

        [NinjaScriptProperty] [Range(1, 50)]
        [Display(Name = "Touch Tolerance (pts)", Order = 1, GroupName = "2. Rules")]
        public int TouchTolerance { get; set; }

        [NinjaScriptProperty] [Range(1, 200)]
        [Display(Name = "Break Points (close beyond)", Order = 2, GroupName = "2. Rules")]
        public int BreakPoints { get; set; }

        [NinjaScriptProperty] [Range(1, 200)]
        [Display(Name = "Reject Points (reverse away)", Order = 3, GroupName = "2. Rules")]
        public int RejectPoints { get; set; }

        [NinjaScriptProperty] [Range(1, 50)]
        [Display(Name = "Outcome Window (bars)", Order = 4, GroupName = "2. Rules")]
        public int OutcomeBars { get; set; }

        [NinjaScriptProperty] [Range(1, 100)]
        [Display(Name = "Cooldown (bars between touches)", Order = 5, GroupName = "2. Rules")]
        public int CooldownBars { get; set; }

        [NinjaScriptProperty] [Range(5, 300)]
        [Display(Name = "Level Refresh (seconds)", Order = 6, GroupName = "2. Rules")]
        public int RefreshSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Verbose Print (debug)", Order = 7, GroupName = "2. Rules")]
        public bool VerbosePrint { get; set; }
        #endregion
    }
}


#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private YMLevelsLogger[] cacheYMLevelsLogger;
		public YMLevelsLogger YMLevelsLogger(string csvPath, string logPath, int touchTolerance, int breakPoints, int rejectPoints, int outcomeBars, int cooldownBars, int refreshSeconds, bool verbosePrint)
		{
			return YMLevelsLogger(Input, csvPath, logPath, touchTolerance, breakPoints, rejectPoints, outcomeBars, cooldownBars, refreshSeconds, verbosePrint);
		}

		public YMLevelsLogger YMLevelsLogger(ISeries<double> input, string csvPath, string logPath, int touchTolerance, int breakPoints, int rejectPoints, int outcomeBars, int cooldownBars, int refreshSeconds, bool verbosePrint)
		{
			if (cacheYMLevelsLogger != null)
				for (int idx = 0; idx < cacheYMLevelsLogger.Length; idx++)
					if (cacheYMLevelsLogger[idx] != null && cacheYMLevelsLogger[idx].CsvPath == csvPath && cacheYMLevelsLogger[idx].LogPath == logPath && cacheYMLevelsLogger[idx].TouchTolerance == touchTolerance && cacheYMLevelsLogger[idx].BreakPoints == breakPoints && cacheYMLevelsLogger[idx].RejectPoints == rejectPoints && cacheYMLevelsLogger[idx].OutcomeBars == outcomeBars && cacheYMLevelsLogger[idx].CooldownBars == cooldownBars && cacheYMLevelsLogger[idx].RefreshSeconds == refreshSeconds && cacheYMLevelsLogger[idx].VerbosePrint == verbosePrint && cacheYMLevelsLogger[idx].EqualsInput(input))
						return cacheYMLevelsLogger[idx];
			return CacheIndicator<YMLevelsLogger>(new YMLevelsLogger(){ CsvPath = csvPath, LogPath = logPath, TouchTolerance = touchTolerance, BreakPoints = breakPoints, RejectPoints = rejectPoints, OutcomeBars = outcomeBars, CooldownBars = cooldownBars, RefreshSeconds = refreshSeconds, VerbosePrint = verbosePrint }, input, ref cacheYMLevelsLogger);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.YMLevelsLogger YMLevelsLogger(string csvPath, string logPath, int touchTolerance, int breakPoints, int rejectPoints, int outcomeBars, int cooldownBars, int refreshSeconds, bool verbosePrint)
		{
			return indicator.YMLevelsLogger(Input, csvPath, logPath, touchTolerance, breakPoints, rejectPoints, outcomeBars, cooldownBars, refreshSeconds, verbosePrint);
		}

		public Indicators.YMLevelsLogger YMLevelsLogger(ISeries<double> input , string csvPath, string logPath, int touchTolerance, int breakPoints, int rejectPoints, int outcomeBars, int cooldownBars, int refreshSeconds, bool verbosePrint)
		{
			return indicator.YMLevelsLogger(input, csvPath, logPath, touchTolerance, breakPoints, rejectPoints, outcomeBars, cooldownBars, refreshSeconds, verbosePrint);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.YMLevelsLogger YMLevelsLogger(string csvPath, string logPath, int touchTolerance, int breakPoints, int rejectPoints, int outcomeBars, int cooldownBars, int refreshSeconds, bool verbosePrint)
		{
			return indicator.YMLevelsLogger(Input, csvPath, logPath, touchTolerance, breakPoints, rejectPoints, outcomeBars, cooldownBars, refreshSeconds, verbosePrint);
		}

		public Indicators.YMLevelsLogger YMLevelsLogger(ISeries<double> input , string csvPath, string logPath, int touchTolerance, int breakPoints, int rejectPoints, int outcomeBars, int cooldownBars, int refreshSeconds, bool verbosePrint)
		{
			return indicator.YMLevelsLogger(input, csvPath, logPath, touchTolerance, breakPoints, rejectPoints, outcomeBars, cooldownBars, refreshSeconds, verbosePrint);
		}
	}
}

#endregion
