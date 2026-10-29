namespace Chatcheology.Archive.Tests
{
    /// <summary>
    /// CH-48: the shared external-link rules (store, writer, validator, phone and desktop) and the host a
    /// viewer shows. Invented addresses only; nothing is fetched.
    /// </summary>
    public sealed class ExternalLinkRulesTests
    {
        [Theory]
        [InlineData("https://photos.example.org/album/abc")]
        [InlineData("HTTPS://photos.example.org/album/abc")]
        [InlineData("https://photos.example.org")]
        [InlineData("https://photos.example.org:8443/a?b=c&d=e#f")]
        [InlineData("https://xn--pple-43d.example/a")]
        public void AnAbsoluteHttpsLinkWithAHost_IsKept(string url)
        {
            Assert.Null(ExternalLinkRules.UrlError(url));
            Assert.NotNull(ExternalLinkRules.DisplayHost(url));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("http://photos.example.org/a")]
        [InlineData("javascript:alert(1)")]
        [InlineData("JavaScript://photos.example.org/%0Aalert(1)")]
        [InlineData("file:///C:/synthetic/photo.jpg")]
        [InlineData("file://server/share/photo.jpg")]
        [InlineData("intent://photos.example.org/#Intent;scheme=https;end")]
        [InlineData("content://media/external/images/1")]
        [InlineData("data:text/html,hello")]
        [InlineData("ftp://photos.example.org/a")]
        [InlineData("/relative/path")]
        [InlineData("relative/path")]
        [InlineData("#fragment")]
        [InlineData("?query")]
        [InlineData("//photos.example.org/a")]
        [InlineData("https:photos.example.org/a")]
        [InlineData("https:/photos.example.org/a")]
        [InlineData("https://")]
        [InlineData("https:///a")]
        [InlineData("https://someone@photos.example.org/a")]
        [InlineData("https://someone:secret@photos.example.org/a")]
        [InlineData("https://@photos.example.org/a")]
        [InlineData("https://photos.example.org@evil.example/a")]
        [InlineData("https://photos.example.org/a b")]
        [InlineData("https://photos.example.org/a\nb")]
        [InlineData(" https://photos.example.org/a")]
        [InlineData("https://photos.example.org/a\t")]
        public void AnythingElse_IsRefused(string? url)
        {
            Assert.NotNull(ExternalLinkRules.UrlError(url));

            // DisplayHost trims first (as a store does), so only a link refused after trimming has no host.
            if (url == url?.Trim())
            {
                Assert.Null(ExternalLinkRules.DisplayHost(url));
            }
        }

        [Fact]
        public void AnAtSignAfterTheHost_IsNotAUserInfoPart()
        {
            Assert.Null(ExternalLinkRules.UrlError("https://photos.example.org/album/@someone"));
            Assert.Null(ExternalLinkRules.UrlError("https://photos.example.org/a?mail=a@b.example"));
            Assert.Equal("photos.example.org", ExternalLinkRules.DisplayHost("https://photos.example.org/album/@someone"));
        }

        [Fact]
        public void TheLength_IsAtMost2000_AfterTrimming()
        {
            var longest = "https://photos.example.org/" + new string('x', 2000 - 27);
            Assert.Equal(2000, longest.Length);

            Assert.Null(ExternalLinkRules.UrlError(longest));
            Assert.NotNull(ExternalLinkRules.UrlError(longest + "x"));

            // Outer spaces do not count: they are trimmed before the check.
            Assert.Equal(longest, ExternalLinkRules.NormaliseUrl("  " + longest + " \n"));
            Assert.Null(ExternalLinkRules.UrlError(ExternalLinkRules.NormaliseUrl("  " + longest + " ")));
        }

        [Fact]
        public void TheLabel_IsOptional_Trimmed_And1To60Characters()
        {
            Assert.Null(ExternalLinkRules.LabelError(null));
            Assert.Null(ExternalLinkRules.LabelError("Day 1"));
            Assert.Null(ExternalLinkRules.LabelError(new string('l', 60)));
            Assert.NotNull(ExternalLinkRules.LabelError(new string('l', 61)));
            Assert.NotNull(ExternalLinkRules.LabelError("   "));

            Assert.Null(ExternalLinkRules.NormaliseLabel(null));
            Assert.Null(ExternalLinkRules.NormaliseLabel("   "));
            Assert.Equal("Day 1", ExternalLinkRules.NormaliseLabel("  Day 1 "));
        }

        [Fact]
        public void AList_IsCheckedInOrder_AndTheFirstBadLinkIsNamed()
        {
            Assert.Null(ExternalLinkRules.Validate([]));
            Assert.Null(ExternalLinkRules.Validate([("https://photos.example.org/a", null), (" https://photos.example.org/b ", "  ")]));

            var bad = ExternalLinkRules.Validate(
            [
                ("https://photos.example.org/a", "Day 1"),
                ("https://photos.example.org/b", new string('l', 61)),
                ("http://photos.example.org/c", null),
            ]);

            Assert.Equal(new ExternalLinkError(1, ExternalLinkField.Label, bad!.Message), bad);
            Assert.Equal(ExternalLinkField.Url, ExternalLinkRules.Validate([("https://photos.example.org/a", null), ("file:///x", null)])!.Field);

            // Ten links are fine; eleven are refused as a whole.
            var ten = Enumerable.Repeat<(string?, string?)>(("https://photos.example.org/a", null), 10).ToList();
            Assert.Null(ExternalLinkRules.Validate(ten));
            var eleven = ExternalLinkRules.Validate([.. ten, ("https://photos.example.org/a", null)]);
            Assert.Equal(ExternalLinkField.Count, eleven!.Field);
            Assert.Null(eleven.Index);
        }

        [Fact]
        public void TheHostShown_IsPunycode_LowerCase_SoALookAlikeCannotPassForAnotherHost()
        {
            Assert.Equal("photos.example.org", ExternalLinkRules.DisplayHost("https://Photos.EXAMPLE.org/album"));

            // A Cyrillic "а" in place of the Latin "a": shown as punycode, never as "apple.example".
            var lookAlike = "https://\u0430pple.example/album";
            Assert.Null(ExternalLinkRules.UrlError(lookAlike));
            Assert.Equal("xn--pple-43d.example", ExternalLinkRules.DisplayHost(lookAlike));
            Assert.NotEqual("apple.example", ExternalLinkRules.DisplayHost(lookAlike));

            // The port, path, query and fragment are never part of the host shown.
            Assert.Equal("photos.example.org", ExternalLinkRules.DisplayHost("https://photos.example.org:8443/a?b#c"));
            Assert.Null(ExternalLinkRules.DisplayHost("javascript:alert(1)"));
        }

        [Fact]
        public void OwnerKinds_HaveOneStoredSpellingEach()
        {
            Assert.Equal("Entry", ExternalLinkRules.OwnerKindText(ExternalLinkOwnerKind.Entry));
            Assert.Equal("NotedDate", ExternalLinkRules.OwnerKindText(ExternalLinkOwnerKind.NotedDate));
            Assert.True(ExternalLinkRules.TryParseOwnerKind("NotedDate", out var kind));
            Assert.Equal(ExternalLinkOwnerKind.NotedDate, kind);
            Assert.False(ExternalLinkRules.TryParseOwnerKind("entry", out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => ExternalLinkRules.OwnerKindText((ExternalLinkOwnerKind)7));
        }
    }
}
