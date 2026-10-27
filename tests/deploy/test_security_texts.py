"""
tools/security-texts.py: MITRE's structured text and example code, CodeQL's query help and metadata, and Bandit's
reStructuredText, read as the security pack's Markdown.
"""
from __future__ import annotations

import importlib.util
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "tools" / "security-texts.py"
spec = importlib.util.spec_from_file_location("security_texts", SCRIPT)
st = importlib.util.module_from_spec(spec)
spec.loader.exec_module(st)

X = 'xmlns="http://cwe.mitre.org/cwe-7" xmlns:h="http://www.w3.org/1999/xhtml"'


class SecurityTexts(unittest.TestCase):
    def test_MITREs_structured_text_is_paragraphs_lists_and_tables(self):
        e = ET.fromstring(f"""<Description {X}><h:p>First <h:b>bold</h:b> line.</h:p><h:div style="margin-left:1em;"><h:div>Highly cost effective:</h:div>
            <h:ul><h:li>Source code   Weakness Analyzer</h:li><h:li>Fuzz Tester</h:li></h:ul></h:div>
            <h:table><h:tr><h:th>Phase</h:th><h:th>Note</h:th></h:tr><h:tr><h:td>Design</h:td><h:td>Early</h:td></h:tr></h:table></Description>""")
        self.assertEqual("First **bold** line.\n\nHighly cost effective:\n\n- Source code Weakness Analyzer\n\n- Fuzz Tester\n\n"
                         "| Phase | Note |\n| --- | --- |\n| Design | Early |", st.prose(e))

    def test_example_code_keeps_its_lines_and_indentation(self):
        e = ET.fromstring(f"""<Example_Code {X} Nature="Bad" Language="C"><h:div>int f() {{<h:div style="margin-left:1em;">gets(buf);<h:br/>return 0;</h:div>}}</h:div></Example_Code>""")
        self.assertEqual("int f() {\n    gets(buf);\n    return 0;\n}", st.code(e))

    def test_a_codeql_query_is_its_metadata_and_its_help_with_samples_included(self):
        with tempfile.TemporaryDirectory() as d:
            q = Path(d) / "Bad.ql"
            q.write_text("/**\n * @name Bad thing\n * @description It is bad,\n *              very.\n * @id cs/bad\n * @tags security\n *       external/cwe/cwe-089\n */\nimport csharp\n")
            (Path(d) / "Bad.cs").write_text("var q = \"SELECT \" + x;\n")
            (Path(d) / "Bad.qhelp").write_text('<!DOCTYPE qhelp PUBLIC "-//Semmle//qhelp//EN" "qhelp.dtd">\n<qhelp><overview><p>Use <code>?</code> se&ntilde;or.</p></overview>'
                                               '<example><sample src="Bad.cs" /></example><references><li><a href="https://owasp.org">OWASP</a></li></references></qhelp>')
            meta = st.ql_metadata(q)
            self.assertEqual(("cs/bad", "It is bad, very.", "security external/cwe/cwe-089"), (meta["id"], meta["description"], meta["tags"]))
            self.assertEqual("Use `?` señor.\n\n## Example\n\n```csharp\nvar q = \"SELECT \" + x;\n```\n\n## References\n\n- [OWASP](https://owasp.org)",
                             st.qhelp_markdown(Path(d) / "Bad.qhelp"))

    def test_bandits_restructured_text_reads_as_markdown(self):
        self.assertEqual("# B608: Test\n\n**Example:**\n\n```\n>> Issue: SQL\n   Severity: Medium\n```\n",
                         st.rst("============\nB608: Test\n============\n\n:Example:\n\n.. code-block:: none\n\n    >> Issue: SQL\n       Severity: Medium\n\n"))

    def test_a_heading_loses_its_pictures(self):
        self.assertEqual("A05:2025 Injection", st.heading('# A05:2025 Injection ![icon](../a.png){: style="height:80px"}\n', "x"))


if __name__ == "__main__":
    unittest.main()
