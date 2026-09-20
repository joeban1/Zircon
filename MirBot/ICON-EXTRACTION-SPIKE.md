# Item icons: spike result

**Question:** can a headless console tool decode item icons out of the live client's sprite
archive, so the status page can show real artwork instead of coloured tiles?

**Answer: yes. The whole archive extracts in two seconds, with no failures.**

Everything below was established by running against the real client, not by reading code.

## The numbers

```
archive opened in 9 ms: 8,590 image slots, 2,370 payload entries
wrote 2370, skipped 6220, failed 0 in 1,987 ms
0.84 ms per icon
9.2 MB of PNGs on disk
```

The 6,220 "skipped" are empty slots in the archive - `Position -1`, zero width. **Coverage was
checked against reality rather than assumed:** of the 47 distinct item images the four running bots
were carrying, wearing or storing at the time, **47 had icons and none were missing.**

This is a one-off. Rerun it only when the client's `Data` folder is updated.

## How it works

`ItemInfo.Image` is a zero-based index into `Client\Data\Storeitem.Zl`. Two special cases an
extractor must mirror: currency items pick a **count-dependent** sprite rather than `Info.Image`,
and `ItemEffect.ItemPart` items resolve their info via `AddedStats[Stat.ItemIndex]` first.

`RenderingCore.dll` ships beside the client, targets `net10.0`, and loads and runs headlessly from
a `net10.0-windows` console app. 88 types resolve; 5 fail (graphics pipelines that are never
touched). `<UseWindowsForms>true</UseWindowsForms>` is needed only so reflection can walk the type
list without tripping over a WinForms-derived type.

The pipeline:

1. `new MirLibrary(path)` then `ReadLibrary()` - parses header, index and metadata. 9 ms.
2. `MirLibrary.Images[]` - public. Each `MirImage` exposes `Position`, `Width`, `Height`,
   `ImageCodec`, `StoredImageDataSize` as public fields.
3. `MirLibrary._zl2Entries` - **private** `Dictionary<int, Zl2Entry>`, reached by reflection.
   `Zl2Entry` itself is public: `{ Type, Id, UncompressedSize, CompressedSize, Offset,
   Compression, Codec }`.
4. An image's `Position` is the **Id of its payload entry**, not a file offset. Payloads are
   deduplicated - 8,590 image slots share 2,370 payloads.
5. Seek to `entry.Offset`, read `CompressedSize` bytes, inflate if `Compression != None`.
6. `MirImage.DecodeBc7Bgra(byte[], short w, short h, out Size)` or `DecodePngBgra(byte[], out
   Size)` - **static, non-public, pure CPU** over BCnEncoder.Net. Returns plain BGRA. No graphics
   device, no window, no D3D.
7. Blit the BGRA into a `Format32bppArgb` bitmap and save as PNG.

## Two dead ends, recorded so nobody repeats them

**Atlas pages are a red herring for this archive.** `MirImage` reports `AtlasPage = 0` and a
`SourceRectangle`, which looks like the icons are packed into shared textures - but
`MirLibrary.AtlasPages` is **null** and `AtlasPageSize` is 0, because `GetUseZlAtlasPages` defaults
to false. The payloads are standalone. An earlier version of this note concluded the opposite and
was wrong.

**`MirImage.CreateImage(BinaryReader, Func<int, byte[]>)` is not the way in.** Both readings of its
callback (buffer allocator, payload reader) reach the BC7 decoder and fail with *"The size of the
input buffer does not align with the compression format"*. Going to the entry table directly and
calling the static decoder sidesteps it entirely.

## What is left to ship it

The extraction is proven. Remaining:

- move the throwaway probe into a proper **separate tool project** - nothing here belongs in the
  bot's runtime path or dependency tree;
- write to `memory/icons/{image}.png`;
- serve `/icons/{n}.png` from `StatusServer`, which needs a binary response path -
  `StatusServer.Send` only writes UTF-8 strings today - plus `image/png`, a long cache header and
  strict numeric filename parsing;
- the page already renders a cell per item with its `image` index, so it picks them up behind a
  `hasIcon` check with no change to the layout.

Probe source: `scratchpad/spike/iconprobe` (throwaway).
