namespace Godot;

// Missing Godot node types
public class Line2D : Node2D
{
    public Vector2[] Points { get; set; } = Array.Empty<Vector2>();
    public float Width { get; set; } = 1f;
    public Color DefaultColor { get; set; } = Color.White;
    public void AddPoint(Vector2 position, int? atPosition = null) { }
    public void ClearPoints() { }
}

public class CpuParticles2D : Node2D
{
    public bool Emitting { get; set; }
}

public class Marker2D : Node2D { }
public class PathFollow2D : Node2D
{
    public float Progress { get; set; }
    public float ProgressRatio { get; set; }
}
public class Path2D : Node2D { }

public class BackBufferCopy : Node2D { }
public class CanvasGroup : Node2D { }
public class CanvasItemMaterial : Material { }

public class NinePatchRect : Control
{
    public Texture2D? Texture { get; set; }
}

public class AspectRatioContainer : Container { }
public class VFlowContainer : FlowContainer { }

public class WorldEnvironment : Node { }
public class FastNoiseLite : Resource { }

public class Font : Resource
{
    public float GetStringSize(string text, int alignment = 0, float width = -1, int fontSize = 16) => text.Length * fontSize * 0.6f;
}

public class TextParagraph
{
    public void Clear() { }
    public void AddString(string text, Font font, int fontSize) { }
    public Vector2 GetSize() => Vector2.Zero;
    public float GetWidth() => 0;
}

public class StyleBoxEmpty : Resource { }

public class GradientTexture2D : Texture2D { }
public class Gradient : Resource { }

public class ParticleProcessMaterial : Material
{
    public Vector3 EmissionBoxExtents { get; set; }
}

public class RenderingServer
{
    public enum ViewportMsaa { Disabled, Msaa2X, Msaa4X, Msaa8X }
    public static void GlobalShaderParameterSet(StringName name, Variant value) { }
}

// Input types
public enum Key { None, A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z, Escape, Enter, Tab, Space, Left, Right, Up, Down, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12 }
public enum MouseButton { None, Left, Right, Middle, WheelUp, WheelDown }
public class InputEventJoypadMotion : InputEvent
{
    public int Axis { get; set; }
    public float AxisValue { get; set; }
}
public class InputEventAction : InputEvent
{
    public StringName Action { get; set; } = "";
}

// Error enum
public enum Error
{
    Ok, Failed, Unavailable, Unconfigured, Unauthorized, ParameterRangeError,
    OutOfMemory, FileNotFound, FileBadDrive, FileBadPath, FileNoPermission,
    FileAlreadyInUse, FileCantOpen, FileCantWrite, FileCantRead, FileUnrecognized,
    FileCorrupt, FileMissingDependencies, FileEof, CantOpen, CantCreate, QueryFailed,
    AlreadyInUse, Locked, Timeout, CantConnect, CantResolve, ConnectionError, CantAcquireResource,
    CantFork, InvalidData, InvalidParameter, AlreadyExists, DoesNotExist, DatabaseCantRead,
    DatabaseCantWrite, CompilationFailed, MethodNotFound, LinkFailed, ScriptFailed,
    CyclicLink, InvalidDeclaration, DuplicateSymbol, ParseError, Busy, Skip, Help, Bug
}

// Tool attribute
[AttributeUsage(AttributeTargets.Class)]
public class ToolAttribute : Attribute { }

// ExportToolButton attribute
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class ExportToolButtonAttribute : Attribute
{
    public ExportToolButtonAttribute(string text, string icon = "") { }
}

// AssemblyHasScripts attribute
[AttributeUsage(AttributeTargets.Assembly)]
public class AssemblyHasScriptsAttribute : Attribute
{
    public AssemblyHasScriptsAttribute() { }
    public AssemblyHasScriptsAttribute(string[] scripts) { }
}

// Signal class (not attribute) - note: this conflicts with Signal attribute,
// but decompiled code uses both patterns
// public class Signal { public Signal(GodotObject owner, StringName name) { } }

// Colors - static color constants
public static class Colors
{
    public static Color White { get; } = Color.White;
    public static Color Black { get; } = Color.Black;
    public static Color Red { get; } = new(1, 0, 0);
    public static Color DarkRed { get; } = new(0.55f, 0, 0);
    public static Color Green { get; } = new(0, 1, 0);
    public static Color Blue { get; } = new(0, 0, 1);
    public static Color Yellow { get; } = new(1, 1, 0);
    public static Color Transparent { get; } = Color.Transparent;
    public static Color Orange { get; } = new(1, 0.65f, 0);
    public static Color Purple { get; } = new(0.63f, 0.13f, 0.94f);
    public static Color Cyan { get; } = new(0, 1, 1);
    public static Color Magenta { get; } = new(1, 0, 1);
    public static Color Gray { get; } = new(0.5f, 0.5f, 0.5f);
    public static Color DarkGray { get; } = new(0.25f, 0.25f, 0.25f);
    public static Color LightGray { get; } = new(0.75f, 0.75f, 0.75f);
}

// Range control
public class Range : Control
{
    public new class SignalName : Control.SignalName
    {
        public static readonly StringName ValueChanged = "ValueChanged";
    }
    public double Value { get; set; }
    public double MinValue { get; set; }
    public double MaxValue { get; set; } = 100;
    public double Step { get; set; } = 1;
    public double Page { get; set; }
    public float Ratio { get; set; }
}

// _ProcessCustomFX for RichTextEffect needs this signature
// Already defined in UI.cs - just ensure it's virtual
