// =====================================================================================
//  YMLevels — Lines + Dashboard  (v11: live "APPROACHING" break/reject probabilities)
// =====================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum YMLevelsCorner { TopLeft, TopRight, BottomLeft, BottomRight }

    public class YMLevels : Indicator
    {
        #region State
        private class LevelInfo
        {
            public string Name;
            public double Price;
            public string Type;
            public string ColorName;
            public Brush Color;
            public DashStyleHelper Dash;
            public int Width;
        }

        private class StatRow
        {
            public string LevelType, Regime, TouchBucket;
            public int N;
            public double BreakPct, RejectPct, HoldPct;
        }

        // v13 — a confluence zone paired with blended break/reject odds
        private class ConfInfo
        {
            public double Price;
            public string Names;
            public double Brk, Rej;
            public int MinN;
            public bool Trust;
            public bool HasOdds;
        }

        private List<LevelInfo> levels = new List<LevelInfo>();
        private DateTime lastFileCheck = DateTime.MinValue;
        private DateTime lastFileWrite = DateTime.MinValue;

        // Stats lookup: key = "LevelType|Regime|TouchBucket"
        private Dictionary<string, StatRow> stats = new Dictionary<string, StatRow>();
        private DateTime lastStatsCheck = DateTime.MinValue;
        private DateTime lastStatsWrite = DateTime.MinValue;

        // Touch counting (for choosing which stat bucket applies to an approach)
        private Dictionary<string, int> touchCountToday = new Dictionary<string, int>();
        private Dictionary<string, int> lastTouchBar    = new Dictionary<string, int>();
        private DateTime currentSessionDate = DateTime.MinValue;

        private const int TouchTol           = 3;
        private const int CooldownBars       = 12;
        private const int StatsMinSamples    = 30;   // matches compute_stats.py MIN_SAMPLES
        private const int StatsRefreshSeconds = 60;

        // v14 — internal confirmation indicators (fixed periods; tunable later)
        private const int    ConfEmaPeriod  = 21;
        private const int    ConfEmaSlopeLB = 3;
        private const int    ConfAdxPeriod  = 14;
        private const double ConfAdxStrong  = 20.0;
        private const int    ConfRsiPeriod  = 14;

        private double  rSpot;
        private string  rRegime = "UNKNOWN";
        private int     rRegimeSign;
        private double  rBias;
        private string  rBiasLabel = "NEUTRAL";
        private int     rBiasSign;
        private string  rBiasBreakdown = "";
        private LevelInfo rNearestUp, rNearestDn;
        private List<LevelInfo> rResList = new List<LevelInfo>();
        private List<LevelInfo> rSupList = new List<LevelInfo>();
        private List<Tuple<double, string>> rConfluences = new List<Tuple<double, string>>();
        private LevelInfo rFlipFull, rFlip0DTE, rCallWall, rCallWall0DTE, rPutWall, rPutWall0DTE;

        // Approach readouts (data thread → UI)
        private bool   rApproachActive;
        private string rApproachHeader = "";
        private string rApproachStat = "";
        private bool   rApproachTrust;

        // v12 — meta captured from CSV (written by ym_full_levels.py)
        private string   csvRegime = "UNK";        // POS / NEG / UNK
        private string   csvUpdatedRaw = "";
        private DateTime csvUpdatedDt = DateTime.MinValue;
        private bool     csvUpdatedValid;

        // v12 — outlook readouts (data thread → UI)
        private string rOutlookBehavior = "";
        private string rOutlookTarget = "";
        private int    rOutlookRegimeSign;

        // v12 — only show confluence clusters within this many pts of price
        private const int NearbyConfluencePts = 300;

        // v13 — day range + confluence odds
        private double   rDayHigh = double.NaN, rDayLow = double.NaN;
        private DateTime rangeSessionDate = DateTime.MinValue;
        private double   rRangePct = double.NaN;
        private List<ConfInfo> rConfOdds = new List<ConfInfo>();

        // v14 — confirmation (internal indicators + InstitutionalZones bridge)
        private int    rTrendDir, rMomDir, rLeanInternal, rLeanOverall, rConvMag;
        private double rAdxVal, rRsiVal;
        private bool   rAdxStrong;
        private string rReadsText = "";
        private int    rZoneBias;
        private bool   rInBull, rInBear, rZoneValid;
        private DateTime lastIzCheck = DateTime.MinValue;
        private string rApproachConfirm = "";
        private int    rApproachConfirmSign;

        // v15 — entry signals
        private bool   rApHaveOdds, rApTrustOdds;
        private double rApBreakPct, rApRejectPct;
        private int    rApNOdds;
        private bool   rSigActive;
        private int    rSigDir;                 // 1 long / -1 short
        private string rSigType = "", rSigLevelName = "", rSigText = "";
        private double rSigLevelPrice, rSigStop, rSigTarget, rSigOddsPct;
        private string lastSigKey = "";
        private int    lastSigBar = -1;
        private const double SignalStopBuffer = 12;   // YM pts beyond level for stop
        #endregion

        #region WPF card fields
        private Grid      chartGrid;
        private Border    card, regimeRow, approachRow;
        private Grid      cardShell;
        private Viewbox   contentBox;
        private TextBlock tbTitle, tbSub, tbInstr, tbUpdated;
        private TextBlock tbRegime, tbBiasNum, tbBiasLabel, tbBiasBreak;
        private TextBlock tbApproachHdr, tbApproachStat;
        private Border    outlookRow;
        private TextBlock tbOutlookBehavior, tbOutlookTarget;
        private Border    confirmRow;
        private TextBlock tbConfirmVerdict, tbConfirmReads, tbConfirmZone;
        private TextBlock tbApproachConfirm;
        private Border    signalRow;
        private TextBlock tbSignal;
        private TextBlock tbSpot, tbNearestUp, tbNearestDn;
        private TextBlock tbRangeInfo;
        private Grid      rangeBarGrid;
        private System.Windows.Shapes.Rectangle rangeMarker;
        private StackPanel resPanel, supPanel, confPanel, gammaPanel;
        private TextBlock tbResHdr, tbSupHdr, tbConfHdr, tbGammaHdr;
        private Grid      biasBarGrid;
        private System.Windows.Shapes.Rectangle biasFill;

        private Border    gripNW, gripNE, gripSW, gripSE;

        private bool     injected, absolutePlaced;
        private DateTime lastUiUpdate = DateTime.MinValue;
        private const int UiThrottleMs = 200;

        private bool      dragging;
        private Point     dragStart;
        private Thickness dragOrigMargin;

        private bool      resizing;
        private int       resizeCorner;
        private Point     resizeStart;
        private double    resizeOrigW, resizeOrigH;
        private Thickness resizeOrigMargin;

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        private static readonly Brush CardBg     = Frozen(Color.FromArgb(235, 20, 22, 28));
        private static readonly Brush TextDim    = Frozen(Color.FromRgb(150, 150, 156));
        private static readonly Brush RowBorder  = Frozen(Color.FromRgb(120, 120, 126));
        private static readonly Brush RowText    = Frozen(Color.FromRgb(214, 214, 218));
        private static readonly Brush RowFill    = Frozen(Color.FromArgb(28, 255, 255, 255));
        private static readonly Brush NeutralAcc = Frozen(Color.FromRgb(120, 120, 126));
        private static readonly Brush BarTrack   = Frozen(Color.FromArgb(60, 255, 255, 255));
        private static readonly Brush MidMarker  = Frozen(Color.FromArgb(160, 255, 255, 255));
        private static readonly Brush BullBrush  = Frozen(Color.FromRgb(0, 200, 100));
        private static readonly Brush BearBrush  = Frozen(Color.FromRgb(230, 70, 70));
        private static readonly Brush GripBrush  = Frozen(Color.FromRgb(78, 205, 196));
        private static readonly Brush AmberBrush = Frozen(Color.FromRgb(240, 180, 60));
        private static readonly Brush AmberFill  = Frozen(Color.FromArgb(40, 240, 180, 60));

        // DirectWrite label format
        private SharpDX.DirectWrite.TextFormat labelFormat;
        private int lastFontSize = -1;
        private bool lastFontBold = true;
        #endregion

        #region Lifecycle
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "YM levels from Python CSV: lines + dashboard with live approaching break/reject probabilities";
                Name = "YMLevels";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                ScaleJustification = ScaleJustification.Right;
                IsSuspendedWhileInactive = false;
                IsChartOnly = true;

                string ymDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ym_levels");
                CsvPath = System.IO.Path.Combine(ymDir, "ym_levels.csv");
                StatsPath = System.IO.Path.Combine(ymDir, "ym_stats.csv");
                RefreshSeconds = 30;

                DrawLines = true;
                LineWidth = 2;

                ShowLabels = true;
                LabelFontSize = 12;
                LabelFontBold = true;
                LabelBelowLine = true;
                LabelOffsetPoints = 8;
                LabelPixelMargin = 8;

                ShowDashboard = true;
                CardCorner = YMLevelsCorner.TopRight;
                CardMarginX = 12;
                CardMarginY = 12;
                CardWidth = 280;
                CardHeight = 480;
                ShowBiasBreakdown = true;
                ShowNearestSection = true;
                ShowResSupSection = false;   // v12 — off by default: full lists duplicate the chart lines
                ShowConfluenceSection = true;
                ShowGammaSection = false;    // v13 — raw gamma hidden (feed unreliable); toggle to show
                ConfluenceTolerance = 50;
                ApproachDistance = 25;

                EnableSignals = true;
                SignalSoundFile = "Alert2.wav";
                BreakSignalMinPct = 60;
                FadeSignalMinPct = 55;

                UseCsvColors = false;
                ResistanceColor = Brushes.Red;
                SupportColor    = Brushes.LimeGreen;
                PivotColor      = Brushes.LightGray;
                OvernightColor  = Brushes.Orange;
                PriorWeekColor  = Brushes.DeepSkyBlue;
                PriorMonthColor = Brushes.MediumPurple;
                GammaFlipColor  = Brushes.Gold;
                CallWallColor   = Brushes.Magenta;
                PutWallColor    = Brushes.Cyan;
            }
            else if (State == State.DataLoaded)
            {
                LoadLevels();
                LoadStats();
            }
            else if (State == State.Historical)
            {
                if (ShowDashboard && ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(() => TryInjectCard());
            }
            else if (State == State.Terminated)
            {
                DisposeLabelFormat();
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(() => TryRemoveCard());
            }
        }

        protected override void OnBarUpdate()
        {
            if ((DateTime.Now - lastFileCheck).TotalSeconds >= RefreshSeconds)
            {
                lastFileCheck = DateTime.Now;
                if (File.Exists(CsvPath))
                {
                    DateTime writeTime = File.GetLastWriteTime(CsvPath);
                    if (writeTime != lastFileWrite)
                        LoadLevels();
                }
            }

            if ((DateTime.Now - lastStatsCheck).TotalSeconds >= StatsRefreshSeconds)
            {
                lastStatsCheck = DateTime.Now;
                if (File.Exists(StatsPath))
                {
                    DateTime sw = File.GetLastWriteTime(StatsPath);
                    if (sw != lastStatsWrite)
                        LoadStats();
                }
            }

            if (DrawLines)
                DrawAllLines();

            rSpot = Close[0];

            if ((DateTime.Now - lastIzCheck).TotalSeconds >= 10)
            { lastIzCheck = DateTime.Now; LoadIzState(); }

            UpdateDayRange();
            UpdateTouchCounts();
            ComputeConfirmation();
            RecomputeSnapshot();
            HandleSignalOutput();

            if (ShowDashboard)
                UpdateCard();
        }
        #endregion

        #region CSV loading
        private void LoadLevels()
        {
            levels.Clear();

            if (!File.Exists(CsvPath))
            {
                Print("YMLevels: CSV not found at " + CsvPath);
                return;
            }

            try
            {
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

                    // v12 — Meta rows (Regime, DIA_Spot, YM_Price, Updated) are NOT
                    // tradeable levels. Capture what we need and skip them so they
                    // never draw lines, appear as "nearest", or pollute confluence.
                    if (ty == "Meta")
                    {
                        string val = parts[1].Trim();
                        if (nm == "Regime")
                        {
                            string r = val.ToUpperInvariant();
                            if (r.StartsWith("POS"))      csvRegime = "POS";
                            else if (r.StartsWith("NEG")) csvRegime = "NEG";
                            else                          csvRegime = "UNK";
                        }
                        else if (nm == "Updated")
                        {
                            csvUpdatedRaw = val;
                            DateTime dt;
                            if (DateTime.TryParse(val, CultureInfo.InvariantCulture,
                                                  DateTimeStyles.None, out dt))
                            { csvUpdatedDt = dt; csvUpdatedValid = true; }
                            else csvUpdatedValid = false;
                        }
                        continue;
                    }

                    double price;
                    if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out price)) continue;

                    LevelInfo lv = new LevelInfo();
                    lv.Name = nm;
                    lv.Price = price;
                    lv.Type = parts[2].Trim();
                    lv.ColorName = parts[3].Trim();
                    lv.Color = ResolveLevelColor(lv);
                    lv.Dash = GetDashStyle(lv.Type);
                    lv.Width = GetWidth(lv.Name);

                    levels.Add(lv);
                }

                Print("YMLevels: loaded " + levels.Count + " levels from CSV");
            }
            catch (Exception ex)
            {
                Print("YMLevels: error reading CSV — " + ex.Message);
            }
        }

        private void LoadStats()
        {
            var ns = new Dictionary<string, StatRow>();
            try
            {
                if (!File.Exists(StatsPath)) { stats = ns; return; }
                lastStatsWrite = File.GetLastWriteTime(StatsPath);

                string[] lines = File.ReadAllLines(StatsPath);
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] p = line.Split(',');
                    if (p.Length < 8) continue;

                    StatRow sr = new StatRow
                    {
                        LevelType   = p[0].Trim(),
                        Regime      = p[1].Trim(),
                        TouchBucket = p[2].Trim(),
                        N           = ParseInt(p[3]),
                        BreakPct    = ParseD(p[4]),
                        RejectPct   = ParseD(p[5]),
                        HoldPct     = ParseD(p[6])
                    };
                    ns[sr.LevelType + "|" + sr.Regime + "|" + sr.TouchBucket] = sr;
                }
                stats = ns;
                Print("YMLevels: loaded " + stats.Count + " stat buckets");
            }
            catch (Exception ex) { Print("YMLevels: stats load error " + ex.Message); }
        }

        private static int ParseInt(string s)
        { int v; return int.TryParse((s ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0; }
        private static double ParseD(string s)
        { double v; return double.TryParse((s ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0; }
        #endregion

        #region Touch counting
        // v13 — track the current session's running high/low for range position.
        private void UpdateDayRange()
        {
            if (CurrentBar < 0) return;
            DateTime d = Time[0].Date;
            if (d != rangeSessionDate)
            {
                rangeSessionDate = d;
                rDayHigh = High[0];
                rDayLow  = Low[0];
            }
            else
            {
                if (High[0] > rDayHigh) rDayHigh = High[0];
                if (Low[0]  < rDayLow)  rDayLow  = Low[0];
            }
        }

        // v13 — touch_number -> bucket string, matching compute_stats.py
        private static string TouchBucketStr(int nextTouch)
        {
            if (nextTouch <= 1) return "1";
            if (nextTouch == 2) return "2";
            return "3plus";
        }

        // v13 — regime-specific bucket, else ALL fallback; null if neither exists.
        private StatRow LookupOdds(string type, string tb, string regime)
        {
            StatRow sr;
            if (stats.TryGetValue(type + "|" + regime + "|" + tb, out sr) && sr.N >= StatsMinSamples) return sr;
            if (stats.TryGetValue(type + "|ALL|"   + tb, out sr) && sr.N >= StatsMinSamples) return sr;
            if (stats.TryGetValue(type + "|" + regime + "|" + tb, out sr)) return sr;
            if (stats.TryGetValue(type + "|ALL|"   + tb, out sr)) return sr;
            return null;
        }

        // v14 — read the InstitutionalZones_MTF bridge file (iz_state.csv), written
        // in the same folder as the levels CSV. Silent if the file isn't present.
        private void LoadIzState()
        {
            rZoneValid = false; rZoneBias = 0; rInBull = false; rInBear = false;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(CsvPath);
                if (string.IsNullOrEmpty(dir)) return;
                string izPath = System.IO.Path.Combine(dir, "iz_state.csv");
                if (!File.Exists(izPath)) return;

                foreach (string line in File.ReadAllLines(izPath))
                {
                    string[] p = line.Split(',');
                    if (p.Length < 2) continue;
                    string k = p[0].Trim().ToLowerInvariant();
                    string v = p[1].Trim();
                    if (k == "bias")         { int b; if (int.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out b)) rZoneBias = b; }
                    else if (k == "in_bull") rInBull = (v == "1");
                    else if (k == "in_bear") rInBear = (v == "1");
                }
                rZoneValid = true;
            }
            catch { rZoneValid = false; }
        }

        // v14 — internal trend/momentum read, combined with zone bias into one
        // overall lean (1 long / -1 short / 0 none). Confirmation only fires when
        // trend is strong (ADX) and trend+momentum agree; zone structure then
        // reinforces or offsets it.
        private void ComputeConfirmation()
        {
            if (CurrentBar < ConfEmaPeriod + ConfEmaSlopeLB + 2)
            {
                rLeanInternal = 0; rLeanOverall = 0; rConvMag = 0;
                rReadsText = "warming up…";
                return;
            }
            try
            {
                double ema     = EMA(ConfEmaPeriod)[0];
                double emaPrev = EMA(ConfEmaPeriod)[ConfEmaSlopeLB];
                rTrendDir = ema > emaPrev ? 1 : (ema < emaPrev ? -1 : 0);

                rAdxVal    = ADX(ConfAdxPeriod)[0];
                rAdxStrong = rAdxVal >= ConfAdxStrong;

                rRsiVal = RSI(ConfRsiPeriod, 3)[0];
                rMomDir = rRsiVal > 55 ? 1 : (rRsiVal < 45 ? -1 : 0);

                if (rAdxStrong && rTrendDir > 0 && rMomDir > 0)      rLeanInternal =  1;
                else if (rAdxStrong && rTrendDir < 0 && rMomDir < 0) rLeanInternal = -1;
                else                                                 rLeanInternal =  0;

                int score = rLeanInternal;
                if (rZoneValid) score += rZoneBias;
                if (rInBull) score += 1;
                if (rInBear) score -= 1;
                rLeanOverall = score > 0 ? 1 : (score < 0 ? -1 : 0);
                rConvMag = Math.Abs(score);

                string tA = rTrendDir > 0 ? "↑" : (rTrendDir < 0 ? "↓" : "→");
                string mA = rMomDir   > 0 ? "↑" : (rMomDir   < 0 ? "↓" : "→");
                rReadsText = "EMA " + tA + " · ADX " + rAdxVal.ToString("0")
                           + (rAdxStrong ? "" : " (weak)") + " · RSI " + rRsiVal.ToString("0") + " " + mA;
            }
            catch { rLeanInternal = 0; rLeanOverall = 0; rReadsText = "n/a"; }
        }

        private void UpdateTouchCounts()
        {
            if (levels.Count == 0 || CurrentBar < 1) return;

            DateTime sessDate = Time[0].Date;
            if (sessDate != currentSessionDate)
            {
                currentSessionDate = sessDate;
                touchCountToday.Clear();
                lastTouchBar.Clear();
            }

            double hi = High[0], lo = Low[0];
            foreach (LevelInfo lv in levels)
            {
                bool touched = (lo - TouchTol) <= lv.Price && (hi + TouchTol) >= lv.Price;
                if (!touched) continue;

                int lb;
                if (lastTouchBar.TryGetValue(lv.Name, out lb) && (CurrentBar - lb) < CooldownBars) continue;

                int tc; touchCountToday.TryGetValue(lv.Name, out tc);
                touchCountToday[lv.Name] = tc + 1;
                lastTouchBar[lv.Name] = CurrentBar;
            }
        }
        #endregion

        #region Color resolution
        private Brush ResolveLevelColor(LevelInfo lv)
        {
            if (UseCsvColors)
                return CsvNameToBrush(lv.ColorName);

            string n = lv.Name;

            if (lv.Type == "Session")
            {
                if (n.StartsWith("R")) return ResistanceColor;
                if (n.StartsWith("S")) return SupportColor;
                return PivotColor;
            }
            if (lv.Type == "Overnight")  return OvernightColor;
            if (lv.Type == "PriorWeek")  return PriorWeekColor;
            if (lv.Type == "PriorMonth") return PriorMonthColor;

            if (lv.Type == "Gamma")
            {
                if (n.Contains("Flip")) return GammaFlipColor;
                if (n.Contains("Call")) return CallWallColor;
                if (n.Contains("Put"))  return PutWallColor;
            }

            return Brushes.White;
        }

        private Brush CsvNameToBrush(string colorName)
        {
            switch ((colorName ?? "").ToLower())
            {
                case "red":     return Brushes.Red;
                case "green":   return Brushes.LimeGreen;
                case "blue":    return Brushes.DeepSkyBlue;
                case "purple":  return Brushes.MediumPurple;
                case "orange":  return Brushes.Orange;
                case "yellow":  return Brushes.Gold;
                case "magenta": return Brushes.Magenta;
                case "cyan":    return Brushes.Cyan;
                case "gray":    return Brushes.LightGray;
                default:        return Brushes.White;
            }
        }

        private DashStyleHelper GetDashStyle(string type)
        {
            switch (type)
            {
                case "Overnight":  return DashStyleHelper.Dash;
                case "PriorWeek":  return DashStyleHelper.DashDot;
                case "PriorMonth": return DashStyleHelper.Dot;
                case "Gamma":      return DashStyleHelper.Solid;
                default:           return DashStyleHelper.Solid;
            }
        }

        private int GetWidth(string name)
        {
            if (name.Contains("Wall") || name.Contains("Flip")) return LineWidth + 1;
            if (name == "Pivot" || name == "ONH" || name == "ONL") return LineWidth + 1;
            return LineWidth;
        }

        private string PrettyName(string name)
        {
            switch (name)
            {
                case "R1":             return "Resistance 1";
                case "R2":             return "Resistance 2";
                case "R3":             return "Resistance 3";
                case "S1":             return "Support 1";
                case "S2":             return "Support 2";
                case "S3":             return "Support 3";
                case "Pivot":          return "Daily Pivot";
                case "Y-Mid":          return "Yesterday Midpoint";
                case "ONH":            return "Overnight High";
                case "ONM":            return "Overnight Midpoint";
                case "ONL":            return "Overnight Low";
                case "PWH":            return "Prior Week High";
                case "PWM":            return "Prior Week Midpoint";
                case "PWC":            return "Prior Week Close";
                case "PWL":            return "Prior Week Low";
                case "PMH":            return "Prior Month High";
                case "PMM":            return "Prior Month Midpoint";
                case "PMC":            return "Prior Month Close";
                case "PML":            return "Prior Month Low";
                case "Flip 0DTE":      return "Gamma Flip 0DTE";
                default:               return name;
            }
        }
        #endregion

        #region Chart line drawing
        private void DrawAllLines()
        {
            foreach (LevelInfo lv in levels)
            {
                lv.Color = ResolveLevelColor(lv);
                string tag = "YMLev_" + lv.Name;
                NinjaTrader.NinjaScript.DrawingTools.Draw.HorizontalLine(
                    this, tag, lv.Price, lv.Color, lv.Dash, lv.Width);
            }
        }
        #endregion

        #region OnRender — labels pinned to right edge
        private void EnsureLabelFormat()
        {
            if (labelFormat != null && lastFontSize == LabelFontSize && lastFontBold == LabelFontBold)
                return;

            DisposeLabelFormat();

            try
            {
                labelFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory,
                    "Segoe UI",
                    LabelFontBold ? SharpDX.DirectWrite.FontWeight.Bold : SharpDX.DirectWrite.FontWeight.Normal,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    LabelFontSize);
                lastFontSize = LabelFontSize;
                lastFontBold = LabelFontBold;
            }
            catch { labelFormat = null; }
        }

        private void DisposeLabelFormat()
        {
            try { if (labelFormat != null) { labelFormat.Dispose(); labelFormat = null; } }
            catch { }
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (!ShowLabels || !DrawLines || levels == null || levels.Count == 0) return;
            if (RenderTarget == null) return;

            EnsureLabelFormat();
            if (labelFormat == null) return;

            float rightEdge = (float)ChartPanel.X + (float)ChartPanel.W;
            float pixelMargin = LabelPixelMargin;

            foreach (LevelInfo lv in levels)
            {
                try
                {
                    float yPrice = chartScale.GetYByValue(lv.Price);
                    float yLabel = LabelBelowLine
                        ? chartScale.GetYByValue(lv.Price - LabelOffsetPoints)
                        : yPrice;

                    string text = PrettyName(lv.Name) + " " + lv.Price.ToString("0");

                    using (var layout = new SharpDX.DirectWrite.TextLayout(
                        NinjaTrader.Core.Globals.DirectWriteFactory,
                        text, labelFormat, 400f, LabelFontSize * 1.6f))
                    {
                        float textW = layout.Metrics.Width;
                        float textH = layout.Metrics.Height;

                        float xLabel = rightEdge - pixelMargin - textW;
                        float yTop   = yLabel - textH * 0.5f;

                        SolidColorBrush scBrush = lv.Color as SolidColorBrush;
                        SharpDX.Color4 color = scBrush != null
                            ? new SharpDX.Color4(scBrush.Color.R / 255f, scBrush.Color.G / 255f, scBrush.Color.B / 255f, 1f)
                            : new SharpDX.Color4(1f, 1f, 1f, 1f);

                        using (var dxBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, color))
                        {
                            RenderTarget.DrawTextLayout(
                                new SharpDX.Vector2(xLabel, yTop),
                                layout,
                                dxBrush);
                        }
                    }
                }
                catch { }
            }
        }
        #endregion

        #region Snapshot
        private LevelInfo FindByName(string name)
        {
            foreach (LevelInfo lv in levels)
                if (lv.Name == name) return lv;
            return null;
        }

        private void RecomputeSnapshot()
        {
            if (levels == null || levels.Count == 0 || rSpot <= 0) return;

            rFlipFull     = FindByName("Gamma Flip");
            rFlip0DTE     = FindByName("Flip 0DTE");
            rCallWall     = FindByName("Call Wall");
            rCallWall0DTE = FindByName("Call Wall 0DTE");
            rPutWall      = FindByName("Put Wall");
            rPutWall0DTE  = FindByName("Put Wall 0DTE");

            // v12 — regime: prefer LIVE derivation from a real flip level (price can
            // cross the flip intraday, so this is more correct than a frozen value).
            // Use full flip, then 0DTE flip, then the CSV meta regime, then UNKNOWN.
            LevelInfo regimeFlip = rFlipFull != null ? rFlipFull : rFlip0DTE;
            if (regimeFlip != null)
            {
                if (rSpot > regimeFlip.Price) { rRegime = "POSITIVE GAMMA (stable)";  rRegimeSign =  1; }
                else                          { rRegime = "NEGATIVE GAMMA (volatile)"; rRegimeSign = -1; }
            }
            else if (csvRegime == "POS") { rRegime = "POSITIVE GAMMA (stable)";   rRegimeSign =  1; }
            else if (csvRegime == "NEG") { rRegime = "NEGATIVE GAMMA (volatile)"; rRegimeSign = -1; }
            else                         { rRegime = "UNKNOWN";                    rRegimeSign =  0; }

            LevelInfo up = null, dn = null;
            foreach (LevelInfo lv in levels)
            {
                if (lv.Price > rSpot)
                {
                    if (up == null || lv.Price < up.Price) up = lv;
                }
                else if (lv.Price < rSpot)
                {
                    if (dn == null || lv.Price > dn.Price) dn = lv;
                }
            }
            rNearestUp = up;
            rNearestDn = dn;

            rResList = levels.Where(l => l.Price > rSpot).OrderBy(l => l.Price - rSpot).Take(5).ToList();
            rSupList = levels.Where(l => l.Price < rSpot).OrderBy(l => rSpot - l.Price).Take(5).ToList();

            rConfluences.Clear();
            rConfOdds.Clear();
            string regimeStr = rRegimeSign > 0 ? "POS" : (rRegimeSign < 0 ? "NEG" : "UNK");
            List<LevelInfo> sorted = levels.OrderBy(l => l.Price).ToList();
            HashSet<int> used = new HashSet<int>();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (used.Contains(i)) continue;
                List<LevelInfo> cluster = new List<LevelInfo> { sorted[i] };
                used.Add(i);
                for (int j = i + 1; j < sorted.Count; j++)
                {
                    if (used.Contains(j)) continue;
                    if (Math.Abs(sorted[j].Price - sorted[i].Price) <= ConfluenceTolerance)
                    {
                        cluster.Add(sorted[j]);
                        used.Add(j);
                    }
                }
                if (cluster.Count >= 2)
                {
                    double avg = cluster.Average(c => c.Price);
                    // v12 — drop far-away clusters (the ±1000 pt noise); keep only nearby.
                    if (Math.Abs(avg - rSpot) <= NearbyConfluencePts)
                    {
                        string names = string.Join(" + ", cluster.Select(c => c.Name));
                        rConfluences.Add(Tuple.Create(avg, names));

                        // v13 — blended break/reject odds for this zone: N-weighted
                        // average across the constituent levels' stat buckets.
                        double sumBrk = 0, sumRej = 0; int sumN = 0, minN = int.MaxValue; int usedM = 0;
                        foreach (LevelInfo m in cluster)
                        {
                            int tcM; touchCountToday.TryGetValue(m.Name, out tcM);
                            string tbM = TouchBucketStr(tcM + 1);
                            StatRow sm = LookupOdds(m.Type, tbM, regimeStr);
                            if (sm != null && sm.N > 0)
                            {
                                sumBrk += sm.BreakPct * sm.N;
                                sumRej += sm.RejectPct * sm.N;
                                sumN   += sm.N;
                                if (sm.N < minN) minN = sm.N;
                                usedM++;
                            }
                        }
                        ConfInfo ci = new ConfInfo { Price = avg, Names = names };
                        if (usedM > 0 && sumN > 0)
                        {
                            ci.Brk = sumBrk / sumN;
                            ci.Rej = sumRej / sumN;
                            ci.MinN = (minN == int.MaxValue) ? 0 : minN;
                            ci.Trust = ci.MinN >= StatsMinSamples;
                            ci.HasOdds = true;
                        }
                        rConfOdds.Add(ci);
                    }
                }
            }

            // v13 — where price sits in today's range (0 = day low, 1 = day high)
            if (!double.IsNaN(rDayHigh) && !double.IsNaN(rDayLow) && rDayHigh > rDayLow)
                rRangePct = Math.Max(0.0, Math.Min(1.0, (rSpot - rDayLow) / (rDayHigh - rDayLow)));
            else
                rRangePct = double.NaN;

            int bias = 0;
            List<string> parts = new List<string>();

            if (rFlipFull != null)
            {
                double diff = rSpot - rFlipFull.Price;
                int c = Math.Abs(diff) < 20 ? 0 : (diff > 0 ? 25 : -25);
                bias += c;
                parts.Add("Flip:" + (c >= 0 ? "+" : "") + c);
            }
            if (rCallWall0DTE != null)
            {
                double diff = rSpot - rCallWall0DTE.Price;
                int c;
                if (diff > 0) c = 20;
                else if (diff > -50) c = -10;
                else c = 0;
                bias += c;
                parts.Add("Call:" + (c >= 0 ? "+" : "") + c);
            }
            if (rPutWall0DTE != null)
            {
                double diff = rSpot - rPutWall0DTE.Price;
                int c;
                if (diff < 0) c = -20;
                else if (diff < 50) c = 10;
                else c = 0;
                bias += c;
                parts.Add("Put:" + (c >= 0 ? "+" : "") + c);
            }
            LevelInfo pvt = FindByName("Pivot");
            if (pvt != null)
            {
                int c = rSpot > pvt.Price ? 10 : -10;
                bias += c;
                parts.Add("Pvt:" + (c >= 0 ? "+" : "") + c);
            }
            LevelInfo onh = FindByName("ONH");
            LevelInfo onl = FindByName("ONL");
            if (onh != null && onl != null)
            {
                int c;
                if (rSpot > onh.Price) c = 15;
                else if (rSpot < onl.Price) c = -15;
                else c = 0;
                bias += c;
                parts.Add("ON:" + (c >= 0 ? "+" : "") + c);
            }
            LevelInfo pwc = FindByName("PWC");
            if (pwc != null)
            {
                int c = rSpot > pwc.Price ? 5 : -5;
                bias += c;
                parts.Add("PWC:" + (c >= 0 ? "+" : "") + c);
            }

            rBias = Math.Max(-100, Math.Min(100, bias));

            if      (rBias >=  60) { rBiasLabel = "STRONGLY BULLISH"; rBiasSign =  2; }
            else if (rBias >=  25) { rBiasLabel = "BULLISH";          rBiasSign =  1; }
            else if (rBias >  -25) { rBiasLabel = "NEUTRAL";          rBiasSign =  0; }
            else if (rBias >  -60) { rBiasLabel = "BEARISH";          rBiasSign = -1; }
            else                   { rBiasLabel = "STRONGLY BEARISH"; rBiasSign = -2; }

            rBiasBreakdown = string.Join(" | ", parts) + " = " + (rBias >= 0 ? "+" : "") + rBias;

            ComputeApproach();
            ComputeOutlook();
        }

        // v12 — plain-language "where is price heading" read, from regime + magnets.
        private void ComputeOutlook()
        {
            if (rRegimeSign > 0)      rOutlookBehavior = "Positive gamma → mean-revert / pin";
            else if (rRegimeSign < 0) rOutlookBehavior = "Negative gamma → trend / momentum";
            else                      rOutlookBehavior = "Regime unknown → trade levels only";
            rOutlookRegimeSign = rRegimeSign;

            // Candidate magnets: deduped gamma walls + nearby confluence centers.
            List<Tuple<string, double>> magnets = new List<Tuple<string, double>>();
            if (rCallWall != null) magnets.Add(Tuple.Create("Call Wall", rCallWall.Price));
            if (rPutWall  != null) magnets.Add(Tuple.Create("Put Wall",  rPutWall.Price));
            if (rCallWall0DTE != null && (rCallWall == null || Math.Abs(rCallWall0DTE.Price - rCallWall.Price) > 1))
                magnets.Add(Tuple.Create("Call Wall 0DTE", rCallWall0DTE.Price));
            if (rPutWall0DTE != null && (rPutWall == null || Math.Abs(rPutWall0DTE.Price - rPutWall.Price) > 1))
                magnets.Add(Tuple.Create("Put Wall 0DTE", rPutWall0DTE.Price));
            foreach (Tuple<double, string> c in rConfluences)
                magnets.Add(Tuple.Create("Confluence " + ((int)c.Item1), c.Item1));

            string magName = null; double magPrice = 0, bestDist = double.MaxValue;
            foreach (Tuple<string, double> m in magnets)
            {
                double d = Math.Abs(m.Item2 - rSpot);
                if (d < bestDist) { bestDist = d; magName = m.Item1; magPrice = m.Item2; }
            }

            if (magName != null)
            {
                double diff = magPrice - rSpot;
                string arrow = diff >= 0 ? "▲" : "▼";
                string sign  = diff >= 0 ? "+" : "";
                rOutlookTarget = "Nearest magnet: " + magName + " " + magPrice.ToString("0")
                               + " (" + sign + ((int)diff) + ") " + arrow;
            }
            else rOutlookTarget = "No gamma magnets loaded";
        }

        // Finds the nearest level within ApproachDistance and looks up its historical stat.
        private void ComputeApproach()
        {
            rApHaveOdds = false; rApTrustOdds = false;
            rSigActive = false;

            LevelInfo near = null;
            double bestDist = double.MaxValue;
            foreach (LevelInfo lv in levels)
            {
                double d = Math.Abs(lv.Price - rSpot);
                if (d <= ApproachDistance && d < bestDist) { bestDist = d; near = lv; }
            }

            if (near == null)
            {
                rApproachActive = false;
                return;
            }

            string regime = rRegimeSign > 0 ? "POS" : (rRegimeSign < 0 ? "NEG" : "UNK");

            int tc; touchCountToday.TryGetValue(near.Name, out tc);
            int nextTouch = tc + 1;                       // "if price tests it now, it's the Nth touch"
            string tb = nextTouch <= 1 ? "1" : (nextTouch == 2 ? "2" : "3plus");
            string touchLabel = nextTouch == 1 ? "1st touch" : (nextTouch == 2 ? "2nd touch" : "3rd+ touch");

            double dir = near.Price - rSpot;
            string arrow = dir >= 0 ? "+" : "";
            rApproachHeader = "APPROACHING " + PrettyName(near.Name) + " " + near.Price.ToString("0")
                            + " (" + arrow + ((int)dir) + ")";

            // v12 — two-tier lookup: prefer the regime-specific bucket; if it's
            // missing or too thin, fall back to the regime-agnostic ALL bucket
            // (all regimes combined, written by compute_stats.py). This surfaces a
            // percentage backed by full history when the POS/NEG bucket isn't ready.
            StatRow srReg;  bool haveReg = stats.TryGetValue(near.Type + "|" + regime + "|" + tb, out srReg);
            StatRow srAll;  bool haveAll = stats.TryGetValue(near.Type + "|ALL|"   + tb, out srAll);

            if (haveReg && srReg.N >= StatsMinSamples)
            {
                // Trustworthy regime-specific stat.
                rApproachStat = regime + " · " + touchLabel + " → BREAK "
                              + srReg.BreakPct.ToString("0") + "% / REJECT "
                              + srReg.RejectPct.ToString("0") + "% (n=" + srReg.N + ")";
                rApproachTrust = true;
                rApBreakPct = srReg.BreakPct; rApRejectPct = srReg.RejectPct;
                rApNOdds = srReg.N; rApHaveOdds = true; rApTrustOdds = true;
            }
            else if (haveAll && srAll.N >= StatsMinSamples)
            {
                // Regime bucket thin → blended all-regime fallback.
                rApproachStat = "ALL · " + touchLabel + " → BREAK "
                              + srAll.BreakPct.ToString("0") + "% / REJECT "
                              + srAll.RejectPct.ToString("0") + "% (n=" + srAll.N + ", blended)";
                rApproachTrust = true;
                rApBreakPct = srAll.BreakPct; rApRejectPct = srAll.RejectPct;
                rApNOdds = srAll.N; rApHaveOdds = true; rApTrustOdds = true;
            }
            else if (haveReg || haveAll)
            {
                int shown = haveAll ? srAll.N : srReg.N;
                rApproachStat = regime + " · " + touchLabel + " → collecting data (n=" + shown + ")";
                rApproachTrust = false;
            }
            else
            {
                rApproachStat = regime + " · " + touchLabel + " → no data yet";
                rApproachTrust = false;
            }

            // v14 — does the combined lean agree with the bounce this level implies?
            // support below price → long bounce; resistance above → short bounce.
            int bounceDir = near.Price < rSpot ? 1 : -1;
            if (rLeanOverall == 0)              { rApproachConfirm = "~ mixed — no confirmation"; rApproachConfirmSign = 0; }
            else if (rLeanOverall == bounceDir) { rApproachConfirm = "✓ confirms bounce";         rApproachConfirmSign = 1; }
            else                                { rApproachConfirm = "✗ favors break";            rApproachConfirmSign = -1; }

            rApproachActive = true;

            // v15 — entry signal: odds pick fade vs break; require trustworthy odds
            // AND the combined lean pointing the same way as that trade.
            if (EnableSignals && rApHaveOdds && rApTrustOdds && rLeanOverall != 0)
            {
                int breakDir = -bounceDir;   // break of support = short; break of resistance = long
                if (rApBreakPct >= BreakSignalMinPct && rLeanOverall == breakDir)
                {
                    rSigActive = true; rSigDir = breakDir; rSigType = "BREAK"; rSigOddsPct = rApBreakPct;
                }
                else if (rApRejectPct >= FadeSignalMinPct && rLeanOverall == bounceDir)
                {
                    rSigActive = true; rSigDir = bounceDir; rSigType = "FADE"; rSigOddsPct = rApRejectPct;
                }

                if (rSigActive)
                {
                    rSigLevelName  = PrettyName(near.Name);
                    rSigLevelPrice = near.Price;
                    rSigStop   = rSigDir > 0 ? near.Price - SignalStopBuffer : near.Price + SignalStopBuffer;
                    rSigTarget = FindTarget(rSigDir, near.Price);

                    string dirTxt = rSigDir > 0 ? "▲ LONG " : "▼ SHORT ";
                    string tgtTxt = double.IsNaN(rSigTarget) ? "" : " · tgt " + rSigTarget.ToString("0");
                    rSigText = dirTxt + rSigType + " @ " + rSigLevelName + " " + rSigLevelPrice.ToString("0")
                             + " · stop " + rSigStop.ToString("0") + tgtTxt
                             + " · edge " + rSigOddsPct.ToString("0") + "%";
                }
            }
        }

        // v15 — nearest level in the trade direction to use as a target (NaN if none)
        private double FindTarget(int dir, double excludePrice)
        {
            double best = double.NaN;
            foreach (LevelInfo lv in levels)
            {
                if (Math.Abs(lv.Price - excludePrice) < 1) continue;
                if (dir > 0 && lv.Price > rSpot)
                { if (double.IsNaN(best) || lv.Price < best) best = lv.Price; }
                else if (dir < 0 && lv.Price < rSpot)
                { if (double.IsNaN(best) || lv.Price > best) best = lv.Price; }
            }
            return best;
        }

        // v15 — draw the chart arrow and fire the sound alert once per new signal.
        // Fires once when a signal appears; re-arms after the signal clears.
        private void HandleSignalOutput()
        {
            if (!EnableSignals || !rSigActive)
            {
                lastSigKey = "";
                return;
            }

            string key = rSigType + "|" + rSigDir + "|" + rSigLevelName;
            if (key == lastSigKey) return;   // already fired for this signal
            lastSigKey = key;
            lastSigBar = CurrentBar;

            try
            {
                string tag = "YMSig_" + CurrentBar + "_" + (rSigDir > 0 ? "U" : "D");
                if (rSigDir > 0)
                    NinjaTrader.NinjaScript.DrawingTools.Draw.ArrowUp(
                        this, tag, false, 0, Low[0] - 2 * TickSize, Brushes.Lime);
                else
                    NinjaTrader.NinjaScript.DrawingTools.Draw.ArrowDown(
                        this, tag, false, 0, High[0] + 2 * TickSize, Brushes.Red);
            }
            catch { }

            if (State == State.Realtime)
            {
                try
                {
                    string snd = string.IsNullOrEmpty(SignalSoundFile)
                        ? "" : NinjaTrader.Core.Globals.InstallDir + @"\sounds\" + SignalSoundFile;
                    Alert("YMSig", NinjaTrader.NinjaScript.Priority.High, rSigText, snd, 0,
                          Brushes.Black, rSigDir > 0 ? Brushes.Lime : Brushes.Red);
                }
                catch { }
            }
        }
        #endregion

        #region Card build / inject / remove
        private void TryInjectCard()
        {
            try
            {
                if (injected || ChartControl == null) return;
                chartGrid = ChartControl.Parent as Grid;
                if (chartGrid == null)
                {
                    DependencyObject p = ChartControl.Parent;
                    while (p != null && !(p is Grid))
                        p = LogicalTreeHelper.GetParent(p) ?? VisualTreeHelper.GetParent(p);
                    chartGrid = p as Grid;
                }
                if (chartGrid == null) return;

                card = BuildCard();
                ApplyCornerPlacement();
                Grid.SetRowSpan(card, Math.Max(1, chartGrid.RowDefinitions.Count));
                Grid.SetColumnSpan(card, Math.Max(1, chartGrid.ColumnDefinitions.Count));
                System.Windows.Controls.Panel.SetZIndex(card, 1000);
                chartGrid.Children.Add(card);
                injected = true;
                UpdateCard();
            }
            catch (Exception ex) { Print(Name + ": inject error " + ex.Message); }
        }

        private void TryRemoveCard()
        {
            try
            {
                if (card != null && chartGrid != null && chartGrid.Children.Contains(card))
                    chartGrid.Children.Remove(card);
                card = null; chartGrid = null; injected = false; absolutePlaced = false;
            }
            catch (Exception ex) { Print(Name + ": remove error " + ex.Message); }
        }

        private Border BuildCard()
        {
            FontFamily fam = new FontFamily("Segoe UI");
            StackPanel stack = new StackPanel();

            tbTitle = new TextBlock
            {
                Text = "YM LEVELS DASHBOARD", Foreground = Brushes.White,
                FontFamily = fam, FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            stack.Children.Add(tbTitle);

            tbSub = new TextBlock
            {
                Text = "session · overnight · gamma", Foreground = TextDim,
                FontFamily = fam, FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            stack.Children.Add(tbSub);

            tbInstr = new TextBlock
            {
                Text = "Instrument: " + (Instrument != null ? Instrument.MasterInstrument.Name : "—"),
                Foreground = TextDim, FontFamily = fam, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };
            stack.Children.Add(tbInstr);

            // v12 — daily-levels freshness confirmation
            tbUpdated = new TextBlock
            {
                Text = "", Foreground = TextDim, FontFamily = fam, FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            stack.Children.Add(tbUpdated);

            tbRegime = new TextBlock
            {
                Text = "UNKNOWN", Foreground = RowText, FontFamily = fam,
                FontSize = 13, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center
            };
            regimeRow = MakeRow(tbRegime, RowFill, RowBorder);
            stack.Children.Add(regimeRow);

            // APPROACHING alert box (hidden until price nears a level)
            tbApproachHdr = new TextBlock
            {
                Text = "", Foreground = AmberBrush, FontFamily = fam,
                FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            tbApproachStat = new TextBlock
            {
                Text = "", Foreground = Brushes.White, FontFamily = fam,
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            StackPanel apStack = new StackPanel();
            apStack.Children.Add(tbApproachHdr);
            apStack.Children.Add(tbApproachStat);
            tbApproachConfirm = new TextBlock
            {
                Text = "", Foreground = Brushes.White, FontFamily = fam,
                FontSize = 11, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            apStack.Children.Add(tbApproachConfirm);
            approachRow = new Border
            {
                Background = AmberFill, BorderBrush = AmberBrush,
                BorderThickness = new Thickness(1.6),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 5, 6, 5),
                Margin = new Thickness(0, 6, 0, 2),
                Child = apStack,
                Visibility = Visibility.Collapsed
            };
            stack.Children.Add(approachRow);

            // v15 — ENTRY SIGNAL box (hidden until a signal fires)
            tbSignal = new TextBlock
            {
                Text = "", Foreground = Brushes.White, FontFamily = fam,
                FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            signalRow = new Border
            {
                Background = RowFill, BorderBrush = RowBorder,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 5, 6, 5),
                Margin = new Thickness(0, 6, 0, 2),
                Child = tbSignal,
                Visibility = Visibility.Collapsed
            };
            stack.Children.Add(signalRow);

            // v12 — OUTLOOK box: regime behavior + nearest magnet direction
            tbOutlookBehavior = new TextBlock
            {
                Text = "", Foreground = RowText, FontFamily = fam,
                FontSize = 11, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            tbOutlookTarget = new TextBlock
            {
                Text = "", Foreground = TextDim, FontFamily = fam,
                FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            StackPanel olStack = new StackPanel();
            olStack.Children.Add(tbOutlookBehavior);
            olStack.Children.Add(tbOutlookTarget);
            outlookRow = new Border
            {
                Background = RowFill, BorderBrush = RowBorder,
                BorderThickness = new Thickness(1.4),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 6, 0, 2),
                Child = olStack
            };
            stack.Children.Add(outlookRow);

            // v14 — CONFIRMATION box: overall lean + reads + zone note
            tbConfirmVerdict = new TextBlock
            {
                Text = "", Foreground = RowText, FontFamily = fam,
                FontSize = 11, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            tbConfirmReads = new TextBlock
            {
                Text = "", Foreground = TextDim, FontFamily = fam,
                FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            tbConfirmZone = new TextBlock
            {
                Text = "", Foreground = TextDim, FontFamily = fam,
                FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = System.Windows.TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            StackPanel cfStack = new StackPanel();
            cfStack.Children.Add(tbConfirmVerdict);
            cfStack.Children.Add(tbConfirmReads);
            cfStack.Children.Add(tbConfirmZone);
            confirmRow = new Border
            {
                Background = RowFill, BorderBrush = RowBorder,
                BorderThickness = new Thickness(1.4),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 6, 0, 2),
                Child = cfStack
            };
            stack.Children.Add(confirmRow);

            Grid biasHdr = new Grid { Margin = new Thickness(0, 8, 0, 2) };
            biasHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            biasHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock biasHeaderLbl = new TextBlock
            {
                Text = "Bull/Bear Bias", Foreground = RowText, FontFamily = fam,
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            tbBiasNum = new TextBlock
            {
                Text = "0", Foreground = RowText, FontFamily = fam,
                FontSize = 14, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(biasHeaderLbl, 0);
            Grid.SetColumn(tbBiasNum, 1);
            biasHdr.Children.Add(biasHeaderLbl);
            biasHdr.Children.Add(tbBiasNum);
            stack.Children.Add(biasHdr);

            biasBarGrid = new Grid { Height = 10, Margin = new Thickness(0, 2, 0, 2) };
            biasBarGrid.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = BarTrack, RadiusX = 3, RadiusY = 3
            });
            biasFill = new System.Windows.Shapes.Rectangle
            {
                Fill = NeutralAcc, RadiusX = 2, RadiusY = 2,
                HorizontalAlignment = HorizontalAlignment.Left, Width = 0
            };
            biasBarGrid.Children.Add(biasFill);
            biasBarGrid.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = MidMarker, Width = 1,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            stack.Children.Add(biasBarGrid);

            tbBiasLabel = new TextBlock
            {
                Text = "NEUTRAL", Foreground = RowText, FontFamily = fam,
                FontSize = 11, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 2)
            };
            stack.Children.Add(tbBiasLabel);

            tbBiasBreak = new TextBlock
            {
                Text = "", Foreground = TextDim, FontFamily = fam,
                FontSize = 9.5, HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            stack.Children.Add(tbBiasBreak);

            if (ShowNearestSection)
            {
                stack.Children.Add(SectionHeader(fam, "Position"));
                tbSpot = MakeRowText(fam, "YM —");
                tbSpot.FontWeight = FontWeights.Bold;
                stack.Children.Add(tbSpot);

                tbRangeInfo = MakeRowText(fam, "");
                stack.Children.Add(tbRangeInfo);

                // mini day-range bar with a position marker
                rangeBarGrid = new Grid { Height = 8, Margin = new Thickness(0, 3, 0, 2) };
                rangeBarGrid.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Fill = BarTrack, RadiusX = 3, RadiusY = 3
                });
                rangeMarker = new System.Windows.Shapes.Rectangle
                {
                    Fill = Brushes.White, Width = 3, RadiusX = 1, RadiusY = 1,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                rangeBarGrid.Children.Add(rangeMarker);
                stack.Children.Add(rangeBarGrid);
            }
            if (ShowResSupSection)
            {
                tbResHdr = SectionHeader(fam, "Resistance Above");
                stack.Children.Add(tbResHdr);
                resPanel = new StackPanel();
                stack.Children.Add(resPanel);

                tbSupHdr = SectionHeader(fam, "Support Below");
                stack.Children.Add(tbSupHdr);
                supPanel = new StackPanel();
                stack.Children.Add(supPanel);
            }
            if (ShowConfluenceSection)
            {
                tbConfHdr = SectionHeader(fam, "Confluence + Odds");
                stack.Children.Add(tbConfHdr);
                confPanel = new StackPanel();
                stack.Children.Add(confPanel);
            }
            if (ShowGammaSection)
            {
                tbGammaHdr = SectionHeader(fam, "Gamma");
                stack.Children.Add(tbGammaHdr);
                gammaPanel = new StackPanel();
                stack.Children.Add(gammaPanel);
            }

            contentBox = new Viewbox
            {
                Child = stack,
                Stretch = System.Windows.Media.Stretch.Uniform,
                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Stretch
            };

            cardShell = new Grid();
            cardShell.Children.Add(contentBox);

            gripNW = MakeGrip(Cursors.SizeNWSE);
            gripNW.HorizontalAlignment = HorizontalAlignment.Left;
            gripNW.VerticalAlignment   = VerticalAlignment.Top;
            gripNE = MakeGrip(Cursors.SizeNESW);
            gripNE.HorizontalAlignment = HorizontalAlignment.Right;
            gripNE.VerticalAlignment   = VerticalAlignment.Top;
            gripSW = MakeGrip(Cursors.SizeNESW);
            gripSW.HorizontalAlignment = HorizontalAlignment.Left;
            gripSW.VerticalAlignment   = VerticalAlignment.Bottom;
            gripSE = MakeGrip(Cursors.SizeNWSE);
            gripSE.HorizontalAlignment = HorizontalAlignment.Right;
            gripSE.VerticalAlignment   = VerticalAlignment.Bottom;
            HookGrip(gripNW, 0);
            HookGrip(gripNE, 1);
            HookGrip(gripSW, 2);
            HookGrip(gripSE, 3);
            cardShell.Children.Add(gripNW);
            cardShell.Children.Add(gripNE);
            cardShell.Children.Add(gripSW);
            cardShell.Children.Add(gripSE);

            Border border = new Border
            {
                Width  = Math.Max(220, CardWidth),
                Height = Math.Max(300, CardHeight),
                Background = CardBg,
                BorderBrush = RowBorder,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 5, 6, 5),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Cursor = Cursors.SizeAll,
                ToolTip = "Drag body to move · Drag corner to resize",
                Child = cardShell
            };
            border.MouseLeftButtonDown += OnCardDown;
            border.MouseMove           += OnCardMove;
            border.MouseLeftButtonUp   += OnCardUp;
            return border;
        }

        private Border MakeGrip(Cursor c)
        {
            return new Border
            {
                Width  = 14, Height = 14,
                Background = GripBrush,
                CornerRadius = new CornerRadius(3),
                Opacity = 0.55,
                Cursor = c,
                Margin = new Thickness(-3),
                BorderBrush = RowText,
                BorderThickness = new Thickness(1)
            };
        }

        private void HookGrip(Border g, int corner)
        {
            g.MouseLeftButtonDown += (s, e) => StartResize(corner, e);
            g.MouseMove           += OnResizeMove;
            g.MouseLeftButtonUp   += OnResizeUp;
            g.MouseEnter          += (s, e) => g.Opacity = 0.9;
            g.MouseLeave          += (s, e) => { if (!resizing) g.Opacity = 0.55; };
        }

        private static TextBlock SectionHeader(FontFamily fam, string text)
        {
            return new TextBlock
            {
                Text = text.ToUpper(), Foreground = TextDim, FontFamily = fam,
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 3)
            };
        }

        private static TextBlock MakeRowText(FontFamily fam, string txt)
        {
            return new TextBlock
            {
                Text = txt, Foreground = RowText, FontFamily = fam,
                FontSize = 11, Margin = new Thickness(0, 1, 0, 1)
            };
        }

        private static Border MakeRow(TextBlock content, Brush fill, Brush border)
        {
            return new Border
            {
                Background = fill, BorderBrush = border,
                BorderThickness = new Thickness(1.4),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 4, 0, 2),
                Child = content
            };
        }

        private static Brush Tint(Brush src, byte alpha)
        {
            SolidColorBrush s = src as SolidColorBrush;
            if (s == null) return RowFill;
            SolidColorBrush b = new SolidColorBrush(Color.FromArgb(alpha, s.Color.R, s.Color.G, s.Color.B));
            b.Freeze(); return b;
        }
        #endregion

        #region Corner + drag + resize
        private void ApplyCornerPlacement()
        {
            if (card == null) return;
            bool left = CardCorner == YMLevelsCorner.TopLeft || CardCorner == YMLevelsCorner.BottomLeft;
            bool top  = CardCorner == YMLevelsCorner.TopLeft || CardCorner == YMLevelsCorner.TopRight;
            card.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            card.VerticalAlignment   = top  ? VerticalAlignment.Top    : VerticalAlignment.Bottom;
            card.Margin = new Thickness(
                left ? CardMarginX : 0,
                top  ? CardMarginY : 0,
                left ? 0 : CardMarginX,
                top  ? 0 : CardMarginY);
            absolutePlaced = false;
        }

        private void EnsureAbsolutePlacement()
        {
            if (absolutePlaced || card == null || chartGrid == null) return;
            double leftPx = card.HorizontalAlignment == HorizontalAlignment.Left
                          ? card.Margin.Left
                          : chartGrid.ActualWidth  - card.ActualWidth  - card.Margin.Right;
            double topPx  = card.VerticalAlignment == VerticalAlignment.Top
                          ? card.Margin.Top
                          : chartGrid.ActualHeight - card.ActualHeight - card.Margin.Bottom;
            card.HorizontalAlignment = HorizontalAlignment.Left;
            card.VerticalAlignment   = VerticalAlignment.Top;
            card.Margin = new Thickness(Math.Max(0, leftPx), Math.Max(0, topPx), 0, 0);
            absolutePlaced = true;
        }

        private void OnCardDown(object s, MouseButtonEventArgs e)
        {
            if (resizing) return;
            if (chartGrid == null) return;
            EnsureAbsolutePlacement();
            dragging = true;
            dragStart = e.GetPosition(chartGrid);
            dragOrigMargin = card.Margin;
            card.CaptureMouse();
            e.Handled = true;
        }

        private void OnCardMove(object s, MouseEventArgs e)
        {
            if (!dragging) return;
            Point p = e.GetPosition(chartGrid);
            double nl = dragOrigMargin.Left + (p.X - dragStart.X);
            double nt = dragOrigMargin.Top  + (p.Y - dragStart.Y);
            double maxL = Math.Max(0, chartGrid.ActualWidth  - card.ActualWidth);
            double maxT = Math.Max(0, chartGrid.ActualHeight - card.ActualHeight);
            card.Margin = new Thickness(
                Math.Min(Math.Max(0, nl), maxL),
                Math.Min(Math.Max(0, nt), maxT), 0, 0);
            e.Handled = true;
        }

        private void OnCardUp(object s, MouseButtonEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            card.ReleaseMouseCapture();
            PersistLayoutToProperties();
            e.Handled = true;
        }

        private void StartResize(int corner, MouseButtonEventArgs e)
        {
            if (chartGrid == null || card == null) return;
            EnsureAbsolutePlacement();
            resizing = true;
            resizeCorner = corner;
            resizeStart = e.GetPosition(chartGrid);
            resizeOrigW = card.ActualWidth;
            resizeOrigH = card.ActualHeight;
            resizeOrigMargin = card.Margin;
            Border b = e.Source as Border;
            if (b != null) { b.CaptureMouse(); b.Opacity = 0.95; }
            e.Handled = true;
        }

        private void OnResizeMove(object s, MouseEventArgs e)
        {
            if (!resizing || card == null || chartGrid == null) return;
            Point p = e.GetPosition(chartGrid);
            double dx = p.X - resizeStart.X, dy = p.Y - resizeStart.Y;
            double newW = resizeOrigW, newH = resizeOrigH;
            double newL = resizeOrigMargin.Left, newT = resizeOrigMargin.Top;

            switch (resizeCorner)
            {
                case 0: newW = resizeOrigW - dx; newH = resizeOrigH - dy; newL += dx; newT += dy; break;
                case 1: newW = resizeOrigW + dx; newH = resizeOrigH - dy; newT += dy;             break;
                case 2: newW = resizeOrigW - dx; newH = resizeOrigH + dy; newL += dx;             break;
                case 3: newW = resizeOrigW + dx; newH = resizeOrigH + dy;                          break;
            }

            const double MIN_W = 200, MIN_H = 260;
            if (newW < MIN_W) { if (resizeCorner == 0 || resizeCorner == 2) newL -= (MIN_W - newW); newW = MIN_W; }
            if (newH < MIN_H) { if (resizeCorner == 0 || resizeCorner == 1) newT -= (MIN_H - newH); newH = MIN_H; }
            if (newL < 0) newL = 0;
            if (newT < 0) newT = 0;
            if (newL + newW > chartGrid.ActualWidth)  newW = chartGrid.ActualWidth  - newL;
            if (newT + newH > chartGrid.ActualHeight) newH = chartGrid.ActualHeight - newT;

            card.Width  = newW;
            card.Height = newH;
            card.Margin = new Thickness(newL, newT, 0, 0);
            e.Handled = true;
        }

        private void OnResizeUp(object s, MouseButtonEventArgs e)
        {
            if (!resizing) return;
            resizing = false;
            Border b = s as Border;
            if (b != null) { b.ReleaseMouseCapture(); b.Opacity = 0.55; }
            PersistLayoutToProperties();
            e.Handled = true;
        }

        private void PersistLayoutToProperties()
        {
            if (card == null || chartGrid == null) return;
            try
            {
                int w = (int)Math.Round(card.Width  > 0 ? card.Width  : card.ActualWidth);
                int h = (int)Math.Round(card.Height > 0 ? card.Height : card.ActualHeight);
                CardWidth  = Math.Max(220, Math.Min(1200, w));
                CardHeight = Math.Max(300, Math.Min(1600, h));

                CardCorner  = YMLevelsCorner.TopLeft;
                CardMarginX = (int)Math.Max(0, Math.Min(10000, Math.Round(card.Margin.Left)));
                CardMarginY = (int)Math.Max(0, Math.Min(10000, Math.Round(card.Margin.Top)));
            }
            catch { }
        }
        #endregion

        #region UpdateCard
        private void UpdateCard()
        {
            if (!injected || card == null || ChartControl == null) return;
            if ((DateTime.UtcNow - lastUiUpdate).TotalMilliseconds < UiThrottleMs) return;
            lastUiUpdate = DateTime.UtcNow;

            double spot = rSpot;
            string regime = rRegime;
            int regSign = rRegimeSign;
            double bias = rBias;
            string biasLabel = rBiasLabel;
            int biasSign = rBiasSign;
            string biasBreak = rBiasBreakdown;
            List<LevelInfo> resList = new List<LevelInfo>(rResList);
            List<LevelInfo> supList = new List<LevelInfo>(rSupList);

            bool apActive = rApproachActive;
            string apHdr = rApproachHeader;
            string apStat = rApproachStat;
            bool apTrust = rApproachTrust;

            LevelInfo flipF = rFlipFull;
            LevelInfo flip0 = rFlip0DTE;
            LevelInfo cwF = rCallWall;
            LevelInfo cw0 = rCallWall0DTE;
            LevelInfo pwF = rPutWall;
            LevelInfo pw0 = rPutWall0DTE;

            // v12 locals
            string olBehavior = rOutlookBehavior;
            string olTarget = rOutlookTarget;
            int    olRegSign = rOutlookRegimeSign;
            bool     upValid = csvUpdatedValid;
            DateTime upDt = csvUpdatedDt;

            // v13 locals
            double dHigh = rDayHigh, dLow = rDayLow, rangePct = rRangePct;
            List<ConfInfo> confOdds = new List<ConfInfo>(rConfOdds);

            // v14 locals
            int    leanOverall = rLeanOverall;
            string readsText = rReadsText;
            bool   zoneValid = rZoneValid;
            int    zoneBias = rZoneBias;
            bool   inBull = rInBull, inBear = rInBear;
            string apConfirm = rApproachConfirm;
            int    apConfirmSign = rApproachConfirmSign;

            // v15 signal locals
            bool   sigActive = rSigActive;
            string sigText = rSigText;
            int    sigDir = rSigDir;

            try
            {
                ChartControl.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (card == null) return;

                        // v12 — freshness stamp
                        if (tbUpdated != null)
                        {
                            if (upValid)
                            {
                                bool fresh = upDt.Date == DateTime.Now.Date;
                                tbUpdated.Text = (fresh ? "Levels updated " : "STALE — ")
                                               + upDt.ToString("MMM d  HH:mm")
                                               + (fresh ? "  ✓" : "  ⚠");
                                tbUpdated.Foreground = fresh ? BullBrush : BearBrush;
                            }
                            else { tbUpdated.Text = "Levels: no timestamp"; tbUpdated.Foreground = TextDim; }
                        }

                        Brush regAcc = regSign > 0 ? BullBrush : (regSign < 0 ? BearBrush : NeutralAcc);
                        tbRegime.Text = regime;
                        tbRegime.Foreground = regAcc;
                        regimeRow.BorderBrush = regAcc;
                        regimeRow.Background = Tint(regAcc, 34);
                        card.BorderBrush = regAcc;

                        // v12 — outlook box
                        if (outlookRow != null)
                        {
                            tbOutlookBehavior.Text = olBehavior;
                            tbOutlookBehavior.Foreground = olRegSign > 0 ? BullBrush
                                                        : (olRegSign < 0 ? BearBrush : RowText);
                            tbOutlookTarget.Text = olTarget;
                        }

                        // APPROACHING box
                        if (apActive)
                        {
                            approachRow.Visibility = Visibility.Visible;
                            tbApproachHdr.Text = apHdr;
                            tbApproachStat.Text = apStat;
                            tbApproachStat.Foreground = apTrust ? Brushes.White : TextDim;
                            tbApproachConfirm.Text = apConfirm;
                            tbApproachConfirm.Foreground = apConfirmSign > 0 ? BullBrush
                                                         : (apConfirmSign < 0 ? BearBrush : TextDim);
                        }
                        else
                        {
                            approachRow.Visibility = Visibility.Collapsed;
                        }

                        // v15 — ENTRY SIGNAL box
                        if (signalRow != null)
                        {
                            if (sigActive)
                            {
                                Brush acc = sigDir > 0 ? BullBrush : BearBrush;
                                signalRow.Visibility = Visibility.Visible;
                                tbSignal.Text = sigText;
                                tbSignal.Foreground = acc;
                                signalRow.BorderBrush = acc;
                                signalRow.Background = Tint(acc, 40);
                            }
                            else
                            {
                                signalRow.Visibility = Visibility.Collapsed;
                            }
                        }

                        // v14 — CONFIRMATION box (always visible)
                        if (confirmRow != null)
                        {
                            string verdict = leanOverall > 0 ? "Lean: LONG"
                                           : (leanOverall < 0 ? "Lean: SHORT" : "Lean: none / mixed");
                            tbConfirmVerdict.Text = verdict;
                            tbConfirmVerdict.Foreground = leanOverall > 0 ? BullBrush
                                                        : (leanOverall < 0 ? BearBrush : RowText);
                            tbConfirmReads.Text = readsText;
                            if (zoneValid)
                            {
                                string zb = zoneBias > 0 ? "bullish" : (zoneBias < 0 ? "bearish" : "mixed");
                                string zin = inBull ? " · in bull zone" : (inBear ? " · in bear zone" : "");
                                tbConfirmZone.Text = "Zones: " + zb + zin;
                            }
                            else tbConfirmZone.Text = "Zones: n/a";
                        }

                        Brush biasAcc = biasSign > 0 ? BullBrush : (biasSign < 0 ? BearBrush : RowText);
                        tbBiasNum.Text = (bias >= 0 ? "+" : "") + ((int)bias).ToString();
                        tbBiasNum.Foreground = biasAcc;
                        tbBiasLabel.Text = biasLabel;
                        tbBiasLabel.Foreground = biasAcc;
                        UpdateBiasFill(bias);
                        tbBiasBreak.Text = biasBreak;
                        tbBiasBreak.Visibility = ShowBiasBreakdown ? Visibility.Visible : Visibility.Collapsed;

                        if (ShowNearestSection && tbSpot != null)
                        {
                            tbSpot.Text = "YM " + spot.ToString("0");
                            tbSpot.Foreground = Brushes.White;
                            tbSpot.FontWeight = FontWeights.Bold;

                            if (!double.IsNaN(rangePct) && !double.IsNaN(dHigh) && !double.IsNaN(dLow))
                            {
                                int pct = (int)Math.Round(rangePct * 100);
                                string zone = rangePct >= 0.66 ? "upper" : (rangePct <= 0.34 ? "lower" : "middle");
                                tbRangeInfo.Text = "Day " + dLow.ToString("0") + "–" + dHigh.ToString("0")
                                                 + " · " + pct + "% (" + zone + ")";
                                tbRangeInfo.Foreground = rangePct >= 0.66 ? BullBrush
                                                       : (rangePct <= 0.34 ? BearBrush : RowText);

                                double bw = rangeBarGrid.ActualWidth;
                                if (bw < 1) bw = Math.Max(180, CardWidth - 28);
                                double x = (bw - 3) * rangePct;
                                rangeMarker.Margin = new Thickness(Math.Max(0, x), 0, 0, 0);
                            }
                            else
                            {
                                tbRangeInfo.Text = "Day range: building…";
                                tbRangeInfo.Foreground = TextDim;
                            }
                        }

                        if (ShowResSupSection)
                        {
                            resPanel.Children.Clear();
                            foreach (LevelInfo lv in resList)
                            {
                                double d = lv.Price - spot;
                                resPanel.Children.Add(LevelLine(lv, "+" + ((int)d)));
                            }
                            supPanel.Children.Clear();
                            foreach (LevelInfo lv in supList)
                            {
                                double d = spot - lv.Price;
                                supPanel.Children.Add(LevelLine(lv, "-" + ((int)d)));
                            }
                        }

                        if (ShowConfluenceSection)
                        {
                            confPanel.Children.Clear();
                            if (confOdds.Count == 0)
                            {
                                confPanel.Children.Add(new TextBlock
                                {
                                    Text = "no nearby clusters", Foreground = TextDim,
                                    FontSize = 10, FontStyle = FontStyles.Italic
                                });
                            }
                            foreach (ConfInfo ci in confOdds)
                            {
                                double distance = ci.Price - spot;
                                string dsign = distance > 0 ? "+" : "";
                                Brush accent = distance > 0 ? BearBrush : BullBrush;

                                TextBlock t1 = new TextBlock
                                {
                                    Text = "YM " + ((int)ci.Price) + " (" + dsign + ((int)distance) + ")",
                                    Foreground = accent, FontSize = 11, FontWeight = FontWeights.SemiBold
                                };
                                confPanel.Children.Add(t1);

                                if (ci.HasOdds)
                                {
                                    TextBlock t2 = new TextBlock
                                    {
                                        Text = "  BRK " + ci.Brk.ToString("0") + "% / REJ " + ci.Rej.ToString("0")
                                             + "%  (n=" + ci.MinN + (ci.Trust ? "" : ", building") + ")",
                                        Foreground = ci.Trust ? RowText : TextDim,
                                        FontSize = 10, FontWeight = FontWeights.SemiBold
                                    };
                                    confPanel.Children.Add(t2);
                                }
                                TextBlock t3 = new TextBlock
                                {
                                    Text = "  " + ci.Names, Foreground = TextDim,
                                    FontSize = 9.5, TextWrapping = TextWrapping.Wrap,
                                    Margin = new Thickness(0, 0, 0, 3)
                                };
                                confPanel.Children.Add(t3);
                            }
                        }

                        if (ShowGammaSection)
                        {
                            gammaPanel.Children.Clear();
                            // v12 — show distances; hide 0DTE duplicates when identical to full.
                            if (flipF != null) gammaPanel.Children.Add(LevelLine(flipF, DistStr(flipF.Price, spot)));
                            if (flip0 != null && (flipF == null || Math.Abs(flip0.Price - flipF.Price) > 1))
                                gammaPanel.Children.Add(LevelLine(flip0, DistStr(flip0.Price, spot)));
                            if (cwF != null)   gammaPanel.Children.Add(LevelLine(cwF, DistStr(cwF.Price, spot)));
                            if (cw0 != null && (cwF == null || Math.Abs(cw0.Price - cwF.Price) > 1))
                                gammaPanel.Children.Add(LevelLine(cw0, DistStr(cw0.Price, spot)));
                            if (pwF != null)   gammaPanel.Children.Add(LevelLine(pwF, DistStr(pwF.Price, spot)));
                            if (pw0 != null && (pwF == null || Math.Abs(pw0.Price - pwF.Price) > 1))
                                gammaPanel.Children.Add(LevelLine(pw0, DistStr(pw0.Price, spot)));
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private static string DistStr(double levelPrice, double spot)
        {
            double d = levelPrice - spot;
            return (d >= 0 ? "+" : "") + ((int)d);
        }

        private Grid LevelLine(LevelInfo lv, string distanceText)
        {
            Grid g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Fill = lv.Color,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            TextBlock nm = new TextBlock
            {
                Text = lv.Name, Foreground = RowText,
                FontSize = 11, VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock px = new TextBlock
            {
                Text = lv.Price.ToString("0"), Foreground = RowText,
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock dist = new TextBlock
            {
                Text = distanceText, Foreground = TextDim,
                FontSize = 10, VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(dot, 0);
            Grid.SetColumn(nm, 1);
            Grid.SetColumn(px, 2);
            Grid.SetColumn(dist, 3);
            g.Children.Add(dot);
            g.Children.Add(nm);
            g.Children.Add(px);
            g.Children.Add(dist);
            return g;
        }

        private void UpdateBiasFill(double bias)
        {
            double w = biasBarGrid.ActualWidth;
            if (w < 1) w = Math.Max(200, CardWidth - 28);
            double half = w / 2.0;
            double mag = Math.Min(1.0, Math.Abs(bias) / 100.0);
            double barW = half * mag;

            if (bias >= 0)
            {
                biasFill.HorizontalAlignment = HorizontalAlignment.Left;
                biasFill.Margin = new Thickness(half, 0, 0, 0);
                biasFill.Fill = BullBrush;
            }
            else
            {
                biasFill.HorizontalAlignment = HorizontalAlignment.Right;
                biasFill.Margin = new Thickness(0, 0, half, 0);
                biasFill.Fill = BearBrush;
            }
            biasFill.Width = barW;
        }
        #endregion

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "CSV Path", Order = 1, GroupName = "1. Data")]
        public string CsvPath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stats CSV Path", Order = 2, GroupName = "1. Data")]
        public string StatsPath { get; set; }

        [NinjaScriptProperty] [Range(5, 300)]
        [Display(Name = "Refresh Seconds", Order = 3, GroupName = "1. Data")]
        public int RefreshSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Lines on Chart", Order = 1, GroupName = "2. Lines")]
        public bool DrawLines { get; set; }

        [NinjaScriptProperty] [Range(1, 10)]
        [Display(Name = "Line Width", Order = 2, GroupName = "2. Lines")]
        public int LineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Line Labels", Order = 1, GroupName = "3. Labels")]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty] [Range(6, 30)]
        [Display(Name = "Label Font Size", Order = 2, GroupName = "3. Labels")]
        public int LabelFontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Label Bold", Order = 3, GroupName = "3. Labels")]
        public bool LabelFontBold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Label Below Line", Order = 4, GroupName = "3. Labels")]
        public bool LabelBelowLine { get; set; }

        [NinjaScriptProperty] [Range(0, 500)]
        [Display(Name = "Label Offset (YM points)", Order = 5, GroupName = "3. Labels")]
        public int LabelOffsetPoints { get; set; }

        [NinjaScriptProperty] [Range(0, 200)]
        [Display(Name = "Label Right-Edge Margin (pixels)", Order = 6, GroupName = "3. Labels")]
        public int LabelPixelMargin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Dashboard", Order = 1, GroupName = "4. Dashboard")]
        public bool ShowDashboard { get; set; }

        [Display(Name = "Card Corner", Order = 2, GroupName = "4. Dashboard")]
        public YMLevelsCorner CardCorner { get; set; }

        [NinjaScriptProperty] [Range(0, 10000)]
        [Display(Name = "Card Margin X", Order = 3, GroupName = "4. Dashboard")]
        public int CardMarginX { get; set; }

        [NinjaScriptProperty] [Range(0, 10000)]
        [Display(Name = "Card Margin Y", Order = 4, GroupName = "4. Dashboard")]
        public int CardMarginY { get; set; }

        [NinjaScriptProperty] [Range(220, 1200)]
        [Display(Name = "Card Width", Order = 5, GroupName = "4. Dashboard")]
        public int CardWidth { get; set; }

        [NinjaScriptProperty] [Range(300, 1600)]
        [Display(Name = "Card Height", Order = 6, GroupName = "4. Dashboard")]
        public int CardHeight { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Bias Breakdown", Order = 7, GroupName = "4. Dashboard")]
        public bool ShowBiasBreakdown { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Nearest Section", Order = 8, GroupName = "4. Dashboard")]
        public bool ShowNearestSection { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Res/Sup Section", Order = 9, GroupName = "4. Dashboard")]
        public bool ShowResSupSection { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Confluence Section", Order = 10, GroupName = "4. Dashboard")]
        public bool ShowConfluenceSection { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Gamma Section", Order = 11, GroupName = "4. Dashboard")]
        public bool ShowGammaSection { get; set; }

        [NinjaScriptProperty] [Range(10, 500)]
        [Display(Name = "Confluence Tolerance (pts)", Order = 12, GroupName = "4. Dashboard")]
        public int ConfluenceTolerance { get; set; }

        [NinjaScriptProperty] [Range(5, 200)]
        [Display(Name = "Approach Distance (pts)", Order = 13, GroupName = "4. Dashboard")]
        public int ApproachDistance { get; set; }

        [Display(Name = "Enable Entry Signals", Order = 1, GroupName = "6. Signals")]
        public bool EnableSignals { get; set; }

        [Display(Name = "Signal Sound File", Order = 2, GroupName = "6. Signals")]
        public string SignalSoundFile { get; set; }

        [Range(50, 100)]
        [Display(Name = "Break Signal Min %", Order = 3, GroupName = "6. Signals")]
        public int BreakSignalMinPct { get; set; }

        [Range(50, 100)]
        [Display(Name = "Fade Signal Min %", Order = 4, GroupName = "6. Signals")]
        public int FadeSignalMinPct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use CSV Colors (override below)", Order = 1, GroupName = "5. Colors")]
        public bool UseCsvColors { get; set; }

        [NinjaScriptProperty] [XmlIgnore]
        [Display(Name = "Resistance (R1/R2/R3)", Order = 2, GroupName = "5. Colors")]
        public Brush ResistanceColor { get; set; }
        [Browsable(false)] public string ResistanceColorSerialize
        { get { return Serialize.BrushToString(ResistanceColor); } set { ResistanceColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty] [XmlIgnore]
        [Display(Name = "Support (S1/S2/S3)", Order = 3, GroupName = "5. Colors")]
        public Brush SupportColor { get; set; }
        [Browsable(false)] public string SupportColorSerialize
        { get { return Serialize.BrushToString(SupportColor); } set { SupportColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty] [XmlIgnore]
        [Display(Name = "Pivot (Pivot/Y-Mid)", Order = 4, GroupName = "5. Colors")]
        public Brush PivotColor { get; set; }
        [Browsable(false)] public string PivotColorSerialize
        { get { return Serialize.BrushToString(PivotColor); } set { PivotColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty] [XmlIgnore]
        [Display(Name = "Overnight (ONH/ONM/ONL)", Order = 5, GroupName = "5. Colors")]
        public Brush OvernightColor { get; set; }
        [Browsable(false)] public string OvernightColorSerialize
        { get { return Serialize.BrushToString(OvernightColor); } set { OvernightColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty] [XmlIgnore]
        [Display(Name = "Prior Week", Order = 6, GroupName = "5. Colors")]
        public Brush PriorWeekColor { get; set; }
        [Browsable(false)] public string PriorWeekColorSerialize
        { get { return Serialize.BrushToString(PriorWeekColor); } set { PriorWeekColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty] [XmlIgnore]
        [Display(Name = "Prior Month", Order = 7, GroupName = "5. Colors")]
        public Brush PriorMonthColor { get; set; }
        [Browsable(false)] public string PriorMonthColorSerialize
        { get { return Serialize.BrushToString(PriorMonthColor); } set { PriorMonthColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty] [XmlIgnore]
        [Display(Name = "Gamma Flip", Order = 8, GroupName = "5. Colors")]
        public Brush GammaFlipColor { get; set; }
        [Browsable(false)] public string GammaFlipColorSerialize
        { get { return Serialize.BrushToString(GammaFlipColor); } set { GammaFlipColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty] [XmlIgnore]
        [Display(Name = "Call Wall", Order = 9, GroupName = "5. Colors")]
        public Brush CallWallColor { get; set; }
        [Browsable(false)] public string CallWallColorSerialize
        { get { return Serialize.BrushToString(CallWallColor); } set { CallWallColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty] [XmlIgnore]
        [Display(Name = "Put Wall", Order = 10, GroupName = "5. Colors")]
        public Brush PutWallColor { get; set; }
        [Browsable(false)] public string PutWallColorSerialize
        { get { return Serialize.BrushToString(PutWallColor); } set { PutWallColor = Serialize.StringToBrush(value); } }
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private YMLevels[] cacheYMLevels;
		public YMLevels YMLevels(string csvPath, string statsPath, int refreshSeconds, bool drawLines, int lineWidth, bool showLabels, int labelFontSize, bool labelFontBold, bool labelBelowLine, int labelOffsetPoints, int labelPixelMargin, bool showDashboard, int cardMarginX, int cardMarginY, int cardWidth, int cardHeight, bool showBiasBreakdown, bool showNearestSection, bool showResSupSection, bool showConfluenceSection, bool showGammaSection, int confluenceTolerance, int approachDistance, bool useCsvColors, Brush resistanceColor, Brush supportColor, Brush pivotColor, Brush overnightColor, Brush priorWeekColor, Brush priorMonthColor, Brush gammaFlipColor, Brush callWallColor, Brush putWallColor)
		{
			return YMLevels(Input, csvPath, statsPath, refreshSeconds, drawLines, lineWidth, showLabels, labelFontSize, labelFontBold, labelBelowLine, labelOffsetPoints, labelPixelMargin, showDashboard, cardMarginX, cardMarginY, cardWidth, cardHeight, showBiasBreakdown, showNearestSection, showResSupSection, showConfluenceSection, showGammaSection, confluenceTolerance, approachDistance, useCsvColors, resistanceColor, supportColor, pivotColor, overnightColor, priorWeekColor, priorMonthColor, gammaFlipColor, callWallColor, putWallColor);
		}

		public YMLevels YMLevels(ISeries<double> input, string csvPath, string statsPath, int refreshSeconds, bool drawLines, int lineWidth, bool showLabels, int labelFontSize, bool labelFontBold, bool labelBelowLine, int labelOffsetPoints, int labelPixelMargin, bool showDashboard, int cardMarginX, int cardMarginY, int cardWidth, int cardHeight, bool showBiasBreakdown, bool showNearestSection, bool showResSupSection, bool showConfluenceSection, bool showGammaSection, int confluenceTolerance, int approachDistance, bool useCsvColors, Brush resistanceColor, Brush supportColor, Brush pivotColor, Brush overnightColor, Brush priorWeekColor, Brush priorMonthColor, Brush gammaFlipColor, Brush callWallColor, Brush putWallColor)
		{
			if (cacheYMLevels != null)
				for (int idx = 0; idx < cacheYMLevels.Length; idx++)
					if (cacheYMLevels[idx] != null && cacheYMLevels[idx].CsvPath == csvPath && cacheYMLevels[idx].StatsPath == statsPath && cacheYMLevels[idx].RefreshSeconds == refreshSeconds && cacheYMLevels[idx].DrawLines == drawLines && cacheYMLevels[idx].LineWidth == lineWidth && cacheYMLevels[idx].ShowLabels == showLabels && cacheYMLevels[idx].LabelFontSize == labelFontSize && cacheYMLevels[idx].LabelFontBold == labelFontBold && cacheYMLevels[idx].LabelBelowLine == labelBelowLine && cacheYMLevels[idx].LabelOffsetPoints == labelOffsetPoints && cacheYMLevels[idx].LabelPixelMargin == labelPixelMargin && cacheYMLevels[idx].ShowDashboard == showDashboard && cacheYMLevels[idx].CardMarginX == cardMarginX && cacheYMLevels[idx].CardMarginY == cardMarginY && cacheYMLevels[idx].CardWidth == cardWidth && cacheYMLevels[idx].CardHeight == cardHeight && cacheYMLevels[idx].ShowBiasBreakdown == showBiasBreakdown && cacheYMLevels[idx].ShowNearestSection == showNearestSection && cacheYMLevels[idx].ShowResSupSection == showResSupSection && cacheYMLevels[idx].ShowConfluenceSection == showConfluenceSection && cacheYMLevels[idx].ShowGammaSection == showGammaSection && cacheYMLevels[idx].ConfluenceTolerance == confluenceTolerance && cacheYMLevels[idx].ApproachDistance == approachDistance && cacheYMLevels[idx].UseCsvColors == useCsvColors && cacheYMLevels[idx].ResistanceColor == resistanceColor && cacheYMLevels[idx].SupportColor == supportColor && cacheYMLevels[idx].PivotColor == pivotColor && cacheYMLevels[idx].OvernightColor == overnightColor && cacheYMLevels[idx].PriorWeekColor == priorWeekColor && cacheYMLevels[idx].PriorMonthColor == priorMonthColor && cacheYMLevels[idx].GammaFlipColor == gammaFlipColor && cacheYMLevels[idx].CallWallColor == callWallColor && cacheYMLevels[idx].PutWallColor == putWallColor && cacheYMLevels[idx].EqualsInput(input))
						return cacheYMLevels[idx];
			return CacheIndicator<YMLevels>(new YMLevels(){ CsvPath = csvPath, StatsPath = statsPath, RefreshSeconds = refreshSeconds, DrawLines = drawLines, LineWidth = lineWidth, ShowLabels = showLabels, LabelFontSize = labelFontSize, LabelFontBold = labelFontBold, LabelBelowLine = labelBelowLine, LabelOffsetPoints = labelOffsetPoints, LabelPixelMargin = labelPixelMargin, ShowDashboard = showDashboard, CardMarginX = cardMarginX, CardMarginY = cardMarginY, CardWidth = cardWidth, CardHeight = cardHeight, ShowBiasBreakdown = showBiasBreakdown, ShowNearestSection = showNearestSection, ShowResSupSection = showResSupSection, ShowConfluenceSection = showConfluenceSection, ShowGammaSection = showGammaSection, ConfluenceTolerance = confluenceTolerance, ApproachDistance = approachDistance, UseCsvColors = useCsvColors, ResistanceColor = resistanceColor, SupportColor = supportColor, PivotColor = pivotColor, OvernightColor = overnightColor, PriorWeekColor = priorWeekColor, PriorMonthColor = priorMonthColor, GammaFlipColor = gammaFlipColor, CallWallColor = callWallColor, PutWallColor = putWallColor }, input, ref cacheYMLevels);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.YMLevels YMLevels(string csvPath, string statsPath, int refreshSeconds, bool drawLines, int lineWidth, bool showLabels, int labelFontSize, bool labelFontBold, bool labelBelowLine, int labelOffsetPoints, int labelPixelMargin, bool showDashboard, int cardMarginX, int cardMarginY, int cardWidth, int cardHeight, bool showBiasBreakdown, bool showNearestSection, bool showResSupSection, bool showConfluenceSection, bool showGammaSection, int confluenceTolerance, int approachDistance, bool useCsvColors, Brush resistanceColor, Brush supportColor, Brush pivotColor, Brush overnightColor, Brush priorWeekColor, Brush priorMonthColor, Brush gammaFlipColor, Brush callWallColor, Brush putWallColor)
		{
			return indicator.YMLevels(Input, csvPath, statsPath, refreshSeconds, drawLines, lineWidth, showLabels, labelFontSize, labelFontBold, labelBelowLine, labelOffsetPoints, labelPixelMargin, showDashboard, cardMarginX, cardMarginY, cardWidth, cardHeight, showBiasBreakdown, showNearestSection, showResSupSection, showConfluenceSection, showGammaSection, confluenceTolerance, approachDistance, useCsvColors, resistanceColor, supportColor, pivotColor, overnightColor, priorWeekColor, priorMonthColor, gammaFlipColor, callWallColor, putWallColor);
		}

		public Indicators.YMLevels YMLevels(ISeries<double> input , string csvPath, string statsPath, int refreshSeconds, bool drawLines, int lineWidth, bool showLabels, int labelFontSize, bool labelFontBold, bool labelBelowLine, int labelOffsetPoints, int labelPixelMargin, bool showDashboard, int cardMarginX, int cardMarginY, int cardWidth, int cardHeight, bool showBiasBreakdown, bool showNearestSection, bool showResSupSection, bool showConfluenceSection, bool showGammaSection, int confluenceTolerance, int approachDistance, bool useCsvColors, Brush resistanceColor, Brush supportColor, Brush pivotColor, Brush overnightColor, Brush priorWeekColor, Brush priorMonthColor, Brush gammaFlipColor, Brush callWallColor, Brush putWallColor)
		{
			return indicator.YMLevels(input, csvPath, statsPath, refreshSeconds, drawLines, lineWidth, showLabels, labelFontSize, labelFontBold, labelBelowLine, labelOffsetPoints, labelPixelMargin, showDashboard, cardMarginX, cardMarginY, cardWidth, cardHeight, showBiasBreakdown, showNearestSection, showResSupSection, showConfluenceSection, showGammaSection, confluenceTolerance, approachDistance, useCsvColors, resistanceColor, supportColor, pivotColor, overnightColor, priorWeekColor, priorMonthColor, gammaFlipColor, callWallColor, putWallColor);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.YMLevels YMLevels(string csvPath, string statsPath, int refreshSeconds, bool drawLines, int lineWidth, bool showLabels, int labelFontSize, bool labelFontBold, bool labelBelowLine, int labelOffsetPoints, int labelPixelMargin, bool showDashboard, int cardMarginX, int cardMarginY, int cardWidth, int cardHeight, bool showBiasBreakdown, bool showNearestSection, bool showResSupSection, bool showConfluenceSection, bool showGammaSection, int confluenceTolerance, int approachDistance, bool useCsvColors, Brush resistanceColor, Brush supportColor, Brush pivotColor, Brush overnightColor, Brush priorWeekColor, Brush priorMonthColor, Brush gammaFlipColor, Brush callWallColor, Brush putWallColor)
		{
			return indicator.YMLevels(Input, csvPath, statsPath, refreshSeconds, drawLines, lineWidth, showLabels, labelFontSize, labelFontBold, labelBelowLine, labelOffsetPoints, labelPixelMargin, showDashboard, cardMarginX, cardMarginY, cardWidth, cardHeight, showBiasBreakdown, showNearestSection, showResSupSection, showConfluenceSection, showGammaSection, confluenceTolerance, approachDistance, useCsvColors, resistanceColor, supportColor, pivotColor, overnightColor, priorWeekColor, priorMonthColor, gammaFlipColor, callWallColor, putWallColor);
		}

		public Indicators.YMLevels YMLevels(ISeries<double> input , string csvPath, string statsPath, int refreshSeconds, bool drawLines, int lineWidth, bool showLabels, int labelFontSize, bool labelFontBold, bool labelBelowLine, int labelOffsetPoints, int labelPixelMargin, bool showDashboard, int cardMarginX, int cardMarginY, int cardWidth, int cardHeight, bool showBiasBreakdown, bool showNearestSection, bool showResSupSection, bool showConfluenceSection, bool showGammaSection, int confluenceTolerance, int approachDistance, bool useCsvColors, Brush resistanceColor, Brush supportColor, Brush pivotColor, Brush overnightColor, Brush priorWeekColor, Brush priorMonthColor, Brush gammaFlipColor, Brush callWallColor, Brush putWallColor)
		{
			return indicator.YMLevels(input, csvPath, statsPath, refreshSeconds, drawLines, lineWidth, showLabels, labelFontSize, labelFontBold, labelBelowLine, labelOffsetPoints, labelPixelMargin, showDashboard, cardMarginX, cardMarginY, cardWidth, cardHeight, showBiasBreakdown, showNearestSection, showResSupSection, showConfluenceSection, showGammaSection, confluenceTolerance, approachDistance, useCsvColors, resistanceColor, supportColor, pivotColor, overnightColor, priorWeekColor, priorMonthColor, gammaFlipColor, callWallColor, putWallColor);
		}
	}
}

#endregion
