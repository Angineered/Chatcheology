namespace Chatcheology.Core.Tests.Importing
{
    /// <summary>
    /// Foundation test only. Proves that the test project executes tests and that the
    /// synthetic fixture is available at runtime. Parsing is deliberately not tested here.
    /// </summary>
    public class TestDataFixtureTests
    {
        private const string FixtureFileName = "SampleChatAndroid.txt";

        private const int ExpectedPhysicalLineCount = 6;

        [Fact]
        public void SampleChatFixture_IsCopiedToOutput_AndHasExpectedPhysicalLineCount()
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", FixtureFileName);

            Assert.True(
                File.Exists(fixturePath),
                $"Expected the synthetic fixture to be copied to the test output directory at '{fixturePath}'.");

            // ReadAllLines treats CR, LF and CRLF as line terminators, so this assertion
            // does not depend on the line endings the fixture is stored with. The count is
            // physical lines, so the continuation line of the multiline message counts
            // separately from the line that starts it.
            var lines = File.ReadAllLines(fixturePath);

            Assert.Equal(ExpectedPhysicalLineCount, lines.Length);
        }
    }
}
