using Llm.Api.Vector;

namespace Llm.Tests;

/// <summary>SVG made safe and tidy: script, handlers and outside references never kept; what draws kept as written.</summary>
public sealed class SvgSafeTests
{
    [Fact]
    public void An_svg_in_a_models_answer_is_read_given_its_namespace_a_viewbox_and_a_size()
    {
        var s = SvgSafe.Clean("""
            Here is your icon:
            ```svg
            <svg width="120" height="80px"><rect x="10.123456" y="10" width="50" height="30" fill="#e33"/><text x="5" y="70">Hi&nbsp;there &copy; &bogus;</text></svg>
            ```
            Enjoy!
            """);
        Assert.StartsWith("<svg", s.Markup, StringComparison.Ordinal);
        Assert.Contains("xmlns=\"http://www.w3.org/2000/svg\"", s.Markup, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 120 80\"", s.Markup, StringComparison.Ordinal);
        Assert.Contains("x=\"10.123\"", s.Markup, StringComparison.Ordinal);
        Assert.Contains("Hi there © </text>", s.Markup, StringComparison.Ordinal);
        Assert.Equal((120, 80), (s.Width, s.Height));
        Assert.Contains("a viewBox added", s.Changed);
        Assert.False(s.Animated);
        Assert.Equal(3, s.Elements);
    }

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script><circle r="5"/></svg>""", "<script")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><circle r="5" onclick="alert(1)" onload="x()"/></svg>""", "onclick")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><foreignObject><div xmlns="http://www.w3.org/1999/xhtml"><img src="x" onerror="alert(1)"/></div></foreignObject><circle r="5"/></svg>""", "foreignObject")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><a xlink:href="javascript:alert(1)"><circle r="5"/></a></svg>""", "javascript")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image href="https://tracker.example/p.png"/><circle r="5"/></svg>""", "tracker")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><use href="other.svg#icon"/><circle r="5"/></svg>""", "other.svg")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><style>@import url(https://x.example/a.css); circle { fill: url(https://x.example/i.png) }</style><circle r="5"/></svg>""", "x.example")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><circle r="5" style="behavior: url(x.htc); fill: red"/></svg>""", "x.htc")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><a href="#x"><circle id="x" r="5"/></a><set attributeName="href" to="javascript:alert(1)"/><animate attributeName="onclick" to="alert(1)"/></svg>""", "alert")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><iframe src="https://x.example"/><circle r="5"/></svg>""", "iframe")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><circle r="5" fill="url( 'https://x.example/g' )"/></svg>""", "x.example")]
    public void Script_handlers_html_and_outside_references_never_stay(string svg, string gone)
    {
        var s = SvgSafe.Clean(svg);
        Assert.DoesNotContain(gone, s.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<circle", s.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(s.Changed);
    }

    [Fact]
    public void A_document_type_never_reaches_a_file_its_entities_go_with_it()
    {
        // What is read starts at the <svg>: the DOCTYPE before it is gone, and an entity it named is dropped.
        var s = SvgSafe.Clean("""<!DOCTYPE svg [<!ENTITY x SYSTEM "file:///etc/passwd">]><svg xmlns="http://www.w3.org/2000/svg"><text>&x;</text><rect width="1" height="1"/></svg>""");
        Assert.Contains("<text />", s.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("passwd", s.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("&x;", s.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void What_draws_stays_as_written_animations_gradients_filters_and_its_own_references_included()
    {
        const string svg = """
            <svg viewBox="0 0 100 100" xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape">
              <defs>
                <linearGradient id="g"><stop offset="0" stop-color="#f00"/><stop offset="1" stop-color="#00f"/></linearGradient>
                <filter id="blur"><feGaussianBlur stdDeviation="2"/></filter>
                <path id="track" d="M10 50 C 30 10, 70 10, 90 50"/>
              </defs>
              <style>@keyframes spin { to { transform: rotate(360deg) } } #wheel { animation: spin 2s linear infinite; transform-box: fill-box; transform-origin: center }</style>
              <circle id="wheel" cx="50" cy="50" r="20" fill="url(#g)" filter="url(#blur)" inkscape:label="Wheel"/>
              <circle r="3"><animateMotion dur="3s" repeatCount="indefinite"><mpath xlink:href="#track"/></animateMotion></circle>
              <use xlink:href="#wheel" x="10"/>
              <image href="data:image/png;base64,iVBORw0KGgo=" width="4" height="4"/>
            </svg>
            """;
        var s = SvgSafe.Clean(svg);
        Assert.True(s.Animated);
        foreach (var kept in new[] { "fill=\"url(#g)\"", "filter=\"url(#blur)\"", "<animateMotion", "xlink:href=\"#track\"", "@keyframes spin", "data:image/png;base64", "<feGaussianBlur" })
        {
            Assert.Contains(kept, s.Markup, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("inkscape", s.Markup, StringComparison.Ordinal);
        Assert.Equal((100, 100), (s.Width, s.Height));
    }

    [Fact]
    public void Undeclared_prefixes_a_cut_answer_and_links_are_mended_not_refused()
    {
        var s = SvgSafe.Clean("""<svg viewBox="0 0 10 10"><a xlink:href="#c"><circle id="c" r="2" sodipodi:nodetypes="cc"/></a><use xlink:href="#c" x="4"/>""");
        Assert.Contains("xlink:href=\"#c\"", s.Markup, StringComparison.Ordinal);
        Assert.Contains("xmlns:xlink=\"http://www.w3.org/1999/xlink\"", s.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("sodipodi", s.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<a", s.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "There is no SVG")]
    [InlineData("<div>no</div>", "no <svg>")]
    [InlineData("<svg><g></g></svg>", "draws nothing")]
    [InlineData("<svg><rect></svg>", "not well-formed")]
    public void What_is_no_svg_is_refused_in_words(string text, string why)
    {
        Assert.Contains(why, Assert.Throws<SvgException>(() => SvgSafe.Clean(text)).Message, StringComparison.Ordinal);
    }
}
