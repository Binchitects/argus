"""Documentation source adapters.

``SOURCES`` is what the CLI resolves ``--source python`` against, so adding an
adapter here is what makes it buildable.
"""

from .base import ApiSymbol, Doc, Source
from .microsoft_docs import CppDocs, WdkDdi, Win32Api
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
}

__all__ = [
    "ApiSymbol", "Doc", "Source", "PythonDocs", "ReactDocs",
    "CppDocs", "Win32Api", "WdkDdi", "SOURCES",
]
