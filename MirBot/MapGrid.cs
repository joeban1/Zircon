using System;
using System.Drawing;
using System.IO;

namespace MirBot
{
    /// <summary>
    /// One map's walkability, read straight out of the client's .map file.
    ///
    /// Until this existed the bot was navigating blind: it knew where it wanted to go and nothing
    /// about what stood in the way, so every approach was a greedy step towards the target with a
    /// sidestep when the step failed. That is a hill-climber, and the shape it cannot solve is
    /// exactly the common one - a wall with a gap in it. A run logged 405 consecutive approaches to
    /// a chicken standing behind such a wall.
    ///
    /// The format is the server's own (ServerLibrary/Models/Map.cs): width and height at byte 22,
    /// a 3-bytes-per-4-cells back-tile block, then 14 bytes per cell whose first byte is the flag.
    /// A cell is walkable only when the bottom two bits are both set. Reading it needs nothing from
    /// the server - the same files the client draws from are enough.
    /// </summary>
    public sealed class MapGrid
    {
        public readonly int Width;
        public readonly int Height;

        /// <summary>Flat, indexed x * Height + y - the order the file itself uses.</summary>
        private readonly bool[] _walkable;

        public string FileName { get; }

        /// <summary>
        /// Identifies this exact file, for cache validation.
        ///
        /// Size and last-write time rather than a hash: the mask is served with a long max-age, so
        /// something has to change when the map does, and hashing a 30 MB file to answer a web
        /// request would be absurd when the filesystem already knows.
        /// </summary>
        public string Version { get; private set; } = "";

        private MapGrid(string fileName, int width, int height, bool[] walkable)
        {
            FileName = fileName;
            Width = width;
            Height = height;
            _walkable = walkable;
        }

        public bool Walkable(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;

            return _walkable[x * Height + y];
        }

        public bool Walkable(Point point) => Walkable(point.X, point.Y);

        /// <summary>How much of this map can be stood on. Counted once, on first use.</summary>
        public int WalkableCount
        {
            get
            {
                if (_walkableCount >= 0) return _walkableCount;

                int count = 0;

                foreach (bool cell in _walkable)
                    if (cell) count++;

                return _walkableCount = count;
            }
        }

        private int _walkableCount = -1;

        /// <summary>
        /// The whole grid as one bit per cell, row-major (y outer, x inner) for the page's canvas.
        ///
        /// Built once and kept, because a grid never changes after Load and the largest map here is
        /// 1360x1500 - two million cells, which is 250 KB packed but several megabytes as JSON
        /// booleans. Row-major deliberately, even though the internal array is column-major: it is
        /// the order ImageData wants, so the page can walk it straight into a bitmap instead of
        /// transposing two million times in JavaScript.
        ///
        /// Encoded on first request and cached. The HTTP loop is single-threaded, so paying this
        /// once is fine and paying it per request would stall every other poll behind it.
        /// </summary>
        public string PackedMask
        {
            get
            {
                if (_packed != null) return _packed;

                lock (_packLock)
                {
                    if (_packed != null) return _packed;

                    byte[] bits = new byte[(Width * Height + 7) / 8];

                    for (int y = 0, i = 0; y < Height; y++)
                        for (int x = 0; x < Width; x++, i++)
                            if (_walkable[x * Height + y]) bits[i >> 3] |= (byte)(1 << (i & 7));

                    return _packed = Convert.ToBase64String(bits);
                }
            }
        }

        private string _packed;
        private readonly object _packLock = new object();

        /// <summary>Loads a .map file, or returns null if it is missing or malformed.</summary>
        public static MapGrid Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            string version;

            try
            {
                FileInfo info = new FileInfo(path);
                version = $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}";
            }
            catch (Exception) { version = ""; }

            byte[] bytes;

            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            if (bytes.Length < 28) return null;

            int width = bytes[23] << 8 | bytes[22];
            int height = bytes[25] << 8 | bytes[24];

            if (width <= 0 || height <= 0) return null;

            // The back-tile block is three bytes for every four cells, then the cell records begin.
            long offset = 28L + (long)width * height / 4 * 3;
            long needed = offset + (long)width * height * 14;

            // A truncated file would otherwise throw deep inside the loop, on a bot thread.
            if (needed > bytes.Length) return null;

            bool[] walkable = new bool[width * height];

            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                {
                    byte flag = bytes[offset + (x * height + y) * 14];

                    walkable[x * height + y] = (flag & 0x01) == 0x01 && (flag & 0x02) == 0x02;
                }

            return new MapGrid(Path.GetFileNameWithoutExtension(path), width, height, walkable)
            {
                Version = version
            };
        }
    }
}
