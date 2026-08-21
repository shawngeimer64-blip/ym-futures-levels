"""Synthetic tests for the gamma math in ym_full_levels.py.

Synthetic rather than live because Yahoo serves no open interest outside RTH —
at 02:11 ET all 717 in-band contracts had an implied vol and zero OI, and by
14:06 ET it was 111/111 on DIA. Tests that needed a live chain could therefore
only be run during the session, so the edge cases are constructed instead.

    python test_gamma.py
"""
import importlib.util, os, sys
from datetime import datetime

_here = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "ymf", os.path.join(_here, "ym_full_levels.py"))
m = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(m)

fails = []

def check(name, got, want):
    ok = got == want
    print("  %s  %s: got %r, want %r" % ("PASS" if ok else "FAIL", name, got, want))
    if not ok:
        fails.append(name)

def book(rows):
    b = {}
    for k, c, p in rows:
        d = m._bucket()
        d["call"], d["put"], d["net"] = c, p, c - p
        b[k] = d
    return b

print("1) NaN coercion — yfinance hands back NaN openInterest, and NaN is TRUTHY,")
print("   so the old `row['openInterest'] or 0` let it through and poisoned the sum.")
check("_num(nan)", m._num(float("nan")), 0.0)
check("_num(inf)", m._num(float("inf")), 0.0)
check("_num(None)", m._num(None), 0.0)
check("_num('12')", m._num("12"), 12.0)

print("\n2) Call wall is found on CALL gamma, so heavy puts at the same strike")
print("   cannot cancel it out of contention (the old net-based test dropped it).")
check("call wall", m.find_walls(
    book([(535, 10., 1.), (540, 100., 130.), (545, 40., 2.)]), 530)[0], 540)

print("\n3) Put wall is found on PUT gamma, below spot.")
check("put wall", m.find_walls(
    book([(515, 5., 90.), (520, 60., 70.), (525, 3., 20.)]), 530)[1], 515)

print("\n4) Flip is per-strike, nearest spot, and INVARIANT to where the band starts.")
print("   The cumulative form crossed zero only at the band EDGE - measured live on")
print("   2026-08-20 it returned 502.92, i.e. 2,548 YM pts below the market, because")
print("   truncating the low tail moves the running total's zero. Per-strike cannot.")
b = book([(470, 0, 10), (475, 12, 0), (480, 0, 14), (500, 0, 1),
          (530, 0, 20), (535, 42, 0), (540, 0, 1)])
full = m.find_flip(b, spot=533)
check("flip near spot 533", round(full, 2), 531.61)
# Drop the two lowest strikes. A truncation-sensitive flip moves; this must not.
truncated = {k: v for k, v in b.items() if k >= 480}
check("unchanged after truncating the low tail",
      round(m.find_flip(truncated, spot=533), 2), round(full, 2))
check("is not a band-edge crossing", round(full, 2) in (472.27, 477.31), False)

print("\n5) 0DTE is priced by the session time actually left, not a full day.")
noon = datetime(2026, 8, 20, 12, 0, tzinfo=m.ET)
check("0DTE at noon = 4 session-hours",
      round(m._t_years(0, noon) * m.TRADING_HOURS_PER_YEAR, 2), 4.0)
check("0DTE < 1DTE", m._t_years(0, noon) < m._t_years(1, noon), True)
check("after the close is floored positive",
      m._t_years(0, datetime(2026, 8, 20, 18, 0, tzinfo=m.ET)) > 0, True)

print("\n6) An empty book degrades cleanly — this is the real case today.")
check("find_flip({})", m.find_flip({}, 530), None)
check("find_walls({})", m.find_walls({}, 530), (None, None))

print("\n7) One ATM vol per expiry — median, so a single junk quote cannot drag it.")


class _Frame:
    def __init__(self, rows):
        self._rows = rows

    def iterrows(self):
        return enumerate(self._rows)


class _Chain:
    def __init__(self, calls, puts):
        self.calls, self.puts = _Frame(calls), _Frame(puts)


# Five strikes at a sane 0.20 vol, plus one absurd 9.0 outlier on a near strike.
calls = [{"strike": k, "impliedVolatility": 0.20} for k in (528, 529, 530, 531, 532)]
puts = [{"strike": k, "impliedVolatility": 0.20} for k in (526, 527, 528, 529)]
puts.append({"strike": 530.0, "impliedVolatility": 9.0})       # the junk quote
iv = m._atm_iv(_Chain(calls, puts), 530.0, 500.0, 560.0)
check("median ignores the 9.0 outlier", iv, 0.20)
check("no usable vol -> None", m._atm_iv(_Chain([], []), 530.0, 500.0, 560.0), None)

print("\n8) Wall ladder ranks strongest-first with each rank's share of the leader.")
b = book([(520, 0, 100.), (522, 0, 80.), (524, 0, 50.), (526, 0, 10.)])
lad = m.wall_ladder(b, 530, "put", depth=3)
check("depth honoured", len(lad), 3)
check("rank 1 is the strongest strike", lad[0][0], 520)
check("rank 1 share is 100%", round(lad[0][2], 3), 1.0)
check("rank 2 share vs leader", round(lad[1][2], 2), 0.8)
check("ranks are descending", [r[1] for r in lad] == sorted([r[1] for r in lad], reverse=True), True)
check("empty side -> []", m.wall_ladder({}, 530, "put"), [])
# The ladder must agree with the single-wall answer, or the chart and the
# console would be naming different strikes as "the" wall.
check("rank 1 == find_walls put wall", lad[0][0], m.find_walls(b, 530)[1])

print("\n9) Wall freeze — walls hold for the session, flips do not.")
import tempfile

_tmp = tempfile.mkdtemp()
m.OUTPUT_DIR = _tmp                     # redirect the freeze file away from real data
check("nothing frozen yet", m.load_frozen_walls("2026-08-20"), None)

walls = {"Call Wall": 53103, "Put Wall": 52603, "Put Wall 2": 52803}
m.save_frozen_walls("2026-08-20", walls, {"Put Wall": 52803}, 52854, 527.51)
got = m.load_frozen_walls("2026-08-20")
check("round-trips the walls", got["walls"], walls)
check("round-trips contested", got["contested"], {"Put Wall": 52803})
check("records the spot it froze at", got["ym_price"], 52854)

# The whole point: a freeze from another day must not be reused.
check("yesterday's freeze is not reused", m.load_frozen_walls("2026-08-19"), None)

# An overnight run has no gamma, and pinning an empty set would blank the walls
# for the entire day — so an empty freeze must never be treated as valid.
m.save_frozen_walls("2026-08-21", {}, {}, 52854, 527.51)
check("an empty freeze is refused", m.load_frozen_walls("2026-08-21"), None)

print("\n" + ("ALL PASS" if not fails else "FAILURES: %s" % fails))
sys.exit(1 if fails else 0)
