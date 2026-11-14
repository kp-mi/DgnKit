# Line style definition oracle — runs INSIDE MicroStation (key-in: python load "<this file>").
#
# MicroStation's COM API only exposes line style name/number, so MstnOracle sends a `python load` key-in that runs this
# script against the active design file. It dumps every line style of the file's line-style map with its component
# tree (stroke patterns, compounds, line points, point symbols) using the DgnPlatform line style API, and writes JSON to
# the path in the module-level OUT_PATH (set by the generated wrapper script) or %DGNKIT_LS_ORACLE_OUT%.
#
# Values are reported exactly as DgnPlatform returns them (definition units: see "unitsType" / "styleScale"). File
# paths are the exception: see _report_path.
import json
import os
import traceback

from MSPyBentley import *
from MSPyECObjects import *  # before MSPyMstnPlatform (its types derive from ECN::IAUIDataContext)
from MSPyBentleyGeom import *
from MSPyDgnPlatform import *
from MSPyMstnPlatform import *


def _report_path(p):
    """A path as written into the JSON: repo-relative with forward slashes inside the repository (REPO_ROOT, set by
    MstnOracle's wrapper, or %DGNKIT_REPO_ROOT%), otherwise the file name alone. The dumps are committed, and an
    absolute path would publish the local user profile. Same rule as MstnOracle's ReportPath."""
    p = str(p)
    if not os.path.isabs(p):
        return p
    full = os.path.normpath(p)
    root = globals().get("REPO_ROOT") or os.environ.get("DGNKIT_REPO_ROOT")
    if root:
        root = os.path.normpath(root).rstrip("\\/") + os.sep
        if full.lower().startswith(root.lower()):
            return full[len(root):].replace("\\", "/")
    return os.path.basename(full)


def _num(v):
    try:
        return int(v)
    except Exception:
        try:
            return int(v.value)
        except Exception:
            return str(v)


def _stroke(s):
    return {
        "length": s.GetLength(),
        "isDash": bool(s.IsDash()),
        "startWidth": s.GetStartWidth(),
        "endWidth": s.GetEndWidth(),
        "stretchable": bool(s.IsStretchable()),
        "invertStart": bool(s.InvertStart()),
        "invertEnd": bool(s.InvertEnd()),
        "bypassCorner": bool(s.BypassCorner()),
        "leftWidth": bool(s.HasLeftWidth()),
        "rightWidth": bool(s.HasRightWidth()),
        "capMode": _num(s.GetCapMode()),
    }


def _component(c, depth=0):
    if c is None:
        return None
    t = _num(c.GetType())
    d = {"type": t, "class": type(c).__name__, "name": str(c.GetName())}
    try:
        d["componentId"] = int(c.GetComponentId())
    except Exception:
        pass
    if depth > 8:
        return d
    if isinstance(c, LsLineCodeComponent):
        d["phaseMode"] = _num(c.GetPhaseMode())
        d["phase"] = c.GetPhaseValue()
        d["iterationLimit"] = bool(c.HasIterationLimit())
        d["iterations"] = int(c.GetNumIterations())
        d["singleSegment"] = bool(c.IsSingleSegmentMode())
        d["strokes"] = [_stroke(c.GetStroke(i)) for i in range(c.GetNumStrokes())]
        if isinstance(c, LsInternalComponent):
            d["hardwareStyle"] = int(c.GetHardwareStyle()) if c.IsHardwareStyle() else None
    elif isinstance(c, LsCompoundComponent):
        d["components"] = []
        for i in range(c.GetNumComponents()):
            sub = _component(c.GetComponent(i), depth + 1) or {}
            sub["offset"] = c.GetComponentOffset(i)
            d["components"].append(sub)
    elif isinstance(c, LsLinePointComponent):
        d["lineCode"] = _component(c.GetLineCode(), depth + 1)
        syms = []
        for i in range(c.GetNumSymbols()):
            si = c.GetSymbolInfo(i)
            sd = {
                "strokeNumber": int(si.GetStrokeNumber()),
                "location": _num(si.GetSymbolLocation()),
                "strokeLocation": _num(si.GetSymbolStrokeLocation()),
                "rotation": _num(si.GetRotationType()),
                "xOffset": si.GetXOffset(),
                "yOffset": si.GetYOffset(),
                "angle": si.GetAngle(),
            }
            try:
                sd["symbol"] = _component(si.GetSymbol(), depth + 1)
            except Exception as ex:
                sd["symbolError"] = str(ex)
            syms.append(sd)
        d["symbols"] = syms
    elif isinstance(c, LsPointSymbolComponent):
        d["unitScale"] = c.GetUnitScale()
        d["is3d"] = bool(c.Is3d())
        d["suppressScale"] = bool(c.SuppressScale())
    return d


_PARAM_FIELDS = ("modifiers", "scale", "dashScale", "gapScale", "startWidth", "endWidth", "distPhase", "fractPhase")


def _params(p):
    if p is None:
        return None
    d = {}
    for f in _PARAM_FIELDS:
        try:
            v = getattr(p, f)
            d[f] = int(v) if f == "modifiers" else float(v)
        except Exception:
            pass
    return d


def _pt(p):
    return [p.x, p.y, p.z]


def _curves(eh, max_points=64):
    """Curve primitives of an element: kind, length, start/end points and line string vertices (UORs)."""
    out = []
    cv = ICurvePathQuery.ElementToCurveVector(eh)
    if cv is None:
        return out
    for prim in cv:
        rec = {"kind": str(prim.GetCurvePrimitiveType())}
        try:
            rec["length"] = prim.Length()
        except Exception:
            pass
        try:
            a, b = DPoint3d(), DPoint3d()
            if prim.GetStartEnd(a, b):
                rec["start"], rec["end"] = _pt(a), _pt(b)
        except Exception:
            pass
        # vertices of line strings / shapes (wide dashes drop as shapes, whose start == end)
        try:
            pts = prim.GetLineString() if hasattr(prim, "GetLineString") else prim.GetLineStringCP()
            if pts is not None and len(pts) <= max_points:
                rec["points"] = [_pt(p) for p in pts]
        except Exception:
            pass
        out.append(rec)
    return out


def all_models(dgn_file, active_model):
    """(name, DgnModel) for every design/sheet/drawing model of the file, active model first."""
    out = [(str(active_model.GetModelName()), active_model)]
    seen = {int(active_model.GetModelId())}
    try:
        for item in dgn_file.GetModelIndex():
            try:
                mid = int(item.GetModelId())
                if mid in seen or _num(item.GetModelType()) not in (0, 1, 3):  # normal, sheet, drawing
                    continue
                status, m = dgn_file.LoadRootModelById(mid, True)
                if m is not None:
                    out.append((str(item.GetName()), m))
                    seen.add(mid)
            except Exception:
                pass
    except Exception:
        pass
    return out


def sample_elements(dgn_file, model, style_numbers, per_style=3, max_drop=400, counts=None, model_name=None):
    """Up to per_style simple linear elements per custom style, with MicroStation's dropped (stroked) geometry."""
    samples = []
    counts = {} if counts is None else counts
    level_styles = {}

    def level_style(level_id):
        if level_id not in level_styles:
            try:
                level_styles[level_id] = int(dgn_file.GetLevelCache().GetLevel(level_id).GetByLevelLineStyle().GetStyle())
            except Exception:
                level_styles[level_id] = None
        return level_styles[level_id]

    for ref in model.GetGraphicElements():
        try:
            eh = ElementHandle(ref)
            el = eh.GetElement()
            etype = int(el.ehdr.type)
            if etype not in (3, 4):  # lines / line strings: segment lengths are unambiguous
                continue
            getter = ElementPropertiesGetter(eh)
            raw_style = int(getter.GetLineStyle())
            style = raw_style
            by_level = raw_style == 0x7FFFFFFF
            if by_level:
                style = level_style(int(getter.GetLevel()))
            if style is None or style not in style_numbers or counts.get(style, 0) >= per_style:
                continue
            rec = {"elementId": int(eh.GetElementId()), "model": model_name, "type": etype, "style": style, "byLevel": by_level}
            try:
                lsp = LineStyleParams()
                getter.GetLineStyle(lsp)
                rec["params"] = _params(lsp)
            except Exception as ex:
                rec["paramsError"] = str(ex)
            rec["source"] = _curves(eh, 2000)
            agenda = ElementAgenda()
            dg = DropGraphics()
            dg.SetOptions(DropGraphics.eOPTION_LineStyles)
            st = DraftingElementSchema.ToDroppedElements(eh, agenda, dg)
            rec["dropStatus"] = _num(st)
            dropped = []
            for i, d in enumerate(agenda):
                if i >= max_drop:
                    rec["truncated"] = True
                    break
                try:
                    de = d.GetElement()
                    item = {"type": int(de.ehdr.type)}
                    try:
                        item["curves"] = _curves(d)
                    except Exception as ex:
                        item["error"] = str(ex)
                    dropped.append(item)
                except Exception as ex:
                    dropped.append({"error": str(ex)})
            rec["dropped"] = dropped
            samples.append(rec)
            counts[style] = counts.get(style, 0) + 1
        except Exception as ex:
            samples.append({"error": str(ex), "trace": traceback.format_exc()})
            if len(samples) > 200:
                break
    return samples


def model_info(model):
    """Line style relevant ModelInfo fields of a DgnModel."""
    d = {}
    try:
        mi = model.GetModelInfo()
        for k, f in (("lineStyleScale", "GetLineStyleScale"), ("lineStyleScaleMode", "GetLineStyleScaleMode"),
                     ("annotationScale", "GetAnnotationScaleFactor"), ("uorPerMeter", "GetUorPerMeter"),
                     ("uorPerStorage", "GetUorPerStorage"), ("modelType", "GetModelType")):
            try:
                v = getattr(mi, f)()
                d[k] = _num(v) if k in ("lineStyleScaleMode", "modelType") else float(v)
            except Exception as ex:
                d[k + "Error"] = str(ex)
    except Exception as ex:
        d["error"] = str(ex)
    return d


def dump(out_path):
    result = {"generator": "MicroStation DgnPlatform line style API (linestyle_oracle.py)", "lineStyles": []}
    try:
        dgn_file = ISessionMgr.GetActiveDgnFile()
        result["file"] = _report_path(dgn_file.GetFileName())
        try:
            result["uorPerMaster"] = ModelRef.GetUorPerMaster(ISessionMgr.GetActiveDgnModelRef())
        except Exception:
            pass
        ls_map = LineStyleManager.GetLineStyleMap(dgn_file)
        for entry in ls_map:
            try:
                # name/number from the map entry (LsMapEntry); its LsEntry can be None and reports number 0
                name = str(entry.GetStyleName())
                rec = {"name": name, "number": int(entry.GetStyleNumber())}
                try:
                    e = entry.GetLineStyleEntry()
                    if e is not None:
                        rec["entryNumber"] = int(e.GetStyleNumber())
                        rec["location"] = _num(e.GetDefinitionLocation())
                        rec["source"] = _report_path(e.GetSourceFileName())
                except Exception as ex:
                    rec["entryError"] = str(ex)
                h = LineStyleHandle.GetStyleHandle(name, dgn_file)
                if h is not None:
                    rec["unitsType"] = _num(h.GetUnitsType())
                    rec["styleScale"] = h.GetStyleScale()
                    rec["physical"] = bool(h.IsPhysical())
                    rec["snappable"] = bool(h.IsSnappable())
                    rec["root"] = _component(h.GetComponent())
                result["lineStyles"].append(rec)
            except Exception as ex:
                result["lineStyles"].append({"error": str(ex), "trace": traceback.format_exc()})
        custom = {r["number"] for r in result["lineStyles"] if r.get("number", 0) < 0 and r.get("root")}
        try:
            mref = ISessionMgr.GetActiveDgnModelRef()
            result["model"] = str(mref.GetDgnModel().GetModelName())
            try:
                mi = mref.GetDgnModel().GetModelInfo()
                result["modelInfo"] = {}
                for k, f in (("lineStyleScale", "GetLineStyleScale"), ("lineStyleScaleMode", "GetLineStyleScaleMode"),
                             ("annotationScale", "GetAnnotationScaleFactor"), ("uorPerMeter", "GetUorPerMeter"),
                             ("uorPerStorage", "GetUorPerStorage")):
                    try:
                        result["modelInfo"][k] = _num(getattr(mi, f)()) if k == "lineStyleScaleMode" else float(getattr(mi, f)())
                    except Exception as ex:
                        result["modelInfo"][k + "Error"] = str(ex)
            except Exception as ex:
                result["modelInfoError"] = str(ex)
            result["samples"] = []
            result["models"] = {}
            counts = {}
            for name, model in all_models(dgn_file, mref.GetDgnModel()):
                result["models"][name] = model_info(model)
                try:
                    result["samples"] += sample_elements(dgn_file, model, custom, counts=counts, model_name=name)
                except Exception as ex:
                    result["samples"].append({"model": name, "error": str(ex), "trace": traceback.format_exc()})
        except Exception as ex:
            result["samplesError"] = str(ex)
            result["samplesTrace"] = traceback.format_exc()
    except Exception as ex:
        result["error"] = str(ex)
        result["trace"] = traceback.format_exc()
    tmp = out_path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1)
    os.replace(tmp, out_path)


if __name__ == "__main__":
    _out = globals().get("OUT_PATH") or os.environ.get("DGNKIT_LS_ORACLE_OUT")
    if _out:
        dump(_out)
