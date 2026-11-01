using System;
using System.IO;
using System.Text;
using System.Threading;

namespace dnSpy.Mcp.Scripting {
	/// <summary>
	/// Collects text written by a script. Bounded so a runaway loop can't exhaust memory.
	/// </summary>
	public sealed class CapturedOutput {
		readonly StringBuilder sb = new StringBuilder();
		readonly int maxChars;
		long droppedChars;

		public CapturedOutput(int maxChars) => this.maxChars = maxChars;

		public void Append(string? text) {
			if (string.IsNullOrEmpty(text))
				return;
			lock (sb) {
				int room = maxChars - sb.Length;
				if (room >= text.Length)
					sb.Append(text);
				else {
					if (room > 0)
						sb.Append(text, 0, room);
					droppedChars += text.Length - Math.Max(room, 0);
				}
			}
		}

		public long DroppedChars {
			get {
				lock (sb)
					return droppedChars;
			}
		}

		public override string ToString() {
			lock (sb)
				return sb.ToString();
		}
	}

	/// <summary>
	/// Redirects Console.Out/Console.Error to the capture buffer of the script that is running
	/// on the current async flow. Writes from anything else go to the original writer.
	/// </summary>
	static class OutputCapture {
		static readonly AsyncLocal<CapturedOutput?> current = new AsyncLocal<CapturedOutput?>();
		static int installed;

		public static CapturedOutput? Current {
			get => current.Value;
			set => current.Value = value;
		}

		public static void Install() {
			if (Interlocked.Exchange(ref installed, 1) != 0)
				return;
			Console.SetOut(new RoutingWriter(Console.Out));
			Console.SetError(new RoutingWriter(Console.Error));
		}

		sealed class RoutingWriter : TextWriter {
			readonly TextWriter fallback;

			public RoutingWriter(TextWriter fallback) => this.fallback = fallback;

			public override Encoding Encoding => Encoding.UTF8;

			public override void Write(char value) {
				if (current.Value is CapturedOutput c)
					c.Append(value.ToString());
				else
					fallback.Write(value);
			}

			public override void Write(string? value) {
				if (current.Value is CapturedOutput c)
					c.Append(value);
				else
					fallback.Write(value);
			}

			public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

			public override void WriteLine(string? value) => Write(value + Environment.NewLine);
		}
	}
}
