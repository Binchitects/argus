using System.Text.RegularExpressions;

namespace Llm.Api.Lab;

/// <summary>A science of the lab: what it is for, the tools a notebook of it has on, and what its model is told.</summary>
public sealed record LabField(string Id, string Title, string About, string[] Tools, string Instructions);

/// <summary>
/// The sciences a lab notebook works in. Each notebook is a chat of kind "lab": its field's instructions are its system
/// prompt and its tools are on (those the person may use): the lab's packs (mathematics and units, chemistry, biology),
/// Lean's proofs, Python, the web.
/// </summary>
public static partial class LabFields
{
    private const string Method =
        "Work in steps. Before each, say in a line what it is for. Do the mathematics with the lab's tools, never in your head: math_compute for " +
        "each computation, math_check for each step you rely on and for the answer (or math_solve, which checks its own solutions). A result is " +
        "proved only when math_check says proved; when it says holds numerically, say so; when it says refuted, find the mistake and correct it. " +
        "Write the mathematics in LaTeX ($...$ inline, $$...$$ on its own line) as the tools give it. End with a short Result: the answer, and " +
        "which of its steps are proved and which are not.";

    public static readonly IReadOnlyList<LabField> All =
    [
        new("mathematics", "Mathematics", "Algebra, calculus, equations, series and matrices checked by SymPy; proofs checked by Lean with Mathlib.", ["lab", "proofs", "python"],
            "You are a mathematician working a problem in the science lab. When the problem asks for a proof and lean_check is among your " +
            "tools, write the theorem in Lean with Mathlib and check it: it is proved only when lean_check says checked. " + Method),
        new("physics", "Physics", "Quantities with units, formulas derived and checked, numbers worked out with their units.", ["lab", "python"],
            "You are a physicist working a problem in the science lab. Keep units on every quantity and convert them with units_convert, which " +
            "also checks that the units of an equation agree. " + Method),
        new("chemistry", "Chemistry", "Molecules from SMILES (RDKit), molar masses, reactions balanced and checked atom by atom, units checked.", ["chemistry", "lab", "python"],
            "You are a chemist working a problem in the science lab. Balance each reaction with chem_balance, take molar masses from " +
            "chem_formula, read a molecule with chem_molecule from its SMILES (and check its formula there), keep units with units_convert, " +
            "and compute with Python when a table of values helps. " + Method),
        new("statistics", "Statistics", "Data, distributions and tests in Python (SciPy, statsmodels), formulas checked by SymPy.", ["python", "lab"],
            "You are a statistician working a problem in the science lab. Compute with Python (SciPy, statsmodels, pandas) and say which test you " +
            "use and why, its assumptions and what it shows. Check closed forms with math_check. " + Method),
        new("biology", "Biology", "DNA, RNA and protein sequences with Biopython: translation, alignment, masses; data in Python.", ["biology", "python", "lab"],
            "You are a biologist working a problem in the science lab. Work sequences with bio_sequence and bio_align, never by hand, and " +
            "data with Python (SciPy, pandas), saying which test you use and why. " + Method),
        new("quantum", "Quantum computing", "Circuits run and checked (Qiskit, Aer), error correction (Stim, PyMatching), open systems (QuTiP), VQE and QAOA (PennyLane), molecules (PySCF, OpenFermion).", ["quantum", "lab", "python"],
            "You are a quantum information scientist working a problem in the science lab, on simulators. Run each circuit with quantum_run, " +
            "check each claimed state, probability or equivalence with quantum_check, sample codes with qec_sample, follow open systems with " +
            "qubit_dynamics, optimise with quantum_variational, and take molecules' energies from quantum_chemistry; never work amplitudes " +
            "out by hand. Do the algebra around them with math_compute and math_check. " + Method),
        new("general", "Any science", "A problem of any science: computed and checked with the lab's tools, the web for what it needs to look up.", ["lab", "python", "web"],
            "You are a scientist working a problem in the science lab. Look up what you need with the web and cite it. " + Method),
    ];

    /// <summary>What the model is asked when its answer in a notebook ran no check: to check it with the tools, and correct it.</summary>
    public const string CheckRequest =
        "[The lab] Before this answer ends, check it with the lab's tools: math_check that the result satisfies the problem (or " +
        "math_solve, chem_balance, quantum_check or lean_check, as fits), with the problem's own numbers. If the check disagrees, find the mistake and " +
        "correct it. Then give the final answer again, in the same form as before.";

    /// <summary>What the model is asked when its answer in a notebook gives code no run had as written: to run it, and correct it.</summary>
    public const string RunRequest =
        "[The lab] Your answer gives code. Before this answer ends, run that code as written with run_python, with a short call of it on " +
        "an example of the problem's input after it. If it fails, or gives what the problem does not ask, correct it and run it again. " +
        "Then give the final answer again, in the same form as before, with the whole code in one block.";

    public static LabField? Of(string? id) => All.FirstOrDefault(f => f.Id == id);

    /// <summary>The last Python block of an answer, or null when it gives none.</summary>
    public static string? AnswerCode(string answer)
    {
        var blocks = PythonBlock().Matches(answer);
        return blocks.Count == 0 ? null : blocks[^1].Groups[1].Value;
    }

    /// <summary>Whether a program ran the code as written: its lines in order, blank lines and trailing spaces aside.</summary>
    public static bool Ran(string program, string code) => Lines(program).Contains(Lines(code), StringComparison.Ordinal);

    private static string Lines(string code) => string.Join('\n', code.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0));

    [GeneratedRegex(@"```(?:python|py)[ \t]*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex PythonBlock();
}
