using System.Runtime.CompilerServices;
using KefRemote.Core;

namespace KefRemote.Tests;

static class TestLanguage
{
    /// <summary>Messages are checked in Korean whatever the machine's display language.</summary>
    [ModuleInitializer]
    internal static void UseKorean() => Loc.Language = UiLanguage.Korean;
}
