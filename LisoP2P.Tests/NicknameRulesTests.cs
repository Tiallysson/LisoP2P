using LisoP2P.Core;

namespace LisoP2P.Tests;

public class NicknameRulesTests
{
    [Fact]
    public void Sanitize_TrimsAndCollapsesWhitespace()
    {
        Assert.Equal("Tiallysson Costa", NicknameRules.Sanitize("  Tiallysson   Costa \t "));
    }

    [Fact]
    public void Sanitize_RemovesControlCharacters()
    {
        Assert.Equal("abc", NicknameRules.Sanitize("abc\n"));
    }

    [Fact]
    public void Sanitize_RemovesZeroWidthAndBom()
    {
        Assert.Equal("ab", NicknameRules.Sanitize("a​b﻿"));
    }

    [Fact]
    public void Sanitize_CapsLength()
    {
        var sanitized = NicknameRules.Sanitize(new string('x', NicknameRules.MaxLength + 20));

        Assert.Equal(NicknameRules.MaxLength, sanitized.Length);
    }

    [Fact]
    public void Sanitize_DoesNotSplitSurrogatePair()
    {
        var input = new string('x', NicknameRules.MaxLength - 1) + "\U0001F600";

        var sanitized = NicknameRules.Sanitize(input);

        Assert.Equal(NicknameRules.MaxLength - 1, sanitized.Length);
        Assert.DoesNotContain(sanitized, char.IsSurrogate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ")]
    public void IsValid_RejectsEmptyResults(string? value)
    {
        Assert.False(NicknameRules.IsValid(value));
        Assert.Equal("", NicknameRules.Sanitize(value));
    }

    [Fact]
    public void FallbackFor_DerivesFromIdOnly()
    {
        var id = new PeerId(Guid.Parse("abcd1234-0000-0000-0000-000000000000"));

        Assert.Equal("user-abcd", NicknameRules.FallbackFor(id));
    }
}
