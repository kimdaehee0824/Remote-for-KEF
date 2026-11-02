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
                context.addPath(path(figure))
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

    /// The shape of `model` in one color, as a template image the system tints like a symbol: the volume indicator
    /// shows it in place of the speaker symbol when there is more than one speaker. Parts of the same color are told
    /// apart by gaps, as in the menu bar glyph: around the cone, the tweeter, the fabric band and the XIO's top face.
    /// `slashed` cuts a slash through it for mute.
    @MainActor static func monochrome(model: String?, size: CGFloat, slashed: Bool = false) -> NSImage {
        let key = "mono|\(model ?? "")@\(size)\(slashed ? "/" : "")"
        if let cached = cache[key] { return cached }
        let figures = KefShape.figures(for: model)
        let image = MenuBarGlyph.render(size: CGSize(width: size, height: size), template: true) { context in
            context.translateBy(x: 0, y: size)
            context.scaleBy(x: size / 48, y: -size / 48)
            context.setFillColor(CGColor(gray: 0, alpha: 1))
            context.setStrokeColor(CGColor(gray: 0, alpha: 1))
            guard slashed else {
                drawMonochrome(figures, in: context)
                return
            }
            // On the 48-point grid, top down: the menu bar glyph's slash scaled up three times.
            let slash = CGMutablePath()
            slash.move(to: CGPoint(x: 4.8, y: 3.6))
            slash.addLine(to: CGPoint(x: 43.2, y: 45.6))
            let clip = CGMutablePath()
            clip.addRect(CGRect(x: -12, y: -12, width: 72, height: 72))
            clip.addPath(slash.copy(strokingWithWidth: 10.2, lineCap: .round, lineJoin: .round, miterLimit: 10))
            context.saveGState()
            context.addPath(clip)
            context.clip(using: .evenOdd)
            drawMonochrome(figures, in: context)
            context.restoreGState()
            context.setLineWidth(3.9)
            context.setLineCap(.round)
            context.addPath(slash)
            context.strokePath()
        }
        cache[key] = image
        return image
    }

    private static func drawMonochrome(_ figures: [KefFigure], in context: CGContext) {
        for (index, figure) in figures.enumerated() where !figure.stroked {
            let next = index + 1 < figures.count ? figures[index + 1].paint : nil
            // The trim ring of the Uni-Q driver (followed by its cone) is the gap around the cone.
            let gap = figure.paint == .tweeter || figure.paint == .seam || figure.paint == .top
                || (figure.paint == .trim && next == .cone)
            context.setBlendMode(gap ? .clear : .normal)
            context.addPath(path(figure))
            context.fillPath()
        }
        context.setBlendMode(.normal)
    }

    private static func path(_ figure: KefFigure) -> CGPath {
        let path = CGMutablePath()
        path.move(to: point(figure.start))
        for i in stride(from: 0, to: figure.curves.count, by: 3) {
            path.addCurve(to: point(figure.curves[i + 2]), control1: point(figure.curves[i]), control2: point(figure.curves[i + 1]))
        }
        path.closeSubpath()
        return path
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
