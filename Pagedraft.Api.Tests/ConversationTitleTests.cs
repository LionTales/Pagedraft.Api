using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Pins <see cref="ConversationTitle.Untitled"/> as an unreachable floor rather than an English string a
/// Hebrew-default author could actually see (be-f01, Show C1 history fixes).
///
/// <para>This does NOT test <c>ProductChatController.Ask</c> itself - <c>ConversationTitle</c> has no
/// dependency on it and must stay pure and DB-free. It instead pins the boundary: <see cref="Untitled_OnlyForInputTheAskGuardAlreadyRejects"/>
/// asserts <see cref="ConversationTitle.Untitled"/> comes back ONLY for null, empty, or whitespace-only
/// input - exactly the set <c>ProductChatController.Ask</c>'s <c>string.IsNullOrWhiteSpace(req?.Question)</c>
/// check turns into a 400 before <c>FromFirstMessage</c> is ever called (no test currently pins that
/// controller guard directly; if its condition ever changes, this test must be re-read against it).</para>
/// </summary>
public class ConversationTitleTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void Untitled_OnlyForInputTheAskGuardAlreadyRejects(string? blank)
    {
        Assert.Equal(ConversationTitle.Untitled, ConversationTitle.FromFirstMessage(blank));
    }

    [Theory]
    [InlineData("a")]
    [InlineData(" a ")]
    [InlineData("How do I export?")]
    public void NonBlankInput_NeverProducesTheUntitledFloor(string nonBlank)
    {
        Assert.NotEqual(ConversationTitle.Untitled, ConversationTitle.FromFirstMessage(nonBlank));
    }
}
