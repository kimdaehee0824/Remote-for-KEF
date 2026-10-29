using KefRemote.Drivers.Kef;
using Xunit;

namespace KefRemote.Tests;

public class KefFinishesTests
{
    [Fact]
    public void EveryKnownModelHasFinishes()
    {
        foreach (var model in KefModels.Names)
        {
            Assert.NotEmpty(KefFinishes.For(model));
            Assert.All(KefFinishes.For(model), f => Assert.Equal(model, f.Model));
        }
        Assert.Equal(["Sage Green", "Stone White", "Graphite Grey"], KefFinishes.For("LSX II LT").Select(f => f.Name));
        Assert.Equal(["Dusk Titanium", "Eclipse Black", "Mineral White"], KefFinishes.For("LS LUXE").Select(f => f.Name));
    }

    [Fact]
    public void IdsAreUnique() =>
        Assert.Equal(KefFinishes.All.Count, KefFinishes.All.Select(f => f.Id).Distinct().Count());

    [Fact]
    public void UnknownModelHasNoFinishes()
    {
        Assert.Empty(KefFinishes.For("KEF LS30"));
        Assert.Empty(KefFinishes.For(null));
    }

    [Fact]
    public void EdgeIsLighterOnDarkCabinetsAndDarkerOnLightOnes()
    {
        var sage = KefFinishes.Find("lsx-ii-lt/sage-green", null)!;
        var stone = KefFinishes.Find("lsx-ii-lt/stone-white", null)!;
        Assert.Equal(0x7E8E84u, sage.Edge);
        Assert.Equal(0xAFA9A2u, stone.Edge);
    }

    [Fact]
    public void DefaultIsRoyalBlueWithItsOutline()
    {
        Assert.Equal("ls60-wireless/royal-blue", KefFinishes.Default.Id);
        Assert.Equal(0x3A527Bu, KefFinishes.Default.Cabinet);
        Assert.Equal(0x7F8FA9u, KefFinishes.Default.Edge);
    }

    [Fact]
    public void SavedFinishOfAnotherModelIsIgnored()
    {
        Assert.Equal("Sage Green", KefFinishes.Find("lsx-ii-lt/sage-green", "LSX II LT")?.Name);
        Assert.Null(KefFinishes.Find("lsx-ii-lt/sage-green", "LS50 Wireless II"));
        Assert.Equal("Sage Green", KefFinishes.Find("lsx-ii-lt/sage-green", null)?.Name);
        Assert.Null(KefFinishes.Find("lsx-ii-lt/sage-green", "KEF LS30"));
        Assert.Null(KefFinishes.Find("lsx-ii-lt/purple", "LSX II LT"));
        Assert.Null(KefFinishes.Find(null, "LSX II LT"));
    }

    [Fact]
    public void ShadesAreMixedFromTheCabinet()
    {
        var sage = KefFinishes.Find("lsx-ii-lt/sage-green", null)!;
        Assert.Equal(sage.Cabinet, sage.Color(KefPaint.Cabinet));
        Assert.Equal(sage.Edge, sage.Color(KefPaint.Edge));
        Assert.Equal(sage.Tweeter, sage.Color(KefPaint.Tweeter));
        Assert.Equal(0x516659u, sage.Color(KefPaint.Fabric));
        Assert.Equal(0x1F2D24u, sage.Color(KefPaint.Seam));
        Assert.Equal(0x5C7063u, sage.Color(KefPaint.Plinth));
        Assert.Equal(0x596D60u, sage.Color(KefPaint.Top));
    }
}
