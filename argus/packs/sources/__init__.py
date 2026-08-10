"""Documentation source adapters.

``SOURCES`` is what the CLI resolves ``--source python`` against, so adding an
adapter here is what makes it buildable.
"""

from .base import ApiSymbol, Doc, Source
from .composite import ScriptingDocs, Win32WithSamples, WdkWithSamples
from .code_samples import AlgorithmsCpp, WindowsClassicSamples, WindowsDriverSamples
from .debugger_docs import DebuggerDocs
from .microsoft_docs import CppDocs, WdkDdi, Win32Api
from .system_design import SystemDesignPrimer
from .python_docs import PythonDocs
from .react_docs import ReactDocs

SOURCES: dict[str, type] = {
    "python": PythonDocs,
    "react": ReactDocs,
    # Separate packs on purpose: a driver developer has no use for the STL
    # reference and an application developer none for IRQL rules, so
    # installing one must not cost the others' disk.
    "cpp": CppDocs,
    # win32/wdk are the composites: API reference AND samples in one
    # pack, because "what does this do" and "show me one working" are
    # halves of the same question. The reference alone stays available
    # for anyone who wants just it.
    "win32": Win32WithSamples,
    "wdk": WdkWithSamples,
    "win32-docs": Win32Api,
    "wdk-docs": WdkDdi,
    # Source-code corpora. No symbol inventory beyond the layout, so these
    # answer docs_search well and docs_lookup only for sample names.
    "wdk-samples": WindowsDriverSamples,
    "win32-samples": WindowsClassicSamples,
    "algorithms": AlgorithmsCpp,
    "system-design": SystemDesignPrimer,
    # WinDbg command reference AND the debugging how-to articles: a question
    # about a crash rarely arrives already sorted into "which command" and
    # "how do I get a dump".
    "debugger": DebuggerDocs,
    # PowerShell + cmd + Unix tools in one pack: a scripting question
    # does not arrive already sorted by shell.
    "scripting": ScriptingDocs,
}

__all__ = [
    "ApiSymbol", "Doc", "Source", "PythonDocs", "ReactDocs",
    "CppDocs", "Win32Api", "WdkDdi", "WindowsDriverSamples",
    "WindowsClassicSamples", "AlgorithmsCpp", "SystemDesignPrimer",
    "Win32WithSamples", "WdkWithSamples", "ScriptingDocs", "DebuggerDocs",
    "SOURCES",
]
