import Foundation

/// The color of a finish that a part of the speaker drawing is painted in.
public enum KefPaint: Sendable, CaseIterable {
    case cabinet, edge, trim, cone, tweeter, fabric, seam, plinth, top
}

public struct KefPoint: Sendable, Equatable, Hashable {
    public let x: Double
    public let y: Double

    public init(_ x: Double, _ y: Double) {
        self.x = x
        self.y = y
    }
}

/// One closed figure of a speaker drawing: a start point and cubic Bézier segments, three points each
/// (straight sides have their control points on the line). A stroked figure is the outline just inside
/// the cabinet, `KefShape.edgeWidth` wide.
public struct KefFigure: Sendable, Equatable {
    public let paint: KefPaint
    public let stroked: Bool
    public let start: KefPoint
    public let curves: [KefPoint]
}

/// The speaker of each model on the 48-unit grid of the app icon, drawn back to front. Proportions and driver
/// positions follow KEF's 3D models of the speakers; only the XIO soundbar is drawn taller than it is, so it does
/// not turn into a line at 16 pt. Each model keeps one detail of its own besides the outline and the Uni-Q driver.
/// Mirrors windows/src/KefRemote.Drivers/Kef/KefShapes.cs.
public enum KefShape {
    public static let edgeWidth = 1.5

    /// The app icon's speaker, for a model that is not known.
    public static let generic = Drawing()
        .box(.cabinet, 10, 3, 28, 42, 5, edge: true)
        .uniQ(24, 24, 11, 8.2, 3.2)
        .figures

    private static let drawings: [String: [KefFigure]] = [
        // Foot, corners a fifth of the width, driver 72% of the width.
        "LSX II LT": Drawing()
            .box(.trim, 15, 43, 18, 3, 1.2)
            .box(.cabinet, 10, 2.5, 28, 42, 5.8, edge: true)
            .uniQ(24, 24, 10, 7.8, 3.5)
            .figures,
        // The LT's outline, with the fabric wrap as a band around the front.
        "LSX II": Drawing()
            .box(.trim, 15, 43, 18, 3, 1.2)
            .box(.fabric, 10, 2.5, 28, 42, 5.8, edge: true)
            .box(.seam, 12.25, 4.75, 23.5, 37.5, 4.2)
            .box(.cabinet, 13, 5.5, 22, 36, 3.6)
            .uniQ(24, 24, 10, 7.8, 3.5)
            .figures,
        // Squarer than the LSX: tighter corners, two feet, driver 62% of the width with its real, smaller tweeter.
        "LS50 Wireless II": Drawing()
            .box(.trim, 13.6, 44, 3.6, 2.6, 0.6)
            .box(.trim, 30.8, 44, 3.6, 2.6, 0.6)
            .box(.cabinet, 9.5, 1.5, 29, 43.5, corners: (3.5, 3.5, 3, 3), edge: true)
            .uniQ(24, 23.25, 9, 6.9, 1.8)
            .figures,
        // Rounded body on a narrow plinth, driver just above the middle.
        "LS LUXE": Drawing()
            .box(.trim, 15.3, 43, 17.4, 3.2, 1.2)
            .outline(.cabinet, KefPoint(24, 1.8), luxeOutline, edge: true)
            .uniQ(24, 21.95, 9.4, 7.4, 2.5)
            .figures,
        // Slim column on a wide plinth, driver near the top.
        "LS60 Wireless": Drawing()
            .box(.plinth, 13, 43.5, 22, 2.5, 1)
            .box(.cabinet, 18.5, 1.5, 11, 40.5, 2.4, edge: true)
            .uniQ(24, 13.4, 4.6, 3.5, 1.4)
            .figures,
        // Seen a little from above, about five times taller than it is: the lit top face and the control in its middle.
        "XIO": Drawing()
            .box(.cabinet, 2, 17.5, 44, 13, 2.8, edge: true)
            .box(.top, 3, 18.5, 42, 4.6, corners: (1.9, 1.9, 0, 0))
            .ellipse(.trim, 24, 20.8, 3.8, 1.5)
            .figures,
    ]

    /// LS LUXE body, measured from the front of KEF's 3D model and kept symmetric.
    private static let luxeOutline: [KefPoint] = [
        KefPoint(27.01, 1.8), KefPoint(30.33, 2.18), KefPoint(32.79, 3.92),
        KefPoint(34.86, 5.37), KefPoint(36.07, 7.9), KefPoint(36.95, 10.27),
        KefPoint(38.3, 13.87), KefPoint(38.43, 17.86), KefPoint(38.5, 21.7),
        KefPoint(38.58, 26.09), KefPoint(38.14, 30.53), KefPoint(37.22, 34.83),
        KefPoint(36.69, 37.3), KefPoint(36.1, 39.86), KefPoint(34.79, 42.02),
        KefPoint(34.28, 42.86), KefPoint(33.44, 43.43), KefPoint(32.76, 44.14),
        KefPoint(32.76, 44.14), KefPoint(15.24, 44.14), KefPoint(15.24, 44.14),
        KefPoint(14.56, 43.43), KefPoint(13.72, 42.86), KefPoint(13.21, 42.02),
        KefPoint(11.9, 39.86), KefPoint(11.31, 37.3), KefPoint(10.78, 34.83),
        KefPoint(9.86, 30.53), KefPoint(9.42, 26.09), KefPoint(9.5, 21.7),
        KefPoint(9.57, 17.86), KefPoint(9.7, 13.87), KefPoint(11.05, 10.27),
        KefPoint(11.93, 7.9), KefPoint(13.14, 5.37), KefPoint(15.21, 3.92),
        KefPoint(17.67, 2.18), KefPoint(20.99, 1.8), KefPoint(24, 1.8),
    ]

    /// The drawing of a model by display name, or `generic` for any other.
    public static func figures(for model: String?) -> [KefFigure] {
        model.flatMap { drawings[$0] } ?? generic
    }

    /// Builds figures from rounded boxes, ellipses and outlines, with corners and ellipses as Bézier arcs.
    struct Drawing {
        typealias Corners = (topLeft: Double, topRight: Double, bottomRight: Double, bottomLeft: Double)

        // Control point distance for a quarter circle drawn as one cubic segment.
        private static let arc = 0.5522847498

        private(set) var figures: [KefFigure] = []

        func box(_ paint: KefPaint, _ x: Double, _ y: Double, _ w: Double, _ h: Double, _ r: Double, edge: Bool = false) -> Drawing {
            box(paint, x, y, w, h, corners: (r, r, r, r), edge: edge)
        }

        func box(_ paint: KefPaint, _ x: Double, _ y: Double, _ w: Double, _ h: Double, corners r: Corners, edge: Bool = false) -> Drawing {
            var next = self
            next.figures.append(Self.roundedBox(paint, stroked: false, x, y, w, h, r))
            if edge {
                let i = KefShape.edgeWidth / 2
                func inner(_ radius: Double) -> Double { max(0, radius - KefShape.edgeWidth / 2) }
                next.figures.append(Self.roundedBox(.edge, stroked: true, x + i, y + i, w - 2 * i, h - 2 * i,
                                                    (inner(r.topLeft), inner(r.topRight), inner(r.bottomRight), inner(r.bottomLeft))))
            }
            return next
        }

        func ellipse(_ paint: KefPaint, _ cx: Double, _ cy: Double, _ rx: Double, _ ry: Double) -> Drawing {
            let kx = rx * Self.arc, ky = ry * Self.arc
            var next = self
            next.figures.append(KefFigure(paint: paint, stroked: false, start: KefPoint(cx + rx, cy), curves: [
                KefPoint(cx + rx, cy + ky), KefPoint(cx + kx, cy + ry), KefPoint(cx, cy + ry),
                KefPoint(cx - kx, cy + ry), KefPoint(cx - rx, cy + ky), KefPoint(cx - rx, cy),
                KefPoint(cx - rx, cy - ky), KefPoint(cx - kx, cy - ry), KefPoint(cx, cy - ry),
                KefPoint(cx + kx, cy - ry), KefPoint(cx + rx, cy - ky), KefPoint(cx + rx, cy),
            ]))
            return next
        }

        /// Trim ring, cone and tweeter of the Uni-Q driver.
        func uniQ(_ cx: Double, _ cy: Double, _ trim: Double, _ cone: Double, _ tweeter: Double) -> Drawing {
            ellipse(.trim, cx, cy, trim, trim)
                .ellipse(.cone, cx, cy, cone, cone)
                .ellipse(.tweeter, cx, cy, tweeter, tweeter)
        }

        /// A free outline. Its edge is the same outline scaled about its middle to sit half the edge width inside,
        /// which keeps the edge even on a shape this close to a rounded box.
        func outline(_ paint: KefPaint, _ start: KefPoint, _ curves: [KefPoint], edge: Bool = false) -> Drawing {
            var next = self
            next.figures.append(KefFigure(paint: paint, stroked: false, start: start, curves: curves))
            if edge {
                let ends = curves.enumerated().filter { $0.offset % 3 == 2 }.map(\.element) + [start]
                let left = ends.map(\.x).min()!, right = ends.map(\.x).max()!
                let top = ends.map(\.y).min()!, bottom = ends.map(\.y).max()!
                let cx = (left + right) / 2, cy = (top + bottom) / 2
                let sx = (right - left - KefShape.edgeWidth) / (right - left), sy = (bottom - top - KefShape.edgeWidth) / (bottom - top)
                func inset(_ p: KefPoint) -> KefPoint { KefPoint(cx + (p.x - cx) * sx, cy + (p.y - cy) * sy) }
                next.figures.append(KefFigure(paint: .edge, stroked: true, start: inset(start), curves: curves.map(inset)))
            }
            return next
        }

        private static func roundedBox(_ paint: KefPaint, stroked: Bool, _ x: Double, _ y: Double, _ w: Double, _ h: Double, _ r: Corners) -> KefFigure {
            let right = x + w, bottom = y + h
            var curves: [KefPoint] = []
            func line(_ from: KefPoint, _ to: KefPoint) { curves += [from, to, to] }
            func corner(_ from: KefPoint, _ corner: KefPoint, _ to: KefPoint) {
                curves += [
                    KefPoint(from.x + (corner.x - from.x) * arc, from.y + (corner.y - from.y) * arc),
                    KefPoint(to.x + (corner.x - to.x) * arc, to.y + (corner.y - to.y) * arc),
                    to,
                ]
            }
            let start = KefPoint(x + r.topLeft, y)
            line(start, KefPoint(right - r.topRight, y))
            corner(KefPoint(right - r.topRight, y), KefPoint(right, y), KefPoint(right, y + r.topRight))
            line(KefPoint(right, y + r.topRight), KefPoint(right, bottom - r.bottomRight))
            corner(KefPoint(right, bottom - r.bottomRight), KefPoint(right, bottom), KefPoint(right - r.bottomRight, bottom))
            line(KefPoint(right - r.bottomRight, bottom), KefPoint(x + r.bottomLeft, bottom))
            corner(KefPoint(x + r.bottomLeft, bottom), KefPoint(x, bottom), KefPoint(x, bottom - r.bottomLeft))
            line(KefPoint(x, bottom - r.bottomLeft), KefPoint(x, y + r.topLeft))
            corner(KefPoint(x, y + r.topLeft), KefPoint(x, y), start)
            return KefFigure(paint: paint, stroked: stroked, start: start, curves: curves)
        }
    }
}
