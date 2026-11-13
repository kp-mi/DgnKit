namespace DgnKit.Elements
{
    /// <summary>
    /// An XAttribute: a typed blob attached to an element (V8+). Identified by a handler id (major/minor) and an XAttribute id.
    /// Used for EC instances (item types, civil feature data), area patterns/fills, line style modifiers, cached graphics, etc.
    /// </summary>
    public sealed class XAttribute
    {
        public ushort HandlerMajor { get; internal set; }
        public ushort HandlerMinor { get; internal set; }
        public uint Id { get; internal set; }
        public uint Flags { get; internal set; }
        public byte[] Data { get; internal set; } = System.Array.Empty<byte>();
        /// <summary>True if the stored payload was zlib-compressed (Data holds the inflated bytes).</summary>
        public bool WasCompressed { get; internal set; }

        public uint HandlerId => ((uint)HandlerMajor << 16) | HandlerMinor;
        public string HandlerName => XAttributeHandlers.NameOf(HandlerMajor, HandlerMinor);
        public override string ToString() => $"XAttr {HandlerName} ({HandlerMajor:X4}:{HandlerMinor}) id={Id} {Data.Length}B";
    }

    /// <summary>Known XAttribute handler major ids (reverse-engineered; names are descriptive).</summary>
    public static class XAttributeHandlers
    {
        public const ushort BinaryEcXml = 0x56FF;      // "BECXML" binary-serialised EC instance
        public const ushort EcInstance = 0x5716;       // EC instance / relationship (ECXA)
        public const ushort ModelInfo = 0x56EC;

        public static string NameOf(ushort major, ushort minor) => major switch
        {
            BinaryEcXml => "BinaryECXml",
            EcInstance => "ECInstance",
            ModelInfo => "ModelInfo",
            _ => $"0x{major:X4}",
        };
    }
}
