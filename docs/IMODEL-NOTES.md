# Notes from iTwin/imodel-native relevant to DGN V8 decoding

Source: https://github.com/iTwin/imodel-native (paths relative to the repo root). The V8 converter itself is **not**
in the repo; these are concepts and binary layouts that carry over.

## Concept mapping (used for DgnKit's model, see ARCHITECTURE.md)
* Model → `DgnModel` subclasses; graphic element → `GeometricElement2d/3d` (+ `Placement`, `GeometryStream`).
* Level → `SpatialCategory/DrawingCategory` + default `SubCategory::Appearance`
  (`PublicAPI/DgnPlatform/DgnCategory.h`): invisible, dontPlot, dontSnap, dontLocate, color, fillColor, weight, style,
  displayPriority, material, transparency. "ByLevel" = appearance not overridden in the element's `BasicSymbology`.
* Shared cell definition → `DgnGeometryPart` (`GeomPart.h`); instance → `GeometryPartInstance` op.
* Reference attachment → separate model + `ExternalSourceAttachment` (`DgnCore/ExternalSource.cpp`).
* EC XAttributes / item types → `ElementAspect` (`DgnElement.h`).

## GeometryStream
Op stream `{u32 opCode; u32 size(8-aligned); data}` (`DgnCore/ElementGeometry.cpp`), ops: Header 1, SubGraphicRange 2,
PartInstance 3, BasicSymbology 4, Point 5/6, Arc 7, CurveVector 8, Polyface 9, CurvePrimitive 10, SolidPrimitive 11,
BsplineSurface 12, AreaFill 19, Pattern 20, Material 21, TextString 22, LineStyleModifiers 23, BRep 25, Image 28.
FlatBuffers schemas: `DgnCore/ElementGraphics.fbs`, `GeomLibs/serialization/src/FlatBuffer/allcg.fbs`.
IModelJson (`GeomLibs/serialization/src/IModelJson*.cpp`) is a good neutral geometry JSON for a future DgnKit output.

## Line styles (`PublicAPI/DgnPlatform/LineStyle.h`, `DgnCore/linestyle/*`)
Component types: PointSymbol 1, Compound 2, LineCode 3, LinePoint 4, Internal 6, RasterImage 7 — matches the
"root component type" field DgnKit decodes from V8 line-style name entries.
LineCode strokes (≤ 32): length, orgWidth, endWidth, strokeMode (persisted bits DASH 1, RAY 2, SCALE 4, SINVERT 8,
EINVERT 16), widthMode (0 none, 1 left, 2 right, 3 full), capMode. Options AUTOPHASE 1, ITERATION 8, SEGMENT 0x10,
CENTERSTRETCH 0x20. LinePoint symbols: strokeNum, x/yOffset, angle, mod1 (ORIGIN 1, END 2, CENTER 3, …).
STYLEMOD (element line-style modifiers): SCALE 1, DSCALE 2, GSCALE 4, SWIDTH 8, EWIDTH 0x10, DISTPHASE 0x20,
FRACTPHASE 0x40, CENTERPHASE 0x80, NORMAL 0x100, RMATRIX 0x200, TRUE_WIDTH 0x2000.
Special style numbers: ByLevel 0x7FFFFFFF, ByCell 0x7FFFFFFE (confirmed in V8 element headers).

## EC instance binary layout (ECDBuffer) — `iModelCore/ecobjects/src/ECDBuffer.cpp`
Header (5 bytes): formatVersion, readableBy, writableBy, headerSize(=5), flags (bit0 UTF-8). Then: null-flag words
(1 bit per property, set = null), fixed-size values (int32 4, long/double/DateTime 8, bool 1, Point2d 16, Point3d 24),
`u32` secondary offsets for variable-size properties + 1 trailing, then strings/binaries/arrays. Property order:
inherited first, then declared order; structs flattened. Useful when an item-type XAttribute is stored as ECDBuffer
rather than BECXML (not yet seen in the corpus).

## RSC fonts — `PublicAPI/DgnPlatform/DgnRscFontStructures.h`, `DgnCore/RscFont.cpp`
Glyph header 20 bytes `{u16 code; u16 width; i16 left; i16 right; u32; i32 kernOffset; i32 symbJust}`; glyph data
`{u32; i32 len; i32 numElems}` + elements `{u8 type (1 line, 2 poly, 3–5 holes, 6 spline); u8; u16 n; i16 x,y[n]}`;
fraction map ≤ 76 × `{u8 num; u8 den; u16 char}`. Needed to render RSC text as vectors exactly like MicroStation plots.
