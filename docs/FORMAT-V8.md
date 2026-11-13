# DGN V8 / V8i / CONNECT file format — reverse-engineering notes

These notes document the on-disk layout DgnKit reads. They were derived from:

* MicroStation 2026 SDK headers (`DgnPlatform/DgnFileIO/DgnElements.h`, `DgnPlatform.r.h`, `DgnPlatformBaseType.r.h`, `DgnPlatform.h`) — the in-memory `MSElement` structures, which are byte-identical to the stored element records;
* byte-level inspection of 108 public DGN files (`testdata/corpus`);
* ground truth from the licensed MicroStation on the development machine (`tools/oracle/MstnOracle`, COM automation), compared field-by-field by `tests/DgnKit.Tests/OracleComparer.cs`;
* GDAL's ODA-produced reference output for `test_dgnv8.dgn`;
* iTwin/imodel-native (`ECDBuffer`, line style component model, RSC font structures) for EC/line-style/font concepts.

Every claim marked ✅ is verified against the oracle or GDAL reference; ⚠️ = plausible but unverified.

---

## 1. Container

A V8-family DGN is a Microsoft **Compound File** (OLE2 structured storage, signature `D0 CF 11 E0 A1 B1 1A E1`).

```
/Dgn~H                 file header: 20-byte prefix + zlib → ~1.5 KB (major version u32 @0x114, minor @0x118)
/Dgn~S                 session/lock info (ignored)
/Dgn~Mf                small zlib stream (model file index; ignored)
/Dgn^Nm , /Dgn^NmA     non-model ("dictionary") elements + their XAttributes
/Dgn^Ix/Dgn~Mix        element index (ignored)
/Dgn-Md/#xxxxxx/       one storage per model; name = model id in hex
      Dgn~Mh           model header (zlib)
      Dgn^C , Dgn^CA   control elements + XAttributes
      Dgn^G , Dgn^GA   graphic elements + XAttributes
/Dgn-Nd/#xxxxxx/       further models with the same layout (non-design/definition-like models)
/Dgn-Cm , .Embedded    comments / embedded files (ignored)
```
Files written by ODA libraries add `Oda~SH`.

### 1.1 Blocked element streams ✅
`Dgn^C`, `Dgn^G`, `Dgn^Nm` and the `…A` XAttribute storages contain streams `$1, $2, …` (ordered **numerically**).
Each stream = 16-byte header `{u32 count, u32 flags(2|3), u32 0, u32 0}` + zlib data. Streams of 16 bytes are empty.
Concatenating the inflated payloads gives the logical stream.

### 1.2 Element records ✅
The logical element stream is a sequence of `{u32 reserved(0); element}`. Each element starts with `Elm_hdr` and is
`elementSize` 16-bit words long.

## 2. Element header (32 bytes) ✅

| off | type | field |
|---|---|---|
| 0 | u16 | type (MSElementTypes: 2 cell, 3 line, 4 line string, 6 shape, 7 text node, 11 curve, 12 complex chain, 14 complex shape, 15 ellipse, 16 arc, 17 text, 18/19 surface/solid, 21 b-spline poles, 22 point string, 23 cone, 24 b-spline surface, 26 knots, 27 b-spline curve, 28 weights, 33 dimension, 34 shared cell def, 35 shared cell, 36 multiline, 37 tag, 38/39 DgnStore (tag sets), 66 application, 87/88 raster, 95/96 table entry/table, 97/98 view group/view, 99 level mask, 100 reference, 101/102/103/105 matrix/mesh, 106/107 extended, 108 ref override, 110/111 named group) |
| 2 | u16 | flags: bits 6-7 archive, 10 nonModel, 11 locked, **12 isGraphics**, **13 isComplexHeader**, **14 complex (component)**, 15 deleted |
| 4 | u32 | elementSize (words, whole element) |
| 8 | u32 | attrOffset (words) — linkages start here |
| 12 | u32 | level id |
| 16 | u64 | element id |
| 24 | f64 | last modified (Unix ms) |

## 3. Display header (72 bytes, graphics only) ✅

| off | type | field |
|---|---|---|
| 32 | u32 | graphic group |
| 36 | i32 | display priority |
| 40 | u16 | properties: bits 0-3 class (0 primary, 1 pattern, 2 construction, 3 dimension, 4 primary rule, 5 linear patterned, 6 construction rule), **7 invisible**, 8 dynamic range, **11 is3d**, 12 view-relative, 13 planar, 14 non-snappable, 15 hole |
| 42 | u16 | reserved |
| 44 | i32 | **style** (0..7 built-in; negative = custom line style number; `0x7FFFFFFF` ByLevel, `0x7FFFFFFE` ByCell) |
| 48 | u32 | **weight** (0..31; `0xFFFFFFFF` ByLevel, `0xFFFFFFFE` ByCell) |
| 52 | u32 | **color** (0..255 colour table; 256+i extended colour i; `0xFFFFFFFF` ByLevel, `0xFFFFFFFE` ByCell) |
| 56 | 6×i64 | stored range (UOR; ordering not consistently low/high — DgnKit recomputes ranges) |

Element body starts at **104** for graphics, **32** for non-graphic elements.

## 4. Element bodies (offsets from body start B) ✅ unless noted

* **Line (3)**: start, end — `DPoint2d`/`DPoint3d` doubles (UOR).
* **Line string / shape / curve / point string (4/6/11/22)**: `u32 numVerts, u32 reserved, points[]`.
* **Ellipse (15)**: 2D `primary, secondary, rotation(rad), origin(2d)`; 3D `primary, secondary, quat[4] (w,x,y,z), origin(3d)`.
* **Arc (16)**: `startAngle, sweepAngle` (**radians**) then as ellipse. Rotation matrix = transpose of the standard quaternion matrix (GDAL convention).
* **Text (17)**: `u32 font, u16 just, u16 numBytes, f64 lengthMult, f64 heightMult, f64 length, f64 height, rotation|quat, origin, u16 edflds, bytes[numBytes]`. Character size = mult × 6/1000. **Origin = lower-left**; MicroStation reports the justification point (see `TextElement.JustificationPoint`).
* **Text node (7)**: `u32 componentCount, u32 nodeNumber, u32 font, u16 maxLength, u16 just, f64 lineSpacing, f64 lengthMult, f64 heightMult, rotation|quat, origin`.
* **Cell (2) / shared cell def (34)**: `u32 componentCount, u16 classMap, u16 flags(bit0 annotation), rangeLow, rangeHigh, transform (2×2 or 3×3 row-major), origin`. Name/description in string linkage keys 1/2.
* **Shared cell instance (35)**: same layout as a 3D cell header with `componentCount = 0`; refers to its definition **by name** (string linkage key 1). World = origin + T·(local − defOrigin).
* **B-spline curve (27)**: `u32 componentCount, u32 flags (order-2:5, curve_display, poly_display, rational, closed, curve_type:8, … storeFullKnots @bit27), u32 numPoles, u32 numKnots`; components: poles (21, like a line string), knots (26, non-graphic doubles), weights (28). Only interior knots are stored by default.
* **Cone (23)**: `u16 type/flags, 3×u16, quat[4], center1, radius1, center2, radius2`.
* **Reference attachment (100, non-graphic, B=32)**: `componentCount, version, groupId, fileNumber @+12, u64 parentId, fb_opts @+24, fd_opts @+28 (bit1 display), priority @+32, ViewFlags[8] (12 bytes each, +36..+132), u16 baseNestDepth, u16, refOrigin(3d) @+136, masterOrigin(3d) @+160, transform 3×3 @+184, scale @+256, reservedScale[2], zFront @+280, zBack, cameraPos @+296, focal @+320, u16 nestDepth @+328, u16, i16, u16 nClip @+334, clip[](2d) @+336`. Offsets verified on V8i (WSDOT XE3649) and CONNECT (FDOT, TxDOT, WSDOT ORD) files: zFront/zBack read ±4.5e15, transforms are orthonormal, FDOT sheet scales 1/24, 1/12, … (an earlier 8-byte ViewFlags assumption put every field from refOrigin on 32 bytes early). `scale` maps reference UORs to master UORs; clip points look like master UORs relative to `masterOrigin` (FDOT: the Text/Details refs at 1/12 and 1/6 share one clip = the sheet's drawing area; in reference UORs every observed clip spans [0, 2·refOrigin]). Not checked against MicroStation, and DisplayListBuilder does not clip: on FDOT 102-600-5of11 the `Default` attachment's box ends at x = 8.5" while geometry plotted in the official PDF extends to 13.2", so some clip flag/semantics is still unknown. File/model/logical names in string linkage keys 3/21/4, full path 31.

### 4.1 Complex elements ✅
A complex header's `componentCount` is the number of **all descendant** elements that follow (nested headers included), not
just direct children. Components carry header flag bit 14.

### 4.2 Dimensions (33) ✅ (oracle: type/points/height/values/overrides/displayed text/note terminator 100% on 4 files, 225 dims)
The `DimensionElm` struct is not in the public SDK headers; this layout was reverse-engineered from the 745 V8 dimensions in
the corpus (all 2D: 461 notes, 283 SizeArrow, 1 CustomLinear) against the COM oracle (`DimensionElement.Point(i)` 1-based,
`DimHeight`, `ActualValue(seg)`, `PrimaryText(seg)`, and `Element.Drop()` = the graphics MicroStation regenerates).
MicroStation stores **definition points + style values**, never the graphics. Offsets from the body start B = 104:

| off (B+) | type | field |
|---|---|---|
| 0 | u32 | dimension style element id (⚠️) |
| 16 | f64 | distance scale (1.0) |
| 28 / 29 / 31 | u8 | **version** (11) / **dimension type** (DgnPlatform `DimensionType`: 1 SizeArrow, 2 SizeStroke, 3/4 Locate, 15 CustomLinear, 53 Note …) / **point count** |
| 32..47 | u32×4 | text flags, text colour (0x7FFFFFFF = dimension's), ?, ? (⚠️) |
| 48 / 56 | f64 | **text height / width** (UOR, already × annotation scale) |
| 64 | u32 | **font** number (≥ 1024 TrueType) |
| 80 / 81 | u8 | secondary / **primary accuracy** (DIMACC_*: 0x81..0xC0,0x80 = 1..8 decimals, 0x01..0x20 = ½..1/64 fractions) |
| 82 | u8 | **value format bits**: 0x20 show sub-units, 0x80 also show master units, 0x40 unit labels, 0x01 leading zero (⚠️). 0xE3/0xEB → `10'-6"` (oracle + PDF), 0x63 → `126"` (official PDF), 0x01/0x03 → `10.5`. Sub-unit decimals print without trailing zeros (`40'-0"` at 3 decimals). The `10' 6"` (no dash) bit is not identified |
| 88..159 | f64×9 | **geometry** (UOR): witness offset, witness extend, text lift, text margin, margin (min leader), terminator width, terminator height, stack offset, centre size |
| 160 / 164 | u32×2 | flags (⚠️) |
| 168 | f64×4 | **rotation**: 2D = 2×2 row-major (column 0 = measuring axis X, column 1 = height axis Y); 3D presumably a quaternion (⚠️ none in corpus) |
| 200 | 48 B × n | **points**: `DPoint3d point (z=0 in 2D), f64 offsetY (dimension-line height, first point), f64 offsetX (⚠️), u32 flags, u32` — flags low u16 = byte offset of the segment's override string (0xFFFF none), bit 24 = has override |

After the points, **optional blocks** `{u8 nWords (whole block), u8 type, …}` until the first byte pair that is not a
valid header: 0x0C view rotation (8-byte header + 2×2/quat), 0x0E terminators (+4 low nibble = note terminator 1 arrow,
4 dot), 0x0B (?), 0x09 primary **units** (+24/+32 master num/den, +40/+48 sub num/den, units per metre = num/den), 0x02,
0x07, 0x08, 0x0A (secondary units?), 0x12. Then the **string area**: NUL-terminated override strings at the per-point
offsets, optional `FF FE 01 00` (8-bit) / `FF FD` (UTF-16) prefix; `*` stands for the computed value. Segment *k* (points
*k−1*→*k*; for Locate types 0→*k*) uses point *k*'s string. Unit labels: string linkages 19 (master, `'`) / 20 (sub, `"`).

**Linkage 0x56DE** (`LINKAGEID_DimExtensionLinkage`), u16 sub-type @0: 0 → annotation scale (f64 @12); 6 (52 bytes) →
note settings: byte 44 low nibble = note terminator (0 style default, 4 dot), byte 45 bit 0x80 = curved leader;
5 → one **run of a rich text-block override**: u32 field mask @8 (bit 2 = run offset x,y present), u16 @12, u16 font @14,
4 (+2 when bit 2) doubles, then the run string (optional `FF FE 01 00` prefix). Runs carry one trailing separator space
(or 0x18) that is not displayed; byte (string NUL + 9) bit 0x80 = line break after the run; the first run (u16@12 = 7,
blank) is the block header. 0x56D3 bit-mask linkages (keys 100..133 `BITMASK_LINKAGE_KEY_Dim*Shields`) flag style overrides.

**Value**: |(p_end − p_start)·X| in UOR → metres → dimension master units (units block, else model master units) × scale.
**Graphics** (what `Element.Drop()` returns; implemented in `Graphics/DimensionGraphics.cs`):
* extension line per point along Y from `v + s·witnessOffset` to `height + s·witnessExtend` (s = side of the dimension line);
* dimension line at `height`; terminators (arrow = closed triangle, tip at the extension line, length = terminator width,
  base = terminator height) inside, or outside when `segment < 2·(terminatorWidth + margin) + textWidth` — then only the two
  outer stubs of 2·terminator width are drawn;
* text centred on the segment, bottom at `textLift` above the line on the reading side: text X = dimension X flipped to read
  left→right / bottom→top. Text-block lines stack upward at ≈1.3 × height. TrueType widths: em ≈ 1.397 × text size
  (cap height = size; wide fonts × `FontMetrics.WidthFactor`, see §8 "Text size");
* notes (53): leader through the points, terminator at point 1 (arrow, or dot = filled circle of radius terminator height/8);
  curved leaders arrive horizontally at the note (approximated by a quadratic Bézier). The note text is a separate cell.
Not modelled: angular/radial/ordinate kinds (only a polyline + text), stacked dims, "text with leader" fits on very short
segments, terminator symbols from fonts/cells, V7 dimensions.

## 5. Linkages ✅
From `attrOffset` to end: `u16 header {wdMantissa:8, wdExponent:4, user:1, modified:1, remote:1, info:1}`, `u16 primaryId`.
Size in words = `wdMantissa + 1` (exponent 0). Known ids: `0x56D2` keyed string, `0x56D0` tag/target links,
`0x0041` shape fill, `0x56EE` model info, `0x80D4` text attributes, `0x7D28` double array, DB linkages (`0x5E62` ODBC, `0x6091` Oracle, `0x71FB` RIS, …).

**String linkage** `0x56D2`: `u32 key, u32 byteLength, bytes`. Keys (DgnPlatform `STRING_LINKAGE_KEY_*`): 1 name, 2 description, 3 file name, 4 logical name, 19/20 master/sub unit labels, 21 model name, 31 full ref path.

**String encodings** ✅: prefix `FF FD` → UTF-16LE; prefix `FF FE 01 00` → 8-bit (UTF-8 when valid, otherwise the locale code page); no prefix → 8-bit locale text.

## 6. XAttributes ✅
Per element record: `u32 0xA11B, u32 payloadLength (excludes this 28-byte header), u32 0xAA, u32 0, u64 elementId, u32 count`,
then `count × {u16 handlerMinor, u16 handlerMajor, u32 xattrId, u32 size (bit31 flag), u32 reserved, byte[size]}`.
A payload beginning `u32 3, u32 uncompressedSize` + zlib is compressed.

Handlers seen: `0x56FF` binary EC XML instance (feature/GIS/civil properties), `0x5716` EC relationship, `0x56EC`, `0x5709` (TCB: extended colours XML), `0x5707`, `0x5874` (.NET BinaryFormatter blobs), `0x58D3`, `0x58E4`.

### 6.1 Binary EC XML ("BECXML") ✅
`… "BECXML\0" FF 0A 0D xx×6`, then `0x30 <n>` string table (`FA <len8> bytes` | `FB <len16> bytes`), then tokens:
`02/03 <s>` start element, `05 <s> 11 <s>` attribute, `06` end-of-attributes, `10 <v>` typed value, `11 <s>` string value, `04` end.
Integers/indices `< 0xF0` are literal; `F1..F4` = 1..4 little-endian bytes follow; `F5/F6` 8-byte integer; `F7` double; `F8` float.

### 6.2 Extended colours ✅
The TCB (type 9) carries a compressed UTF-16 XML XAttribute `<ExtendedColors><Entry Color="(r,g,b)" …/>…</ExtendedColors>`;
element colour `256 + i` ↦ entry `i`.

## 7. Model header (`Dgn~Mh`) ✅
zlib → 4096-byte header + `{u32 0; element}` where the element is type 66, level 32 (model element). Offsets from the element start:

| off | field |
|---|---|
| 0x24 | u32 model type word (⚠️ sheet/design not yet decoded) |
| 0x40 / 0x48 / 0x4C | unit flags (master / sub / storage): base (bits 0-2: 1 meter, 2 degree), system (bits 3-5) |
| 0x50 / 0x58 | master unit numerator / denominator (units per meter = num/den) |
| 0x60 / 0x68 | sub unit numerator / denominator |
| 0x70 / 0x78 / 0x80 | **global origin** (UOR) |
| 0x88 | last modified |
| 0x90 | model range (6 × i64) |
| 0xE0 | UORs per storage unit |
| 0xE8 / 0xF0 | storage unit numerator / denominator |
| linkages | key 1 name, 2 description, 19 master label, 20 sub label |

Master coordinate = (UOR − globalOrigin) / (UORperStorage × masterMeters / storageMeters).

### 7.1 Geographic coordinate system (GCS) ✅
MicroStation stores a model's GCS (`DgnGCS::ToModel`) as a **type 66 application element with signature 182**
(`MSGEOCOORD_SIGNATURE`, `DgnElements.h`) among the model's **control** elements (`Dgn^C`), not in the model header or
an XAttribute. The element is 766 words: header (32 bytes), `i16 signature`, `u16 0x1000`, `u32 dataBytes (1496)`, then
a fixed record derived from CS-MAP's coordinate-system parameters (8-bit NUL-padded strings, little-endian doubles).
Offsets from the element start (decoded by `Formats/V8/V8GeoCoord.cs`):

| off | type | field | `EPSG:2927` sample | `HARN.WA-NF` sample |
|---|---|---|---|---|
| 0x20 | i16 | signature | 182 | 182 |
| 0x24 | u32 | data byte count | 1496 | 1496 |
| 0x28 | u32 | ⚠️ version? | 2 | 2 |
| 0x34 / 0x3C | f64 | ellipsoid semi-major / semi-minor (m) | 6378137 / 6356752.314 | same |
| 0xB4 | char[24] | **GCS key name** | `EPSG:2927` | `HARN.WA-NF` |
| 0xCC | char[24] | CS-MAP group | `EPSGPRJ` | `SPCSHPF` |
| 0xE4 | char[24] | ⚠️ key name again | `EPSG:2927` | `HARN.WA-NF` |
| 0xFC | char[24] | projection key | `LM` | `LM` |
| 0x114 | char[64] | projection description | Lambert Conformal Conic | same |
| 0x154 | char[64] | datum description | NAD83 (High Accuracy Regional Network) | High Accuracy Regional Network, ala 48HPGN… |
| 0x194 | char[64] | ellipsoid description | GRS 1980 | Geodetic Reference System of 1980 |
| 0x1D4 | char[64] | **GCS description** | NAD83(HARN) / Washington South (ftUS) | HPGN Washington, North, US Foot |
| 0x214 | char[64] | source | EPSG V6 [Large and medium scale …] | Snyder, J.P., 1987, … |
| 0x2DC | i32 | **EPSG code** (0 = not stored) | 0 | 2926 |
| 0x37C | f64 | central meridian (°) | −120.5 | −120.8333 |
| 0x384 / 0x38C | f64 | false easting / northing (CS units) | 1640416.667 / 0 | 1640416.667 / 0 |
| 0x394 | f64×4 | useful range min lon, min lat, max lon, max lat (all 0 = unset) | unset | −125, 47, −116.33, 49.33 |
| 0x3D4 | f64 | scale reduction | 1 | 1 |
| 0x3DC / 0x3E4 | f64 | standard parallels 1 / 2 (°) | 47.3333 / 45.8333 | 48.7333 / 47.5 |
| 0x3EC | f64 | origin latitude (°) | 45.3333 | 47.0 |
| 0x3F4 | u32 | ⚠️ unit code? | 1 | 1 |
| 0x3F8 | char[16] | unit name (`Foot` = US survey foot) | Foot | Foot |
| 0x408 | char[24] | datum key | `EPSG:6152` | `NAD83/HARN-A` |

EPSG is taken from the stored code, else from an `EPSG:nnnn` key, else from a key-name table (`Model/GcsKeys.cs`).
WKT is built from the stored parameters for `LM` (Lambert conformal conic 2SP) and `LL`; it reproduces the built-in
EPSG:2927/2926 projections to 1e-7° (the `EPSG:2927` record stores EPSG's rounded false easting 1640416.67 ftUS). ⚠️ Parameter slots for other projections (TM, UTM, …) and the "reference" GCS
(`GetGCS(false)`) storage are unverified: no corpus file carries them.

Only 2 of 108 corpus files carry this element (`wsdot-XE3771/316251H_BP_VM.dgn`, `wsdot-XE3736/XL7089_CF_CS.dgn`, both
in model 0). Ground truth: `MstnOracle --gcs-only` (`ModelReference.GetGCS` + `LatLongFromMasterUnits` samples) →
`testdata/oracle/gcs/*.gcs.oracle.json`, compared by `GeoCoordinateTests.Gcs_MatchesOracle`: key name, description,
datum key, units and false origin match, DgnKit's lon/lat equals MicroStation's `LatLongFromMasterUnits` to < 5e-8°
(≈ 5 mm) on all samples, files whose only "GCS" is a GIS SpatialRef have no model GCS in MicroStation either, and no
corpus model has a reference GCS (`GetGCS(false)` = none).

**GIS feature spatial reference (file-level fallback).** GIS data imported into MicroStation (e.g. WSDOT shapefiles)
brings an ECSchema whose `FeatureDefinition` custom attribute has a `SpatialRef` property holding the source's ESRI
WKT (`PROJCS["NAD_1983_HARN_StatePlane_Washington_South_FIPS_4602_Feet",…]`). The schema is UTF-16 XML in a type 39
DgnStore with magic `fLMX` at +0x28 in `Dgn^Nm`. It describes the *source* data, not necessarily the DGN model: in
`01_XL7089_PS_IV.dgn` the vicinity-map model holds the same features scaled down, so exporters apply a file-level
GCS only when the model's median point projects into the CRS's area of use.

`DgnModel.GeoCoordinateSystem` = the model's MSGEOCOORD; `DgnFile.GeoCoordinateSystem` = default model's GCS, else the
first model GCS, else the most common SpatialRef.

## 8. Tables in `Dgn^Nm` ✅
Tables are type 96 headers with type 95 entries; the element **level** field holds the table kind:
1 levels, 2 fonts, 3 text styles, 4 filters, 5 dimension styles, 6 multiline styles, 7 line style names, 8 line style definitions, 9 dictionary, 10 registered apps, 11 colour books, 18 materials, 19 level-name dictionary, 23/24 display/named presentation.

**Level entry** (offsets from element start): `+32 id, +36 parent, +40 flags (bit21 displayed, bit22 plot), +48 override style, +52 override weight, +56 override colour, +64 style, +68 weight, +72 colour, +92 code`; name/description in string linkages. Oracle match: 100% on 11 files.

**Font entry**: `+40 u32 font number (TrueType ≥ 1024), +44 u16 name byte length, +46 UTF-16 name`.

**Text size** (rendering, verified on official DOT plots — vector glyph ink of all-caps words vs the size in the plot's
invisible text layer, 11 sheets): MicroStation draws TrueType *and* RSC text with **cap height = text size** (Arial em ≈
1.397 × size). DgnKit's PDF uses Helvetica at em = size / 0.718, which matches within 0.96–1.03 for Arial, FDOT, WSDOT
CAE, Engineering and NJDOT RSC fonts. Advance widths differ per font: FDOT ≈ 1.24 ×, FDOT Bold ≈ 1.14 ×, Engineering
Regular/Bold ≈ 1.13 / 1.12 × Helvetica at the same cap height (`Graphics/FontMetrics.WidthFactor`, applied as a
horizontal text scale). Caution: the FDOT fonts are not installed on the oracle machine and the oracle reports font
"Arial" for FDOT texts and dropped dimension texts (font 1025 = FDOT in the file) — evidently a substitution, so oracle
widths of FDOT text are Arial widths, not the plotted ones.

**Line style name entry** (level 7) and **definition entries** (level 8): see §8.3.

**Shared cell definitions (34)** live in `Dgn^Nm` with their components.

## 8.1 Tags ✅ (oracle: value/target/set/origin 100%)
**Tag set** (type 39 in `Dgn^Nm`, magic `"teSt"` at +0x28): +0x34 u32 byte length of the tag list starting at +0x3C;
per tag `name\0, u16 id, prompt\0, u16 type (1 char, 2 short, 3 long, 4 double, 5 binary), u16 value length,
3 flag bytes, default value`. Set name = string linkage key 1.
**Tag** (type 37, graphic): +0x98 u16 font, +0x9A u16 justification, +0xA0 origin (3 doubles), +0xD0 u16 tag id,
+0xF0/+0xF8 length/height multipliers (× 6/1000), +0x138 u32 value byte length, +0x140 value. Linkage `0x56D0`
key 10007 → tag-set element id (u64 @ +8); key 10000 → target element id (u64 @ +16).

## 8.2 RSC fraction glyphs ✅
Text bytes 0x81–0x9F (C1 controls) are RSC-font fractions: 0x81 ½, 0x82 ¼, 0x83 ¾, 0x84 ⅛ … 0x87 ⅞, 0x88–0x8F
sixteenths, 0x90–0x9F thirty-seconds (first four verified against MicroStation).

## 8.3 Custom line styles
Structures: SDK `DgnPlatform/LineStyleResource.r.h` (RSC resources, stored verbatim in the DGN), `LineStyle.h` /
`lstyle.h` (LsElementType, LsUnit, LCOPT_/LCSTROKE_/LCWIDTH_/LCPOINT_ flags in `DgnPlatform.r.h`). Decoder:
`Formats/V8/V8Misc.cs` (`V8LineStyles`, `V8LineStyleDefs`); ground truth: `MstnOracle --linestyles` (runs
`tools/oracle/linestyle_oracle.py` inside MicroStation through a `python load` key-in, because COM's `LineStyle` only
exposes Name/Number) → `testdata/oracle/*.linestyles.json`, compared by `LineStyleTests.Definitions_MatchMicroStation`
✅ (6 WSDOT/TxDOT files, 28 styles with in-file definitions, 142 strokes: kinds, stroke lengths/flags/widths/caps,
phase modes, compound offsets, line point symbols and scales all 100%) and by
`LineStyleTests.DroppedStrokes_MatchMicroStation` ✅ (MicroStation's stroked geometry from
`DraftingElementSchema.ToDroppedElements` for 26 sample lines: dash lengths and positions match on all 21 dashed ones).

**Name entry** (level 7, offsets from element start): `+0x40 u32 attributes` (LSATTR_: bits 0-1 units 0 master /
1 UOR / 2 device, 4 no-snap, 8 continuous, 0x10 no-range, 0x20 shared-cell-scale-independent, 0x80 physical),
`+0x50 u64 id of the root definition entry` (0 → defined in an external RSC/dgnlib, not in the file),
`+0x58 u32 root LsElementType`, `+0x5C i32 style number` (custom styles negative), `+0x60 UTF-16 name`.
Most styles in DOT files (e.g. NJDOT's 76, WSDOT paver sets' 534) are names only; the definitions live in dgnlibs.

**Definition entry** (level 8, type 95), one per component:

| off | type | field |
|---|---|---|
| 0x42 | u16 | LsElementType: 1 point symbol, 2 compound, 3 line code (stroke pattern), 4 line point, 6 internal, 7 raster |
| 0x44 | wchar[64] | component name |
| 0xC4 | u16 | dependency count n |
| 0xC6 | u16 | dependency byte size (8 n) |
| 0xC8 | u32 | resource size s |
| 0xCC | s bytes | resource (below); reserved fields contain uninitialised memory — ignore them |
| 0xCC+s | u64[n] | dependency element ids: sub-components in order (compound children; line point → line code, then each distinct symbol in order of first use; point symbol → its geometry cell) |

Every resource starts `char descr[128]; u32 lHdr1, lHdr2, auxType, auxID` (auxType = RTYPE: `'lsLC'` line code,
`'lsLS'` compound, `'lsLP'` line point, `'l8PS'` V8 point symbol — stored little-endian, i.e. the bytes read "CLsl",
"SLsl", "PLsl", "SP8l"). Offsets below are from the resource start (+0xCC):

* **LineCodeRsc** (stroke pattern): `+0x90 f64 phase`, `+0xC0 u32 options` (LCOPT_ 1 auto/fraction phase, 8 iteration
  limit, 0x10 single segment, 0x20 centre stretch), `+0xC4 u32 maxIterate`, `+0xCC u32 nStrokes` (≤ 32),
  `+0xD0 StrokeData[n]`, 48 bytes each: `f64 length, f64 width, f64 endWidth, f64 reserved, 3×u32 reserved,
  u8 strokeMode` (1 dash, 2 ray/bypass corner, 4 stretchable, 8 invert in first, 16 invert in last)`, u8 widthMode`
  (1 left half, 2 right half, 4 taper, 8 end taper)`, u8 capMode` (0 closed, 1 open, 2 extended, ≥3 arc)`, u8`.
* **LineStyleRsc** (compound): `+0xAC u32 nComp`, `+0xB0 ComponentInfo[n]`, 56 bytes each: `u32 rtype, u32 rscId,
  4×u32, f64 offset` (perpendicular, definition units)`, 3×f64`.
* **LinePointRsc**: `+0x98 u32 lcType, +0x9C u32 lcID, +0xA4 u32 nSym, +0xA8 PointSymInfo[n]`, 88 bytes each:
  `u32 symType, u32 symID, u16 strokeNo, u16 mod1` (LCPOINT_: bits 0-1 origin/end/centre of stroke, 4 line origin,
  8 line end, 0x10 each vertex, 0x20 adjusted / 0x40 absolute rotation, 0x100 no scale, 0x200 no clip, 0x400 no
  partial, 0x800 project partial, 0x4000/0x8000 colour/weight from symbol)`, u32, f64 xOffset, yOffset, zOffset,
  xAngle, yAngle, zAngle (degrees), xScale, yScale, zScale`.
* **PointSymRsc**: `+0x90 range low/high (2 × DPoint3d), +0xC0 offset (unused), +0xD8 f64 scale, +0x140 u32 symFlags
  (1 3D, 2 no scale), +0x144 u32 nBytes` (0 in V8 files: the geometry is the dependency element, a type-2 cell named
  "Linestyle Definition Cell" in `Dgn^Nm`). Symbol geometry ÷ scale = definition units (e.g. a guardrail tick
  ±208334 UOR with scale 10000 against strokes of 20.8/41.7/166.7).

**Element modifiers** — user linkage `0x79F9`: `u32 STYLEMOD bits` followed by packed (unaligned) f64 values for
each bit present, in bit order: 0x01 scale, 0x02 dash scale, 0x04 gap scale, 0x08 start width, 0x10 end width,
0x20 distance phase, 0x40 fraction phase (0x80 centre phase, 0x2000 true width carry no value). Seen: `0x01` (scale
only) and `0x21` (scale + distance phase). ✅ The distance phase is in the element model's **master units** whatever the
style's units (MicroStation shifts a UOR-unit style by phase × uorPerMaster × scale), and it is scaled like the
strokes. Fraction phase (share of the pattern length) is not in the corpus ⚠️.

**Model info** linkage `0x56EE` on the model element (type 66 in `Dgn~Mh`): `u32, u32, f64 1.0, f64 1.0, f64
annotation scale (e.g. 1200 for 1"=100'), f64`. ✅ The annotation scale (data offset 24) equals MicroStation's
`ModelInfo.GetAnnotationScaleFactor()` and `GetLineStyleScale()` on every model of the oracle files (1200, 600, 480;
sheets and models without the linkage: 1). Those models report line style scale mode 1 ("annotation scale"); a
separate global line style scale (mode 0 with a value ≠ 1) is not in the corpus and not decoded ⚠️.

**Scale rule** ✅ (oracle drop comparison): stroke length in UOR = definition length × [uorPerMaster if the style is in
master units; 1 for UOR] × element scale (0x79F9) × **model line style scale if the style is not physical**
(LSATTR 0x80; physical styles are true size). Examples: WSDOT `RD_MK_LineSkipCenterNew` (physical, UOR) at element
scale 1 in a 1:1200 design model strokes 100000/300000 UOR (10 ft / 30 ft); at 1/1200 on a sheet 83.333/250 UOR;
`RD_BR_BarrierCenter` (not physical) at element scale 1/1200 in a 1200 model strokes its definition size 25/8.3/58.3
UOR. DgnKit applies the line style scale of the element's own model; through references the attachment scales
follow (⚠️ MicroStation's per-attachment "scale line styles" option is not read).

**Rendering in DgnKit.** The main stroke pattern (root line code; a line point's line code; for compounds the
dashed child nearest the centre line) is flattened to `LineStyleDefinition.DashPattern` (alternating dash/gap,
merged runs, starting with a dash; `DashPhase` = start offset) in definition units. `DisplayListBuilder` resolves it
per element to `ResolvedSymbology.DashPattern` in root master units with the scale rule above and the element model's
UOR → root master scale (reference attachment scales). MicroStation strokes a continuous wide style (e.g.
`RD_MK_LineSolidNew`) into abutting pieces; DgnKit draws it as one line at the stroke width. Line
point symbols are emitted as extra primitives (definition cell geometry ÷ symbol scale, rotated with the line,
offsets in the line frame, the line's colour/weight unless LCPOINT_COLOR/WEIGHT). A uniform dash width becomes the
PDF pen width. PDF uses the pattern as a dash array; DXF/DWG get one linetype per style plus a per-entity linetype
scale. Against the official DOT plots this changes four WSDOT sheets, all upward (ink F1 +0.1 … +0.5); the
oracle-driven phase and model line style scale corrections leave every page's ink F1 unchanged (±0.05).

## 9. Still open ⚠️
* Line style rendering: compound children at perpendicular offsets (drawn on the centre line), tapers/half widths,
  centre-stretch and single-segment modes, device-unit styles; styles defined only in external dgnlibs/RSC files
  (most DOT styles) stay continuous.
* Multilines (36) — definition points/styles; MicroStation regenerates their graphics. Dimensions: see §4.2 for the
  remaining unknowns (angular/radial kinds, format-5 delimiter bit, 3D layout, V7 dimensions).
* Mesh (105 + matrix 101/102/103) payload layout.
* Model type (design vs sheet vs drawing), level frozen bit.
* GCS: parameter slots of non-LCC projections, reference (secondary) GCS, vertical datum (§7.1).
* Raster attachments (87/88/90).

## 10. V7 (IGDS/ISFF)
Flat word-aligned elements: `byte level|complex, byte type|deleted, u16 wordsToFollow`, 6 × i32 range (VAX middle-endian,
2³¹ biased), graphic group, attribute index, properties, symbology byte (style:3, weight:5) + colour byte. Doubles are
VAX D-float. TCB (type 9): units at 1112/1116/1120, global origin at 1240, 3D flag at 1214 bit 6. See `Formats/V7/V7Reader.cs`.
