using KefRemote.Drivers.Kef;
using Xunit;

namespace KefRemote.Tests;

public class KefShapesTests
{
    static IEnumerable<KefPoint> Points(KefFigure figure) => figure.Curves.Prepend(figure.Start);

    [Fact]
    public void EveryKnownModelHasItsOwnDrawing()
    {
        Assert.Equal(KefModels.Names.Count, KefModels.Names.Select(KefShapes.For).Distinct().Count());
        Assert.All(KefModels.Names, model => Assert.NotSame(KefShapes.Generic, KefShapes.For(model)));
    }

    [Fact]
    public void OtherModelsGetTheAppIconsDrawing()
    {
        Assert.Same(KefShapes.Generic, KefShapes.For(null));
        Assert.Same(KefShapes.Generic, KefShapes.For("KEF LS30"));
    }

    [Fact]
    public void FiguresAreClosedBezierPathsOnTheGrid()
    {
        foreach (var figure in KefModels.Names.SelectMany(KefShapes.For).Concat(KefShapes.Generic))
        {
            Assert.Equal(0, figure.Curves.Count % 3);
            Assert.Equal(figure.Start, figure.Curves[^1]);
            Assert.All(Points(figure), p => Assert.InRange(p.X, 0, 48));
            Assert.All(Points(figure), p => Assert.InRange(p.Y, 0, 48));
        }
    }

    [Fact]
    public void EdgeSitsInsideTheCabinet()
    {
        foreach (var model in KefModels.Names.Append("generic"))
        {
            var figures = KefShapes.For(model);
            var edge = Assert.Single(figures, f => f.Stroked);
            Assert.Equal(KefPaint.Edge, edge.Paint);
            var cabinet = figures[figures.ToList().IndexOf(edge) - 1];
            Assert.InRange(Points(edge).Min(p => p.X) - Points(cabinet).Min(p => p.X), 0.74, 0.76);
            Assert.InRange(Points(cabinet).Max(p => p.Y) - Points(edge).Max(p => p.Y), 0.74, 0.76);
        }
    }

    [Theory]
    [InlineData("LSX II LT", 24, 24, 10)]
    [InlineData("LS50 Wireless II", 24, 23.25, 9)]
    [InlineData("LS LUXE", 24, 21.95, 9.4)]
    [InlineData("LS60 Wireless", 24, 13.4, 4.6)]
    public void UniQDriverIsWhereTheModelHasIt(string model, double cx, double cy, double radius)
    {
        var trim = KefShapes.For(model).Last(f => f.Paint == KefPaint.Trim);
        Assert.Equal(cx + radius, Points(trim).Max(p => p.X), 6);
        Assert.Equal(cy - radius, Points(trim).Min(p => p.Y), 6);
        Assert.Equal(KefPaint.Tweeter, KefShapes.For(model)[^1].Paint);
    }

    [Fact]
    public void LuxeIsSymmetric()
    {
        var body = KefShapes.For("LS LUXE").First(f => f.Paint == KefPaint.Cabinet);
        var xs = Points(body).Select(p => Math.Round(p.X - 24, 2)).ToHashSet();
        Assert.All(xs, x => Assert.Contains(-x, xs));
    }
}
