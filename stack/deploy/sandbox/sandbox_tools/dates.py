"""Date and time: now anywhere, conversions between time zones, date arithmetic
with business days and public holidays, spans between dates, facts about a
date (including its Persian calendar date), holidays, and recurrences."""

import datetime as dt
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from dateutil import parser as dateparser
from dateutil import rrule
from dateutil.relativedelta import relativedelta

from . import ToolError

DAYS = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"]
# Weekends that are not Saturday and Sunday.
WEEKENDS = {"IR": [4], "AF": [4], "SA": [4, 5], "IL": [4, 5], "EG": [4, 5], "IQ": [4, 5], "JO": [4, 5], "KW": [4, 5],
            "OM": [4, 5], "QA": [4, 5], "BH": [4, 5], "DZ": [4, 5], "SY": [4, 5], "YE": [4, 5], "LY": [4, 5], "SD": [4, 5]}


def _zone(name):
    if not name:
        return ZoneInfo("UTC")
    aliases = {"tehran": "Asia/Tehran", "iran": "Asia/Tehran", "utc": "UTC", "gmt": "UTC", "london": "Europe/London",
               "new york": "America/New_York", "est": "America/New_York", "pst": "America/Los_Angeles", "cet": "Europe/Berlin"}
    try:
        return ZoneInfo(aliases.get(name.strip().lower(), name.strip()))
    except (ZoneInfoNotFoundError, ValueError):
        raise ToolError(f"Unknown time zone '{name}'. Use an IANA name such as Asia/Tehran or America/New_York.") from None


def _when(text, zone=None, default_time=True):
    """A date or date and time as people write it: 2026-10-03, 3 Oct 2026 9:30, 2026-10-03T09:30+02:00."""
    if isinstance(text, (int, float)):
        return dt.datetime.fromtimestamp(text, tz=zone or ZoneInfo("UTC"))
    t = str(text).strip().lower()
    now = dt.datetime.now(zone or ZoneInfo("UTC"))
    if t in ("now", ""):
        return now
    if t in ("today", "tomorrow", "yesterday"):
        return now.replace(hour=0, minute=0, second=0, microsecond=0) + dt.timedelta(days={"today": 0, "tomorrow": 1, "yesterday": -1}[t])
    try:
        value = dateparser.parse(str(text), default=now.replace(hour=0, minute=0, second=0, microsecond=0, tzinfo=None))
    except (ValueError, OverflowError):
        raise ToolError(f"Could not read the date '{text}'. Write it as YYYY-MM-DD, optionally with HH:MM.") from None
    if value.tzinfo is None:
        value = value.replace(tzinfo=zone or ZoneInfo("UTC"))
    return value


def _describe(value):
    return {"iso": value.isoformat(timespec="minutes" if value.second == 0 else "seconds"), "weekday": DAYS[value.weekday()].capitalize(),
            "utc_offset": value.strftime("%z")[:3] + ":" + value.strftime("%z")[3:], "zone": getattr(value.tzinfo, "key", str(value.tzinfo)),
            "abbreviation": value.tzname(), "dst": bool(value.dst())}


def tool_now(time_zones=None):
    """The current date and time in one or more time zones."""
    zones = time_zones if isinstance(time_zones, list) else [time_zones] if time_zones else ["UTC"]
    utc = dt.datetime.now(dt.timezone.utc)
    return {"utc": utc.isoformat(timespec="seconds"), "unix": int(utc.timestamp()),
            "times": [_describe(utc.astimezone(_zone(z))) for z in zones]}


def tool_convert_time(time, from_zone, to_zones):
    """A date and time in one zone, in others (e.g. a meeting at 9:00 in New York, in Tehran and Berlin)."""
    source = _when(time, _zone(from_zone))
    zones = to_zones if isinstance(to_zones, list) else [to_zones]
    return {"from": _describe(source), "to": [_describe(source.astimezone(_zone(z))) for z in zones]}


def _holidays(country, subdivision, years):
    if not country:
        return {}
    import holidays
    try:
        return holidays.country_holidays(country.upper(), subdiv=subdivision, years=years)
    except (NotImplementedError, KeyError):
        raise ToolError(f"No holiday calendar for '{country}'. Use an ISO country code such as US, DE, GB, IR, IN.") from None


def _weekend(country, weekend):
    if weekend:
        names = weekend if isinstance(weekend, list) else [weekend]
        return {DAYS.index(next(d for d in DAYS if d.startswith(str(n).lower()[:3]))) for n in names}
    return set(WEEKENDS.get((country or "").upper(), [5, 6]))


def tool_add(date, years=0, months=0, weeks=0, days=0, hours=0, minutes=0, business_days=0, country=None, subdivision=None, weekend=None, time_zone=None):
    """Date arithmetic: add (or with negatives, subtract) calendar units and business days (skipping weekends and the country's public holidays)."""
    start = _when(date, _zone(time_zone))
    value = start + relativedelta(years=int(years), months=int(months), weeks=int(weeks), days=int(days), hours=int(hours), minutes=int(minutes))
    skipped = []
    if business_days:
        off, step, left = _weekend(country, weekend), (1 if business_days > 0 else -1), abs(int(business_days))
        hol = _holidays(country, subdivision, range(value.year - 1, value.year + 3 + left // 200))
        while left:
            value += dt.timedelta(days=step)
            if value.weekday() in off:
                continue
            if value.date() in hol:
                skipped.append(f"{value.date()} {hol.get(value.date())}")
                continue
            left -= 1
    out = {"start": _describe(start), "result": _describe(value), "date": str(value.date())}
    if skipped:
        out["holidays_skipped"] = skipped[:30]
    return out


def tool_between(start, end, country=None, subdivision=None, weekend=None, time_zone=None):
    """The span between two dates or times: days, weeks, months and years, business days, and the holidays in between."""
    a, b = _when(start, _zone(time_zone)), _when(end, _zone(time_zone))
    sign = 1 if b >= a else -1
    lo, hi = (a, b) if sign > 0 else (b, a)
    rel = relativedelta(hi, lo)
    total = hi - lo
    off = _weekend(country, weekend)
    hol = _holidays(country, subdivision, range(lo.year, hi.year + 1)) if country else {}
    business, in_between = 0, []
    day = lo.date()
    while day < hi.date():
        if day.weekday() not in off:
            if day in hol:
                in_between.append(f"{day} {hol.get(day)}")
            else:
                business += 1
        day += dt.timedelta(days=1)
    out = {"days": sign * total.days, "weeks": round(sign * total.days / 7, 2),
           "as_years_months_days": {"years": rel.years, "months": rel.months, "days": rel.days},
           "business_days": sign * business, "weekend": [DAYS[d] for d in sorted(off)]}
    if total.seconds or a.time() != dt.time(0) or b.time() != dt.time(0):
        out["hours"] = round(sign * total.total_seconds() / 3600, 4)
        out["minutes"] = round(sign * total.total_seconds() / 60, 2)
    if country:
        out["holidays_on_workdays"] = in_between[:50]
    return out


def _jalali(g):
    """Gregorian to the Persian (Solar Hijri) calendar."""
    gy, gm, gd = g.year, g.month, g.day
    g_d_m = [0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334]
    gy2 = gy + 1 if gm > 2 else gy
    days = 355666 + (365 * gy) + ((gy2 + 3) // 4) - ((gy2 + 99) // 100) + ((gy2 + 399) // 400) + gd + g_d_m[gm - 1]
    jy = -1595 + (33 * (days // 12053))
    days %= 12053
    jy += 4 * (days // 1461)
    days %= 1461
    if days > 365:
        jy += (days - 1) // 365
        days = (days - 1) % 365
    jm = 1 + days // 31 if days < 186 else 7 + (days - 186) // 30
    jd = 1 + (days % 31 if days < 186 else (days - 186) % 30)
    months = ["Farvardin", "Ordibehesht", "Khordad", "Tir", "Mordad", "Shahrivar", "Mehr", "Aban", "Azar", "Dey", "Bahman", "Esfand"]
    return {"date": f"{jy:04d}/{jm:02d}/{jd:02d}", "month_name": months[jm - 1]}


def tool_info(date, country=None, subdivision=None, time_zone=None):
    """Facts about a date: weekday, ISO week, day of year, quarter, leap year, holidays, its Persian calendar date."""
    value = _when(date, _zone(time_zone))
    d = value.date()
    iso = d.isocalendar()
    nxt = (d.replace(day=28) + dt.timedelta(days=4))
    out = {"date": str(d), "weekday": DAYS[d.weekday()].capitalize(), "iso_week": f"{iso.year}-W{iso.week:02d}",
           "day_of_year": d.timetuple().tm_yday, "quarter": (d.month - 1) // 3 + 1,
           "days_in_month": (nxt - dt.timedelta(days=nxt.day)).day, "leap_year": d.year % 4 == 0 and (d.year % 100 != 0 or d.year % 400 == 0),
           "persian_calendar": _jalali(d), "unix": int(value.timestamp())}
    if country:
        hol = _holidays(country, subdivision, [d.year])
        out["holiday"] = hol.get(d)
        out["weekend"] = d.weekday() in _weekend(country, None)
    return out


def tool_holidays(country, year=None, subdivision=None):
    """A country's public holidays in a year."""
    year = int(year or dt.date.today().year)
    hol = _holidays(country, subdivision, [year])
    return {"country": country.upper(), "year": year, "holidays": [f"{d} {DAYS[d.weekday()][:3]} {name}" for d, name in sorted(hol.items())]}


def tool_recurrence(rule, start, count=10, until=None, time_zone=None):
    """The dates of a recurring event, from an iCalendar RRULE (e.g. FREQ=MONTHLY;BYDAY=2TU for every second Tuesday)."""
    begin = _when(start, _zone(time_zone))
    text = rule.strip()
    if text.upper().startswith("RRULE:"):
        text = text[6:]
    try:
        r = rrule.rrulestr(text, dtstart=begin)
    except (ValueError, TypeError) as e:
        raise ToolError(f"Could not read the rule: {e}. Example: FREQ=WEEKLY;BYDAY=MO,WE;INTERVAL=2") from None
    limit = min(int(count), 200)
    end = _when(until, _zone(time_zone)) if until else None
    dates = []
    for when in r:
        if end and when > end or len(dates) >= limit:
            break
        dates.append(f"{when.isoformat(timespec='minutes')} {DAYS[when.weekday()][:3]}")
    return {"rule": text, "dates": dates}
