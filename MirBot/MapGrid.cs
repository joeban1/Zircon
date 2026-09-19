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

        /// <summary>Loads a .map file, or returns null if it is missing or malformed.</summary>
        public static MapGrid Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

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

            return new MapGrid(Path.GetFileNameWithoutExtension(path), width, height, walkable);
        }
    }
}
