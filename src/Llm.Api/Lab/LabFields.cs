namespace Llm.Api.Lab;

/// <summary>A science of the lab: what it is for, the tools a notebook of it has on, and what its model is told.</summary>
public sealed record LabField(string Id, string Title, string About, string[] Tools, string Instructions);

/// <summary>
/// The sciences a lab notebook works in. Each notebook is a chat of kind "lab": its field's instructions are its system
/// prompt and its tools are on (those the person may use). Chemistry, biology and proofs gain their own tools (RDKit,
/// Biopython, Lean) as they are added to the sandbox.
/// </summary>
public static class LabFields
{
    private const string Method =
        "Work in steps. Before each, say in a line what it is for. Do the mathematics with the lab's tools, never in your head: math_compute for " +
        "each computation, math_check for each step you rely on and for the answer (or math_solve, which checks its own solutions). A result is " +
        "proved only when math_check says proved; when it says holds numerically, say so; when it says refuted, find the mistake and correct it. " +
        "Write the mathematics in LaTeX ($...$ inline, $$...$$ on its own line) as the tools give it. End with a short Result: the answer, and " +
        "which of its steps are proved and which are not.";

    public static readonly IReadOnlyList<LabField> All =
    [
        new("mathematics", "Mathematics", "Algebra, calculus, equations, series and matrices, each step computed and checked by SymPy.", ["lab", "python"],
            "You are a mathematician working a problem in the science lab. " + Method),
        new("physics", "Physics", "Quantities with units, formulas derived and checked, numbers worked out with their units.", ["lab", "python"],
            "You are a physicist working a problem in the science lab. Keep units on every quantity and convert them with units_convert, which " +
            "also checks that the units of an equation agree. " + Method),
        new("chemistry", "Chemistry", "Stoichiometry, concentrations, gas laws and equilibria with units checked; molecules by Python.", ["lab", "python"],
            "You are a chemist working a problem in the science lab. Balance equations with math_solve on the atoms' counts, keep units with " +
            "units_convert, and compute with Python when a table of values helps. " + Method),
        new("statistics", "Statistics", "Data, distributions and tests in Python (SciPy, statsmodels), formulas checked by SymPy.", ["python", "lab"],
            "You are a statistician working a problem in the science lab. Compute with Python (SciPy, statsmodels, pandas) and say which test you " +
            "use and why, its assumptions and what it shows. Check closed forms with math_check. " + Method),
        new("general", "Any science", "A problem of any science: computed and checked with the lab's tools, the web for what it needs to look up.", ["lab", "python", "web"],
            "You are a scientist working a problem in the science lab. Look up what you need with the web and cite it. " + Method),
    ];

    public static LabField? Of(string? id) => All.FirstOrDefault(f => f.Id == id);
}
