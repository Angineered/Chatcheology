namespace Chatcheology.Archive.Tests
{
    /// <summary>
    /// CH-48: the shared external-link rules (store, writer, validator, phone and desktop) and the host a
    /// viewer shows. Invented addresses only; nothing is fetched.
    /// </summary>
    public sealed class ExternalLinkRulesTests
    {
        /// <summary>Every address the rule tests expect to be kept.</summary>
        public static readonly TheoryData<string> Accepted = new()
        {
            "https://photos.example.org/album/abc",
            "HTTPS://photos.example.org/album/abc",
            "HTTPS://PHOTOS.EXAMPLE.ORG/x",
            "https://photos.example.org",
            "https://photos.example.org/",
            "https://photos.example.org:8443/a?b=c&d=e#f",
            "https://xn--pple-43d.example/a",
            "https://\u0430pple.example/album",
            "https://b\u00fccher.example/a",
            "https://photos-1.example.org/a",
            "https://photos.example.org/album/@someone",
            "https://photos.example.org/a?mail=a@b.example",
            "https://photos.example.org/a?x=\uFF20#\u2047",
            "https://photos.example.org/" + new string('x', 2000 - 27),
        };

        [Theory]
        [MemberData(nameof(Accepted))]
        public void AnAbsoluteHttpsLinkWithAHost_IsKept(string url)
        {
            Assert.Null(ExternalLinkRules.UrlError(url));
            Assert.NotNull(ExternalLinkRules.DisplayHost(url));
            Assert.NotNull(ExternalLinkRules.LaunchUri(url));
        }

        [Fact]
        public void DisplayHost_NeverThrows_ForAnyAcceptedLink_AndShowsPlainAscii()
        {
            foreach (var url in ((IEnumerable<object[]>)Accepted).Select(row => (string)row[0]))
            {
                Assert.Null(ExternalLinkRules.UrlError(url));

                var host = ExternalLinkRules.DisplayHost(url);

                Assert.False(string.IsNullOrEmpty(host));
                Assert.All(host, c => Assert.True(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.', url));
            }
        }

        /// <summary>
        /// Hosts with characters a browser may map to ASCII punctuation (full-width @, /, #, ?, small @, one-dot
        /// leader, digit full stop, squared and letterlike symbols) or that the IDN host cannot be computed for.
        /// </summary>
        [Theory]
        [InlineData("https://photos.google.com\uFF20evil.com/")]
        [InlineData("https://evil.com\uFF0Fphotos.google.com")]
        [InlineData("https://evil.com\uFF03.google.com")]
        [InlineData("https://evil.com\uFE6Bphotos.google.com/")]
        [InlineData("https://evil\u2488com/")]
        [InlineData("https://photos.google.co\u33C2/")]
        [InlineData("https://evil\uFE52com/")]
        [InlineData("https://evil\u2024com/")]
        [InlineData("https://a\u2100b/")]
        [InlineData("https://evil.com\uFF1Fx/")]
        [InlineData("https://evil.com\u2047/")]
        [InlineData("https://evil.com\\photos.google.com/")]
        [InlineData("https://evil_host.example/")]
        [InlineData("https://evil.example:/")]
        [InlineData("https://evil.example:443:1/")]
        [InlineData("https://evil.example:12a/")]
        [InlineData("https://[::1]/")]
        [InlineData("https://:443/")]
        public void AHostACharacterCouldDisguise_IsRefused(string url)
        {
            Assert.NotNull(ExternalLinkRules.UrlError(url));
            Assert.Null(ExternalLinkRules.DisplayHost(url));
            Assert.Null(ExternalLinkRules.LaunchUri(url));
        }

        [Theory]
        [InlineData("https://photos.example.org/\u202Egpj.exe")]
        [InlineData("https://photos.example.org/\u202Da")]
        [InlineData("https://photos.example.org/\u200Ea")]
        [InlineData("https://photos\u200B.example.org/a")]
        [InlineData("https://photos.example.org/a\uFEFF")]
        [InlineData("https://photos.example.org/\u2066a\u2069")]
        [InlineData("https://photos.example.org/a\u00ADb")]
        [InlineData("https://photos.example.org/a\u0085b")]
        [InlineData("https://photos.example.org/a\u007F")]
        public void AnInvisibleOrControlCharacter_InALink_IsRefused(string url)
        {
            Assert.NotNull(ExternalLinkRules.UrlError(url));
            Assert.Null(ExternalLinkRules.DisplayHost(url));
            Assert.Null(ExternalLinkRules.LaunchUri(url));
        }

        [Fact]
        public void ANulCharacter_IsRefused_InALinkAndALabel()
        {
            // Not as InlineData: a NUL does not survive the test runner's serialisation of theory data.
            Assert.NotNull(ExternalLinkRules.UrlError("https://photos.example.org/a" + '\0'));
            Assert.NotNull(ExternalLinkRules.UrlError("https://photos.example.org" + '\0' + ".evil.example/a"));
            Assert.NotNull(ExternalLinkRules.LabelError("Day" + '\0' + "1"));
        }

        [Theory]
        [InlineData("Day\n1")]
        [InlineData("Day\r\n1")]
        [InlineData("Day\t1")]
        [InlineData("\u202EDay 1")]
        [InlineData("Day\u200B1")]
        [InlineData("Day 1\uFEFFx")]
        public void AnInvisibleOrControlCharacter_InALabel_IsRefused(string label)
        {
            Assert.NotNull(ExternalLinkRules.LabelError(label));
            Assert.NotNull(ExternalLinkRules.LabelError(ExternalLinkRules.NormaliseLabel(label)));
        }

        [Fact]
        public void LaunchUri_IsTheAbsoluteForm_WithALowerCaseSchemeAndPunycodeHost()
        {
            Assert.Equal("https://photos.google.com/x", ExternalLinkRules.LaunchUri("HTTPS://PHOTOS.GOOGLE.COM/x"));
            Assert.Equal("https://photos.example.org/A?B=C#D", ExternalLinkRules.LaunchUri("  https://Photos.Example.org/A?B=C#D "));
            Assert.Equal("https://photos.example.org/", ExternalLinkRules.LaunchUri("https://photos.example.org"));
            Assert.Equal("https://photos.example.org:8443/a", ExternalLinkRules.LaunchUri("https://photos.example.org:8443/a"));
            Assert.Equal("https://photos.example.org/a", ExternalLinkRules.LaunchUri("https://photos.example.org:443/a"));
            Assert.Equal("https://xn--pple-43d.example/album", ExternalLinkRules.LaunchUri("https://\u0430pple.example/album"));

            Assert.Null(ExternalLinkRules.LaunchUri(null));
            Assert.Null(ExternalLinkRules.LaunchUri("http://photos.example.org/a"));
            Assert.Null(ExternalLinkRules.LaunchUri("javascript:alert(1)"));
            Assert.Null(ExternalLinkRules.LaunchUri("https://someone@photos.example.org/a"));
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
