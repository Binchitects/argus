"""Math: exact and arbitrary-precision calculation, algebra and calculus
(SymPy), units (Pint), statistics (SciPy) and finance (numpy-financial)."""

import re
import statistics as stats

import sympy as sp
from sympy.parsing.sympy_parser import (convert_xor, implicit_multiplication_application,
                                        parse_expr, standard_transformations)

from . import ToolError, cap

TRANSFORMS = standard_transformations + (implicit_multiplication_application, convert_xor)


def _floats(*values):
    return [float(sp.N(v)) for v in values]


def _npf(name):
    def f(*args):
        import numpy_financial as npf
        return sp.Float(getattr(npf, name)(*_floats(*args)))
    return f


def _list(values):
    return [sp.sympify(v) for v in (values if isinstance(values, (list, tuple)) else [values])]


EXTRA = {
    # Statistics on a list, exact where it can be: mean([1, 2, 4]) = 7/3.
    "mean": lambda xs: sp.Add(*_list(xs)) / len(_list(xs)),
    "median": lambda xs: sp.nsimplify(stats.median(_list(xs))),
    "mode": lambda xs: stats.mode(_list(xs)),
    "stdev": lambda xs: sp.sqrt(sp.nsimplify(stats.variance(_list(xs)))),
    "pstdev": lambda xs: sp.sqrt(sp.nsimplify(stats.pvariance(_list(xs)))),
    "variance": lambda xs: sp.nsimplify(stats.variance(_list(xs))),
    "total": lambda xs: sp.Add(*_list(xs)),
    # Finance, as in spreadsheets (rate per period; money paid out is negative).
    "pmt": _npf("pmt"), "fv": _npf("fv"), "pv": _npf("pv"), "nper": _npf("nper"), "rate": _npf("rate"),
    "npv": lambda r, flows: sp.Float(__import__("numpy_financial").npv(float(r), _floats(*_list(flows)))),
    "irr": lambda flows: sp.Float(__import__("numpy_financial").irr(_floats(*_list(flows)))),
    # Counting and number bases.
    "nCr": sp.binomial, "nPr": lambda n, k: sp.ff(n, k),
    "to_base": lambda n, b: sp.Symbol(_to_base(int(n), int(b))),
    "from_base": lambda s, b: sp.Integer(int(str(s), int(b))),
    "percent": lambda x: sp.sympify(x) / 100,
}


def _to_base(n, b):
    if not 2 <= b <= 36:
        raise ToolError("A base is 2 to 36.")
    digits, sign, n = "0123456789abcdefghijklmnopqrstuvwxyz", "-" if n < 0 else "", abs(n)
    out = ""
    while True:
        n, r = divmod(n, b)
        out = digits[r] + out
        if n == 0:
            return sign + out


def _prepare(text):
    text = text.strip().replace("×", "*").replace("÷", "/").replace("−", "-").replace("**", "^")
    # "15% of 200" and "20%": percent as a fraction.
    text = re.sub(r"(\d+(?:\.\d+)?)\s*%\s*of\b", r"(\1/100)*", text)
    text = re.sub(r"(\d+(?:\.\d+)?)\s*%", r"(\1/100)", text)
    # Thousands separators, as people write them: 1,234,567.89
    text = re.sub(r"(?<![\w.])(\d{1,3}(?:,\d{3})+)(\.\d+)?(?![\d,])", lambda m: m.group(1).replace(",", "") + (m.group(2) or ""), text)
    return text


def _parse(text):
    try:
        return parse_expr(_prepare(text), local_dict=dict(EXTRA), transformations=TRANSFORMS, evaluate=True)
    except ToolError:
        raise
    except Exception as e:
        raise ToolError(f"Could not read '{text}': {e}. Write it as a formula, e.g. sqrt(2)*pi or 3^40 mod 7 as Mod(3^40, 7).")


def _show(value, digits):
    """The exact value when there is one, and its decimal to the digits asked."""
    if isinstance(value, bool) or value in (sp.true, sp.false):
        return {"value": bool(value)}
    if isinstance(value, (sp.MatrixBase,)):
        return {"value": cap(str(value.tolist()))}
    if isinstance(value, dict):
        return {"value": cap(str({str(k): str(v) for k, v in value.items()}))}
    if isinstance(value, (list, tuple, set)):
        return {"value": cap(str([str(v) for v in value]))}
    if not isinstance(value, sp.Basic):
        return {"value": cap(str(value))}
    if not value.is_number:
        return {"value": cap(str(sp.simplify(value)))}
    if value.is_Integer:
        text = str(value)
        return {"value": cap(text), **({"digits": len(text.lstrip("-"))} if len(text) > 30 else {})}
    decimal = sp.N(value, digits)
    out = {"value": cap(_decimal(decimal))}
    if not value.is_Float:
        out["exact"] = cap(str(value))
    return out


def _decimal(x):
    text = str(x)
    if "e" not in text and "." in text:
        text = text.rstrip("0").rstrip(".")
    return text


def tool_calculate(expression, digits=15):
    """Evaluates an expression exactly (integers of any size, fractions, roots) and to `digits` decimals."""
    digits = max(1, min(int(digits), 1000))
    value = _parse(expression)
    return {"expression": expression, **_show(value, digits)}


def _equation(text, symbols):
    for op, rel in (("<=", sp.Le), (">=", sp.Ge), ("!=", sp.Ne), ("<", sp.Lt), (">", sp.Gt), ("=", sp.Eq)):
        # "==" is written for an equation too.
        parts = re.split(r"==" if op == "=" and "==" in text else re.escape(op), text, maxsplit=1)
        if len(parts) == 2 and not (op == "=" and re.search(r"[<>!]=", text)):
            return rel(_parse(parts[0]), _parse(parts[1]))
    return _parse(text)


def tool_solve(task, expression, variable=None, lower=None, upper=None, point=None, order=None, direction=None, digits=15):
    """Algebra and calculus: solve (equations, systems, inequalities), derivative, integral, limit,
    series, simplify, factor, expand, partial_fractions, dsolve (ODEs), minimize, maximize."""
    task = task.strip().lower().replace(" ", "_")
    parts = [p for p in re.split(r"[;\n]", expression) if p.strip()]
    exprs = [_equation(p, None) for p in parts]
    free = sorted(set().union(*[e.free_symbols for e in exprs]), key=str)
    var = [sp.Symbol(v.strip()) for v in variable.split(",")] if variable else free
    x = var[0] if var else sp.Symbol("x")
    e = exprs[0]
    lo = _parse(str(lower)) if lower is not None else None
    hi = _parse(str(upper)) if upper is not None else None
    if task in ("solve", "roots"):
        if any(isinstance(q, (sp.Lt, sp.Le, sp.Gt, sp.Ge)) for q in exprs):
            out = sp.reduce_inequalities(exprs, var[0] if len(var) == 1 else var)
        else:
            eqs = [q if isinstance(q, (sp.Eq, sp.Ne)) else sp.Eq(q, 0) for q in exprs]
            out = sp.solve(eqs, var, dict=True)
            if not out:
                # No closed form: numbers, from starting points across a range.
                if len(eqs) == 1 and len(var) == 1:
                    guesses = [lo, hi, 0, 1, -1, 10, -10]
                    found = set()
                    for g in [g for g in guesses if g is not None]:
                        try:
                            found.add(sp.N(sp.nsolve(eqs[0].lhs - eqs[0].rhs, x, g), digits))
                        except Exception:
                            pass
                    out = sorted(found, key=lambda v: float(sp.re(v)))
            return {"task": task, "solutions": cap(str(out)), "decimal": cap(str(_numeric(out, digits)))}
    elif task in ("derivative", "diff", "differentiate"):
        out = sp.diff(e, x, int(order or 1))
    elif task in ("integral", "integrate"):
        out = sp.integrate(e, (x, lo, hi)) if lo is not None and hi is not None else sp.integrate(e, x)
    elif task == "limit":
        out = sp.limit(e, x, _parse(str(point if point is not None else 0)), direction or "+-")
    elif task in ("series", "taylor"):
        out = sp.series(e, x, _parse(str(point if point is not None else 0)), int(order or 6)).removeO()
    elif task in ("simplify", "factor", "expand", "partial_fractions", "apart", "trigsimp", "cancel"):
        fn = {"partial_fractions": sp.apart}.get(task) or getattr(sp, task)
        out = fn(e)
    elif task in ("dsolve", "ode"):
        out = sp.dsolve(exprs if len(exprs) > 1 else e)
    elif task in ("minimize", "maximize"):
        return _extremum(task, e, x, lo, hi, digits)
    else:
        raise ToolError(f"Unknown task '{task}'. Tasks: solve, derivative, integral, limit, series, simplify, factor, expand, partial_fractions, dsolve, minimize, maximize.")
    return {"task": task, "result": cap(str(out)), **({"decimal": _decimal(sp.N(out, digits))} if isinstance(out, sp.Basic) and out.is_number and not out.is_Integer else {})}


def _numeric(out, digits):
    try:
        if isinstance(out, list):
            return [{str(k): _decimal(sp.N(v, digits)) for k, v in s.items()} if isinstance(s, dict) else _decimal(sp.N(s, digits)) for s in out]
        return str(out)
    except Exception:
        return str(out)


def _extremum(task, f, x, lo, hi, digits):
    """Where f is smallest or largest: critical points and the ends of the range."""
    candidates = [c for c in sp.solve(sp.diff(f, x), x) if c.is_real is not False]
    candidates = [sp.N(c) for c in candidates if sp.im(sp.N(c)) == 0]
    if lo is not None:
        candidates = [c for c in candidates if c >= lo] + [lo]
    if hi is not None:
        candidates = [c for c in candidates if c <= hi] + [hi]
    if not candidates:
        raise ToolError("No critical points found: give lower and upper to search a range.")
    values = [(c, sp.N(f.subs(x, c), digits)) for c in candidates]
    best = (min if task == "minimize" else max)(values, key=lambda p: float(p[1]))
    return {"task": task, "at": {str(x): _decimal(sp.N(best[0], digits))}, "value": _decimal(best[1]),
            "checked": [{"at": _decimal(sp.N(c, 10)), "value": _decimal(v)} for c, v in values][:20]}


_UNITS = None
_ALIASES = {"c": "degC", "°c": "degC", "celsius": "degC", "f": "degF", "°f": "degF", "fahrenheit": "degF", "k": "kelvin",
            "kmh": "km/h", "kph": "km/h", "mph": "mile/hour", "sqm": "m**2", "sqft": "ft**2", "cc": "cm**3"}


def tool_convert_units(value, from_unit, to_unit):
    """Converts between units: length, mass, time, speed, temperature, data, energy, pressure... (no currencies: no network)."""
    global _UNITS
    import pint
    if _UNITS is None:
        _UNITS = pint.UnitRegistry(autoconvert_offset_to_baseunit=True)
    unit = lambda u: _ALIASES.get(u.strip().lower(), u.strip())
    try:
        q = _UNITS.Quantity(float(sp.N(_parse(str(value)))), unit(from_unit)).to(unit(to_unit))
    except pint.errors.DimensionalityError as e:
        raise ToolError(f"{from_unit} cannot become {to_unit}: {e}") from None
    except (pint.errors.UndefinedUnitError, AttributeError) as e:
        raise ToolError(f"Unknown unit: {e}. Use names like km, mile, lb, degC, kWh, GiB, psi.") from None
    return {"value": float(f"{q.magnitude:.12g}"), "unit": f"{q.units:~P}" or str(q.units), "from": f"{value} {from_unit}"}


def tool_statistics(values, y=None, test=None):
    """Describes numbers; with y, their correlation and a linear regression; test compares two groups."""
    import numpy as np
    from scipy import stats as st
    a = np.array([float(v) for v in values], dtype=float)
    if a.size == 0:
        raise ToolError("Give some numbers in 'values'.")
    q1, q3 = np.percentile(a, [25, 75])
    out = {
        "count": int(a.size), "sum": float(a.sum()), "mean": float(a.mean()), "median": float(np.median(a)),
        "min": float(a.min()), "max": float(a.max()), "range": float(a.max() - a.min()),
        "stdev_sample": float(a.std(ddof=1)) if a.size > 1 else None, "stdev_population": float(a.std()),
        "variance_sample": float(a.var(ddof=1)) if a.size > 1 else None,
        "q1": float(q1), "q3": float(q3), "iqr": float(q3 - q1),
        "p5": float(np.percentile(a, 5)), "p95": float(np.percentile(a, 95)),
    }
    modes = st.mode(a, keepdims=False)
    out["mode"] = float(modes.mode)
    if a.size > 2:
        out["skewness"] = float(st.skew(a))
        out["kurtosis"] = float(st.kurtosis(a))
    if (a > 0).all():
        out["geometric_mean"] = float(st.gmean(a))
    if y is not None:
        b = np.array([float(v) for v in y], dtype=float)
        if test:
            kind = test.lower().replace("-", "_").replace(" ", "_")
            if kind in ("ttest", "t_test", "welch"):
                r = st.ttest_ind(a, b, equal_var=False)
            elif kind in ("paired", "paired_ttest"):
                r = st.ttest_rel(a, b)
            elif kind in ("mannwhitney", "mann_whitney", "u_test"):
                r = st.mannwhitneyu(a, b)
            else:
                raise ToolError("test is ttest, paired_ttest or mannwhitney.")
            out["test"] = {"kind": kind, "statistic": float(r.statistic), "p_value": float(r.pvalue),
                           "mean_y": float(b.mean())}
        else:
            if b.size != a.size:
                raise ToolError("values and y must be as long as each other.")
            reg = st.linregress(a, b)
            out["regression"] = {"slope": float(reg.slope), "intercept": float(reg.intercept), "r": float(reg.rvalue),
                                 "r_squared": float(reg.rvalue ** 2), "p_value": float(reg.pvalue), "stderr": float(reg.stderr),
                                 "equation": f"y = {reg.slope:.6g}x + {reg.intercept:.6g}"}
            out["spearman"] = float(st.spearmanr(a, b).statistic)
    return {k: (round(v, 12) if isinstance(v, float) else v) for k, v in out.items()}
