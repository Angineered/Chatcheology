using System.Runtime.CompilerServices;

// The Stage B2C-1 alignment work reads an internal per-pair snapshot of the two direction sequences
// the B2C-0 gate builds, and its equivalence tests have to assert on that snapshot directly: the whole
// point of them is that B2C-1 reads the same streams the accepted gate produced rather than a second
// construction of them.
//
// The alternative was making the snapshot part of this assembly's public API, which would publish the
// raw outgoing/incoming streams as a supported type. The privacy rule for those streams is that they
// stay in memory and never reach permanent evidence, and neither choice changes that — but a public
// type invites a caller this stage has no reason to have, so the test surface is widened instead of
// the product surface. Decided deliberately for Stage B2C-1.
[assembly: InternalsVisibleTo("Chatcheology.Data.Tests")]
