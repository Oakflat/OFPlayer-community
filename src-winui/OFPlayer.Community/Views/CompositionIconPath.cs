using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Windows.Graphics;

namespace OFPlayer_WinUI.Views;

/// <summary>
/// Hands icon contours to the compositor. A CompositionPath only reads Direct2D geometry, so the
/// contours are written into one here; the app ships no drawing library that would do it.
/// </summary>
internal static partial class CompositionIconPath
{
    private const int FillModeWinding = 1;
    private const int FigureBeginFilled = 0;
    private const int FigureEndOpen = 0;
    private const int FigureEndClosed = 1;
    private const int FactoryMultiThreaded = 1;

    private static readonly Lazy<ID2D1Factory> Factory = new(() =>
    {
        Marshal.ThrowExceptionForHR(D2D1CreateFactory(FactoryMultiThreaded, typeof(ID2D1Factory).GUID, 0, out var factory));
        return factory;
    });

    /// <summary>One filled path from the given contours, with the same winding rule as the still icon.</summary>
    public static CompositionPath Create(IEnumerable<MusicIconContour> contours)
    {
        Marshal.ThrowExceptionForHR(Factory.Value.CreatePathGeometry(out var geometry));
        Marshal.ThrowExceptionForHR(geometry.Open(out var sink));
        sink.SetFillMode(FillModeWinding);

        foreach (var contour in contours)
        {
            sink.BeginFigure(new PointF((float)contour.Start.X, (float)contour.Start.Y), FigureBeginFilled);
            foreach (var segment in contour.Segments)
            {
                var values = new float[segment.Length * 2];
                for (var index = 0; index < segment.Length; index++)
                {
                    values[index * 2] = (float)segment[index].X;
                    values[index * 2 + 1] = (float)segment[index].Y;
                }

                if (segment.Length == 1) sink.AddLines(values, 1);
                else sink.AddBeziers(values, (uint)segment.Length / 3);
            }

            sink.EndFigure(contour.Closed ? FigureEndClosed : FigureEndOpen);
        }

        Marshal.ThrowExceptionForHR(sink.Close());
        return new CompositionPath(new Source(geometry));
    }

    /// <summary>
    /// The compositor takes this as a WinRT object and then asks it for the Direct2D geometry
    /// through a classic COM interface, which the WinRT wrapper only answers when asked here.
    /// </summary>
    private sealed partial class Source : IGeometrySource2D, IGeometrySource2DInterop, ICustomQueryInterface
    {
        // A raw reference, because the compositor may ask from a thread other than the one that built the path.
        private readonly nint geometry;

        public Source(ID2D1PathGeometry path) => geometry = Marshal.GetComInterfaceForObject(path, typeof(ID2D1PathGeometry));

        ~Source() => Marshal.Release(geometry);

        public int GetGeometry(out nint value)
        {
            Marshal.AddRef(geometry);
            value = geometry;
            return 0;
        }

        public int TryGetGeometryUsingFactory(nint factory, out nint value)
        {
            value = 0;
            return unchecked((int)0x80004001); // E_NOTIMPL: the geometry is not tied to a factory of the caller's.
        }

        CustomQueryInterfaceResult ICustomQueryInterface.GetInterface(ref Guid iid, out nint ppv)
        {
            if (iid == typeof(IGeometrySource2DInterop).GUID)
            {
                ppv = Marshal.GetComInterfaceForObject(this, typeof(IGeometrySource2DInterop), CustomQueryInterfaceMode.Ignore);
                return CustomQueryInterfaceResult.Handled;
            }

            ppv = 0;
            return CustomQueryInterfaceResult.NotHandled;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PointF(float X, float Y);

    [DllImport("d2d1.dll")]
    private static extern int D2D1CreateFactory(int factoryType, in Guid iid, nint options, out ID2D1Factory factory);

    [ComImport, Guid("0657AF73-53FD-47CF-84FF-C8492D2A80A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGeometrySource2DInterop
    {
        [PreserveSig] int GetGeometry(out nint value);
        [PreserveSig] int TryGetGeometryUsingFactory(nint factory, out nint value);
    }

    // Only the members used here are typed; the rest hold their place in the native method order.
    [ComImport, Guid("06152247-6f50-465a-9245-118bfd3b6007"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID2D1Factory
    {
        void ReloadSystemMetrics();
        void GetDesktopDpi();
        void CreateRectangleGeometry();
        void CreateRoundedRectangleGeometry();
        void CreateEllipseGeometry();
        void CreateGeometryGroup();
        void CreateTransformedGeometry();
        [PreserveSig] int CreatePathGeometry(out ID2D1PathGeometry geometry);
    }

    [ComImport, Guid("2cd906a5-12e2-11dc-9fed-001143a055f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID2D1PathGeometry
    {
        void GetFactory();
        void GetBounds();
        void GetWidenedBounds();
        void StrokeContainsPoint();
        void FillContainsPoint();
        void CompareWithGeometry();
        void Simplify();
        void Tessellate();
        void CombineWithGeometry();
        void Outline();
        void ComputeArea();
        void ComputeLength();
        void ComputePointAtLength();
        void Widen();
        [PreserveSig] int Open(out ID2D1SimplifiedGeometrySink sink);
    }

    [ComImport, Guid("2cd9069e-12e2-11dc-9fed-001143a055f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID2D1SimplifiedGeometrySink
    {
        [PreserveSig] void SetFillMode(int mode);
        [PreserveSig] void SetSegmentFlags(int flags);
        [PreserveSig] void BeginFigure(PointF start, int figureBegin);
        [PreserveSig] void AddLines([In, MarshalAs(UnmanagedType.LPArray)] float[] points, uint count);
        [PreserveSig] void AddBeziers([In, MarshalAs(UnmanagedType.LPArray)] float[] beziers, uint count);
        [PreserveSig] void EndFigure(int figureEnd);
        [PreserveSig] int Close();
    }
}
