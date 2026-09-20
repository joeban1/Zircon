using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace IconExport
{
    /// <summary>
    /// Pulls every item icon out of the game client's sprite archive and writes them as PNGs for
    /// the status page to serve.
    ///
    /// Why this exists: the page renders a bag as a grid of cells, and a cell is far more readable
    /// as the item's own artwork than as three initials on a coloured tile. The artwork lives in
    /// the client's ZL2 archive, which nothing in the bot can read.
    ///
    /// Why it is a separate tool: it needs RenderingCore.dll from the client install, System.Drawing
    /// and BCnEncoder. The bot must not carry any of that - it runs unattended for hours and would
    /// gain a way to fail at startup in exchange for something it uses once.
    ///
    /// Measured: 8,590 image slots, 2,370 real payloads, about two seconds, no failures.
    /// </summary>
    internal static class Program
    {
        private const string ArchiveName = "Storeitem.Zl";

        private static int Main(string[] args)
        {
            string client = Arg(args, "--client")
                ?? @"G:\Program Files (x86)\Mir3\Home Server\Client";
            string output = Arg(args, "--out")
                ?? @"G:\Program Files (x86)\Mir3\MirBot\memory\icons";

            string archive = Path.Combine(client, "Data", ArchiveName);
            string rendering = Path.Combine(client, "RenderingCore.dll");

            if (!File.Exists(archive))
            {
                Console.WriteLine($"No archive at {archive}");
                return 1;
            }

            if (!File.Exists(rendering))
            {
                Console.WriteLine($"No RenderingCore.dll at {rendering}");
                return 1;
            }

            Console.WriteLine($"archive : {archive}");
            Console.WriteLine($"output  : {output}");

            try
            {
                Export(rendering, archive, output);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed: " + ex);
                return 1;
            }
        }

        private static void Export(string rendering, string archive, string output)
        {
            Assembly asm = Assembly.LoadFrom(rendering);

            Type libType = asm.GetType("Shared.Rendering.MirLibrary");
            Type imgType = asm.GetType("Shared.Rendering.MirImage");
            Type entryType = asm.GetType("Shared.Envir.Zl2Entry");

            if (libType == null || imgType == null || entryType == null)
                throw new InvalidOperationException(
                    "RenderingCore does not expose the expected library types - the client may " +
                    "have changed format. See ICON-EXTRACTION-SPIKE.md.");

            Stopwatch opening = Stopwatch.StartNew();

            object library = Activator.CreateInstance(libType, archive);
            libType.GetMethod("ReadLibrary").Invoke(library, null);

            opening.Stop();

            Array images = (Array)libType.GetField("Images").GetValue(library);

            // The payload table is private, but Zl2Entry itself is public. An image's Position is
            // the Id of its payload, NOT a file offset - and payloads are deduplicated, so 8,590
            // image slots share about 2,370 of them.
            IDictionary entries = (IDictionary)libType
                .GetField("_zl2Entries", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(library);

            if (entries == null)
                throw new InvalidOperationException("Could not reach the ZL2 payload table.");

            Console.WriteLine($"opened in {opening.ElapsedMilliseconds} ms: " +
                              $"{images.Length:N0} image slots, {entries.Count:N0} payloads");

            FieldInfo fPosition = imgType.GetField("Position");
            FieldInfo fWidth = imgType.GetField("Width");
            FieldInfo fHeight = imgType.GetField("Height");
            FieldInfo fCodec = imgType.GetField("ImageCodec");

            FieldInfo eOffset = entryType.GetField("Offset");
            FieldInfo eCompressed = entryType.GetField("CompressedSize");
            FieldInfo eUncompressed = entryType.GetField("UncompressedSize");
            FieldInfo eCompression = entryType.GetField("Compression");

            // Static, non-public, and pure CPU: they hand back plain BGRA with no graphics device
            // anywhere in sight. That is the whole reason this is possible headlessly.
            MethodInfo decodeBc7 = imgType.GetMethod("DecodeBc7Bgra",
                BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo decodePng = imgType.GetMethod("DecodePngBgra",
                BindingFlags.NonPublic | BindingFlags.Static);

            Directory.CreateDirectory(output);

            int written = 0, empty = 0, failed = 0;
            Stopwatch watch = Stopwatch.StartNew();

            using (FileStream stream = File.OpenRead(archive))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                for (int index = 0; index < images.Length; index++)
                {
                    object image = images.GetValue(index);
                    if (image == null) { empty++; continue; }

                    int position = (int)fPosition.GetValue(image);
                    short width = (short)fWidth.GetValue(image);
                    short height = (short)fHeight.GetValue(image);

                    // A slot with no payload or no size is a hole in the archive, not a failure.
                    if (position < 0 || width <= 0 || height <= 0 || !entries.Contains(position))
                    {
                        empty++;
                        continue;
                    }

                    object entry = entries[position];

                    long offset = (long)eOffset.GetValue(entry);
                    int compressed = (int)eCompressed.GetValue(entry);
                    int uncompressed = (int)eUncompressed.GetValue(entry);
                    string compression = eCompression.GetValue(entry).ToString();

                    stream.Position = offset;
                    byte[] raw = reader.ReadBytes(compressed);

                    byte[] payload = compression == "None" ? raw : Inflate(raw, uncompressed);

                    try
                    {
                        object[] call;
                        byte[] bgra;

                        if (fCodec.GetValue(image).ToString() == "Png")
                        {
                            call = new object[] { payload, null };
                            bgra = (byte[])decodePng.Invoke(null, call);
                        }
                        else
                        {
                            call = new object[] { payload, width, height, null };
                            bgra = (byte[])decodeBc7.Invoke(null, call);
                        }

                        Save(bgra, (Size)call[call.Length - 1],
                            Path.Combine(output, index + ".png"));

                        written++;
                    }
                    catch (Exception ex)
                    {
                        failed++;

                        if (failed <= 5)
                            Console.WriteLine($"  image {index}: " +
                                (ex.InnerException ?? ex).Message);
                    }
                }
            }

            watch.Stop();

            Console.WriteLine($"wrote {written:N0}, {empty:N0} empty slot(s), {failed} failed " +
                              $"in {watch.ElapsedMilliseconds:N0} ms");
        }

        /// <summary>
        /// Decompress a payload, trying the two stream formats the container plausibly uses.
        ///
        /// The container names its compression in the entry but not the framing, and zlib and raw
        /// deflate differ only by a two-byte header - so rather than guess from a name, try the
        /// likely one first and fall back. Returning the input unchanged is the right answer for
        /// anything already stored raw.
        /// </summary>
        private static byte[] Inflate(byte[] data, int expected)
        {
            using MemoryStream input = new MemoryStream(data);
            using MemoryStream output = new MemoryStream(expected > 0 ? expected : data.Length * 4);

            foreach (bool zlib in new[] { true, false })
            {
                input.Position = 0;
                output.SetLength(0);

                try
                {
                    using (Stream decompress = zlib
                               ? new ZLibStream(input, CompressionMode.Decompress, true)
                               : new DeflateStream(input, CompressionMode.Decompress, true))
                        decompress.CopyTo(output);

                    return output.ToArray();
                }
                catch (Exception) { /* try the other framing */ }
            }

            return data;
        }

        private static void Save(byte[] bgra, Size size, string file)
        {
            using Bitmap bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);

            BitmapData bits = bitmap.LockBits(new Rectangle(0, 0, size.Width, size.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            // Format32bppArgb is BGRA in memory on little-endian, which is exactly what the
            // decoder produced - so this is a straight copy, not a conversion.
            Marshal.Copy(bgra, 0, bits.Scan0, Math.Min(bgra.Length, bits.Stride * size.Height));

            bitmap.UnlockBits(bits);
            bitmap.Save(file, ImageFormat.Png);
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];

            return null;
        }
    }
}
