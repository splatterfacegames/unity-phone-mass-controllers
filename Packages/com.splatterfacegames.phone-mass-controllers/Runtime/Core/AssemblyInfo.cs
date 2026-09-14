using System.Runtime.CompilerServices;

// The pure-C# NUnit suite in tests/dotnet exercises internals (pid probes, quic-error parsing)
// without widening the public surface.
[assembly: InternalsVisibleTo("Splatter.Pmc.Core.Tests")]
