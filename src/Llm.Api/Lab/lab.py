"""The science lab's tools, run in the sandbox: exact mathematics with SymPy, units with Pint, molecules, formulas and
reactions with RDKit, sequences with Biopython, and quantum computing on simulators: circuits with Qiskit and Aer, error
correction with Stim and PyMatching, open systems with QuTiP, variational circuits with PennyLane, molecules' energies
with PySCF and their qubit Hamiltonians with OpenFermion.

Reads its call from lab-call.json beside it ({"op": ..., "args": {...}}) and prints one JSON object: what the tool
returns, and what the page shows beside it (a molecule's or a circuit's drawing, a chart: "show", not read by the
model). The expressions are
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
    return result, {"figures": [{"svg": drawing(mol), "label": f"{result['formula']}, drawn"}]}


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


# Quantum computing, on simulators (the sandbox has no network). Pauli strings are read left to right: the first letter
# acts on qubit 0. A circuit's bitstrings are Qiskit's: qubit 0 is the rightmost bit.

MAX_QUBITS, MAX_SHOTS, SHOWN_STATE, EXACT_QUBITS = 24, 100_000, 6, 12
# The app's chart colours, in their order (src/web/src/components/charts/palette.ts, light: a drawing is on white).
SERIES = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100"]
INK, MUTED, GRID = "#1f2937", "#6b7280", "#e5e7eb"
PAULI_TERM = re.compile(r"\s*([+-])?\s*(?:(\d+(?:\.\d*)?(?:[eE][+-]?\d+)?|\.\d+(?:[eE][+-]?\d+)?)\s*\*?\s*)?([IXYZ]+)\s*")


def chart(width=6.0, height=3.0):
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    plt.rcParams.update({"svg.fonttype": "none", "font.size": 9, "text.color": INK, "axes.labelcolor": INK, "axes.edgecolor": MUTED,
                         "xtick.color": MUTED, "ytick.color": MUTED, "axes.spines.top": False, "axes.spines.right": False, "legend.frameon": False})
    fig, ax = plt.subplots(figsize=(width, height))
    ax.grid(axis="y", color=GRID, linewidth=0.6)
    ax.set_axisbelow(True)
    return fig, ax


def svg_of(fig):
    import io

    import matplotlib.pyplot as plt

    out = io.StringIO()
    fig.savefig(out, format="svg", bbox_inches="tight", transparent=True)
    plt.close(fig)
    return out.getvalue()


def whole(a, name, default, lo, hi):
    value = a.get(name, default)
    if value is None:
        value = default
    if isinstance(value, bool) or not isinstance(value, (int, float)) or int(value) != value or not lo <= value <= hi:
        raise LabError(f"{name} is a whole number from {lo} to {hi}.")
    return int(value)


def real(a, name, default, lo, hi):
    value = a.get(name, default)
    if value is None:
        value = default
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not lo <= value <= hi:
        raise LabError(f"{name} is a number from {lo} to {hi}.")
    return float(value)


def circuit_of(text, name="qasm"):
    """A circuit from OpenQASM 2 (qelib1's gates; the header may be left out) or OpenQASM 3."""
    if not isinstance(text, str) or not text.strip():
        raise LabError(f'Give {name} as OpenQASM, for example: qreg q[2]; creg c[2]; h q[0]; cx q[0],q[1]; measure q -> c;')
    if len(text) > 20_000:
        raise LabError(f"{name} is longer than 20000 characters.")
    from qiskit import qasm2, qasm3

    try:
        if re.search(r"^\s*OPENQASM\s+3", text, re.M):
            circuit = qasm3.loads_experimental(text)
        else:
            if not re.search(r"^\s*OPENQASM\s+2", text, re.M):
                text = "OPENQASM 2.0;\n" + text
            if "qelib1.inc" not in text:
                text = re.sub(r"(OPENQASM\s+2(?:\.0)?\s*;)", r'\1\ninclude "qelib1.inc";', text, count=1)
            circuit = qasm2.loads(text, custom_instructions=qasm2.LEGACY_CUSTOM_INSTRUCTIONS)
    except Exception as e:  # the parser says where
        raise LabError(f"{name} could not be read as OpenQASM: {e}") from None
    if circuit.num_qubits > MAX_QUBITS:
        raise LabError(f"{name} has {circuit.num_qubits} qubits: at most {MAX_QUBITS} are simulated here.")
    return circuit


def unmeasured(circuit):
    """The circuit without its final measurements, or None when it measures or resets before its end (no single state)."""
    bare = circuit.remove_final_measurements(inplace=False)
    if any(i.operation.name in ("measure", "reset") for i in bare.data):
        return None
    return bare


def dirac(state, digits=4):
    terms = []
    for index, amp in enumerate(state.data):
        if abs(amp) > 10 ** -digits:
            re_, im = round(amp.real, digits), round(amp.imag, digits)
            value = f"{re_:g}" if im == 0 else f"{im:g}i" if re_ == 0 else f"({re_:g}{im:+g}i)"
            terms.append(f"{value}|{index:0{state.num_qubits}b}⟩")
    return " + ".join(terms).replace("+ -", "- ") or "0"


def noise_model(noise):
    if not noise:
        return None
    if not isinstance(noise, dict):
        raise LabError("noise is an object: {\"depolarizing\": 0.001, \"two_qubit\": 0.01, \"readout\": 0.02}.")
    from qiskit_aer.noise import NoiseModel, ReadoutError, depolarizing_error

    model = NoiseModel()
    one = real(noise, "depolarizing", 0, 0, 0.5)
    two = real(noise, "two_qubit", one * 10 if one else 0, 0, 0.5)
    readout = real(noise, "readout", 0, 0, 0.5)
    if one:
        model.add_all_qubit_quantum_error(depolarizing_error(one, 1), ["u", "u1", "u2", "u3", "p", "rx", "ry", "rz", "x", "y", "z", "h", "s", "sdg", "t", "tdg", "sx", "sxdg"])
    if two:
        model.add_all_qubit_quantum_error(depolarizing_error(two, 2), ["cx", "cy", "cz", "ch", "swap", "crx", "cry", "crz", "cp", "cu", "ecr", "rzz"])
    if readout:
        model.add_all_qubit_readout_error(ReadoutError([[1 - readout, readout], [readout, 1 - readout]]))
    return model


def histogram(counts, shots, ideal):
    keys = sorted(counts, key=lambda k: -counts[k])[:16]
    keys.sort()
    fig, ax = chart(max(3.5, min(9.0, 0.5 * len(keys) + 2)), 2.8)
    xs = range(len(keys))
    ax.bar(xs, [counts[k] / shots for k in keys], width=0.6, color=SERIES[0], label="measured", zorder=2)
    if ideal:
        exact = [(x, ideal[k]) for x, k in zip(xs, keys) if k in ideal]
        ax.scatter([x for x, _ in exact], [v for _, v in exact], marker="_", s=260, linewidths=2, color=SERIES[1], label="exact", zorder=3)
        ax.legend(loc="lower center", bbox_to_anchor=(0.5, 1.0), ncols=2, fontsize=8)
    ax.set_xticks(list(xs), keys, rotation=45 if len(keys) > 8 or len(keys[0]) > 4 else 0, family="monospace")
    ax.set_ylabel("probability")
    ax.set_ylim(0, max(1e-9, max([counts[k] / shots for k in keys] + [ideal.get(k, 0) for k in keys] if ideal else [counts[k] / shots for k in keys])) * 1.15)
    return svg_of(fig)


def quantum_run(a):
    from qiskit import qasm2, transpile
    from qiskit.quantum_info import Statevector
    from qiskit.transpiler import CouplingMap
    from qiskit_aer import AerSimulator

    circuit = circuit_of(a.get("qasm"))
    shots = whole(a, "shots", 1024, 1, MAX_SHOTS)
    seed = whole(a, "seed", 7, 0, 2**31 - 1)
    result = {"qubits": circuit.num_qubits, "clbits": circuit.num_clbits, "depth": circuit.depth(), "gates": dict(circuit.count_ops()), "seed": seed}
    bare, ideal = unmeasured(circuit), None
    if bare is not None and circuit.num_qubits <= EXACT_QUBITS:
        state = Statevector(bare)
        ideal = {k: round(float(v), 6) for k, v in state.probabilities_dict().items() if v > 1e-12}
        result["exact_probabilities"] = dict(sorted(ideal.items(), key=lambda kv: -kv[1])[:32])
        if circuit.num_qubits <= SHOWN_STATE:
            result["statevector"] = dirac(state)
    measured = circuit if any(i.operation.name == "measure" for i in circuit.data) else circuit.measure_all(inplace=False)
    model = noise_model(a.get("noise"))
    simulator = AerSimulator(noise_model=model, seed_simulator=seed) if model else AerSimulator(seed_simulator=seed)
    counts = simulator.run(transpile(measured, simulator, seed_transpiler=seed), shots=shots).result().get_counts()
    if isinstance(counts, list):
        counts = counts[0]
    counts = {k.replace(" ", ""): v for k, v in counts.items()}
    result["shots"] = shots
    result["counts"] = dict(sorted(counts.items(), key=lambda kv: -kv[1])[:64])
    if len(counts) > 64:
        result["outcomes"] = f"{len(counts)} outcomes; the 64 most frequent shown"
    result["noise"] = "none (ideal)" if not model else ", ".join(f"{k} {v}" for k, v in a["noise"].items())
    basis = a.get("basis")
    if basis:
        if not isinstance(basis, list) or not all(isinstance(g, str) and re.fullmatch(r"[a-z][a-z0-9_]{0,15}", g) for g in basis) or len(basis) > 20:
            raise LabError('basis is a list of gate names, e.g. ["rz", "sx", "x", "cx"] (IBM\'s) or ["rz", "rx", "cz"].')
        coupling = CouplingMap.from_line(circuit.num_qubits) if a.get("coupling") == "line" and circuit.num_qubits > 1 else None
        try:
            compiled = transpile(bare if bare is not None else circuit, basis_gates=basis, coupling_map=coupling,
                                 optimization_level=whole(a, "optimization", 2, 0, 3), seed_transpiler=seed)
        except Exception as e:
            raise LabError(f"It does not compile to {basis}: {e}") from None
        try:
            text = qasm2.dumps(compiled)
        except Exception:  # a gate OpenQASM 2 has no name for
            text = None
        result["compiled"] = {"basis": basis, "coupling": "a line of qubits" if coupling else "all to all", "depth": compiled.depth(),
                              "gates": dict(compiled.count_ops()), **({"qasm": text[:8000]} if text else {})}
    drawn = circuit.draw("text", fold=110).single_string()
    figures = [{"svg": histogram(counts, shots, ideal), "label": f"{shots} shots{', with noise' if model else ''}"}]
    return result, {"diagram": drawn[:12_000], "figures": figures}


def amplitude(x):
    if isinstance(x, (int, float)) and not isinstance(x, bool):
        return complex(x)
    if not isinstance(x, str):
        raise LabError('An amplitude is a number or text such as "1/sqrt(2)", "-i/2" or "0.5+0.5i".')
    value = parse(re.sub(r"(?<![A-Za-z])[ij](?![A-Za-z])", "I", x), "an amplitude")
    try:
        return complex(sp.N(value))
    except TypeError:
        raise LabError(f"{x!r} is not a number.") from None


def quantum_check(a):
    import numpy as np
    from qiskit.quantum_info import Operator, Statevector, process_fidelity, state_fidelity

    first = circuit_of(a.get("first"), "first")
    bare = unmeasured(first)
    if bare is None:
        raise LabError("first measures or resets before its end: it has no single state or unitary to check.")
    if a.get("second"):
        second = unmeasured(circuit_of(a.get("second"), "second"))
        if second is None:
            raise LabError("second measures or resets before its end: it has no unitary to compare.")
        if second.num_qubits != bare.num_qubits:
            return {"checked": False, "verdict": "different", "detail": f"They act on different numbers of qubits ({bare.num_qubits} and {second.num_qubits})."}
        if bare.num_qubits > 10:
            raise LabError("Unitaries are compared up to 10 qubits here; compare their states (state) instead.")
        u, v = Operator(bare), Operator(second)
        same = u.equiv(v, rtol=1e-9, atol=1e-9)
        fidelity = float(process_fidelity(u, v))
        return {"checked": bool(same), "verdict": "equal" if same else "different", "process_fidelity": round(fidelity, 12),
                "detail": ("The two unitaries are equal up to a global phase (to 1e-9)." if same else
                           f"The unitaries differ: process fidelity {fidelity:.6f} (1 when equal up to a global phase).")}
    if bare.num_qubits > EXACT_QUBITS:
        raise LabError(f"States are checked up to {EXACT_QUBITS} qubits here.")
    state = Statevector(bare)
    if a.get("state") is not None:
        claimed = a["state"]
        if not isinstance(claimed, list) or len(claimed) != 2 ** bare.num_qubits:
            raise LabError(f"state is the list of the {2 ** bare.num_qubits} amplitudes, |0…0⟩ first (qubit 0 is the rightmost bit).")
        vector = np.array([amplitude(x) for x in claimed])
        norm = float(np.linalg.norm(vector))
        if norm == 0:
            raise LabError("state is all zeros.")
        fidelity = float(state_fidelity(state, Statevector(vector / norm)))
        same = fidelity > 1 - 1e-9
        return {"checked": same, "verdict": "equal" if same else "different", "fidelity": round(fidelity, 12), "state": dirac(state),
                "detail": ("The circuit's state is the one claimed (up to a global phase)." if same else f"The circuit's state is not the one claimed: fidelity {fidelity:.6f}.")
                + ("" if abs(norm - 1) < 1e-6 else f" The claimed amplitudes were not normalised (norm {norm:.6f}): they were, to compare.")}
    if a.get("probabilities") is not None:
        claimed = a["probabilities"]
        if not isinstance(claimed, dict) or not all(isinstance(k, str) and re.fullmatch(r"[01]{%d}" % bare.num_qubits, k) for k in claimed):
            raise LabError(f'probabilities is an object of {bare.num_qubits}-bit strings, e.g. {{"00": 0.5, "11": 0.5}} (qubit 0 is the rightmost bit).')
        exact = state.probabilities_dict()
        keys = set(exact) | set(claimed)
        distance = 0.5 * sum(abs(float(exact.get(k, 0)) - float(amplitude(claimed.get(k, 0)).real)) for k in keys)
        same = distance < 1e-6
        return {"checked": same, "verdict": "equal" if same else "different", "total_variation_distance": round(distance, 9),
                "exact_probabilities": {k: round(float(v), 6) for k, v in sorted(exact.items(), key=lambda kv: -kv[1]) if v > 1e-12},
                "detail": "The circuit gives these probabilities." if same else f"The circuit's probabilities differ from those claimed by {distance:.6f} (total variation distance)."}
    raise LabError("Give second (a circuit to compare with first), state (the claimed amplitudes) or probabilities (the claimed outcome probabilities).")


def qec_sample(a):
    import math

    import numpy as np
    import pymatching
    import stim

    tasks = {"repetition": "repetition_code:memory", "surface": "surface_code:rotated_memory_z", "surface_x": "surface_code:rotated_memory_x"}
    code = str(a.get("code") or "surface").lower()
    if code not in tasks:
        raise LabError("code is repetition, surface (a rotated surface code, Z memory) or surface_x.")
    distances = a.get("distances") or [whole(a, "distance", 3, 2, 25)]
    errors = a.get("errors") or [real(a, "error", 0.001, 1e-6, 0.3)]
    if not isinstance(distances, list) or not 1 <= len(distances) <= 4 or not all(isinstance(d, int) and 2 <= d <= (25 if code == "repetition" else 11) for d in distances):
        raise LabError(f"distances is a list of 1 to 4 whole numbers from 2 to {25 if code == 'repetition' else 11}.")
    if not isinstance(errors, list) or not 1 <= len(errors) <= 8 or not all(isinstance(p, (int, float)) and 1e-6 <= p <= 0.3 for p in errors):
        raise LabError("errors is a list of 1 to 8 physical error rates from 0.000001 to 0.3.")
    shots = whole(a, "shots", 20_000, 100, 1_000_000)
    if shots * len(distances) * len(errors) > 4_000_000:
        raise LabError("At most 4,000,000 shots in all (shots × distances × errors): fewer shots, or fewer points.")
    seed = whole(a, "seed", 7, 0, 2**31 - 1)
    rows = []
    for d in sorted(set(distances)):
        rounds = whole(a, "rounds", d, 1, 50) if a.get("rounds") else d
        for p in sorted(set(errors)):
            circuit = stim.Circuit.generated(tasks[code], distance=d, rounds=rounds, after_clifford_depolarization=p, after_reset_flip_probability=p,
                                             before_measure_flip_probability=p, before_round_data_depolarization=p)
            matching = pymatching.Matching.from_detector_error_model(circuit.detector_error_model(decompose_errors=True))
            detections, observed = circuit.compile_detector_sampler(seed=seed).sample(shots, separate_observables=True)
            failures = int(np.sum(np.any(matching.decode_batch(detections) != observed, axis=1)))
            rate = failures / shots
            rows.append({"distance": d, "rounds": rounds, "physical": p, "logical": rate, "plus_minus": round(math.sqrt(max(rate * (1 - rate), 1 / shots) / shots), 8),
                         "failures": failures, "shots": shots, "qubits": circuit.num_qubits, "detectors": circuit.num_detectors})
    helps = []
    for p in sorted(set(errors)):
        at = [r for r in rows if r["physical"] == p]
        if len(at) > 1:
            better = all(b["logical"] <= a_["logical"] for a_, b in zip(at, at[1:]))
            helps.append(f"p = {p:g}: a larger distance {'lowers' if better else 'does not lower'} the logical error rate")
    result = {"code": code, "decoder": "minimum-weight perfect matching (PyMatching)", "noise": "circuit-level depolarizing noise at p on every gate, reset and measurement",
              "results": rows, "detail": "The logical error rate is per shot: the whole memory experiment of its rounds failing. " +
              ("; ".join(helps) + " (below the threshold a larger distance helps)." if helps else "Sample several distances and errors to see the threshold.")}
    fig, ax = chart(5.6, 3.4)
    for i, d in enumerate(sorted(set(distances))):
        at = [r for r in rows if r["distance"] == d]
        xs, ys = [r["physical"] for r in at], [max(r["logical"], 0.5 / shots) for r in at]
        ax.plot(xs, ys, marker="o", markersize=4, linewidth=2, color=SERIES[i], label=f"d = {d}")
    if len(set(errors)) > 1:
        ps = sorted(set(errors))
        ax.plot(ps, ps, linestyle="--", linewidth=1, color=MUTED, label="logical = physical")
        ax.set_xscale("log")
    ax.set_yscale("log")
    ax.set_xlabel("physical error rate")
    ax.set_ylabel("logical error rate")
    ax.legend(fontsize=8)
    return result, {"figures": [{"svg": svg_of(fig), "label": f"{code} code, {shots} shots a point"}]}


def pauli_terms(text, name="hamiltonian", qubits=None):
    """'0.5*X + Z' or '-1.05 II + 0.39 ZI + 0.18 XX': [(coefficient, letters)], every string as long."""
    if not isinstance(text, str) or not text.strip() or len(text) > 4000:
        raise LabError(f"Give {name} as Pauli strings with coefficients, for example 0.5*XX + 0.5*YY + ZI (the first letter acts on qubit 0).")
    terms, at = [], 0
    while at < len(text):
        m = PAULI_TERM.match(text, at)
        if not m or m.end() == at or (terms and not m.group(1)):
            raise LabError(f"{name} could not be read at {text[at:at + 20]!r}: write terms such as 0.5*XZ joined by + or -.")
        terms.append(((-1 if m.group(1) == "-" else 1) * float(m.group(2) or 1), m.group(3)))
        at = m.end()
    lengths = {len(s) for _, s in terms}
    if len(lengths) != 1:
        raise LabError(f"Every Pauli string of {name} has as many letters as there are qubits: {sorted(lengths)} here.")
    if qubits and lengths != {qubits}:
        raise LabError(f"{name} acts on {lengths.pop()} qubits; {qubits} are wanted.")
    return terms


def qubit_dynamics(a):
    import numpy as np
    import qutip as qt

    terms = pauli_terms(a.get("hamiltonian"))
    n = len(terms[0][1])
    if n > 4:
        raise LabError("qubit_dynamics follows up to 4 qubits.")
    paulis = {"I": qt.qeye(2), "X": qt.sigmax(), "Y": qt.sigmay(), "Z": qt.sigmaz()}

    def on(op, k):
        return qt.tensor([op if j == k else qt.qeye(2) for j in range(n)])

    hamiltonian = sum(c * qt.tensor([paulis[ch] for ch in s]) for c, s in terms)
    initial = str(a.get("initial") or "0" * n)
    kets = {"0": qt.basis(2, 0), "1": qt.basis(2, 1), "+": (qt.basis(2, 0) + qt.basis(2, 1)).unit(), "-": (qt.basis(2, 0) - qt.basis(2, 1)).unit(),
            "r": (qt.basis(2, 0) + 1j * qt.basis(2, 1)).unit(), "l": (qt.basis(2, 0) - 1j * qt.basis(2, 1)).unit()}
    if len(initial) != n or any(c not in kets for c in initial):
        raise LabError(f"initial is one letter a qubit ({n} here): 0, 1, + or - (X), r or l (Y's +i and -i), e.g. {'0' * n}.")
    state = qt.tensor([kets[c] for c in initial])
    t1 = real(a, "t1", 0, 0, 1e9) or None
    t2 = real(a, "t2", 0, 0, 1e9) or None
    collapse = []
    if t1:
        collapse += [np.sqrt(1 / t1) * on(qt.destroy(2), k) for k in range(n)]
    if t2:
        dephasing = 1 / t2 - (1 / (2 * t1) if t1 else 0)
        if dephasing < -1e-12:
            raise LabError("T2 is at most 2 T1.")
        if dephasing > 0:
            collapse += [np.sqrt(dephasing / 2) * on(qt.sigmaz(), k) for k in range(n)]
    time = real(a, "time", 10, 1e-9, 1e6)
    steps = whole(a, "steps", 201, 11, 2001)
    times = np.linspace(0, time, steps)
    observables = [on(op, k) for k in range(n) for op in (qt.sigmax(), qt.sigmay(), qt.sigmaz())]
    run = qt.mesolve(hamiltonian, state, times, collapse, e_ops=observables, options={"store_final_state": True})
    expect = np.array(run.expect)
    final = run.final_state if run.final_state.isoper else qt.ket2dm(run.final_state)
    sample = np.linspace(0, steps - 1, 11).astype(int)
    result = {"qubits": n, "units": "ħ = 1: time is in units of 1/energy (T1, T2 and time alike)", "solver": "QuTiP's Lindblad master equation (mesolve)" if collapse else "QuTiP, closed (Schrödinger) evolution",
              "bloch": {f"qubit {k}": [round(float(expect[3 * k + j][-1]), 6) for j in range(3)] for k in range(n)},
              "purity": round(float((final * final).tr().real), 6),
              "qubit_0": [{"t": round(float(times[i]), 6), "x": round(float(expect[0][i]), 5), "y": round(float(expect[1][i]), 5), "z": round(float(expect[2][i]), 5)} for i in sample],
              "detail": "Each qubit's Bloch vector (⟨X⟩, ⟨Y⟩, ⟨Z⟩) at the end; the purity Tr ρ² (1 for a pure state)."}
    fig, ax = chart(6.0, 3.0)
    for j, label in enumerate(("⟨X⟩", "⟨Y⟩", "⟨Z⟩")):
        ax.plot(times, expect[j], linewidth=2, color=SERIES[j], label=label)
    ax.set_ylim(-1.05, 1.05)
    ax.set_xlabel("time (1/energy)")
    ax.set_ylabel("qubit 0")
    ax.legend(loc="upper right", fontsize=8, ncols=3)
    return result, {"figures": [{"svg": svg_of(fig), "label": "Qubit 0's Bloch vector over time"}]}


def pennylane_hamiltonian(terms, n):
    import pennylane as qml

    letters = {"X": qml.PauliX, "Y": qml.PauliY, "Z": qml.PauliZ}
    ops = []
    for _, s in terms:
        factors = [letters[ch](k) for k, ch in enumerate(s) if ch != "I"]
        ops.append(qml.prod(*factors) if len(factors) > 1 else factors[0] if factors else qml.Identity(0))
    return qml.Hamiltonian([c for c, _ in terms], ops)


def convergence(history, exact, title, unit=""):
    fig, ax = chart(5.6, 3.0)
    ax.plot(range(1, len(history) + 1), history, linewidth=2, color=SERIES[0], label="variational energy")
    if exact is not None:
        ax.axhline(exact, linestyle="--", linewidth=1, color=MUTED, label="exact")
    ax.set_xlabel("step")
    ax.set_ylabel(f"energy{f' ({unit})' if unit else ''}")
    ax.legend(fontsize=8)
    return {"svg": svg_of(fig), "label": title}


def minimise(cost, params, steps, rate):
    import pennylane as qml

    optimiser, history = qml.AdamOptimizer(rate), []
    for _ in range(steps):
        params, energy = optimiser.step_and_cost(cost, params)
        history.append(float(energy))
    return params, history


def quantum_variational(a):
    import itertools

    import numpy as np
    import pennylane as qml
    from pennylane import numpy as pnp

    problem = str(a.get("problem") or "vqe").lower()
    steps, layers = whole(a, "steps", 120, 1, 500), whole(a, "layers", 2, 1, 8)
    rate, seed = real(a, "step_size", 0.1, 1e-4, 2), whole(a, "seed", 7, 0, 2**31 - 1)
    rng = np.random.default_rng(seed)
    if problem == "qaoa":
        import networkx as nx

        edges = a.get("edges")
        if not isinstance(edges, list) or not 1 <= len(edges) <= 60 or not all(isinstance(e, list) and len(e) == 2 and all(isinstance(v, int) and 0 <= v < 16 for v in e) and e[0] != e[1] for e in edges):
            raise LabError("edges is a list of pairs of vertices, e.g. [[0, 1], [1, 2], [2, 0]] (vertices 0 to 15).")
        graph = nx.Graph([tuple(e) for e in edges])
        graph.add_nodes_from(range(max(max(e) for e in edges) + 1))
        n = graph.number_of_nodes()
        if n > 12:
            raise LabError("QAOA is simulated up to 12 vertices here.")
        cost_h, mixer_h = qml.qaoa.maxcut(graph)
        device = qml.device("default.qubit", wires=n)

        @qml.qnode(device)
        def cost(params):
            for w in range(n):
                qml.Hadamard(w)
            for layer in range(layers):
                qml.qaoa.cost_layer(params[0][layer], cost_h)
                qml.qaoa.mixer_layer(params[1][layer], mixer_h)
            return qml.expval(cost_h)

        @qml.qnode(device)
        def probabilities(params):
            for w in range(n):
                qml.Hadamard(w)
            for layer in range(layers):
                qml.qaoa.cost_layer(params[0][layer], cost_h)
                qml.qaoa.mixer_layer(params[1][layer], mixer_h)
            return qml.probs(wires=range(n))

        params = pnp.array(rng.uniform(0, 1, (2, layers)), requires_grad=True)
        params, history = minimise(cost, params, steps, rate)
        probs = np.asarray(probabilities(params))
        best = format(int(np.argmax(probs)), f"0{n}b")

        def cut(bits):
            return sum(1 for u, v in graph.edges if bits[u] != bits[v])
        found = cut(best)
        maximum = max(cut("".join(b)) for b in itertools.product("01", repeat=n))
        return ({"problem": "MaxCut by QAOA", "vertices": n, "edges": graph.number_of_edges(), "layers": layers, "steps": steps, "energy": round(history[-1], 8),
                 "best_bitstring": best, "probability": round(float(probs.max()), 6), "cut": found, "maximum_cut": maximum, "checked": found == maximum,
                 "detail": f"The likeliest bitstring (wire 0 first) cuts {found} edges; the largest cut, by trying all {2 ** n}, is {maximum}."},
                {"figures": [convergence(history, -maximum, "QAOA's cost by step (the exact one is minus the largest cut)")]})
    terms = pauli_terms(a.get("hamiltonian"))
    n = len(terms[0][1])
    if n > 10:
        raise LabError("VQE is simulated up to 10 qubits here.")
    hamiltonian = pennylane_hamiltonian(terms, n)
    exact = float(np.linalg.eigvalsh(qml.matrix(hamiltonian, wire_order=range(n)))[0])
    device = qml.device("default.qubit", wires=n)

    @qml.qnode(device)
    def cost(weights):
        qml.StronglyEntanglingLayers(weights, wires=range(n))
        return qml.expval(hamiltonian)

    weights = pnp.array(rng.uniform(0, 2 * np.pi, qml.StronglyEntanglingLayers.shape(n_layers=layers, n_wires=n)), requires_grad=True)
    weights, history = minimise(cost, weights, steps, rate)
    gradient = float(np.linalg.norm(qml.grad(cost)(weights)))
    energy = float(cost(weights))
    return ({"problem": "VQE", "qubits": n, "ansatz": f"StronglyEntanglingLayers, {layers} layer(s)", "optimiser": f"Adam, step {rate}", "steps": steps,
             "energy": round(energy, 8), "exact": round(exact, 8), "error": round(energy - exact, 8), "gradient_norm": round(gradient, 8),
             "history": [round(history[i], 6) for i in np.linspace(0, len(history) - 1, min(11, len(history))).astype(int)],
             "detail": "The variational energy is never below the exact ground energy (the variational principle); the exact one is the Hamiltonian's lowest eigenvalue."},
            {"figures": [convergence(history, exact, "VQE's energy by step")]})


def quantum_chemistry(a):
    import numpy as np
    from pyscf import ao2mo, cc, fci, gto, scf

    atoms = a.get("atoms")
    if not isinstance(atoms, str) or not atoms.strip() or len(atoms) > 2000 or not re.fullmatch(r"[A-Za-z0-9.\s;:,+\-eE]+", atoms):
        raise LabError("atoms is each atom and its x y z in ångström, separated by ;, e.g. H 0 0 0; H 0 0 0.74.")
    basis = str(a.get("basis") or "sto-3g")
    if not re.fullmatch(r"[A-Za-z0-9*+()\-]{2,24}", basis):
        raise LabError("basis is a basis set's name, e.g. sto-3g, 6-31g, cc-pvdz.")
    charge, spin = whole(a, "charge", 0, -4, 4), whole(a, "spin", 0, 0, 8)
    try:
        mol = gto.M(atom=atoms, basis=basis, charge=charge, spin=spin, unit="Angstrom", verbose=0)
    except Exception as e:  # PySCF says what it could not read
        raise LabError(f"PySCF could not build that molecule: {e}") from None
    if mol.nao > 60:
        raise LabError(f"It has {mol.nao} orbitals in {basis}: at most 60 here (a smaller basis, or a smaller molecule).")
    mf = (scf.RHF if spin == 0 else scf.ROHF)(mol).run()
    result = {"atoms": mol.natm, "electrons": mol.nelectron, "orbitals": mol.nao, "basis": basis, "units": "hartree",
              "nuclear_repulsion": round(mol.energy_nuc(), 10), "hf": round(float(mf.e_tot), 10), "hf_converged": bool(mf.converged)}
    if spin == 0:
        coupled = cc.CCSD(mf).run()
        result["ccsd"] = round(float(coupled.e_tot), 10)
        result["ccsd_t"] = round(float(coupled.e_tot + coupled.ccsd_t()), 10)
    exact = None
    if mol.nao <= 10:
        exact = float(fci.FCI(mf).kernel()[0])
        result["fci"] = round(exact, 10)
    if 2 * mol.nao <= 16:
        from openfermion import InteractionOperator, get_fermion_operator, get_sparse_operator, jordan_wigner, jw_get_ground_state_at_particle_number
        from openfermion.chem.molecular_data import spinorb_from_spatial

        c = mf.mo_coeff
        norb = c.shape[1]
        one = c.T @ mf.get_hcore() @ c
        two = np.asarray(ao2mo.restore(1, ao2mo.kernel(mol, c), norb).transpose(0, 2, 3, 1), order="C")
        one_so, two_so = spinorb_from_spatial(one, two)
        qubit_h = jordan_wigner(get_fermion_operator(InteractionOperator(mol.energy_nuc(), one_so, 0.5 * two_so)))
        qubit_h.compress()
        ground = float(jw_get_ground_state_at_particle_number(get_sparse_operator(qubit_h, n_qubits=2 * norb), mol.nelectron)[0])
        result["qubit_hamiltonian"] = {"mapping": "Jordan–Wigner (OpenFermion), spin orbitals interleaved", "qubits": 2 * norb, "terms": len(qubit_h.terms),
                                       "ground_energy": round(ground, 10), **({"matches_fci": abs(ground - exact) < 1e-6} if exact is not None else {})}
        if a.get("vqe"):
            if 2 * norb > 8:
                raise LabError(f"VQE is run here up to 8 qubits; this molecule's Hamiltonian has {2 * norb}.")
            import pennylane as qml
            from pennylane import numpy as pnp

            letters = {"X": qml.PauliX, "Y": qml.PauliY, "Z": qml.PauliZ}
            coefficients, ops = [], []
            for term, value in qubit_h.terms.items():
                factors = [letters[p](q) for q, p in term]
                coefficients.append(float(np.real(value)))
                ops.append(qml.prod(*factors) if len(factors) > 1 else factors[0] if factors else qml.Identity(0))
            hamiltonian = qml.Hamiltonian(coefficients, ops)
            wires = 2 * norb
            hf = qml.qchem.hf_state(mol.nelectron, wires)
            singles, doubles = qml.qchem.excitations(mol.nelectron, wires)
            device = qml.device("default.qubit", wires=wires)

            @qml.qnode(device)
            def cost(theta):
                qml.AllSinglesDoubles(theta, wires=range(wires), hf_state=hf, singles=singles, doubles=doubles)
                return qml.expval(hamiltonian)

            steps = whole(a, "steps", 80, 1, 400)
            theta, history = minimise(cost, pnp.zeros(len(singles) + len(doubles), requires_grad=True), steps, real(a, "step_size", 0.05, 1e-4, 1))
            energy = float(cost(theta))
            result["vqe"] = {"ansatz": f"all single and double excitations from Hartree–Fock ({len(singles)} + {len(doubles)} parameters)", "steps": steps,
                             "energy": round(energy, 10), "exact": round(ground, 10), "error_mhartree": round(1000 * (energy - ground), 4),
                             "chemical_accuracy": abs(energy - ground) < 0.0016}
            result["detail"] = "Energies in hartree. FCI is exact in this basis; chemical accuracy is 1.6 mHa."
            return result, {"figures": [convergence(history, ground, "VQE's energy by step, against the exact one", "hartree")]}
    result["detail"] = "Energies in hartree. FCI is exact in this basis (when computed); the qubit Hamiltonian's ground energy at this electron count should equal it."
    return result


OPS = {"math_compute": compute, "math_check": check, "math_solve": solve, "units_convert": units,
       "chem_molecule": chem_molecule, "chem_similarity": chem_similarity, "chem_formula": chem_formula, "chem_balance": chem_balance,
       "bio_sequence": bio_sequence, "bio_align": bio_align,
       "quantum_run": quantum_run, "quantum_check": quantum_check, "qec_sample": qec_sample, "qubit_dynamics": qubit_dynamics,
       "quantum_variational": quantum_variational, "quantum_chemistry": quantum_chemistry}


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
