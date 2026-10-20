using System.Runtime.CompilerServices;

// The tests assert on the writer's internal compaction statements (temp_store before VACUUM), which
// have no observable effect a test could check from outside without watching the temporary folder.
[assembly: InternalsVisibleTo("Chatcheology.Archive.Tests")]
