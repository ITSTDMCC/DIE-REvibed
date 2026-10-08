using System;
using System.IO;
using System.Text;

namespace EpidemicServer.Match
{
    /// <summary>
    /// A write-only stream handed to the game's own logger
    /// (StunCore.StunLog.AddOutputStream), so the game's log lines, including
    /// its error messages, reach our log.
    /// </summary>
    public sealed class GameLogStream : Stream
    {
        private readonly Action<string> _line;
        private readonly StringBuilder _pending = new StringBuilder();

        public GameLogStream(Action<string> line) { _line = line; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_pending)
            {
                foreach (char ch in Encoding.UTF8.GetString(buffer, offset, count))
                {
                    if (ch == '\n')
                    {
                        string text = _pending.ToString().TrimEnd('\r');
                        _pending.Length = 0;
                        if (text.Trim().Length > 0) _line(text);
                    }
                    else if (ch != '\0') _pending.Append(ch);
                }
            }
        }

        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { return 0; } }
        public override long Position { get { return 0; } set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }
}
