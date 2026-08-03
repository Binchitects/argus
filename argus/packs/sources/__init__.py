"""Documentation source adapters.

``SOURCES`` is what the CLI resolves ``--source python`` against, so adding an
adapter here is what makes it buildable.
"""

from .base import ApiSymbol, Doc, Source
from .python_docs import PythonDocs
from .react_docs import ReactDocs

SOURCES: dict[str, type] = {
    "python": PythonDocs,
    "react": ReactDocs,
}

__all__ = ["ApiSymbol", "Doc", "Source", "PythonDocs", "ReactDocs", "SOURCES"]
