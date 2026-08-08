"""Documentation source adapters.

``SOURCES`` is what the CLI resolves ``--source python`` against, so adding an
adapter here is what makes it buildable.
"""

from .base import ApiSymbol, Doc, Source
from .code_samples import AlgorithmsCpp, WindowsClassicSamples, WindowsDriverSamples
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
    "win32": Win32Api,
    "wdk": WdkDdi,
    # Source-code corpora. No symbol inventory beyond the layout, so these
    # answer docs_search well and docs_lookup only for sample names.
    "wdk-samples": WindowsDriverSamples,
    "win32-samples": WindowsClassicSamples,
    "algorithms": AlgorithmsCpp,
    "system-design": SystemDesignPrimer,
}

__all__ = [
    "ApiSymbol", "Doc", "Source", "PythonDocs", "ReactDocs",
    "CppDocs", "Win32Api", "WdkDdi", "WindowsDriverSamples",
    "WindowsClassicSamples", "AlgorithmsCpp", "SystemDesignPrimer", "SOURCES",
]
