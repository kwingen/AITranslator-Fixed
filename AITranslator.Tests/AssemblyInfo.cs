global using System.IO;
using Xunit;

// Translation tasks use a process-global view model and relative managed paths.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
