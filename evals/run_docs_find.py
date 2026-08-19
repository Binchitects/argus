"""How good is `docs_find` at answering "which API does X"?

Every question here is description-shaped: **no identifier appears in the
question text**. That is the whole point of the tool -- `docs_lookup` needs a
name and cannot fire, and `docs_search` ranks whole pages rather than symbols.

**Each expected answer is verified to exist before anything is scored.** A
question whose answer is not in the installed packs measures the corpus, not
the ranking, and would quietly drag the score down forever. The harness
refuses to run rather than report a number built on absent answers.

Reported as top-1 / top-3 / top-10, because they say different things: top-1
is what a model shows a developer, and top-10 is whether the ranking is broken
or merely imperfect. A result that is 20% top-1 and 90% top-10 needs ranking
work; one that is 20% at both needs retrieval work.

    python evals/run_docs_find.py            # score
    python evals/run_docs_find.py --detail   # and show every miss
"""
from __future__ import annotations

import argparse
import pathlib
import sys

PACKS = "packs"

#: (question, expected symbol, pack). The expected symbol is matched
#: case-insensitively as a substring of the returned name, so
#: `std::vector::push_back` matches an entry named for the member.
QUESTIONS: list[tuple[str, str, str]] = [
    # -- scripting ------------------------------------------------------
    ("mirror a directory tree including deletions", "robocopy", "scripting"),
    ("write objects to a comma separated values file", "Export-Csv", "scripting"),
    ("read a comma separated values file into objects", "Import-Csv", "scripting"),
    ("list the processes running on this machine", "Get-Process", "scripting"),
    ("stop a running process", "Stop-Process", "scripting"),
    ("download a file over http from a script", "Invoke-WebRequest", "scripting"),
    # -- python ---------------------------------------------------------
    ("join path segments into a single path", "os.path.join", "python"),
    ("parse a JSON string into a python object", "json.loads", "python"),
    ("run an external command and capture its output", "subprocess.run", "python"),
    ("make a shallow copy of a list", "copy.copy", "python"),
    # -- dotnet ---------------------------------------------------------
    ("convert an object to JSON text in .NET", "JsonSerializer.Serialize", "dotnet"),
    ("build a string efficiently through many edits", "StringBuilder", "dotnet"),
    ("queue work onto the thread pool", "Task.Run", "dotnet"),
    ("read the entire contents of a text file", "File.ReadAllText", "dotnet"),
    # -- wdk ------------------------------------------------------------
    ("allocate memory from the kernel pool", "ExAllocatePool2", "wdk"),
    ("create a device object for a driver", "IoCreateDevice", "wdk"),
    ("copy a wide string safely into a fixed buffer", "RtlStringCchCopyW", "wdk"),
    # -- win32 ----------------------------------------------------------
    ("open or create a file and get a handle", "CreateFileW", "win32"),
    ("acquire a cryptographic service provider context", "CryptAcquireContextW", "win32"),
    # -- debugger -------------------------------------------------------
    ("automatically analyse a crash dump", "analyze", "debugger"),
    ("reload symbol files in the debugger", ".reload", "debugger"),
    ("display a structure at a memory address", "dt", "debugger"),
    # -- sqlite ---------------------------------------------------------
    ("rebuild the database file to reclaim unused space", "VACUUM", "sqlite"),
    # -- cppreference ---------------------------------------------------
    ("append an element to the end of a vector", "std::vector::push_back", "cppreference"),
    ("reserve vector capacity ahead of time", "std::vector::reserve", "cppreference"),
    # -- python C API ---------------------------------------------------
    # Labelled apart from the py-domain questions above though both live in
    # the python pack: the two were read by different code paths and, until
    # the c: domain was matched at all, only one of them had descriptions.
    # Scoring them together would average a fixed corpus with an unfixed one.
    ("convert the arguments passed to a C extension function into local variables",
     "PyArg_ParseTuple", "python-capi"),
    ("raise an exception from C code with a message", "PyErr_SetString", "python-capi"),
    ("check whether an error has been raised in C code", "PyErr_Occurred", "python-capi"),
    ("build a string object from a null-terminated C string", "PyUnicode_FromString",
     "python-capi"),
    # Not "call a callable with a tuple of arguments" -- that describes
    # PyObject_Call exactly as well as PyObject_CallObject, and a question
    # with two correct answers measures nothing. The C-string key is what
    # distinguishes this one from PyDict_SetItem.
    ("store a value in a dictionary under a C string key", "PyDict_SetItemString",
     "python-capi"),
    ("create the module object for a C extension", "PyModule_Create", "python-capi"),
    # -- cpp (MSVC: CRT, MFC, ATL) --------------------------------------
    # The pack held 37,325 symbols and had no question, so nothing measured
    # whether it answered anything -- it could only ever have shown up here by
    # displacing another pack's answer. Each expected answer below was read
    # out of the corpus, not recalled, and the questions deliberately avoid
    # the page's own wording: _countof's lede says "statically allocated
    # array", so the question asks for a "fixed-size array".
    ("count the items in a fixed-size array at compile time", "_countof", "cpp"),
    ("smart pointer that releases a COM interface automatically", "CComPtr", "cpp"),
    ("MFC class letting one thread at a time use a resource", "CCriticalSection", "cpp"),
    ("the class an MFC program derives its application object from", "CWinApp", "cpp"),
    ("allocate memory on the stack rather than the heap", "_alloca", "cpp"),
]


#: Question group -> installed source name, where they differ.
LANG_FOR_GROUP = {"python-capi": "python"}


def verify(opened) -> list[str]:
    """Every expected answer must exist, or the score measures the corpus."""
    from argus.store import packs as packs_store

    problems = []
    for question, expected, pack in QUESTIONS:
        if not packs_store.lookup_symbol(opened, expected, limit=1):
            problems.append(f"{pack}: {expected!r} is not in the packs "
                            f"(question: {question!r})")
    return problems


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--packs", default=PACKS)
    ap.add_argument("--detail", action="store_true", help="show every miss")
    ap.add_argument("--limit", type=int, default=10)
    ap.add_argument("--arm", choices=("lexical", "hybrid", "both"),
                    default="lexical",
                    help="hybrid adds symbols from semantically-matching "
                         "pages; it needs the embedder running")
    ap.add_argument("--scoped", action="store_true",
                    help="pass each question's own source as lang, measuring "
                         "the ceiling if the caller always scoped correctly. "
                         "The tool cannot do this for itself -- it is what "
                         "the model chooses -- so this measures what telling "
                         "the model to scope is worth")
    ap.add_argument("--pack", action="append", metavar="NAME",
                    help="score only questions whose answer lives in NAME "
                         "(repeatable). Adding questions changes the "
                         "denominator, so this is how a figure stays "
                         "comparable to one recorded earlier")
    args = ap.parse_args()

    from argus.store import packs as packs_store

    opened = packs_store.open_packs(
        sorted(pathlib.Path(args.packs).glob("*.arguspack")))
    try:
        problems = verify(opened)
        if problems:
            print("QUESTION SET REJECTED -- expected answers missing:")
            for p in problems:
                print("  " + p)
            sys.exit(1)
        print(f"question set verified: {len(QUESTIONS)} answers all present\n")

        # Verification always covers the whole set -- a filter selects what is
        # SCORED, never what is checked for existence, so narrowing can never
        # hide a question whose answer has fallen out of the packs.
        selected = [q for q in QUESTIONS
                    if not args.pack or q[2] in set(args.pack)]
        if not selected:
            print(f"no questions for {args.pack}")
            sys.exit(1)
        if args.pack:
            print(f"scoring {len(selected)} of {len(QUESTIONS)} "
                  f"({', '.join(sorted(set(args.pack)))} only)\n")

        arms = ("lexical", "hybrid") if args.arm == "both" else (args.arm,)
        vectors = {}
        if "hybrid" in arms:
            # Embedded in one batch so the embedder is not what is being
            # measured, and through embed_batch rather than the model directly
            # so a question gets the same L2 normalisation the pack's chunks
            # were built with. Comparing an unnormalised query against
            # normalised chunks would score every pack slightly wrong in a way
            # no assertion here would catch.
            from argus.embed import embed_batch
            questions = [q for q, _e, _p in selected]
            vectors = dict(zip(questions, embed_batch(questions)))

        for arm in arms:
            at1 = at3 = at10 = 0
            misses = []
            for question, expected, pack in selected:
                # The label is the question GROUP; python-capi's answers live
                # in the python pack, so the group is mapped to a source name
                # rather than passed through.
                lang = LANG_FOR_GROUP.get(pack, pack) if args.scoped else None
                if arm == "hybrid":
                    rows = packs_store.search_symbols_hybrid(
                        opened, question, vectors[question], lang=lang,
                        limit=args.limit)
                else:
                    rows = packs_store.search_symbols(
                        opened, question, lang=lang, limit=args.limit)
                names = [str(r["name"]) for r in rows]
                rank = next((i for i, n in enumerate(names)
                             if expected.lower() in n.lower()), None)
                if rank is None:
                    misses.append((question, expected, pack, names[:3]))
                else:
                    at10 += 1
                    at3 += rank < 3
                    at1 += rank < 1
                    if rank > 0:
                        misses.append((question, expected, pack,
                                       names[:3] + [f"<rank {rank + 1}>"]))

            total = len(selected)
            print(f"  [{arm}]")
            print(f"  top-1  {at1:3}/{total}  {at1 / total:5.0%}")
            print(f"  top-3  {at3:3}/{total}  {at3 / total:5.0%}")
            print(f"  top-10 {at10:3}/{total}  {at10 / total:5.0%}")

            if args.detail and misses:
                print(f"\n  not top-1 ({len(misses)}):")
                for question, expected, pack, got in misses:
                    print(f"    [{pack}] {question}")
                    print(f"       want {expected}  got {got}")
            print()
    finally:
        packs_store.close_packs(opened)


if __name__ == "__main__":
    main()
