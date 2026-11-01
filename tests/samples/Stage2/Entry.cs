// Benign test fixture for dnSpy.Mcp. Stage 2 only prints a line.
using System;

namespace Stage2 {
	public static class Entry {
		public static readonly string Host = "stage2.example.invalid";
		public const int Port = 8443;

		public static void Run() => Console.WriteLine($"stage2 fixture (would talk to {Host}:{Port})");
	}
}
