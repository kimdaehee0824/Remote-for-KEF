import AppKit
import KefW2Driver

/// The speaker drawn in a KEF finish's colors, for the panel header and the settings pickers.
/// Same figures as the Windows app (Ui.AppGlyph), from `KefShape` on its 48-point grid.
enum SpeakerArt {
    /// The shape of `model`, or of the finish's model when one is picked, in the finish's colors or, when none is
    /// picked, those of the app icon. With neither it is the app icon.
    @MainActor static func image(_ finish: KefFinish?, model: String? = nil, size: CGFloat) -> NSImage {
        let shape = finish?.model ?? model
        let colors = finish ?? .appDefault
        let key = "\(colors.id)|\(shape ?? "")@\(size)"
        if let cached = cache[key] { return cached }
        let image = MenuBarGlyph.render(size: CGSize(width: size, height: size), template: false) { context in
            // The grid runs top down like the Windows canvas.
            context.translateBy(x: 0, y: size)
            context.scaleBy(x: size / 48, y: -size / 48)
            context.setLineWidth(KefShape.edgeWidth)
            for figure in KefShape.figures(for: shape) {
                let path = CGMutablePath()
                path.move(to: point(figure.start))
                for i in stride(from: 0, to: figure.curves.count, by: 3) {
                    path.addCurve(to: point(figure.curves[i + 2]), control1: point(figure.curves[i]), control2: point(figure.curves[i + 1]))
                }
                path.closeSubpath()
                context.addPath(path)
                if figure.stroked {
                    context.setStrokeColor(color(colors.color(figure.paint)))
                    context.strokePath()
                } else {
                    context.setFillColor(color(colors.color(figure.paint)))
                    context.fillPath()
                }
            }
        }
        cache[key] = image
        return image
    }

    @MainActor private static var cache: [String: NSImage] = [:]

    private static func point(_ p: KefPoint) -> CGPoint {
        CGPoint(x: p.x, y: p.y)
    }

    private static func color(_ rgb: UInt32) -> CGColor {
        let channel = { (shift: UInt32) in CGFloat((rgb >> shift) & 0xFF) / 255 }
        return CGColor(srgbRed: channel(16), green: channel(8), blue: channel(0), alpha: 1)
    }
}
