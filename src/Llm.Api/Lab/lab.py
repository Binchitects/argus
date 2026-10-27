"""The science lab's tools, run in the sandbox: exact mathematics with SymPy, units with Pint.

Reads its call from lab-call.json beside it ({"op": ..., "args": {...}}) and prints one JSON object: what the tool
returns. The expressions are the model's words, parsed by SymPy here, in the sandbox (no network, a time limit): never
code of the app's. A claim is "proved" only when SymPy shows it exactly; "holds numerically" when it is true at every
point tried but not shown; "refuted" with the point where it fails.
"""

import json
import random
import re
import sys

import sympy as sp
from sympy.parsing.sympy_parser import convert_xor, implicit_multiplication_application, parse_expr, standard_transformations

MAX_LENGTH = 4000
TRANSFORMS = standard_transformations + (implicit_multiplication_application, convert_xor)
# SymPy reads an expression with Python's eval: what is not mathematics (names with __, attributes, imports, code) is
# refused before it is read. The sandbox is the boundary; this keeps the tool to what it is for.
NOT_MATHEMATICS = re.compile(r"__|[A-Za-z_]\s*\.\s*[A-Za-z_]|\b(import|lambda|exec|eval|open|globals|locals|getattr|setattr|delattr|compile|input|breakpoint|vars|dir|type|object|class|def|os|sys|subprocess)\b|[;\n`]")
OPERATIONS = ("simplify", "expand", "factor", "diff", "integrate", "solve", "limit", "series", "evaluate", "apart", "together", "det", "inverse", "eigenvals")


class LabError(Exception):
    """A call the tool cannot do: its words go back to the model."""


def parse(text, name="expression"):
    if not isinstance(text, str) or not text.strip():
        raise LabError(f"Give {name} as text, for example x**2 + 2*x + 1.")
    if len(text) > MAX_LENGTH:
        raise LabError(f"{name} is longer than {MAX_LENGTH} characters.")
    if NOT_MATHEMATICS.search(text):
        raise LabError(f"{name} is not mathematics this tool reads: write it as SymPy does (x**2 + sin(x), Matrix([[1, 2], [3, 4]])).")
    try:
        return parse_expr(text, transformations=TRANSFORMS, evaluate=True)
    except Exception as e:  # SymPy says what it could not read
        raise LabError(f"{name} could not be read as mathematics: {type(e).__name__}: {e}") from None


def symbol(name, expr=None):
    if name:
        return sp.Symbol(name)
    free = sorted(getattr(expr, "free_symbols", set()), key=str)
    if len(free) == 1:
        return free[0]
    if not free:
        raise LabError("There is no variable to work on: name one in variable.")
    raise LabError(f"Say which variable: the expression has {', '.join(map(str, free))}.")


def shown(value):
    return {"result": str(value), "latex": sp.latex(value)}


def compute(a):
    op = a.get("operation") or "simplify"
    if op not in OPERATIONS:
        raise LabError(f"operation is one of {', '.join(OPERATIONS)}.")
    e = parse(a.get("expression"))
    if op == "simplify":
        out = sp.simplify(e)
    elif op == "expand":
        out = sp.expand(e)
    elif op == "factor":
        out = sp.factor(e)
    elif op == "apart":
        out = sp.apart(e, symbol(a.get("variable"), e))
    elif op == "together":
        out = sp.together(e)
    elif op == "diff":
        out = sp.diff(e, symbol(a.get("variable"), e), int(a.get("order") or 1))
    elif op == "integrate":
        x = symbol(a.get("variable"), e)
        if a.get("lower") is not None and a.get("upper") is not None:
            out = sp.integrate(e, (x, parse(str(a["lower"]), "lower"), parse(str(a["upper"]), "upper")))
        else:
            out = sp.integrate(e, x)
    elif op == "solve":
        x = symbol(a.get("variable"), e)
        out = sp.solve(e, x)
    elif op == "limit":
        x = symbol(a.get("variable"), e)
        out = sp.limit(e, x, parse(str(a.get("at", "0")), "at"), a.get("direction") or "+-")
    elif op == "series":
        x = symbol(a.get("variable"), e)
        out = sp.series(e, x, parse(str(a.get("at", "0")), "at"), int(a.get("order") or 6))
    elif op == "evaluate":
        out = sp.N(e, int(a.get("digits") or 15))
    else:
        m = sp.Matrix(e) if not isinstance(e, sp.MatrixBase) else e
        out = m.det() if op == "det" else m.inv() if op == "inverse" else m.eigenvals()
    result = shown(out)
    result["operation"] = op
    if op not in ("evaluate", "solve", "eigenvals", "inverse") and getattr(out, "is_number", False):
        result["numeric"] = str(sp.N(out, 15))
    return result


def check(a):
    left, right = parse(a.get("left"), "left"), parse(a.get("right"), "right")
    difference = sp.simplify(left - right)
    if difference == 0:
        return {"verdict": "proved", "detail": "SymPy shows left − right simplifies to 0.", "left": sp.latex(left), "right": sp.latex(right)}
    free = sorted(left.free_symbols | right.free_symbols, key=str)
    if not free:
        value = sp.N(difference, 30)
        if value.is_number and abs(complex(value)) < 1e-25:
            return {"verdict": "proved", "detail": "Both sides are the same number (to 30 digits).", "left": sp.latex(left), "right": sp.latex(right)}
        return {"verdict": "refuted", "detail": f"Left − right is {sp.N(difference, 15)}, not 0.", "left": sp.latex(left), "right": sp.latex(right)}
    rng = random.Random(7)
    tried = 0
    for _ in range(60):
        point = {s: sp.Rational(rng.randint(-300, 300), rng.randint(7, 97)) for s in free}
        try:
            value = complex(sp.N(difference.subs(point), 30))
        except (TypeError, ValueError, ZeroDivisionError):
            continue
        if value != value or abs(value) == float("inf"):
            continue
        tried += 1
        scale = max(1.0, abs(complex(sp.N(left.subs(point), 30))))
        if abs(value) > 1e-9 * scale:
            return {
                "verdict": "refuted",
                "detail": f"At {', '.join(f'{k} = {v}' for k, v in point.items())} left − right is {sp.N(difference.subs(point), 12)}.",
                "counterexample": {str(k): str(v) for k, v in point.items()},
                "left": sp.latex(left), "right": sp.latex(right),
            }
        if tried >= 20:
            break
    if tried == 0:
        return {"verdict": "unknown", "detail": "Neither shown exactly nor testable at any point tried.", "left": sp.latex(left), "right": sp.latex(right)}
    return {
        "verdict": "holds numerically",
        "detail": f"Not shown exactly, but equal at {tried} points tried (to 1e-9). Not a proof.",
        "left": sp.latex(left), "right": sp.latex(right),
    }


def solve(a):
    equations = a.get("equations")
    if isinstance(equations, str):
        equations = [equations]
    if not equations:
        raise LabError("Give equations: a list such as [\"x + y = 3\", \"x - y = 1\"].")
    parsed = []
    for i, eq in enumerate(equations):
        if not isinstance(eq, str):
            raise LabError("Each equation is text.")
        if "=" in eq and "==" not in eq and not any(op in eq for op in ("<=", ">=")):
            lhs, rhs = eq.split("=", 1)
            parsed.append(sp.Eq(parse(lhs, f"equation {i + 1}"), parse(rhs, f"equation {i + 1}")))
        else:
            parsed.append(parse(eq, f"equation {i + 1}"))
    names = a.get("variables") or []
    variables = [sp.Symbol(n) for n in names] if names else sorted(set().union(*(e.free_symbols for e in parsed)), key=str)
    solutions = sp.solve(parsed, variables, dict=True)
    out = []
    for s in solutions:
        checked = all(sp.simplify((e.lhs - e.rhs if isinstance(e, sp.Equality) else e).subs(s)) == 0 for e in parsed)
        out.append({"solution": {str(k): str(v) for k, v in s.items()}, "latex": ", ".join(f"{sp.latex(k)} = {sp.latex(v)}" for k, v in s.items()), "checked": checked})
    return {"solutions": out, "count": len(out), "detail": "Each solution put back into every equation." if out else "No solution."}


def units(a):
    import pint

    ureg = pint.UnitRegistry()
    text, to = a.get("quantity"), a.get("to")
    if not isinstance(text, str) or not text.strip():
        raise LabError("Give quantity as text, for example 9.81 m/s^2.")
    try:
        q = ureg.Quantity(text)
        if to:
            q = q.to(to)
        else:
            q = q.to_base_units()
    except pint.errors.DimensionalityError as e:
        raise LabError(f"The units do not match: {e}") from None
    except Exception as e:
        raise LabError(f"Could not read the quantity: {type(e).__name__}: {e}") from None
    return {"result": f"{q.magnitude:.12g} {q.units:~P}", "magnitude": q.magnitude, "units": str(q.units), "dimensions": str(q.dimensionality)}


OPS = {"math_compute": compute, "math_check": check, "math_solve": solve, "units_convert": units}


def main():
    with open("lab-call.json", encoding="utf-8") as f:
        call = json.load(f)
    op = OPS.get(call.get("op"))
    try:
        if op is None:
            raise LabError(f"There is no lab tool {call.get('op')}.")
        print(json.dumps({"ok": True, "result": op(call.get("args") or {})}, default=str))
    except LabError as e:
        print(json.dumps({"ok": False, "error": str(e)}))
    except Exception as e:  # SymPy's own failure: said, not raised
        print(json.dumps({"ok": False, "error": f"{type(e).__name__}: {e}"}))


if __name__ == "__main__":
    sys.exit(main())
