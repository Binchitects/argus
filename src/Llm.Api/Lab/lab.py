"""The science lab's tools, run in the sandbox: exact mathematics with SymPy, units with Pint, molecules, formulas and
reactions with RDKit, sequences with Biopython.

Reads its call from lab-call.json beside it ({"op": ..., "args": {...}}) and prints one JSON object: what the tool
returns, and what the page shows beside it (a molecule's drawing: "show", not read by the model). The expressions are
the model's words, parsed here, in the sandbox (no network, a time limit): never code of the app's. A claim is "proved"
only when SymPy shows it exactly; "holds numerically" when it is true at every point tried but not shown; "refuted"
with the point where it fails. A balanced reaction is checked atom by atom (and charge) on both sides.
"""

import functools
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


MAX_SEQUENCE = 20000


def molecule_of(smiles, name="smiles"):
    from rdkit import Chem, RDLogger

    RDLogger.DisableLog("rdApp.*")
    if not isinstance(smiles, str) or not smiles.strip():
        raise LabError(f"Give {name} as a SMILES string, for example CCO for ethanol.")
    if len(smiles) > MAX_LENGTH:
        raise LabError(f"{name} is longer than {MAX_LENGTH} characters.")
    mol = Chem.MolFromSmiles(smiles.strip())
    if mol is None:
        raise LabError(f"{name} is not a SMILES RDKit reads: {smiles!r}. Check its brackets, ring numbers and valences.")
    return mol


def drawing(mol):
    from rdkit.Chem.Draw import rdMolDraw2D

    d = rdMolDraw2D.MolDraw2DSVG(360, 260)
    d.drawOptions().clearBackground = False
    d.DrawMolecule(mol)
    d.FinishDrawing()
    return d.GetDrawingText()


def chem_molecule(a):
    from rdkit import Chem
    from rdkit.Chem import Crippen, Descriptors, Lipinski, rdMolDescriptors

    mol = molecule_of(a.get("smiles"))
    weight = Descriptors.MolWt(mol)
    donors, acceptors, logp = Lipinski.NumHDonors(mol), Lipinski.NumHAcceptors(mol), Crippen.MolLogP(mol)
    broken = [rule for rule, bad in (("weight over 500", weight > 500), ("logP over 5", logp > 5), ("over 5 H donors", donors > 5), ("over 10 H acceptors", acceptors > 10)) if bad]
    result = {
        "smiles": Chem.MolToSmiles(mol),
        "formula": rdMolDescriptors.CalcMolFormula(mol),
        "molar_mass": f"{weight:.3f} g/mol",
        "exact_mass": round(Descriptors.ExactMolWt(mol), 6),
        "charge": Chem.GetFormalCharge(mol),
        "heavy_atoms": mol.GetNumHeavyAtoms(),
        "rings": rdMolDescriptors.CalcNumRings(mol),
        "aromatic_rings": rdMolDescriptors.CalcNumAromaticRings(mol),
        "stereocentres": len(Chem.FindMolChiralCenters(mol, includeUnassigned=True, useLegacyImplementation=False)),
        "logP": round(logp, 2),
        "tpsa": round(rdMolDescriptors.CalcTPSA(mol), 2),
        "h_donors": donors,
        "h_acceptors": acceptors,
        "rotatable_bonds": Lipinski.NumRotatableBonds(mol),
        "lipinski": "passes the rule of five" if not broken else "breaks the rule of five: " + ", ".join(broken),
        "inchi": Chem.MolToInchi(mol),
        "inchikey": Chem.MolToInchiKey(mol),
    }
    return result, {"svg": drawing(mol)}


def chem_similarity(a):
    from rdkit import Chem, DataStructs
    from rdkit.Chem import rdFingerprintGenerator

    first, second = molecule_of(a.get("first"), "first"), molecule_of(a.get("second"), "second")
    generator = rdFingerprintGenerator.GetMorganGenerator(radius=2, fpSize=2048)
    tanimoto = DataStructs.TanimotoSimilarity(generator.GetFingerprint(first), generator.GetFingerprint(second))
    same = Chem.MolToInchiKey(first) == Chem.MolToInchiKey(second)
    return {
        "tanimoto": round(tanimoto, 4),
        "same_molecule": same,
        "detail": "The same molecule (same InChIKey)." if same else f"Tanimoto similarity of their Morgan fingerprints (radius 2): {tanimoto:.2f} (1 is identical, under 0.4 unlike).",
        "first": Chem.MolToSmiles(first),
        "second": Chem.MolToSmiles(second),
    }


FORMULA_TOKEN = re.compile(r"([A-Z][a-z]?|\(|\)|\[|\]|\d+)")


@functools.cache
def elements():
    from rdkit import Chem

    table = Chem.GetPeriodicTable()
    return frozenset(table.GetElementSymbol(n) for n in range(1, 119))


def parse_formula(text):
    """A formula's atoms and charge: CuSO4·5H2O, Ca(OH)2, [Fe(CN)6]^4-, SO4^2-, Fe2+, e-."""
    raw = text.strip()
    if raw in ("e", "e-", "e^-"):
        return {}, -1
    charge = 0
    # The charge at the end: Fe^2+, SO4^2-, SO4 2-, {2-} as written; bare digits before the sign are the charge only of a
    # one-element ion (Fe2+, Al3+) or a bracketed complex ([Fe(CN)6]4-), else the last atom's count (MnO4-, NH4+); Fe+3 too.
    explicit = re.search(r"(?:\^\{?|\{|\s+)(\d*)([+-])\}?$", raw)
    bare = re.search(r"(\d+)([+-])$", raw)
    after = re.search(r"([+-])(\d*)$", raw)
    if explicit:
        charge = (int(explicit.group(1)) if explicit.group(1) else 1) * (1 if explicit.group(2) == "+" else -1)
        raw = raw[: explicit.start()]
    elif bare and (re.fullmatch(r"[A-Z][a-z]?", raw[: bare.start()]) or raw[: bare.start()].endswith("]")):
        charge = int(bare.group(1)) * (1 if bare.group(2) == "+" else -1)
        raw = raw[: bare.start()]
    elif after:
        charge = (int(after.group(2)) if after.group(2) else 1) * (1 if after.group(1) == "+" else -1)
        raw = raw[: after.start()]
    raw = raw.replace("(s)", "").replace("(l)", "").replace("(g)", "").replace("(aq)", "")
    total = {}
    for part in re.split(r"[·•*.]", raw):
        part = part.strip()
        if not part:
            continue
        lead = re.match(r"^(\d+)", part)
        times = int(lead.group(1)) if lead else 1
        part = part[lead.end():] if lead else part
        if re.sub(r"[A-Za-z0-9()\[\]]", "", part):
            raise LabError(f"{text!r} is not a formula this tool reads: write it as CuSO4·5H2O, Ca(OH)2 or [Fe(CN)6]4-.")
        stack = [{}]
        tokens = FORMULA_TOKEN.findall(part)
        if "".join(tokens) != part:
            raise LabError(f"{text!r} is not a formula this tool reads.")
        i = 0
        while i < len(tokens):
            t = tokens[i]
            count = int(tokens[i + 1]) if i + 1 < len(tokens) and tokens[i + 1].isdigit() else 1
            step = 2 if i + 1 < len(tokens) and tokens[i + 1].isdigit() else 1
            if t in "([":
                stack.append({})
                i += 1
                continue
            if t in ")]":
                if len(stack) == 1:
                    raise LabError(f"{text!r} closes a bracket it never opened.")
                inner = stack.pop()
                for k, v in inner.items():
                    stack[-1][k] = stack[-1].get(k, 0) + v * count
            elif t.isdigit():
                raise LabError(f"{text!r} has a number where an element should be.")
            elif t not in elements():
                raise LabError(f"{t} in {text!r} is not an element.")
            else:
                stack[-1][t] = stack[-1].get(t, 0) + count
            i += step
        if len(stack) != 1:
            raise LabError(f"{text!r} opens a bracket it never closes.")
        for k, v in stack[0].items():
            total[k] = total.get(k, 0) + v * times
    if not total:
        raise LabError(f"{text!r} has no elements.")
    return total, charge


def chem_formula(a):
    from rdkit import Chem

    text = a.get("formula")
    if not isinstance(text, str) or not text.strip() or len(text) > 200:
        raise LabError("Give formula as text, for example CuSO4·5H2O or Ca(OH)2.")
    atoms, charge = parse_formula(text)
    table = Chem.GetPeriodicTable()
    parts = [{"element": k, "count": v, "mass": round(table.GetAtomicWeight(k) * v, 4)} for k, v in atoms.items()]
    total = sum(p["mass"] for p in parts)
    for p in parts:
        p["share"] = f"{100 * p['mass'] / total:.2f}%"
    return {"formula": text.strip(), "charge": charge, "molar_mass": f"{total:.4f} g/mol", "elements": parts,
            "detail": "Standard atomic weights (IUPAC, as RDKit has them); the share of the mass each element has."}


def chem_balance(a):
    text = a.get("equation")
    if not isinstance(text, str) or not text.strip() or len(text) > 500:
        raise LabError("Give equation as text, for example Fe + O2 -> Fe2O3.")
    sides = re.split(r"\s*(?:<=>|<->|->|→|⇌|=)\s*", text.strip())
    if len(sides) != 2:
        raise LabError("Write the reaction with one arrow: reactants -> products (or =).")

    def species(side):
        out = []
        for s in re.split(r"\s+\+\s+", side.strip()):
            s = re.sub(r"^\d+\s*", "", s.strip())
            if s:
                out.append(s)
        if not out:
            raise LabError("Each side needs at least one species.")
        return out

    left, right = species(sides[0]), species(sides[1])
    parsed = [parse_formula(s) for s in left + right]
    elements = sorted({e for atoms, _ in parsed for e in atoms})
    rows = [[(1 if j < len(left) else -1) * atoms.get(e, 0) for j, (atoms, _) in enumerate(parsed)] for e in elements]
    if any(c for _, c in parsed):
        rows.append([(1 if j < len(left) else -1) * c for j, (_, c) in enumerate(parsed)])
    space = sp.Matrix(rows).nullspace()
    if not space:
        raise LabError("No coefficients balance it: check the species (an element on one side only, a wrong formula).")
    if len(space) > 1:
        raise LabError(f"It balances in {len(space)} independent ways (several reactions together): give it as separate reactions.")
    v = space[0]
    v = v * sp.ilcm(*[x.q for x in v])
    if all(x <= 0 for x in v):
        v = -v
    if any(x <= 0 for x in v):
        raise LabError("The only balance needs a species on the other side: check which are reactants and which products.")
    g = sp.igcd(*[int(x) for x in v])
    coefficients = [int(x) // g for x in v]

    def side_text(names, cs):
        return " + ".join(f"{c} {n}" if c != 1 else n for n, c in zip(names, cs))

    balanced = side_text(left, coefficients[: len(left)]) + " -> " + side_text(right, coefficients[len(left):])
    counts = []
    for e in elements:
        l = sum(coefficients[j] * parsed[j][0].get(e, 0) for j in range(len(left)))
        r = sum(coefficients[j] * parsed[j][0].get(e, 0) for j in range(len(left), len(parsed)))
        counts.append({"element": e, "left": l, "right": r})
    charge = [sum(coefficients[j] * parsed[j][1] for j in range(len(left))), sum(coefficients[j] * parsed[j][1] for j in range(len(left), len(parsed)))]
    checked = all(c["left"] == c["right"] for c in counts) and charge[0] == charge[1]
    return {"balanced": balanced, "coefficients": coefficients, "atoms": counts, "charge": {"left": charge[0], "right": charge[1]}, "checked": checked,
            "detail": "Each element counted on both sides" + (" and the charge" if any(c for _, c in parsed) else "") + ": " + ("equal." if checked else "NOT equal.")}


def bio_sequence(a):
    from Bio.Seq import Seq
    from Bio.SeqUtils import gc_fraction, molecular_weight
    from Bio.SeqUtils.ProtParam import ProteinAnalysis

    text = a.get("sequence")
    if not isinstance(text, str) or not text.strip():
        raise LabError("Give sequence as text, for example ATGGCC...")
    text = re.sub(r"\s+|\d+", "", text.split("\n", 1)[1] if text.lstrip().startswith(">") else text).upper()
    if len(text) > MAX_SEQUENCE:
        raise LabError(f"sequence is longer than {MAX_SEQUENCE} letters.")
    kind = a.get("kind") or ("rna" if set(text) <= set("ACGUN") and "U" in text else "dna" if set(text) <= set("ACGTN") else "protein")
    op = a.get("operation") or "info"
    letters = {"dna": "ACGTNRYKMSWBDHV", "rna": "ACGUNRYKMSWBDHV", "protein": "ACDEFGHIKLMNPQRSTVWYXBZJUO*"}.get(kind)
    if letters is None:
        raise LabError("kind is dna, rna or protein.")
    wrong = sorted(set(text) - set(letters))
    if wrong:
        raise LabError(f"A {kind} sequence has no {', '.join(wrong)}.")
    seq = Seq(text)
    result = {"kind": kind, "length": len(text), "operation": op}
    if op == "info":
        if kind == "protein":
            clean = text.replace("*", "")
            analysed = ProteinAnalysis(clean)
            result.update({"molecular_weight": f"{analysed.molecular_weight():.2f} Da", "isoelectric_point": round(analysed.isoelectric_point(), 2),
                           "aromaticity": round(analysed.aromaticity(), 4), "instability_index": round(analysed.instability_index(), 2),
                           "gravy": round(analysed.gravy(), 3)})
        else:
            result.update({"gc_content": f"{100 * gc_fraction(seq):.2f}%", "molecular_weight": f"{molecular_weight(seq, seq_type=kind.upper()):.2f} Da (single strand)"})
    elif op == "reverse_complement" and kind != "protein":
        result["result"] = str(seq.reverse_complement_rna() if kind == "rna" else seq.reverse_complement())
    elif op == "transcribe" and kind == "dna":
        result["result"] = str(seq.transcribe())
    elif op == "translate" and kind != "protein":
        table = int(a.get("table") or 1)
        usable = len(text) - len(text) % 3
        result["result"] = str(seq[:usable].translate(table=table, to_stop=bool(a.get("to_stop"))))
        result["table"] = table
        if usable != len(text):
            result["note"] = f"The last {len(text) - usable} letters are not a whole codon: left out."
    else:
        raise LabError(f"operation is info, reverse_complement, transcribe (DNA) or translate (DNA or RNA); {op} does not fit a {kind} sequence.")
    return result


def bio_align(a):
    from Bio import Align
    from Bio.Align import substitution_matrices

    first, second = (re.sub(r"\s+", "", str(a.get(k) or "")).upper() for k in ("first", "second"))
    if not first or not second:
        raise LabError("Give first and second as sequences.")
    if len(first) * len(second) > 25_000_000:
        raise LabError("The sequences are too long to align here: up to about 5000 letters each.")
    aligner = Align.PairwiseAligner()
    aligner.mode = "local" if a.get("mode") == "local" else "global"
    protein = not (set(first + second) <= set("ACGTUN"))
    if protein:
        aligner.substitution_matrix = substitution_matrices.load("BLOSUM62")
        aligner.open_gap_score, aligner.extend_gap_score = -10, -0.5
    else:
        aligner.match_score, aligner.mismatch_score, aligner.open_gap_score, aligner.extend_gap_score = 2, -1, -2, -0.5
    best = aligner.align(first, second)[0]
    lines = str(best).rstrip("\n").split("\n")
    identical = sum(1 for x, y in zip(best[0], best[1]) if x == y and x != "-")
    columns = best.shape[1]
    return {"mode": aligner.mode, "scoring": "BLOSUM62, gaps -10/-0.5" if protein else "match 2, mismatch -1, gaps -2/-0.5", "score": best.score,
            "identity": f"{100 * identical / columns:.1f}% ({identical}/{columns})", "alignment": "\n".join(lines[:60])}


OPS = {"math_compute": compute, "math_check": check, "math_solve": solve, "units_convert": units,
       "chem_molecule": chem_molecule, "chem_similarity": chem_similarity, "chem_formula": chem_formula, "chem_balance": chem_balance,
       "bio_sequence": bio_sequence, "bio_align": bio_align}


def main():
    with open("lab-call.json", encoding="utf-8") as f:
        call = json.load(f)
    op = OPS.get(call.get("op"))
    try:
        if op is None:
            raise LabError(f"There is no lab tool {call.get('op')}.")
        out = op(call.get("args") or {})
        result, show = out if isinstance(out, tuple) else (out, None)
        print(json.dumps({"ok": True, "result": result, **({"show": show} if show else {})}, default=str))
    except LabError as e:
        print(json.dumps({"ok": False, "error": str(e)}))
    except Exception as e:  # SymPy's own failure: said, not raised
        print(json.dumps({"ok": False, "error": f"{type(e).__name__}: {e}"}))


if __name__ == "__main__":
    sys.exit(main())
