using KefRemote.Drivers.Kef;
using Xunit;

namespace KefRemote.Tests;

public class KefFinishesTests
{
    [Fact]
    public void EveryKnownModelHasFinishes()
    {
        foreach (var model in new[] { "LSX II LT", "LSX II", "LS50 Wireless II", "LS60 Wireless", "XIO" })
            Assert.All(KefFinishes.For(model), f => Assert.Equal(model, f.Model));
        Assert.Equal(["Sage Green", "Stone White", "Graphite Grey"], KefFinishes.For("LSX II LT").Select(f => f.Name));
    }

    [Fact]
    public void IdsAreUnique() =>
        Assert.Equal(KefFinishes.All.Count, KefFinishes.All.Select(f => f.Id).Distinct().Count());

    [Fact]
    public void UnknownModelOffersEveryFinish() =>
        Assert.Equal(KefFinishes.All, KefFinishes.For("KEF LS30"));

    [Fact]
    public void SavedFinishOfAnotherModelIsIgnored()
    {
        Assert.Equal("Sage Green", KefFinishes.Find("lsx-ii-lt/sage-green", "LSX II LT")?.Name);
        Assert.Null(KefFinishes.Find("lsx-ii-lt/sage-green", "LS50 Wireless II"));
        Assert.Equal("Sage Green", KefFinishes.Find("lsx-ii-lt/sage-green", null)?.Name);
        Assert.Equal("Sage Green", KefFinishes.Find("lsx-ii-lt/sage-green", "KEF LS30")?.Name);
        Assert.Null(KefFinishes.Find("lsx-ii-lt/purple", "LSX II LT"));
        Assert.Null(KefFinishes.Find(null, "LSX II LT"));
    }
}
