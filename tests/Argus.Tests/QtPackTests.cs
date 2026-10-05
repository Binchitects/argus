using Argus.Packs;
using Argus.Packs.Sources;

namespace Argus.Tests;

/// <summary>
/// The Qt pack, from pages and qdoc indexes cut out of the real Qt 4.8.7,
/// 5.15.2 and 6.10.3 documentation (tests/fixtures/argus/packs/qt).
/// </summary>
[Collection("process-state")]
public class QtPackTests
{
    static string Root => Path.Combine(AppContext.BaseDirectory, "packfixtures", "qt");

    static ApiSymbol One(IEnumerable<ApiSymbol> symbols, string name, string docPath, string anchor) =>
        symbols.Single(s => s.Name == name && s.DocPath == docPath && s.Anchor == anchor);

    [Fact]
    public void Qdoc_pages_become_markdown_with_their_declarations_pinned_to_qdoc_anchors()
    {
        var page = QDocHtml.Read(File.ReadAllText(Path.Combine(Root, "qt6", "qtcore", "qstring.html")), " (Qt 6.10)");
        Assert.Equal("QString Class", page.Title);
        Assert.StartsWith("# QString Class (Qt 6.10)\n\nThe QString class provides a Unicode character string.\n", page.Body);
        Assert.Equal("<QString>", page.Header);
        Assert.Equal("The QString class provides a Unicode character string.", page.Brief);
        // A [since] tag leaves the declaration and becomes the member's version; the heading keeps qdoc's anchor.
        Assert.Contains("### [since 6.1] QString::QString(const char8_t *str) {/* QString-8 */}", page.Body);
        Assert.Equal("Qt 6.1", page.Members["QString-8"].Since);
        Assert.Equal("QString::QString(const char8_t *str)", page.Members["QString-8"].Signature);
        // Two overloads documented together: one heading, the second declaration under it, one description for both.
        Assert.Contains("### QString QString::left(qsizetype n) && {/* left */}\n\n`QString QString::left(qsizetype n) const &`", page.Body);
        Assert.Equal(page.Members["left"].Brief, page.Members["left-1"].Brief);
        Assert.StartsWith("Returns a substring that contains the n leftmost characters", page.Members["left"].Brief);
        Assert.Contains("```cpp\n", page.Body);
        Assert.DoesNotContain("Qt Company Ltd", page.Body);
        Assert.DoesNotContain("Reference Documentation", page.Body);

        // Qt 4 spaces its declarations and writes its tags last; both come out as the later sets write them.
        var qt4 = QDocHtml.Read(File.ReadAllText(Path.Combine(Root, "qt4", "doc", "html", "qstring.html")), " (Qt 4.8)");
        Assert.Equal("<QString>", qt4.Header);
        Assert.Equal("QString QString::arg(const QString & a, int fieldWidth = 0, const QChar & fillChar = QLatin1Char(' ')) const", qt4.Members["arg"].Signature);
        Assert.Equal("[static] QString QString::fromAscii(const char * str, int size = -1)", qt4.Members["fromAscii"].Display);

        var enums = QDocHtml.Read(File.ReadAllText(Path.Combine(Root, "qt6", "qtcore", "qt.html")), " (Qt 6.10)");
        Assert.Equal("Aligns with the left edge.", enums.Values["Qt::AlignLeft"]);
        Assert.StartsWith("The background colors are darker than the text color", enums.Values["Qt::ColorScheme::Dark"]);

        var obsolete = QDocHtml.Read(File.ReadAllText(Path.Combine(Root, "qt6", "qtcore", "qstring-obsolete.html")), " (Qt 6.10)");
        Assert.Equal("6.4", obsolete.Members["count-1"].Deprecated);
        Assert.Equal("Use size() or length() instead.", obsolete.Members["count-1"].Brief);
    }

    [Fact]
    public void Every_version_is_a_document_set_linked_to_its_own_pages_on_doc_qt_io()
    {
        var docs = new QtDocs().IterDocs(Root).ToDictionary(d => d.Path);
        Assert.Equal("https://doc.qt.io/qt-6/qstring.html", docs["qt-6/qstring.html"].Url);
        Assert.Equal("https://doc.qt.io/qt-5/qstring.html", docs["qt-5/qstring.html"].Url);
        Assert.Equal("https://doc.qt.io/archives/qt-4.8/qstring.html", docs["qt-4.8/qstring.html"].Url);
        Assert.Equal("QString Class (Qt 6.10)", docs["qt-6/qstring.html"].Title);
        Assert.Equal("QString Class (Qt 5.15)", docs["qt-5/qstring.html"].Title);
        Assert.Equal("QString Class Reference (Qt 4.8)", docs["qt-4.8/qstring.html"].Title);
        Assert.Contains("qt-6/qstring-obsolete.html", docs.Keys);
        Assert.Contains("qt-4.8/qcolor-obsolete.html", docs.Keys);
        Assert.Equal("Signals & Slots (Qt 6.10)", docs["qt-6/signalsandslots.html"].Title);
        // A member list repeats the class page, and an example's source listing is not reference text.
        Assert.DoesNotContain("qt-4.8/qstring-members.html", docs.Keys);
        Assert.DoesNotContain("qt-4.8/activeqt-comapp-main-cpp.html", docs.Keys);

        // A class page says, under its title, which versions document the class and where it went.
        Assert.StartsWith("# QRegExp Class (Qt 6.10)\n\nDocumented in: Qt 4.8, Qt 5.15, Qt 6.10 in QtCore5Compat.\n", docs["qt-6/qregexp.html"].Body);
        Assert.StartsWith("# QString Class Reference (Qt 4.8)\n\nDocumented in: Qt 4.8, Qt 5.15, Qt 6.10.\n", docs["qt-4.8/qstring.html"].Body);
        Assert.Contains("## Introduction", docs["qt-6/signalsandslots.html"].Body);
    }

    [Fact]
    public void A_symbol_leads_with_the_versions_that_document_it_and_where_it_went()
    {
        var source = new QtDocs();
        var docs = source.IterDocs(Root).Select(d => d.Path).ToHashSet();
        var symbols = source.IterSymbols(Root).ToList();
        Assert.All(symbols, s => Assert.Contains(s.DocPath, docs));

        // A row per version, newest first, each linked to its own version's page.
        var regexp = symbols.Where(s => s.Name == "QRegExp").ToList();
        Assert.Equal(["qt-6/qregexp.html", "qt-5/qregexp.html", "qt-4.8/qregexp.html"], regexp.Select(s => s.DocPath));
        Assert.Equal("class", regexp[0].Kind);
        Assert.StartsWith("Qt 4.8, Qt 5.15, Qt 6.10 in QtCore5Compat; Header: <QRegExp>; Module: QtCore5Compat -- class QRegExp -- ", regexp[0].Signature);
        Assert.StartsWith("Qt 4.8, Qt 5.15, Qt 6.10 in QtCore5Compat; Header: <QRegExp>; Module: QtCore -- ", regexp[2].Signature);

        // Obsolete in Qt 5, gone in Qt 6.
        var fromAscii = One(symbols, "QString::fromAscii", "qt-5/qstring-obsolete.html", "fromAscii");
        Assert.StartsWith("Qt 4.8, Qt 5.15 (obsolete), not in Qt 6; Header: <QString>; Module: QtCore -- [static] QString QString::fromAscii(const char *str, int size = -1)", fromAscii.Signature);
        // Deprecated in one overload only: the row says so for that overload, with the release from the page.
        var count = One(symbols, "QString::count", "qt-6/qstring-obsolete.html", "count-1");
        Assert.StartsWith("Qt 4.8, Qt 5.15, Qt 6.10 (deprecated since 6.4); Header: <QString>; Module: QtCore -- [constexpr] qsizetype QString::count() const -- Use size() or length() instead.", count.Signature);
        Assert.StartsWith("Qt 4.8, Qt 5.15, Qt 6.10; ", One(symbols, "QString::count", "qt-6/qstring.html", "count").Signature);

        // When it arrived: a Qt 6 tag, a Qt 5 sentence.
        Assert.Contains("; Since: Qt 6.1;", One(symbols, "QString::QString", "qt-6/qstring.html", "QString-8").Signature);
        Assert.Contains("; Since: Qt 5.10;", One(symbols, "QString::arg", "qt-5/qstring.html", "arg-12").Signature);

        // Enum values, a scoped enum's under its enum, with the description from the value table.
        Assert.Equal("enum-value", One(symbols, "Qt::AlignLeft", "qt-6/qt.html", "AlignmentFlag-enum").Kind);
        Assert.EndsWith(" -- Qt::AlignLeft = 0x0001 -- Aligns with the left edge.", One(symbols, "Qt::AlignLeft", "qt-6/qt.html", "AlignmentFlag-enum").Signature);
        Assert.StartsWith("Qt 4.8, Qt 5.15, Qt 6.10; ", One(symbols, "Qt::AlignLeft", "qt-4.8/qt.html", "AlignmentFlag-enum").Signature);
        Assert.StartsWith("Qt 6.10; Header: <Qt>; Module: QtCore -- Qt::ColorScheme::Dark = 2 -- The background colors are darker",
            One(symbols, "Qt::ColorScheme::Dark", "qt-6/qt.html", "ColorScheme-enum").Signature);
        Assert.Equal("flags", One(symbols, "Qt::Alignment", "qt-6/qt.html", "AlignmentFlag-enum").Kind);

        // A property's getter is the property; its setter links to the property and keeps its own declaration.
        Assert.Equal(["property"], symbols.Where(s => s.Name == "QObject::objectName").Select(s => s.Kind));
        Assert.Contains(" -- void QObject::setObjectName(const QString &name)", One(symbols, "QObject::setObjectName", "qt-4.8/qobject.html", "objectName-prop").Signature);
        Assert.Equal("signal", One(symbols, "QObject::destroyed", "qt-4.8/qobject.html", "destroyed").Kind);
        Assert.StartsWith("Qt 4.8, not in Qt 5, Qt 6; Header: <QObject>", One(symbols, "Q_OBJECT", "qt-4.8/qobject.html", "Q_OBJECT").Signature);
        Assert.Equal("macro", One(symbols, "QStringLiteral", "qt-6/qstring.html", "QStringLiteral").Kind);

        // QML types and their properties are symbols too, with the import instead of a header.
        var width = One(symbols, "Item.width", "qt-6/qml-qtquick-item.html", "width-prop");
        Assert.Equal("qml-property", width.Kind);
        // (The fixture's Qt 5 set has no Qt Quick pages.)
        Assert.StartsWith("Qt 4.8, Qt 6.10, not in Qt 5; Import: import QtQuick; Since: Qt 4.7 -- width : real", width.Signature);
        Assert.Contains(symbols, s => s.Name == "Item.width" && s.DocPath == "qt-4.8/qml-item.html" && s.Anchor == "width-prop");

        // An index entry whose anchor the page never wrote is undocumented, not a symbol.
        Assert.DoesNotContain(symbols, s => s.Name == "QString::append");
        Assert.DoesNotContain(symbols.GroupBy(s => (s.Name, s.Kind, s.DocPath, s.Anchor)), g => g.Count() > 1);
    }

    [Fact]
    public void A_qt_pack_builds_with_no_unresolved_symbols_and_a_search_can_ask_for_one_version()
    {
        using var dir = new TempDir();
        var outPath = Path.Combine(dir.Path, "qt.arguspack");
        // No commit given: the documentation sets' own versions are the provenance.
        PackBuilder.BuildPack(new QtDocs(), Root, outPath, "1", FakeEmbedder.Embed, log: TextWriter.Null);
        var packs = PackStore.OpenPacks([outPath]);
        try
        {
            var meta = packs[0].Meta;
            Assert.Equal("qt4=4.8.7,qt5=5.15.2,qt6=6.10.3", meta["source_commit"]);
            Assert.Equal("0", meta["unresolved_symbol_count"]);
            Assert.Equal("GFDL-1.3-only", meta["license"]);
            Assert.Equal("qt4=qt-4.8/;qt5=qt-5/;qt6=qt-6/", meta["facets"]);

            var all = PackStore.LookupSymbol(packs, "QRegExp");
            Assert.Equal(["https://doc.qt.io/qt-6/qregexp.html", "https://doc.qt.io/qt-5/qregexp.html", "https://doc.qt.io/archives/qt-4.8/qregexp.html"],
                all.Select(r => r["url"]!.GetValue<string>()));
            var qt5 = PackStore.LookupSymbol(packs, "QRegExp", "qt5");
            Assert.Equal("https://doc.qt.io/qt-5/qregexp.html", qt5.Single()["url"]!.GetValue<string>());
            Assert.Equal("qt", qt5[0]["source"]!.GetValue<string>());
            Assert.Contains(PackStore.LookupSymbol(packs, "QString::count", "qt6"),
                r => r["url"]!.GetValue<string>() == "https://doc.qt.io/qt-6/qstring-obsolete.html#count-1");
            Assert.All(PackStore.LookupSymbol(packs, "QString::count", "qt4"), r => Assert.StartsWith("qt-4.8/", r["doc_path"]!.GetValue<string>()));

            var text = PackStore.SearchText(packs, "QRegExp", "qt4");
            Assert.NotEmpty(text);
            Assert.All(text, r => Assert.StartsWith("qt-4.8/", r["doc_path"]!.GetValue<string>()));
            var semantic = PackStore.SearchDocs(packs, FakeEmbedder.Vector("pattern matching with regular expressions"), "qt5", limit: 5);
            Assert.NotEmpty(semantic);
            Assert.All(semantic, r => Assert.StartsWith("qt-5/", r["doc_path"]!.GetValue<string>()));
            var described = PackStore.SearchSymbolsHybrid(packs, "deprecated string count", FakeEmbedder.Vector("deprecated string count"), "qt6");
            Assert.NotEmpty(described);
            Assert.All(described, r => Assert.StartsWith("qt-6/", r["doc_path"]!.GetValue<string>()));
            // The pack's name still means all of it, and a facet no pack declares selects nothing.
            Assert.Single(PackStore.SelectPacks(packs, "qt"));
            Assert.Single(PackStore.SelectPacks(packs, "Qt6"));
            Assert.Empty(PackStore.SelectPacks(packs, "qt7"));
        }
        finally { PackStore.ClosePacks(packs); }
    }
}
