namespace KefRemote.Drivers.Kef;

/// <summary>The color of a finish that a part of the speaker drawing is painted in.</summary>
public enum KefPaint { Cabinet, Edge, Trim, Cone, Tweeter, Fabric, Seam, Plinth, Top }

public readonly record struct KefPoint(double X, double Y);

/// <summary>
/// One closed figure of a speaker drawing: a start point and cubic Bézier segments, three points each (straight sides
/// have their control points on the line). A stroked figure is the outline just inside the cabinet, <see cref="KefShapes.EdgeWidth"/> wide.
/// </summary>
public sealed record KefFigure(KefPaint Paint, bool Stroked, KefPoint Start, IReadOnlyList<KefPoint> Curves);

/// <summary>
/// The speaker of each model on the 48-unit grid of the app icon, drawn back to front. Proportions and driver
/// positions follow KEF's 3D models of the speakers; only the XIO soundbar is drawn taller than it is, so it does not
/// turn into a line at 16 px. Each model keeps one detail of its own besides the outline and the Uni-Q driver.
/// Mirrors macos/Sources/KefW2Driver/KefShape.swift.
/// </summary>
public static class KefShapes
{
    public const double EdgeWidth = 1.5;

    /// <summary>The app icon's speaker, for a model that is not known.</summary>
    public static readonly IReadOnlyList<KefFigure> Generic = new Drawing()
        .Box(KefPaint.Cabinet, 10, 3, 28, 42, 5, edge: true)
        .UniQ(24, 24, 11, 8.2, 3.2);

    static readonly Dictionary<string, IReadOnlyList<KefFigure>> Drawings = new()
    {
        // Foot, corners a fifth of the width, driver 72% of the width.
        ["LSX II LT"] = new Drawing()
            .Box(KefPaint.Trim, 15, 43, 18, 3, 1.2)
            .Box(KefPaint.Cabinet, 10, 2.5, 28, 42, 5.8, edge: true)
            .UniQ(24, 24, 10, 7.8, 3.5),
        // The LT's outline, with the fabric wrap as a band around the front.
        ["LSX II"] = new Drawing()
            .Box(KefPaint.Trim, 15, 43, 18, 3, 1.2)
            .Box(KefPaint.Fabric, 10, 2.5, 28, 42, 5.8, edge: true)
            .Box(KefPaint.Seam, 12.25, 4.75, 23.5, 37.5, 4.2)
            .Box(KefPaint.Cabinet, 13, 5.5, 22, 36, 3.6)
            .UniQ(24, 24, 10, 7.8, 3.5),
        // Squarer than the LSX: tighter corners, two feet, driver 62% of the width with its real, smaller tweeter.
        ["LS50 Wireless II"] = new Drawing()
            .Box(KefPaint.Trim, 13.6, 44, 3.6, 2.6, 0.6)
            .Box(KefPaint.Trim, 30.8, 44, 3.6, 2.6, 0.6)
            .Box(KefPaint.Cabinet, 9.5, 1.5, 29, 43.5, (3.5, 3.5, 3, 3), edge: true)
            .UniQ(24, 23.25, 9, 6.9, 1.8),
        // Rounded body on a narrow plinth, driver just above the middle.
        ["LS LUXE"] = new Drawing()
            .Box(KefPaint.Trim, 15.3, 43, 17.4, 3.2, 1.2)
            .Outline(KefPaint.Cabinet, new(24, 1.8),
            [
                new(27.01, 1.8), new(30.33, 2.18), new(32.79, 3.92),
                new(34.86, 5.37), new(36.07, 7.9), new(36.95, 10.27),
                new(38.3, 13.87), new(38.43, 17.86), new(38.5, 21.7),
                new(38.58, 26.09), new(38.14, 30.53), new(37.22, 34.83),
                new(36.69, 37.3), new(36.1, 39.86), new(34.79, 42.02),
                new(34.28, 42.86), new(33.44, 43.43), new(32.76, 44.14),
                new(32.76, 44.14), new(15.24, 44.14), new(15.24, 44.14),
                new(14.56, 43.43), new(13.72, 42.86), new(13.21, 42.02),
                new(11.9, 39.86), new(11.31, 37.3), new(10.78, 34.83),
                new(9.86, 30.53), new(9.42, 26.09), new(9.5, 21.7),
                new(9.57, 17.86), new(9.7, 13.87), new(11.05, 10.27),
                new(11.93, 7.9), new(13.14, 5.37), new(15.21, 3.92),
                new(17.67, 2.18), new(20.99, 1.8), new(24, 1.8),
            ], edge: true)
            .UniQ(24, 21.95, 9.4, 7.4, 2.5),
        // Slim column on a wide plinth, driver near the top.
        ["LS60 Wireless"] = new Drawing()
            .Box(KefPaint.Plinth, 13, 43.5, 22, 2.5, 1)
            .Box(KefPaint.Cabinet, 18.5, 1.5, 11, 40.5, 2.4, edge: true)
            .UniQ(24, 13.4, 4.6, 3.5, 1.4),
        // Seen a little from above, about five times taller than it is: the lit top face and the control in its middle.
        ["XIO"] = new Drawing()
            .Box(KefPaint.Cabinet, 2, 17.5, 44, 13, 2.8, edge: true)
            .Box(KefPaint.Top, 3, 18.5, 42, 4.6, (1.9, 1.9, 0, 0))
            .Ellipse(KefPaint.Trim, 24, 20.8, 3.8, 1.5),
    };

    /// <summary>The drawing of a model by display name, or <see cref="Generic"/> for any other.</summary>
    public static IReadOnlyList<KefFigure> For(string? model) =>
        model != null && Drawings.TryGetValue(model, out var drawing) ? drawing : Generic;

    /// <summary>Builds figures from rounded boxes, ellipses and outlines, with corners and ellipses as Bézier arcs.</summary>
    sealed class Drawing : List<KefFigure>
    {
        // Control point distance for a quarter circle drawn as one cubic segment.
        const double Arc = 0.5522847498;

        public Drawing Box(KefPaint paint, double x, double y, double w, double h, double r, bool edge = false) =>
            Box(paint, x, y, w, h, (r, r, r, r), edge);

        /// <param name="r">Corner radii: top left, top right, bottom right, bottom left.</param>
        public Drawing Box(KefPaint paint, double x, double y, double w, double h, (double TopLeft, double TopRight, double BottomRight, double BottomLeft) r, bool edge = false)
        {
            Add(RoundedBox(paint, false, x, y, w, h, r));
            if (edge)
            {
                double i = EdgeWidth / 2;
                static double Inner(double radius) => Math.Max(0, radius - EdgeWidth / 2);
                Add(RoundedBox(KefPaint.Edge, true, x + i, y + i, w - 2 * i, h - 2 * i,
                    (Inner(r.TopLeft), Inner(r.TopRight), Inner(r.BottomRight), Inner(r.BottomLeft))));
            }
            return this;
        }

        public Drawing Ellipse(KefPaint paint, double cx, double cy, double rx, double ry)
        {
            double kx = rx * Arc, ky = ry * Arc;
            Add(new KefFigure(paint, false, new(cx + rx, cy),
            [
                new(cx + rx, cy + ky), new(cx + kx, cy + ry), new(cx, cy + ry),
                new(cx - kx, cy + ry), new(cx - rx, cy + ky), new(cx - rx, cy),
                new(cx - rx, cy - ky), new(cx - kx, cy - ry), new(cx, cy - ry),
                new(cx + kx, cy - ry), new(cx + rx, cy - ky), new(cx + rx, cy),
            ]));
            return this;
        }

        /// <summary>Trim ring, cone and tweeter of the Uni-Q driver.</summary>
        public Drawing UniQ(double cx, double cy, double trim, double cone, double tweeter) =>
            Ellipse(KefPaint.Trim, cx, cy, trim, trim)
                .Ellipse(KefPaint.Cone, cx, cy, cone, cone)
                .Ellipse(KefPaint.Tweeter, cx, cy, tweeter, tweeter);

        /// <summary>
        /// A free outline. Its edge is the same outline scaled about its middle to sit half the edge width inside,
        /// which keeps the edge even on a shape this close to a rounded box.
        /// </summary>
        public Drawing Outline(KefPaint paint, KefPoint start, KefPoint[] curves, bool edge = false)
        {
            Add(new KefFigure(paint, false, start, curves));
            if (edge)
            {
                var ends = curves.Where((_, n) => n % 3 == 2).Append(start).ToArray();
                double left = ends.Min(p => p.X), right = ends.Max(p => p.X), top = ends.Min(p => p.Y), bottom = ends.Max(p => p.Y);
                double cx = (left + right) / 2, cy = (top + bottom) / 2;
                double sx = (right - left - EdgeWidth) / (right - left), sy = (bottom - top - EdgeWidth) / (bottom - top);
                KefPoint Inset(KefPoint p) => new(cx + (p.X - cx) * sx, cy + (p.Y - cy) * sy);
                Add(new KefFigure(KefPaint.Edge, true, Inset(start), curves.Select(Inset).ToArray()));
            }
            return this;
        }

        static KefFigure RoundedBox(KefPaint paint, bool stroked, double x, double y, double w, double h,
            (double TopLeft, double TopRight, double BottomRight, double BottomLeft) r)
        {
            double right = x + w, bottom = y + h;
            var curves = new List<KefPoint>();
            void Line(KefPoint from, KefPoint to) => curves.AddRange([from, to, to]);
            void Corner(KefPoint from, KefPoint corner, KefPoint to) => curves.AddRange(
            [
                new(from.X + (corner.X - from.X) * Arc, from.Y + (corner.Y - from.Y) * Arc),
                new(to.X + (corner.X - to.X) * Arc, to.Y + (corner.Y - to.Y) * Arc),
                to,
            ]);
            KefPoint start = new(x + r.TopLeft, y);
            Line(start, new(right - r.TopRight, y));
            Corner(new(right - r.TopRight, y), new(right, y), new(right, y + r.TopRight));
            Line(new(right, y + r.TopRight), new(right, bottom - r.BottomRight));
            Corner(new(right, bottom - r.BottomRight), new(right, bottom), new(right - r.BottomRight, bottom));
            Line(new(right - r.BottomRight, bottom), new(x + r.BottomLeft, bottom));
            Corner(new(x + r.BottomLeft, bottom), new(x, bottom), new(x, bottom - r.BottomLeft));
            Line(new(x, bottom - r.BottomLeft), new(x, y + r.TopLeft));
            Corner(new(x, y + r.TopLeft), new(x, y), start);
            return new KefFigure(paint, stroked, start, curves);
        }
    }
}
