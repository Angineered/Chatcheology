using Xunit;

// These tests share two process-wide resources that no test owns, and each one breaks a correct
// test now and then when test classes run in parallel.
//
// The first is WPF's shared XAML schema. Four classes build the real MainWindow, each on its own
// STA thread. Loading its BAML fills a member cache that every thread in the process shares, and
// WPF creates that cache lazily without a lock: when two threads load the window for the first
// time at once, one can add a member to a cache the other has just replaced, then look it up in
// the replacement and fail with "The given key 'SelectedItem' was not present in the dictionary".
//
// The second is Microsoft.Data.Sqlite's connection pool. SqliteConnection.ClearAllPools is
// global, so one test clearing the pool before deleting its workspace can dispose a connection
// another test is still using, which surfaces as an ObjectDisposedException.
//
// Running this assembly's tests one at a time removes both races, as it does for
// Chatcheology.Data.Tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
