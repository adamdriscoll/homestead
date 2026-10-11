# Image upload validation alternatives

Research date: 2026-10-11. Scope and acceptance remain in
[issue #8](https://github.com/PoshTools/homestead/issues/8).
The investigation was followed by an executable compatibility prototype and
upload regression tests. The results below describe the tested corpus, rather
than guaranteeing identical behavior for every possible malformed image.

## Existing requirement

[`MediaService`](../../src/Homestead/Features/Media/MediaService.cs) uses ImageSharp
only to detect format, identify dimensions, and decode an uploaded image to reject
damaged content. It preserves the original bytes and performs no resizing or
encoding. The contract includes matching extension/MIME/detected JPEG, PNG, or
WebP before decoding, a 20 MiB input limit, 40 million pixels, one decoded frame,
and `SkipMetadata = true`. Post media and branding share this path. Issue #8
explicitly requires these safeguards to survive replacement.

## Prototype result

The [executable prototype](https://github.com/PoshTools/homestead/tree/codex/skiasharp-prototype/tests/prototypes/SkiaSharpUpload)
compares ImageSharp 3.1.12 with a bounded metadata filter and strict SkiaSharp
frame-zero decoding. Its generated PNG, JPEG, noninterleaved JPEG, palette/16-bit/
Adam7 PNG, PNG/WebP animation, and lossy/lossless WebP fixtures pass; independent
Pillow progressive and CMYK JPEG fixtures also pass. Deliberately malformed
ICC/EXIF/text bytes are absent from the copy passed to the native codec.

`ImageUploadValidator` now implements that approach in the application, with
independent checked-in fixtures in `tests/Homestead.Tests/Fixtures/Images`.
Regression tests cover exact 20 MiB and 40-million-pixel boundaries, oversized
and overflowed headers, matching format/extension/MIME before codec creation,
original-byte storage for both post and branding uploads, and rejection without
media records or files. Damaging a later animation frame leaves frame-zero
validation successful; requesting that later frame from Skia fails.

The candidate rejects all truncated prefixes in the test corpus. The old decoder
accepted some prefixes missing final markers or later animation data, and its
3 x 4 Adam7 encoder generated one file rejected by both Skia and Pillow. A valid
16 x 16 Adam7 fixture succeeds. This is stricter damaged-container handling,
not proof that two decoders classify every malformed bitstream identically.
Stored originals, published snapshots, and existing media are unchanged.

## Candidates

| Option | Distribution and deployment | Assessment |
| --- | --- | --- |
| SkiaSharp | Current stable NuGet version **4.153.1**, published 2026-09-30; MIT; includes a .NET 10 target. Windows native assets are dependencies of the main package. Linux needs a matching native-assets package. | Best initial candidate: one maintained .NET API for the three formats, but it adds native code and has no exposed metadata-skip switch. |
| Magick.NET | Current stable `Magick.NET-Q8-AnyCPU` **15.0.0**, published 2026-10-10; Apache-2.0; includes .NET 10 and statically linked ImageMagick binaries. | Capable alternative with read-time profile controls, but a much broader image engine and unproven metadata/frame parity. |
| System.Drawing.Common | Microsoft supports it only on Windows in .NET 6 and later. | Unsuitable for Homestead's Linux Docker deployment. |
| Direct native codecs | libjpeg-turbo, libpng, and libwebp provide separate decoding APIs. | More control over metadata; also three native dependencies, interop surfaces, and packaging/update responsibilities. |
| Header-only or browser checks | Headers can identify dimensions; browser APIs can decode for upload feedback. | They cannot replace the current server-side full decode requirement. |

Versions, package licenses, target frameworks, and publication dates are from
[SkiaSharp's NuGet listing](https://www.nuget.org/packages/SkiaSharp/4.153.1) and
[Magick.NET's NuGet listing](https://www.nuget.org/packages/Magick.NET-Q8-AnyCPU/15.0.0).
These permissive package licenses still require their notices; native component
licenses also apply. Recent releases establish active
release activity, not freedom from vulnerabilities.

The [SkiaSharp deployment guide](https://github.com/mono/SkiaSharp/blob/main/documentation/dev/packages.md)
recommends `SkiaSharp.NativeAssets.Linux.NoDependencies` for minimal containers.
It omits fontconfig and ships glibc/musl variants; system C libraries remain
required. The application references its managed/native pair at 4.153.1 and
publishes license and native third-party notices. Magick.NET's
[cross-platform guide](https://github.com/dlemstra/Magick.NET/blob/15.0.0/docs/CrossPlatform.md)
describes statically linked Linux builds and fontconfig requirements; Magick.NET
was researched but not restored or executed.
Microsoft's [.NET documentation](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/system-drawing-common-windows-only)
rules out System.Drawing.Common; its temporary Unix switch was removed in .NET 7.

## SkiaSharp validation details

[`SKCodec` 4.153.1 source](https://github.com/mono/SkiaSharp/blob/v4.153.1/binding/SkiaSharp/SKCodec.cs)
exposes `EncodedFormat`, `Info`, and `GetPixels`. These allow dimension checks
before allocating the pixel buffer and explicit handling of decode results.
[`SKCodecOptions`](https://github.com/mono/SkiaSharp/blob/v4.153.1/binding/SkiaSharp/Definitions.cs)
selects a frame index (default zero), but has no metadata-skip member.
[`SKBitmap.Decode`](https://github.com/mono/SkiaSharp/blob/v4.153.1/binding/SkiaSharp/SKBitmap.cs)
and `SKCodec.Pixels` both accept `IncompleteInput` as well as `Success`.
Microsoft documents that [IncompleteInput generates a partial image](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skcodecresult).
Validation must call `GetPixels` directly and require `Success`, with checked
dimension/allocation arithmetic and deterministic disposal of native objects.

Skia's milestone-153 sources show metadata processing during codec creation:
[JPEG](https://github.com/google/skia/blob/chrome/m153/src/codec/SkJpegCodec.cpp)
extracts EXIF orientation and creates an ICC profile;
[PNG](https://github.com/google/skia/blob/chrome/m153/src/codec/SkPngCodec.cpp)
reads an already-decompressed iCCP profile;
[WebP](https://github.com/google/skia/blob/chrome/m153/src/codec/SkWebpCodec.cpp)
extracts ICCP and parses EXIF orientation. Supplying an output color space or
ignoring orientation does not establish that metadata was never parsed. These
upstream branch sources are evidence of the decoder design; the exact native
commit/build shipped in a selected package still needs verification.

Strict `Success` is necessary but does not prove identical corruption rejection
across decoders. Metadata filtering must not repair damaged pixel/container data,
discard required color/alpha data, decode later frames, or change stored originals.
First-frame validation does not validate every later frame of an animation.

## Other approaches and limitations

ImageMagick documents [`profile:skip=*`](https://imagemagick.org/defines/) for
read-time profile skipping. Its [PNG source](https://github.com/ImageMagick/ImageMagick/blob/main/coders/png.c)
can discard iCCP before `png_read_info`, but still handles text chunks.
Its [JPEG source](https://github.com/ImageMagick/ImageMagick/blob/main/coders/jpeg.c)
can install the ICC reader despite skipping profiles when UHDR detection is
enabled. Its [WebP source](https://github.com/ImageMagick/ImageMagick/blob/main/coders/webp.c)
extracts ICC, EXIF, and XMP profiles. Therefore the generic define alone does not
prove ImageSharp-equivalent `SkipMetadata` behavior for all three formats.
Stripping profiles after reading is too late to establish that boundary.

Direct native decoding is feasible: [libjpeg-turbo documents its APIs](https://www.libjpeg-turbo.org/Documentation/Documentation),
[libpng provides its reference library](https://libpng.sourceforge.io/index.html),
and [libwebp documents separate header and pixel decode functions](https://developers.google.com/speed/webp/docs/api).
The distinction matters: `WebPGetInfo` validates the header and retrieves size;
it does not perform a full pixel decode. A bespoke header parser has the same
limitation. Browser decoding may improve feedback, but direct HTTP uploads can
bypass client checks, so it cannot enforce the server contract.

ImageSharp runtime and test references and their five package-specific audit
suppressions have been removed. Those ImageSharp advisory paths disappear with
the dependency; they were not repaired in ImageSharp itself. Native codec audits,
resource behavior, and updates still need attention when changing this boundary.
Validation evidence and scope remain recorded in issue #8.
