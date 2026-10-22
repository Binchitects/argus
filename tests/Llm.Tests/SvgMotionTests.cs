using System.Xml.Linq;
using Llm.Api.Vector;

namespace Llm.Tests;

/// <summary>Motions compiled into an SVG as CSS: wrapped parts that move, lines drawn on, colours changed; compiled again from the still drawing.</summary>
public sealed class SvgMotionTests
{
    /// <summary>A small scene with named parts, as a model draws one.</summary>
    public const string Scene = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 300">
          <rect width="400" height="300" fill="#bfe3ff"/>
          <circle id="sun" cx="320" cy="70" r="36" fill="#ffc83d"/>
          <path id="road" d="M0 260 C 120 220, 280 300, 400 250" fill="none" stroke="#555" stroke-width="6"/>
          <g id="car" transform="translate(120,200)">
            <rect id="body" x="0" y="0" width="120" height="40" rx="10" fill="#e33"/>
            <path id="roof" d="M20 0 L40 -26 L90 -26 L104 0 Z" fill="#c22"/>
            <circle id="wheel-left" cx="28" cy="42" r="14" fill="#222"/>
            <circle id="wheel-right" cx="94" cy="42" r="14" fill="#222"/>
          </g>
          <g id="birds"><path d="M40 60 q10 -10 20 0" stroke="#333" fill="none"/><path d="M80 40 q10 -10 20 0" stroke="#333" fill="none"/><path d="M120 70 q10 -10 20 0" stroke="#333" fill="none"/></g>
        </svg>
        """;

    private static readonly Motion[] Motions =
    [
        new() { Target = "sun", Kind = "pop", Duration = 0.8 },
        new() { Target = "sun", Kind = "pulse", Delay = 0.8 },
        new() { Target = "wheel-left", Kind = "spin", Duration = 1 },
        new() { Target = "wheel-right", Kind = "spin", Duration = 1 },
        new() { Target = "car", Kind = "slide-in", From = "left", Amount = 260, Duration = 1.6, Easing = "smooth" },
        new() { Target = "road", Kind = "draw", Duration = 1.2 },
        new() { Target = "roof", Kind = "color", Color = "#8b5cf6", Duration = 1.5 },
        new() { Target = "birds", Kind = "fade-in", Stagger = 0.3, Duration = 0.6 },
        new() { Target = "birds", Kind = "float", Amount = 6 },
    ];

    [Fact]
    public void Each_motion_wraps_its_part_and_two_on_one_part_add_up_with_its_own_transform_kept()
    {
        var a = SvgMotion.Apply(Scene, Motions);
        Assert.Empty(a.Problems);
        var root = XElement.Parse(a.Markup);
        XNamespace s = SvgSafe.Svg;
        // The sun inside two moving groups: pulse outside pop.
        var sun = root.Descendants(s + "circle").Single(c => (string?)c.Attribute("id") == "sun");
        Assert.Equal(["m2", "m1"], sun.Ancestors(s + "g").Where(g => g.Attribute(SvgMotion.Mark) is not null).Select(g => (string)g.Attribute("class")!).Take(2));
        // The car moves as a whole, its own translate kept on it.
        var car = root.Descendants(s + "g").Single(g => (string?)g.Attribute("id") == "car");
        Assert.Equal("translate(120,200)", (string?)car.Attribute("transform"));
        Assert.Equal("m5", (string?)car.Parent!.Attribute("class"));
        // Lines drawn on: measured as 1; a colour change on the roof itself; the birds one after another.
        var road = root.Descendants(s + "path").Single(p => (string?)p.Attribute("id") == "road");
        Assert.Equal(("1", "m6"), ((string?)road.Attribute("pathLength"), (string?)road.Attribute("class")));
        Assert.Contains("stroke-dasharray:1", (string?)road.Attribute("style"), StringComparison.Ordinal);
        var css = root.Elements(s + "style").Single().Value;
        Assert.Contains("@keyframes k7{to{fill:#8b5cf6}}", css, StringComparison.Ordinal);
        Assert.Contains(".m8-2{animation:k8 0.6s ease-out 0.3s 1 normal both;transform-box:fill-box;transform-origin:50% 50%}", css, StringComparison.Ordinal);
        Assert.Contains(".m5{animation:k5 1.6s cubic-bezier(.45,0,.55,1) 0s 1 normal both", css, StringComparison.Ordinal);
        Assert.Contains("translate(-260px,0px)", css, StringComparison.Ordinal);
        // Once through: the car's slide (1.6 s) is the longest of what ends; loops go on.
        Assert.Equal(new Timeline(3, true), a.Timeline);

        var dump = Path.Combine(AppContext.BaseDirectory, "svg-out");
        Directory.CreateDirectory(dump);
        File.WriteAllText(Path.Combine(dump, "scene.svg"), a.Markup);
    }

    [Fact]
    public void Compiled_again_from_the_still_drawing_what_a_compile_added_goes()
    {
        var once = SvgMotion.Apply(Scene, Motions);
        var still = SvgMotion.Still(XElement.Parse(once.Markup)).ToString(SaveOptions.DisableFormatting);
        Assert.Equal(SvgSafe.Clean(Scene).Markup, still);
        var twice = SvgMotion.Apply(once.Markup, [new() { Target = "car", Kind = "bounce" }]);
        Assert.Single(XElement.Parse(twice.Markup).Descendants(), e => (string?)e.Attribute(SvgMotion.Mark) == "wrap");
        Assert.DoesNotContain("pathLength", twice.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("m6", twice.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("m8-1", twice.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("data-motion-style", twice.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_motion_it_cannot_do_says_why_and_the_others_are_done()
    {
        var a = SvgMotion.Apply(Scene, [
            new() { Target = "moon", Kind = "spin" },
            new() { Target = "sun", Kind = "explode" },
            new() { Target = "sun", Kind = "keyframes" },
            new() { Target = "sun", Kind = "keyframes", Keys = [new() { At = 0, Opacity = 0, Scale = 0.5 }, new() { At = 1, Opacity = 1, Rotate = 30, Fill = "red;}*{display:none" }] },
            new() { Kind = "fade-in", Repeat = "3" },
        ]);
        Assert.Equal(3, a.Problems.Count);
        Assert.Contains("no part with the id \"moon\"", a.Problems[0], StringComparison.Ordinal);
        Assert.Contains("no kind \"explode\"", a.Problems[1], StringComparison.Ordinal);
        // Keys of one's own, a colour that is not one never reaching the style; the whole scene faded in three times.
        Assert.Contains("0%{transform:scale(0.5);opacity:0}100%{transform:rotate(30deg);opacity:1}", a.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("display:none", a.Markup, StringComparison.Ordinal);
        Assert.Contains("id=\"scene\"", a.Markup, StringComparison.Ordinal);
        Assert.Equal(new Timeline(2.4, false), a.Timeline);
    }
}

/// <summary>Traced drawings made into parts, and what is said of a drawing that is wrong.</summary>
public sealed class VectorPartsTests
{
    [Fact]
    public void A_traced_picture_has_its_background_and_its_subject_whose_shapes_can_move_one_after_another()
    {
        var named = Llm.Api.Vector.VectorJobs.Named("""
            <svg version="1.1" xmlns="http://www.w3.org/2000/svg" width="100" height="100">
            <path d="M0 0 C40 0 60 0 100 0 C100 40 100 60 100 100 C60 100 40 100 0 100 Z" fill="#FEFEFE" transform="translate(0,0)"/>
            <path d="M0 0 L20 0 L20 20 Z" fill="#E33" transform="translate(30,30)"/>
            <path d="M0 0 L10 0 L10 10 Z" fill="#222" transform="translate(50,50)"/>
            </svg>
            """);
        var root = XElement.Parse(named);
        XNamespace s = SvgSafe.Svg;
        Assert.Equal("background", (string?)root.Elements(s + "path").Single().Attribute("id"));
        var subject = root.Elements(s + "g").Single();
        Assert.Equal(("subject", 2), ((string?)subject.Attribute("id"), subject.Elements().Count()));
        Assert.Equal(["shape-2", "shape-3"], subject.Elements().Select(e => (string)e.Attribute("id")!));
        var a = SvgMotion.Apply(named, [new() { Target = "subject", Kind = "pop", Stagger = 0.1 }]);
        Assert.Empty(a.Problems);
        Assert.Contains(".m1-2{", a.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shape_with_no_area_and_a_drawing_lost_in_its_canvas_are_said()
    {
        var root = XElement.Parse(SvgSafe.Clean("""<svg viewBox="0 0 640 480"><rect id="bg" width="640" height="480"/><polygon id="tree" points="0,0 10,10 20,20"/><rect id="cabin" x="300" y="350" width="50" height="50"/></svg>""").Markup);
        IReadOnlyList<PartBox> parts = [new("bg", "rect", 0, 0, 640, 480, null, null), new("tree", "polygon", 200, 200, 200, 0.5, null, null), new("cabin", "rect", 300, 350, 50, 50, null, null)];
        var issues = SvgDraw.Issues(root, parts, 640, 480);
        Assert.Contains(issues, i => i.StartsWith("#tree has no area", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.StartsWith("the drawing fills too little of the canvas", StringComparison.Ordinal));
        Assert.DoesNotContain(issues, i => i.Contains("#bg", StringComparison.Ordinal));
    }
}
