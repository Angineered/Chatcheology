using System.Runtime.CompilerServices;

// The write-facing parts of the viewer are internal on purpose: WorkspaceSession is the only thing
// that holds the read-only connection, DecisionCoordinator is the only thing that closes it around
// a Phase 7 write, and MediaPresentation is the only thing that opens a recovered file. None of
// them is a supported surface for anything outside this application.
//
// Their tests still have to reach them, because the properties worth pinning — that a decision
// closes and reopens exactly once, that a refusal is not retried, that a decoded preview leaves no
// handle on the file — are properties of those types rather than of anything they expose.
[assembly: InternalsVisibleTo("Chatcheology.Desktop.Tests")]
