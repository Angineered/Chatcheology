using Xunit;

// These tests share one process-wide resource that no test owns: Microsoft.Data.Sqlite's
// connection pool. Proving a workspace file is byte-identical, or deleting a temporary one, means
// calling SqliteConnection.ClearAllPools first, because a disposed connection can still hold the
// file open. That call is global — it reaches into pools belonging to whatever else is running —
// so with tests running in parallel one test clearing the pool can dispose a connection another
// test is in the middle of using, which surfaces as an ObjectDisposedException from a perfectly
// correct test.
//
// Running this assembly's tests one at a time removes the race outright. The alternative would be
// to stop proving files are untouched, which is the guarantee these tests exist for.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
